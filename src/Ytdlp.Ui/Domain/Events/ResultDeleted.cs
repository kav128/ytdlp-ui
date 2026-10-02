using Ytdlp.Ui.Domain.Primitives;

namespace Ytdlp.Ui.Domain.Events;

public sealed record ResultDeleted(
    Guid AttemptId,
    DownloadState Stage,
    Guid PublicationId,
    bool AbsenceConfirmed) : DownloadEvent;
