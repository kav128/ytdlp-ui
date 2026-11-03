using Ytdlp.Ui.Domain.Primitives;

namespace Ytdlp.Ui.Domain.Events;

public sealed record ArtifactsUnavailable(Guid AttemptId, DownloadState Stage, DownloadState ResumeState)
    : DownloadEvent;
