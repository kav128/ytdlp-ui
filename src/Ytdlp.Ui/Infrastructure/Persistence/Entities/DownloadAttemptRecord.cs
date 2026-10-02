using Ytdlp.Ui.Domain.Primitives;

namespace Ytdlp.Ui.Infrastructure.Persistence.Entities;

public sealed class DownloadAttemptRecord
{
    public Guid Id { get; set; }
    public Guid DownloadId { get; set; }
    public int Number { get; set; }
    public AttemptReason Reason { get; set; }
    public DownloadState InitialState { get; set; }
    public AttemptStatus Status { get; set; }
    public DateTimeOffset QueuedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorDetails { get; set; }
}
