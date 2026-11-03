using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ytdlp.Ui.Domain.Events;
using Ytdlp.Ui.Domain.Primitives;
using Ytdlp.Ui.Infrastructure.Persistence;
using Ytdlp.Ui.Infrastructure.Persistence.Entities;
using Ytdlp.Ui.Infrastructure.Storage;

namespace Ytdlp.Ui.Features.Library;

public sealed record LibraryDeletionResult(Guid? DownloadId = null);

public sealed class LibraryDeletionService(AppDbContext database, ILibraryStorage storage, TimeProvider clock)
{
    public async Task<LibraryDeletionResult> DeleteAsync(string key, CancellationToken cancellationToken)
    {
        var transfer = await database.Transfers.SingleOrDefaultAsync(item => item.ObjectKey == key, cancellationToken);

        if (transfer is null)
        {
            var external = await storage.FindAsync(key, cancellationToken);
            if (external is not null) await storage.DeleteAsync(external, cancellationToken);

            return new();
        }

        var record = await database.Downloads.SingleAsync(item => item.Id == transfer.DownloadId, cancellationToken);

        if (record.PublicationId != transfer.PublicationId) throw new StorageConflictException();

        if (record.State == DownloadState.Deleting
         || record.State == DownloadState.Queued && record.ResumeState == DownloadState.Deleting)
            return new(record.Id);

        if (record.State == DownloadState.Deleted)
        {
            if (await storage.FindAsync(key, cancellationToken) is not null) throw new StorageConflictException();

            return new();
        }

        var aggregate = record.RestoreAggregate();
        var attemptId = Guid.NewGuid();
        var transition = aggregate.Apply(new RequestDeleteResult(attemptId));

        if (!transition.IsAccepted) throw new StorageConflictException();

        var now = clock.GetUtcNow();
        record.Capture(aggregate, transition.Intent, now);

        database.Attempts.Add(
            new DownloadAttemptRecord
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
                QueuedAt = now
            }
        );

        database.Stages.Add(
            new StageExecutionRecord
            {
                Id = Guid.NewGuid(),
                DownloadId = record.Id,
                AttemptId = attemptId,
                Stage = ProcessingStage.DeleteResult,
                Status = StageStatus.Pending
            }
        );

        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException error) when (error is DbUpdateConcurrencyException
                                           || error.InnerException is PostgresException
                                              {
                                                  SqlState: PostgresErrorCodes.UniqueViolation
                                              })
        {
            throw new StorageConflictException();
        }

        return new(record.Id);
    }
}
