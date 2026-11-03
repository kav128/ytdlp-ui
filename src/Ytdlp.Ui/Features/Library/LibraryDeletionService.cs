using Microsoft.EntityFrameworkCore;
using Ytdlp.Ui.Domain.Events;
using Ytdlp.Ui.Domain.Primitives;
using Ytdlp.Ui.Domain.ValueObjects;
using Ytdlp.Ui.Infrastructure.Persistence;
using Ytdlp.Ui.Infrastructure.Persistence.Entities;
using Ytdlp.Ui.Infrastructure.Storage;

namespace Ytdlp.Ui.Features.Library;

public sealed class LibraryDeletionService(AppDbContext database, ILibraryStorage storage, TimeProvider clock)
{
    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        var transfer = await database.Transfers.SingleOrDefaultAsync(item => item.ObjectKey == key, cancellationToken);
        DownloadRecord? record = null;

        if (transfer is not null)
        {
            record = await database.Downloads.SingleAsync(item => item.Id == transfer.DownloadId, cancellationToken);

            if (record.PublicationId != transfer.PublicationId
             || !record.RestoreAggregate().CanDeleteResult && record.State != DownloadState.Deleted)
                throw new StorageConflictException();
        }

        var file = await storage.FindAsync(key, cancellationToken);

        if (record?.State == DownloadState.Deleted)
        {
            if (file is not null) throw new StorageConflictException();

            return;
        }

        if (record is null)
        {
            if (file is not null) await storage.DeleteAsync(file, cancellationToken);

            return;
        }

        if (file is not null && file.PublicationId != record.PublicationId.ToString("D"))
            throw new StorageConflictException();

        var aggregate = record.RestoreAggregate();
        var attemptId = Guid.NewGuid();
        var transition = aggregate.Apply(new RequestDeleteResult(attemptId));

        if (!transition.IsAccepted) throw new StorageConflictException();

        var now = clock.GetUtcNow();
        record.Capture(aggregate, transition.Intent, now);

        var attempt = new DownloadAttemptRecord
        {
            Id = attemptId,
            DownloadId = record.Id,
            Number =
                (await database.Attempts.Where(item => item.DownloadId == record.Id)
                               .MaxAsync(item => (int?)item.Number, cancellationToken)
              ?? 0)
              + 1,
            Reason = AttemptReason.DeleteResult,
            InitialState = DownloadState.Deleting,
            Status = AttemptStatus.Running,
            QueuedAt = now,
            StartedAt = now
        };

        var stage = new StageExecutionRecord
        {
            Id = Guid.NewGuid(),
            DownloadId = record.Id,
            AttemptId = attemptId,
            Stage = ProcessingStage.DeleteResult,
            Status = StageStatus.Running,
            StartedAt = now
        };

        database.AddRange(attempt, stage);

        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            throw new StorageConflictException();
        }

        try
        {
            if (file is not null) await storage.DeleteAsync(file, cancellationToken);

            transition = aggregate.Apply(
                new ResultDeleted(attemptId, DownloadState.Deleting, record.PublicationId, true)
            );

            if (!transition.IsAccepted) throw new StorageConflictException();

            record.Capture(aggregate, transition.Intent, clock.GetUtcNow());
            attempt.Status = AttemptStatus.Succeeded;
            attempt.FinishedAt = clock.GetUtcNow();
            stage.Status = StageStatus.Succeeded;
            stage.FinishedAt = clock.GetUtcNow();
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (Exception error) when (error is StorageUnavailableException
                                            or StorageConflictException
                                            or OperationCanceledException)
        {
            var code = error is StorageConflictException ? "publication_changed" : "storage_unavailable";
            transition = aggregate.Apply(new StageFailed(attemptId, DownloadState.Deleting, new DownloadFailure(code)));
            record.Capture(aggregate, transition.Intent, clock.GetUtcNow());
            attempt.Status = AttemptStatus.Failed;
            attempt.FinishedAt = clock.GetUtcNow();
            attempt.ErrorCode = code;
            stage.Status = StageStatus.Failed;
            stage.FinishedAt = clock.GetUtcNow();
            stage.ErrorCode = code;
            using var persistence = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await database.SaveChangesAsync(persistence.Token);

            throw;
        }
    }
}
