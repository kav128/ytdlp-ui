using Ytdlp.Ui.Domain.Primitives;
using Ytdlp.Ui.Domain.ValueObjects;

namespace Ytdlp.Ui.Domain.Events;

public sealed record StageFailed(
    Guid AttemptId,
    DownloadState Stage,
    DownloadFailure Failure,
    bool PublicationUncertain = false) : DownloadEvent;
