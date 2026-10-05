using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;
using Meilisearch;

namespace TruyenDex.Api;

public class TruyenGg(HttpClient http, IMemoryCache cache, IConnectionMultiplexer? redis = null, MeilisearchClient? meili = null)
{
    private const string BaseUrl = "https://truyenggvn.com";
    private static readonly ConcurrentDictionary<Guid, string> MangaSlugMap = new();
    private static readonly ConcurrentDictionary<Guid, string> ChapterUrlMap = new();
    private static readonly ConcurrentDictionary<Guid, Guid> ChapterMangaMap = new();

    public static Guid CreateGuid(string key)
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(key));
        return new Guid(hash);
    }

    public static string RemoveDiacritics(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var normalizedString = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in normalizedString)
        {
            var uc = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
            if (uc != System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                if (c is 'đ' or 'Đ') sb.Append('d');
                else sb.Append(c);
            }
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    public static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var unaccented = RemoveDiacritics(title).Replace('×', 'x').Replace('✕', 'x').Replace('✖', 'x').ToLowerInvariant();
        var sb = new StringBuilder();
        foreach (var c in unaccented)
        {
            if (char.IsAsciiLetterOrDigit(c))
                sb.Append(c);
        }
        return sb.ToString();
    }

    public static HashSet<string> GetMatchKeys(string? title, string? altTitles = null)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mainNorm = NormalizeTitle(title);
        if (!string.IsNullOrEmpty(mainNorm) && mainNorm.Length >= 3) keys.Add(mainNorm);

        var altNorm = NormalizeTitle(altTitles);
        if (!string.IsNullOrEmpty(altNorm) && altNorm.Length >= 3) keys.Add(altNorm);

        if (!string.IsNullOrWhiteSpace(altTitles))
        {
            var parts = altTitles.Split([';', '/', '|', '\n'], StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var pNorm = NormalizeTitle(part);
                if (!string.IsNullOrEmpty(pNorm) && pNorm.Length >= 3) keys.Add(pNorm);
            }
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            var parts = title.Split([';', '/', '|', '\n'], StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var pNorm = NormalizeTitle(part);
                if (!string.IsNullOrEmpty(pNorm) && pNorm.Length >= 3) keys.Add(pNorm);
            }
        }

        return keys;
    }

    public static bool IsSameManga(string? titleA, string? altA, string? titleB, string? altB)
    {
        var keysA = GetMatchKeys(titleA, altA);
        var keysB = GetMatchKeys(titleB, altB);

        foreach (var ka in keysA)
        {
            if (keysB.Contains(ka)) return true;
        }

        var normA = NormalizeTitle(titleA);
        var normB = NormalizeTitle(titleB);
        if (normA.Length >= 10 && normB.Length >= 10)
        {
            if (normA.Contains(normB) || normB.Contains(normA))
            {
                double ratio = (double)Math.Min(normA.Length, normB.Length) / Math.Max(normA.Length, normB.Length);
                if (ratio >= 0.80) return true;
            }
        }

        return false;
    }

    public static string ToSlug(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var unaccented = RemoveDiacritics(title).Replace('×', 'x').Replace('✕', 'x').Replace('✖', 'x').ToLowerInvariant();
        var s = Regex.Replace(unaccented, @"[^\w\s-]", "");
        s = Regex.Replace(s, @"\s+", "-").Trim('-');
        return s;
    }

    public void RegisterManga(Guid id, string slug)
    {
        MangaSlugMap[id] = slug;
        _ = CacheSetString($"truyengg:slug:{id}", slug, TimeSpan.FromDays(30));
    }

    public void RegisterChapter(Guid chapId, string chapHref, Guid mangaId)
    {
        ChapterUrlMap[chapId] = chapHref;
        ChapterMangaMap[chapId] = mangaId;
        _ = CacheSetString($"truyengg:chap:{chapId}", chapHref, TimeSpan.FromDays(30));
        _ = CacheSetString($"truyengg:chap_manga:{chapId}", mangaId.ToString(), TimeSpan.FromDays(30));
    }

    public async Task<string?> ResolveSlug(Guid id)
    {
        if (MangaSlugMap.TryGetValue(id, out var slug) && !string.IsNullOrEmpty(slug)) return slug;
        slug = await CacheGetString($"truyengg:slug:{id}");
        if (!string.IsNullOrEmpty(slug))
        {
            MangaSlugMap[id] = slug;
            return slug;
        }

        if (meili != null)
        {
            try
            {
                var index = meili.Index("mangas");
                var doc = await index.GetDocumentAsync<MangaCard>(id.ToString());
                if (doc != null)
                {
                    if (!string.IsNullOrEmpty(doc.Slug))
                    {
                        RegisterManga(id, doc.Slug);
                        return doc.Slug;
                    }
                    if (!string.IsNullOrEmpty(doc.Title))
                    {
                        var searchRes = await Search(doc.Title, 1);
                        var match = searchRes.FirstOrDefault(x => x.Id == id);
                        if (match != null && !string.IsNullOrEmpty(match.Slug))
                        {
                            RegisterManga(id, match.Slug);
                            return match.Slug;
                        }
                    }
                }
            }
            catch { }
        }

        return null;
    }

    public async Task<Guid> ResolveChapterMangaId(Guid chapterId)
    {
        if (ChapterMangaMap.TryGetValue(chapterId, out var mangaId) && mangaId != Guid.Empty) return mangaId;
        var mangaIdStr = await CacheGetString($"truyengg:chap_manga:{chapterId}");
        if (!string.IsNullOrEmpty(mangaIdStr) && Guid.TryParse(mangaIdStr, out var mId))
        {
            ChapterMangaMap[chapterId] = mId;
            return mId;
        }
        return Guid.Empty;
    }

    private async Task<string?> ResolveChapterUrl(Guid chapterId)
    {
        if (ChapterUrlMap.TryGetValue(chapterId, out var url) && !string.IsNullOrEmpty(url)) return url;
        url = await CacheGetString($"truyengg:chap:{chapterId}");
        if (!string.IsNullOrEmpty(url))
        {
            ChapterUrlMap[chapterId] = url;
            return url;
        }

        var mId = await ResolveChapterMangaId(chapterId);
        if (mId != Guid.Empty)
        {
            await GetChapters(mId, 1, 500);
            if (ChapterUrlMap.TryGetValue(chapterId, out url)) return url;
        }

        return null;
    }

    private async Task<string?> CacheGetString(string key)
    {
        if (redis != null && redis.IsConnected)
        {
            try {
                var v = await redis.GetDatabase().StringGetAsync(key);
                if (v.HasValue) return v.ToString();
            } catch { }
        }
        return cache.TryGetValue<string>(key, out var cached) ? cached : null;
    }

    private async Task CacheSetString(string key, string value, TimeSpan expiry)
    {
        if (redis != null && redis.IsConnected)
        {
            try {
                await redis.GetDatabase().StringSetAsync(key, value, expiry);
            } catch { }
        }
        cache.Set(key, value, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = expiry, Size = 1 });
    }

    private async Task<string?> FetchHtml(string url)
    {
        var cacheKey = $"truyengg:html:{url}";
        var cached = await CacheGetString(cacheKey);
        if (cached != null) return cached;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
            req.Headers.Referrer = new Uri(BaseUrl);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var res = await http.SendAsync(req, cts.Token);
            if (!res.IsSuccessStatusCode) return null;
            var html = await res.Content.ReadAsStringAsync(cts.Token);
            if (!string.IsNullOrEmpty(html))
            {
                await CacheSetString(cacheKey, html, TimeSpan.FromMinutes(10));
            }
            return html;
        }
        catch
        {
            return null;
        }
    }

    private static DateTime ParseTimeAgo(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return DateTime.UtcNow;
        s = s.Trim().ToLowerInvariant();
        if (s.Contains("vừa xong") || s.Contains("vài giây")) return DateTime.UtcNow;

        var m = Regex.Match(s, @"(\d+)\s*(phút|giờ|ngày|tháng|năm|giây)");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var num))
        {
            var unit = m.Groups[2].Value;
            return unit switch
            {
                "giây" => DateTime.UtcNow.AddSeconds(-num),
                "phút" => DateTime.UtcNow.AddMinutes(-num),
                "giờ" => DateTime.UtcNow.AddHours(-num),
                "ngày" => DateTime.UtcNow.AddDays(-num),
                "tháng" => DateTime.UtcNow.AddDays(-num * 30),
                "năm" => DateTime.UtcNow.AddDays(-num * 365),
                _ => DateTime.UtcNow
            };
        }
        if (DateTime.TryParseExact(s, ["dd/MM/yyyy", "dd/MM/yyyy HH:mm", "yyyy-MM-dd"], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var dt))
        {
            return dt.ToUniversalTime();
        }
        if (DateTime.TryParse(s, out var d)) return d.ToUniversalTime();
        return DateTime.UtcNow;
    }

    private static string StripHtml(string input)
    {
        if (string.IsNullOrEmpty(input)) return "";
        var decoded = System.Net.WebUtility.HtmlDecode(input);
        var cleaned = Regex.Replace(decoded, "<.*?>", string.Empty);
        cleaned = Regex.Replace(cleaned, @"Truyện tranh\s+.*?được cập nhật nhanh và đầy đủ nhất tại\s+\S+", string.Empty, RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"Bạn đọc đừng quên để lại bình luận và chia sẻ,\s*ủng hộ.*$", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Multiline);
        cleaned = Regex.Replace(cleaned, @"Đọc truyện\s+.*?tại\s+\S+", string.Empty, RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"Xem truyện\s+.*?tại\s+\S+", string.Empty, RegexOptions.IgnoreCase);
        return Regex.Replace(cleaned, @"\s+", " ").Trim();
    }

    private List<MangaCard> ParseGridHtml(string html, string defaultCountry = "ja")
    {
        var items = new List<MangaCard>();
        var liMatches = Regex.Matches(html, @"<li>\s*<div class=""book_avatar"">([\s\S]*?)<\/li>");
        foreach (Match m in liMatches)
        {
            var block = m.Value;
            var linkMatch = Regex.Match(block, @"href=""(/truyen-tranh/[^""]+)""");
            if (!linkMatch.Success) continue;
            var href = linkMatch.Groups[1].Value;
            var slug = href.Replace("/truyen-tranh/", "").Trim('/');
            if (string.IsNullOrEmpty(slug)) continue;

            var titleMatch = Regex.Match(block, @"class=""book_name[^""]*""[^>]*>\s*<h3><a[^>]*>(.*?)<\/a>");
            var title = titleMatch.Success ? StripHtml(titleMatch.Groups[1].Value) : "";
            if (string.IsNullOrEmpty(title))
            {
                var altTitleMatch = Regex.Match(block, @"alt=""([^""]+)""");
                if (altTitleMatch.Success) title = altTitleMatch.Groups[1].Value;
            }
            if (string.IsNullOrEmpty(title)) continue;

            var otherTitleMatch = Regex.Match(block, @"class=""title-more-other"">([^<]+)<\/div>");
            var otherTitle = otherTitleMatch.Success ? otherTitleMatch.Groups[1].Value.Replace("Tên khác:", "").Trim() : "";

            var imgMatch = Regex.Match(block, @"<img[^>]*src=""([^""]+)""");
            var coverUrl = imgMatch.Success ? imgMatch.Groups[1].Value : "";
            if (coverUrl.StartsWith("//")) coverUrl = "https:" + coverUrl;
            else if (coverUrl.StartsWith("/")) coverUrl = BaseUrl + coverUrl;

            var timeMatch = Regex.Match(block, @"class=""time-ago"">([^<]+)<\/span>");
            var updatedAt = timeMatch.Success ? ParseTimeAgo(timeMatch.Groups[1].Value) : DateTime.UtcNow;

            var followMatch = Regex.Match(block, @"<i class=""fa fa-bookmark""><\/i>\s*([\d,]+)");
            int.TryParse(followMatch.Groups[1].Value.Replace(",", ""), out var follows);

            var tagMatches = Regex.Matches(block, @"<p class=""blue"">([^<]+)<\/p>");
            var genresList = tagMatches.Select(t => t.Groups[1].Value.Trim()).Where(g => !string.IsNullOrEmpty(g)).ToList();
            if (defaultCountry == "ko" && !genresList.Any(g => g.Equals("Manhwa", StringComparison.OrdinalIgnoreCase)))
            {
                genresList.Add("Manhwa");
            }
            else if (defaultCountry == "zh" && !genresList.Any(g => g.Equals("Manhua", StringComparison.OrdinalIgnoreCase)))
            {
                genresList.Add("Manhua");
            }
            var genres = genresList.ToArray();

            var countryCode = genres.Contains("Manhwa", StringComparer.OrdinalIgnoreCase) ? "ko" :
                              genres.Contains("Manhua", StringComparer.OrdinalIgnoreCase) ? "zh" :
                              defaultCountry;

            var lastChapMatch = Regex.Match(block, @"class=""last_chapter"">\s*<a href=""([^""]+)""[^>]*>([^<]+)<\/a>");
            var mangaId = CreateGuid("truyengg:manga:" + slug);
            RegisterManga(mangaId, slug);

            var chapters = new List<ChapterCard>();
            if (lastChapMatch.Success)
            {
                var chapHref = lastChapMatch.Groups[1].Value;
                var chapTitle = StripHtml(lastChapMatch.Groups[2].Value);
                var numMatch = Regex.Match(chapTitle, @"(?:\b|[^\w\d])(?:chương|chapter|chap|ch|c)?[\s\._-]*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
                decimal num = 0;
                if (numMatch.Success)
                {
                    decimal.TryParse(numMatch.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out num);
                }

                var chapId = CreateGuid("truyengg:chap:" + chapHref);
                RegisterChapter(chapId, chapHref, mangaId);
                chapters.Add(new ChapterCard(chapId, mangaId, chapTitle, num, "vi", updatedAt, "TruyenGG"));
            }

            var maxChap = chapters.Count > 0 ? (int)Math.Round(chapters.Max(c => c.Number)) : 0;
            var card = new MangaCard
            {
                Id = mangaId,
                Slug = slug,
                Title = title,
                AlternativeTitle = string.IsNullOrEmpty(otherTitle) ? title : otherTitle,
                Author = "Đang cập nhật",
                Cover = !string.IsNullOrEmpty(coverUrl) ? "/api/catalog/image-proxy?url=" + Uri.EscapeDataString(coverUrl) : "/cover-placeholder.svg",
                Description = "",
                Genres = genres,
                Status = block.Contains("Hoàn thành") ? "completed" : "ongoing",
                Country = countryCode,
                ContentRating = "safe",
                Follows = follows,
                Rating = 8.5,
                UpdatedAt = updatedAt,
                Chapters = chapters,
                TotalChapters = maxChap
            };

            items.Add(card);
        }

        return items;
    }

    public async Task<List<MangaCard>> GetLatest(int page = 1, int country = 4)
    {
        var p = Math.Max(1, page);
        var url = $"{BaseUrl}/truyen-moi-cap-nhat/trang-{p}.html?country={country}";
        var html = await FetchHtml(url);
        if (string.IsNullOrEmpty(html)) return [];
        var defaultCountry = country switch { 3 => "ko", 1 => "zh", 2 => "vi", _ => "ja" };
        return ParseGridHtml(html, defaultCountry);
    }

    public async Task<List<MangaCard>> GetLatestManhwaManhua(int page = 1)
    {
        var cacheKey = $"truyengg:manhwa_manhua:{page}";
        var cached = await CacheGetString(cacheKey);
        if (cached != null)
        {
            try { return JsonSerializer.Deserialize<List<MangaCard>>(cached) ?? []; } catch { }
        }

        var manhwaTask = GetLatest(page, 3); // 3 = Hàn Quốc (Manhwa)
        var manhuaTask = GetLatest(page, 1); // 1 = Trung Quốc (Manhua)
        await Task.WhenAll(manhwaTask, manhuaTask);

        var manhwa = await manhwaTask;
        var manhua = await manhuaTask;

        var merged = new List<MangaCard>();
        int max = Math.Max(manhwa.Count, manhua.Count);
        for (int i = 0; i < max; i++)
        {
            if (i < manhwa.Count) merged.Add(manhwa[i]);
            if (i < manhua.Count) merged.Add(manhua[i]);
        }

        var result = merged.OrderByDescending(x => x.UpdatedAt).ToList();
        if (result.Count > 0)
        {
            await CacheSetString(cacheKey, JsonSerializer.Serialize(result), TimeSpan.FromMinutes(5));
        }
        return result;
    }

    public async Task<List<MangaCard>> Search(string q, int page = 1)
    {
        if (string.IsNullOrWhiteSpace(q)) return await GetLatest(page);
        var cacheKey = $"truyengg:search:v3:{q.Trim().ToLowerInvariant()}:{page}";
        var cached = await CacheGetString(cacheKey);
        if (cached != null)
        {
            try {
                var list = JsonSerializer.Deserialize<List<MangaCard>>(cached) ?? [];
                foreach (var item in list)
                {
                    if (!string.IsNullOrEmpty(item.Slug))
                    {
                        RegisterManga(item.Id, item.Slug);
                    }
                }
                return list;
            } catch { }
        }

        var items = new List<MangaCard>();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/frontend/search/search");
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
            req.Headers.Referrer = new Uri(BaseUrl);
            req.Headers.Add("X-Requested-With", "XMLHttpRequest");
            req.Content = new FormUrlEncodedContent(new Dictionary<string, string> { { "search", q.Trim() } });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var res = await http.SendAsync(req, cts.Token);
            if (res.IsSuccessStatusCode)
            {
                var html = await res.Content.ReadAsStringAsync(cts.Token);
                if (!string.IsNullOrWhiteSpace(html))
                {
                    var liMatches = Regex.Matches(html, @"<li>([\s\S]*?)<\/li>");
                    foreach (Match liMatch in liMatches)
                    {
                        var li = liMatch.Groups[1].Value;
                        var hrefMatch = Regex.Match(li, @"href=""([^""]+)""");
                        var nameMatch = Regex.Match(li, @"class=""name""[^>]*>([^<]+)<\/p>");
                        if (!hrefMatch.Success || !nameMatch.Success) continue;

                        var href = hrefMatch.Groups[1].Value.Trim();
                        var name = StripHtml(nameMatch.Groups[1].Value);

                        var imgMatch = Regex.Match(li, @"<img[^>]+src=""([^""]+)""");
                        var coverUrl = imgMatch.Success ? imgMatch.Groups[1].Value.Trim() : "";

                        var altMatch = Regex.Match(li, @"class=""name_other""[^>]*>([\s\S]*?)<\/p>");
                        var otherName = altMatch.Success ? StripHtml(altMatch.Groups[1].Value) : "";

                        var slug = href.Replace("https://truyenggvn.com", "").Replace("/truyen-tranh/", "").Trim('/');
                        if (string.IsNullOrEmpty(slug)) continue;

                        if (coverUrl.StartsWith("//")) coverUrl = "https:" + coverUrl;
                        else if (coverUrl.StartsWith("/")) coverUrl = BaseUrl + coverUrl;

                        var mangaId = CreateGuid("truyengg:manga:" + slug);
                        RegisterManga(mangaId, slug);

                        var card = new MangaCard
                        {
                            Id = mangaId,
                            Slug = slug,
                            Title = name,
                            AlternativeTitle = string.IsNullOrEmpty(otherName) ? name : otherName,
                            Author = "Đang cập nhật",
                            Cover = !string.IsNullOrEmpty(coverUrl) ? "/api/catalog/image-proxy?url=" + Uri.EscapeDataString(coverUrl) : "/cover-placeholder.svg",
                            Country = "ja",
                            ContentRating = "safe",
                            Rating = 8.5,
                            UpdatedAt = DateTime.UtcNow
                        };

                        var chapMatch = Regex.Match(li, @"<p>\s*(Chương\s*[^<]+)<\/p>", RegexOptions.IgnoreCase);
                        if (chapMatch.Success)
                        {
                            var chapTitle = chapMatch.Groups[1].Value.Trim();
                            var numStr = Regex.Match(chapTitle, @"[\d.]+").Value;
                            decimal.TryParse(numStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var chapNum);
                            card.Chapters.Add(new ChapterCard(
                                Id: Guid.NewGuid(),
                                MangaId: mangaId,
                                Title: chapTitle,
                                Number: chapNum,
                                Language: "vi",
                                PublishedAt: DateTime.UtcNow,
                                Group: "TruyenGG"
                            ));
                            card.TotalChapters = (int)Math.Round(chapNum);
                        }

                        items.Add(card);
                    }
                }
            }

            if (items.Count > 0)
            {
                await CacheSetString(cacheKey, JsonSerializer.Serialize(items), TimeSpan.FromMinutes(10));
            }
        }
        catch { }

        return items;
    }

    public async Task<MangaCard?> GetDetail(Guid id)
    {
        var slug = await ResolveSlug(id);
        if (string.IsNullOrEmpty(slug)) return null;
        var url = $"{BaseUrl}/truyen-tranh/{slug}";
        var html = await FetchHtml(url);
        if (string.IsNullOrEmpty(html)) return null;

        var titleMatch = Regex.Match(html, @"<h1 itemprop=""name"">([^<]+)<\/h1>");
        var title = titleMatch.Success ? titleMatch.Groups[1].Value.Trim() : slug;

        var altTitleMatch = Regex.Match(html, @"<h2 class=""book-othername"">([^<]+)<\/h2>");
        var altTitle = altTitleMatch.Success ? altTitleMatch.Groups[1].Value.Trim() : title;

        var imgMatch = Regex.Match(html, @"class=""book_avatar""[^>]*>\s*<img[^>]*src=""([^""]+)""");
        var coverUrl = imgMatch.Success ? imgMatch.Groups[1].Value : "";
        if (coverUrl.StartsWith("//")) coverUrl = "https:" + coverUrl;
        else if (coverUrl.StartsWith("/")) coverUrl = BaseUrl + coverUrl;

        var authorMatch = Regex.Match(html, @"Tác giả<\/span>\s*<span class=""book-counts__value"">\s*<a[^>]*>([^<]+)<\/a>");
        var author = authorMatch.Success ? authorMatch.Groups[1].Value.Trim() : "Đang cập nhật";

        var statusMatch = Regex.Match(html, @"Tình trạng<\/span>\s*<span class=""book-counts__value"">([^<]+)<\/span>");
        var status = statusMatch.Success && statusMatch.Groups[1].Value.Contains("Hoàn thành") ? "completed" : "ongoing";

        var followMatch = Regex.Match(html, @"Theo dõi<\/span>\s*<span class=""book-counts__value"">([\d,]+)<\/span>");
        int.TryParse(followMatch.Groups[1].Value.Replace(",", ""), out var follows);

        var genreMatches = Regex.Matches(html, @"class=""book-genres"">[\s\S]*?<ul class=""list01"">([\s\S]*?)<\/ul>");
        var genres = new List<string>();
        if (genreMatches.Count > 0)
        {
            var gNames = Regex.Matches(genreMatches[0].Value, @"<a[^>]*>([^<]+)<\/a>");
            foreach (Match gm in gNames) genres.Add(gm.Groups[1].Value.Trim());
        }

        var descMatch = Regex.Match(html, @"class=""story-detail-info[^""]*"">([\s\S]*?)<\/div>");
        var desc = descMatch.Success ? StripHtml(descMatch.Groups[1].Value) : "";

        return new MangaCard
        {
            Id = id,
            Slug = slug,
            Title = title,
            AlternativeTitle = altTitle,
            Author = author,
            Cover = !string.IsNullOrEmpty(coverUrl) ? "/api/catalog/image-proxy?url=" + Uri.EscapeDataString(coverUrl) : "/cover-placeholder.svg",
            Description = desc,
            Genres = genres.ToArray(),
            Status = status,
            Country = genres.Contains("Manhwa") ? "ko" : genres.Contains("Manhua") ? "zh" : "ja",
            ContentRating = "safe",
            Follows = follows,
            Rating = 8.5,
            UpdatedAt = DateTime.UtcNow
        };
    }

    public async Task<ChapterPage?> GetChapters(Guid id, int page = 1, int size = 100, bool ascending = false)
    {
        var slug = await ResolveSlug(id);
        if (string.IsNullOrEmpty(slug)) return null;
        var url = $"{BaseUrl}/truyen-tranh/{slug}";
        var html = await FetchHtml(url);
        if (string.IsNullOrEmpty(html)) return null;

        var chapMatches = Regex.Matches(html, @"<div class=""works-chapter-item"">\s*<div class=""col-md-10[^""]*name-chap"">\s*<a[^>]*href=""([^""]+)""[^>]*>([^<]+)<\/a>\s*<\/div>\s*<div class=""col-md-2[^""]*time-chap"">\s*([^<]+)\s*<\/div>");
        var allChapters = new List<ChapterCard>();
        foreach (Match cm in chapMatches)
        {
            var chapHref = cm.Groups[1].Value;
            var chapTitle = StripHtml(cm.Groups[2].Value);
            var timeStr = cm.Groups[3].Value.Trim();
            var publishedAt = ParseTimeAgo(timeStr);

            var numMatch = Regex.Match(chapTitle, @"(?:\b|[^\w\d])(?:chương|chapter|chap|ch|c)?[\s\._-]*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
            decimal num = 0;
            if (numMatch.Success)
            {
                decimal.TryParse(numMatch.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out num);
            }

            var chapId = CreateGuid("truyengg:chap:" + chapHref);
            RegisterChapter(chapId, chapHref, id);

            allChapters.Add(new ChapterCard(chapId, id, chapTitle, num, "vi", publishedAt, "TruyenGG"));
        }

        var deduplicated = Catalog.DeduplicateChapters(allChapters, ascending);
        var total = deduplicated.Count;
        var paged = deduplicated.Skip((page - 1) * size).Take(size).ToList();
        return new ChapterPage(paged, total, page, size);
    }

    public async Task<List<ChapterCard>?> FindMatchingChapters(Guid mangaId, string title, string? altTitles = null)
    {
        try
        {
            var searchResults = await Search(title);
            if (searchResults.Count == 0 && !string.IsNullOrWhiteSpace(altTitles))
            {
                var firstAlt = altTitles.Split([';', '/', '|', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
                if (!string.IsNullOrWhiteSpace(firstAlt))
                {
                    searchResults = await Search(firstAlt);
                }
            }

            var matched = searchResults.FirstOrDefault(gg => IsSameManga(title, altTitles, gg.Title, gg.AlternativeTitle));
            if (matched != null)
            {
                var slug = await ResolveSlug(matched.Id);
                if (!string.IsNullOrEmpty(slug))
                {
                    RegisterManga(mangaId, slug);
                    var url = $"{BaseUrl}/truyen-tranh/{slug}";
                    var html = await FetchHtml(url);
                    if (!string.IsNullOrEmpty(html))
                    {
                        var chapMatches = Regex.Matches(html, @"<div class=""works-chapter-item"">\s*<div class=""col-md-10[^""]*name-chap"">\s*<a[^>]*href=""([^""]+)""[^>]*>([^<]+)<\/a>\s*<\/div>\s*<div class=""col-md-2[^""]*time-chap"">\s*([^<]+)\s*<\/div>");
                        var chaps = new List<ChapterCard>();
                        foreach (Match cm in chapMatches)
                        {
                            var chapHref = cm.Groups[1].Value;
                            var chapTitle = StripHtml(cm.Groups[2].Value);
                            var timeStr = cm.Groups[3].Value.Trim();
                            var publishedAt = ParseTimeAgo(timeStr);

                            var numMatch = Regex.Match(chapTitle, @"(?:\b|[^\w\d])(?:chương|chapter|chap|ch|c)?[\s\._-]*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
                            decimal num = 0;
                            if (numMatch.Success)
                            {
                                decimal.TryParse(numMatch.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out num);
                            }

                            var chapId = CreateGuid("truyengg:chap:" + chapHref);
                            RegisterChapter(chapId, chapHref, mangaId);

                            chaps.Add(new ChapterCard(chapId, mangaId, chapTitle, num, "vi", publishedAt, "TruyenGG"));
                        }
                        return Catalog.DeduplicateChapters(chaps, ascending: false);
                    }
                }
            }
        }
        catch { }

        return null;
    }

    public async Task<ReaderData?> GetReader(Guid chapterId)
    {
        var chapHref = await ResolveChapterUrl(chapterId);
        if (string.IsNullOrEmpty(chapHref)) return null;
        var mangaId = await ResolveChapterMangaId(chapterId);
        var url = chapHref.StartsWith("http") ? chapHref : BaseUrl + (chapHref.StartsWith("/") ? "" : "/") + chapHref;

        var html = await FetchHtml(url);
        if (string.IsNullOrEmpty(html)) return null;

        var imgMatches = Regex.Matches(html, @"class=""page-chapter""[^>]*>\s*<img[^>]*>");
        var pages = new List<string>();
        foreach (Match im in imgMatches)
        {
            var tag = im.Value;
            var srcMatch = Regex.Match(tag, @"data-original=""([^""]+)""");
            if (!srcMatch.Success) srcMatch = Regex.Match(tag, @"data-cdn=""([^""]+)""");
            if (!srcMatch.Success) srcMatch = Regex.Match(tag, @"src=""([^""]+)""");
            if (srcMatch.Success)
            {
                var src = srcMatch.Groups[1].Value;
                if (src.StartsWith("//")) src = "https:" + src;
                else if (src.StartsWith("/")) src = BaseUrl + src;
                pages.Add(src);
            }
        }

        var titleMatch = Regex.Match(html, @"<h1 class=""detail-title[^""]*"">\s*<a[^>]*href=""([^""]+)""[^>]*>([^<]+)<\/a>\s*-\s*([^<]+)<\/h1>");
        var mangaTitle = titleMatch.Success ? StripHtml(titleMatch.Groups[2].Value) : "Truyện Tranh";
        var chapterTitle = titleMatch.Success ? StripHtml(titleMatch.Groups[3].Value) : "Chương";

        var numMatch = Regex.Match(chapterTitle, @"(?:\b|[^\w\d])(?:chương|chapter|chap|ch|c)?[\s\._-]*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
        decimal num = 0;
        if (numMatch.Success)
        {
            decimal.TryParse(numMatch.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out num);
        }

        var mangaCard = new MangaCard
        {
            Id = mangaId,
            Title = mangaTitle,
            AlternativeTitle = mangaTitle,
            Cover = "/cover-placeholder.svg",
            Author = "TruyenGG"
        };

        var currentChap = new ChapterCard(chapterId, mangaId, chapterTitle, num, "vi", DateTime.UtcNow, "TruyenGG");

        // Navigation chapters
        var navigation = new List<ChapterCard>();
        if (mangaId != Guid.Empty)
        {
            var allChaps = await GetChapters(mangaId, 1, 500, true);
            if (allChaps != null && allChaps.Items.Count > 0)
            {
                navigation = allChaps.Items;
            }
        }
        if (!navigation.Any(x => x.Id == chapterId))
        {
            navigation.Add(currentChap);
        }
        navigation = Catalog.DeduplicateChapters(navigation, ascending: true);

        string ProxyUrl(string u) => "/api/catalog/image-proxy?url=" + Uri.EscapeDataString(u);
        var proxyPages = pages.Select(ProxyUrl).ToArray();
        return new ReaderData(currentChap, mangaCard, proxyPages, proxyPages, null, navigation);
    }

    public static bool IsTruyenGgManga(Guid id) => MangaSlugMap.ContainsKey(id);
    public static bool IsTruyenGgChapter(Guid id) => ChapterUrlMap.ContainsKey(id);
}
