using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ytdlp.Ui.Domain.Primitives;
using Ytdlp.Ui.Infrastructure.Media;
using Ytdlp.Ui.Infrastructure.Persistence;
using Ytdlp.Ui.Infrastructure.Persistence.Entities;
using Ytdlp.Ui.Infrastructure.Storage;

namespace Ytdlp.Ui.Features.Downloads;

public sealed record RetryPreparation(DownloadState State, string? Explanation = null);

public sealed class DownloadRetryPlanner(AppDbContext database, ArtifactIntegrity integrity, ILibraryStorage storage)
{
    public async Task<RetryPreparation> PrepareAsync(DownloadRecord record, CancellationToken cancellationToken)
    {
        var resume = record.ResumeState ?? DownloadState.Downloading;

        if (record.State != DownloadState.Failed
         || resume is not (DownloadState.Downloading or DownloadState.Preparing or DownloadState.Uploading))
            return new(resume);

        if (resume == DownloadState.Uploading)
        {
            var transfer = await database.Transfers.SingleOrDefaultAsync(
                               item => item.DownloadId == record.Id
                                    && item.PublicationId == record.PublicationId,
                               cancellationToken
                           );

            if (transfer is not null)
            {
                var file = await storage.FindAsync(transfer.ObjectKey, cancellationToken);

                if (file is not null)
                {
                    if (!MediaPipeline.Matches(file, transfer)) throw new StorageConflictException();

                    return new(resume, "The existing publication will be reconciled before any upload.");
                }
            }
        }

        var plan = record.MediaPlanJson is null ? null : JsonSerializer.Deserialize<MediaPlan>(record.MediaPlanJson);

        if (plan is null)
            return new(
                DownloadState.Downloading,
                resume == DownloadState.Downloading ? null : "The media plan is missing; downloading must be restored."
            );

        var artifacts = await database.Artifacts.Where(item => item.DownloadId == record.Id
                                                            && item.WorkspaceGeneration == record.WorkspaceGeneration
                                       )
                                      .ToDictionaryAsync(item => item.Id, cancellationToken);

        var valid = new HashSet<Guid>();

        foreach (var artifact in artifacts.Values)
            if (await integrity.IsValidAsync(artifact, cancellationToken)) valid.Add(artifact.Id);
            else if (artifact.IsReady) artifact.IsReady = false;

        var target = resume;

        if (resume == DownloadState.Uploading && !valid.Contains(plan.OutputArtifactId))
            target = plan.Tracks.All(track => valid.Contains(track.ArtifactId))
                         ? DownloadState.Preparing
                         : DownloadState.Downloading;

        if (resume == DownloadState.Preparing
         && !valid.Contains(plan.OutputArtifactId)
         && !plan.Tracks.All(track => valid.Contains(track.ArtifactId))) target = DownloadState.Downloading;

        return new(
            target,
            target != resume
                ? "A required artifact is missing or has a different size/SHA-256; the earliest necessary stage will be restored."
                : null
        );
    }
}
