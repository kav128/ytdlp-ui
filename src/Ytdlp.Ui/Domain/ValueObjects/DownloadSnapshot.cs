using Ytdlp.Ui.Domain.Primitives;

namespace Ytdlp.Ui.Domain.ValueObjects;

public sealed record DownloadSnapshot(
    Guid Id,
    Guid AttemptId,
    Guid PublicationId,
    DownloadState State,
    AttemptStatus AttemptStatus,
    DownloadState? ResumeState = null,
    DownloadState? FailedState = null,
    PublicationState PublicationState = PublicationState.None,
    bool CancelRequested = false,
    DownloadFailure? Failure = null)
{
    public bool HasActiveAttempt => AttemptStatus is AttemptStatus.Pending or AttemptStatus.Running;
}
