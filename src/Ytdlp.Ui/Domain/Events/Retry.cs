using Ytdlp.Ui.Domain.Primitives;

namespace Ytdlp.Ui.Domain.Events;

public sealed record Retry(Guid NewAttemptId, DownloadState? ResumeState = null, Guid? NewPublicationId = null) : DownloadEvent;
