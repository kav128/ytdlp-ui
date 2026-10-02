using Ytdlp.Ui.Domain;
using Ytdlp.Ui.Domain.Events;
using Ytdlp.Ui.Domain.Primitives;
using Ytdlp.Ui.Domain.ValueObjects;

namespace Ytdlp.Ui.Tests.Domain;

[TestFixture]
public class DownloadTests
{
    private static readonly Guid _downloadId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid _attemptId = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid _newAttemptId = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly Guid _publicationId = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid _newPublicationId = Guid.Parse("30000000-0000-0000-0000-000000000002");
    private static readonly DownloadState[] _operations = [DownloadState.Downloading, DownloadState.Preparing,
        DownloadState.Uploading, DownloadState.CleaningUp, DownloadState.Deleting, DownloadState.Canceling];

    [Test]
    public void NewDownloadIsQueuedWithAnUnpublishedResult()
    {
        var snapshot = Download.Create(_downloadId, _attemptId, _publicationId).Snapshot;

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.State, Is.EqualTo(DownloadState.Queued));
            Assert.That(snapshot.ResumeState, Is.EqualTo(DownloadState.Downloading));
            Assert.That(snapshot.HasActiveAttempt, Is.True);
            Assert.That(snapshot.PublicationState, Is.EqualTo(PublicationState.None));
            Assert.That(snapshot.CancelRequested, Is.False);
            Assert.That(Download.Restore(snapshot).AllowedActions, Is.EqualTo(new[] { DownloadAction.Cancel }));
        });
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public void NewDownloadRejectsEmptyIdentifiers(int index)
    {
        Guid[] ids = [_downloadId, _attemptId, _publicationId];
        ids[index] = Guid.Empty;
        Assert.Throws<ArgumentException>(() => Download.Create(ids[0], ids[1], ids[2]));
    }

    [Test]
    public void SuccessfulDownloadPublishesBeforeCleanupAndCompletesAfterCleanup()
    {
        var queued = Download.Create(_downloadId, _attemptId, _publicationId).Snapshot;
        var current = Accept(queued, new StartAttempt(_attemptId), DownloadState.Downloading, DownloadIntent.ExecuteStage);
        current = Accept(current, new StageSucceeded(_attemptId, DownloadState.Downloading), DownloadState.Preparing, DownloadIntent.ExecuteStage);
        current = Accept(current, new StageSucceeded(_attemptId, DownloadState.Preparing), DownloadState.Uploading, DownloadIntent.ExecuteStage);
        current = Accept(current, new StageSucceeded(_attemptId, DownloadState.Uploading, PublicationConfirmed: true), DownloadState.CleaningUp, DownloadIntent.ExecuteStage);

        Assert.That(current.PublicationState, Is.EqualTo(PublicationState.Available));
        Assert.That(current.HasActiveAttempt, Is.True);
        current = Accept(current, new StageSucceeded(_attemptId, DownloadState.CleaningUp, LocalCleanupConfirmed: true), DownloadState.Completed);

        Assert.Multiple(() =>
        {
            Assert.That(current.AttemptStatus, Is.EqualTo(AttemptStatus.Succeeded));
            Assert.That(current.ResumeState, Is.Null);
            Assert.That(current.PublicationState, Is.EqualTo(PublicationState.Available));
            Assert.That(current.Id, Is.EqualTo(_downloadId));
            Assert.That(queued.State, Is.EqualTo(DownloadState.Queued));
            Assert.That(queued.PublicationState, Is.EqualTo(PublicationState.None));
        });
    }

    [TestCaseSource(nameof(AllStates))]
    public void StartOnlyAcceptsAnUnclaimedQueuedAttempt(DownloadState state)
    {
        var snapshot = At(state);
        var result = Apply(snapshot, new StartAttempt(_attemptId));
        Assert.That(result.IsAccepted, Is.EqualTo(state == DownloadState.Queued));
        if (!result.IsAccepted) AssertRejected(snapshot, result);
    }

    [TestCaseSource(nameof(_operations))]
    public void QueueCanResumeEachProcessingOperation(DownloadState resume)
    {
        var queued = At(DownloadState.Queued) with { ResumeState = resume, CancelRequested = resume == DownloadState.Canceling };
        var started = Accept(queued, new StartAttempt(_attemptId), resume, DownloadIntent.ExecuteStage);
        Assert.That(started.AttemptStatus, Is.EqualTo(AttemptStatus.Running));
        AssertRejected(started, Apply(started, new StartAttempt(_attemptId)));
    }

    [TestCaseSource(nameof(AllStates))]
    public void SuccessfulStagesFollowOnlyTheDocumentedProcessingChain(DownloadState state)
    {
        var snapshot = At(state);
        var result = Apply(snapshot, new StageSucceeded(_attemptId, state, true, true));
        var expected = state switch
        {
            DownloadState.Downloading => DownloadState.Preparing,
            DownloadState.Preparing => DownloadState.Uploading,
            DownloadState.Uploading => DownloadState.CleaningUp,
            DownloadState.CleaningUp => DownloadState.Completed,
            _ => (DownloadState?)null
        };
        Assert.That(result.IsAccepted, Is.EqualTo(expected is not null));
        if (expected is { } next) Assert.That(result.Snapshot.State, Is.EqualTo(next));
        else AssertRejected(snapshot, result);
    }

    [TestCase(DownloadState.Uploading)]
    [TestCase(DownloadState.CleaningUp)]
    public void PublicationAndCleanupRequireExplicitConfirmation(DownloadState state)
    {
        var snapshot = At(state);
        AssertRejected(snapshot, Apply(snapshot, new StageSucceeded(_attemptId, state)), TransitionError.ConfirmationRequired);
    }

    [TestCaseSource(nameof(AllStates))]
    public void OnlyProcessingOperationsCanFailOrBeInterrupted(DownloadState state)
    {
        var snapshot = At(state);
        DownloadEvent[] events = [new StageFailed(_attemptId, state, new("stage_failed", "diagnostic")), new ProcessInterrupted(_attemptId, state)];
        foreach (var @event in events)
        {
            var result = Apply(snapshot, @event);
            Assert.That(result.IsAccepted, Is.EqualTo(_operations.Contains(state)));
            if (!result.IsAccepted) { AssertRejected(snapshot, result); continue; }
            Assert.Multiple(() =>
            {
                Assert.That(result.Snapshot.State, Is.EqualTo(DownloadState.Failed));
                Assert.That(result.Snapshot.FailedState, Is.EqualTo(state));
                Assert.That(result.Snapshot.ResumeState, Is.EqualTo(state));
                Assert.That(result.Snapshot.HasActiveAttempt, Is.False);
                Assert.That(result.Snapshot.Failure?.Code, Is.EqualTo(@event is ProcessInterrupted ? "interrupted" : "stage_failed"));
                Assert.That(result.Intent, Is.EqualTo(DownloadIntent.None));
            });
        }
    }

    [TestCaseSource(nameof(_operations))]
    public void RetryCreatesANewAttemptAtTheFailedOperation(DownloadState stage)
    {
        var failed = FailAt(stage);
        var queued = Accept(failed, new Retry(_newAttemptId), DownloadState.Queued, DownloadIntent.EnqueueAttempt);
        Assert.Multiple(() =>
        {
            Assert.That(queued.Id, Is.EqualTo(failed.Id));
            Assert.That(queued.AttemptId, Is.EqualTo(_newAttemptId));
            Assert.That(queued.PublicationId, Is.EqualTo(failed.PublicationId));
            Assert.That(queued.ResumeState, Is.EqualTo(stage));
            Assert.That(queued.AttemptStatus, Is.EqualTo(AttemptStatus.Pending));
            Assert.That(queued.Failure, Is.Null);
            Assert.That(queued.FailedState, Is.Null);
            Assert.That(queued.CancelRequested, Is.EqualTo(stage == DownloadState.Canceling));
            Assert.That(failed.AttemptId, Is.EqualTo(_attemptId));
            Assert.That(failed.Failure?.Code, Is.EqualTo("stage_failed"));
        });
        Accept(queued, new StartAttempt(_newAttemptId), stage, DownloadIntent.ExecuteStage);
        AssertRejected(queued, Apply(queued, new Retry(Guid.NewGuid())));
    }

    [TestCase(DownloadState.Preparing, DownloadState.Downloading)]
    [TestCase(DownloadState.Uploading, DownloadState.Downloading)]
    [TestCase(DownloadState.Uploading, DownloadState.Preparing)]
    public void RetryMayRestoreEarlierMediaStagesWhenArtifactsAreLost(DownloadState failedStage, DownloadState resume)
    {
        var queued = Accept(FailAt(failedStage), new Retry(_newAttemptId, resume), DownloadState.Queued, DownloadIntent.EnqueueAttempt);
        Assert.That(queued.ResumeState, Is.EqualTo(resume));
    }

    [TestCaseSource(nameof(InvalidResumeCases))]
    public void RetryCannotSkipStagesOrRestartCleanupDeletionAndCancellation(DownloadState failedStage, DownloadState resume)
    {
        var failed = FailAt(failedStage);
        AssertRejected(failed, Apply(failed, new Retry(_newAttemptId, resume)), TransitionError.InvalidResumeState);
    }

    [Test]
    public void CleanupFailureAndRetryKeepThePublishedFileAvailable()
    {
        var failed = FailAt(DownloadState.CleaningUp);
        var queued = Accept(failed, new Retry(_newAttemptId), DownloadState.Queued, DownloadIntent.EnqueueAttempt);
        Assert.Multiple(() =>
        {
            Assert.That(failed.PublicationState, Is.EqualTo(PublicationState.Available));
            Assert.That(queued.PublicationState, Is.EqualTo(PublicationState.Available));
            Assert.That(Download.Restore(failed).AllowedActions, Is.EqualTo(new[] { DownloadAction.Retry }));
            Assert.That(Download.Restore(queued).AllowedActions, Is.Empty);
            Assert.That(Download.Restore(failed).CanDeleteResult, Is.False);
        });
    }

    [TestCaseSource(nameof(CancellationCases))]
    public void CancellationIsAcceptedBeforeCleanup(DownloadState state, DownloadState resume)
    {
        var snapshot = At(state) with { ResumeState = resume };
        var cancel = new RequestCancel(state == DownloadState.Failed ? _newAttemptId : null);
        var canceling = Accept(snapshot, cancel, DownloadState.Canceling, DownloadIntent.StopAndCleanUp);
        Assert.Multiple(() =>
        {
            Assert.That(canceling.CancelRequested, Is.True);
            Assert.That(canceling.ResumeState, Is.EqualTo(DownloadState.Canceling));
            Assert.That(canceling.AttemptId, Is.EqualTo(state == DownloadState.Failed ? _newAttemptId : _attemptId));
            Assert.That(canceling.AttemptStatus, Is.EqualTo(AttemptStatus.Running));
            Assert.That(Download.Restore(canceling).AllowedActions, Is.Empty);
        });
    }

    [TestCase(DownloadState.CleaningUp)]
    [TestCase(DownloadState.Deleting)]
    [TestCase(DownloadState.Canceling)]
    public void CancelIsForbiddenForProtectedOperationsAndTheirQueuedAndFailedForms(DownloadState stage)
    {
        DownloadSnapshot[] snapshots = [At(stage), At(DownloadState.Queued) with { ResumeState = stage },
            At(DownloadState.Failed) with { ResumeState = stage }];
        foreach (var snapshot in snapshots)
        {
            if (snapshot.CancelRequested) continue;
            Assert.That(Download.Restore(snapshot).AllowedActions, Does.Not.Contain(DownloadAction.Cancel));
            AssertRejected(snapshot, Apply(snapshot, new RequestCancel(_newAttemptId)), TransitionError.InvalidState);
        }
    }

    [Test]
    public void RepeatedAcceptedCancellationNeverStartsNewWork()
    {
        var canceling = Accept(At(DownloadState.Downloading), new RequestCancel(), DownloadState.Canceling, DownloadIntent.StopAndCleanUp);
        var failed = Accept(canceling, new StageFailed(_attemptId, DownloadState.Canceling, new("cleanup_failed")), DownloadState.Failed);
        var queued = Accept(failed, new Retry(_newAttemptId), DownloadState.Queued, DownloadIntent.EnqueueAttempt);
        var canceled = Accept(canceling, new CancellationSucceeded(_attemptId, true), DownloadState.Canceled);
        foreach (var snapshot in new[] { canceling, failed, queued, canceled })
        {
            var repeated = Apply(snapshot, new RequestCancel(Guid.NewGuid()));
            Assert.That(repeated.IsAccepted, Is.True);
            Assert.That(repeated.Snapshot, Is.SameAs(snapshot));
            Assert.That(repeated.Intent, Is.EqualTo(DownloadIntent.None));
        }
    }

    [Test]
    public void CancellationWaitsForLocalCleanupAndAbsenceOfAnUncertainPublication()
    {
        var canceling = Accept(At(DownloadState.Uploading), new RequestCancel(), DownloadState.Canceling, DownloadIntent.StopAndCleanUp);
        Assert.That(canceling.PublicationState, Is.EqualTo(PublicationState.Unknown));
        AssertRejected(canceling, Apply(canceling, new CancellationSucceeded(_attemptId, false, true)), TransitionError.ConfirmationRequired);
        AssertRejected(canceling, Apply(canceling, new CancellationSucceeded(_attemptId, true)), TransitionError.ConfirmationRequired);
        var canceled = Accept(canceling, new CancellationSucceeded(_attemptId, true, true), DownloadState.Canceled);
        Assert.That(canceled.PublicationState, Is.EqualTo(PublicationState.Deleted));
        Assert.That(canceled.AttemptStatus, Is.EqualTo(AttemptStatus.Canceled));
    }

    [Test]
    public void UploadSuccessBeforeCancellationProtectsCleanup()
    {
        var cleanup = Accept(At(DownloadState.Uploading), new StageSucceeded(_attemptId, DownloadState.Uploading, true), DownloadState.CleaningUp, DownloadIntent.ExecuteStage);
        AssertRejected(cleanup, Apply(cleanup, new RequestCancel()), TransitionError.InvalidState);
        Assert.That(cleanup.PublicationState, Is.EqualTo(PublicationState.Available));
    }

    [Test]
    public void CancellationBeforeUploadSuccessRejectsTheLateEvent()
    {
        var canceling = Accept(At(DownloadState.Uploading), new RequestCancel(), DownloadState.Canceling, DownloadIntent.StopAndCleanUp);
        AssertRejected(canceling, Apply(canceling, new StageSucceeded(_attemptId, DownloadState.Uploading, true)), TransitionError.UnexpectedStage);
        Assert.That(canceling.CancelRequested, Is.True);
    }

    [Test]
    public void RetryAfterCancellationStartsANewPublicationFromDownloading()
    {
        var canceled = Accept(At(DownloadState.Canceling), new CancellationSucceeded(_attemptId, true), DownloadState.Canceled);
        var queued = Accept(canceled, new Retry(_newAttemptId, NewPublicationId: _newPublicationId), DownloadState.Queued, DownloadIntent.EnqueueAttempt);
        Assert.Multiple(() =>
        {
            Assert.That(queued.Id, Is.EqualTo(canceled.Id));
            Assert.That(queued.ResumeState, Is.EqualTo(DownloadState.Downloading));
            Assert.That(queued.PublicationId, Is.EqualTo(_newPublicationId));
            Assert.That(queued.PublicationState, Is.EqualTo(PublicationState.None));
            Assert.That(queued.CancelRequested, Is.False);
        });
    }

    [Test]
    public void DeletionUsesANewAttemptAndRequiresConfirmedAbsence()
    {
        var completed = At(DownloadState.Completed);
        var deleting = Accept(completed, new RequestDeleteResult(_newAttemptId), DownloadState.Deleting, DownloadIntent.ExecuteStage);
        Assert.That(deleting.PublicationState, Is.EqualTo(PublicationState.Available));
        Assert.That(completed.AttemptStatus, Is.EqualTo(AttemptStatus.Succeeded));
        AssertRejected(deleting, Apply(deleting, new ResultDeleted(_newAttemptId, DownloadState.Deleting, _publicationId, false)), TransitionError.ConfirmationRequired);
        var deleted = Accept(deleting, new ResultDeleted(_newAttemptId, DownloadState.Deleting, _publicationId, true), DownloadState.Deleted);
        Assert.That(deleted.PublicationState, Is.EqualTo(PublicationState.Deleted));
        Assert.That(deleted.HasActiveAttempt, Is.False);
    }

    [Test]
    public void DeletionFailureCanBeRetriedThroughQueueOrDeleteCommand()
    {
        var failed = FailAt(DownloadState.Deleting);
        Assert.That(Download.Restore(failed).CanDeleteResult, Is.True);
        Accept(failed, new RequestDeleteResult(_newAttemptId), DownloadState.Deleting, DownloadIntent.ExecuteStage);
        var queued = Accept(failed, new Retry(_newAttemptId), DownloadState.Queued, DownloadIntent.EnqueueAttempt);
        Accept(queued, new StartAttempt(_newAttemptId), DownloadState.Deleting, DownloadIntent.ExecuteStage);
        Assert.That(Download.Restore(queued).CanDeleteResult, Is.False);
    }

    [Test]
    public void ConfirmedExternalDeletionPreservesTheSuccessfulAttempt()
    {
        var completed = At(DownloadState.Completed);
        var deleted = Accept(completed, new ResultDeleted(_attemptId, DownloadState.Completed, _publicationId, true), DownloadState.Deleted);
        Assert.That(deleted.AttemptId, Is.EqualTo(completed.AttemptId));
        Assert.That(deleted.AttemptStatus, Is.EqualTo(AttemptStatus.Succeeded));
        Assert.That(Download.Restore(deleted).AllowedActions, Is.EqualTo(new[] { DownloadAction.Retry }));
    }

    [Test]
    public void BothRetryEntryPointsForDeletedResultUseTheSameTransition()
    {
        var deleted = At(DownloadState.Deleted);
        var command = Apply(deleted, new Retry(_newAttemptId, NewPublicationId: _newPublicationId));
        var resubmission = Apply(deleted, new RetryDeletedResult(_newAttemptId, _newPublicationId));
        Assert.That(command, Is.EqualTo(resubmission));
        var queued = command.Snapshot;
        Assert.That(command.IsAccepted, Is.True);
        Assert.That(queued.Id, Is.EqualTo(_downloadId));
        Assert.That(queued.ResumeState, Is.EqualTo(DownloadState.Downloading));
        Assert.That(queued.PublicationState, Is.EqualTo(PublicationState.None));
        AssertRejected(queued, Apply(queued, new RetryDeletedResult(Guid.NewGuid(), Guid.NewGuid())));
        AssertRejected(queued, Apply(queued, new ResultDeleted(_attemptId, DownloadState.Completed, _publicationId, true)), TransitionError.StaleAttempt);
    }

    [TestCaseSource(nameof(AllStates))]
    public void DeleteAndRetryCommandsAreRestrictedToTheirDocumentedStates(DownloadState state)
    {
        var snapshot = At(state);
        var delete = Apply(snapshot, new RequestDeleteResult(_newAttemptId));
        var retry = Apply(snapshot, new Retry(_newAttemptId, NewPublicationId: state is DownloadState.Canceled or DownloadState.Deleted ? _newPublicationId : null));
        Assert.That(delete.IsAccepted, Is.EqualTo(state == DownloadState.Completed));
        Assert.That(retry.IsAccepted, Is.EqualTo(state is DownloadState.Failed or DownloadState.Canceled or DownloadState.Deleted));
        if (!delete.IsAccepted) AssertRejected(snapshot, delete);
        if (!retry.IsAccepted) AssertRejected(snapshot, retry);
    }

    [TestCaseSource(nameof(AllStates))]
    public void AllowedActionsMatchCommandGuards(DownloadState state)
    {
        var snapshot = At(state);
        var actions = Download.Restore(snapshot).AllowedActions;
        var retry = Apply(snapshot, new Retry(_newAttemptId, NewPublicationId: state is DownloadState.Canceled or DownloadState.Deleted ? _newPublicationId : null));
        var cancel = Apply(snapshot, new RequestCancel(state == DownloadState.Failed ? _newAttemptId : null));
        Assert.That(actions.Contains(DownloadAction.Retry), Is.EqualTo(retry.IsAccepted));
        Assert.That(actions.Contains(DownloadAction.Cancel), Is.EqualTo(cancel.IsAccepted && cancel.Intent == DownloadIntent.StopAndCleanUp));
    }

    [TestCaseSource(nameof(_operations))]
    public void RestartKeepsTheManualResumePointAndCancellationIntent(DownloadState stage)
    {
        var failed = Accept(At(stage), new ProcessInterrupted(_attemptId, stage), DownloadState.Failed);
        Assert.That(failed.CancelRequested, Is.EqualTo(stage == DownloadState.Canceling));
        Assert.That(failed.PublicationState, Is.EqualTo(stage switch
        {
            DownloadState.Uploading or DownloadState.Deleting => PublicationState.Unknown,
            DownloadState.CleaningUp => PublicationState.Available,
            _ => PublicationState.None
        }));
        var queued = Accept(failed, new Retry(_newAttemptId), DownloadState.Queued, DownloadIntent.EnqueueAttempt);
        Assert.That(queued.ResumeState, Is.EqualTo(stage));
    }

    [Test]
    public void LateWorkerEventsCannotChangeANewerAttemptOrDifferentStage()
    {
        var snapshot = At(DownloadState.Downloading);
        DownloadEvent[] stale = [new StartAttempt(_newAttemptId), new StageSucceeded(_newAttemptId, snapshot.State),
            new StageFailed(_newAttemptId, snapshot.State, new("failed")), new ProcessInterrupted(_newAttemptId, snapshot.State),
            new CancellationSucceeded(_newAttemptId, true), new ResultDeleted(_newAttemptId, snapshot.State, _publicationId, true)];
        foreach (var @event in stale)
            AssertRejected(snapshot, Apply(snapshot, @event), TransitionError.StaleAttempt);

        DownloadEvent[] wrongStage = [new StageSucceeded(_attemptId, DownloadState.Uploading, true),
            new StageFailed(_attemptId, DownloadState.Uploading, new("failed")), new ProcessInterrupted(_attemptId, DownloadState.Uploading),
            new CancellationSucceeded(_attemptId, true), new ResultDeleted(_attemptId, DownloadState.Completed, _publicationId, true)];
        foreach (var @event in wrongStage)
            AssertRejected(snapshot, Apply(snapshot, @event), TransitionError.UnexpectedStage);
    }

    [Test]
    public void CommandsRejectEmptyReusedAndUnexpectedIdentifiers()
    {
        var failed = FailAt(DownloadState.Uploading);
        var completed = At(DownloadState.Completed);
        var downloading = At(DownloadState.Downloading);
        var deleted = At(DownloadState.Deleted);
        foreach (var id in new[] { Guid.Empty, _attemptId })
        {
            AssertRejected(failed, Apply(failed, new Retry(id)), TransitionError.InvalidAttempt);
            AssertRejected(failed, Apply(failed, new RequestCancel(id)), TransitionError.InvalidAttempt);
            AssertRejected(completed, Apply(completed, new RequestDeleteResult(id)), TransitionError.InvalidAttempt);
        }
        AssertRejected(failed, Apply(failed, new RequestCancel()), TransitionError.InvalidAttempt);
        AssertRejected(downloading, Apply(downloading, new RequestCancel(_newAttemptId)), TransitionError.InvalidAttempt);
        AssertRejected(failed, Apply(failed, new Retry(_newAttemptId, NewPublicationId: _newPublicationId)), TransitionError.InvalidPublication);
        foreach (var id in new[] { Guid.Empty, _publicationId })
            AssertRejected(deleted, Apply(deleted, new RetryDeletedResult(_newAttemptId, id)), TransitionError.InvalidPublication);
        AssertRejected(completed, Apply(completed, new ResultDeleted(_attemptId, DownloadState.Completed, _newPublicationId, true)), TransitionError.InvalidPublication);
    }

    [TestCaseSource(nameof(_operations))]
    public void ActionsInQueueAndAfterFailureRespectTheResumeOperation(DownloadState stage)
    {
        var failed = FailAt(stage);
        var queued = Accept(failed, new Retry(_newAttemptId), DownloadState.Queued, DownloadIntent.EnqueueAttempt);
        var cancelable = stage is DownloadState.Downloading or DownloadState.Preparing or DownloadState.Uploading;
        Assert.That(Download.Restore(failed).AllowedActions, Is.EqualTo(cancelable
            ? new[] { DownloadAction.Retry, DownloadAction.Cancel } : new[] { DownloadAction.Retry }));
        Assert.That(Download.Restore(queued).AllowedActions, Is.EqualTo(cancelable
            ? new[] { DownloadAction.Cancel } : Array.Empty<DownloadAction>()));
    }

    [TestCase(AttemptStatus.Pending)]
    [TestCase(AttemptStatus.Running)]
    public void AnActiveAttemptBlocksCreationOfAnotherAttempt(AttemptStatus status)
    {
        var failed = FailAt(DownloadState.Deleting) with { AttemptStatus = status };
        AssertRejected(failed, Apply(failed, new Retry(_newAttemptId)), TransitionError.InvalidState);
        AssertRejected(failed, Apply(failed, new RequestDeleteResult(_newAttemptId)), TransitionError.InvalidState);
        Assert.That(Download.Restore(failed).AllowedActions, Is.Empty);
        var deleted = At(DownloadState.Deleted) with { AttemptStatus = status };
        AssertRejected(deleted, Apply(deleted, new RetryDeletedResult(_newAttemptId, _newPublicationId)), TransitionError.InvalidState);
    }

    [TestCase(null)]
    [TestCase(DownloadState.Queued)]
    [TestCase(DownloadState.Completed)]
    [TestCase(DownloadState.Failed)]
    [TestCase(DownloadState.Canceled)]
    [TestCase(DownloadState.Deleted)]
    public void QueueRequiresAProcessingOperationAsItsResumePoint(DownloadState? resume)
    {
        var queued = At(DownloadState.Queued) with { ResumeState = resume };
        AssertRejected(queued, Apply(queued, new StartAttempt(_attemptId)), TransitionError.InvalidResumeState);
    }

    [TestCase(DownloadState.Uploading)]
    [TestCase(DownloadState.Deleting)]
    [TestCase(DownloadState.Canceling)]
    public void UncertainStorageOutcomeCanBeRecordedWithoutClaimingAbsence(DownloadState stage)
    {
        var failed = Accept(At(stage), new StageFailed(_attemptId, stage, new("storage_unavailable"), PublicationUncertain: true), DownloadState.Failed);
        Assert.That(failed.PublicationState, Is.EqualTo(PublicationState.Unknown));
        Assert.That(failed.ResumeState, Is.EqualTo(stage));
    }

    [Test]
    public void FailedCancellationOfAnUploadRemainsCancellationAfterRestartAndRetry()
    {
        var canceling = Accept(At(DownloadState.Uploading), new RequestCancel(), DownloadState.Canceling, DownloadIntent.StopAndCleanUp);
        var failed = Accept(canceling, new ProcessInterrupted(_attemptId, DownloadState.Canceling), DownloadState.Failed);
        var queued = Accept(failed, new Retry(_newAttemptId), DownloadState.Queued, DownloadIntent.EnqueueAttempt);
        var resumed = Accept(queued, new StartAttempt(_newAttemptId), DownloadState.Canceling, DownloadIntent.ExecuteStage);
        Assert.That(resumed.CancelRequested, Is.True);
        Assert.That(resumed.PublicationState, Is.EqualTo(PublicationState.Unknown));
        AssertRejected(resumed, Apply(resumed, new CancellationSucceeded(_newAttemptId, true)), TransitionError.ConfirmationRequired);
    }

    [Test]
    public void UnknownPublicationDoesNotProveDeletion()
    {
        var completed = At(DownloadState.Completed) with { PublicationState = PublicationState.Unknown };
        AssertRejected(completed, Apply(completed, new ResultDeleted(_attemptId, DownloadState.Completed, _publicationId, false)), TransitionError.ConfirmationRequired);
        AssertRejected(completed, Apply(completed, new RetryDeletedResult(_newAttemptId, _newPublicationId)), TransitionError.InvalidState);
        Assert.That(completed.State, Is.EqualTo(DownloadState.Completed));
        Assert.That(completed.PublicationState, Is.EqualTo(PublicationState.Unknown));
    }

    [Test]
    public void AggregateOwnsTheLifecycleAcrossFailureRetryAndCompletion()
    {
        var download = Download.Create(_downloadId, _attemptId, _publicationId);
        var initial = download.Snapshot;
        DownloadEvent[] events = [new StartAttempt(_attemptId), new StageSucceeded(_attemptId, DownloadState.Downloading),
            new StageSucceeded(_attemptId, DownloadState.Preparing), new StageSucceeded(_attemptId, DownloadState.Uploading, PublicationConfirmed: true)];
        foreach (var @event in events)
            Assert.That(download.Apply(@event).IsAccepted, Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(download.State, Is.EqualTo(DownloadState.CleaningUp));
            Assert.That(download.PublicationState, Is.EqualTo(PublicationState.Available));
            Assert.That(download.HasActiveAttempt, Is.True);
            Assert.That(download.AllowedActions, Is.Empty);
            Assert.That(download.CanDeleteResult, Is.False);
        });

        var cleanup = download.Snapshot;
        var rejected = download.Apply(new RequestCancel());
        AssertRejected(cleanup, rejected, TransitionError.InvalidState);
        Assert.That(download.Snapshot, Is.SameAs(cleanup));
        Assert.That(download.Apply(new StageFailed(_attemptId, DownloadState.CleaningUp, new("cleanup_failed"))).IsAccepted, Is.True);
        var failed = download.Snapshot;
        Assert.That(download.FailedState, Is.EqualTo(DownloadState.CleaningUp));
        Assert.That(download.Failure?.Code, Is.EqualTo("cleanup_failed"));
        Assert.That(download.AllowedActions, Is.EqualTo(new[] { DownloadAction.Retry }));

        Assert.That(download.Apply(new Retry(_newAttemptId)).IsAccepted, Is.True);
        Assert.That(download.ResumeState, Is.EqualTo(DownloadState.CleaningUp));
        Assert.That(download.Apply(new StartAttempt(_newAttemptId)).IsAccepted, Is.True);
        Assert.That(download.Apply(new StageSucceeded(_newAttemptId, DownloadState.CleaningUp, LocalCleanupConfirmed: true)).IsAccepted, Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(download.Id, Is.EqualTo(_downloadId));
            Assert.That(download.AttemptId, Is.EqualTo(_newAttemptId));
            Assert.That(download.PublicationId, Is.EqualTo(_publicationId));
            Assert.That(download.State, Is.EqualTo(DownloadState.Completed));
            Assert.That(download.AttemptStatus, Is.EqualTo(AttemptStatus.Succeeded));
            Assert.That(download.HasActiveAttempt, Is.False);
            Assert.That(download.CancelRequested, Is.False);
            Assert.That(download.Failure, Is.Null);
            Assert.That(download.CanDeleteResult, Is.True);
            Assert.That(initial.State, Is.EqualTo(DownloadState.Queued));
            Assert.That(failed.Failure?.Code, Is.EqualTo("cleanup_failed"));
        });
    }

    [Test]
    public void RestoredAggregateContinuesFromItsSavedResumePoint()
    {
        var original = Download.Restore(At(DownloadState.Uploading));
        Assert.That(original.Apply(new ProcessInterrupted(_attemptId, DownloadState.Uploading)).IsAccepted, Is.True);
        var saved = original.Snapshot;
        var restored = Download.Restore(saved);
        Assert.That(restored.Snapshot, Is.EqualTo(saved));
        Assert.That(restored.Apply(new Retry(_newAttemptId)).IsAccepted, Is.True);
        Assert.That(restored.Apply(new StartAttempt(_newAttemptId)).IsAccepted, Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(restored.State, Is.EqualTo(DownloadState.Uploading));
            Assert.That(restored.PublicationState, Is.EqualTo(PublicationState.Unknown));
            Assert.That(original.State, Is.EqualTo(DownloadState.Failed));
            Assert.That(original.Snapshot, Is.SameAs(saved));
        });
    }

    [Test]
    public void ChangingAnExportedSnapshotDoesNotChangeTheAggregate()
    {
        var download = Download.Create(_downloadId, _attemptId, _publicationId);
        var saved = download.Snapshot;
        var changed = saved with { State = DownloadState.Completed, PublicationState = PublicationState.Available };
        Assert.That(changed.State, Is.EqualTo(DownloadState.Completed));
        Assert.That(download.State, Is.EqualTo(DownloadState.Queued));
        Assert.That(download.Snapshot, Is.SameAs(saved));
        Assert.That(download.Apply(new StartAttempt(_attemptId)).IsAccepted, Is.True);
        Assert.That(saved.State, Is.EqualTo(DownloadState.Queued));
    }

    [Test]
    public void NullEventOrSnapshotIsRejectedWithoutChangingTheAggregate()
    {
        var download = Download.Create(_downloadId, _attemptId, _publicationId);
        var saved = download.Snapshot;
        Assert.Throws<ArgumentNullException>(() => download.Apply(null!));
        Assert.That(download.Snapshot, Is.SameAs(saved));
        Assert.Throws<ArgumentNullException>(() => Download.Restore(null!));
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public void RestorationRejectsEmptyIdentifiers(int index)
    {
        var saved = Download.Create(_downloadId, _attemptId, _publicationId).Snapshot;
        saved = index switch
        {
            0 => saved with { Id = Guid.Empty },
            1 => saved with { AttemptId = Guid.Empty },
            _ => saved with { PublicationId = Guid.Empty }
        };
        Assert.Throws<ArgumentException>(() => Download.Restore(saved));
    }

    private static IEnumerable<DownloadState> AllStates => Enum.GetValues<DownloadState>();

    private static IEnumerable<TestCaseData> CancellationCases =>
        from state in new[] { DownloadState.Queued, DownloadState.Downloading, DownloadState.Preparing, DownloadState.Uploading, DownloadState.Failed }
        from resume in new[] { DownloadState.Downloading, DownloadState.Preparing, DownloadState.Uploading }
        where state is DownloadState.Queued or DownloadState.Failed || state == resume
        select new TestCaseData(state, resume);

    private static IEnumerable<TestCaseData> InvalidResumeCases =>
        from stage in _operations
        from resume in Enum.GetValues<DownloadState>()
        where !(stage == resume || stage == DownloadState.Preparing && resume == DownloadState.Downloading ||
            stage == DownloadState.Uploading && resume is DownloadState.Downloading or DownloadState.Preparing)
        select new TestCaseData(stage, resume);

    private static DownloadSnapshot At(DownloadState state) => new(_downloadId, _attemptId, _publicationId, state,
        state switch
        {
            DownloadState.Queued => AttemptStatus.Pending,
            DownloadState.Failed => AttemptStatus.Failed,
            DownloadState.Canceled => AttemptStatus.Canceled,
            DownloadState.Completed or DownloadState.Deleted => AttemptStatus.Succeeded,
            _ => AttemptStatus.Running
        }, ResumeState: state switch
        {
            DownloadState.Queued or DownloadState.Failed => DownloadState.Downloading,
            DownloadState.Completed or DownloadState.Deleted or DownloadState.Canceled => null,
            _ => state
        }, PublicationState: state switch
        {
            DownloadState.CleaningUp or DownloadState.Completed or DownloadState.Deleting => PublicationState.Available,
            DownloadState.Deleted => PublicationState.Deleted,
            _ => PublicationState.None
        }, CancelRequested: state is DownloadState.Canceling or DownloadState.Canceled);

    private static DownloadSnapshot FailAt(DownloadState state) => Accept(At(state), new StageFailed(_attemptId, state, new("stage_failed")), DownloadState.Failed);

    private static DownloadTransition Apply(DownloadSnapshot snapshot, DownloadEvent @event)
    {
        var download = Download.Restore(snapshot);
        var transition = download.Apply(@event);
        Assert.That(download.Snapshot, Is.SameAs(transition.Snapshot));
        return transition;
    }

    private static DownloadSnapshot Accept(DownloadSnapshot snapshot, DownloadEvent @event, DownloadState state, DownloadIntent intent = DownloadIntent.None)
    {
        var result = Apply(snapshot, @event);
        Assert.Multiple(() =>
        {
            Assert.That(result.Error, Is.Null);
            Assert.That(result.Snapshot.State, Is.EqualTo(state));
            Assert.That(result.Intent, Is.EqualTo(intent));
        });
        return result.Snapshot;
    }

    private static void AssertRejected(DownloadSnapshot snapshot, DownloadTransition result, TransitionError? error = null)
    {
        Assert.Multiple(() =>
        {
            Assert.That(result.IsAccepted, Is.False);
            if (error is not null) Assert.That(result.Error, Is.EqualTo(error));
            Assert.That(result.Snapshot, Is.SameAs(snapshot));
            Assert.That(result.Intent, Is.EqualTo(DownloadIntent.None));
        });
    }
}
