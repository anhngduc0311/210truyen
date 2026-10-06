using System.Text.RegularExpressions;
using System.Text.Json;
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

// Aggregator for VinaHentai (Manga/Hentai), MimiHentai (Manga/Hentai), HentaiVN (Manhwa), and SayHentai.
public class Catalog(IMemoryCache cache, VinaHentai vinahentai, SayHentai sayhentai, HentaiVn hentaivn, MimiHentai mimihentai, IConnectionMultiplexer? redis = null, MeilisearchClient? meili = null)
{
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

    public static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var lower = title.Trim().ToLowerInvariant();
        return Regex.Replace(lower, @"[^\p{L}\p{N}]+", " ").Trim();
    }

    public static bool IsSameManga(string t1, string alt1, string t2, string alt2)
    {
        var n1 = NormalizeTitle(t1);
        var n2 = NormalizeTitle(t2);
        if (string.IsNullOrEmpty(n1) || string.IsNullOrEmpty(n2)) return false;
        if (n1 == n2) return true;
        if (n1.Length > 6 && n2.Length > 6 && (n1.Contains(n2) || n2.Contains(n1))) return true;

        var a1 = NormalizeTitle(alt1);
        var a2 = NormalizeTitle(alt2);
        if (!string.IsNullOrEmpty(a1) && (a1 == n2 || a1 == a2 || (a1.Length > 6 && n2.Length > 6 && (a1.Contains(n2) || n2.Contains(a1))))) return true;
        if (!string.IsNullOrEmpty(a2) && (n1 == a2 || (n1.Length > 6 && a2.Length > 6 && (n1.Contains(a2) || a2.Contains(n1))))) return true;
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
                var mSlug = await mimihentai.ResolveSlug(m.Id);
                if (!string.IsNullOrEmpty(mSlug))
                {
                    var chaps = await mimihentai.GetChapters(m.Id, 1, 10, ascending: false);
                    if (chaps != null && chaps.Items.Count > 0)
                    {
                        var existing = m.Chapters ?? [];
                        m.Chapters = DeduplicateChapters(existing.Concat(chaps.Items), ascending: false).Take(targetCount).ToList();
                        return;
                    }
                }

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

    public static bool IsManhwa(MangaCard m)
    {
        if (m == null) return false;

        if (!string.IsNullOrWhiteSpace(m.Country) && (m.Country.Equals("ko", StringComparison.OrdinalIgnoreCase) || m.Country.Contains("manhwa", StringComparison.OrdinalIgnoreCase)))
            return true;

        if (m.Genres != null && m.Genres.Any(g => 
            g.Contains("manhwa", StringComparison.OrdinalIgnoreCase) || 
            g.Contains("webtoon", StringComparison.OrdinalIgnoreCase) ||
            g.Contains("truyện hàn", StringComparison.OrdinalIgnoreCase) ||
            g.Contains("hàn quốc", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (Regex.IsMatch(m.Title ?? "", @"\b(manhwa|webtoon)\b|\[manhwa\]|\[webtoon\]", RegexOptions.IgnoreCase))
            return true;

        if (Regex.IsMatch(m.AlternativeTitle ?? "", @"\b(manhwa|webtoon)\b|\[manhwa\]|\[webtoon\]", RegexOptions.IgnoreCase))
            return true;

        return false;
    }

    public async Task<CatalogPage> Home(int page, int size)
    {
        var cacheKey = $"catalog:home:v7:{page}:{size}";
        var cachedPage = await CacheGet<CatalogPage>(cacheKey);
        if (cachedPage != null) return cachedPage;

        // 1. Fetch latest updated manga from MimiHentai and VinaHentai concurrently
        var mimiTask = mimihentai.GetLatest(page, size);
        var vinaTask = vinahentai.GetLatest(page);

        try
        {
            await Task.WhenAll(mimiTask, vinaTask);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Catalog.Home] Fetch warning: {ex.Message}");
        }

        var mimiItems = mimiTask.IsCompletedSuccessfully ? (mimiTask.Result ?? []) : [];
        var vinaItems = vinaTask.IsCompletedSuccessfully ? (vinaTask.Result ?? []) : [];

        var combined = new List<MangaCard>();
        var max = Math.Max(mimiItems.Count, vinaItems.Count);

        // Interleave items from both APIs while deduplicating by title and excluding Manhwa
        for (int i = 0; i < max; i++)
        {
            if (i < mimiItems.Count)
            {
                var m = mimiItems[i];
                if (!IsManhwa(m) && !combined.Any(existing => IsSameManga(existing.Title, existing.AlternativeTitle, m.Title, m.AlternativeTitle)))
                {
                    combined.Add(m);
                }
            }

            if (i < vinaItems.Count)
            {
                var v = vinaItems[i];
                if (!IsManhwa(v) && !combined.Any(existing => IsSameManga(existing.Title, existing.AlternativeTitle, v.Title, v.AlternativeTitle)))
                {
                    combined.Add(v);
                }
            }
        }

        if (combined.Count > 0)
        {
            var finalItems = combined.Take(size).ToList();
            await EnsureTopChapters(finalItems, 3);

            const int totalEstimated = 71500;
            var result = new CatalogPage(finalItems, totalEstimated, page, size);
            await CacheSet(cacheKey, result, TimeSpan.FromMinutes(5));
            return result;
        }

        // 2. Fallback to HentaiVN if both are temporarily unreachable
        var fallbackItems = await hentaivn.GetLatestManhwa(page);
        var fallbackResult = new CatalogPage(fallbackItems, 1200, page, size);
        return fallbackResult;
    }

    public async Task<CatalogPage> Featured(int size = 20)
    {
        var cacheKey = $"catalog:featured:hentaivn:v2:{size}";
        var cached = await CacheGet<CatalogPage>(cacheKey);
        if (cached != null) return cached;

        // 1. Fetch latest Manhwa from HentaiVN
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
            var res = new CatalogPage(finalItems, Math.Max(1200, finalItems.Count), 1, size);
            await CacheSet(cacheKey, res, TimeSpan.FromMinutes(10));
            return res;
        }

        // 2. Fallback to SayHentai
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
            var res = new CatalogPage(finalItems, Math.Max(1200, finalItems.Count), 1, size);
            await CacheSet(cacheKey, res, TimeSpan.FromMinutes(10));
            return res;
        }

        return new CatalogPage([], 0, 1, size);
    }

    private static int GetRelevanceScore(MangaCard m, string q)
    {
        var qNorm = NormalizeTitle(q);
        if (string.IsNullOrEmpty(qNorm)) return 10;

        var aNorm = NormalizeTitle(m.Author);
        if (!string.IsNullOrEmpty(aNorm) && aNorm == qNorm) return 0;

        var tNorm = NormalizeTitle(m.Title);
        var altNorm = NormalizeTitle(m.AlternativeTitle);

        if (tNorm == qNorm) return 0;
        if (altNorm == qNorm) return 1;
        if (!string.IsNullOrEmpty(aNorm) && (aNorm.Contains(qNorm) || qNorm.Contains(aNorm))) return 1;
        if (tNorm.StartsWith(qNorm) || qNorm.StartsWith(tNorm)) return 2;
        if (altNorm.StartsWith(qNorm) || qNorm.StartsWith(altNorm)) return 3;
        if (tNorm.Contains(qNorm)) return 4;
        if (altNorm.Contains(qNorm)) return 5;

        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 1 && words.All(w => (m.Title + " " + m.AlternativeTitle + " " + m.Author).Contains(w, StringComparison.OrdinalIgnoreCase)))
            return 6;

        return 10;
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

    public async Task<CatalogPage> Search(int page, int size, string? q, string? genre, string? status, string? country, string? demographic, string? language, string? sort, int? year)
    {
        var cacheKey = $"catalog:search:v14:{page}:{size}:{q}:{genre}:{status}:{country}:{demographic}:{language}:{sort}:{year}";
        var cachedSearch = await CacheGet<CatalogPage>(cacheKey);
        if (cachedSearch != null)
        {
            return cachedSearch;
        }

        bool isKorea = !string.IsNullOrWhiteSpace(country) && (country.Contains("ko", StringComparison.OrdinalIgnoreCase) || country.Contains("manhwa", StringComparison.OrdinalIgnoreCase));
        bool isJapan = !string.IsNullOrWhiteSpace(country) && (country.Contains("ja", StringComparison.OrdinalIgnoreCase) || country.Contains("manga", StringComparison.OrdinalIgnoreCase));
        bool noCountryFilter = string.IsNullOrWhiteSpace(country);

        var vinaTask = !string.IsNullOrWhiteSpace(q)
            ? vinahentai.Search(q, page)
            : (!string.IsNullOrWhiteSpace(genre)
                ? vinahentai.GetByGenre(genre, page)
                : (isJapan || (noCountryFilter && string.IsNullOrEmpty(genre)) ? vinahentai.GetLatest(page) : Task.FromResult(new List<MangaCard>())));

        var mimiTask = !string.IsNullOrWhiteSpace(q)
            ? mimihentai.Search(q, page)
            : (!string.IsNullOrWhiteSpace(genre)
                ? mimihentai.GetByGenre(genre, page)
                : (isJapan || (noCountryFilter && string.IsNullOrEmpty(genre)) ? mimihentai.GetLatest(page, size) : Task.FromResult(new List<MangaCard>())));

        var hvnTask = !string.IsNullOrWhiteSpace(q)
            ? hentaivn.Search(q, page)
            : (isKorea || (noCountryFilter && string.IsNullOrEmpty(genre)) ? hentaivn.GetLatestManhwa(page) : Task.FromResult(new List<MangaCard>()));

        var sayTask = !string.IsNullOrWhiteSpace(q)
            ? sayhentai.Search(q, page)
            : (isKorea || (noCountryFilter && string.IsNullOrEmpty(genre)) ? sayhentai.GetLatestManhwa(page) : Task.FromResult(new List<MangaCard>()));

        await Task.WhenAll(vinaTask, mimiTask, hvnTask, sayTask);

        var vinaResults = await vinaTask;
        var mimiResults = await mimiTask;
        var hvnResults = await hvnTask;
        var sayResults = await sayTask;

        var items = new List<MangaCard>();
        int total = 0;

        if (isKorea)
        {
            foreach (var h in hvnResults) items.Add(h);
            foreach (var s in sayResults)
            {
                if (!items.Any(existing => IsSameManga(existing.Title, existing.AlternativeTitle, s.Title, s.AlternativeTitle)))
                    items.Add(s);
            }
            total = !string.IsNullOrWhiteSpace(q) ? (items.Count >= size ? page * size + size : (page - 1) * size + items.Count) : Math.Max(1200, items.Count);
        }
        else if (isJapan || !string.IsNullOrWhiteSpace(genre))
        {
            foreach (var v in vinaResults) items.Add(v);
            foreach (var m in mimiResults)
            {
                if (!items.Any(existing => IsSameManga(existing.Title, existing.AlternativeTitle, m.Title, m.AlternativeTitle)))
                    items.Add(m);
            }
            total = !string.IsNullOrWhiteSpace(q) ? (items.Count >= size ? page * size + size : (page - 1) * size + items.Count) : (vinaResults.Count > 0 || mimiResults.Count > 0 ? Math.Max(71500, page * 24 + 48) : items.Count);
        }
        else
        {
            foreach (var v in vinaResults) items.Add(v);
            foreach (var m in mimiResults)
            {
                if (!items.Any(existing => IsSameManga(existing.Title, existing.AlternativeTitle, m.Title, m.AlternativeTitle)))
                    items.Add(m);
            }
            foreach (var h in hvnResults)
            {
                if (!items.Any(existing => IsSameManga(existing.Title, existing.AlternativeTitle, h.Title, h.AlternativeTitle)))
                    items.Add(h);
            }
            foreach (var s in sayResults)
            {
                if (!items.Any(existing => IsSameManga(existing.Title, existing.AlternativeTitle, s.Title, s.AlternativeTitle)))
                    items.Add(s);
            }
            total = !string.IsNullOrWhiteSpace(q) ? (items.Count >= size ? page * size + size : (page - 1) * size + items.Count) : Math.Max(71500, items.Count);
        }

        // Fallback: If external sources returned 0 items and meili is available, search local meili index
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

    public async Task<MangaCard> Detail(Guid id)
    {
        var cacheKey = $"catalog:detail:v10:{id}";
        var cachedManga = await CacheGet<MangaCard>(cacheKey);
        if (cachedManga != null) return cachedManga;

        var vinaManga = await vinahentai.GetDetail(id);
        if (vinaManga != null)
        {
            await CacheSet(cacheKey, vinaManga, TimeSpan.FromMinutes(20));
            return vinaManga;
        }

        var mimiManga = await mimihentai.GetDetail(id);
        if (mimiManga != null)
        {
            await CacheSet(cacheKey, mimiManga, TimeSpan.FromMinutes(20));
            return mimiManga;
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

        if (meili != null)
        {
            try
            {
                var index = meili.Index("mangas");
                var doc = await index.GetDocumentAsync<MangaCard>(id.ToString());
                if (doc != null) return doc;
            }
            catch { }
        }

        throw new UpstreamException("Không tìm thấy thông tin truyện.", 404);
    }

    public async Task<List<ChapterCard>> GetAllChapters(Guid id, string language)
    {
        var cacheKey = $"catalog:all_chapters:v10:{id}:{language}";
        var cached = await CacheGet<List<ChapterCard>>(cacheKey);
        if (cached != null && cached.Count > 0) return cached;

        var vinaChaps = await vinahentai.GetChapters(id, 1, 1000, ascending: true);
        if (vinaChaps != null && vinaChaps.Items.Count > 0)
        {
            var cleanVina = DeduplicateChapters(vinaChaps.Items, ascending: true);
            await CacheSet(cacheKey, cleanVina, TimeSpan.FromMinutes(20));
            return cleanVina;
        }

        var mimiChaps = await mimihentai.GetChapters(id, 1, 1000, ascending: true);
        if (mimiChaps != null && mimiChaps.Items.Count > 0)
        {
            var cleanMimi = DeduplicateChapters(mimiChaps.Items, ascending: true);
            await CacheSet(cacheKey, cleanMimi, TimeSpan.FromMinutes(20));
            return cleanMimi;
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

        return [];
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
        var cacheKey = $"catalog:reader:v10:{id}";
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

        var mimiReader = await mimihentai.GetReader(id);
        if (mimiReader != null)
        {
            var mangaId = mimiReader.Chapter.MangaId;
            var manga = mangaId != Guid.Empty ? await Detail(mangaId) : null;
            var nav = mangaId != Guid.Empty ? await GetAllChapters(mangaId, "vi") : null;
            if (nav == null || nav.Count == 0)
            {
                nav = mimiReader.Navigation;
            }
            if (!nav.Any(x => x.Id == id))
            {
                nav.Add(mimiReader.Chapter);
                nav = DeduplicateChapters(nav, ascending: true);
            }

            var finalReader = new ReaderData(
                mimiReader.Chapter,
                manga ?? mimiReader.Manga,
                mimiReader.Pages,
                mimiReader.DataSaverPages,
                mimiReader.ExternalUrl,
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

        throw new UpstreamException("Không tìm thấy chương truyện hoặc trang đọc.", 404);
    }

    public async Task<object> Tags()
    {
        var vinaGenresTask = vinahentai.GetGenres();
        var mimiGenresTask = mimihentai.GetGenres();
        await Task.WhenAll(vinaGenresTask, mimiGenresTask);

        var list = new List<VinaGenre>();
        foreach (var g in await vinaGenresTask) list.Add(g);
        foreach (var g in await mimiGenresTask)
        {
            if (!list.Any(x => x.Name.Equals(g.Name, StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(g);
            }
        }

        return list.Select(g => new { id = g.Id, name = g.Name, description = g.Description }).ToArray();
    }
}
