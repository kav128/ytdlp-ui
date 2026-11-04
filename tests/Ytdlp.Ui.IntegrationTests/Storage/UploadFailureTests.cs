using System.Net;
using System.Net.Http.Json;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Ytdlp.Ui.Configuration;
using Ytdlp.Ui.Features.Library;
using Ytdlp.Ui.Infrastructure.Storage;
using Ytdlp.Ui.IntegrationTests.Storage.Tools;
using Ytdlp.Ui.IntegrationTests.Tools;

namespace Ytdlp.Ui.IntegrationTests.Storage;

[TestFixture]
public sealed class UploadFailureTests : SetupBase
{
    private FaultingS3Client _faults = null!;

    protected override void ConfigureApplication(IWebHostBuilder builder) => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAmazonS3>();
            services.AddSingleton<IAmazonS3>(_ => _faults = new FaultingS3Client(SetupGlobal.Endpoint));
        }
    );

    [TestCase("lost_initiate")]
    [TestCase("upload_error")]
    public async Task FailedUploadRemovesOnlyItsPartsAndScratchDirectory(string fault)
    {
        await OidcBrowser.SignInAsync(Factory, Client);
        await PutAsync("keep-object", [1]);

        var other = await S3.InitiateMultipartUploadAsync(
                        new InitiateMultipartUploadRequest
                        {
                            BucketName = Bucket,
                            Key = "keep-parts"
                        }
                    );

        _faults.Fault = fault;
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(new byte[11 * 1024 * 1024]), "file", "failed.bin");
        using var response = await Client.PostAsync("/api/library/uploads", form);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Not.Contain("Injected"));

        Assert.That(
            (await S3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = Bucket })).S3Objects
                                                                                           .Select(item => item.Key),
            Is.EqualTo(new[] { "keep-object" })
        );

        var uploads = await S3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = Bucket });
        Assert.That(uploads.MultipartUploads.Select(item => item.UploadId), Is.EqualTo(new[] { other.UploadId }));
        AssertScratchEmpty();
    }

    [Test]
    public async Task LostCompleteResponseIsConfirmedWithoutRepeatingOrRemovingObject()
    {
        await OidcBrowser.SignInAsync(Factory, Client);
        _faults.Fault = "lost_complete";
        var bytes = new byte[6 * 1024 * 1024];
        new Random(8).NextBytes(bytes);
        var file = await UploadAsync(bytes);
        Assert.That(_faults.CompletedCalls, Is.EqualTo(1));
        using var content = await Client.GetAsync(file.ContentUrl);
        Assert.That(await content.Content.ReadAsByteArrayAsync(), Is.EqualTo(bytes));

        Assert.That(
            (await S3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = Bucket }))
           .MultipartUploads,
            Is.Null.Or.Empty
        );

        AssertScratchEmpty();
    }

    [Test]
    public async Task TruncatedMultipartIsBadRequestAndNeverCompleted()
    {
        await OidcBrowser.SignInAsync(Factory, Client);

        var prefix = Encoding.UTF8.GetBytes(
            "--broken\r\nContent-Disposition: form-data; name=\"file\"; filename=\"truncated.bin\"\r\n\r\n"
        );

        var body = new byte[prefix.Length + 6 * 1024 * 1024];
        prefix.CopyTo(body, 0);
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new("multipart/form-data") { Parameters = { new("boundary", "broken") } };
        using var response = await Client.PostAsync("/api/library/uploads", content);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(_faults.CompletedCalls, Is.Zero);

        Assert.That(
            (await S3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = Bucket })).S3Objects,
            Is.Null.Or.Empty
        );

        Assert.That(
            (await S3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = Bucket }))
           .MultipartUploads,
            Is.Null.Or.Empty
        );

        AssertScratchEmpty();
    }

    [TestCase("mismatch_size")]
    [TestCase("mismatch_metadata")]
    public async Task UncertainCompleteWithWrongMetadataOrSizeIsNotSuccessAndNeverDeletesReplacement(string fault)
    {
        await OidcBrowser.SignInAsync(Factory, Client);
        _faults.Fault = fault;
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(new byte[6 * 1024 * 1024]), "file", "uncertain.bin");
        using var response = await Client.PostAsync("/api/library/uploads", form);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That(_faults.CompletedCalls, Is.EqualTo(1));
        var listing = await S3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = Bucket });
        Assert.That(listing.S3Objects, Has.Count.EqualTo(1));
        using var replacement = await S3.GetObjectAsync(Bucket, listing.S3Objects[0].Key);
        Assert.That(replacement.ResponseStream.ReadByte(), Is.EqualTo(99));
        AssertScratchEmpty();
    }

    [Test]
    public async Task CancellationStopsTheReadAndAbortsPreviouslyWrittenParts()
    {
        var storage = Factory.Services.GetRequiredService<ILibraryStorage>();
        using var cancel = new CancellationTokenSource();

        await using (var upload = await storage.BeginUploadAsync("canceled.bin", 12 * 1024 * 1024, cancel.Token))
        {
            await using var input = new CancelingReadStream(cancel);

            await Assert.ThrowsAsync<OperationCanceledException>(async () => await upload.WriteAsync(
                                                                                 input,
                                                                                 cancel.Token
                                                                             )
            );
        }

        Assert.That(_faults.UploadedParts, Is.EqualTo(1));

        Assert.That(
            (await S3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = Bucket }))
           .MultipartUploads,
            Is.Null.Or.Empty
        );

        Assert.That(
            (await S3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = Bucket })).S3Objects,
            Is.Null.Or.Empty
        );

        AssertScratchEmpty();
    }

    private void AssertScratchEmpty()
    {
        var work = Factory.Services.GetRequiredService<IOptions<PathsOptions>>().Value.WorkDirectory;
        Assert.That(Directory.Exists(work) ? Directory.GetFiles(work, "*", SearchOption.AllDirectories) : [], Is.Empty);
    }

    private sealed class CancelingReadStream(CancellationTokenSource cancel) : Stream
    {
        private long _read;

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            if (_read >= 6 * 1024 * 1024)
            {
                cancel.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            buffer.Span.Clear();
            _read += buffer.Length;
            await Task.Yield();

            return buffer.Length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
