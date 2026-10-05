using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;
using Meilisearch;

namespace TruyenDex.Api;

public class HentaiVn(HttpClient http, IMemoryCache cache, IConnectionMultiplexer? redis = null, MeilisearchClient? meili = null)
{
    private const string BaseUrl = "https://www.hentaivnx1.com";
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
        _ = CacheSetString($"hentaivn:slug:{id}", slug, TimeSpan.FromDays(30));
    }

    public void RegisterChapter(Guid chapId, string chapHref, Guid mangaId)
    {
        ChapterUrlMap[chapId] = chapHref;
        ChapterMangaMap[chapId] = mangaId;
        _ = CacheSetString($"hentaivn:chap:{chapId}", chapHref, TimeSpan.FromDays(30));
        _ = CacheSetString($"hentaivn:chap_manga:{chapId}", mangaId.ToString(), TimeSpan.FromDays(30));
    }

    public async Task<string?> ResolveSlug(Guid id)
    {
        if (MangaSlugMap.TryGetValue(id, out var slug) && !string.IsNullOrEmpty(slug)) return slug;
        slug = await CacheGetString($"hentaivn:slug:{id}");
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
        var mangaIdStr = await CacheGetString($"hentaivn:chap_manga:{chapterId}");
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
        url = await CacheGetString($"hentaivn:chap:{chapterId}");
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
        var cacheKey = $"hentaivn:html:{url}";
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

        if (DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var dt))
        {
            return dt.ToUniversalTime();
        }

        var lower = s.ToLowerInvariant();
        if (lower.Contains("vừa xong") || lower.Contains("vài giây")) return DateTime.UtcNow;

        var m = Regex.Match(lower, @"(\d+)\s*(p|phút|h|giờ|d|ngày|tháng|năm|tuần|s|giây)\s*(?:trc|trước)?");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var num))
        {
            var unit = m.Groups[2].Value;
            return unit switch
            {
                "s" or "giây" => DateTime.UtcNow.AddSeconds(-num),
                "p" or "phút" => DateTime.UtcNow.AddMinutes(-num),
                "h" or "giờ" => DateTime.UtcNow.AddHours(-num),
                "d" or "ngày" => DateTime.UtcNow.AddDays(-num),
                "tuần" => DateTime.UtcNow.AddDays(-num * 7),
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

    public async Task<List<MangaCard>> GetLatestManhwa(int page = 1)
    {
        var url = $"{BaseUrl}/tim-truyen/manhwa?page={page}";
        var html = await FetchHtml(url);
        if (string.IsNullOrEmpty(html)) return [];

        return ParseCards(html);
    }

    private List<MangaCard> ParseCards(string html)
    {
        var items = new List<MangaCard>();
        var itemRegex = new Regex(@"<div class=""item"">([\s\S]*?)</figcaption>", RegexOptions.IgnoreCase);
        var matches = itemRegex.Matches(html);

        foreach (Match m in matches)
        {
            var itemHtml = m.Groups[1].Value;

            var linkM = Regex.Match(itemHtml, @"href=""https://www\.hentaivnx1\.com/truyen-hentai/([^""/]+)""", RegexOptions.IgnoreCase);
            if (!linkM.Success)
            {
                linkM = Regex.Match(itemHtml, @"href=""/truyen-hentai/([^""/]+)""", RegexOptions.IgnoreCase);
            }
            if (!linkM.Success) continue;

            var slug = linkM.Groups[1].Value.Trim();
            if (string.IsNullOrEmpty(slug)) continue;

            // Title
            var titleM = Regex.Match(itemHtml, @"<a class=""jtip""[^>]*>([\s\S]*?)</a>", RegexOptions.IgnoreCase);
            if (!titleM.Success)
            {
                titleM = Regex.Match(itemHtml, @"<h3>[\s\S]*?<a[^>]*>([\s\S]*?)</a>", RegexOptions.IgnoreCase);
            }
            var title = titleM.Success ? StripHtml(titleM.Groups[1].Value) : slug;

            // Cover
            var imgM = Regex.Match(itemHtml, @"data-original=""([^"">]+)""", RegexOptions.IgnoreCase);
            if (!imgM.Success)
            {
                imgM = Regex.Match(itemHtml, @"src=""([^"">]+)""", RegexOptions.IgnoreCase);
            }
            var coverUrl = imgM.Success ? imgM.Groups[1].Value : "";

            // Chapter
            var chapM = Regex.Match(itemHtml, @"<li class=""chapter[^""]*""[\s\S]*?<a[^>]*href=""([^""]+)""[^>]*>([\s\S]*?)</a>[\s\S]*?<i class=""time"">([\s\S]*?)</i>", RegexOptions.IgnoreCase);
            var latestChapterTitle = chapM.Success ? StripHtml(chapM.Groups[2].Value) : "";
            var latestChapterHref = chapM.Success ? chapM.Groups[1].Value : "";
            var updatedAt = chapM.Success ? ParseTimeAgo(chapM.Groups[3].Value) : DateTime.UtcNow;

            var mangaId = CreateGuid("hentaivn:manga:" + slug);
            RegisterManga(mangaId, slug);

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
                var chapHref = !string.IsNullOrEmpty(latestChapterHref) ? latestChapterHref : $"/truyen-hentai/{slug}/chapter-{(chapNum > 0 ? chapNum : 1)}";
                var chapId = CreateGuid("hentaivn:chap:" + chapHref);
                RegisterChapter(chapId, chapHref, mangaId);
                chapters.Add(new ChapterCard(chapId, mangaId, latestChapterTitle, chapNum, "vi", updatedAt, "HentaiVN"));
            }

            var card = new MangaCard
            {
                Id = mangaId,
                Slug = slug,
                Title = title,
                AlternativeTitle = title,
                Author = "HentaiVN",
                Cover = !string.IsNullOrEmpty(coverUrl) ? coverUrl : "/cover-placeholder.svg",
                Description = "",
                Status = "ongoing",
                Country = "ko",
                Demographic = "Manhwa",
                ContentRating = "erotica",
                Year = updatedAt.Year,
                Rating = 9.4,
                Follows = 200,
                UpdatedAt = updatedAt,
                Chapters = chapters,
                TotalChapters = (int)Math.Max(1, Math.Round(chapNum)),
                Genres = ["Manhwa", "Hentai", "Webtoon", "18+"]
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

        var titleMatch = Regex.Match(html, @"<h1[^>]*class=""title-detail""[^>]*>([\s\S]*?)</h1>", RegexOptions.IgnoreCase);
        if (!titleMatch.Success) titleMatch = Regex.Match(html, @"<h1[^>]*>([\s\S]*?)</h1>", RegexOptions.IgnoreCase);
        var title = titleMatch.Success ? StripHtml(titleMatch.Groups[1].Value) : slug;

        // Description
        var descMatch = Regex.Match(html, @"<div class=""detail-content"">[\s\S]*?<p>([\s\S]*?)</p>", RegexOptions.IgnoreCase);
        if (!descMatch.Success) descMatch = Regex.Match(html, @"<div class=""detail-content"">([\s\S]*?)</div>", RegexOptions.IgnoreCase);
        var desc = descMatch.Success ? StripHtml(descMatch.Groups[1].Value) : "";

        // Author
        var authorMatch = Regex.Match(html, @"<li class=""author[^""]*""[\s\S]*?<p class=""col-xs-8"">([\s\S]*?)</p>", RegexOptions.IgnoreCase);
        var author = authorMatch.Success ? StripHtml(authorMatch.Groups[1].Value) : "HentaiVN";
        if (string.IsNullOrWhiteSpace(author) || author.Contains("Đang cập nhật", StringComparison.OrdinalIgnoreCase)) author = "HentaiVN";

        // Cover
        var coverMatch = Regex.Match(html, @"<div class=""col-xs-4 col-image"">[\s\S]*?<img[^>]+src=""([^"">]+)""", RegexOptions.IgnoreCase);
        if (!coverMatch.Success) coverMatch = Regex.Match(html, @"property=""og:image""\s+content=""([^""]+)""", RegexOptions.IgnoreCase);
        var coverUrl = coverMatch.Success ? coverMatch.Groups[1].Value : "/cover-placeholder.svg";

        // Status
        var statusMatch = Regex.Match(html, @"<li class=""status[^""]*""[\s\S]*?<p class=""col-xs-8"">([\s\S]*?)</p>", RegexOptions.IgnoreCase);
        var statusStr = statusMatch.Success ? StripHtml(statusMatch.Groups[1].Value) : "Đang tiến hành";
        var status = statusStr.Contains("hoàn thành", StringComparison.OrdinalIgnoreCase) ? "completed" : "ongoing";

        // Genres
        var genreSectionMatch = Regex.Match(html, @"<li class=""kind[^""]*""[\s\S]*?<p class=""col-xs-8"">([\s\S]*?)</p>", RegexOptions.IgnoreCase);
        var genres = new List<string>();
        if (genreSectionMatch.Success)
        {
            var gMatches = Regex.Matches(genreSectionMatch.Groups[1].Value, @"<a[^>]*>([^<]+)</a>", RegexOptions.IgnoreCase);
            genres = gMatches.Select(g => StripHtml(g.Groups[1].Value)).Distinct().ToList();
        }
        if (genres.Count == 0) genres = ["Manhwa", "Hentai", "Webtoon", "18+"];

        // Chapters
        var chapRowRegex = new Regex(@"<div class=""col-xs-5 chapter"">[\s\S]*?<a\s+href=""([^""]+)""[^>]*>([\s\S]*?)</a>[\s\S]*?<div class=""col-xs-4[^""]*"">([\s\S]*?)</div>", RegexOptions.IgnoreCase);
        var chapMatches = chapRowRegex.Matches(html);
        var chapters = new List<ChapterCard>();

        foreach (Match cm in chapMatches)
        {
            var chapHref = cm.Groups[1].Value.Trim();
            var chapTitle = StripHtml(cm.Groups[2].Value);
            var timeStr = StripHtml(cm.Groups[3].Value);
            var publishedAt = ParseTimeAgo(timeStr);

            var numMatch = Regex.Match(chapTitle, @"(?:\b|[^\w\d])(?:chương|chapter|chap|ch|c)?[\s\._-]*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
            decimal num = 0;
            if (numMatch.Success)
            {
                decimal.TryParse(numMatch.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out num);
            }

            var chapId = CreateGuid("hentaivn:chap:" + chapHref);
            RegisterChapter(chapId, chapHref, id);

            chapters.Add(new ChapterCard(chapId, id, chapTitle, num, "vi", publishedAt, "HentaiVN"));
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
            Country = "ko",
            Demographic = "Manhwa",
            ContentRating = "erotica",
            Year = DateTime.UtcNow.Year,
            Rating = 9.4,
            Follows = 300,
            UpdatedAt = cleanChaps.Count > 0 ? cleanChaps[0].PublishedAt : DateTime.UtcNow,
            Chapters = cleanChaps,
            TotalChapters = cleanChaps.Count > 0 ? (int)Math.Max(cleanChaps.Count, Math.Round(cleanChaps.Max(c => c.Number))) : 1,
            Genres = genres.ToArray()
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

        // Extract image URLs from cdn1 json or regex
        var pages = new List<string>();
        var cdnMatch = Regex.Match(html, @"var\s+cdn1\s*=\s*'(\[[^']+\])'", RegexOptions.IgnoreCase);
        if (!cdnMatch.Success) cdnMatch = Regex.Match(html, @"var\s+cdn1\s*=\s*(\[[^\]]+\])", RegexOptions.IgnoreCase);

        if (cdnMatch.Success)
        {
            try
            {
                var rawJson = cdnMatch.Groups[1].Value.Replace("\\'", "'");
                var arr = System.Text.Json.Nodes.JsonNode.Parse(rawJson)?.AsArray();
                if (arr != null)
                {
                    foreach (var x in arr)
                    {
                        var u = x?.ToString().Trim();
                        if (!string.IsNullOrEmpty(u) && !pages.Contains(u)) pages.Add(u);
                    }
                }
            }
            catch { }
        }

        if (pages.Count == 0)
        {
            var imgMatches = Regex.Matches(html, @"https:\\?/\\?/[a-zA-Z0-9_\-\.]+\.cfd\\?/[^\s""'<>]+?\.(?:jpg|webp|jpeg|png)", RegexOptions.IgnoreCase);
            foreach (Match im in imgMatches)
            {
                var u = im.Value.Replace("\\/", "/").Replace("\\", "").Trim();
                if (!string.IsNullOrEmpty(u) && !pages.Contains(u)) pages.Add(u);
            }
        }

        // Manga title and chapter title
        var mangaTitle = "Manhwa";
        var chapterTitle = "Chương";

        var h1Match = Regex.Match(html, @"<h1[^>]*class=['""]txt-primary['""][^>]*>([\s\S]*?)</h1>", RegexOptions.IgnoreCase);
        if (h1Match.Success)
        {
            var h1Text = StripHtml(h1Match.Groups[1].Value);
            if (h1Text.Contains(" - "))
            {
                var parts = h1Text.Split(" - ");
                if (parts.Length >= 2)
                {
                    mangaTitle = parts[0].Trim();
                    chapterTitle = parts[1].Trim();
                }
            }
            else
            {
                chapterTitle = h1Text;
            }
        }

        if (chapterTitle == "Chương" || mangaTitle == "Manhwa")
        {
            var titleMatch = Regex.Match(html, @"<title>([\s\S]*?)</title>", RegexOptions.IgnoreCase);
            if (titleMatch.Success)
            {
                var t = StripHtml(titleMatch.Groups[1].Value).Replace("- HentaiVn", "").Replace("| HentaiVn", "").Trim();
                if (t.Contains(" - "))
                {
                    var parts = t.Split(" - ");
                    if (parts.Length >= 2)
                    {
                        mangaTitle = parts[0].Trim();
                        chapterTitle = parts[1].Trim();
                    }
                }
                else
                {
                    chapterTitle = t;
                }
            }
        }

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
            Author = "HentaiVN",
            Demographic = "Manhwa",
            Country = "ko"
        };

        var currentChap = new ChapterCard(chapterId, mangaId, chapterTitle, num, "vi", DateTime.UtcNow, "HentaiVN");

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
        var url = $"{BaseUrl}/tim-truyen?keyword={Uri.EscapeDataString(query.Trim())}";
        if (page > 1) url += $"&page={page}";
        var html = await FetchHtml(url);
        if (string.IsNullOrEmpty(html)) return [];

        return ParseCards(html);
    }

    public static bool IsHentaiVnManga(Guid id) => MangaSlugMap.ContainsKey(id);
    public static bool IsHentaiVnChapter(Guid chapId) => ChapterUrlMap.ContainsKey(chapId);
}
