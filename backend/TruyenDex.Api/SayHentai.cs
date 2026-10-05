using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;
using Meilisearch;

namespace TruyenDex.Api;

public class SayHentai(HttpClient http, IMemoryCache cache, IConnectionMultiplexer? redis = null, MeilisearchClient? meili = null)
{
    private const string BaseUrl = "https://sayhentai.cx";
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
        _ = CacheSetString($"sayhentai:slug:{id}", slug, TimeSpan.FromDays(30));
    }

    public void RegisterChapter(Guid chapId, string chapHref, Guid mangaId)
    {
        ChapterUrlMap[chapId] = chapHref;
        ChapterMangaMap[chapId] = mangaId;
        _ = CacheSetString($"sayhentai:chap:{chapId}", chapHref, TimeSpan.FromDays(30));
        _ = CacheSetString($"sayhentai:chap_manga:{chapId}", mangaId.ToString(), TimeSpan.FromDays(30));
    }

    public async Task<string?> ResolveSlug(Guid id)
    {
        if (MangaSlugMap.TryGetValue(id, out var slug) && !string.IsNullOrEmpty(slug)) return slug;
        slug = await CacheGetString($"sayhentai:slug:{id}");
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
        var mangaIdStr = await CacheGetString($"sayhentai:chap_manga:{chapterId}");
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
        url = await CacheGetString($"sayhentai:chap:{chapterId}");
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
        var cacheKey = $"sayhentai:html:{url}";
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
        var url = page > 1 ? $"{BaseUrl}/genre/manhwa?page={page}" : $"{BaseUrl}/genre/manhwa";
        var html = await FetchHtml(url);
        if (string.IsNullOrEmpty(html)) return [];

        return ParseCards(html);
    }

    private List<MangaCard> ParseCards(string html)
    {
        var items = new List<MangaCard>();
        var cardRegex = new Regex(@"<div class=""page-item-detail"">([\s\S]*?)</div>\s*</div>\s*</div>", RegexOptions.IgnoreCase);
        var matches = cardRegex.Matches(html);

        foreach (Match m in matches)
        {
            var cardContent = m.Groups[1].Value;

            var linkMatch = Regex.Match(cardContent, @"href=""https://sayhentai\.cx/(?:truyen-)?([^""/]+)\.html""", RegexOptions.IgnoreCase);
            if (!linkMatch.Success)
            {
                linkMatch = Regex.Match(cardContent, @"href=""/(?:truyen-)?([^""/]+)\.html""", RegexOptions.IgnoreCase);
            }
            if (!linkMatch.Success) continue;

            var slug = linkMatch.Groups[1].Value.Trim();
            if (string.IsNullOrEmpty(slug)) continue;

            // Cover
            var imgMatch = Regex.Match(cardContent, @"<img[^>]+src=""([^"">]+)""", RegexOptions.IgnoreCase);
            var coverUrl = imgMatch.Success ? imgMatch.Groups[1].Value : "";

            // Title
            var titleMatch = Regex.Match(cardContent, @"<h3[^>]*>[\s\S]*?<a[^>]*>([\s\S]*?)</a>", RegexOptions.IgnoreCase);
            if (!titleMatch.Success)
            {
                titleMatch = Regex.Match(cardContent, @"title=""([^""]+)""", RegexOptions.IgnoreCase);
            }
            var title = titleMatch.Success ? StripHtml(titleMatch.Groups[1].Value) : slug;

            // Chapter
            var chapMatch = Regex.Match(cardContent, @"<span class=""chapter[^""]*""[\s\S]*?<a[^>]*>([\s\S]*?)</a>", RegexOptions.IgnoreCase);
            var latestChapterTitle = chapMatch.Success ? StripHtml(chapMatch.Groups[1].Value) : "";

            // Updated time
            var timeMatch = Regex.Match(cardContent, @"<span class=""post-on[^""]*""[^>]*>([\s\S]*?)</span>", RegexOptions.IgnoreCase);
            var updatedAt = timeMatch.Success ? ParseTimeAgo(timeMatch.Groups[1].Value) : DateTime.UtcNow;

            var mangaId = CreateGuid("sayhentai:manga:" + slug);
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
                var chapSlug = chapNum > 0 ? $"chuong-{chapNum}" : "chuong-1";
                var chapId = CreateGuid("sayhentai:chap:" + slug + ":" + chapSlug);
                var chapHref = $"/truyen-{slug}/{chapSlug}";
                RegisterChapter(chapId, chapHref, mangaId);
                chapters.Add(new ChapterCard(chapId, mangaId, latestChapterTitle, chapNum, "vi", updatedAt, "SayHentai"));
            }

            var card = new MangaCard
            {
                Id = mangaId,
                Slug = slug,
                Title = title,
                AlternativeTitle = title,
                Author = "SayHentai",
                Cover = !string.IsNullOrEmpty(coverUrl) ? coverUrl : "/cover-placeholder.svg",
                Description = "",
                Status = "ongoing",
                Country = "ko",
                Demographic = "Manhwa",
                ContentRating = "erotica",
                Year = updatedAt.Year,
                Rating = 9.2,
                Follows = 150,
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

        var url = $"{BaseUrl}/truyen-{slug}.html";
        var html = await FetchHtml(url);
        if (string.IsNullOrEmpty(html))
        {
            // Try without 'truyen-'
            url = $"{BaseUrl}/{slug}.html";
            html = await FetchHtml(url);
            if (string.IsNullOrEmpty(html)) return null;
        }

        var titleMatch = Regex.Match(html, @"<h1[^>]*>([\s\S]*?)</h1>", RegexOptions.IgnoreCase);
        var title = titleMatch.Success ? StripHtml(titleMatch.Groups[1].Value) : slug;

        // Description
        var descMatch = Regex.Match(html, @"<div class=""[^""]*summary__content[^""]*""[\s\S]*?>([\s\S]*?)</div>", RegexOptions.IgnoreCase);
        if (!descMatch.Success)
        {
            descMatch = Regex.Match(html, @"<div class=""[^""]*description-summary[^""]*""[\s\S]*?>([\s\S]*?)</div>", RegexOptions.IgnoreCase);
        }
        var desc = descMatch.Success ? StripHtml(descMatch.Groups[1].Value) : "";

        // Author
        var authorMatch = Regex.Match(html, @"<div class=""author-content"">[\s\S]*?<a[^>]*>([^<]+)</a>", RegexOptions.IgnoreCase);
        if (!authorMatch.Success)
        {
            authorMatch = Regex.Match(html, @"Tác giả[\s\S]*?<div class=""summary-content"">([\s\S]*?)</div>", RegexOptions.IgnoreCase);
        }
        var author = authorMatch.Success ? StripHtml(authorMatch.Groups[1].Value) : "SayHentai";
        if (author == "..." || string.IsNullOrWhiteSpace(author)) author = "SayHentai";

        // Cover
        var coverMatch = Regex.Match(html, @"<div class=""summary_image"">[\s\S]*?<img[^>]+src=""([^"">]+)""", RegexOptions.IgnoreCase);
        if (!coverMatch.Success)
        {
            coverMatch = Regex.Match(html, @"property=""og:image""\s+content=""([^""]+)""", RegexOptions.IgnoreCase);
        }
        var coverUrl = coverMatch.Success ? coverMatch.Groups[1].Value : "/cover-placeholder.svg";

        // Status
        var statusMatch = Regex.Match(html, @"Tình trạng[\s\S]*?<div class=""summary-content"">([\s\S]*?)</div>", RegexOptions.IgnoreCase);
        var statusStr = statusMatch.Success ? StripHtml(statusMatch.Groups[1].Value) : "Đang tiến hành";
        var status = statusStr.Contains("hoàn thành", StringComparison.OrdinalIgnoreCase) ? "completed" : "ongoing";

        // Genres
        var genreMatches = Regex.Matches(html, @"<a[^>]*href=""https://sayhentai\.cx/genre/([^""/]+)""[^>]*>([^<]+)</a>", RegexOptions.IgnoreCase);
        var genres = genreMatches.Select(g => StripHtml(g.Groups[2].Value)).Distinct().ToArray();
        if (genres.Length == 0) genres = ["Manhwa", "Hentai", "Webtoon"];

        // Chapters
        var chapRegex = new Regex(@"<li[^>]*class=""[^""]*wp-manga-chapter[^""]*""[\s\S]*?<a\s+href=""([^""]+)""[^>]*>([\s\S]*?)</a>([\s\S]*?)</li>", RegexOptions.IgnoreCase);
        var chapMatches = chapRegex.Matches(html);
        var chapters = new List<ChapterCard>();

        foreach (Match cm in chapMatches)
        {
            var chapHref = cm.Groups[1].Value;
            var chapTitle = StripHtml(cm.Groups[2].Value);
            var rest = cm.Groups[3].Value;

            var timeMatch = Regex.Match(rest, @"<i>([^<]+)</i>", RegexOptions.IgnoreCase);
            if (!timeMatch.Success) timeMatch = Regex.Match(rest, @"class=""chapter-release-date""[^>]*>([\s\S]*?)</span>", RegexOptions.IgnoreCase);
            var publishedAt = timeMatch.Success ? ParseTimeAgo(timeMatch.Groups[1].Value) : DateTime.UtcNow;

            var numMatch = Regex.Match(chapTitle, @"(?:\b|[^\w\d])(?:chương|chapter|chap|ch|c)?[\s\._-]*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
            decimal num = 0;
            if (numMatch.Success)
            {
                decimal.TryParse(numMatch.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out num);
            }

            var chapSlugMatch = Regex.Match(chapHref, @"/(?:truyen-[^/]+/)?([^/]+)/?$", RegexOptions.IgnoreCase);
            var chapSlug = chapSlugMatch.Success ? chapSlugMatch.Groups[1].Value : (num > 0 ? $"chuong-{num}" : "chuong-1");

            var chapId = CreateGuid("sayhentai:chap:" + slug + ":" + chapSlug);
            RegisterChapter(chapId, chapHref, id);

            chapters.Add(new ChapterCard(chapId, id, chapTitle, num, "vi", publishedAt, "SayHentai"));
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
            Rating = 9.3,
            Follows = 280,
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
        var imgRegex = new Regex(@"<img[^>]+src=""([^"">]+)""[^>]*class=""[^""]*chapter-img[^""]*""", RegexOptions.IgnoreCase);
        var matches = imgRegex.Matches(html);
        var pages = new List<string>();

        foreach (Match im in matches)
        {
            var u = System.Net.WebUtility.HtmlDecode(im.Groups[1].Value).Trim();
            if (!string.IsNullOrEmpty(u) && !pages.Contains(u))
            {
                pages.Add(u);
            }
        }

        // Fallback if class regex didn't find any images
        if (pages.Count == 0)
        {
            var allImgMatches = Regex.Matches(html, @"https://(?:cdn|img)\.pubtranxzyzz\.store/hen/[^\s""'<>]+", RegexOptions.IgnoreCase);
            foreach (Match im in allImgMatches)
            {
                var u = System.Net.WebUtility.HtmlDecode(im.Value).Trim();
                if (!string.IsNullOrEmpty(u) && !pages.Contains(u))
                {
                    pages.Add(u);
                }
            }
        }

        // Title and Chapter Title
        var mangaTitle = "Manhwa";
        var chapterTitle = "Chương";

        var breadcrumbM = Regex.Match(html, @"<ol class=""breadcrumb"">([\s\S]*?)</ol>", RegexOptions.IgnoreCase);
        if (breadcrumbM.Success)
        {
            var bHtml = breadcrumbM.Groups[1].Value;
            var mangaLinkM = Regex.Match(bHtml, @"href=""[^""]*(?:truyen-)?[^""/]+\.html""[^>]*>([^<]+)</a>", RegexOptions.IgnoreCase);
            if (mangaLinkM.Success) mangaTitle = StripHtml(mangaLinkM.Groups[1].Value);

            var activeM = Regex.Match(bHtml, @"<li class=""active"">([^<]+)</li>", RegexOptions.IgnoreCase);
            if (activeM.Success) chapterTitle = StripHtml(activeM.Groups[1].Value);
        }

        if (chapterTitle == "Chương" || mangaTitle == "Manhwa")
        {
            var h1Match = Regex.Match(html, @"<h1[^>]*>([\s\S]*?)</h1>", RegexOptions.IgnoreCase);
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
        }

        if (chapterTitle == "Chương" || mangaTitle == "Manhwa")
        {
            var titleMatch = Regex.Match(html, @"<title>([\s\S]*?)</title>", RegexOptions.IgnoreCase);
            if (titleMatch.Success)
            {
                var t = StripHtml(titleMatch.Groups[1].Value).Replace("- SayHentai", "").Replace("| SayHentai", "").Trim();
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
            Author = "SayHentai",
            Demographic = "Manhwa",
            Country = "ko"
        };

        var currentChap = new ChapterCard(chapterId, mangaId, chapterTitle, num, "vi", DateTime.UtcNow, "SayHentai");

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
        var url = $"{BaseUrl}/?s={Uri.EscapeDataString(query.Trim())}&post_type=wp-manga";
        if (page > 1) url += $"&page={page}";
        var html = await FetchHtml(url);
        if (string.IsNullOrEmpty(html)) return [];

        return ParseCards(html);
    }

    public static bool IsSayHentaiManga(Guid id) => MangaSlugMap.ContainsKey(id);
    public static bool IsSayHentaiChapter(Guid chapId) => ChapterUrlMap.ContainsKey(chapId);
}
