using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using Ytdlp.Ui.Configuration;
using Ytdlp.Ui.Features.Library;

namespace Ytdlp.Ui.Infrastructure.Storage;

public sealed class S3LibraryStorage(
    IAmazonS3 client,
    IOptions<S3Options> options,
    IOptions<PathsOptions> paths,
    ILogger<S3LibraryStorage> logger
) : ILibraryStorage
{
    private readonly string _bucket = options.Value.Bucket;

    public async Task CheckReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await client.ListObjectsV2Async(
                new ListObjectsV2Request
                {
                    BucketName = _bucket,
                    MaxKeys = 1
                },
                cancellationToken
            );
        }
        catch (Exception error) when (IsStorageError(error)
                                   || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new StorageUnavailableException(error);
        }
    }

    public async Task<LibraryFile[]> ListAsync(CancellationToken cancellationToken)
    {
        try
        {
            var files = new List<LibraryFile>();
            string? continuation = null;

            do
            {
                var page = await client.ListObjectsV2Async(
                               new ListObjectsV2Request
                               {
                                   BucketName = _bucket,
                                   ContinuationToken = continuation
                               },
                               cancellationToken
                           );

                foreach (var item in page.S3Objects ?? [])
                    files.Add(LibraryFile.Create(item.Key, item.Size ?? 0, item.LastModified ?? DateTime.UtcNow));

                continuation = page.IsTruncated == true ? page.NextContinuationToken : null;

                if (page.IsTruncated == true && string.IsNullOrEmpty(continuation))
                    throw new StorageUnavailableException();
            } while (continuation is not null);

            return files.OrderByDescending(file => file.LastModified)
                        .ThenBy(file => file.Id, StringComparer.Ordinal)
                        .ToArray();
        }
        catch (Exception error) when (IsStorageError(error)
                                   || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new StorageUnavailableException(error);
        }
    }

    public async Task<StoredObject?> FindAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            var result = await client.GetObjectMetadataAsync(
                             new GetObjectMetadataRequest
                             {
                                 BucketName = _bucket,
                                 Key = key
                             },
                             cancellationToken
                         );

            return new(
                key,
                result.ContentLength,
                result.LastModified ?? DateTime.UtcNow,
                result.ETag,
                Metadata(result.Metadata, "publication-id"),
                Metadata(result.Metadata, "upload-operation"),
                Metadata(result.Metadata, "download-id"),
                Metadata(result.Metadata, "sha256")
            );
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            // HEAD uses generic 404 for an absent object AND an absent bucket. Only a successful
            // listing proves that the bucket is accessible, and confirms absence of this exact key.
            return await ConfirmAbsentAsync(key, cancellationToken)
                       ? null
                       : throw new StorageUnavailableException(error);
        }
        catch (Exception error) when (IsStorageError(error)
                                   || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new StorageUnavailableException(error);
        }
    }

    public async Task<StorageContent?> OpenAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            var result = await client.GetObjectAsync(
                             new GetObjectRequest
                             {
                                 BucketName = _bucket,
                                 Key = key
                             },
                             cancellationToken
                         );

            return new(result.ResponseStream, result.ContentLength, result);
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            return await ConfirmAbsentAsync(key, cancellationToken)
                       ? null
                       : throw new StorageUnavailableException(error);
        }
        catch (Exception error) when (IsStorageError(error)
                                   || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new StorageUnavailableException(error);
        }
    }

    public Task<IStorageUpload> BeginUploadAsync(string name, long? requestLength, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult<IStorageUpload>(
            new S3StorageUpload(
                client,
                this,
                _bucket,
                paths.Value.WorkDirectory,
                LibraryFile.SafeName(name),
                requestLength,
                logger
            )
        );
    }

    public Task<IStorageUpload> BeginPublicationAsync(
        PublicationUpload publication,
        Func<string, CancellationToken, Task> recordUploadId,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!publication.ObjectKey.StartsWith(
                $"downloads/{publication.DownloadId:D}/{publication.PublicationId:D}/",
                StringComparison.Ordinal
            ))
            throw new InvalidDataException("Publication key does not match its identity.");

        return Task.FromResult<IStorageUpload>(
            new S3StorageUpload(
                client,
                this,
                _bucket,
                paths.Value.WorkDirectory,
                "",
                publication.Size,
                logger,
                publication,
                recordUploadId
            )
        );
    }

    public async Task AbortPublicationAsync(
        Guid downloadId,
        Guid publicationId,
        string key,
        CancellationToken cancellationToken
    )
    {
        if (!key.StartsWith($"downloads/{downloadId:D}/{publicationId:D}/", StringComparison.Ordinal))
            throw new StorageConflictException();

        try
        {
            string? keyMarker = null, uploadMarker = null;
            ListMultipartUploadsResponse page;

            do
            {
                page = await client.ListMultipartUploadsAsync(
                           new ListMultipartUploadsRequest
                           {
                               BucketName = _bucket,
                               Prefix = key,
                               KeyMarker = keyMarker,
                               UploadIdMarker = uploadMarker
                           },
                           cancellationToken
                       );

                foreach (var upload in (page.MultipartUploads ?? []).Where(item => item.Key == key))
                    await client.AbortMultipartUploadAsync(
                        new AbortMultipartUploadRequest
                        {
                            BucketName = _bucket,
                            Key = key,
                            UploadId = upload.UploadId
                        },
                        cancellationToken
                    );

                keyMarker = page.NextKeyMarker;
                uploadMarker = page.NextUploadIdMarker;
            } while (page.IsTruncated == true);
        }
        catch (Exception error) when (IsStorageError(error)
                                   || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new StorageUnavailableException(error);
        }

        S3StorageUpload.CleanSpool(paths.Value.WorkDirectory, publicationId);
    }

    public async Task DeleteAsync(StoredObject file, CancellationToken cancellationToken)
    {
        try
        {
            await client.DeleteObjectAsync(
                new DeleteObjectRequest
                {
                    BucketName = _bucket,
                    Key = file.Key,
                    IfMatch = file.ETag
                },
                cancellationToken
            );

            if (await FindAsync(file.Key, cancellationToken) is not null) throw new StorageConflictException();
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new StorageConflictException();
        }
        catch (Exception error) when (IsStorageError(error)
                                   || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new StorageUnavailableException(error);
        }
    }

    private async Task<bool> ConfirmAbsentAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.ListObjectsV2Async(
                               new ListObjectsV2Request
                               {
                                   BucketName = _bucket,
                                   Prefix = key,
                                   MaxKeys = 1
                               },
                               cancellationToken
                           );

            return response.S3Objects?.All(item => item.Key != key) != false;
        }
        catch (Exception error) when (IsStorageError(error)
                                   || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new StorageUnavailableException(error);
        }
    }

    private static string? Metadata(MetadataCollection metadata, string key) =>
        metadata.Keys.Contains($"x-amz-meta-{key}") ? metadata[$"x-amz-meta-{key}"] : null;

    internal static bool IsStorageError(Exception error) =>
        error is AmazonS3Exception or HttpRequestException or IOException or TimeoutException;
}
