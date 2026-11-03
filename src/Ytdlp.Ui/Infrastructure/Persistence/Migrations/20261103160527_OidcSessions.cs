using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ytdlp.Ui.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OidcSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE AdminSessions SET RevokedAt = COALESCE(RevokedAt, CAST(strftime('%s', 'now') AS INTEGER) * 1000);");
            migrationBuilder.DropIndex(
                name: "IX_AdminSessions_RefreshTokenHash",
                table: "AdminSessions");

            migrationBuilder.DropColumn(name: "RefreshTokenHash", table: "AdminSessions");
            migrationBuilder.AddColumn<byte[]>(name: "ProtectedTicket", table: "AdminSessions", type: "BLOB", nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Issuer",
                table: "AdminSessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Subject",
                table: "AdminSessions",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Issuer",
                table: "AdminSessions");

            migrationBuilder.DropColumn(
                name: "Subject",
                table: "AdminSessions");

            migrationBuilder.DropColumn(name: "ProtectedTicket", table: "AdminSessions");
            migrationBuilder.AddColumn<byte[]>(name: "RefreshTokenHash", table: "AdminSessions", type: "BLOB", nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AdminSessions_RefreshTokenHash",
                table: "AdminSessions",
                column: "RefreshTokenHash",
                unique: true);
        }
    }
}
