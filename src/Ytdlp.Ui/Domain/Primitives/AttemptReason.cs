namespace Ytdlp.Ui.Domain.Primitives;

public enum AttemptReason
{
    Initial,
    Retry,
    RetryDeletedResult,
    Cancellation,
    DeleteResult
}
