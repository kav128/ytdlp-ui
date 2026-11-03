using Amazon.S3;
using Amazon.S3.Model;
using Ytdlp.Ui.Features.Library;

namespace Ytdlp.Ui.Infrastructure.Storage;

internal sealed class S3StorageUpload : IStorageUpload
{
    private const long _minimumPartSize = 5L * 1024 * 1024;
    private const long _maximumPartSize = 5L * 1024 * 1024 * 1024;
    private readonly IAmazonS3 _client;
    private readonly S3LibraryStorage _storage;
    private readonly ILogger _logger;
    private readonly string _bucket;
    private readonly string _operation = Guid.NewGuid().ToString("N");
    private readonly string _key;
    private readonly string _directory;
    private readonly long _partSize;
    private readonly List<PartETag> _parts = [];
    private string? _uploadId;
    private long _size;
    private bool _written;
    private bool _completed;
    private bool _initiationStarted;

    public S3StorageUpload(
        IAmazonS3 client,
        S3LibraryStorage storage,
        string bucket,
        string workDirectory,
        string name,
        long? requestLength,
        ILogger logger
    )
    {
        _client = client;
        _storage = storage;
        _bucket = bucket;
        _logger = logger;
        _key = $"uploads/{_operation}/{name}";

        if (System.Text.Encoding.UTF8.GetByteCount(_key) > 1024)
            throw new InvalidDataException("File name is too long.");

        _directory = Path.Combine(workDirectory, "uploads", _operation);
        // Content-Length includes multipart framing, so it is a safe upper bound on file bytes.
        // With an unknown length, maximum-sized disk parts avoid an artificial buffer * 10,000 cap.
        _partSize = CalculatePartSize(requestLength);
    }

    internal static long CalculatePartSize(long? requestLength) => requestLength.HasValue
                                                                       ? Math.Clamp(
                                                                           requestLength.Value / 10000
                                                                         + (requestLength.Value % 10000 == 0 ? 0 : 1),
                                                                           _minimumPartSize,
                                                                           _maximumPartSize
                                                                       )
                                                                       : _maximumPartSize;

    public async Task WriteAsync(Stream input, CancellationToken cancellationToken)
    {
        if (_written) throw new InvalidOperationException("Upload content can only be written once.");

        _written = true;
        Directory.CreateDirectory(_directory);

        await using var part = new FileStream(
            Path.Combine(_directory, "part"),
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose
        );

        var buffer = new byte[128 * 1024];
        var eof = false;

        try
        {
            while (!eof)
            {
                part.SetLength(0);
                part.Position = 0;

                while (part.Length < _partSize)
                {
                    int count;

                    try
                    {
                        count = await input.ReadAsync(
                                    buffer.AsMemory(0, (int)Math.Min(buffer.Length, _partSize - part.Length)),
                                    cancellationToken
                                );
                    }
                    catch (IOException error)
                    {
                        throw new InvalidDataException("The upload body is incomplete.", error);
                    }

                    if (count == 0)
                    {
                        eof = true;

                        break;
                    }

                    await part.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                }

                if (part.Length == 0) break;

                if (_parts.Count == 10000) throw new InvalidDataException("S3 multipart part count exceeded.");

                if (_uploadId is null)
                {
                    var initiate = new InitiateMultipartUploadRequest
                    {
                        BucketName = _bucket,
                        Key = _key,
                        ContentType = "application/octet-stream"
                    };

                    initiate.Metadata.Add("upload-operation", _operation);
                    _initiationStarted = true;
                    _uploadId = (await _client.InitiateMultipartUploadAsync(initiate, cancellationToken)).UploadId;
                }

                part.Position = 0;

                var uploaded = await _client.UploadPartAsync(
                                   new UploadPartRequest
                                   {
                                       BucketName = _bucket,
                                       Key = _key,
                                       UploadId = _uploadId,
                                       PartNumber = _parts.Count + 1,
                                       InputStream = part,
                                       PartSize = part.Length,
                                       IsLastPart = eof
                                   },
                                   cancellationToken
                               );

                _parts.Add(new PartETag(_parts.Count + 1, uploaded.ETag));
                _size += part.Length;
            }
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception error) when (S3LibraryStorage.IsStorageError(error)
                                   || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new StorageUnavailableException(error);
        }
    }

    public async Task<LibraryFile> CompleteAsync(CancellationToken cancellationToken)
    {
        if (!_written || _completed) throw new InvalidOperationException("Invalid upload completion.");

        try
        {
            if (_uploadId is null)
            {
                var request = new PutObjectRequest
                {
                    BucketName = _bucket,
                    Key = _key,
                    InputStream = Stream.Null,
                    ContentType = "application/octet-stream",
                    IfNoneMatch = "*"
                };

                request.Metadata.Add("upload-operation", _operation);
                await _client.PutObjectAsync(request, cancellationToken);
            }
            else
            {
                await _client.CompleteMultipartUploadAsync(
                    new CompleteMultipartUploadRequest
                    {
                        BucketName = _bucket,
                        Key = _key,
                        UploadId = _uploadId,
                        PartETags = _parts,
                        IfNoneMatch = "*"
                    },
                    cancellationToken
                );
            }
        }
        catch (Exception error) when (S3LibraryStorage.IsStorageError(error) || error is OperationCanceledException)
        {
            // Complete may have committed despite a lost response. Never repeat Complete or delete
            // a possibly published object; inspect this operation's unique key using a fresh token.
            using var verification = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var observed = await VerifyAsync(verification.Token);

            if (observed is not null) return observed;

            throw new StorageUnavailableException(error);
        }

        return await VerifyAsync(cancellationToken) ?? throw new StorageUnavailableException();
    }

    private async Task<LibraryFile?> VerifyAsync(CancellationToken cancellationToken)
    {
        var stored = await _storage.FindAsync(_key, cancellationToken);

        if (stored is null || stored.Size != _size || stored.OperationId != _operation) return null;

        _completed = true;

        return LibraryFile.Create(_key, stored.Size, stored.Modified);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_completed && (_uploadId is not null || _initiationStarted))
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            try
            {
                var ids = new List<string>();

                if (_uploadId is not null) ids.Add(_uploadId);
                else
                {
                    string? keyMarker = null, uploadMarker = null;
                    ListMultipartUploadsResponse page;

                    do
                    {
                        page = await _client.ListMultipartUploadsAsync(
                                   new ListMultipartUploadsRequest
                                   {
                                       BucketName = _bucket,
                                       Prefix = _key,
                                       KeyMarker = keyMarker,
                                       UploadIdMarker = uploadMarker
                                   },
                                   cleanup.Token
                               );

                        ids.AddRange(
                            (page.MultipartUploads ?? []).Where(upload => upload.Key == _key)
                                                         .Select(upload => upload.UploadId)
                        );

                        keyMarker = page.NextKeyMarker;
                        uploadMarker = page.NextUploadIdMarker;
                    } while (page.IsTruncated == true);
                }

                foreach (var id in ids)
                    await _client.AbortMultipartUploadAsync(
                        new AbortMultipartUploadRequest
                        {
                            BucketName = _bucket,
                            Key = _key,
                            UploadId = id
                        },
                        cleanup.Token
                    );
            }
            catch (Exception error) when (S3LibraryStorage.IsStorageError(error) || error is OperationCanceledException)
            {
                _logger.LogWarning(
                    "Could not abort storage upload {Operation}: {ErrorType}",
                    _operation,
                    error.GetType().Name
                );
            }
        }

        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
