using Ytdlp.Ui.Domain.Primitives;

namespace Ytdlp.Ui.Infrastructure.Persistence.Entities;

public sealed class StorageTransferRecord
{
    public Guid Id { get; set; }
    public Guid DownloadId { get; set; }
    public Guid ArtifactId { get; set; }
    public Guid PublicationId { get; set; }
    public string ObjectKey { get; set; } = null!;
    public string? MultipartUploadId { get; set; }
    public TransferStatus Status { get; set; }
    public long ExpectedSize { get; set; }
    public string? Sha256 { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
