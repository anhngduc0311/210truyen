namespace TruyenDex.Api;
public record RegisterRequest(string Email, string Password, string Name);
public record LoginRequest(string Email, string Password);
public record CommentRequest(string Body);
public record RatingRequest(int Score);
public record MangaRequest(string Title, string AlternativeTitle, string Author, string Cover, string Description,
    string[] Genres, string Status, string Country, string Demographic, int Year, bool Featured);
public record ChapterRequest(decimal Number, string Title, string Language, string[] Pages);
public record FollowRequest(bool Followed);
public record GoogleAuthRequest(string? Credential, string? Code, string? RedirectUri);

public record AdminMangaRequest(
    string Title, string? AlternativeTitle, string? Author, string? Artist, string? Cover, string? Description,
    string[]? Genres, string? Status, string? Country, string? Demographic, int? Year, bool? Featured,
    string? SourceType, bool? IsDraft, bool? IsHidden, string? ScanlationGroup, string[]? Tags);

public record AdminChapterRequest(
    decimal Number, string Title, string? Language, string[]? Pages, string? Content, string? ContentType,
    bool? IsLocked, int? CoinPrice, DateTime? UnlockAt, DateTime? ScheduledPublishAt);

public record UpdateUserRoleRequest(string Role);
public record AdjustUserCoinsRequest(long Amount, string? Description);
public record TaxonomyRequest(string Type, string Name, string? Slug, string? Description);
public record BannedKeywordRequest(string Keyword, string? Action);
public record ReportRequest(Guid? MangaId, Guid? ChapterId, Guid? CommentId, string Type, string Reason);
public record UpdateReportRequest(string Status, string? Notes);
public record SystemLogRequest(string Level, string Source, string Message);
