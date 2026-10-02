using Ytdlp.Ui.Domain.Primitives;

namespace Ytdlp.Ui.Domain.Events;

public sealed record ProcessInterrupted(Guid AttemptId, DownloadState Stage) : DownloadEvent;
