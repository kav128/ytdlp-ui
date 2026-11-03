using System.Data;
using Microsoft.EntityFrameworkCore;
using Ytdlp.Ui.Domain.Events;
using Ytdlp.Ui.Domain.Primitives;
using Ytdlp.Ui.Infrastructure.Persistence;

namespace Ytdlp.Ui.Features.Downloads;

public sealed record DownloadWorkItem(
    Guid DownloadId,
    Guid AttemptId,
    Guid PublicationId,
    Guid WorkspaceGeneration,
    string OriginalUrl,
    DownloadState State
);

public sealed class DownloadQueue(AppDbContext database, TimeProvider clock)
{
    public async Task<DownloadWorkItem?> TryClaimAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(
                                          IsolationLevel.ReadCommitted,
                                          cancellationToken
                                      );

        // The transaction-level global guard serializes claimers across scopes. ReadCommitted
        // gives the next SELECT a fresh snapshot after the lock; no external I/O is enclosed.
        await database.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(82937614001);", cancellationToken);

        if (await database.Attempts.AnyAsync(
                attempt => attempt.Status == AttemptStatus.Running
                        && attempt.StartedAt != null,
                cancellationToken
            )) return null;

        var attempt = await database.Attempts.Where(item => item.Status == AttemptStatus.Pending
                                                         || item.Status == AttemptStatus.Running
                                                         && item.StartedAt == null
                                     )
                                    .OrderBy(item => item.QueuedAt)
                                    .ThenBy(item => item.Id)
                                    .FirstOrDefaultAsync(cancellationToken);

        if (attempt is null) return null;

        var record = await database.Downloads.SingleAsync(item => item.Id == attempt.DownloadId, cancellationToken);

        if (record.CurrentAttemptId != attempt.Id) throw new InvalidOperationException("Queue attempt is not current.");

        var aggregate = record.RestoreAggregate();

        if (aggregate.State == DownloadState.Queued)
        {
            var start = aggregate.Apply(new StartAttempt(attempt.Id));

            if (!start.IsAccepted) throw new InvalidOperationException("Queued attempt could not be started.");

            record.Capture(aggregate, start.Intent, clock.GetUtcNow());
        }
        else if (aggregate.State != DownloadState.Canceling || record.Intent != DownloadIntent.StopAndCleanUp)
            throw new InvalidOperationException("Unexpected pending queue operation.");

        attempt.StartedAt = clock.GetUtcNow();
        attempt.Status = AttemptStatus.Running;
        var stageName = DownloadStages.FromState(aggregate.State);

        var stage = await database.Stages.SingleAsync(
                        item => item.AttemptId == attempt.Id && item.Stage == stageName,
                        cancellationToken
                    );

        stage.Status = StageStatus.Running;
        stage.StartedAt = clock.GetUtcNow();

        try
        {
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            database.ChangeTracker.Clear();

            return null;
        }

        return new(
            record.Id,
            attempt.Id,
            record.PublicationId,
            record.WorkspaceGeneration,
            record.OriginalUrl,
            record.State
        );
    }
}
