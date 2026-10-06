using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;
using Meilisearch;

namespace TruyenDex.Api;

public class MimiAuthor
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("manga_count")] public int? MangaCount { get; set; }
    [JsonPropertyName("cover_url")] public string? CoverUrl { get; set; }
}

public class MimiGenreItem
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("manga_count")] public int? MangaCount { get; set; }
}

public class MimiUploader
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
    [JsonPropertyName("avatar_url")] public string? AvatarUrl { get; set; }
    [JsonPropertyName("username")] public string? Username { get; set; }
}

public class MimiMangaItem
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("cover_url")] public string CoverUrl { get; set; } = "";
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("view")] public int View { get; set; }
    [JsonPropertyName("follows")] public int Follows { get; set; }
    [JsonPropertyName("total_likes")] public int TotalLikes { get; set; }
    [JsonPropertyName("is_reup")] public bool IsReup { get; set; }
    [JsonPropertyName("last_updated")] public DateTime? LastUpdated { get; set; }
    [JsonPropertyName("chapter_count")] public int ChapterCount { get; set; }
    [JsonPropertyName("authors")] public List<MimiAuthor>? Authors { get; set; }
    [JsonPropertyName("genres")] public List<MimiGenreItem>? Genres { get; set; }
    [JsonPropertyName("alt_names")] public List<string>? AltNames { get; set; }
    [JsonPropertyName("uploader")] public MimiUploader? Uploader { get; set; }
}

public class MimiMangaListResponse
{
    [JsonPropertyName("page")] public int Page { get; set; }
    [JsonPropertyName("page_size")] public int PageSize { get; set; }
    [JsonPropertyName("total_items")] public int TotalItems { get; set; }
    [JsonPropertyName("total_pages")] public int TotalPages { get; set; }
    [JsonPropertyName("has_prev")] public bool HasPrev { get; set; }
    [JsonPropertyName("has_next")] public bool HasNext { get; set; }
    [JsonPropertyName("items")] public List<MimiMangaItem> Items { get; set; } = [];
}

public class MimiChapterItem
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("order")] public decimal Order { get; set; }
    [JsonPropertyName("likes")] public int Likes { get; set; }
    [JsonPropertyName("manga_id")] public long MangaId { get; set; }
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
}

public class MimiChapterInfo
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("order")] public decimal Order { get; set; }
    [JsonPropertyName("likes")] public int Likes { get; set; }
    [JsonPropertyName("manga_id")] public long MangaId { get; set; }
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
}

public class MimiPageItem
{
    [JsonPropertyName("image_url")] public string ImageUrl { get; set; } = "";
}

public class MimiChapterDetailResponse
{
    [JsonPropertyName("info")] public MimiChapterInfo? Info { get; set; }
    [JsonPropertyName("next")] public long? Next { get; set; }
    [JsonPropertyName("prev")] public long? Prev { get; set; }
    [JsonPropertyName("pages")] public List<MimiPageItem> Pages { get; set; } = [];
}

public class MimiHentai(HttpClient http, IMemoryCache cache, IConnectionMultiplexer? redis = null, MeilisearchClient? meili = null)
{
    private const string BaseUrl = "https://mimihentai.moe";
    private static readonly ConcurrentDictionary<Guid, string> MangaSlugMap = new();
    private static readonly ConcurrentDictionary<Guid, string> ChapterIdMap = new();
    private static readonly ConcurrentDictionary<Guid, Guid> ChapterMangaMap = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static Guid CreateGuid(string key)
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(key));
        return new Guid(hash);
    }

    public void RegisterManga(Guid id, string slug)
    {
        MangaSlugMap[id] = slug;
        _ = CacheSetString($"mimihentai:slug:{id}", slug, TimeSpan.FromDays(30));
    }

    public void RegisterChapter(Guid chapId, string chapNumericId, Guid mangaId)
    {
        ChapterIdMap[chapId] = chapNumericId;
        ChapterMangaMap[chapId] = mangaId;
        _ = CacheSetString($"mimihentai:chap:{chapId}", chapNumericId, TimeSpan.FromDays(30));
        _ = CacheSetString($"mimihentai:chap_manga:{chapId}", mangaId.ToString(), TimeSpan.FromDays(30));
    }

    public async Task<string?> ResolveSlug(Guid id)
    {
        if (MangaSlugMap.TryGetValue(id, out var slug) && !string.IsNullOrEmpty(slug)) return slug;
        slug = await CacheGetString($"mimihentai:slug:{id}");
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
        var mangaIdStr = await CacheGetString($"mimihentai:chap_manga:{chapterId}");
        if (!string.IsNullOrEmpty(mangaIdStr) && Guid.TryParse(mangaIdStr, out var mId))
        {
            ChapterMangaMap[chapterId] = mId;
            return mId;
        }
        return Guid.Empty;
    }

    public async Task<string?> ResolveChapterId(Guid chapterId)
    {
        if (ChapterIdMap.TryGetValue(chapterId, out var chapId) && !string.IsNullOrEmpty(chapId)) return chapId;
        chapId = await CacheGetString($"mimihentai:chap:{chapterId}");
        if (!string.IsNullOrEmpty(chapId))
        {
            ChapterIdMap[chapterId] = chapId;
            return chapId;
        }

        var mId = await ResolveChapterMangaId(chapterId);
        if (mId != Guid.Empty)
        {
            await GetChapters(mId, 1, 500);
            if (ChapterIdMap.TryGetValue(chapterId, out chapId)) return chapId;
        }

        return null;
    }

    private async Task<string?> CacheGetString(string key)
    {
        if (redis != null && redis.IsConnected)
        {
            try
            {
                var v = await redis.GetDatabase().StringGetAsync(key);
                if (v.HasValue) return v.ToString();
            }
            catch { }
        }
        return cache.TryGetValue<string>(key, out var cached) ? cached : null;
    }

    private async Task CacheSetString(string key, string value, TimeSpan expiry)
    {
        if (redis != null && redis.IsConnected)
        {
            try
            {
                await redis.GetDatabase().StringSetAsync(key, value, expiry);
            }
            catch { }
        }
        cache.Set(key, value, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = expiry, Size = 1 });
    }

    private async Task<T?> FetchJson<T>(string endpoint, TimeSpan? cacheDuration = null) where T : class
    {
        var url = endpoint.StartsWith("http") ? endpoint : $"{BaseUrl}{endpoint}";
        var cacheKey = $"mimihentai:json:{url}";
        var cached = await CacheGetString(cacheKey);
        if (cached != null)
        {
            try
            {
                return JsonSerializer.Deserialize<T>(cached, JsonOptions);
            }
            catch { }
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
            req.Headers.Referrer = new Uri(BaseUrl);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var res = await http.SendAsync(req, cts.Token);
            if (!res.IsSuccessStatusCode) return null;

            var json = await res.Content.ReadAsStringAsync(cts.Token);
            if (!string.IsNullOrEmpty(json))
            {
                if (cacheDuration.HasValue)
                {
                    await CacheSetString(cacheKey, json, cacheDuration.Value);
                }
                return JsonSerializer.Deserialize<T>(json, JsonOptions);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MimiHentai.FetchJson] Error fetching {url}: {ex.Message}");
        }

        return null;
    }

    public MangaCard ToMangaCard(MimiMangaItem m)
    {
        var id = CreateGuid($"mimihentai:manga:{m.Id}");
        RegisterManga(id, m.Id.ToString());

        var author = m.Authors != null && m.Authors.Count > 0
            ? string.Join(", ", m.Authors.Select(a => a.Name))
            : "";

        var alt = m.AltNames != null && m.AltNames.Count > 0
            ? string.Join(", ", m.AltNames)
            : "";

        var genres = m.Genres != null && m.Genres.Count > 0
            ? m.Genres.Select(g => g.Name).ToArray()
            : [];

        var isManhwa = genres.Any(g => g.Contains("Manhwa", StringComparison.OrdinalIgnoreCase) || g.Contains("Webtoon", StringComparison.OrdinalIgnoreCase));
        var country = isManhwa ? "ko" : "ja";

        var card = new MangaCard
        {
            Id = id,
            Slug = m.Id.ToString(),
            Title = m.Title.Trim(),
            AlternativeTitle = alt,
            Author = author,
            Cover = m.CoverUrl ?? "",
            Description = m.Description ?? "",
            Genres = genres,
            Status = "Đang tiến hành",
            Country = country,
            Demographic = "",
            ContentRating = "erotica",
            Year = m.LastUpdated?.Year,
            Rating = 5.0,
            Follows = m.Follows,
            UpdatedAt = m.LastUpdated ?? DateTime.UtcNow,
            TotalChapters = m.ChapterCount,
            Chapters = []
        };

        return card;
    }

    public ChapterCard ToChapterCard(MimiChapterItem c, Guid mangaId, string group = "")
    {
        var chapId = CreateGuid($"mimihentai:chapter:{c.Id}");
        RegisterChapter(chapId, c.Id.ToString(), mangaId);

        return new ChapterCard(
            chapId,
            mangaId,
            c.Title.Trim(),
            c.Order,
            "vi",
            c.CreatedAt,
            group
        );
    }

    public async Task<List<MangaCard>> GetLatest(int page = 1, int pageSize = 24)
    {
        var data = await FetchJson<MimiMangaListResponse>($"/api/manga?page={page}&page_size={pageSize}", TimeSpan.FromMinutes(5));
        if (data == null || data.Items == null || data.Items.Count == 0)
        {
            return [];
        }

        return data.Items.Select(ToMangaCard).ToList();
    }

    public async Task<List<MangaCard>> Search(string query, int page = 1)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        var data = await FetchJson<MimiMangaListResponse>($"/api/manga/search?q={Uri.EscapeDataString(query.Trim())}&page={page}", TimeSpan.FromMinutes(5));
        if (data == null || data.Items == null || data.Items.Count == 0)
        {
            return [];
        }

        return data.Items.Select(ToMangaCard).ToList();
    }

    public async Task<List<MangaCard>> GetByGenre(string genre, int page = 1)
    {
        if (string.IsNullOrWhiteSpace(genre)) return [];

        var genres = await GetGenres();
        var match = genres.FirstOrDefault(g => g.Name.Equals(genre, StringComparison.OrdinalIgnoreCase) || g.Id.Equals(genre, StringComparison.OrdinalIgnoreCase));

        string endpoint;
        if (match != null)
        {
            endpoint = $"/api/manga/advanced-search?genres={match.Id}&page={page}";
        }
        else
        {
            endpoint = $"/api/manga/search?q={Uri.EscapeDataString(genre)}&page={page}";
        }

        var data = await FetchJson<MimiMangaListResponse>(endpoint, TimeSpan.FromMinutes(5));
        if (data == null || data.Items == null || data.Items.Count == 0)
        {
            return [];
        }

        return data.Items.Select(ToMangaCard).ToList();
    }

    public async Task<MangaCard?> GetDetail(Guid id)
    {
        var slug = await ResolveSlug(id);
        if (string.IsNullOrEmpty(slug)) return null;

        var m = await FetchJson<MimiMangaItem>($"/api/manga/{slug}", TimeSpan.FromMinutes(10));
        if (m == null) return null;

        var card = ToMangaCard(m);

        // Also fetch top chapters
        try
        {
            var chaptersData = await FetchJson<List<MimiChapterItem>>($"/api/manga/{slug}/chapters", TimeSpan.FromMinutes(10));
            if (chaptersData != null && chaptersData.Count > 0)
            {
                var uploaderGroup = m.Uploader?.DisplayName ?? "";
                card.Chapters = chaptersData
                    .Select(c => ToChapterCard(c, id, uploaderGroup))
                    .OrderByDescending(c => c.Number)
                    .ThenByDescending(c => c.PublishedAt)
                    .Take(10)
                    .ToList();
                card.TotalChapters = Math.Max(card.TotalChapters, chaptersData.Count);
            }
        }
        catch { }

        return card;
    }

    public async Task<ChapterPage?> GetChapters(Guid id, int page = 1, int size = 100, bool ascending = false)
    {
        var slug = await ResolveSlug(id);
        if (string.IsNullOrEmpty(slug)) return null;

        var chaptersData = await FetchJson<List<MimiChapterItem>>($"/api/manga/{slug}/chapters", TimeSpan.FromMinutes(10));
        if (chaptersData == null || chaptersData.Count == 0)
        {
            return new ChapterPage([], 0, page, size);
        }

        var cards = chaptersData.Select(c => ToChapterCard(c, id)).ToList();
        var ordered = ascending
            ? cards.OrderBy(c => c.Number).ThenBy(c => c.PublishedAt).ToList()
            : cards.OrderByDescending(c => c.Number).ThenByDescending(c => c.PublishedAt).ToList();

        var total = ordered.Count;
        var paged = ordered.Skip((page - 1) * size).Take(size).ToList();
        return new ChapterPage(paged, total, page, size);
    }

    public async Task<ReaderData?> GetReader(Guid chapterId)
    {
        var chapNumericId = await ResolveChapterId(chapterId);
        if (string.IsNullOrEmpty(chapNumericId)) return null;

        var chapRes = await FetchJson<MimiChapterDetailResponse>($"/api/chapters/{chapNumericId}", TimeSpan.FromMinutes(30));
        if (chapRes == null || chapRes.Info == null) return null;

        var mangaNumericId = chapRes.Info.MangaId;
        var mangaId = CreateGuid($"mimihentai:manga:{mangaNumericId}");
        RegisterManga(mangaId, mangaNumericId.ToString());

        var currentChap = new ChapterCard(
            chapterId,
            mangaId,
            chapRes.Info.Title.Trim(),
            chapRes.Info.Order,
            "vi",
            chapRes.Info.CreatedAt
        );

        var pages = chapRes.Pages.Select(p => p.ImageUrl).Where(u => !string.IsNullOrWhiteSpace(u)).ToArray();

        // Get navigation chapters
        var allChaps = await GetChapters(mangaId, 1, 1000, ascending: true);
        var navList = allChaps?.Items ?? [currentChap];

        var mangaDetail = await GetDetail(mangaId);
        var externalUrl = $"{BaseUrl}/manga/{mangaNumericId}/chapter/{chapNumericId}";

        return new ReaderData(
            currentChap,
            mangaDetail ?? new MangaCard { Id = mangaId, Title = chapRes.Info.Title },
            pages,
            pages,
            externalUrl,
            navList
        );
    }

    public async Task<List<VinaGenre>> GetGenres()
    {
        var cacheKey = "mimihentai:genres";
        var cached = await CacheGetString(cacheKey);
        if (cached != null)
        {
            try
            {
                return JsonSerializer.Deserialize<List<VinaGenre>>(cached, JsonOptions) ?? [];
            }
            catch { }
        }

        var data = await FetchJson<List<MimiGenreItem>>("/api/genres", TimeSpan.FromHours(24));
        if (data == null || data.Count == 0) return [];

        var list = data.Select(g => new VinaGenre(g.Id.ToString(), g.Name, g.Description)).ToList();
        var json = JsonSerializer.Serialize(list, JsonOptions);
        await CacheSetString(cacheKey, json, TimeSpan.FromHours(24));
        return list;
    }
}
