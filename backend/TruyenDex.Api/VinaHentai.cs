using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;
using Meilisearch;

namespace TruyenDex.Api;

public record VinaGenre(string Id, string Name, string? Description = null);

public class VinaHentai(HttpClient http, IMemoryCache cache, IConnectionMultiplexer? redis = null, MeilisearchClient? meili = null)
{
    private const string BaseUrl = "https://vinahentai.pics";
    private static readonly ConcurrentDictionary<Guid, string> MangaSlugMap = new();
    private static readonly ConcurrentDictionary<Guid, string> ChapterUrlMap = new();
    private static readonly ConcurrentDictionary<Guid, Guid> ChapterMangaMap = new();

    public static Guid CreateGuid(string key)
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(key));
        return new Guid(hash);
    }

    public void RegisterManga(Guid id, string slug)
    {
        MangaSlugMap[id] = slug;
        _ = CacheSetString($"vinahentai:slug:{id}", slug, TimeSpan.FromDays(30));
    }

    public void RegisterChapter(Guid chapId, string chapHref, Guid mangaId)
    {
        ChapterUrlMap[chapId] = chapHref;
        ChapterMangaMap[chapId] = mangaId;
        _ = CacheSetString($"vinahentai:chap:{chapId}", chapHref, TimeSpan.FromDays(30));
        _ = CacheSetString($"vinahentai:chap_manga:{chapId}", mangaId.ToString(), TimeSpan.FromDays(30));
    }

    public async Task<string?> ResolveSlug(Guid id)
    {
        if (MangaSlugMap.TryGetValue(id, out var slug) && !string.IsNullOrEmpty(slug)) return slug;
        slug = await CacheGetString($"vinahentai:slug:{id}");
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
                if (doc != null && !string.IsNullOrEmpty(doc.Slug))
                {
                    RegisterManga(id, doc.Slug);
                    return doc.Slug;
                }
            }
            catch { }
        }

        return null;
    }

    public async Task<Guid> ResolveChapterMangaId(Guid chapterId)
    {
        if (ChapterMangaMap.TryGetValue(chapterId, out var mangaId) && mangaId != Guid.Empty) return mangaId;
        var mangaIdStr = await CacheGetString($"vinahentai:chap_manga:{chapterId}");
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
        url = await CacheGetString($"vinahentai:chap:{chapterId}");
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
        var cacheKey = $"vinahentai:html:{url}";
        var cached = await CacheGetString(cacheKey);
        if (cached != null) return cached;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
            req.Headers.Referrer = new Uri(BaseUrl);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
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
        s = s.Trim();
        
        // Handle "10/5/2026, 8:03:38 PM" or "2026-10-05T13:03:38.327Z"
        if (DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var dt))
        {
            return dt.ToUniversalTime();
        }

        var lower = s.ToLowerInvariant();
        if (lower.Contains("vừa xong") || lower.Contains("vài giây")) return DateTime.UtcNow;

        var m = Regex.Match(lower, @"(\d+)\s*(p|phút|h|giờ|d|ngày|tháng|năm|s|giây)\s*(?:trc|trước)?");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var num))
        {
            var unit = m.Groups[2].Value;
            return unit switch
            {
                "s" or "giây" => DateTime.UtcNow.AddSeconds(-num),
                "p" or "phút" => DateTime.UtcNow.AddMinutes(-num),
                "h" or "giờ" => DateTime.UtcNow.AddHours(-num),
                "d" or "ngày" => DateTime.UtcNow.AddDays(-num),
                "tháng" => DateTime.UtcNow.AddDays(-num * 30),
                "năm" => DateTime.UtcNow.AddDays(-num * 365),
                _ => DateTime.UtcNow
            };
        }

        return DateTime.UtcNow;
    }

    private static string StripHtml(string input)
    {
        if (string.IsNullOrEmpty(input)) return "";
        var decoded = System.Net.WebUtility.HtmlDecode(input);
        var cleaned = Regex.Replace(decoded, "<.*?>", string.Empty);
        return Regex.Replace(cleaned, @"\s+", " ").Trim();
    }

    public async Task<List<MangaCard>> GetLatest(int page = 1)
    {
        var url = $"{BaseUrl}/danh-sach?page={page}";
        var html = await FetchHtml(url);
        if (string.IsNullOrEmpty(html)) return [];

        var items = new List<MangaCard>();
        var cardRegex = new Regex(@"<a\s+[^>]*href=""/truyen-hentai/([^""/]+)""[^>]*>([\s\S]*?)</a>", RegexOptions.IgnoreCase);
        var matches = cardRegex.Matches(html);

        foreach (Match m in matches)
        {
            var slug = m.Groups[1].Value.Trim();
            if (string.IsNullOrEmpty(slug)) continue;

            var cardContent = m.Groups[2].Value;

            // Cover
            var imgMatch = Regex.Match(cardContent, @"<img[^>]+src=""([^"">]+)""[^>]*alt=""([^"">]*)""", RegexOptions.IgnoreCase);
            if (!imgMatch.Success)
            {
                imgMatch = Regex.Match(cardContent, @"<img[^>]+alt=""([^"">]*)""[^>]*src=""([^"">]+)""", RegexOptions.IgnoreCase);
            }
            var coverUrl = imgMatch.Success ? (imgMatch.Groups[1].Value.StartsWith("http") ? imgMatch.Groups[1].Value : imgMatch.Groups[2].Value) : "";
            var imgAlt = imgMatch.Success ? (imgMatch.Groups[2].Value.StartsWith("http") ? imgMatch.Groups[1].Value : imgMatch.Groups[2].Value) : "";

            // Title
            var titleMatch = Regex.Match(cardContent, @"title=""([^""]+)""\s*>\s*([^<]+)\s*</div>\s*</div>\s*</div>", RegexOptions.IgnoreCase);
            if (!titleMatch.Success)
            {
                titleMatch = Regex.Match(cardContent, @"<div class=""mt-1\.5[^""]*"" title=""([^""]+)""", RegexOptions.IgnoreCase);
            }
            var title = titleMatch.Success ? StripHtml(titleMatch.Groups[1].Value) : StripHtml(imgAlt);
            if (string.IsNullOrWhiteSpace(title)) title = slug;

            // Chapter
            var chapMatch = Regex.Match(cardContent, @"title=""(Chap[^""]*|Chương[^""]*|Ch\.[^""]*|Một Bắn[^""]*)""", RegexOptions.IgnoreCase);
            if (!chapMatch.Success)
            {
                chapMatch = Regex.Match(cardContent, @"<span[^>]*class=""[^""]*text-white/90[^""]*""[^>]*title=""([^""]+)""", RegexOptions.IgnoreCase);
            }
            var latestChapterTitle = chapMatch.Success ? StripHtml(chapMatch.Groups[1].Value) : "";

            // Updated time
            var timeMatch = Regex.Match(cardContent, @"title=""([^""]+)""[^>]*>\s*[^<]*trc\s*</span>", RegexOptions.IgnoreCase);
            var updatedAt = timeMatch.Success ? ParseTimeAgo(timeMatch.Groups[1].Value) : DateTime.UtcNow;

            var mangaId = CreateGuid("vinahentai:manga:" + slug);
            RegisterManga(mangaId, slug);

            // Parse chapter number
            decimal chapNum = 0;
            if (!string.IsNullOrEmpty(latestChapterTitle))
            {
                var numMatch = Regex.Match(latestChapterTitle, @"(?:\b|[^\w\d])(?:chương|chapter|chap|ch|c)?[\s\._-]*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
                if (numMatch.Success)
                {
                    decimal.TryParse(numMatch.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out chapNum);
                }
            }

            var chapters = new List<ChapterCard>();
            if (!string.IsNullOrEmpty(latestChapterTitle))
            {
                var chapSlug = chapNum > 0 ? $"chap-{chapNum}" : "chap-1";
                var chapId = CreateGuid("vinahentai:chap:" + slug + ":" + chapSlug);
                var chapHref = $"/truyen-hentai/{slug}/{chapSlug}";
                RegisterChapter(chapId, chapHref, mangaId);
                chapters.Add(new ChapterCard(chapId, mangaId, latestChapterTitle, chapNum, "vi", updatedAt, "VinaHentai"));
            }

            var card = new MangaCard
            {
                Id = mangaId,
                Slug = slug,
                Title = title,
                AlternativeTitle = title,
                Author = "VinaHentai",
                Cover = !string.IsNullOrEmpty(coverUrl) ? coverUrl : "/cover-placeholder.svg",
                Description = "",
                Status = "ongoing",
                Country = "ja",
                Demographic = "VinaHentai",
                ContentRating = "erotica",
                Year = updatedAt.Year,
                Rating = 9.0,
                Follows = 100,
                UpdatedAt = updatedAt,
                Chapters = chapters,
                TotalChapters = (int)Math.Max(1, Math.Round(chapNum)),
                Genres = ["Hentai", "Manga"]
            };

            items.Add(card);

            if (meili != null)
            {
                _ = Task.Run(async () => {
                    try {
                        var index = meili.Index("mangas");
                        await index.AddDocumentsAsync(new[] { card });
                    } catch { }
                });
            }
        }

        return items;
    }

    public async Task<MangaCard?> GetDetail(Guid id)
    {
        var slug = await ResolveSlug(id);
        if (string.IsNullOrEmpty(slug)) return null;

        var url = $"{BaseUrl}/truyen-hentai/{slug}";
        var html = await FetchHtml(url);
        if (string.IsNullOrEmpty(html)) return null;

        var titleMatch = Regex.Match(html, @"<h1[^>]*>([^<]+)</h1>", RegexOptions.IgnoreCase);
        var title = titleMatch.Success ? StripHtml(titleMatch.Groups[1].Value) : slug;

        // Description
        var descMatch = Regex.Match(html, @"<div class=""text-txt-secondary[^""]*whitespace-pre-line[^""]*"">([\s\S]*?)</div>", RegexOptions.IgnoreCase);
        var desc = descMatch.Success ? StripHtml(descMatch.Groups[1].Value) : "";

        // Author
        var authorMatch = Regex.Match(html, @"href=""/authors/[^""]*""[^>]*>[\s\S]*?<span class=""truncate[^""]*"">([^<]+)</span>", RegexOptions.IgnoreCase);
        var author = authorMatch.Success ? StripHtml(authorMatch.Groups[1].Value) : "VinaHentai";

        // Translator
        var transMatch = Regex.Match(html, @"href=""/translators/[^""]*""[^>]*>[\s\S]*?<span class=""truncate[^""]*"">([^<]+)</span>", RegexOptions.IgnoreCase);
        var translator = transMatch.Success ? StripHtml(transMatch.Groups[1].Value) : "";

        // Cover
        var coverMatch = Regex.Match(html, @"<img\s+[^>]*src=""([^""]+)""[^>]*alt=""Bìa truyện", RegexOptions.IgnoreCase);
        if (!coverMatch.Success)
        {
            coverMatch = Regex.Match(html, @"property=""og:image""\s+content=""([^""]+)""", RegexOptions.IgnoreCase);
        }
        var coverUrl = coverMatch.Success ? coverMatch.Groups[1].Value : "/cover-placeholder.svg";

        // Status
        var statusMatch = Regex.Match(html, @"Tình trạng:</div>\s*<div[^>]*>([^<]+)</div>", RegexOptions.IgnoreCase);
        var statusStr = statusMatch.Success ? StripHtml(statusMatch.Groups[1].Value) : "Đang tiến hành";
        var status = statusStr.Contains("hoàn thành", StringComparison.OrdinalIgnoreCase) ? "completed" : "ongoing";

        // Genres
        var genreMatches = Regex.Matches(html, @"href=""/genres/[^""]*""[^>]*><span[^>]*>([^<]+)</span>", RegexOptions.IgnoreCase);
        var genres = genreMatches.Select(g => StripHtml(g.Groups[1].Value)).Distinct().ToArray();
        if (genres.Length == 0) genres = ["Hentai", "Manga"];

        // Chapters
        var chapRegex = new Regex(@"href=""(/truyen-hentai/[^/\""]+/([^/\""]+))""[^>]*>([\s\S]*?)</a>", RegexOptions.IgnoreCase);
        var chapMatches = chapRegex.Matches(html);
        var chapters = new List<ChapterCard>();

        foreach (Match cm in chapMatches)
        {
            var chapHref = cm.Groups[1].Value;
            var chapSlug = cm.Groups[2].Value;
            var rawInner = cm.Groups[3].Value;

            var chapTitleMatch = Regex.Match(rawInner, @"<span[^>]*>([^<]+)</span>", RegexOptions.IgnoreCase);
            var chapTitle = chapTitleMatch.Success ? StripHtml(chapTitleMatch.Groups[1].Value) : chapSlug;

            if (chapTitle.Equals("Đọc từ đầu", StringComparison.OrdinalIgnoreCase) ||
                chapTitle.Equals("Đọc mới nhất", StringComparison.OrdinalIgnoreCase) ||
                chapTitle.Equals(chapSlug, StringComparison.OrdinalIgnoreCase))
            {
                var numM = Regex.Match(chapSlug, @"\d+");
                if (numM.Success) chapTitle = $"Chap {numM.Value}";
            }

            var timeMatch = Regex.Match(rawInner, @"dateTime=""([^""]+)""", RegexOptions.IgnoreCase);
            if (!timeMatch.Success) timeMatch = Regex.Match(rawInner, @"title=""([^""]+)""", RegexOptions.IgnoreCase);
            var publishedAt = timeMatch.Success ? ParseTimeAgo(timeMatch.Groups[1].Value) : DateTime.UtcNow;

            var numMatch = Regex.Match(chapTitle, @"(?:\b|[^\w\d])(?:chương|chapter|chap|ch|c)?[\s\._-]*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
            decimal num = 0;
            if (numMatch.Success)
            {
                decimal.TryParse(numMatch.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out num);
            }

            var chapId = CreateGuid("vinahentai:chap:" + slug + "/" + chapSlug);
            RegisterChapter(chapId, chapHref, id);

            chapters.Add(new ChapterCard(chapId, id, chapTitle, num, "vi", publishedAt, !string.IsNullOrEmpty(translator) ? translator : "VinaHentai"));
        }

        var cleanChaps = Catalog.DeduplicateChapters(chapters, ascending: false);

        var card = new MangaCard
        {
            Id = id,
            Slug = slug,
            Title = title,
            AlternativeTitle = title,
            Author = author,
            Cover = coverUrl,
            Description = desc,
            Status = status,
            Country = "ja",
            Demographic = "VinaHentai",
            ContentRating = "erotica",
            Year = DateTime.UtcNow.Year,
            Rating = 9.2,
            Follows = 250,
            UpdatedAt = cleanChaps.Count > 0 ? cleanChaps[0].PublishedAt : DateTime.UtcNow,
            Chapters = cleanChaps,
            TotalChapters = cleanChaps.Count > 0 ? (int)Math.Max(cleanChaps.Count, Math.Round(cleanChaps.Max(c => c.Number))) : 1,
            Genres = genres
        };

        return card;
    }

    public async Task<ChapterPage?> GetChapters(Guid mangaId, int page = 1, int pageSize = 100, bool ascending = false)
    {
        var card = await GetDetail(mangaId);
        if (card == null) return null;

        var ordered = ascending
            ? card.Chapters.OrderBy(c => c.Number).ThenBy(c => c.PublishedAt).ToList()
            : card.Chapters.OrderByDescending(c => c.Number).ThenByDescending(c => c.PublishedAt).ToList();

        var total = ordered.Count;
        var pagedItems = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return new ChapterPage(pagedItems, total, page, pageSize);
    }

    public async Task<ReaderData?> GetReader(Guid chapterId)
    {
        var chapHref = await ResolveChapterUrl(chapterId);
        if (string.IsNullOrEmpty(chapHref)) return null;

        var mangaId = await ResolveChapterMangaId(chapterId);
        var url = chapHref.StartsWith("http") ? chapHref : BaseUrl + (chapHref.StartsWith("/") ? "" : "/") + chapHref;

        var html = await FetchHtml(url);
        if (string.IsNullOrEmpty(html)) return null;

        // Extract image URLs
        var imgMatches = Regex.Matches(html, @"https://vnht\.vinahentai\.pics/manga-images/[a-zA-Z0-9_\-\.]+\.(?:webp|jpg|jpeg|png)", RegexOptions.IgnoreCase);
        var pages = new List<string>();
        foreach (Match im in imgMatches)
        {
            var u = im.Value;
            if (!pages.Contains(u))
            {
                pages.Add(u);
            }
        }

        // Title and Chapter Title
        var titleMatch = Regex.Match(html, @"<title>([^|<]+)\s*\|\s*([^<]+)</title>", RegexOptions.IgnoreCase);
        var chapterTitle = titleMatch.Success ? StripHtml(titleMatch.Groups[1].Value) : "Chương";
        var mangaTitle = titleMatch.Success ? StripHtml(titleMatch.Groups[2].Value) : "Truyện";

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
            Author = "VinaHentai"
        };

        var currentChap = new ChapterCard(chapterId, mangaId, chapterTitle, num, "vi", DateTime.UtcNow, "VinaHentai");

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

    public async Task<List<MangaCard>> Search(string query, int page = 1)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var url = $"{BaseUrl}/search?q={Uri.EscapeDataString(query.Trim())}&page={page}";
        var html = await FetchHtml(url);
        if (string.IsNullOrEmpty(html)) return [];

        var items = new List<MangaCard>();
        var cardRegex = new Regex(@"<a\s+[^>]*href=""/truyen-hentai/([^""/]+)""[^>]*>([\s\S]*?)</a>", RegexOptions.IgnoreCase);
        var matches = cardRegex.Matches(html);

        foreach (Match m in matches)
        {
            var slug = m.Groups[1].Value.Trim();
            if (string.IsNullOrEmpty(slug)) continue;

            var cardContent = m.Groups[2].Value;

            var imgMatch = Regex.Match(cardContent, @"<img\s+[^>]*src=""([^""]+)""", RegexOptions.IgnoreCase);
            var coverUrl = imgMatch.Success ? imgMatch.Groups[1].Value : "/cover-placeholder.svg";

            var h2Match = Regex.Match(cardContent, @"<h2[^>]*>([^<]+)</h2>", RegexOptions.IgnoreCase);
            var title = h2Match.Success ? StripHtml(h2Match.Groups[1].Value) : slug;

            var pMatch = Regex.Match(cardContent, @"<p[^>]*class=""[^""]*italic[^""]*""[^>]*>([^<]+)</p>", RegexOptions.IgnoreCase);
            var altTitle = pMatch.Success ? StripHtml(pMatch.Groups[1].Value) : title;

            var genreMatches = Regex.Matches(cardContent, @"<span class=""text-xs font-medium capitalize"">([^<]+)</span>", RegexOptions.IgnoreCase);
            var genres = genreMatches.Select(g => StripHtml(g.Groups[1].Value)).Distinct().ToArray();
            if (genres.Length == 0) genres = ["Hentai", "Manga"];

            var chapsMatch = Regex.Match(cardContent, @"(\d+)\s*(?:<!--\s*-->)?\s*chương", RegexOptions.IgnoreCase);
            int totalChaps = 1;
            if (chapsMatch.Success && int.TryParse(chapsMatch.Groups[1].Value, out var cCount))
            {
                totalChaps = cCount;
            }

            var mangaId = CreateGuid("vinahentai:manga:" + slug);
            RegisterManga(mangaId, slug);

            var card = new MangaCard
            {
                Id = mangaId,
                Slug = slug,
                Title = title,
                AlternativeTitle = altTitle,
                Author = "VinaHentai",
                Cover = coverUrl,
                Description = "",
                Status = "ongoing",
                Country = "ja",
                Demographic = "VinaHentai",
                ContentRating = "erotica",
                Year = DateTime.UtcNow.Year,
                Rating = 9.0,
                Follows = 150,
                UpdatedAt = DateTime.UtcNow,
                Chapters = [],
                TotalChapters = totalChaps,
                Genres = genres
            };

            items.Add(card);

            if (meili != null)
            {
                _ = Task.Run(async () => {
                    try {
                        var index = meili.Index("mangas");
                        await index.AddDocumentsAsync(new[] { card });
                    } catch { }
                });
            }
        }

        return items;
    }


    public async Task<List<VinaGenre>> GetGenres()
    {
        var cacheKey = "vinahentai:genres:list";
        var cached = await CacheGetString(cacheKey);
        if (!string.IsNullOrEmpty(cached))
        {
            try
            {
                var parsed = System.Text.Json.JsonSerializer.Deserialize<List<VinaGenre>>(cached);
                if (parsed != null && parsed.Count > 0) return parsed;
            }
            catch { }
        }

        if (cache.TryGetValue<List<VinaGenre>>(cacheKey, out var memList) && memList != null && memList.Count > 0)
        {
            return memList;
        }

        var html = await FetchHtml($"{BaseUrl}/genres");
        var list = new List<VinaGenre>();
        if (!string.IsNullOrEmpty(html))
        {
            var cardRegex = new Regex(@"<a\s+[^>]*href=""/genres/([^""]+)""[^>]*>([\s\S]*?)</a>", RegexOptions.IgnoreCase);
            var matches = cardRegex.Matches(html);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match m in matches)
            {
                var slug = m.Groups[1].Value.Trim();
                if (string.IsNullOrEmpty(slug) || seen.Contains(slug)) continue;
                seen.Add(slug);

                var cardContent = m.Groups[2].Value;

                var nameMatch = Regex.Match(cardContent, @"<div[^>]*font-semibold[^>]*>([^<]+)</div>", RegexOptions.IgnoreCase);
                if (!nameMatch.Success)
                {
                    nameMatch = Regex.Match(cardContent, @"<div[^>]*>([^<]+)</div>", RegexOptions.IgnoreCase);
                }
                var name = nameMatch.Success ? StripHtml(nameMatch.Groups[1].Value) : slug;

                var descMatch = Regex.Match(cardContent, @"<p[^>]*>([^<]+)</p>", RegexOptions.IgnoreCase);
                var desc = descMatch.Success ? StripHtml(descMatch.Groups[1].Value) : "";

                list.Add(new VinaGenre(slug, name, desc));
            }
        }

        if (list.Count == 0)
        {
            list = [.. DefaultGenres];
        }

        list = list.OrderBy(g => g.Name).ToList();
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(list);
            await CacheSetString(cacheKey, json, TimeSpan.FromHours(24));
        }
        catch { }
        cache.Set(cacheKey, list, TimeSpan.FromHours(24));
        return list;
    }

    public async Task<List<MangaCard>> GetByGenre(string genreSlug, int page = 1)
    {
        if (string.IsNullOrWhiteSpace(genreSlug)) return [];
        var cleanSlug = genreSlug.Trim().ToLowerInvariant();
        var url = $"{BaseUrl}/genres/{Uri.EscapeDataString(cleanSlug)}?page={page}";
        var html = await FetchHtml(url);
        if (string.IsNullOrEmpty(html)) return [];

        var items = new List<MangaCard>();
        var cardRegex = new Regex(@"<a\s+[^>]*href=""/truyen-hentai/([^""/]+)""[^>]*>([\s\S]*?)</a>", RegexOptions.IgnoreCase);
        var matches = cardRegex.Matches(html);

        foreach (Match m in matches)
        {
            var slug = m.Groups[1].Value.Trim();
            if (string.IsNullOrEmpty(slug)) continue;

            var cardContent = m.Groups[2].Value;

            // Cover
            var imgMatch = Regex.Match(cardContent, @"<img[^>]+src=""([^"">]+)""[^>]*alt=""([^"">]*)""", RegexOptions.IgnoreCase);
            if (!imgMatch.Success)
            {
                imgMatch = Regex.Match(cardContent, @"<img[^>]+alt=""([^"">]*)""[^>]*src=""([^"">]+)""", RegexOptions.IgnoreCase);
            }
            var coverUrl = imgMatch.Success ? (imgMatch.Groups[1].Value.StartsWith("http") ? imgMatch.Groups[1].Value : imgMatch.Groups[2].Value) : "";
            var imgAlt = imgMatch.Success ? (imgMatch.Groups[2].Value.StartsWith("http") ? imgMatch.Groups[1].Value : imgMatch.Groups[2].Value) : "";

            // Title
            var titleMatch = Regex.Match(cardContent, @"title=""([^""]+)""\s*>\s*([^<]+)\s*</div>\s*</div>\s*</div>", RegexOptions.IgnoreCase);
            if (!titleMatch.Success)
            {
                titleMatch = Regex.Match(cardContent, @"<div class=""mt-1\.5[^""]*"" title=""([^""]+)""", RegexOptions.IgnoreCase);
            }
            var title = titleMatch.Success ? StripHtml(titleMatch.Groups[1].Value) : StripHtml(imgAlt);
            if (string.IsNullOrWhiteSpace(title)) title = slug;

            // Chapter
            var chapMatch = Regex.Match(cardContent, @"title=""(Chap[^""]*|Chương[^""]*|Ch\.[^""]*|Một Bắn[^""]*)""", RegexOptions.IgnoreCase);
            if (!chapMatch.Success)
            {
                chapMatch = Regex.Match(cardContent, @"<span[^>]*class=""[^""]*text-white/90[^""]*""[^>]*title=""([^""]+)""", RegexOptions.IgnoreCase);
            }
            var latestChapterTitle = chapMatch.Success ? StripHtml(chapMatch.Groups[1].Value) : "";

            // Updated time
            var timeMatch = Regex.Match(cardContent, @"title=""([^""]+)""[^>]*>\s*[^<]*trc\s*</span>", RegexOptions.IgnoreCase);
            var updatedAt = timeMatch.Success ? ParseTimeAgo(timeMatch.Groups[1].Value) : DateTime.UtcNow;

            var mangaId = CreateGuid("vinahentai:manga:" + slug);
            RegisterManga(mangaId, slug);

            // Parse chapter number
            decimal chapNum = 0;
            if (!string.IsNullOrEmpty(latestChapterTitle))
            {
                var numMatch = Regex.Match(latestChapterTitle, @"(?:\b|[^\w\d])(?:chương|chapter|chap|ch|c)?[\s\._-]*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
                if (numMatch.Success)
                {
                    decimal.TryParse(numMatch.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out chapNum);
                }
            }

            var chapters = new List<ChapterCard>();
            if (!string.IsNullOrEmpty(latestChapterTitle))
            {
                var chapSlug = chapNum > 0 ? $"chap-{chapNum}" : "chap-1";
                var chapId = CreateGuid("vinahentai:chap:" + slug + ":" + chapSlug);
                var chapHref = $"/truyen-hentai/{slug}/{chapSlug}";
                RegisterChapter(chapId, chapHref, mangaId);
                chapters.Add(new ChapterCard(chapId, mangaId, latestChapterTitle, chapNum, "vi", updatedAt, "VinaHentai"));
            }

            var card = new MangaCard
            {
                Id = mangaId,
                Slug = slug,
                Title = title,
                AlternativeTitle = title,
                Author = "VinaHentai",
                Cover = !string.IsNullOrEmpty(coverUrl) ? coverUrl : "/cover-placeholder.svg",
                Description = "",
                Status = "ongoing",
                Country = "ja",
                Demographic = "VinaHentai",
                ContentRating = "erotica",
                Year = updatedAt.Year,
                Rating = 9.0,
                Follows = 100,
                UpdatedAt = updatedAt,
                Chapters = chapters,
                TotalChapters = (int)Math.Max(1, Math.Round(chapNum)),
                Genres = [cleanSlug, "Hentai", "Manga"]
            };

            items.Add(card);

            if (meili != null)
            {
                _ = Task.Run(async () => {
                    try {
                        var index = meili.Index("mangas");
                        await index.AddDocumentsAsync(new[] { card });
                    } catch { }
                });
            }
        }

        return items;
    }

    public static readonly List<VinaGenre> DefaultGenres = new()
    {
        new("3d-hentai", "3D Hentai", "Truyện tranh hoặc video hentai sử dụng đồ họa 3D."),
        new("action", "Action", "Khám phá truyện thuộc thể loại này."),
        new("adult", "Adult", "Khám phá truyện thuộc thể loại này."),
        new("adventure", "Adventure", "Nhân vật bước vào hành trình phiêu lưu: thế giới fantasy, dị giới, hoặc vùng đất xa lạ. Nội dung kết hợp tình dục với yếu tố hành động, khám phá, và cốt truyện dài."),
        new("ahegao", "Ahegao", "Biểu cảm nhân vật khi đạt cực khoái mãnh liệt: mắt đảo, lưỡi thè, mặt đỏ, nước dãi chảy ra. Thể hiện sự mất kiểm soát và khoái cảm tột độ."),
        new("ai-generated", "AI Generated", "Tác phẩm do AI tạo ra (nét vẽ, hình ảnh, truyện); nhấn mạnh phong cách, hiệu ứng và sự độc đáo của công cụ AI."),
        new("anal", "Anal", "Quan hệ tình dục qua đường hậu môn."),
        new("angel", "Angel", "Khám phá truyện thuộc thể loại này."),
        new("anh-dong", "Ảnh động", "Khám phá truyện thuộc thể loại này."),
        new("animal", "Animal", "Khám phá truyện thuộc thể loại này."),
        new("animal-girl", "Animal Girl", "Nhân vật nữ mang đặc điểm động vật như tai, đuôi, móng vuốt (mèo, chó, cáo, thỏ...). Tính cách thường dễ thương hoặc hoang dã, gợi cảm theo kiểu \"nửa người nửa thú\"."),
        new("ao-dai", "Áo dài", "Khám phá truyện thuộc thể loại này."),
        new("apron", "Apron", "Tạp dề không nội y. Nhân vật nữ chỉ mặc duy nhất chiếc tạp dề mỏng, thường là vợ đảm, mẹ kế hoặc cô gái phục vụ trong nhà – mang cảm giác \"nguy hiểm trong an toàn\"."),
        new("artist-cg", "Artist CG", "Bộ tranh vẽ minh họa (CG – computer graphic) từ các họa sĩ hentai nổi tiếng. Thường không có cốt truyện rõ, chủ yếu là các cảnh sex chất lượng cao và tạo hình đẹp."),
        new("aunt", "Aunt", "Quan hệ với dì/cô — người phụ nữ lớn tuổi hơn trong họ hàng, thường gắn với sự vụng trộm trong gia đình."),
        new("based-game", "Based Game", "Nội dung hentai được chuyển thể từ game người lớn. Có thể giữ nguyên tạo hình, tình huống và bối cảnh của game gốc."),
        new("bbm", "BBM", "Nhân vật nam có thân hình to lớn, cơ bắp, thường xuất hiện trong các cảnh mạnh mẽ, quyền lực."),
        new("bbw", "BBW", "Nhân vật nữ có thân hình đầy đặn, quyến rũ, tập trung vào vẻ đẹp tự nhiên và sự gợi cảm."),
        new("bdsm", "BDSM", "Trói buộc, kiểm soát, đau đớn, phục tùng. Gồm nhiều cấp độ: từ nhẹ (bịt mắt, trói tay) đến nặng (đánh, tra tấn, điều khiển tâm lý)."),
        new("beach", "Beach", "Bối cảnh diễn ra tại bãi biển hoặc hồ bơi, nơi nhân vật mặc bikini, đồ bơi hở hang, cơ thể ướt át. Thường mang không khí vui tươi, gợi cảm và tự do."),
        new("bestiality", "Bestiality", "Quan hệ tình dục giữa con người và động vật thật, mang yếu tố cấm kỵ, gây tranh cãi mạnh."),
        new("big-ass", "Big Ass", "Tập trung vào nhân vật nữ có vòng 3 to, tròn, căng mẩy."),
        new("big-boobs", "Big Boobs", "Vòng 1 ngoại cỡ, thường rung động mạnh khi va chạm. Nét vẽ được phóng đại chuyển động và độ căng mọng của ngực."),
        new("big-penis", "Big Penis", "Dương vật ngoại cỡ, quá khổ so với bình thường. Nhấn mạnh sự đau đớn – sung sướng cực độ từ việc bị “nhồi nhét” quá mức."),
        new("bikini", "Bikini", "Khám phá truyện thuộc thể loại này."),
        new("bisexual", "Bisexual", "Nhân vật có xu hướng tình dục với cả nam và nữ, thể hiện sự đa dạng trong các mối quan hệ."),
        new("black-skin", "Black Skin", "Khám phá truyện thuộc thể loại này."),
        new("blackmail", "Blackmail", "Cưỡng ép quan hệ bằng cách uy hiếp, đe dọa (bằng ảnh, clip, bí mật...). Tình huống thường căng thẳng, nhân vật rơi vào trạng thái vừa chống cự vừa bị kích thích."),
        new("blindfold", "Blindfold", "Quan hệ khi nhân vật bị bịt mắt, tăng cảm giác bí ẩn và kích thích."),
        new("bloomers", "Bloomers", "Nhân vật mặc quần bó thể thao kiểu Nhật, thường xuất hiện trong bối cảnh học đường hoặc luyện tập."),
        new("blowjobs", "BlowJobs", "Dùng miệng để kích thích dương vật. Có thể đi kèm các kỹ thuật sâu như lick, suck, deepthroat."),
        new("body-swap", "Body Swap", "Khám phá truyện thuộc thể loại này."),
        new("body-writing", "Body Writing", "Viết chữ hoặc vẽ lên cơ thể nhân vật, tạo cảm giác sở hữu và kích thích thị giác."),
        new("bodysuit", "Bodysuit", "Trang phục bó sát toàn thân (như latex, da bóng). Tôn lên đường cong, tạo cảm giác ngộp thở, quyến rũ và ướt át."),
        new("bondage", "Bondage", "Khám phá truyện thuộc thể loại này."),
        new("breast-sucking", "Breast Sucking", "Hành động bú hoặc mút ngực, nhấn mạnh sự thân mật và khoái cảm."),
        new("breastjobs", "BreastJobs", "Kẹp dương vật giữa hai bầu ngực và di chuyển lên xuống để kích thích. Thường đi kèm với cảnh xuất tinh lên mặt hoặc ngực."),
        new("brocon", "Brocon", "Khám phá truyện thuộc thể loại này."),
        new("brother", "Brother", "Khám phá truyện thuộc thể loại này."),
        new("bukkake", "Bukkake", "Nhiều nhân vật xuất tinh lên một người, tạo cảnh tượng táo bạo và quá tải khoái cảm."),
        new("bunny-girl", "Bunny Girl", "Nhân vật nữ mặc trang phục thỏ gợi cảm với tai và tất lưới, tạo sức hút quyến rũ."),
        new("business-suit", "Business Suit", "Trang phục công sở bó sát – váy bút chì, áo sơ mi ôm, vest nữ. Hình ảnh “chị sếp/ thư ký” gợi cảm."),
        new("chastity-belt", "Chastity belt", "Nhân vật đeo đai trinh tiết, nhấn mạnh sự kiểm soát và cấm đoán trong ham muốn."),
        new("che-it", "Che ít", "Khám phá truyện thuộc thể loại này."),
        new("che-nhieu", "Che nhiều", "Nội dung gợi cảm nhưng vẫn kín đáo, tập trung vào trang phục che phủ nhiều nhưng vẫn khêu gợi."),
        new("cheating", "Cheating", "Khám phá truyện thuộc thể loại này."),
        new("cheerleader", "Cheerleader", "Nhân vật mặc đồng phục cổ động năng động, gợi cảm trong bối cảnh thể thao hoặc học đường."),
        new("chikan", "Chikan", "Quấy rối tình dục nơi công cộng (tàu điện/xe buýt). Nhấn mạnh sự lén lút và cảm giác cấm kỵ."),
        new("chinese-dress", "Chinese Dress", "Trang phục sườn xám xẻ cao, ôm sát, khoe đường cong. Thường gợi cảm tinh tế."),
        new("co-che", "Có che", "Khám phá truyện thuộc thể loại này."),
        new("collar", "Collar", "Vòng cổ biểu thị sự phục tùng, thuộc quyền sở hữu hoặc gắn kết quan hệ chủ - tớ."),
        new("comedy", "Comedy", "Tình huống tình dục pha hài hước, các “tai nạn”/hiểu lầm dẫn đến cảnh nóng vui nhộn."),
        new("comic", "Comic", "Khám phá truyện thuộc thể loại này."),
        new("condom", "Condom", "Quan hệ sử dụng bao cao su — nhấn mạnh an toàn hoặc như một chi tiết gợi cảm."),
        new("cosplay", "Cosplay", "Nhân vật hóa trang sexy như nữ hầu, bunny girl, nhân vật anime/game nổi tiếng, tăng yếu tố mới lạ và hấp dẫn thị giác."),
        new("cousin", "Cousin", "Khám phá truyện thuộc thể loại này."),
        new("creampie", "Creampie", "Quan hệ tình dục với hành động xuất tinh bên trong âm đạo, nhấn mạnh sự thân mật, khoái cảm trọn vẹn và cảm giác chiếm hữu."),
        new("cross-dressing", "Cross-dressing", "Nhân vật mặc trang phục khác giới (nam giả nữ hoặc nữ giả nam), tạo tình huống bất ngờ, gợi cảm."),
        new("crotch-tattoo", "Crotch Tattoo", "Hình xăm vùng kín, cảm giác nổi loạn, táo bạo."),
        new("cuckold", "Cuckold", "Chủ đề ngoại tình/“cắm sừng” (cuckold), thường gắn với NTR hoặc yếu tố tâm lý."),
        new("cum-swap", "Cum swap", "Trao đổi tinh dịch qua miệng giữa các nhân vật, tạo cảm giác thân mật táo bạo."),
        new("cunnilingus", "Cunnilingus", "Quan hệ bằng miệng tập trung kích thích âm đạo bằng môi và lưỡi."),
        new("dark-skin", "Dark Skin", "Làn da nâu/sẫm. Thường được vẽ với vẻ đẹp gợi cảm, mạnh mẽ."),
        new("daughter", "Daughter", "Khám phá truyện thuộc thể loại này."),
        new("deepthroat", "Deepthroat", "Kỹ thuật ngậm sâu toàn bộ dương vật, thường gây nghẹn – cảm giác mãnh liệt."),
        new("demon", "Demon", "Khám phá truyện thuộc thể loại này."),
        new("demongirl", "DemonGirl", "Nữ quỷ (sừng/đuôi/ánh mắt dâm đãng), thường chủ động và thống trị."),
        new("devil", "Devil", "Khám phá truyện thuộc thể loại này."),
        new("devilgirl", "DevilGirl", "Khám phá truyện thuộc thể loại này."),
        new("dirty", "Dirty", "Khám phá truyện thuộc thể loại này."),
        new("dirtyoldman", "DirtyOldMan", "Khám phá truyện thuộc thể loại này."),
        new("double-penetration", "Double Penetration", "Quan hệ đồng thời qua hai đường (âm đạo & hậu môn)."),
        new("doujinshi", "Doujinshi", "Truyện 18+ do fan sáng tác, dùng nhân vật anime/game nổi tiếng hoặc tự tạo."),
        new("drama", "Drama", "Khám phá truyện thuộc thể loại này."),
        new("drug", "Drug", "Khám phá truyện thuộc thể loại này."),
        new("drunk", "Drunk", "Nhân vật say rượu, hành động tình dục diễn ra trong trạng thái mất kiểm soát hoặc mơ hồ."),
        new("ecchi", "Ecchi", "Tình huống hớ hênh, đụng chạm vô tình… tạo tình huống nhạy cảm."),
        new("elder-sister", "Elder Sister", "Chị gái trong các mối quan hệ gia đình hoặc cấm kỵ, thường vừa che chở vừa cám dỗ."),
        new("elf", "Elf", "Chủng tộc yêu tinh, tai nhọn, mảnh mai; thường trong bối cảnh fantasy."),
        new("exhibitionism", "Exhibitionism", "Lộ bộ phận nhạy cảm trước người khác; thường gắn với tình huống bị nhìn lén, rình trộm hoặc chủ động phô bày táo bạo."),
        new("facesitting", "Facesitting", "Nhân vật ngồi lên mặt đối phương để được phục vụ khoái cảm trực tiếp."),
        new("fantasy", "Fantasy", "Bối cảnh giả tưởng, sinh vật huyền ảo; kết hợp yếu tố tình dục."),
        new("father", "Father", "Khám phá truyện thuộc thể loại này."),
        new("females-only", "Females only", "Chỉ có các nhân vật nữ xuất hiện, tạo không gian đồng giới hoặc nữ quyền."),
        new("femdom", "Femdom", "Nữ làm chủ/điều khiển, áp đảo nam trong quan hệ tình dục."),
        new("feminization", "Feminization", "Nam bị biến đổi hoặc hóa trang thành nữ, nhấn mạnh sự thay đổi giới tính và tâm lý."),
        new("filming", "Filming", "Cảnh làm tình bị quay phim hoặc ghi hình, có thể lén lút hoặc cố ý."),
        new("fingering", "Fingering", "Kích thích âm đạo hoặc điểm G bằng tay, thường là màn dạo đầu gợi cảm, thể hiện sự khéo léo và phản ứng mãnh liệt của nhân vật."),
        new("footjob", "Footjob", "Kích thích dương vật bằng bàn chân."),
        new("full-color", "Full Color", "Toàn bộ truyện được tô màu; màu sắc sống động tăng kích thích thị giác."),
        new("furry", "Furry", "Khám phá truyện thuộc thể loại này."),
        new("futanari", "Futanari", "Khám phá truyện thuộc thể loại này."),
        new("gag", "Gag", "Nhân vật bị bịt miệng bằng đồ vật, tăng cảm giác bị khống chế và phục tùng."),
        new("gangbang", "Gangbang", "Khám phá truyện thuộc thể loại này."),
        new("garter-belts", "Garter Belts", "Đai giữ tất tôn vẻ đẹp đôi chân và đường cong cơ thể, tăng độ gợi cảm."),
        new("gender-bender", "Gender Bender", "Nhân vật bị biến đổi giới tính (nam thành nữ hoặc ngược lại) qua phép thuật, thuốc hoặc công nghệ, tạo nên các tình huống hài hước và kích thích."),
        new("ghost", "Ghost", "Khám phá truyện thuộc thể loại này."),
        new("glasses", "Glasses", "Khám phá truyện thuộc thể loại này."),
        new("glory-hole", "Glory hole", "Quan hệ qua lỗ kín trên vách ngăn, tăng yếu tố ẩn danh, bí ẩn và táo bạo."),
        new("goc-nhin-nu", "Góc Nhìn Nữ", "Góc nhìn nữ (Female POV) là thể loại kể chuyện đặt nhân vật nữ làm trung tâm trải nghiệm. Câu chuyện được dẫn dắt qua cảm xúc, suy nghĩ, nội tâm và cách nhìn nhận thế giới của nữ chính."),
        new("gothic-lolita", "Gothic Lolita", "Khám phá truyện thuộc thể loại này."),
        new("group", "Group", "Cảnh quan hệ tình dục có từ ba người trở lên, tập trung vào tương tác chéo giữa các nhân vật, mang lại cảm giác mới lạ và mạnh mẽ."),
        new("guro", "Guro", "Khám phá truyện thuộc thể loại này."),
        new("gyaru", "Gyaru", "Nữ sinh phong cách gyaru: da rám nắng, trang điểm đậm, tóc nhuộm sáng, thời trang sành điệu và tính cách bạo dạn."),
        new("hairjob", "Hairjob", "Kích thích bằng tóc, tạo trải nghiệm thị giác và xúc giác mới lạ."),
        new("hairy", "Hairy", "Nhân vật nữ có lông vùng kín rậm rạp, nhấn mạnh sự tự nhiên, trưởng thành và quyến rũ nguyên bản."),
        new("handjob", "Handjob", "Khám phá truyện thuộc thể loại này."),
        new("harem", "Harem", "Một nhân vật (thường nam) được nhiều nhân vật khác giới vây quanh."),
        new("hell-no", "Hell No", "Nội dung bị từ chối hoặc phản đối mạnh, đôi khi dùng làm yếu tố hài hước."),
        new("hentaivn", "Hentaivn", "HentaiVN – web đọc truyện hentai vietsub, hentai không che, cập nhật nhanh, nội dung 18+ đa dạng, chất lượng cao."),
        new("hidden-sex", "Hidden sex", "Quan hệ diễn ra bí mật, tránh bị người khác phát hiện, tăng cảm giác hồi hộp."),
        new("historical", "Historical", "Bối cảnh lịch sử, xa xưa; trang phục, phong tục và không gian cổ điển tạo nên sắc thái gợi cảm đặc trưng."),
        new("horror", "Horror", "Khám phá truyện thuộc thể loại này."),
        new("housewife", "Housewife", "Khám phá truyện thuộc thể loại này."),
        new("humiliation", "Humiliation", "Làm nhục, làm xấu hổ; mang tính ép buộc hoặc đặt nhân vật vào tình cảnh tiến thoái lưỡng nan không thể trốn tránh."),
        new("idol", "Idol", "Khám phá truyện thuộc thể loại này."),
        new("imouto", "Imouto", "Em gái dễ thương, gắn bó với nhân vật chính, đôi khi phát triển cảm xúc cấm kỵ."),
        new("incest", "Incest", "Khám phá truyện thuộc thể loại này."),
        new("insect", "Insect", "Nhân vật hoặc sinh vật côn trùng tham gia cảnh tình dục kỳ lạ (khác với &#x27;Côn trùng&#x27; bản địa)."),
        new("invisible", "Invisible", "Nhân vật tàng hình hoặc không bị nhìn thấy, lợi dụng điều đó để tiếp cận đối phương."),
        new("isekai", "Isekai", "Nhân vật sang thế giới khác; pha trộn phiêu lưu/ma thuật và tình dục."),
        new("hentai-khong-che", "Không che", "Khám phá truyện thuộc thể loại này."),
        new("kimono", "Kimono", "Khám phá truyện thuộc thể loại này."),
        new("kissing", "Kissing", "Hôn môi, thể hiện sự thân mật, khởi đầu cho cảm xúc và khoái cảm."),
        new("kuudere", "Kuudere", "Kiểu nhân vật bề ngoài cực kỳ lạnh lùng để che giấu cảm xúc bên trong; chỉ mở lòng khi được chinh phục."),
        new("lactation", "Lactation", "Tiết sữa — nhân vật nữ ra sữa, thường gắn với ngực căng tức, bú mút hoặc vắt sữa."),
        new("lingerie", "Lingerie", "Nhân vật mặc đồ lót gợi cảm như áo ngực, quần lót ren, corset hoặc tất lưới. Tập trung vào yếu tố thị giác, quyến rũ và kích thích."),
        new("lolicon", "Lolicon", "Hentai chỉ là sản phẩm của trí tưởng tượng, không phản ánh cách con người thực sự cư xử, không phải t��i liệu hướng dẫn cuộc sống hay tình dục. Hãy xem hentai như xem phim viễn tưởng: xem nhưng đừng nhầm lẫn với thực tế."),
        new("maids", "Maids", "Khám phá truyện thuộc thể loại này."),
        new("males-only", "Males only", "Chỉ có các nhân vật nam xuất hiện, tạo bối cảnh đồng giới nam."),
        new("manhua", "Manhua", "Khám phá truyện thuộc thể loại này."),
        new("manhwa", "Manhwa", "Truyện tranh Hàn Quốc; nét vẽ mượt, hiện đại hoặc giả tưởng; hấp dẫn."),
        new("masturbation", "Masturbation", "Nhân vật tự kích thích bản thân bằng tay hoặc đồ chơi tình dục, thể hiện sự tò mò, cô đơn hoặc ham muốn dồn nén."),
        new("mature", "Mature", "Nhân vật trưởng thành, giàu kinh nghiệm, toát lên nét quyến rũ chín muồi."),
        new("mermaid", "Mermaid", "Nhân vật nữ người cá, kết hợp yếu tố giả tưởng dưới nước đầy mê hoặc."),
        new("miko", "Miko", "Vu nữ giữ đền với trang phục truyền thống, vẻ đẹp thanh khiết nhưng gợi cảm trong bối cảnh tâm linh Nhật Bản."),
        new("milf", "Milf", "Phụ nữ trưởng thành, quyến rũ, giàu kinh nghiệm (mẹ kế, cô giáo…)."),
        new("mind-break", "Mind Break", "Bị ép đến mức tâm trí vỡ vụn/tẩy não do khoái cảm hay áp lực tâm lý."),
        new("mind-control", "Mind Control", "Bị thao túng tâm trí bằng ma thuật/thuốc/công nghệ, dẫn đến hành vi ngoài ý muốn."),
        new("monster", "Monster", "Khám phá truyện thuộc thể loại này."),
        new("monstergirl", "Monstergirl", "Nhân vật nữ mang đặc điểm quái vật (sừng, đuôi…), vừa dị thường vừa quyến rũ."),
        new("mother", "Mother", "Khám phá truyện thuộc thể loại này."),
        new("nakadashi", "Nakadashi", "Khám phá truyện thuộc thể loại này."),
        new("netori", "Netori", "Khám phá truyện thuộc thể loại này."),
        new("ngot", "Ngọt", "Nội dung ngọt ngào, lãng mạn, nhẹ nhàng (sweet/romance)."),
        new("nipple-play", "Nipple Play", "Tập trung kích thích đầu ngực: mút, véo, dùng đồ chơi hoặc quan hệ trực tiếp với đầu ngực."),
        new("non-hen", "Non-hen", "Nội dung không tập trung vào cảnh tình dục, thiên về cốt truyện hoặc hài hước."),
        new("ntr", "NTR", "Nhân vật chính bị cắm sừng – người yêu/vợ bị người khác chiếm đoạt."),
        new("nun", "Nun", "Khám phá truyện thuộc thể loại này."),
        new("nurse", "Nurse", "Khám phá truyện thuộc thể loại này."),
        new("office-lady", "Office Lady", "Nhân vật nữ công sở mặc váy bút chì, áo sơ mi, vest – phong thái trưởng thành, quyến rũ nơi văn phòng."),
        new("old-man", "Old Man", "Nhân vật nam lớn tuổi xuất hiện trong các tình huống cấm kỵ hoặc gây tò mò."),
        new("oneshot", "Oneshot", "Truyện ngắn chỉ trong một chương, tập trung vào một tình huống tình dục cụ thể, không có cốt truyện dài dòng."),
        new("oral", "Oral", "Quan hệ bằng miệng, tập trung mô tả kỹ thuật và khoái cảm trực tiếp."),
        new("osananajimi", "Osananajimi", "Bạn thuở nhỏ gắn bó từ lâu, mối quan hệ chuyển dần sang lãng mạn hoặc tình dục."),
        new("paizuri", "Paizuri", "Quan hệ tình dục với ngực (ép dương vật giữa bầu ngực để kích thích và đạt cực khoái)."),
        new("pantyhose", "Pantyhose", "Nhân vật mặc quần tất mỏng, tôn đôi chân; thường rách/kéo xuống để tăng sự gợi cảm."),
        new("pegging", "Pegging", "Nữ dùng đồ chơi đeo để quan hệ qua hậu môn với nam, đảo ngược vai trò truyền thống."),
        new("piercing", "Piercing", "Khuyên trên cơ thể (môi, lưỡi, ngực…) tạo hình tượng nổi loạn và gợi cảm."),
        new("police", "Police", "Nhân vật cảnh sát đồng phục, mang hình ảnh quyền lực, kiểm soát; đôi khi kết hợp yếu tố hành động."),
        new("ponytail", "Ponytail", "Nhân vật buộc tóc đuôi ngựa, thể hiện sự năng động, khỏe khoắn và quyến rũ."),
        new("pregnant", "Pregnant", "Khám phá truyện thuộc thể loại này."),
        new("princess", "Princess", "Khám phá truyện thuộc thể loại này."),
        new("prostitution", "Prostitution", "Quan hệ tình dục để đổi lấy tiền: gái gọi, nhà thổ, bán dâm nghiệp dư."),
        new("rape", "Rape", "Khám phá truyện thuộc thể loại này."),
        new("rimjob", "Rimjob", "Kích thích hậu môn bằng miệng, trải nghiệm táo bạo và cảm giác lạ."),
        new("romance", "Romance", "Tình dục gắn liền với tình yêu, đồng thuận, nhiều cảm xúc, không bạo lực."),
        new("ryona", "Ryona", "Nhân vật chịu đau đớn thể xác kết hợp yếu tố tình dục gây sốc và căng thẳng."),
        new("scat", "Scat", "Nội dung liên quan đến phân – yếu tố cực đoan, gây tranh cãi và kén người xem."),
        new("school-uniform", "School uniform", "Khám phá truyện thuộc thể loại này."),
        new("schoolgirl", "SchoolGirl", "Nữ sinh 16–18 tuổi, đồng phục váy ngắn, bối cảnh trường/ký túc xá/thư viện."),
        new("see-through", "See-through", "Quần áo ướt, mỏng hoặc xuyên thấu làm lộ cơ thể bên trong."),
        new("series", "Series", "Truyện dài kỳ gồm nhiều chương, phát triển cốt truyện và nhân vật qua thời gian. Kết hợp yếu tố tình dục với mạch truyện hấp dẫn."),
        new("sex-toys", "Sex Toys", "Khám phá truyện thuộc thể loại này."),
        new("shimapan", "Shimapan", "Quần lót sọc dễ thương, thường dùng trong bối cảnh học đường hài hước hoặc gợi cảm."),
        new("short", "Short", "Truyện ngắn hoặc one-shot chỉ có một chương, tập trung vào một tình huống tình dục duy nhất, diễn biến nhanh và trực tiếp."),
        new("shota", "Shota", "Khám phá truyện thuộc thể loại này."),
        new("shoujo", "Shoujo", "Phong cách hướng tới nữ trẻ: cảm xúc lãng mạn, nhẹ nhàng, giàu biểu cảm."),
        new("siscon", "Siscon", "Khám phá truyện thuộc thể loại này."),
        new("sister", "Sister", "Khám phá truyện thuộc thể loại này."),
        new("sixty-nine", "Sixty-Nine", "Tư thế hai người đồng thời kích thích nhau bằng miệng, nhấn mạnh sự tương hỗ."),
        new("slave", "Slave", "Khám phá truyện thuộc thể loại này."),
        new("sleeping", "Sleeping", "Khám phá truyện thuộc thể loại này."),
        new("small-boobs", "Small Boobs", "Khám phá truyện thuộc thể loại này."),
        new("soft-incest", "Soft Incest", "Quan hệ/tình cảm họ hàng xa (anh họ–em họ, con riêng–mẹ kế…), không huyết thống trực tiếp."),
        new("son", "Son", "Khám phá truyện thuộc thể loại này."),
        new("spanking", "Spanking", "Đánh mông tạo cảm giác đau nhẹ xen khoái cảm và phục tùng."),
        new("sport", "Sport", "Khám phá truyện thuộc thể loại này."),
        new("squirting", "Squirting", "Nữ đạt cực khoái phun nước mạnh mẽ, nhấn mạnh sự mãnh liệt và chân thực."),
        new("stockings", "Stockings", "Khám phá truyện thuộc thể loại này."),
        new("strap-on", "Strap-on", "Dùng đồ chơi đeo (dương vật giả) để chủ động quan hệ với đối phương."),
        new("succubus", "Succubus", "Nhân vật là nữ quỷ dâm dục chuyên quyến rũ đàn ông để hút sinh lực, mang vẻ đẹp ma mị, chủ động và tràn đầy dục vọng."),
        new("supernatural", "Supernatural", "Kết hợp yếu tố siêu nhiên như ma quỷ, phép thuật, thần linh hoặc năng lực đặc biệt với tình dục, tạo không khí huyền bí và kích thích."),
        new("sweating", "Sweating", "Chảy mồ hôi nhiều; trong cảnh quan hệ nhấn mạnh cơ thể ướt át, hơi thở gấp và cao trào mãnh liệt."),
        new("swimsuit", "Swimsuit", "Đồ bơi (bikini/one-piece) ở bãi biển/hồ bơi; gợi cảm trẻ trung."),
        new("tail-plug", "Tail plug", "Đồ chơi hình đuôi thú gắn vào cơ thể, tạo nét hoang dã và gợi cảm."),
        new("tall-girl", "Tall Girl", "Gái cao ráo, chân dài; thường có chiều cao vượt nam chính, tạo cảm giác áp đảo và quyến rũ."),
        new("teacher", "Teacher", "Khám phá truyện thuộc thể loại này."),
        new("tentacles", "Tentacles", "Khám phá truyện thuộc thể loại này."),
        new("threesome", "Threesome", "Cảnh quan hệ giữa ba người (thường là hai nữ một nam hoặc ngược lại), nhấn mạnh cảm giác đa chiều, ghen tuông nhẹ và khoái cảm mạnh mẽ."),
        new("time-stop", "Time Stop", "Khám phá truyện thuộc thể loại này."),
        new("tomboy", "Tomboy", "Khám phá truyện thuộc thể loại này."),
        new("tracksuit", "Tracksuit", "Nhân vật mặc đồ thể thao bó sát, nhấn mạnh sự khỏe khoắn và đường cong."),
        new("transformation", "Transformation", "Nhân vật biến thân sang hình thể khác (ma thuật, công nghệ, lời nguyền...), mang đến trải nghiệm gợi cảm mới lạ."),
        new("trap", "Trap", "Nhân vật nam ăn mặc hoặc thể hiện như nữ; ngoại hình và/hoặc tính cách nữ tính tạo nên sự nhập nhằng giới tính gợi cảm."),
        new("truyen-viet", "Truyện Việt", "Khám phá truyện thuộc thể loại này."),
        new("tsundere", "Tsundere", "Ngoài lạnh trong ấm; tương phản tính cách khi vào cảnh thân mật."),
        new("tu-tien", "Tu Tiên", "Thể loại tu luyện/tiên hiệp: luyện công, thăng cấp, thế giới huyền huyễn."),
        new("twins", "Twins", "Khám phá truyện thuộc thể loại này."),
        new("twintails", "Twintails", "Tóc buộc hai bên trẻ trung, thường gắn với tính cách năng động hoặc tsundere nhẹ."),
        new("underwater", "Underwater", "Khám phá truyện thuộc thể loại này."),
        new("vampire", "Vampire", "Ma cà rồng quyến rũ, kết hợp yếu tố hút máu và mê hoặc dục tính."),
        new("vanilla", "Vanilla", "Cảnh sex nhẹ nhàng, đồng thuận; tập trung kết nối cảm xúc."),
        new("virgin", "Virgin", "Khám phá truyện thuộc thể loại này."),
        new("voyeurism", "Voyeurism", "Nhìn trộm hoặc quay lén cảnh riêng tư mà đối phương không hay biết."),
        new("vtuber", "Vtuber", "Nhân vật ảo do streamer điều khiển, xuất hiện trong nội dung gợi cảm hoặc tương tác độc đáo."),
        new("watersports", "Watersports", "Nội dung liên quan tới nước tiểu: tiểu tiện, bị tưới lên người hoặc uống."),
        new("webtoon", "Webtoon", "Khám phá truyện thuộc thể loại này."),
        new("wormhole", "Wormhole", "Quan hệ qua cổng không gian hoặc lối xuyên chiều, tăng tính kỳ ảo mới lạ."),
        new("x-ray", "X-ray", "Khám phá truyện thuộc thể loại này."),
        new("yandere", "Yandere", "Khám phá truyện thuộc thể loại này."),
        new("yaoi", "Yaoi", "Quan hệ giữa hai nam; có thể lãng mạn/đam mỹ, phổ biến trong cộng đồng BL."),
        new("yuri", "Yuri", "Quan hệ giữa hai nữ; nhấn mạnh sự lãng mạn/gợi cảm, nhẹ nhàng hoặc mãnh liệt."),
        new("zombie", "Zombie", "Xác sống xuất hiện trong bối cảnh kinh dị, xen yếu tố tình dục kỳ quái hoặc đen tối."),
    };


    public static bool IsVinaHentaiManga(Guid id) => MangaSlugMap.ContainsKey(id);
    public static bool IsVinaHentaiChapter(Guid chapId) => ChapterUrlMap.ContainsKey(chapId);
}
