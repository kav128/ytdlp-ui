using Microsoft.Extensions.DependencyInjection;
using Ytdlp.Ui.Domain;
using Ytdlp.Ui.Domain.Primitives;
using Ytdlp.Ui.Domain.ValueObjects;
using Ytdlp.Ui.Features.Downloads;
using Ytdlp.Ui.Infrastructure.Persistence;
using Ytdlp.Ui.Infrastructure.Persistence.Entities;

namespace Ytdlp.Ui.IntegrationTests.Data;

public static class DownloadData
{
    public static async Task<DownloadRecord> SeedAsync(
        IServiceProvider services,
        DownloadState state,
        DownloadState? resume = null,
        string url = "https://example.test/seed"
    )
    {
        var status = state switch
        {
            DownloadState.Queued                             => AttemptStatus.Pending,
            DownloadState.Failed                             => AttemptStatus.Failed,
            DownloadState.Completed or DownloadState.Deleted => AttemptStatus.Succeeded,
            DownloadState.Canceled                           => AttemptStatus.Canceled,
            _                                                => AttemptStatus.Running
        };

        var publication = state is DownloadState.Completed or DownloadState.CleaningUp
                       || resume is DownloadState.CleaningUp or DownloadState.Deleting
                              ? PublicationState.Available
                              : state == DownloadState.Deleted
                                  ? PublicationState.Deleted
                                  : PublicationState.None;

        var snapshot = new DownloadSnapshot(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            state,
            status,
            resume ?? (state == DownloadState.Queued ? DownloadState.Downloading : null),
            state == DownloadState.Failed ? resume : null,
            publication,
            Failure: state == DownloadState.Failed
                         ? new DownloadFailure("seed_failure", "Previous attempt failed")
                         : null
        );

        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        var record = DownloadRecord.Create(Download.Restore(snapshot), url, url, Guid.NewGuid(), now);
        record.Intent = state == DownloadState.Queued ? DownloadIntent.EnqueueAttempt : DownloadIntent.None;

        var initial = resume
                   ?? (state == DownloadState.Deleted
                           ? DownloadState.Deleting
                           : state is DownloadState.Queued or DownloadState.Completed or DownloadState.Canceled
                               ? DownloadState.Downloading
                               : state);

        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        database.AddRange(
            record,
            new DownloadAttemptRecord
            {
                Id = record.CurrentAttemptId,
                DownloadId = record.Id,
                Number = 1,
                Reason = AttemptReason.Initial,
                InitialState = initial,
                Status = status,
                QueuedAt = now,
                StartedAt = status == AttemptStatus.Pending ? null : now,
                FinishedAt = status is AttemptStatus.Pending or AttemptStatus.Running ? null : now,
                ErrorCode = record.ErrorCode,
                ErrorDetails = record.ErrorDetails
            },
            new StageExecutionRecord
            {
                Id = Guid.NewGuid(),
                DownloadId = record.Id,
                AttemptId = record.CurrentAttemptId,
                Stage = DownloadStages.FromState(initial),
                Status = status switch
                {
                    AttemptStatus.Pending => StageStatus.Pending, AttemptStatus.Running => StageStatus.Running,
                    AttemptStatus.Failed  => StageStatus.Failed, AttemptStatus.Canceled => StageStatus.Canceled,
                    _                     => StageStatus.Succeeded
                },
                ErrorCode = record.ErrorCode,
                ErrorDetails = record.ErrorDetails
            }
        );

        await database.SaveChangesAsync();

        return record;
    }
}
