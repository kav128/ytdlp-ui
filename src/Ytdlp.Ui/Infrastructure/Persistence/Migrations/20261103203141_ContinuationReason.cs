using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ytdlp.Ui.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ContinuationReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContinuationReasonCode",
                table: "DownloadAttempts",
                type: "text",
                nullable: true);
            migrationBuilder.Sql("""
                UPDATE "DownloadAttempts" SET "ContinuationReasonCode" = CASE "ErrorDetails"
                    WHEN 'The media plan is missing; downloading must be restored.' THEN 'media_plan_missing'
                    WHEN 'A required artifact is missing or has a different size/SHA-256; the earliest necessary stage will be restored.' THEN 'artifacts_unavailable'
                    WHEN 'The existing publication will be reconciled before any upload.' THEN 'publication_reconciliation' END,
                    "ErrorDetails" = NULL
                WHERE "ErrorDetails" IN (
                    'The media plan is missing; downloading must be restored.',
                    'A required artifact is missing or has a different size/SHA-256; the earliest necessary stage will be restored.',
                    'The existing publication will be reconciled before any upload.');
                UPDATE "DownloadAttempts" AS attempt SET "ContinuationReasonCode" = 'artifacts_unavailable'
                WHERE attempt."ContinuationReasonCode" IS NULL AND EXISTS
                    (SELECT 1 FROM "StageExecutions" AS stage WHERE stage."AttemptId" = attempt."Id" AND stage."ErrorCode" = 'artifacts_unavailable');
                """);
            // Only exact application-owned messages are removed. Arbitrary tool diagnostics are retained.
            var messages = new[] {
                "The stage failed; its files were retained for a manual retry.",
                "Processing was interrupted; retry is required.",
                "Processing stopped; its files were retained for a manual retry.",
                "The process was stopped before the attempt finished.",
                "Stopping could not be confirmed. Recreate the application container before retrying.",
                "Size/SHA-256 validation requires an earlier stage.",
                "Metadata exceeded the technical JSON buffer.", "The source returned invalid metadata.",
                "The selected track was not produced.", "The downloaded track does not match the media requirements.",
                "The produced container differs from the persisted preparation plan.",
                "The downloaded media could not be verified.", "Prepared media is missing.",
                "Prepared media must contain audio and video up to 1080p.", "Unknown media container.",
                "Saved media plan is missing.", "Prepared artifact is missing or changed.",
                "playlist_not_supported", "live_not_supported", "format_unavailable"
            };
            var values = string.Join(",", messages.Select(message => "'" + message.Replace("'", "''") + "'"));
            foreach (var table in new[] { "Downloads", "DownloadAttempts", "StageExecutions" })
                migrationBuilder.Sql($"UPDATE \"{table}\" SET \"ErrorDetails\" = NULL WHERE \"ErrorDetails\" IN ({values});");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContinuationReasonCode",
                table: "DownloadAttempts");
        }
    }
}
