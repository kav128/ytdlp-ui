using System.Net;
using Amazon.S3;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ytdlp.Ui.IntegrationTests.Storage.Tools;

namespace Ytdlp.Ui.IntegrationTests.Storage;

[TestFixture]
public sealed class ReadinessTests : SetupBase
{
    private FaultingS3Client _client = null!;

    protected override void ConfigureApplication(IWebHostBuilder builder) => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAmazonS3>();
            services.AddSingleton<IAmazonS3>(_ => _client = new FaultingS3Client(SetupGlobal.Endpoint));
        }
    );

    [Test]
    public async Task ReadinessChecksTheActualBucketWithOneSmallReadOnlyRequest()
    {
        await PutAsync("external.txt", [1, 2]);
        using var response = await Client.GetAsync("/health/ready");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(_client.ListCalls, Is.EqualTo(1));
        Assert.That(_client.LastListLimit, Is.EqualTo(1));
        Assert.That(_client.DeleteCalls, Is.Zero);
        Assert.That(_client.UploadedParts, Is.Zero);
        Assert.That((await S3.GetObjectMetadataAsync(Bucket, "external.txt")).ContentLength, Is.EqualTo(2));
    }

    [TestCase("forbidden")]
    [TestCase("network")]
    [TestCase("timeout")]
    public async Task StorageErrorsAreGenericUnhealthyResponses(string fault)
    {
        _client.Fault = fault;
        using var ready = await Client.GetAsync("/health/ready");
        Assert.That(ready.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That(await ready.Content.ReadAsStringAsync(), Is.EqualTo("Unhealthy"));
        using var live = await Client.GetAsync("/health/live");
        Assert.That(live.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task MissingBucketIsUnhealthyAndIsNeverCreatedByTheProbe()
    {
        await S3.DeleteBucketAsync(Bucket);
        using var ready = await Client.GetAsync("/health/ready");
        Assert.That(ready.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));

        Assert.That(
            (await S3.ListBucketsAsync()).Buckets?.Any(bucket => bucket.BucketName == Bucket) ?? false,
            Is.False
        );
    }
}
