using Ytdlp.Ui.Domain.Primitives;

namespace Ytdlp.Ui.Infrastructure.Persistence.Entities;

public sealed class DownloadArtifactRecord
{
    public Guid Id { get; set; }
    public Guid DownloadId { get; set; }
    public Guid WorkspaceGeneration { get; set; }
    public ArtifactKind Kind { get; set; }
    public string RelativePath { get; set; } = null!;
    public long? Size { get; set; }
    public string? Sha256 { get; set; }
    public bool IsReady { get; set; }
}
