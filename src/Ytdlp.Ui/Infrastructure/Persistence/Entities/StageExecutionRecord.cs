using Ytdlp.Ui.Domain.Primitives;

namespace Ytdlp.Ui.Infrastructure.Persistence.Entities;

public sealed class StageExecutionRecord
{
    public Guid Id { get; set; }
    public Guid DownloadId { get; set; }
    public Guid AttemptId { get; set; }
    public ProcessingStage Stage { get; set; }
    public StageStatus Status { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public Guid? ReusedFromStageId { get; set; }
    public Guid? ArtifactId { get; set; }
    public double? Progress { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorDetails { get; set; }
}
