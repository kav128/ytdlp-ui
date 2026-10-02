using Ytdlp.Ui.Domain.Events;
using Ytdlp.Ui.Domain.Primitives;
using Ytdlp.Ui.Domain.ValueObjects;

namespace Ytdlp.Ui.Domain;

public static class DownloadStateMachine
{
    public static DownloadSnapshot Create(Guid downloadId, Guid attemptId, Guid publicationId)
    {
        if (downloadId == Guid.Empty || attemptId == Guid.Empty || publicationId == Guid.Empty)
            throw new ArgumentException("Download, attempt and publication IDs must be non-empty.");

        return new(downloadId, attemptId, publicationId, DownloadState.Queued,
            AttemptStatus.Pending, ResumeState: DownloadState.Downloading);
    }

    public static DownloadTransition Apply(DownloadSnapshot snapshot, DownloadEvent @event) => @event switch
    {
        StartAttempt start => Start(snapshot, start),
        StageSucceeded success => Succeed(snapshot, success),
        StageFailed failure => Fail(snapshot, failure),
        ProcessInterrupted interrupted => Interrupt(snapshot, interrupted),
        RequestCancel cancel => Cancel(snapshot, cancel),
        CancellationSucceeded canceled => FinishCancellation(snapshot, canceled),
        Retry retry => RetryAttempt(snapshot, retry),
        RetryDeletedResult retry => RetryDeleted(snapshot, retry),
        RequestDeleteResult delete => Delete(snapshot, delete),
        ResultDeleted deleted => FinishDeletion(snapshot, deleted),
        _ => Reject(snapshot, TransitionError.InvalidState)
    };

    public static IReadOnlyList<DownloadAction> GetAllowedActions(DownloadSnapshot snapshot)
    {
        List<DownloadAction> actions = [];
        if (CanRetry(snapshot)) actions.Add(DownloadAction.Retry);
        if (CanCancel(snapshot)) actions.Add(DownloadAction.Cancel);
        return actions.AsReadOnly();
    }

    public static bool CanDeleteResult(DownloadSnapshot snapshot) => !snapshot.HasActiveAttempt &&
        (snapshot.State == DownloadState.Completed ||
         snapshot.State == DownloadState.Failed && snapshot.ResumeState == DownloadState.Deleting);

    private static bool CanRetry(DownloadSnapshot snapshot) => !snapshot.HasActiveAttempt &&
        (snapshot.State is DownloadState.Canceled or DownloadState.Deleted ||
         snapshot.State == DownloadState.Failed && IsOperation(snapshot.ResumeState));

    private static bool CanCancel(DownloadSnapshot snapshot) => !snapshot.CancelRequested && (snapshot.State switch
    {
        DownloadState.Downloading or DownloadState.Preparing or DownloadState.Uploading =>
            snapshot.AttemptStatus == AttemptStatus.Running,
        DownloadState.Queued => snapshot.AttemptStatus == AttemptStatus.Pending && IsCancelableStage(snapshot.ResumeState),
        DownloadState.Failed => !snapshot.HasActiveAttempt && IsCancelableStage(snapshot.ResumeState),
        _ => false
    });

    private static DownloadTransition Start(DownloadSnapshot snapshot, StartAttempt @event)
    {
        if (@event.AttemptId != snapshot.AttemptId) return Reject(snapshot, TransitionError.StaleAttempt);
        if (snapshot.State != DownloadState.Queued || snapshot.AttemptStatus != AttemptStatus.Pending)
            return Reject(snapshot, TransitionError.InvalidState);
        if (!IsOperation(snapshot.ResumeState)) return Reject(snapshot, TransitionError.InvalidResumeState);

        return new(snapshot with { State = snapshot.ResumeState!.Value, AttemptStatus = AttemptStatus.Running },
            DownloadIntent.ExecuteStage);
    }

    private static DownloadTransition Succeed(DownloadSnapshot snapshot, StageSucceeded @event)
    {
        if (CheckOperation(snapshot, @event.AttemptId, @event.Stage) is { } error) return Reject(snapshot, error);
        var next = @event.Stage switch
        {
            DownloadState.Downloading => DownloadState.Preparing,
            DownloadState.Preparing => DownloadState.Uploading,
            DownloadState.Uploading => DownloadState.CleaningUp,
            DownloadState.CleaningUp => DownloadState.Completed,
            _ => (DownloadState?)null
        };
        if (next is null) return Reject(snapshot, TransitionError.InvalidState);
        if (@event.Stage == DownloadState.Uploading && !@event.PublicationConfirmed ||
            @event.Stage == DownloadState.CleaningUp && !@event.LocalCleanupConfirmed)
            return Reject(snapshot, TransitionError.ConfirmationRequired);

        var completed = next == DownloadState.Completed;
        return new(snapshot with
        {
            State = next.Value,
            ResumeState = completed ? null : next,
            AttemptStatus = completed ? AttemptStatus.Succeeded : AttemptStatus.Running,
            PublicationState = @event.Stage == DownloadState.Uploading ? PublicationState.Available : snapshot.PublicationState
        }, completed ? DownloadIntent.None : DownloadIntent.ExecuteStage);
    }

    private static DownloadTransition Fail(DownloadSnapshot snapshot, StageFailed @event)
    {
        if (CheckOperation(snapshot, @event.AttemptId, @event.Stage) is { } error) return Reject(snapshot, error);
        if (@event.PublicationUncertain && @event.Stage is not (DownloadState.Uploading or DownloadState.Deleting or DownloadState.Canceling))
            return Reject(snapshot, TransitionError.InvalidState);

        return new(snapshot with
        {
            State = DownloadState.Failed,
            AttemptStatus = AttemptStatus.Failed,
            FailedState = @event.Stage,
            ResumeState = @event.Stage,
            Failure = @event.Failure,
            PublicationState = @event.PublicationUncertain ? PublicationState.Unknown : snapshot.PublicationState
        });
    }

    private static DownloadTransition Interrupt(DownloadSnapshot snapshot, ProcessInterrupted @event) =>
        Fail(snapshot, new StageFailed(@event.AttemptId, @event.Stage, new("interrupted"),
            PublicationUncertain: @event.Stage is DownloadState.Uploading or DownloadState.Deleting ||
                @event.Stage == DownloadState.Canceling && snapshot.PublicationState is PublicationState.Available or PublicationState.Unknown));

    private static DownloadTransition Cancel(DownloadSnapshot snapshot, RequestCancel @event)
    {
        if (snapshot.CancelRequested && (snapshot.State is DownloadState.Canceling or DownloadState.Canceled ||
            snapshot.State is DownloadState.Queued or DownloadState.Failed && snapshot.ResumeState == DownloadState.Canceling))
            return new(snapshot);
        if (!CanCancel(snapshot)) return Reject(snapshot, TransitionError.InvalidState);
        if (snapshot.State == DownloadState.Failed)
        {
            if (@event.NewAttemptId is not { } id || !IsNewAttempt(snapshot, id))
                return Reject(snapshot, TransitionError.InvalidAttempt);
        }
        else if (@event.NewAttemptId is not null)
            return Reject(snapshot, TransitionError.InvalidAttempt);

        return new(snapshot with
        {
            AttemptId = @event.NewAttemptId ?? snapshot.AttemptId,
            State = DownloadState.Canceling,
            AttemptStatus = AttemptStatus.Running,
            ResumeState = DownloadState.Canceling,
            FailedState = null,
            Failure = null,
            CancelRequested = true,
            PublicationState = snapshot.PublicationState == PublicationState.None &&
                (snapshot.State == DownloadState.Uploading || snapshot.ResumeState == DownloadState.Uploading)
                    ? PublicationState.Unknown : snapshot.PublicationState
        }, DownloadIntent.StopAndCleanUp);
    }

    private static DownloadTransition FinishCancellation(DownloadSnapshot snapshot, CancellationSucceeded @event)
    {
        if (CheckOperation(snapshot, @event.AttemptId, DownloadState.Canceling) is { } error) return Reject(snapshot, error);
        if (!@event.LocalCleanupConfirmed || snapshot.PublicationState != PublicationState.None && !@event.PublicationAbsentConfirmed)
            return Reject(snapshot, TransitionError.ConfirmationRequired);

        return new(snapshot with
        {
            State = DownloadState.Canceled,
            AttemptStatus = AttemptStatus.Canceled,
            ResumeState = null,
            PublicationState = snapshot.PublicationState == PublicationState.None ? PublicationState.None : PublicationState.Deleted
        });
    }

    private static DownloadTransition RetryAttempt(DownloadSnapshot snapshot, Retry @event)
    {
        if (snapshot.State == DownloadState.Deleted && @event.NewPublicationId is { } publicationId)
        {
            if (@event.ResumeState is not null and not DownloadState.Downloading)
                return Reject(snapshot, TransitionError.InvalidResumeState);
            return RetryDeleted(snapshot, new(@event.NewAttemptId, publicationId));
        }
        if (!CanRetry(snapshot) || snapshot.State == DownloadState.Deleted) return Reject(snapshot, TransitionError.InvalidState);
        if (!IsNewAttempt(snapshot, @event.NewAttemptId)) return Reject(snapshot, TransitionError.InvalidAttempt);

        if (snapshot.State == DownloadState.Canceled)
        {
            if (@event.ResumeState is not null and not DownloadState.Downloading)
                return Reject(snapshot, TransitionError.InvalidResumeState);
            if (@event.NewPublicationId is not { } newPublicationId || !IsNewPublication(snapshot, newPublicationId))
                return Reject(snapshot, TransitionError.InvalidPublication);
            return Queue(snapshot, @event.NewAttemptId, DownloadState.Downloading, newPublicationId);
        }

        if (@event.NewPublicationId is not null) return Reject(snapshot, TransitionError.InvalidPublication);
        var resume = @event.ResumeState ?? snapshot.ResumeState!.Value;
        if (!CanResume(snapshot.ResumeState!.Value, resume)) return Reject(snapshot, TransitionError.InvalidResumeState);
        return Queue(snapshot, @event.NewAttemptId, resume);
    }

    private static DownloadTransition RetryDeleted(DownloadSnapshot snapshot, RetryDeletedResult @event)
    {
        if (snapshot.State != DownloadState.Deleted || !CanRetry(snapshot) || snapshot.PublicationState != PublicationState.Deleted)
            return Reject(snapshot, TransitionError.InvalidState);
        if (!IsNewAttempt(snapshot, @event.NewAttemptId)) return Reject(snapshot, TransitionError.InvalidAttempt);
        if (!IsNewPublication(snapshot, @event.NewPublicationId)) return Reject(snapshot, TransitionError.InvalidPublication);
        return Queue(snapshot, @event.NewAttemptId, DownloadState.Downloading, @event.NewPublicationId);
    }

    private static DownloadTransition Queue(DownloadSnapshot snapshot, Guid attemptId, DownloadState resume, Guid? publicationId = null) =>
        new(snapshot with
        {
            AttemptId = attemptId,
            PublicationId = publicationId ?? snapshot.PublicationId,
            State = DownloadState.Queued,
            AttemptStatus = AttemptStatus.Pending,
            ResumeState = resume,
            FailedState = null,
            Failure = null,
            CancelRequested = resume == DownloadState.Canceling,
            PublicationState = publicationId is null ? snapshot.PublicationState : PublicationState.None
        }, DownloadIntent.EnqueueAttempt);

    private static DownloadTransition Delete(DownloadSnapshot snapshot, RequestDeleteResult @event)
    {
        if (!CanDeleteResult(snapshot)) return Reject(snapshot, TransitionError.InvalidState);
        if (!IsNewAttempt(snapshot, @event.NewAttemptId)) return Reject(snapshot, TransitionError.InvalidAttempt);

        return new(snapshot with
        {
            AttemptId = @event.NewAttemptId,
            State = DownloadState.Deleting,
            AttemptStatus = AttemptStatus.Running,
            ResumeState = DownloadState.Deleting,
            FailedState = null,
            Failure = null
        }, DownloadIntent.ExecuteStage);
    }

    private static DownloadTransition FinishDeletion(DownloadSnapshot snapshot, ResultDeleted @event)
    {
        if (@event.AttemptId != snapshot.AttemptId) return Reject(snapshot, TransitionError.StaleAttempt);
        if (@event.Stage != snapshot.State) return Reject(snapshot, TransitionError.UnexpectedStage);
        if (@event.PublicationId != snapshot.PublicationId) return Reject(snapshot, TransitionError.InvalidPublication);
        if (snapshot.State == DownloadState.Deleting ? snapshot.AttemptStatus != AttemptStatus.Running :
            snapshot.State != DownloadState.Completed || snapshot.HasActiveAttempt)
            return Reject(snapshot, TransitionError.InvalidState);
        if (!@event.AbsenceConfirmed) return Reject(snapshot, TransitionError.ConfirmationRequired);

        return new(snapshot with
        {
            State = DownloadState.Deleted,
            AttemptStatus = AttemptStatus.Succeeded,
            ResumeState = null,
            PublicationState = PublicationState.Deleted
        });
    }

    private static bool CanResume(DownloadState failed, DownloadState resume) => failed switch
    {
        DownloadState.Downloading => resume == DownloadState.Downloading,
        DownloadState.Preparing => resume is DownloadState.Downloading or DownloadState.Preparing,
        DownloadState.Uploading => resume is DownloadState.Downloading or DownloadState.Preparing or DownloadState.Uploading,
        _ => failed == resume
    };

    private static TransitionError? CheckOperation(DownloadSnapshot snapshot, Guid attemptId, DownloadState stage) =>
        attemptId != snapshot.AttemptId ? TransitionError.StaleAttempt :
        stage != snapshot.State ? TransitionError.UnexpectedStage :
        !IsOperation(snapshot.State) || snapshot.AttemptStatus != AttemptStatus.Running ? TransitionError.InvalidState : null;

    private static bool IsOperation(DownloadState? state) => state is DownloadState.Downloading or DownloadState.Preparing or
        DownloadState.Uploading or DownloadState.CleaningUp or DownloadState.Deleting or DownloadState.Canceling;

    private static bool IsCancelableStage(DownloadState? state) => state is DownloadState.Downloading or DownloadState.Preparing or DownloadState.Uploading;
    private static bool IsNewAttempt(DownloadSnapshot snapshot, Guid id) => id != Guid.Empty && id != snapshot.AttemptId;
    private static bool IsNewPublication(DownloadSnapshot snapshot, Guid id) => id != Guid.Empty && id != snapshot.PublicationId;
    private static DownloadTransition Reject(DownloadSnapshot snapshot, TransitionError error) => new(snapshot, Error: error);
}
