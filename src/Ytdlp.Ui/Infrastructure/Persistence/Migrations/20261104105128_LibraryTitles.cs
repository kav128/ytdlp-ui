using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ytdlp.Ui.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LibraryTitles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LibraryTitles",
                columns: table => new
                {
                    ObjectKey = table.Column<string>(type: "text", nullable: false),
                    ETag = table.Column<string>(type: "text", nullable: false),
                    ModifiedSecond = table.Column<long>(type: "bigint", nullable: false),
                    Title = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LibraryTitles", x => new { x.ObjectKey, x.ETag, x.ModifiedSecond });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LibraryTitles");
        }
    }
}
