using Npgsql;
using Microsoft.EntityFrameworkCore;
using Ytdlp.Ui.Domain;
using Ytdlp.Ui.Domain.Events;
using Ytdlp.Ui.Domain.Primitives;
using Ytdlp.Ui.Infrastructure.Persistence;
using Ytdlp.Ui.Infrastructure.Persistence.Entities;
using Ytdlp.Ui.Infrastructure.Storage;

namespace Ytdlp.Ui.Features.Downloads;

public sealed class DownloadCommands(
    AppDbContext database,
    DownloadQueries queries,
    ILibraryStorage storage,
    DownloadRetryPlanner retryPlanner,
    TimeProvider clock
)
{
    public async Task<DownloadCommandResult> CreateAsync(string? original, CancellationToken cancellationToken)
    {
        if (!SourceUrl.TryNormalize(original, out var normalized)) return new(Status: 400, Code: "invalid_url");

        var hash = DownloadRecord.ComputeNormalizedUrlHash(normalized);

        var existing = await database.Downloads.SingleOrDefaultAsync(
                           record => record.NormalizedUrlHash == hash,
                           cancellationToken
                       );

        if (existing is not null)
            return StringComparer.Ordinal.Equals(existing.NormalizedUrl, normalized)
                       ? await RecreateAsync(existing, cancellationToken)
                       : HashConflict();

        var now = clock.GetUtcNow();
        var aggregate = Download.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var record = DownloadRecord.Create(aggregate, original!, normalized, Guid.NewGuid(), now);
        database.Downloads.Add(record);
        AddAttempt(record, 1, AttemptReason.Initial, DownloadState.Downloading, now);

        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException error) when (IsUniqueConstraint(error))
        {
            database.ChangeTracker.Clear();

            var winner = await database.Downloads.SingleAsync(
                             item => item.NormalizedUrlHash == hash,
                             cancellationToken
                         );

            return StringComparer.Ordinal.Equals(winner.NormalizedUrl, normalized)
                       ? Duplicate(winner.Id)
                       : HashConflict();
        }

        return new(await queries.GetAsync(record.Id, cancellationToken), 201);
    }

    public async Task<DownloadCommandResult> RetryAsync(Guid id, CancellationToken cancellationToken)
    {
        var record = await database.Downloads.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);

        if (record is null) return new(Status: 404, Code: "not_found");

        if (record.State == DownloadState.Completed)
        {
            var absent = await ConfirmPublicationAbsentAsync(record, cancellationToken);

            if (!absent) return InvalidState(record.Id);
        }

        return await EnqueueRetryAsync(record, cancellationToken);
    }

    public async Task<DownloadCommandResult> CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        var record = await database.Downloads.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);

        if (record is null) return new(Status: 404, Code: "not_found");

        var aggregate = record.RestoreAggregate();
        var before = aggregate.Snapshot;

        var transition =
            aggregate.Apply(new RequestCancel(record.State == DownloadState.Failed ? Guid.NewGuid() : null));

        if (!transition.IsAccepted) return InvalidState(id);
        if (transition.Snapshot == before) return new(await queries.GetAsync(id, cancellationToken), 202);

        var now = clock.GetUtcNow();
        record.Capture(aggregate, transition.Intent, now);

        if (aggregate.AttemptId != before.AttemptId)
        {
            var number = await NextAttemptNumberAsync(id, cancellationToken);
            AddAttempt(record, number, AttemptReason.Cancellation, DownloadState.Canceling, now);
        }
        else
        {
            var attempt = await database.Attempts.SingleAsync(
                              item => item.Id == record.CurrentAttemptId,
                              cancellationToken
                          );

            attempt.Status = aggregate.AttemptStatus;

            var cancelStage = await database.Stages.SingleOrDefaultAsync(
                                  item => item.AttemptId == attempt.Id && item.Stage == ProcessingStage.CancelCleanup,
                                  cancellationToken
                              );

            if (cancelStage is null) AddStage(record, DownloadState.Canceling);
        }

        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException error) when (error is DbUpdateConcurrencyException || IsUniqueConstraint(error))
        {
            database.ChangeTracker.Clear();
            var current = await database.Downloads.SingleAsync(item => item.Id == id, cancellationToken);

            return current.CancelRequested ? new(await queries.GetAsync(id, cancellationToken), 202) : InvalidState(id);
        }

        return new(await queries.GetAsync(id, cancellationToken), 202);
    }

    private async Task<DownloadCommandResult> RecreateAsync(DownloadRecord record, CancellationToken cancellationToken)
    {
        if (record.State == DownloadState.Completed && !await ConfirmPublicationAbsentAsync(record, cancellationToken))
            return Duplicate(record.Id);

        if (record.State != DownloadState.Deleted) return Duplicate(record.Id);

        return await EnqueueRetryAsync(record, cancellationToken);
    }

    private async Task<bool> ConfirmPublicationAbsentAsync(DownloadRecord record, CancellationToken cancellationToken)
    {
        var transfer = await database.Transfers.SingleOrDefaultAsync(
                           item => item.DownloadId == record.Id
                                && item.PublicationId == record.PublicationId,
                           cancellationToken
                       );

        if (transfer is null) throw new StorageUnavailableException();

        var file = await storage.FindAsync(transfer.ObjectKey, cancellationToken);

        if (file is not null)
        {
            if (file.PublicationId != record.PublicationId.ToString("D")) throw new StorageConflictException();

            return false;
        }

        var aggregate = record.RestoreAggregate();

        var deleted = aggregate.Apply(
            new ResultDeleted(record.CurrentAttemptId, DownloadState.Completed, record.PublicationId, true)
        );

        if (!deleted.IsAccepted) return false;

        record.Capture(aggregate, deleted.Intent, clock.GetUtcNow());

        // Keep the external-deletion observation in the old attempt's history, even when
        // the same atomic command immediately queues a new publication.
        database.Stages.Add(
            new StageExecutionRecord
            {
                Id = Guid.NewGuid(),
                DownloadId = record.Id,
                AttemptId = record.CurrentAttemptId,
                Stage = ProcessingStage.DeleteResult,
                Status = StageStatus.Succeeded,
                StartedAt = clock.GetUtcNow(),
                FinishedAt = clock.GetUtcNow()
            }
        );

        return true;
    }

    private async Task<DownloadCommandResult> EnqueueRetryAsync(
        DownloadRecord record,
        CancellationToken cancellationToken
    )
    {
        var before = record.RestoreAggregate();
        var reason = before.State == DownloadState.Deleted ? AttemptReason.RetryDeletedResult : AttemptReason.Retry;

        var newPublication = before.State is DownloadState.Deleted or DownloadState.Canceled
                                 ? Guid.NewGuid()
                                 : (Guid?)null;

        var preparation = newPublication is not null
                              ? new RetryPreparation(DownloadState.Downloading)
                              : await retryPlanner.PrepareAsync(record, cancellationToken);

        var previousAttempt = record.CurrentAttemptId;
        var retry = before.Apply(new Retry(Guid.NewGuid(), preparation.State, newPublication));

        if (!retry.IsAccepted) return InvalidState(record.Id);

        var now = clock.GetUtcNow();
        record.Capture(before, retry.Intent, now);

        if (newPublication is not null)
        {
            record.WorkspaceGeneration = Guid.NewGuid();
            record.MediaPlanJson = null;
        }

        var number = await NextAttemptNumberAsync(record.Id, cancellationToken);
        AddAttempt(record, number, reason, record.ResumeState!.Value, now);
        var newAttempt = database.Attempts.Local.Single(item => item.Id == record.CurrentAttemptId);
        newAttempt.ErrorDetails = preparation.Explanation;

        if (newPublication is null)
        {
            var reused = await database.Stages.Where(item => item.AttemptId == previousAttempt
                                                          && item.Status == StageStatus.Succeeded
                                        )
                                       .ToArrayAsync(cancellationToken);

            var initial = DownloadStages.FromState(record.ResumeState.Value);

            foreach (var stage in reused.Where(item => item.Stage < initial))
                database.Stages.Add(
                    new StageExecutionRecord
                    {
                        Id = Guid.NewGuid(),
                        DownloadId = record.Id,
                        AttemptId = record.CurrentAttemptId,
                        Stage = stage.Stage,
                        Status = StageStatus.Succeeded,
                        StartedAt = now,
                        FinishedAt = now,
                        Progress = 100,
                        ArtifactId = stage.ArtifactId,
                        ReusedFromStageId = stage.Id
                    }
                );
        }

        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return InvalidState(record.Id);
        }
        catch (DbUpdateException error) when (IsUniqueConstraint(error))
        {
            return InvalidState(record.Id);
        }

        return new(await queries.GetAsync(record.Id, cancellationToken), 202);
    }

    private async Task<int> NextAttemptNumberAsync(Guid id, CancellationToken cancellationToken) =>
        (await database.Attempts.Where(item => item.DownloadId == id).MaxAsync(item => item.Number, cancellationToken))
      + 1;

    private void AddAttempt(
        DownloadRecord record,
        int number,
        AttemptReason reason,
        DownloadState initial,
        DateTimeOffset now
    )
    {
        database.Attempts.Add(
            new DownloadAttemptRecord
            {
                Id = record.CurrentAttemptId,
                DownloadId = record.Id,
                Number = number,
                Reason = reason,
                InitialState = initial,
                Status = record.AttemptStatus,
                QueuedAt = now
            }
        );

        AddStage(record, initial);
    }

    private void AddStage(DownloadRecord record, DownloadState initial) => database.Stages.Add(
        new StageExecutionRecord
        {
            Id = Guid.NewGuid(),
            DownloadId = record.Id,
            AttemptId = record.CurrentAttemptId,
            Stage = DownloadStages.FromState(initial),
            Status = StageStatus.Pending
        }
    );

    private static bool IsUniqueConstraint(DbUpdateException error) =>
        error.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private static DownloadCommandResult Duplicate(Guid id) => new(
        Status: 409,
        Code: "download_already_exists",
        ExistingDownloadId: id
    );

    private static DownloadCommandResult InvalidState(Guid id) => new(
        Status: 409,
        Code: "invalid_state",
        ExistingDownloadId: id
    );

    private static DownloadCommandResult HashConflict() => new(Status: 409, Code: "url_conflict");
}
