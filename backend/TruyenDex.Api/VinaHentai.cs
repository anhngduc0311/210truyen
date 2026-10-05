using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;
using Meilisearch;

namespace TruyenDex.Api;

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

    public static bool IsVinaHentaiManga(Guid id) => MangaSlugMap.ContainsKey(id);
    public static bool IsVinaHentaiChapter(Guid chapId) => ChapterUrlMap.ContainsKey(chapId);
}
