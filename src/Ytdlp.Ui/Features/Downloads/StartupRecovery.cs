using Microsoft.EntityFrameworkCore;
using Ytdlp.Ui.Domain.Events;
using Ytdlp.Ui.Domain.Primitives;
using Ytdlp.Ui.Infrastructure.Persistence;
using Ytdlp.Ui.Infrastructure.Persistence.Entities;

namespace Ytdlp.Ui.Features.Downloads;

public sealed class StartupRecovery(AppDbContext database, TimeProvider clock)
{
    public async Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);

        var interrupted = await database.Downloads.Where(item => item.AttemptStatus == AttemptStatus.Running
                                                              && item.State != DownloadState.Queued
                                         )
                                        .ToArrayAsync(cancellationToken);

        foreach (var record in interrupted)
        {
            var aggregate = record.RestoreAggregate();
            var state = record.State;
            var transition = aggregate.Apply(new ProcessInterrupted(record.CurrentAttemptId, state));

            if (!transition.IsAccepted)
                throw new InvalidOperationException("An interrupted operation could not be recovered.");

            record.Capture(aggregate, transition.Intent, clock.GetUtcNow());

            var attempt = await database.Attempts.SingleAsync(
                              item => item.Id == record.CurrentAttemptId,
                              cancellationToken
                          );

            attempt.Status = AttemptStatus.Failed;
            attempt.FinishedAt = clock.GetUtcNow();
            attempt.ErrorCode = "interrupted";
            attempt.ErrorDetails = null;

            var stage = await database.Stages.SingleOrDefaultAsync(
                            item => item.AttemptId == attempt.Id
                                 && item.Stage == DownloadStages.FromState(state),
                            cancellationToken
                        );

            if (stage is null)
            {
                stage = new StageExecutionRecord
                {
                    Id = Guid.NewGuid(),
                    DownloadId = record.Id,
                    AttemptId = attempt.Id,
                    Stage = DownloadStages.FromState(state)
                };

                database.Stages.Add(stage);
            }

            foreach (var active in await database.Stages.Where(item => item.AttemptId == attempt.Id
                                                                    && (item.Status == StageStatus.Running
                                                                     || item.Status == StageStatus.Pending)
                                                  )
                                                 .ToArrayAsync(cancellationToken))
            {
                active.Status = StageStatus.Failed;
                active.FinishedAt = clock.GetUtcNow();
            }

            stage.Status = StageStatus.Failed;
            stage.FinishedAt = clock.GetUtcNow();
            stage.ErrorCode = "interrupted";
            stage.ErrorDetails = attempt.ErrorDetails;
        }

        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
