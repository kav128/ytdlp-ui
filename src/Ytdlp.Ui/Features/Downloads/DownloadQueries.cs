using Microsoft.EntityFrameworkCore;
using Ytdlp.Ui.Domain.Primitives;
using Ytdlp.Ui.Infrastructure.Persistence;
using Ytdlp.Ui.Infrastructure.Persistence.Entities;

namespace Ytdlp.Ui.Features.Downloads;

public sealed class DownloadQueries(AppDbContext database)
{
    public async Task<DownloadView[]> ListAsync(CancellationToken cancellationToken) =>
        await ReadAsync(null, cancellationToken);

    public async Task<DownloadView?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        (await ReadAsync(id, cancellationToken)).SingleOrDefault();

    private async Task<DownloadView[]> ReadAsync(Guid? id, CancellationToken cancellationToken)
    {
        await using var snapshot = await database.Database.BeginTransactionAsync(
                                       System.Data.IsolationLevel.RepeatableRead,
                                       cancellationToken
                                   );

        var downloads = await database.Downloads.AsNoTracking()
                                      .Where(record => id == null || record.Id == id)
                                      .OrderByDescending(record => record.CreatedAt)
                                      .ThenBy(record => record.Id)
                                      .ToArrayAsync(cancellationToken);

        var attempts = (await database.Attempts.AsNoTracking()
                                      .Where(record => id == null || record.DownloadId == id)
                                      .OrderBy(record => record.Number)
                                      .ToArrayAsync(cancellationToken)).ToLookup(record => record.DownloadId);

        var stages = (await database.Stages.AsNoTracking()
                                    .Where(record => id == null || record.DownloadId == id)
                                    .ToArrayAsync(cancellationToken)).OrderBy(record => record.Stage)
                                                                     .ToLookup(record => record.AttemptId);

        return downloads.Select(record => new DownloadView(
                                    record.Id,
                                    record.OriginalUrl,
                                    record.Title,
                                    record.State.ToString(),
                                    record.ResumeState?.ToString(),
                                    stages[record.CurrentAttemptId]
                                       .LastOrDefault(stage => stage.Status == StageStatus.Running)
                                      ?.Progress,
                                    record.CreatedAt,
                                    record.ErrorCode,
                                    record.ErrorDetails,
                                    record.RestoreAggregate()
                                          .AllowedActions.Select(action => action.ToString().ToLowerInvariant())
                                          .ToArray(),
                                    attempts[record.Id]
                                       .Select(attempt => new DownloadAttemptView(
                                                   attempt.Number,
                                                   State(attempt, record),
                                                   attempt.QueuedAt,
                                                   attempt.ErrorDetails,
                                                   attempt.Id,
                                                   attempt.Reason.ToString(),
                                                   attempt.Status.ToString(),
                                                   attempt.InitialState.ToString(),
                                                   attempt.StartedAt,
                                                   attempt.FinishedAt,
                                                   attempt.ErrorCode,
                                                   stages[attempt.Id]
                                                      .Select(stage => new DownloadStageView(
                                                                  stage.Id,
                                                                  stage.Stage.ToString(),
                                                                  stage.Status.ToString(),
                                                                  stage.StartedAt,
                                                                  stage.FinishedAt,
                                                                  stage.Progress,
                                                                  stage.ErrorCode,
                                                                  stage.ErrorDetails,
                                                                  stage.ArtifactId,
                                                                  stage.ReusedFromStageId
                                                              )
                                                       )
                                                      .ToArray()
                                               )
                                        )
                                       .ToArray(),
                                    record.CurrentAttemptId,
                                    record.PublicationId,
                                    record.PublicationState.ToString(),
                                    record.FailedState?.ToString(),
                                    record.CancelRequested,
                                    record.Version
                                )
                         )
                        .ToArray();
    }

    private static string State(DownloadAttemptRecord attempt, DownloadRecord record) => attempt.Status switch
    {
        AttemptStatus.Pending => DownloadState.Queued.ToString(),
        AttemptStatus.Running when attempt.Id == record.CurrentAttemptId => record.State.ToString(),
        AttemptStatus.Succeeded => attempt.InitialState == DownloadState.Deleting ? "Deleted" : "Completed",
        AttemptStatus.Canceled => "Canceled",
        AttemptStatus.Failed => "Failed",
        _ => attempt.Status.ToString()
    };
}
