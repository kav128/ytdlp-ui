namespace Ytdlp.Ui.Domain.Events;

public sealed record StartAttempt(Guid AttemptId) : DownloadEvent;
