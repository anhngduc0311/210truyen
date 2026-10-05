using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TruyenDex.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminDashboardFeatures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "Coins",
                table: "Users",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<bool>(
                name: "IsBanned",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Artist",
                table: "Mangas",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsDraft",
                table: "Mangas",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsHidden",
                table: "Mangas",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ScanlationGroup",
                table: "Mangas",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SourceType",
                table: "Mangas",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string[]>(
                name: "Tags",
                table: "Mangas",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.AddColumn<bool>(
                name: "IsFlagged",
                table: "Comments",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "CoinPrice",
                table: "Chapters",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Content",
                table: "Chapters",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContentType",
                table: "Chapters",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsLocked",
                table: "Chapters",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "ScheduledPublishAt",
                table: "Chapters",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UnlockAt",
                table: "Chapters",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BannedKeywords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Keyword = table.Column<string>(type: "text", nullable: false),
                    Action = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BannedKeywords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CoinTransactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<long>(type: "bigint", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoinTransactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CoinTransactions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Reports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    MangaId = table.Column<Guid>(type: "uuid", nullable: true),
                    ChapterId = table.Column<Guid>(type: "uuid", nullable: true),
                    CommentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Type = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Reports_Chapters_ChapterId",
                        column: x => x.ChapterId,
                        principalTable: "Chapters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Reports_Mangas_MangaId",
                        column: x => x.MangaId,
                        principalTable: "Mangas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Reports_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "SystemLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Level = table.Column<string>(type: "text", nullable: false),
                    Source = table.Column<string>(type: "text", nullable: false),
                    Message = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TaxonomyItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Slug = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxonomyItems", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BannedKeywords_Keyword",
                table: "BannedKeywords",
                column: "Keyword",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CoinTransactions_UserId",
                table: "CoinTransactions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Reports_ChapterId",
                table: "Reports",
                column: "ChapterId");

            migrationBuilder.CreateIndex(
                name: "IX_Reports_MangaId",
                table: "Reports",
                column: "MangaId");

            migrationBuilder.CreateIndex(
                name: "IX_Reports_UserId",
                table: "Reports",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_SystemLogs_CreatedAt",
                table: "SystemLogs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_TaxonomyItems_Type_Slug",
                table: "TaxonomyItems",
                columns: new[] { "Type", "Slug" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BannedKeywords");

            migrationBuilder.DropTable(
                name: "CoinTransactions");

            migrationBuilder.DropTable(
                name: "Reports");

            migrationBuilder.DropTable(
                name: "SystemLogs");

            migrationBuilder.DropTable(
                name: "TaxonomyItems");

            migrationBuilder.DropColumn(
                name: "Coins",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "IsBanned",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Artist",
                table: "Mangas");

            migrationBuilder.DropColumn(
                name: "IsDraft",
                table: "Mangas");

            migrationBuilder.DropColumn(
                name: "IsHidden",
                table: "Mangas");

            migrationBuilder.DropColumn(
                name: "ScanlationGroup",
                table: "Mangas");

            migrationBuilder.DropColumn(
                name: "SourceType",
                table: "Mangas");

            migrationBuilder.DropColumn(
                name: "Tags",
                table: "Mangas");

            migrationBuilder.DropColumn(
                name: "IsFlagged",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "CoinPrice",
                table: "Chapters");

            migrationBuilder.DropColumn(
                name: "Content",
                table: "Chapters");

            migrationBuilder.DropColumn(
                name: "ContentType",
                table: "Chapters");

            migrationBuilder.DropColumn(
                name: "IsLocked",
                table: "Chapters");

            migrationBuilder.DropColumn(
                name: "ScheduledPublishAt",
                table: "Chapters");

            migrationBuilder.DropColumn(
                name: "UnlockAt",
                table: "Chapters");
        }
    }
}
