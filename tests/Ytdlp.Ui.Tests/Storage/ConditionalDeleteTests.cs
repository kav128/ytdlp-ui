using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Ytdlp.Ui.Configuration;
using Ytdlp.Ui.Infrastructure.Storage;

namespace Ytdlp.Ui.Tests.Storage;

[TestFixture]
public sealed class ConditionalDeleteTests
{
    [Test]
    public async Task SendsObservedEtagAndMapsSupportedPreconditionFailureToConflict()
    {
        using var client = Substitute.For<IAmazonS3>();

        client.DeleteObjectAsync(Arg.Any<DeleteObjectRequest>(), Arg.Any<CancellationToken>())
              .Returns<Task<DeleteObjectResponse>>(_ => throw new AmazonS3Exception("precondition")
                   {
                       StatusCode = HttpStatusCode.PreconditionFailed
                   }
               );

        var storage = new S3LibraryStorage(
            client,
            Options.Create(new S3Options { Bucket = "bucket" }),
            Options.Create(new PathsOptions()),
            NullLogger<S3LibraryStorage>.Instance
        );

        await Assert.ThrowsAsync<StorageConflictException>(async () => await storage.DeleteAsync(
                                                                           new StoredObject(
                                                                               "own/key",
                                                                               1,
                                                                               DateTimeOffset.UtcNow,
                                                                               "\"observed-etag\""
                                                                           ),
                                                                           CancellationToken.None
                                                                       )
        );

        await client.Received(1)
                    .DeleteObjectAsync(
                         Arg.Is<DeleteObjectRequest>(request =>
                                                         request.BucketName == "bucket"
                                                      && request.Key == "own/key"
                                                      && request.IfMatch == "\"observed-etag\""
                         ),
                         Arg.Any<CancellationToken>()
                     );
    }
}
