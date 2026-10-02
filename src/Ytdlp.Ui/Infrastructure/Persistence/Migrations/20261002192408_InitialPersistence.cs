using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ytdlp.Ui.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdminSessions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    IssuedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RevokedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminSessions", x => x.Id);
                    table.CheckConstraint("CK_Sessions_Expiration", "\"ExpiresAt\" > \"IssuedAt\"");
                });

            migrationBuilder.CreateTable(
                name: "Downloads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OriginalUrl = table.Column<string>(type: "TEXT", nullable: false),
                    NormalizedUrl = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    CurrentAttemptId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PublicationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceGeneration = table.Column<Guid>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    AttemptStatus = table.Column<string>(type: "TEXT", nullable: false),
                    ResumeState = table.Column<string>(type: "TEXT", nullable: true),
                    FailedState = table.Column<string>(type: "TEXT", nullable: true),
                    PublicationState = table.Column<string>(type: "TEXT", nullable: false),
                    CancelRequested = table.Column<bool>(type: "INTEGER", nullable: false),
                    Intent = table.Column<string>(type: "TEXT", nullable: false),
                    ErrorCode = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorDetails = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Downloads", x => x.Id);
                    table.CheckConstraint("CK_Downloads_Version", "\"Version\" >= 1");
                });

            migrationBuilder.CreateTable(
                name: "DownloadArtifacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DownloadId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceGeneration = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    RelativePath = table.Column<string>(type: "TEXT", nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: true),
                    Sha256 = table.Column<string>(type: "TEXT", nullable: true),
                    IsReady = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DownloadArtifacts", x => x.Id);
                    table.UniqueConstraint("AK_DownloadArtifacts_DownloadId_Id", x => new { x.DownloadId, x.Id });
                    table.CheckConstraint("CK_Artifacts_Size", "\"Size\" IS NULL OR \"Size\" >= 0");
                    table.ForeignKey(
                        name: "FK_DownloadArtifacts_Downloads_DownloadId",
                        column: x => x.DownloadId,
                        principalTable: "Downloads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DownloadAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DownloadId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<int>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    InitialState = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    QueuedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    FinishedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ErrorCode = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorDetails = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DownloadAttempts", x => x.Id);
                    table.UniqueConstraint("AK_DownloadAttempts_DownloadId_Id", x => new { x.DownloadId, x.Id });
                    table.CheckConstraint("CK_Attempts_Number", "\"Number\" >= 1");
                    table.ForeignKey(
                        name: "FK_DownloadAttempts_Downloads_DownloadId",
                        column: x => x.DownloadId,
                        principalTable: "Downloads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StorageTransfers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DownloadId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PublicationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ObjectKey = table.Column<string>(type: "TEXT", nullable: false),
                    MultipartUploadId = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    ExpectedSize = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorageTransfers", x => x.Id);
                    table.CheckConstraint("CK_Transfers_Size", "\"ExpectedSize\" >= 0");
                    table.ForeignKey(
                        name: "FK_StorageTransfers_DownloadArtifacts_DownloadId_ArtifactId",
                        columns: x => new { x.DownloadId, x.ArtifactId },
                        principalTable: "DownloadArtifacts",
                        principalColumns: new[] { "DownloadId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StageExecutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DownloadId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AttemptId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Stage = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    FinishedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ReusedFromStageId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ArtifactId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Progress = table.Column<double>(type: "REAL", nullable: true),
                    ErrorCode = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorDetails = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StageExecutions", x => x.Id);
                    table.UniqueConstraint("AK_StageExecutions_DownloadId_Id", x => new { x.DownloadId, x.Id });
                    table.CheckConstraint("CK_Stages_Progress", "\"Progress\" IS NULL OR (\"Progress\" >= 0 AND \"Progress\" <= 100)");
                    table.ForeignKey(
                        name: "FK_StageExecutions_DownloadArtifacts_DownloadId_ArtifactId",
                        columns: x => new { x.DownloadId, x.ArtifactId },
                        principalTable: "DownloadArtifacts",
                        principalColumns: new[] { "DownloadId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StageExecutions_DownloadAttempts_DownloadId_AttemptId",
                        columns: x => new { x.DownloadId, x.AttemptId },
                        principalTable: "DownloadAttempts",
                        principalColumns: new[] { "DownloadId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StageExecutions_StageExecutions_DownloadId_ReusedFromStageId",
                        columns: x => new { x.DownloadId, x.ReusedFromStageId },
                        principalTable: "StageExecutions",
                        principalColumns: new[] { "DownloadId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdminSessions_ExpiresAt",
                table: "AdminSessions",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_DownloadArtifacts_DownloadId_WorkspaceGeneration_RelativePath",
                table: "DownloadArtifacts",
                columns: new[] { "DownloadId", "WorkspaceGeneration", "RelativePath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Attempts_OneActive",
                table: "DownloadAttempts",
                column: "DownloadId",
                unique: true,
                filter: "\"Status\" IN ('Pending', 'Running')");

            migrationBuilder.CreateIndex(
                name: "IX_DownloadAttempts_DownloadId_Number",
                table: "DownloadAttempts",
                columns: new[] { "DownloadId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DownloadAttempts_Status_QueuedAt_Id",
                table: "DownloadAttempts",
                columns: new[] { "Status", "QueuedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Downloads_NormalizedUrl",
                table: "Downloads",
                column: "NormalizedUrl",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StageExecutions_AttemptId_Stage",
                table: "StageExecutions",
                columns: new[] { "AttemptId", "Stage" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StageExecutions_DownloadId_ArtifactId",
                table: "StageExecutions",
                columns: new[] { "DownloadId", "ArtifactId" });

            migrationBuilder.CreateIndex(
                name: "IX_StageExecutions_DownloadId_AttemptId",
                table: "StageExecutions",
                columns: new[] { "DownloadId", "AttemptId" });

            migrationBuilder.CreateIndex(
                name: "IX_StageExecutions_DownloadId_ReusedFromStageId",
                table: "StageExecutions",
                columns: new[] { "DownloadId", "ReusedFromStageId" });

            migrationBuilder.CreateIndex(
                name: "IX_StorageTransfers_DownloadId_ArtifactId",
                table: "StorageTransfers",
                columns: new[] { "DownloadId", "ArtifactId" });

            migrationBuilder.CreateIndex(
                name: "IX_StorageTransfers_DownloadId_PublicationId",
                table: "StorageTransfers",
                columns: new[] { "DownloadId", "PublicationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StorageTransfers_ObjectKey",
                table: "StorageTransfers",
                column: "ObjectKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdminSessions");

            migrationBuilder.DropTable(
                name: "StageExecutions");

            migrationBuilder.DropTable(
                name: "StorageTransfers");

            migrationBuilder.DropTable(
                name: "DownloadAttempts");

            migrationBuilder.DropTable(
                name: "DownloadArtifacts");

            migrationBuilder.DropTable(
                name: "Downloads");
        }
    }
}
