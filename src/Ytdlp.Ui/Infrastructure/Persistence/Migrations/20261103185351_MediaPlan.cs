using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ytdlp.Ui.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MediaPlan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MediaPlanJson",
                table: "Downloads",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MediaPlanJson",
                table: "Downloads");
        }
    }
}
