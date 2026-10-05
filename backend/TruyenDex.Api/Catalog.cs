using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;
using Meilisearch;

namespace TruyenDex.Api;

public record ChapterCard(Guid Id, Guid MangaId, string Title, decimal Number, string Language, DateTime PublishedAt, string Group = "");
public class MangaCard
{
    public Guid Id { get; set; }
    public string? Slug { get; set; }
    public string Title { get; set; } = "";
    public string AlternativeTitle { get; set; } = "";
    public string Author { get; set; } = "";
    public string Cover { get; set; } = "";
    public string Description { get; set; } = "";
    public string[] Genres { get; set; } = [];
    public string Status { get; set; } = "";
    public string Country { get; set; } = "";
    public string Demographic { get; set; } = "";
    public string ContentRating { get; set; } = "safe";
    public int? Year { get; set; }
    public double Rating { get; set; }
    public int Follows { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<ChapterCard> Chapters { get; set; } = [];
    public int TotalChapters { get; set; }
}
public record CatalogPage(List<MangaCard> Items, int Total, int Page, int PageSize);
public record ChapterPage(List<ChapterCard> Items, int Total, int Page, int PageSize);
public record ReaderData(ChapterCard Chapter, MangaCard Manga, string[] Pages, string[] DataSaverPages, string? ExternalUrl, List<ChapterCard> Navigation);
public class UpstreamException(string message, int status = 502) : Exception(message) { public int Status { get; } = status; }

// Read-only adapter. Integrates MangaDex/TruyenDex, TruyenGGVN, VinaHentai, SayHentai, and HentaiVN.
public class Catalog(HttpClient http, IMemoryCache cache, TruyenGg truyengg, VinaHentai vinahentai, SayHentai sayhentai, HentaiVn hentaivn, IConnectionMultiplexer? redis = null, MeilisearchClient? meili = null)
{
    private static readonly SemaphoreSlim Gate = new(6, 6);
    private static readonly string[] SiteOrigins = ["https://api.truyendex.cc", "https://api.truyendex.xyz"];
    private static readonly string[] Origins = ["https://api-proxy.truyendex.cc/mangadex", "https://api-proxy.truyendex.xyz/mangadex", "https://api.mangadex.org"];
    private static string S(JsonNode? n) => n?.ToString() ?? "";
    private static string E(string s) => Uri.EscapeDataString(s);
    private static DateTime Date(JsonNode? n) => DateTime.TryParse(S(n), out var d) ? d.ToUniversalTime() : DateTime.UtcNow;
    private static string Localized(JsonNode? n) => S(n?["vi"] ?? n?["en"] ?? (n as JsonObject)?.FirstOrDefault().Value);

    private async Task<T?> CacheGet<T>(string key) where T : class
    {
        if (redis != null && redis.IsConnected)
        {
            try {
                var v = await redis.GetDatabase().StringGetAsync(key);
                if (v.HasValue)
                    return typeof(T) == typeof(string) ? (T)(object)v.ToString() : JsonSerializer.Deserialize<T>(v.ToString());
            } catch { }
        }
        return cache.TryGetValue<T>(key, out var cached) ? cached : null;
    }

    private async Task CacheSet<T>(string key, T value, TimeSpan expiry) where T : class
    {
        if (redis != null && redis.IsConnected)
        {
            try {
                var json = typeof(T) == typeof(string) ? (string)(object)value : JsonSerializer.Serialize(value);
                await redis.GetDatabase().StringSetAsync(key, json, expiry);
            } catch { }
        }
        cache.Set(key, value, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = expiry, Size = 1 });
    }

    public async Task<JsonNode> Get(string path, bool site = false)
    {
        var key = $"upstream:{site}:{path}";
        var cached = await CacheGet<string>(key);
        if (cached != null) return JsonNode.Parse(cached)!;
        await Gate.WaitAsync();
        try
        {
            cached = await CacheGet<string>(key);
            if (cached != null) return JsonNode.Parse(cached)!;
            var origins = site ? SiteOrigins : Origins;
            foreach (var origin in origins)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    using var response = await http.GetAsync(origin + path, cts.Token);
                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                        throw new UpstreamException("Không tìm thấy truyện hoặc chương ở nguồn.", 404);
                    if (!response.IsSuccessStatusCode) continue;
                    var json = await response.Content.ReadAsStringAsync(cts.Token);
                    var node = JsonNode.Parse(json) ?? throw new JsonException();
                    var expiry = path.Contains("/homepage") ? TimeSpan.FromMinutes(15)
                        : path.Contains("/manga/tag") ? TimeSpan.FromHours(2)
                        : path.Contains("/aggregate") ? TimeSpan.FromMinutes(30)
                        : path.Contains("/statistics/") ? TimeSpan.FromMinutes(15)
                        : TimeSpan.FromMinutes(10);
                    await CacheSet(key, json, expiry);
                    return node;
                }
                catch (UpstreamException) { throw; }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException) { }
            }
            throw new UpstreamException("Nguồn truyện đang tạm thời không phản hồi. Vui lòng thử lại sau.");
        }
        finally { await Task.Delay(site ? 15 : 60); Gate.Release(); }
    }

    private MangaCard Map(JsonNode n, bool isThumbnail = true)
    {
        var a = n["attributes"]!;
        var rel = n["relationships"]!.AsArray();
        var id = Guid.Parse(S(n["id"]));

        var altTitlesList = a["altTitles"]?.AsArray()
            .SelectMany(x => (x as JsonObject)?.Select(kv => S(kv.Value)) ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct()
            .ToList() ?? [];

        var primaryTitle = Localized(a["title"]);
        var viTitle = a["altTitles"]?.AsArray().Select(x => x?["vi"]).FirstOrDefault(x => x != null);
        var displayTitle = viTitle != null ? S(viTitle) : (string.IsNullOrWhiteSpace(primaryTitle) ? altTitlesList.FirstOrDefault() ?? "" : primaryTitle);
        var altTitleDisplay = string.Join(" / ", new[] { primaryTitle }.Concat(altTitlesList).Where(s => !string.IsNullOrWhiteSpace(s) && s != displayTitle).Distinct());

        var file = S(rel.FirstOrDefault(x => S(x?["type"]) == "cover_art")?["attributes"]?["fileName"]);
        var sizeExt = isThumbnail ? ".256.jpg" : ".512.jpg";
        var coverUrl = $"https://mangadex.org/covers/{id}/{file}{sizeExt}";
        var lastChapStr = S(a["lastChapter"]);
        int totalChaps = 0;
        if (decimal.TryParse(lastChapStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsedChaps))
        {
            totalChaps = (int)Math.Round(parsedChaps);
        }

        return new MangaCard {
            Id = id,
            Title = displayTitle,
            AlternativeTitle = altTitleDisplay,
            Author = string.Join(" / ", rel.Where(x => S(x?["type"]) == "author").Select(x => S(x?["attributes"]?["name"]))),
            Cover = file.Length > 0 ? "https://services.f-ck.me/v1/image/" + Convert.ToBase64String(Encoding.UTF8.GetBytes(coverUrl)).Replace('+', '-').Replace('/', '_') : "/cover-placeholder.svg",
            Description = Localized(a["description"]),
            Status = S(a["status"]),
            Country = S(a["originalLanguage"]),
            Demographic = S(a["publicationDemographic"]),
            ContentRating = S(a["contentRating"]),
            Year = (int?)a["year"],
            Genres = a["tags"]!.AsArray().Select(x => Localized(x?["attributes"]?["name"])).ToArray(),
            UpdatedAt = Date(a["updatedAt"]),
            TotalChapters = totalChaps
        };
    }

    private async Task Stats(List<MangaCard> items)
    {
        if (items.Count == 0) return;
        try {
            var stats = await Get("/statistics/manga?" + string.Join("&", items.Select(x => "manga[]=" + x.Id)));
            foreach (var m in items) {
                var s = stats["statistics"]?[m.Id.ToString()];
                m.Rating = (double?)(s?["rating"]?["bayesian"]) ?? 0;
                m.Follows = (int?)s?["follows"] ?? 0;
            }
        } catch (UpstreamException) { /* Statistics must not block reading. */ }
    }

    public static bool IsManhwaOrManhua(MangaCard m)
    {
        if (m == null) return false;
        if (string.Equals(m.Country, "ko", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(m.Country, "zh", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(m.Country, "zh-hk", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(m.Country, "zh-ro", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (m.Genres != null && m.Genres.Length > 0)
        {
            foreach (var g in m.Genres)
            {
                if (string.IsNullOrWhiteSpace(g)) continue;
                var tag = g.Trim();
                if (tag.Equals("Manhwa", StringComparison.OrdinalIgnoreCase) ||
                    tag.Equals("Manhua", StringComparison.OrdinalIgnoreCase) ||
                    tag.Equals("Truyện Hàn Quốc", StringComparison.OrdinalIgnoreCase) ||
                    tag.Equals("Truyện Trung Quốc", StringComparison.OrdinalIgnoreCase) ||
                    tag.Equals("Truyen Han Quoc", StringComparison.OrdinalIgnoreCase) ||
                    tag.Equals("Truyen Trung Quoc", StringComparison.OrdinalIgnoreCase) ||
                    tag.Equals("Korean", StringComparison.OrdinalIgnoreCase) ||
                    tag.Equals("Chinese", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static readonly Regex Chapter1Regex = new(
        @"(?:^|[^\w\d])(?:chương|chapter|chap|ch|c)[\s\._-]*0*1(?:\.0+|\.5)?(?=[^\d]|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex OneshotRegex = new(
        @"(?:oneshot|one-shot|one\s+shot|truyện\s+ngắn)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool HasChapter1(MangaCard? m)
    {
        if (m == null || m.Chapters == null || m.Chapters.Count == 0) return false;
        foreach (var c in m.Chapters)
        {
            if (c == null) continue;
            if (c.Number >= 0.5m && c.Number <= 1.5m) return true;
            if (string.IsNullOrWhiteSpace(c.Title)) continue;
            var t = c.Title.Trim();
            if (t.Equals("Oneshot", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("One-shot", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("Chương 1", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("Chapter 1", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("Chap 1", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("C1", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (Chapter1Regex.IsMatch(t) || OneshotRegex.IsMatch(t))
            {
                var numMatch = Regex.Match(t, @"(?:chương|chapter|chap|ch|c)\s*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
                if (numMatch.Success && decimal.TryParse(numMatch.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var n))
                {
                    if (n >= 0.5m && n <= 1.5m) return true;
                    if (n > 1.5m) continue;
                }
                return true;
            }
        }
        return false;
    }

    public static bool ShouldPreferCandidate(MangaCard candidate, MangaCard existing)
    {
        bool candHasCh1 = HasChapter1(candidate);
        bool existHasCh1 = HasChapter1(existing);
        if (candHasCh1 && !existHasCh1) return true;
        if (!candHasCh1 && existHasCh1) return false;

        int candChaps = candidate.Chapters?.Count ?? 0;
        int existChaps = existing.Chapters?.Count ?? 0;
        if (candChaps > 0 && existChaps == 0) return true;
        if (existChaps > 0 && candChaps == 0) return false;

        if (candChaps > 0 && existChaps > 0)
        {
            var candMin = candidate.Chapters!.Min(c => c.Number > 0 ? c.Number : 99999m);
            var existMin = existing.Chapters!.Min(c => c.Number > 0 ? c.Number : 99999m);
            if (candMin < existMin) return true;
        }

        return false;
    }

    public static List<ChapterCard> DeduplicateChapters(IEnumerable<ChapterCard> chapters, bool ascending = false)
    {
        if (chapters == null) return [];

        var grouped = chapters
            .GroupBy(c => c.Number > 0 ? (object)c.Number : (object)c.Title.Trim().ToLowerInvariant())
            .Select(g => g
                .OrderByDescending(c => !string.IsNullOrWhiteSpace(c.Group))
                .ThenByDescending(c => c.Title.Contains(" · "))
                .ThenByDescending(c => c.PublishedAt)
                .First()
            );

        if (ascending)
        {
            return grouped.OrderBy(c => c.Number).ThenBy(c => c.PublishedAt).ToList();
        }
        else
        {
            return grouped.OrderByDescending(c => c.Number).ThenByDescending(c => c.PublishedAt).ToList();
        }
    }

    private async Task EnsureTopChapters(List<MangaCard> items, int targetCount = 3)
    {
        if (items.Count == 0) return;
        var missing = items.Where(m => m.Chapters == null || m.Chapters.Count < targetCount).ToList();
        if (missing.Count == 0) return;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var sem = new SemaphoreSlim(8);
        var tasks = missing.Select(async m =>
        {
            await sem.WaitAsync(cts.Token);
            try
            {
                var vSlug = await vinahentai.ResolveSlug(m.Id);
                if (!string.IsNullOrEmpty(vSlug))
                {
                    var chaps = await vinahentai.GetChapters(m.Id, 1, 10, ascending: false);
                    if (chaps != null && chaps.Items.Count > 0)
                    {
                        var existing = m.Chapters ?? [];
                        m.Chapters = DeduplicateChapters(existing.Concat(chaps.Items), ascending: false).Take(targetCount).ToList();
                        return;
                    }
                }

                var hSlug = await hentaivn.ResolveSlug(m.Id);
                if (!string.IsNullOrEmpty(hSlug))
                {
                    var chaps = await hentaivn.GetChapters(m.Id, 1, 10, ascending: false);
                    if (chaps != null && chaps.Items.Count > 0)
                    {
                        var existing = m.Chapters ?? [];
                        m.Chapters = DeduplicateChapters(existing.Concat(chaps.Items), ascending: false).Take(targetCount).ToList();
                        return;
                    }
                }

                var sSlug = await sayhentai.ResolveSlug(m.Id);
                if (!string.IsNullOrEmpty(sSlug))
                {
                    var chaps = await sayhentai.GetChapters(m.Id, 1, 10, ascending: false);
                    if (chaps != null && chaps.Items.Count > 0)
                    {
                        var existing = m.Chapters ?? [];
                        m.Chapters = DeduplicateChapters(existing.Concat(chaps.Items), ascending: false).Take(targetCount).ToList();
                        return;
                    }
                }

                var slug = await truyengg.ResolveSlug(m.Id);
                if (!string.IsNullOrEmpty(slug))
                {
                    var chaps = await truyengg.GetChapters(m.Id, 1, 10, ascending: false);
                    if (chaps != null && chaps.Items.Count > 0)
                    {
                        var existing = m.Chapters ?? [];
                        m.Chapters = DeduplicateChapters(existing.Concat(chaps.Items), ascending: false).Take(targetCount).ToList();
                        return;
                    }
                }
                else
                {
                    var chaps = await Chapters(m.Id, "vi", 1, ascending: false);
                    if (chaps != null && chaps.Items.Count > 0)
                    {
                        var existing = m.Chapters ?? [];
                        m.Chapters = DeduplicateChapters(existing.Concat(chaps.Items), ascending: false).Take(targetCount).ToList();
                    }
                    else
                    {
                        var enChaps = await Chapters(m.Id, "en", 1, ascending: false);
                        if (enChaps != null && enChaps.Items.Count > 0)
                        {
                            var existing = m.Chapters ?? [];
                            m.Chapters = DeduplicateChapters(existing.Concat(enChaps.Items), ascending: false).Take(targetCount).ToList();
                        }
                    }
                }
            }
            catch { }
            finally
            {
                sem.Release();
            }
        });

        try
        {
            await Task.WhenAll(tasks);
        }
        catch { }
    }

    public async Task<CatalogPage> Home(int page, int size)
    {
        var cacheKey = $"catalog:home:vinahentai:v1:{page}:{size}";
        var cachedPage = await CacheGet<CatalogPage>(cacheKey);
        if (cachedPage != null) return cachedPage;

        // 1. Fetch latest updated manga from VinaHentai
        var vinaItems = new List<MangaCard>();
        try
        {
            vinaItems = await vinahentai.GetLatest(page);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Catalog.Home] VinaHentai fetch warning: {ex.Message}");
        }

        if (vinaItems != null && vinaItems.Count > 0)
        {
            const int totalEstimated = 39680;
            var result = new CatalogPage(vinaItems, totalEstimated, page, vinaItems.Count);
            await CacheSet(cacheKey, result, TimeSpan.FromMinutes(10));
            return result;
        }

        // 2. Fallback to MangaDex / TruyenGG if VinaHentai is temporarily unreachable
        size = 28;
        var mangaDexItems = new List<MangaCard>();
        int total = 0;

        var ggTask = Task.Run(async () =>
        {
            try
            {
                var raw = await truyengg.GetLatest(page);
                return raw.Where(m => !IsManhwaOrManhua(m)).ToList();
            }
            catch { return new List<MangaCard>(); }
        });

        try
        {
            var home = await Get($"/api/series/homepage?page={page}&limit={size}", true);
            var rows = home["data"]?.AsArray();
            if (rows != null && rows.Count > 0)
            {
                var ids = rows.Select(x => S(x?["uuid"])).Where(x => !string.IsNullOrEmpty(x)).ToArray();
                var response = await Get("/manga?limit=100&includes[]=cover_art&includes[]=author&" + string.Join("&", ids.Select(x => "ids[]=" + x)));
                var map = response["data"]!.AsArray().Select(x => Map(x!, isThumbnail: true)).ToDictionary(x => x.Id.ToString());
                foreach (var row in rows) {
                    if (!map.TryGetValue(S(row?["uuid"]), out var m)) continue;
                    if (IsManhwaOrManhua(m)) continue;
                    m.UpdatedAt = Date(row?["last_chapter_updated_at"]);
                    var rawChaps = row?["chapters"]?.AsArray().Select(c => {
                        var chapTitle = S(c?["title"]);
                        var numMatch = Regex.Match(chapTitle, @"(?:\b|[^\w\d])(?:chương|chapter|chap|ch|c)?[\s\._-]*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
                        decimal num = 0;
                        if (numMatch.Success)
                        {
                            decimal.TryParse(numMatch.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out num);
                        }
                        return new ChapterCard(Guid.Parse(S(c?["uuid"])), m.Id, chapTitle, num, "vi", Date(c?["md_updated_at"]));
                    }).ToList() ?? [];
                    m.Chapters = DeduplicateChapters(rawChaps, ascending: false).Take(3).ToList();
                    mangaDexItems.Add(m);
                }
                await Stats(mangaDexItems);
                total = (int?)home["total"] ?? mangaDexItems.Count;
            }
        }
        catch (Exception)
        {
            try
            {
                var fallback = await Search(page, size, null, null, null, "ja", null, "vi", "latest", null);
                if (fallback.Items.Count > 0)
                {
                    mangaDexItems = fallback.Items.Where(m => !IsManhwaOrManhua(m)).ToList();
                    total = fallback.Total;
                }
            }
            catch { }
        }

        var ggItems = await ggTask;

        var merged = new List<MangaCard>();
        foreach (var m in mangaDexItems)
        {
            bool isDuplicate = false;
            for (int i = 0; i < merged.Count; i++)
            {
                var existing = merged[i];
                if (TruyenGg.IsSameManga(existing.Title, existing.AlternativeTitle, m.Title, m.AlternativeTitle))
                {
                    isDuplicate = true;
                    if (ShouldPreferCandidate(m, existing))
                    {
                        merged[i] = m;
                    }
                    break;
                }
            }
            if (!isDuplicate)
            {
                merged.Add(m);
            }
        }

        foreach (var gg in ggItems)
        {
            bool isDuplicate = false;
            for (int i = 0; i < merged.Count; i++)
            {
                var existing = merged[i];
                if (TruyenGg.IsSameManga(existing.Title, existing.AlternativeTitle, gg.Title, gg.AlternativeTitle))
                {
                    isDuplicate = true;
                    if (ShouldPreferCandidate(gg, existing))
                    {
                        merged[i] = gg;
                    }
                    break;
                }
            }
            if (!isDuplicate)
            {
                merged.Add(gg);
            }
        }

        merged = merged.Where(m => !IsManhwaOrManhua(m)).OrderByDescending(x => x.UpdatedAt).Take(size).ToList();
        await EnsureTopChapters(merged, 3);

        var fallbackResult = new CatalogPage(merged, total + ggItems.Count, page, size);
        await CacheSet(cacheKey, fallbackResult, TimeSpan.FromMinutes(15));
        return fallbackResult;
    }

    public async Task<CatalogPage> Featured(int size = 20)
    {
        var cacheKey = $"catalog:featured:hentaivn:v1:{size}";
        var cached = await CacheGet<CatalogPage>(cacheKey);
        if (cached != null) return cached;

        // 1. Fetch latest Manhwa from HentaiVN (https://www.hentaivnx1.com/tim-truyen/manhwa?page=1)
        List<MangaCard> hvnItems = [];
        try
        {
            hvnItems = await hentaivn.GetLatestManhwa(1);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Catalog.Featured] HentaiVN fetch warning: {ex.Message}");
        }

        if (hvnItems != null && hvnItems.Count > 0)
        {
            var finalItems = hvnItems.Take(size).ToList();
            await EnsureTopChapters(finalItems, 3);
            var res = new CatalogPage(finalItems, Math.Max(1000, finalItems.Count), 1, size);
            await CacheSet(cacheKey, res, TimeSpan.FromMinutes(10));
            return res;
        }

        // 2. Fallback to SayHentai & TruyenGG
        List<MangaCard> sayItems = [];
        try
        {
            sayItems = await sayhentai.GetLatestManhwa(1);
        }
        catch { }

        if (sayItems != null && sayItems.Count > 0)
        {
            var finalItems = sayItems.Take(size).ToList();
            await EnsureTopChapters(finalItems, 3);
            var res = new CatalogPage(finalItems, Math.Max(1000, finalItems.Count), 1, size);
            await CacheSet(cacheKey, res, TimeSpan.FromMinutes(10));
            return res;
        }

        // 2. Fallback to TruyenGG (Korean Manhwa & Chinese Manhua) & MangaDex
        List<MangaCard> ggItems = [];
        try
        {
            ggItems = await truyengg.GetLatestManhwaManhua(1);
        }
        catch { }

        List<MangaCard> mdItems = [];
        try
        {
            var path = $"/manga?limit={size}&includes[]=cover_art&includes[]=author&contentRating[]=safe&contentRating[]=suggestive&contentRating[]=erotica&order[latestUploadedChapter]=desc&originalLanguage[]=ko&originalLanguage[]=zh&availableTranslatedLanguage[]=vi";
            var result = await Get(path);
            var rawItems = result["data"]!.AsArray().Select(x => Map(x!, isThumbnail: true)).ToList();
            await Stats(rawItems);
            mdItems = rawItems;
        }
        catch { }

        var merged = new List<MangaCard>();
        foreach (var m in mdItems)
        {
            if (!IsManhwaOrManhua(m)) continue;
            bool isDuplicate = false;
            for (int i = 0; i < merged.Count; i++)
            {
                var existing = merged[i];
                if (TruyenGg.IsSameManga(existing.Title, existing.AlternativeTitle, m.Title, m.AlternativeTitle))
                {
                    isDuplicate = true;
                    if (ShouldPreferCandidate(m, existing))
                    {
                        merged[i] = m;
                    }
                    break;
                }
            }
            if (!isDuplicate)
            {
                merged.Add(m);
            }
        }

        foreach (var gg in ggItems)
        {
            if (!IsManhwaOrManhua(gg)) continue;
            bool isDuplicate = false;
            for (int i = 0; i < merged.Count; i++)
            {
                var existing = merged[i];
                if (TruyenGg.IsSameManga(existing.Title, existing.AlternativeTitle, gg.Title, gg.AlternativeTitle))
                {
                    isDuplicate = true;
                    if (ShouldPreferCandidate(gg, existing))
                    {
                        merged[i] = gg;
                    }
                    break;
                }
            }
            if (!isDuplicate)
            {
                merged.Add(gg);
            }
        }

        var fallbackItems = merged.OrderByDescending(x => x.UpdatedAt).Take(size).ToList();
        await EnsureTopChapters(fallbackItems, 3);
        var fallbackRes = new CatalogPage(fallbackItems, fallbackItems.Count, 1, size);
        await CacheSet(cacheKey, fallbackRes, TimeSpan.FromMinutes(5));
        return fallbackRes;
    }

    private static int GetRelevanceScore(MangaCard m, string q)
    {
        var qNorm = TruyenGg.NormalizeTitle(q);
        if (string.IsNullOrEmpty(qNorm)) return 10;

        var tNorm = TruyenGg.NormalizeTitle(m.Title);
        var altNorm = TruyenGg.NormalizeTitle(m.AlternativeTitle);

        if (tNorm == qNorm) return 0;
        if (altNorm == qNorm) return 1;
        if (tNorm.StartsWith(qNorm) || qNorm.StartsWith(tNorm)) return 2;
        if (altNorm.StartsWith(qNorm) || qNorm.StartsWith(altNorm)) return 3;
        if (tNorm.Contains(qNorm)) return 4;
        if (altNorm.Contains(qNorm)) return 5;

        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 1 && words.All(w => (m.Title + " " + m.AlternativeTitle).Contains(w, StringComparison.OrdinalIgnoreCase)))
            return 6;

        return 10;
    }

    public async Task<CatalogPage> Search(int page, int size, string? q, string? genre, string? status, string? country, string? demographic, string? language, string? sort, int? year)
    {
        var cacheKey = $"catalog:search:v12:{page}:{size}:{q}:{genre}:{status}:{country}:{demographic}:{language}:{sort}:{year}";
        var cachedSearch = await CacheGet<CatalogPage>(cacheKey);
        if (cachedSearch != null)
        {
            foreach (var item in cachedSearch.Items)
            {
                if (!string.IsNullOrEmpty(item.Slug))
                {
                    truyengg.RegisterManga(item.Id, item.Slug);
                }
            }
            return cachedSearch;
        }

        var order = sort switch { "rating" => "rating", "hot" => "followedCount", "title" => "title", "new" => "createdAt", "chapters" => "followedCount", _ => "latestUploadedChapter" };
        var fetchLimit = sort == "chapters" ? Math.Clamp(size * 2, 24, 60) : size;
        var path = $"/manga?limit={fetchLimit}&offset={(page - 1) * size}&includes[]=cover_art&includes[]=author&contentRating[]=safe&contentRating[]=suggestive&contentRating[]=erotica&order[{order}]={(order == "title" ? "asc" : "desc")}";
        if (!string.IsNullOrWhiteSpace(q)) path += "&title=" + E(q.Trim());
        if (Guid.TryParse(genre, out var tag)) path += "&includedTags[]=" + tag;
        if (new[] { "ongoing", "completed", "hiatus", "cancelled" }.Contains(status)) path += "&status[]=" + status;
        if (!string.IsNullOrWhiteSpace(country))
        {
            var countries = country.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var c in countries)
            {
                if (new[] { "ja", "ko", "zh", "en" }.Contains(c))
                    path += "&originalLanguage[]=" + c;
                else if (c.Equals("manhwa", StringComparison.OrdinalIgnoreCase))
                    path += "&originalLanguage[]=ko";
                else if (c.Equals("manhua", StringComparison.OrdinalIgnoreCase))
                    path += "&originalLanguage[]=zh";
            }
        }
        if (new[] { "shounen", "shoujo", "josei", "seinen" }.Contains(demographic)) path += "&publicationDemographic[]=" + demographic;
        path += "&availableTranslatedLanguage[]=" + (language == "en" ? "en" : "vi");
        if (year is >= 1900 and <= 2100) path += "&year=" + year;

        var items = new List<MangaCard>();
        int total = 0;

        bool isKorea = !string.IsNullOrWhiteSpace(country) && (country.Contains("ko", StringComparison.OrdinalIgnoreCase) || country.Contains("manhwa", StringComparison.OrdinalIgnoreCase));
        bool isJapan = !string.IsNullOrWhiteSpace(country) && (country.Contains("ja", StringComparison.OrdinalIgnoreCase) || country.Contains("manga", StringComparison.OrdinalIgnoreCase));
        bool isChina = !string.IsNullOrWhiteSpace(country) && (country.Contains("zh", StringComparison.OrdinalIgnoreCase) || country.Contains("manhua", StringComparison.OrdinalIgnoreCase));
        bool noCountryFilter = string.IsNullOrWhiteSpace(country);

        // Run MangaDex search, SayHentai, VinaHentai, and TruyenGG in parallel
        var mdTask = Task.Run(async () =>
        {
            try
            {
                var result = await Get(path);
                var rawItems = new List<MangaCard>();
                if (result["data"]?.AsArray() is { } dataArr)
                {
                    foreach (var x in dataArr)
                    {
                        if (x == null) continue;
                        try { rawItems.Add(Map(x, isThumbnail: true)); } catch { }
                    }
                }
                var statsTask = Stats(rawItems);
                var chapsTask = PopulateMangaDexChapters(rawItems, language);
                try { await Task.WhenAll(statsTask, chapsTask); } catch { }
                var mdTotal = Math.Min((int?)result["total"] ?? 0, 10000);
                return (rawItems, mdTotal);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Catalog.Search] MangaDex upstream warning: {ex.Message}");
                if (ex is UpstreamException && string.IsNullOrWhiteSpace(q))
                {
                    throw;
                }
                return (new List<MangaCard>(), 0);
            }
        });

        var vinaTask = !string.IsNullOrWhiteSpace(q)
            ? vinahentai.Search(q, page)
            : (isJapan || (noCountryFilter && string.IsNullOrEmpty(genre)) ? vinahentai.GetLatest(page) : Task.FromResult(new List<MangaCard>()));

        var hvnTask = !string.IsNullOrWhiteSpace(q)
            ? hentaivn.Search(q, page)
            : (isKorea || (noCountryFilter && string.IsNullOrEmpty(genre)) ? hentaivn.GetLatestManhwa(page) : Task.FromResult(new List<MangaCard>()));

        var sayTask = !string.IsNullOrWhiteSpace(q)
            ? sayhentai.Search(q, page)
            : (isKorea || (noCountryFilter && string.IsNullOrEmpty(genre)) ? sayhentai.GetLatestManhwa(page) : Task.FromResult(new List<MangaCard>()));

        var ggTask = !string.IsNullOrWhiteSpace(q)
            ? truyengg.Search(q, page)
            : (isKorea || isChina ? truyengg.GetLatestManhwaManhua(page) : Task.FromResult(new List<MangaCard>()));

        await Task.WhenAll(mdTask, ggTask, vinaTask, hvnTask, sayTask);

        var (rawItems, mdTotal) = await mdTask;
        var ggResults = await ggTask;
        var vinaResults = await vinaTask;
        var hvnResults = await hvnTask;
        var sayResults = await sayTask;
        total = mdTotal;

        if (isKorea && (hvnResults.Count > 0 || sayResults.Count > 0))
        {
            total = Math.Max(total, 1200);
        }
        else if (isJapan && vinaResults.Count > 0)
        {
            total = Math.Max(total, 39680);
        }

        // 1. If Korea filter, HentaiVN & SayHentai are prioritized
        if (isKorea)
        {
            foreach (var h in hvnResults) items.Add(h);
            foreach (var s in sayResults)
            {
                if (!items.Any(existing => TruyenGg.IsSameManga(existing.Title, existing.AlternativeTitle, s.Title, s.AlternativeTitle)))
                    items.Add(s);
            }
            foreach (var gg in ggResults)
            {
                if (!items.Any(existing => TruyenGg.IsSameManga(existing.Title, existing.AlternativeTitle, gg.Title, gg.AlternativeTitle)))
                    items.Add(gg);
            }
        }
        else
        {
            foreach (var v in vinaResults) items.Add(v);
            foreach (var h in hvnResults) items.Add(h);
            foreach (var s in sayResults) items.Add(s);
            foreach (var gg in ggResults) items.Add(gg);
        }

        // 2. Add MangaDex results, merging/deduplicating against existing results
        foreach (var m in rawItems)
        {
            bool isDuplicate = false;
            for (int i = 0; i < items.Count; i++)
            {
                var existing = items[i];
                if (TruyenGg.IsSameManga(existing.Title, existing.AlternativeTitle, m.Title, m.AlternativeTitle))
                {
                    isDuplicate = true;
                    if (ShouldPreferCandidate(m, existing))
                    {
                        items[i] = m;
                    }
                    break;
                }
            }
            if (!isDuplicate)
            {
                items.Add(m);
            }
        }

        // Fallback: If both external sources returned 0 items and meili is available, search local meili index
        if (items.Count == 0 && !string.IsNullOrWhiteSpace(q) && meili != null)
        {
            try
            {
                var index = meili.Index("mangas");
                var meiliHits = await index.SearchAsync<MangaCard>(q.Trim(), new SearchQuery { Limit = size, Offset = (page - 1) * size });
                if (meiliHits?.Hits?.Any() == true)
                {
                    items.AddRange(meiliHits.Hits);
                    total = (meiliHits as SearchResult<MangaCard>)?.EstimatedTotalHits ?? meiliHits.Hits.Count;
                }
            }
            catch { }
        }

        // Sort items by chapter count or relevance to search query
        if (sort == "chapters" && items.Count > 1)
        {
            items = items.OrderByDescending(m => GetChapterCount(m)).ToList();
        }
        else if (!string.IsNullOrWhiteSpace(q) && items.Count > 1)
        {
            items = items.OrderBy(m => GetRelevanceScore(m, q.Trim())).ToList();
        }

        if (items.Count > size)
        {
            items = items.Take(size).ToList();
        }

        if (total < items.Count) total = items.Count;

        if (items.Count > 0)
        {
            _ = Task.Run(async () => {
                try { await EnsureTopChapters(items.Take(8).ToList(), 3); } catch { }
            });
            var res = new CatalogPage(items, total, page, size);
            await CacheSet(cacheKey, res, TimeSpan.FromMinutes(5));

            if (meili != null)
            {
                _ = Task.Run(async () => {
                    try {
                        var index = meili.Index("mangas");
                        await index.AddDocumentsAsync(items);
                    } catch { }
                });
            }
            return res;
        }

        return new CatalogPage(items, total, page, size);
    }

    private async Task PopulateMangaDexChapters(List<MangaCard> items, string? language)
    {
        if (items.Count == 0) return;
        try
        {
            var targetLang = language == "en" ? "en" : "vi";
            var ids = items.Select(x => x.Id.ToString()).Distinct().ToList();
            var chapPath = $"/chapter?limit=100&translatedLanguage[]={targetLang}&order[readableAt]=desc&" + string.Join("&", ids.Select(id => "manga[]=" + id));
            var res = await Get(chapPath);
            var data = res["data"]?.AsArray();
            if (data != null && data.Count > 0)
            {
                var mapped = new List<ChapterCard>();
                foreach (var c in data)
                {
                    try
                    {
                        mapped.Add(MapChapter(c!));
                    }
                    catch { }
                }

                var chapGroups = mapped
                    .GroupBy(c => c.MangaId)
                    .ToDictionary(g => g.Key, g => DeduplicateChapters(g.ToList(), ascending: false).Take(3).ToList());

                foreach (var m in items)
                {
                    if (chapGroups.TryGetValue(m.Id, out var chaps))
                    {
                        m.Chapters = chaps;
                        if (chaps.Count > 0)
                        {
                            var maxChap = (int)Math.Round(chaps.Max(c => c.Number));
                            if (maxChap > m.TotalChapters) m.TotalChapters = maxChap;
                        }
                    }
                }
            }
        }
        catch { }
    }

    public static int GetChapterCount(MangaCard m)
    {
        var count = m.TotalChapters;
        if (m.Chapters != null && m.Chapters.Count > 0)
        {
            var maxNum = (int)Math.Round(m.Chapters.Max(c => c.Number));
            if (maxNum > count) count = maxNum;
            if (m.Chapters.Count > count) count = m.Chapters.Count;
        }
        return count;
    }

    public async Task<MangaCard> Detail(Guid id)
    {
        var cacheKey = $"catalog:detail:v8:{id}";
        var cachedManga = await CacheGet<MangaCard>(cacheKey);
        if (cachedManga != null) return cachedManga;

        var vinaManga = await vinahentai.GetDetail(id);
        if (vinaManga != null)
        {
            await CacheSet(cacheKey, vinaManga, TimeSpan.FromMinutes(20));
            return vinaManga;
        }

        var hvnManga = await hentaivn.GetDetail(id);
        if (hvnManga != null)
        {
            await CacheSet(cacheKey, hvnManga, TimeSpan.FromMinutes(20));
            return hvnManga;
        }

        var sayManga = await sayhentai.GetDetail(id);
        if (sayManga != null)
        {
            await CacheSet(cacheKey, sayManga, TimeSpan.FromMinutes(20));
            return sayManga;
        }

        var ggManga = await truyengg.GetDetail(id);
        if (ggManga != null)
        {
            await CacheSet(cacheKey, ggManga, TimeSpan.FromMinutes(20));
            return ggManga;
        }

        var data = await Get($"/manga/{id}?includes[]=cover_art&includes[]=author");
        var m = Map(data["data"]!, isThumbnail: false);
        await Stats([m]);
        await CacheSet(cacheKey, m, TimeSpan.FromMinutes(20));
        return m;
    }

    private ChapterCard MapChapter(JsonNode c)
    {
        var a = c["attributes"]!;
        var rel = c["relationships"]?.AsArray() ?? [];
        decimal.TryParse(S(a["chapter"]), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var num);
        var chapter = S(a["chapter"]);
        var title = S(a["title"]);
        var mangaRel = rel.FirstOrDefault(x => S(x?["type"]) == "manga");
        var mangaId = mangaRel != null ? Guid.Parse(S(mangaRel["id"])) : Guid.Empty;
        return new(Guid.Parse(S(c["id"])), mangaId,
            (chapter.Length > 0 ? "Chương " + chapter : "Oneshot") + (title.Length > 0 ? " · " + title : ""), num,
            S(a["translatedLanguage"]), Date(a["publishAt"]), S(rel.FirstOrDefault(x => S(x?["type"]) == "scanlation_group")?["attributes"]?["name"]));
    }

    public async Task<List<ChapterCard>> GetAllChapters(Guid id, string language)
    {
        var cacheKey = $"catalog:all_chapters:v8:{id}:{language}";
        var cached = await CacheGet<List<ChapterCard>>(cacheKey);
        if (cached != null && cached.Count > 0) return cached;

        var vinaChaps = await vinahentai.GetChapters(id, 1, 1000, ascending: true);
        if (vinaChaps != null && vinaChaps.Items.Count > 0)
        {
            var cleanVina = DeduplicateChapters(vinaChaps.Items, ascending: true);
            await CacheSet(cacheKey, cleanVina, TimeSpan.FromMinutes(20));
            return cleanVina;
        }

        var hvnChaps = await hentaivn.GetChapters(id, 1, 1000, ascending: true);
        if (hvnChaps != null && hvnChaps.Items.Count > 0)
        {
            var cleanHvn = DeduplicateChapters(hvnChaps.Items, ascending: true);
            await CacheSet(cacheKey, cleanHvn, TimeSpan.FromMinutes(20));
            return cleanHvn;
        }

        var sayChaps = await sayhentai.GetChapters(id, 1, 1000, ascending: true);
        if (sayChaps != null && sayChaps.Items.Count > 0)
        {
            var cleanSay = DeduplicateChapters(sayChaps.Items, ascending: true);
            await CacheSet(cacheKey, cleanSay, TimeSpan.FromMinutes(20));
            return cleanSay;
        }

        var ggChaps = await truyengg.GetChapters(id, 1, 1000, ascending: true);
        if (ggChaps != null && ggChaps.Items.Count > 0)
        {
            var cleanGg = DeduplicateChapters(ggChaps.Items, ascending: true);
            await CacheSet(cacheKey, cleanGg, TimeSpan.FromMinutes(20));
            return cleanGg;
        }

        var mdChapters = new List<ChapterCard>();
        try
        {
            var r = await Get($"/manga/{id}/feed?limit=500&offset=0&translatedLanguage[]={E(language == "en" ? "en" : "vi")}&includes[]=scanlation_group&order[chapter]=asc&includeExternalUrl=0");
            if (r["data"]?.AsArray() is JsonArray arr)
            {
                mdChapters.AddRange(arr.Select(x => MapChapter(x!)));
            }
        }
        catch { }

        // Check if MangaDex is missing chapters (e.g. no chapters, starts at chapter > 1 like Mayonaka Heart Tune starting at 71, or large gaps)
        bool needsMoreChapters = false;
        if (mdChapters.Count == 0)
        {
            needsMoreChapters = true;
        }
        else
        {
            var validNums = mdChapters.Where(c => c.Number > 0).Select(c => c.Number).OrderBy(n => n).ToList();
            if (validNums.Count == 0 || validNums[0] > 1.5m || mdChapters.Count < 25)
            {
                needsMoreChapters = true;
            }
            else
            {
                for (int i = 0; i < validNums.Count - 1; i++)
                {
                    if (validNums[i + 1] - validNums[i] > 10m)
                    {
                        needsMoreChapters = true;
                        break;
                    }
                }
            }
        }

        if ((language == "vi" || string.IsNullOrEmpty(language)) && needsMoreChapters)
        {
            try
            {
                var manga = await Detail(id);
                if (manga != null)
                {
                    var extraChaps = await truyengg.FindMatchingChapters(id, manga.Title, manga.AlternativeTitle);
                    if (extraChaps != null && extraChaps.Count > 0)
                    {
                        mdChapters.AddRange(extraChaps);
                    }
                }
            }
            catch { }
        }

        var deduplicated = DeduplicateChapters(mdChapters, ascending: true);
        if (deduplicated.Count > 0)
        {
            await CacheSet(cacheKey, deduplicated, TimeSpan.FromMinutes(20));
        }
        return deduplicated;
    }

    public async Task<ChapterPage> Chapters(Guid id, string language, int page, bool ascending = false)
    {
        var allChapters = await GetAllChapters(id, language);
        var ordered = ascending
            ? allChapters.OrderBy(c => c.Number).ThenBy(c => c.PublishedAt).ToList()
            : allChapters.OrderByDescending(c => c.Number).ThenByDescending(c => c.PublishedAt).ToList();

        const int size = 100;
        var total = ordered.Count;
        var pagedItems = ordered.Skip((page - 1) * size).Take(size).ToList();
        return new ChapterPage(pagedItems, total, page, size);
    }

    public async Task<ReaderData> Read(Guid id)
    {
        var cacheKey = $"catalog:reader:v8:{id}";
        var cachedReader = await CacheGet<ReaderData>(cacheKey);
        if (cachedReader != null) return cachedReader;

        var vinaReader = await vinahentai.GetReader(id);
        if (vinaReader != null)
        {
            var mangaId = vinaReader.Chapter.MangaId;
            var manga = mangaId != Guid.Empty ? await Detail(mangaId) : null;
            var nav = mangaId != Guid.Empty ? await GetAllChapters(mangaId, "vi") : null;
            if (nav == null || nav.Count == 0)
            {
                nav = vinaReader.Navigation;
            }
            if (!nav.Any(x => x.Id == id))
            {
                nav.Add(vinaReader.Chapter);
                nav = DeduplicateChapters(nav, ascending: true);
            }

            var finalReader = new ReaderData(
                vinaReader.Chapter,
                manga ?? vinaReader.Manga,
                vinaReader.Pages,
                vinaReader.DataSaverPages,
                vinaReader.ExternalUrl,
                nav
            );
            await CacheSet(cacheKey, finalReader, TimeSpan.FromMinutes(30));
            return finalReader;
        }

        var hvnReader = await hentaivn.GetReader(id);
        if (hvnReader != null)
        {
            var mangaId = hvnReader.Chapter.MangaId;
            var manga = mangaId != Guid.Empty ? await Detail(mangaId) : null;
            var nav = mangaId != Guid.Empty ? await GetAllChapters(mangaId, "vi") : null;
            if (nav == null || nav.Count == 0)
            {
                nav = hvnReader.Navigation;
            }
            if (!nav.Any(x => x.Id == id))
            {
                nav.Add(hvnReader.Chapter);
                nav = DeduplicateChapters(nav, ascending: true);
            }

            var finalReader = new ReaderData(
                hvnReader.Chapter,
                manga ?? hvnReader.Manga,
                hvnReader.Pages,
                hvnReader.DataSaverPages,
                hvnReader.ExternalUrl,
                nav
            );
            await CacheSet(cacheKey, finalReader, TimeSpan.FromMinutes(30));
            return finalReader;
        }

        var sayReader = await sayhentai.GetReader(id);
        if (sayReader != null)
        {
            var mangaId = sayReader.Chapter.MangaId;
            var manga = mangaId != Guid.Empty ? await Detail(mangaId) : null;
            var nav = mangaId != Guid.Empty ? await GetAllChapters(mangaId, "vi") : null;
            if (nav == null || nav.Count == 0)
            {
                nav = sayReader.Navigation;
            }
            if (!nav.Any(x => x.Id == id))
            {
                nav.Add(sayReader.Chapter);
                nav = DeduplicateChapters(nav, ascending: true);
            }

            var finalReader = new ReaderData(
                sayReader.Chapter,
                manga ?? sayReader.Manga,
                sayReader.Pages,
                sayReader.DataSaverPages,
                sayReader.ExternalUrl,
                nav
            );
            await CacheSet(cacheKey, finalReader, TimeSpan.FromMinutes(30));
            return finalReader;
        }

        var ggReader = await truyengg.GetReader(id);
        if (ggReader != null)
        {
            var mangaId = ggReader.Chapter.MangaId;
            var manga = mangaId != Guid.Empty ? await Detail(mangaId) : null;
            var nav = mangaId != Guid.Empty ? await GetAllChapters(mangaId, "vi") : null;
            if (nav == null || nav.Count == 0)
            {
                nav = ggReader.Navigation;
            }
            if (!nav.Any(x => x.Id == id))
            {
                nav.Add(ggReader.Chapter);
                nav = DeduplicateChapters(nav, ascending: true);
            }

            var finalReader = new ReaderData(
                ggReader.Chapter,
                manga ?? ggReader.Manga,
                ggReader.Pages,
                ggReader.DataSaverPages,
                ggReader.ExternalUrl,
                nav
            );
            await CacheSet(cacheKey, finalReader, TimeSpan.FromMinutes(15));
            return finalReader;
        }

        // Fetch chapter metadata and image server URL in parallel for maximum speed
        var chapterTask = Get($"/chapter/{id}?includes[]=scanlation_group");
        var atHomeTask = Get($"/at-home/server/{id}");
        await Task.WhenAll(chapterTask, atHomeTask);

        var chapterNode = (await chapterTask)["data"]!;
        var c = MapChapter(chapterNode);

        // Fetch detail and navigation safely in parallel without blocking image display
        var detailTask = Task.Run(async () =>
        {
            try { return await Detail(c.MangaId); }
            catch { return new MangaCard { Id = c.MangaId, Title = c.Title }; }
        });

        var navTask = Task.Run(async () =>
        {
            try {
                var list = await GetAllChapters(c.MangaId, c.Language);
                return list ?? [];
            }
            catch { return new List<ChapterCard>(); }
        });

        var timeoutTask = Task.Delay(4000);
        await Task.WhenAll(detailTask, Task.WhenAny(navTask, timeoutTask));
        var m = await detailTask;
        var navigation = navTask.IsCompletedSuccessfully ? await navTask : new List<ChapterCard>();

        if (navigation.Count == 0)
        {
            navigation = [c];
        }
        else if (!navigation.Any(x => x.Id == id))
        {
            navigation.Add(c);
            navigation = DeduplicateChapters(navigation, ascending: true);
        }

        var external = S(chapterNode["attributes"]?["externalUrl"]);
        if (external.Length > 0) return new(c, m, [], [], external.StartsWith("https://") ? external : null, navigation);

        var r = await atHomeTask;
        var baseUrl = S(r["baseUrl"]).TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https") throw new UpstreamException("Địa chỉ máy chủ ảnh không hợp lệ.");
        var hash = S(r["chapter"]?["hash"]);
        string ProxyUrl(string url) => "https://services.f-ck.me/v1/image/" + Convert.ToBase64String(Encoding.UTF8.GetBytes(url)).Replace('+', '-').Replace('/', '_');
        string[] Pages(string key, string folder) => r["chapter"]?[key]?.AsArray().Select(x => ProxyUrl($"{baseUrl}/{folder}/{hash}/{E(S(x))}")).ToArray() ?? [];
        var result = new ReaderData(c, m, Pages("data", "data"), Pages("dataSaver", "data-saver"), null, navigation);
        await CacheSet(cacheKey, result, TimeSpan.FromMinutes(20));
        return result;
    }
    public async Task<object> Tags() => (await Get("/manga/tag"))["data"]!.AsArray().Select(x => new { id = S(x?["id"]), name = Localized(x?["attributes"]?["name"]) }).OrderBy(x => x.name).ToArray();
}
