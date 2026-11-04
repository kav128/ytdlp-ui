using Ytdlp.Ui.Domain.Primitives;

namespace Ytdlp.Ui.Infrastructure.Media;

public sealed record MediaTrack(
    Guid ArtifactId,
    ArtifactKind Kind,
    string FormatId,
    string Extension,
    string VideoCodec,
    string AudioCodec,
    int? Height,
    long? Size,
    string RelativePath
);

public sealed record MediaPlan(
    string Title,
    MediaTrack[] Tracks,
    Guid OutputArtifactId,
    string OutputPath,
    string OutputExtension
);

public sealed record MediaProbe(
    bool HasVideo,
    bool HasAudio,
    int? Height,
    string Format,
    string? VideoCodec,
    string? AudioCodec
);

public sealed class MediaProcessingException(string code, string? details = null, bool toolDiagnostic = false)
    : Exception(details ?? code)
{
    public string Code { get; } = code;
    public string? DiagnosticDetails { get; } = toolDiagnostic ? details : null;
}
