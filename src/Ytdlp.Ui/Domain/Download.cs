using Ytdlp.Ui.Domain.Events;
using Ytdlp.Ui.Domain.Primitives;
using Ytdlp.Ui.Domain.ValueObjects;

namespace Ytdlp.Ui.Domain;

public sealed class Download
{
    private DownloadSnapshot _snapshot;

    private Download(DownloadSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.Id == Guid.Empty || snapshot.AttemptId == Guid.Empty || snapshot.PublicationId == Guid.Empty)
            throw new ArgumentException("Download, attempt and publication IDs must be non-empty.", nameof(snapshot));

        _snapshot = snapshot;
    }

    public Guid Id => _snapshot.Id;
    public Guid AttemptId => _snapshot.AttemptId;
    public Guid PublicationId => _snapshot.PublicationId;
    public DownloadState State => _snapshot.State;
    public AttemptStatus AttemptStatus => _snapshot.AttemptStatus;
    public DownloadState? ResumeState => _snapshot.ResumeState;
    public DownloadState? FailedState => _snapshot.FailedState;
    public PublicationState PublicationState => _snapshot.PublicationState;
    public bool CancelRequested => _snapshot.CancelRequested;
    public DownloadFailure? Failure => _snapshot.Failure;
    public bool HasActiveAttempt => _snapshot.HasActiveAttempt;
    public IReadOnlyList<DownloadAction> AllowedActions => DownloadStateMachine.GetAllowedActions(_snapshot);
    public bool CanDeleteResult => DownloadStateMachine.CanDeleteResult(_snapshot);
    public DownloadSnapshot Snapshot => _snapshot;

    public static Download Create(Guid downloadId, Guid attemptId, Guid publicationId) =>
        new(
            new(
                downloadId,
                attemptId,
                publicationId,
                DownloadState.Queued,
                AttemptStatus.Pending,
                ResumeState: DownloadState.Downloading
            )
        );

    public static Download Restore(DownloadSnapshot snapshot) => new(snapshot);

    public DownloadTransition Apply(DownloadEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        var transition = DownloadStateMachine.Apply(_snapshot, @event);

        if (transition.IsAccepted)
            _snapshot = transition.Snapshot;

        return transition;
    }
}
