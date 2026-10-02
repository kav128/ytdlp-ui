namespace Ytdlp.Ui.Domain.Events;

public sealed record RequestDeleteResult(Guid NewAttemptId) : DownloadEvent;
