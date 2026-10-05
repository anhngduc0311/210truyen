using Microsoft.EntityFrameworkCore;

namespace TruyenDex.Api;

public class Manga
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string AlternativeTitle { get; set; } = "";
    public string Author { get; set; } = "";
    public string Cover { get; set; } = "";
    public string Description { get; set; } = "";
    public string[] Genres { get; set; } = [];
    public string Status { get; set; } = "ongoing";
    public string Country { get; set; } = "jp";
    public string Demographic { get; set; } = "shounen";
    public int Year { get; set; } = 2026;
    public bool Featured { get; set; }
    public bool IsDemo { get; set; }
    public long Views { get; set; }
    public string Artist { get; set; } = "";
    public string SourceType { get; set; } = "crawl"; // original, translation, crawl
    public bool IsDraft { get; set; }
    public bool IsHidden { get; set; }
    public string ScanlationGroup { get; set; } = "";
    public string[] Tags { get; set; } = [];
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<Chapter> Chapters { get; set; } = [];
    public List<Follow> Followers { get; set; } = [];
    public List<Rating> Ratings { get; set; } = [];
    public List<Comment> Comments { get; set; } = [];
}
public class Chapter
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MangaId { get; set; }
    public Manga Manga { get; set; } = null!;
    public decimal Number { get; set; }
    public string Title { get; set; } = "";
    public string Language { get; set; } = "vi";
    public string[] Pages { get; set; } = [];
    public string? Content { get; set; } // Rich text for web novels
    public string ContentType { get; set; } = "comic"; // comic, novel
    public bool IsLocked { get; set; }
    public int CoinPrice { get; set; }
    public DateTime? UnlockAt { get; set; }
    public DateTime? ScheduledPublishAt { get; set; }
    public DateTime PublishedAt { get; set; } = DateTime.UtcNow;
}
public class AppUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "reader"; // superadmin, admin, editor, translator, reader
    public long Coins { get; set; }
    public bool IsBanned { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
public class Follow
{
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public Guid MangaId { get; set; }
    public Manga Manga { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
public class Rating
{
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public Guid MangaId { get; set; }
    public Manga Manga { get; set; } = null!;
    public int Score { get; set; }
}
public class History
{
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public Guid MangaId { get; set; }
    public Manga Manga { get; set; } = null!;
    public Guid ChapterId { get; set; }
    public Chapter Chapter { get; set; } = null!;
    public DateTime ReadAt { get; set; } = DateTime.UtcNow;
}
public class Comment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public Guid MangaId { get; set; }
    public Manga Manga { get; set; } = null!;
    public string Body { get; set; } = "";
    public bool IsFlagged { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
public class Report
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? UserId { get; set; }
    public AppUser? User { get; set; }
    public Guid? MangaId { get; set; }
    public Manga? Manga { get; set; }
    public Guid? ChapterId { get; set; }
    public Chapter? Chapter { get; set; }
    public Guid? CommentId { get; set; }
    public string Type { get; set; } = "broken_image"; // broken_image, wrong_order, translation_error, comment_spam, other
    public string Reason { get; set; } = "";
    public string Status { get; set; } = "pending"; // pending, resolved, dismissed
    public string Notes { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }
}
public class CoinTransaction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public long Amount { get; set; }
    public string Type { get; set; } = "deposit"; // deposit, unlock, admin_adjust
    public string Description { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
public class BannedKeyword
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Keyword { get; set; } = "";
    public string Action { get; set; } = "block"; // block, flag
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
public class TaxonomyItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Type { get; set; } = "genre"; // genre, tag, author, group
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
public class SystemLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Level { get; set; } = "info"; // info, warning, error
    public string Source { get; set; } = "system";
    public string Message { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
public class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<Manga> Mangas => Set<Manga>();
    public DbSet<Chapter> Chapters => Set<Chapter>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Follow> Follows => Set<Follow>();
    public DbSet<Rating> Ratings => Set<Rating>();
    public DbSet<History> Histories => Set<History>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<Report> Reports => Set<Report>();
    public DbSet<CoinTransaction> CoinTransactions => Set<CoinTransaction>();
    public DbSet<BannedKeyword> BannedKeywords => Set<BannedKeyword>();
    public DbSet<TaxonomyItem> TaxonomyItems => Set<TaxonomyItem>();
    public DbSet<SystemLog> SystemLogs => Set<SystemLog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AppUser>().HasIndex(x => x.Email).IsUnique();
        b.Entity<Follow>().HasKey(x => new { x.UserId, x.MangaId });
        b.Entity<Rating>().HasKey(x => new { x.UserId, x.MangaId });
        b.Entity<History>().HasKey(x => new { x.UserId, x.MangaId });
        b.Entity<Chapter>().HasIndex(x => new { x.MangaId, x.Number, x.Language });
        b.Entity<Manga>().HasIndex(x => x.UpdatedAt);
        b.Entity<Comment>().HasIndex(x => new { x.MangaId, x.CreatedAt });
        b.Entity<AppUser>().Property(x => x.Email).HasMaxLength(254);
        b.Entity<Comment>().Property(x => x.Body).HasMaxLength(2000);
        b.Entity<History>().HasOne(x => x.Chapter).WithMany().HasForeignKey(x => x.ChapterId).OnDelete(DeleteBehavior.Cascade);

        b.Entity<Report>().HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<Report>().HasOne(x => x.Manga).WithMany().HasForeignKey(x => x.MangaId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<Report>().HasOne(x => x.Chapter).WithMany().HasForeignKey(x => x.ChapterId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<CoinTransaction>().HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<TaxonomyItem>().HasIndex(x => new { x.Type, x.Slug }).IsUnique();
        b.Entity<BannedKeyword>().HasIndex(x => x.Keyword).IsUnique();
        b.Entity<SystemLog>().HasIndex(x => x.CreatedAt);
    }
}
