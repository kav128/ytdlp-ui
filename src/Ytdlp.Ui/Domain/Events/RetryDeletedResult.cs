namespace Ytdlp.Ui.Domain.Events;

public sealed record RetryDeletedResult(Guid NewAttemptId, Guid NewPublicationId) : DownloadEvent;
