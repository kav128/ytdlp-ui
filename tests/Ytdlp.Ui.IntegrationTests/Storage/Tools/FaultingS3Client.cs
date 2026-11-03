using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace Ytdlp.Ui.IntegrationTests.Storage.Tools;

public sealed class FaultingS3Client(string endpoint) : AmazonS3Client(
    new BasicAWSCredentials("integration-user", "integration-password-only"),
    new AmazonS3Config
    {
        ServiceURL = endpoint,
        AuthenticationRegion = "us-east-1",
        ForcePathStyle = true,
        MaxErrorRetry = 0
    }
)
{
    public string? Fault { get; set; }
    public int CompletedCalls { get; private set; }
    public int UploadedParts { get; private set; }

    public override async Task<InitiateMultipartUploadResponse> InitiateMultipartUploadAsync(
        InitiateMultipartUploadRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var response = await base.InitiateMultipartUploadAsync(request, cancellationToken);

        if (Fault == "lost_initiate") throw new HttpRequestException("Injected lost Initiate response.");

        return response;
    }

    public override async Task<UploadPartResponse> UploadPartAsync(
        UploadPartRequest request,
        CancellationToken cancellationToken = default
    )
    {
        if (Fault == "upload_error" && UploadedParts > 0)
            throw new AmazonS3Exception("Injected part failure.") { StatusCode = HttpStatusCode.ServiceUnavailable };

        var response = await base.UploadPartAsync(request, cancellationToken);
        UploadedParts++;

        return response;
    }

    public override async Task<CompleteMultipartUploadResponse> CompleteMultipartUploadAsync(
        CompleteMultipartUploadRequest request,
        CancellationToken cancellationToken = default
    )
    {
        CompletedCalls++;
        var response = await base.CompleteMultipartUploadAsync(request, cancellationToken);

        if (Fault == "lost_complete") throw new HttpRequestException("Injected lost Complete response.");

        if (Fault is "mismatch_size" or "mismatch_metadata")
        {
            var metadata = await base.GetObjectMetadataAsync(
                               new GetObjectMetadataRequest
                               {
                                   BucketName = request.BucketName,
                                   Key = request.Key
                               },
                               cancellationToken
                           );

            var bytes = new byte[Fault == "mismatch_size" ? 1 : (int)metadata.ContentLength];
            Array.Fill(bytes, (byte)99);

            var replacement = new PutObjectRequest
            {
                BucketName = request.BucketName,
                Key = request.Key,
                InputStream = new MemoryStream(bytes),
                AutoCloseStream = true
            };

            if (Fault == "mismatch_size")
                replacement.Metadata.Add("upload-operation", metadata.Metadata["x-amz-meta-upload-operation"]);

            await base.PutObjectAsync(replacement, cancellationToken);

            throw new HttpRequestException("Injected uncertain Complete with a replaced object.");
        }

        return response;
    }

    public override Task<GetObjectMetadataResponse> GetObjectMetadataAsync(
        GetObjectMetadataRequest request,
        CancellationToken cancellationToken = default
    )
    {
        if (Fault == "forbidden")
            throw new AmazonS3Exception("Injected private S3 error.") { StatusCode = HttpStatusCode.Forbidden };

        if (Fault == "network") throw new HttpRequestException("Injected private network error.");
        if (Fault == "timeout") throw new TaskCanceledException("Injected private storage timeout.");

        return base.GetObjectMetadataAsync(request, cancellationToken);
    }

    public override async Task<DeleteObjectResponse> DeleteObjectAsync(
        DeleteObjectRequest request,
        CancellationToken cancellationToken = default
    )
    {
        if (Fault == "replace_before_delete")
            await base.PutObjectAsync(
                new PutObjectRequest
                {
                    BucketName = request.BucketName,
                    Key = request.Key,
                    ContentBody = "replacement"
                },
                cancellationToken
            );

        if (Fault == "delete_error")
            throw new AmazonS3Exception("Injected deletion failure.")
            {
                StatusCode = HttpStatusCode.ServiceUnavailable
            };

        return await base.DeleteObjectAsync(request, cancellationToken);
    }
}
