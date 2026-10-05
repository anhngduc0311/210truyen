using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TruyenDex.Api.Migrations
{
    /// <inheritdoc />
    public partial class AllowLongSourceTitles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Title",
                table: "Mangas",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(250)",
                oldMaxLength: 250);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Title",
                table: "Mangas",
                type: "character varying(250)",
                maxLength: 250,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");
        }
    }
}
