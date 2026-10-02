namespace Ytdlp.Ui.Domain.Events;

public sealed record CancellationSucceeded(
    Guid AttemptId,
    bool LocalCleanupConfirmed,
    bool PublicationAbsentConfirmed = false) : DownloadEvent;
