namespace Ytdlp.Ui.Domain.Events;

public sealed record RequestCancel(Guid? NewAttemptId = null) : DownloadEvent;
