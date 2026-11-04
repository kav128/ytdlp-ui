namespace Ytdlp.Ui.Features.Downloads;

public sealed record DownloadView(
    Guid Id,
    string Url,
    string? Title,
    string State,
    string? ResumeState,
    double? Progress,
    DateTimeOffset CreatedAt,
    string? ErrorCode,
    string? Error,
    string[] AllowedActions,
    DownloadAttemptView[] Attempts,
    Guid CurrentAttemptId,
    Guid PublicationId,
    string PublicationState,
    string? FailedState,
    bool CancelRequested,
    long Version
);

public sealed record DownloadAttemptView(
    int Number,
    string State,
    DateTimeOffset CreatedAt,
    string? Error,
    Guid Id,
    string Reason,
    string Status,
    string InitialState,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? ErrorCode,
    string? ContinuationReasonCode,
    DownloadStageView[] Stages
);

public sealed record DownloadStageView(
    Guid Id,
    string Stage,
    string Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    double? Progress,
    string? ErrorCode,
    string? Error,
    Guid? ArtifactId,
    Guid? ReusedFromStageId
);

public sealed record DownloadCommandResult(
    DownloadView? Download = null,
    int Status = 200,
    string? Code = null,
    Guid? ExistingDownloadId = null
);
