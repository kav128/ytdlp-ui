using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ytdlp.Ui.Domain.Events;
using Ytdlp.Ui.Domain.Primitives;
using Ytdlp.Ui.Features.Library;
using Ytdlp.Ui.Infrastructure.Media;
using Ytdlp.Ui.Infrastructure.Persistence;
using Ytdlp.Ui.Infrastructure.Persistence.Entities;
using Ytdlp.Ui.Infrastructure.Storage;

namespace Ytdlp.Ui.Features.Downloads;

public sealed class MediaPipeline(
    IServiceScopeFactory scopes,
    MediaProcessor media,
    WorkspaceStorage workspace,
    ILibraryStorage storage,
    TimeProvider clock
)
{
    public async Task RunAsync(DownloadWorkItem work, CancellationToken cancellationToken)
    {
        workspace.Create(work.DownloadId, work.WorkspaceGeneration);
        var directory = workspace.DirectoryFor(work.DownloadId, work.WorkspaceGeneration);
        string Resolve(string relative) => workspace.Resolve(work.DownloadId, work.WorkspaceGeneration, relative);

        while (true)
        {
            var record = await ReadAsync(work, cancellationToken);

            var plan = record.MediaPlanJson is null
                           ? null
                           : JsonSerializer.Deserialize<MediaPlan>(record.MediaPlanJson);

            switch (record.State)
            {
                case DownloadState.Downloading:
                    if (plan is null)
                    {
                        plan = await media.PlanAsync(work.OriginalUrl, directory, cancellationToken);
                        await SavePlanAsync(work, plan, cancellationToken);
                    }

                    var downloaded = new long[plan.Tracks.Length];
                    var totals = plan.Tracks.Select(track => track.Size).ToArray();
                    var lastProgress = DateTimeOffset.MinValue;

                    for (var index = 0; index < plan.Tracks.Length; index++)
                    {
                        var track = plan.Tracks[index];
                        var position = index;
                        var artifact = await ReadArtifactAsync(track.ArtifactId, cancellationToken);

                        if (!artifact.IsReady)
                        {
                            await media.DownloadAsync(
                                work.OriginalUrl,
                                track,
                                Resolve(track.RelativePath),
                                directory,
                                async (bytes, total) =>
                                {
                                    downloaded[position] = bytes;
                                    totals[position] = total;
                                    var now = clock.GetUtcNow();

                                    if (now - lastProgress < TimeSpan.FromSeconds(1)) return;

                                    lastProgress = now;

                                    double? progress = totals.All(size => size > 0)
                                                           ? Math.Clamp(
                                                               100.0
                                                             * downloaded.Sum()
                                                             / totals.Sum(size => (double)size!.Value),
                                                               0,
                                                               100
                                                           )
                                                           : null;

                                    await ProgressAsync(work, DownloadState.Downloading, progress, cancellationToken);
                                },
                                cancellationToken
                            );

                            await ReadyArtifactAsync(work, track.ArtifactId, track.RelativePath, cancellationToken);
                        }

                        downloaded[position] = new FileInfo(Resolve(track.RelativePath)).Length;
                        totals[position] = downloaded[position];
                    }

                    await AdvanceAsync(
                        work,
                        DownloadState.Downloading,
                        plan.Tracks[0].ArtifactId,
                        false,
                        cancellationToken
                    );

                    break;
                case DownloadState.Preparing:
                    if (plan is null)
                        throw new MediaProcessingException("prepare_failed", "Saved media plan is missing.");

                    plan = await media.PrepareAsync(plan, Resolve, directory, cancellationToken);
                    await SavePreparedAsync(work, plan, cancellationToken);
                    await AdvanceAsync(work, DownloadState.Preparing, plan.OutputArtifactId, false, cancellationToken);

                    break;
                case DownloadState.Uploading:
                    if (plan is null)
                        throw new MediaProcessingException("upload_failed", "Saved media plan is missing.");

                    await PublishAsync(work, plan, cancellationToken);
                    await AdvanceAsync(work, DownloadState.Uploading, plan.OutputArtifactId, true, cancellationToken);

                    break;
                case DownloadState.CleaningUp:
                    workspace.Clean(work.DownloadId, work.WorkspaceGeneration);
                    await AdvanceAsync(work, DownloadState.CleaningUp, null, false, cancellationToken);

                    return;
                case DownloadState.Canceling:
                    await CancelAsync(work, cancellationToken);

                    return;
                case DownloadState.Deleting:
                    await DeleteAsync(work, cancellationToken);

                    return;
                default: return;
            }
        }
    }

    public async Task CancelAsync(DownloadWorkItem work, CancellationToken cancellationToken)
    {
        var current = await ReadAsync(work, cancellationToken);

        if (current.State != DownloadState.Canceling) return;

        using (var scope = scopes.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var transfer = await database.Transfers.AsNoTracking()
                                         .SingleOrDefaultAsync(
                                              item => item.DownloadId == work.DownloadId
                                                   && item.PublicationId == work.PublicationId,
                                              cancellationToken
                                          );

            if (transfer is not null)
            {
                var file = await storage.FindAsync(transfer.ObjectKey, cancellationToken);

                if (file is not null)
                {
                    if (!Matches(file, transfer)) throw new StorageConflictException();

                    await storage.DeleteAsync(file, cancellationToken);
                }
            }
        }

        workspace.Clean(work.DownloadId, work.WorkspaceGeneration);
        using var saving = scopes.CreateScope();
        var db = saving.ServiceProvider.GetRequiredService<AppDbContext>();
        var record = await CurrentAsync(db, work, cancellationToken);
        var aggregate = record.RestoreAggregate();
        var transition = aggregate.Apply(new CancellationSucceeded(work.AttemptId, true, true));

        if (!transition.IsAccepted) throw new OperationCanceledException("Attempt changed.");

        record.Capture(aggregate, transition.Intent, clock.GetUtcNow());
        await FinishAttemptAsync(db, record, StageStatus.Canceled, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task FailAsync(
        DownloadWorkItem work,
        string code,
        string? details,
        CancellationToken cancellationToken
    )
    {
        using var scope = scopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var record = await database.Downloads.SingleAsync(item => item.Id == work.DownloadId, cancellationToken);

        if (record.CurrentAttemptId != work.AttemptId || record.AttemptStatus != AttemptStatus.Running) return;

        var aggregate = record.RestoreAggregate();

        var transition = aggregate.Apply(
            new StageFailed(
                work.AttemptId,
                record.State,
                new(code, details),
                PublicationUncertain: record.State is DownloadState.Uploading or DownloadState.Deleting
                                   || record.State == DownloadState.Canceling
                                   && record.PublicationState is PublicationState.Available or PublicationState.Unknown
            )
        );

        if (!transition.IsAccepted) return;

        record.Capture(aggregate, transition.Intent, clock.GetUtcNow());
        await FinishAttemptAsync(database, record, StageStatus.Failed, cancellationToken);

        var stage = await database.Stages.SingleAsync(
                        item => item.AttemptId == work.AttemptId
                             && item.Stage == DownloadStages.FromState(record.FailedState!.Value),
                        cancellationToken
                    );

        stage.ErrorCode = code;
        stage.ErrorDetails = details;

        if (record.FailedState == DownloadState.Uploading)
        {
            var transfer = await database.Transfers.SingleOrDefaultAsync(
                               item => item.DownloadId == work.DownloadId && item.PublicationId == work.PublicationId,
                               cancellationToken
                           );

            if (transfer is not null && transfer.Status != TransferStatus.Published)
            {
                transfer.Status = TransferStatus.Failed;
                transfer.UpdatedAt = clock.GetUtcNow();
            }
        }

        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task DeleteAsync(DownloadWorkItem work, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var record = await CurrentAsync(database, work, cancellationToken);

        var transfer = await database.Transfers.SingleAsync(
                           item => item.DownloadId == work.DownloadId && item.PublicationId == work.PublicationId,
                           cancellationToken
                       );

        var file = await storage.FindAsync(transfer.ObjectKey, cancellationToken);

        if (file is not null)
        {
            if (!Matches(file, transfer)) throw new StorageConflictException();

            await storage.DeleteAsync(file, cancellationToken);
        }

        var aggregate = record.RestoreAggregate();

        var transition = aggregate.Apply(
            new ResultDeleted(work.AttemptId, DownloadState.Deleting, work.PublicationId, true)
        );

        if (!transition.IsAccepted) throw new OperationCanceledException("Attempt changed.");

        record.Capture(aggregate, transition.Intent, clock.GetUtcNow());
        await FinishAttemptAsync(database, record, StageStatus.Succeeded, cancellationToken);
        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task SavePlanAsync(DownloadWorkItem work, MediaPlan plan, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var record = await CurrentAsync(database, work, cancellationToken);

        if (record.State != DownloadState.Downloading) throw new OperationCanceledException("Attempt changed.");

        record.Title = plan.Title;
        record.MediaPlanJson = JsonSerializer.Serialize(plan);

        foreach (var track in plan.Tracks)
            database.Artifacts.Add(
                new DownloadArtifactRecord
                {
                    Id = track.ArtifactId,
                    DownloadId = work.DownloadId,
                    WorkspaceGeneration = work.WorkspaceGeneration,
                    Kind = track.Kind,
                    RelativePath = track.RelativePath
                }
            );

        if (plan.Tracks.Length > 1)
            database.Artifacts.Add(
                new DownloadArtifactRecord
                {
                    Id = plan.OutputArtifactId,
                    DownloadId = work.DownloadId,
                    WorkspaceGeneration = work.WorkspaceGeneration,
                    Kind = ArtifactKind.Prepared,
                    RelativePath = plan.OutputPath
                }
            );

        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task ReadyArtifactAsync(
        DownloadWorkItem work,
        Guid artifactId,
        string relative,
        CancellationToken cancellationToken
    )
    {
        var path = workspace.Resolve(work.DownloadId, work.WorkspaceGeneration, relative);
        var size = new FileInfo(path).Length;
        var hash = await MediaProcessor.Sha256Async(path, cancellationToken);
        using var scope = scopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await CurrentAsync(database, work, cancellationToken);

        var artifact = await database.Artifacts.SingleAsync(
                           item => item.Id == artifactId
                                && item.DownloadId == work.DownloadId
                                && item.WorkspaceGeneration == work.WorkspaceGeneration,
                           cancellationToken
                       );

        artifact.RelativePath = relative;
        artifact.Size = size;
        artifact.Sha256 = hash;
        artifact.IsReady = true;
        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task SavePreparedAsync(DownloadWorkItem work, MediaPlan plan, CancellationToken cancellationToken)
    {
        await ReadyArtifactAsync(work, plan.OutputArtifactId, plan.OutputPath, cancellationToken);
        using var scope = scopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var record = await CurrentAsync(database, work, cancellationToken);
        record.MediaPlanJson = JsonSerializer.Serialize(plan);
        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task PublishAsync(DownloadWorkItem work, MediaPlan plan, CancellationToken cancellationToken)
    {
        var artifact = await ReadArtifactAsync(plan.OutputArtifactId, cancellationToken);
        var path = workspace.Resolve(work.DownloadId, work.WorkspaceGeneration, artifact.RelativePath);

        if (!artifact.IsReady
         || !File.Exists(path)
         || new FileInfo(path).Length != artifact.Size
         || await MediaProcessor.Sha256Async(path, cancellationToken) != artifact.Sha256)
            throw new MediaProcessingException("upload_failed", "Prepared artifact is missing or changed.");

        StorageTransferRecord transfer;

        using (var scope = scopes.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var record = await CurrentAsync(database, work, cancellationToken);

            if (record.State != DownloadState.Uploading) throw new OperationCanceledException("Attempt changed.");

            transfer = await database.Transfers.SingleOrDefaultAsync(
                           item => item.DownloadId == work.DownloadId && item.PublicationId == work.PublicationId,
                           cancellationToken
                       )
                    ?? new StorageTransferRecord
                       {
                           Id = Guid.NewGuid(),
                           DownloadId = work.DownloadId,
                           ArtifactId = artifact.Id,
                           PublicationId = work.PublicationId,
                           ObjectKey =
                               $"downloads/{work.DownloadId:D}/{work.PublicationId:D}/{PublicationName(plan.Title, plan.OutputExtension)}",
                           ExpectedSize = artifact.Size!.Value,
                           Sha256 = artifact.Sha256,
                           CreatedAt = clock.GetUtcNow()
                       };

            if (database.Entry(transfer).State == EntityState.Detached) database.Transfers.Add(transfer);
            transfer.Status = TransferStatus.Uploading;
            transfer.UpdatedAt = clock.GetUtcNow();
            await database.SaveChangesAsync(cancellationToken);
        }

        var existing = await storage.FindAsync(transfer.ObjectKey, cancellationToken);

        if (existing is not null && !Matches(existing, transfer)) throw new StorageConflictException();

        if (existing is null)
        {
            await using var upload = await storage.BeginPublicationAsync(
                                         new(
                                             work.DownloadId,
                                             work.PublicationId,
                                             transfer.ObjectKey,
                                             transfer.ExpectedSize,
                                             transfer.Sha256!
                                         ),
                                         async (id, token) =>
                                         {
                                             using var scope = scopes.CreateScope();
                                             var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                                             var saved = await database.Transfers.SingleAsync(
                                                             item => item.Id == transfer.Id,
                                                             token
                                                         );

                                             saved.MultipartUploadId = id;
                                             saved.UpdatedAt = clock.GetUtcNow();
                                             await database.SaveChangesAsync(token);
                                         },
                                         cancellationToken
                                     );

            await using var file = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan
            );

            await upload.WriteAsync(file, cancellationToken);
            await upload.CompleteAsync(cancellationToken);
        }

        var confirmed = await storage.FindAsync(transfer.ObjectKey, cancellationToken);

        if (confirmed is null || !Matches(confirmed, transfer)) throw new StorageUnavailableException();
    }

    private async Task AdvanceAsync(
        DownloadWorkItem work,
        DownloadState stage,
        Guid? artifactId,
        bool published,
        CancellationToken cancellationToken
    )
    {
        using var scope = scopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var record = await CurrentAsync(database, work, cancellationToken);
        var aggregate = record.RestoreAggregate();

        var transition = aggregate.Apply(
            new StageSucceeded(work.AttemptId, stage, published, stage == DownloadState.CleaningUp)
        );

        if (!transition.IsAccepted) throw new OperationCanceledException("Attempt changed.");

        record.Capture(aggregate, transition.Intent, clock.GetUtcNow());

        var execution = await database.Stages.SingleAsync(
                            item => item.AttemptId == work.AttemptId && item.Stage == DownloadStages.FromState(stage),
                            cancellationToken
                        );

        execution.Status = StageStatus.Succeeded;
        execution.FinishedAt = clock.GetUtcNow();
        execution.Progress = 100;
        execution.ArtifactId = artifactId;

        if (published)
        {
            var transfer = await database.Transfers.SingleAsync(
                               item => item.DownloadId == work.DownloadId && item.PublicationId == work.PublicationId,
                               cancellationToken
                           );

            transfer.Status = TransferStatus.Published;
            transfer.UpdatedAt = clock.GetUtcNow();
        }

        if (aggregate.State == DownloadState.Completed)
            await FinishAttemptAsync(database, record, StageStatus.Succeeded, cancellationToken);
        else
            database.Stages.Add(
                new StageExecutionRecord
                {
                    Id = Guid.NewGuid(),
                    DownloadId = work.DownloadId,
                    AttemptId = work.AttemptId,
                    Stage = DownloadStages.FromState(aggregate.State),
                    Status = StageStatus.Running,
                    StartedAt = clock.GetUtcNow()
                }
            );

        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task ProgressAsync(
        DownloadWorkItem work,
        DownloadState stage,
        double? progress,
        CancellationToken cancellationToken
    )
    {
        using var scope = scopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (!await database.Downloads.AnyAsync(
                 item => item.Id == work.DownloadId && item.CurrentAttemptId == work.AttemptId && item.State == stage,
                 cancellationToken
             )) return;

        var execution = await database.Stages.SingleAsync(
                            item => item.AttemptId == work.AttemptId && item.Stage == DownloadStages.FromState(stage),
                            cancellationToken
                        );

        execution.Progress = progress;
        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task FinishAttemptAsync(
        AppDbContext database,
        DownloadRecord record,
        StageStatus stageStatus,
        CancellationToken cancellationToken
    )
    {
        var attempt = await database.Attempts.SingleAsync(
                          item => item.Id == record.CurrentAttemptId,
                          cancellationToken
                      );

        attempt.Status = record.AttemptStatus;
        attempt.FinishedAt = clock.GetUtcNow();
        attempt.ErrorCode = record.ErrorCode;
        attempt.ErrorDetails = record.ErrorDetails;

        foreach (var stage in await database.Stages
                                            .Where(item => item.AttemptId == attempt.Id
                                                        && (item.Status == StageStatus.Running
                                                         || item.Status == StageStatus.Pending)
                                             )
                                            .ToArrayAsync(cancellationToken))
        {
            stage.Status = stageStatus;
            stage.FinishedAt = clock.GetUtcNow();
        }
    }

    private async Task<DownloadRecord> ReadAsync(DownloadWorkItem work, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();

        return await CurrentAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), work, cancellationToken);
    }

    private async Task<DownloadArtifactRecord> ReadArtifactAsync(Guid id, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                          .Artifacts.AsNoTracking()
                          .SingleAsync(item => item.Id == id, cancellationToken);
    }

    private static async Task<DownloadRecord> CurrentAsync(
        AppDbContext database,
        DownloadWorkItem work,
        CancellationToken cancellationToken
    )
    {
        var record = await database.Downloads.SingleAsync(item => item.Id == work.DownloadId, cancellationToken);

        if (record.CurrentAttemptId != work.AttemptId
         || record.PublicationId != work.PublicationId
         || record.WorkspaceGeneration != work.WorkspaceGeneration)
            throw new OperationCanceledException("Attempt changed.");

        return record;
    }

    private static bool Matches(StoredObject file, StorageTransferRecord transfer) => file.Size == transfer.ExpectedSize
                                                                                   && file.PublicationId
                                                                                   == transfer.PublicationId.ToString(
                                                                                          "D"
                                                                                      )
                                                                                   && file.DownloadId
                                                                                   == transfer.DownloadId.ToString("D")
                                                                                   && file.Sha256 == transfer.Sha256;

    private static string PublicationName(string title, string extension)
    {
        var name = new StringBuilder();
        var size = 0;

        foreach (var rune in LibraryFile.SafeName(title).EnumerateRunes())
        {
            if (size + rune.Utf8SequenceLength > 200) break;

            name.Append(rune);
            size += rune.Utf8SequenceLength;
        }

        return (name.Length == 0 ? "video" : name.ToString()) + "." + extension;
    }
}
