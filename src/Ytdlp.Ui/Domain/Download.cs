using Ytdlp.Ui.Domain.Events;
using Ytdlp.Ui.Domain.Primitives;
using Ytdlp.Ui.Domain.ValueObjects;

namespace Ytdlp.Ui.Domain;

public sealed class Download
{
    private DownloadSnapshot snapshot;

    private Download(DownloadSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Id == Guid.Empty || snapshot.AttemptId == Guid.Empty || snapshot.PublicationId == Guid.Empty)
            throw new ArgumentException("Download, attempt and publication IDs must be non-empty.", nameof(snapshot));

        this.snapshot = snapshot;
    }

    public Guid Id => snapshot.Id;
    public Guid AttemptId => snapshot.AttemptId;
    public Guid PublicationId => snapshot.PublicationId;
    public DownloadState State => snapshot.State;
    public AttemptStatus AttemptStatus => snapshot.AttemptStatus;
    public DownloadState? ResumeState => snapshot.ResumeState;
    public DownloadState? FailedState => snapshot.FailedState;
    public PublicationState PublicationState => snapshot.PublicationState;
    public bool CancelRequested => snapshot.CancelRequested;
    public DownloadFailure? Failure => snapshot.Failure;
    public bool HasActiveAttempt => snapshot.HasActiveAttempt;
    public IReadOnlyList<DownloadAction> AllowedActions => DownloadStateMachine.GetAllowedActions(snapshot);
    public bool CanDeleteResult => DownloadStateMachine.CanDeleteResult(snapshot);
    public DownloadSnapshot Snapshot => snapshot;

    public static Download Create(Guid downloadId, Guid attemptId, Guid publicationId) =>
        new(new(downloadId, attemptId, publicationId, DownloadState.Queued,
            AttemptStatus.Pending, ResumeState: DownloadState.Downloading));

    public static Download Restore(DownloadSnapshot snapshot) => new(snapshot);

    public DownloadTransition Apply(DownloadEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        var transition = DownloadStateMachine.Apply(snapshot, @event);
        if (transition.IsAccepted)
            snapshot = transition.Snapshot;

        return transition;
    }
}
