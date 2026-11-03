using Ytdlp.Ui.Features.Library;

namespace Ytdlp.Ui.Infrastructure.Storage;

public sealed record StoredObject(
    string Key,
    long Size,
    DateTimeOffset Modified,
    string ETag,
    string? PublicationId = null,
    string? OperationId = null,
    string? DownloadId = null,
    string? Sha256 = null
);

public sealed record PublicationUpload(Guid DownloadId, Guid PublicationId, string ObjectKey, long Size, string Sha256);

public interface ILibraryStorage
{
    Task<LibraryFile[]> ListAsync(CancellationToken cancellationToken);
    Task<StoredObject?> FindAsync(string key, CancellationToken cancellationToken);
    Task<StorageContent?> OpenAsync(string key, CancellationToken cancellationToken);
    Task<IStorageUpload> BeginUploadAsync(string name, long? requestLength, CancellationToken cancellationToken);

    Task<IStorageUpload> BeginPublicationAsync(
        PublicationUpload publication,
        Func<string, CancellationToken, Task> recordUploadId,
        CancellationToken cancellationToken
    );

    Task DeleteAsync(StoredObject file, CancellationToken cancellationToken);
}

public interface IStorageUpload : IAsyncDisposable
{
    Task WriteAsync(Stream input, CancellationToken cancellationToken);
    Task<LibraryFile> CompleteAsync(CancellationToken cancellationToken);
}

public sealed class StorageContent(Stream stream, long length, IDisposable owner) : IDisposable
{
    public Stream Stream { get; } = stream;
    public long Length { get; } = length;
    public void Dispose() => owner.Dispose();
}

public sealed class StorageUnavailableException(Exception? inner = null) : Exception("Storage is unavailable.", inner);

public sealed class StorageConflictException : Exception;
