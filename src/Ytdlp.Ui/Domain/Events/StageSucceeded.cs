using Ytdlp.Ui.Domain.Primitives;

namespace Ytdlp.Ui.Domain.Events;

public sealed record StageSucceeded(
    Guid AttemptId,
    DownloadState Stage,
    bool PublicationConfirmed = false,
    bool LocalCleanupConfirmed = false) : DownloadEvent;
