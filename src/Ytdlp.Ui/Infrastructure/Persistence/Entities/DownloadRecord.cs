using Ytdlp.Ui.Domain;
using Ytdlp.Ui.Domain.Primitives;
using Ytdlp.Ui.Domain.ValueObjects;

namespace Ytdlp.Ui.Infrastructure.Persistence.Entities;

public sealed class DownloadRecord
{
    public Guid Id { get; set; }
    public string OriginalUrl { get; set; } = null!;
    public string NormalizedUrl { get; set; } = null!;
    public string? Title { get; set; }
    public Guid CurrentAttemptId { get; set; }
    public Guid PublicationId { get; set; }
    public Guid WorkspaceGeneration { get; set; }
    public DownloadState State { get; set; }
    public AttemptStatus AttemptStatus { get; set; }
    public DownloadState? ResumeState { get; set; }
    public DownloadState? FailedState { get; set; }
    public PublicationState PublicationState { get; set; }
    public bool CancelRequested { get; set; }
    public DownloadIntent Intent { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorDetails { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; }

    public static DownloadRecord Create(Download download, string originalUrl, string normalizedUrl,
        Guid workspaceGeneration, DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedUrl);
        if (workspaceGeneration == Guid.Empty)
            throw new ArgumentException("Workspace generation must be non-empty.", nameof(workspaceGeneration));

        var record = new DownloadRecord
        {
            Id = download.Id,
            OriginalUrl = originalUrl,
            NormalizedUrl = normalizedUrl,
            WorkspaceGeneration = workspaceGeneration,
            CreatedAt = createdAt
        };
        record.Capture(download, DownloadIntent.EnqueueAttempt, createdAt);
        return record;
    }

    public Download RestoreAggregate() => Download.Restore(new DownloadSnapshot(
        Id, CurrentAttemptId, PublicationId, State, AttemptStatus, ResumeState, FailedState,
        PublicationState, CancelRequested, ErrorCode is null ? null : new DownloadFailure(ErrorCode, ErrorDetails)));

    public void Capture(Download download, DownloadIntent intent, DateTimeOffset updatedAt)
    {
        if (download.Id != Id)
            throw new ArgumentException("Cannot capture a different download.", nameof(download));

        var snapshot = download.Snapshot;
        CurrentAttemptId = snapshot.AttemptId;
        PublicationId = snapshot.PublicationId;
        State = snapshot.State;
        AttemptStatus = snapshot.AttemptStatus;
        ResumeState = snapshot.ResumeState;
        FailedState = snapshot.FailedState;
        PublicationState = snapshot.PublicationState;
        CancelRequested = snapshot.CancelRequested;
        ErrorCode = snapshot.Failure?.Code;
        ErrorDetails = snapshot.Failure?.Details;
        Intent = intent;
        UpdatedAt = updatedAt;
    }
}
