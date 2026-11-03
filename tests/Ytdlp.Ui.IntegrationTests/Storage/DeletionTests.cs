using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ytdlp.Ui.Domain;
using Ytdlp.Ui.Domain.Primitives;
using Ytdlp.Ui.Domain.ValueObjects;
using Ytdlp.Ui.Features.Library;
using Ytdlp.Ui.Infrastructure.Persistence;
using Ytdlp.Ui.Infrastructure.Persistence.Entities;
using Ytdlp.Ui.IntegrationTests.Storage.Tools;
using Ytdlp.Ui.IntegrationTests.Tools;

namespace Ytdlp.Ui.IntegrationTests.Storage;

[TestFixture]
public sealed class DeletionTests : SetupBase
{
    private FaultingS3Client _faults = null!;

    protected override void ConfigureApplication(IWebHostBuilder builder) => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAmazonS3>();
            services.AddSingleton<IAmazonS3>(_ => _faults = new FaultingS3Client(SetupGlobal.Endpoint));
        }
    );

    [Test]
    public async Task OwnPublicationIsDeletedThroughAggregateAndKeepsHistory()
    {
        var record = await SeedPublicationAsync();
        await OidcBrowser.SignInAsync(Factory, Client);
        using var response = await DeleteAsync();
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        using var scope = Factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var saved = await database.Downloads.SingleAsync();
        Assert.That(saved.Id, Is.EqualTo(record.Id));
        Assert.That(saved.State, Is.EqualTo(DownloadState.Deleted));
        Assert.That(saved.PublicationState, Is.EqualTo(PublicationState.Deleted));
        Assert.That(await database.Attempts.CountAsync(), Is.EqualTo(2));

        Assert.That(
            await database.Stages.CountAsync(item => item.Stage == ProcessingStage.DeleteResult
                                                  && item.Status == StageStatus.Succeeded
            ),
            Is.EqualTo(1)
        );

        Assert.That(await database.Artifacts.CountAsync(), Is.EqualTo(1));
        Assert.That(await database.Transfers.CountAsync(), Is.EqualTo(1));
        using var replay = await DeleteAsync();
        Assert.That(replay.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(await database.Attempts.CountAsync(), Is.EqualTo(2));
    }

    [Test]
    public async Task CleaningUpCannotDeleteAnAlreadyPublishedResult()
    {
        await SeedPublicationAsync(cleanup: true);
        await OidcBrowser.SignInAsync(Factory, Client);
        using var response = await DeleteAsync();
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That((await S3.GetObjectMetadataAsync(Bucket, "own/result.bin")).ContentLength, Is.EqualTo(3));
        using var scope = Factory.Services.CreateScope();

        Assert.That(
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Attempts.CountAsync(),
            Is.EqualTo(1)
        );
    }

    [TestCase("forbidden")]
    [TestCase("network")]
    [TestCase("timeout")]
    [TestCase("delete_error")]
    public async Task StorageFailureNeverConfirmsADeletedPublication(string fault)
    {
        await SeedPublicationAsync();
        await OidcBrowser.SignInAsync(Factory, Client);
        _faults.Fault = fault;
        using var response = await DeleteAsync();
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        using var scope = Factory.Services.CreateScope();
        var record = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Downloads.SingleAsync();
        Assert.That(record.State, Is.Not.EqualTo(DownloadState.Deleted));
        Assert.That(record.PublicationState, Is.EqualTo(PublicationState.Available));
        Assert.That((await S3.GetObjectMetadataAsync(Bucket, "own/result.bin")).ContentLength, Is.EqualTo(3));
    }

    [Test]
    public async Task LateDeletionOfOldPublicationCannotTouchNewPublicationKey()
    {
        var old = await SeedPublicationAsync();
        var newPublication = Guid.NewGuid();
        var newKey = $"downloads/{old.Id:D}/{newPublication:D}/new.bin";
        await PutAsync(newKey, [9, 8, 7], newPublication.ToString("D"));

        using (var scope = Factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var current = await database.Downloads.SingleAsync();
            current.PublicationId = newPublication;
            await database.SaveChangesAsync();
        }

        await OidcBrowser.SignInAsync(Factory, Client);
        using var response = await DeleteAsync();
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        using var content = await S3.GetObjectAsync(Bucket, newKey);
        using var bytes = new MemoryStream();
        await content.ResponseStream.CopyToAsync(bytes);

        Assert.That(
            bytes.ToArray(),
            Is.EqualTo(
                new byte[]
                {
                    9,
                    8,
                    7
                }
            )
        );

        using var check = Factory.Services.CreateScope();

        Assert.That(
            await check.ServiceProvider.GetRequiredService<AppDbContext>().Attempts.CountAsync(),
            Is.EqualTo(1)
        );
    }

    [Test]
    public async Task ReportsLegacyMinioConditionalDeleteCapabilityWithoutAssumingAtomicExternalOverwrite()
    {
        await PutAsync("capability/probe", [1]);
        var original = await S3.GetObjectMetadataAsync(Bucket, "capability/probe");
        await PutAsync("capability/probe", [2]);

        try
        {
            await S3.DeleteObjectAsync(
                new DeleteObjectRequest
                {
                    BucketName = Bucket,
                    Key = "capability/probe",
                    IfMatch = original.ETag
                }
            );

            var listing = await S3.ListObjectsV2Async(
                              new ListObjectsV2Request
                              {
                                  BucketName = Bucket,
                                  Prefix = "capability/probe"
                              }
                          );

            Assert.That(listing.S3Objects, Is.Null.Or.Empty);

            TestContext.Out.WriteLine(
                "Compatibility limit: this MinIO ignores DeleteObject If-Match; external same-key replacement during DELETE is not atomic."
            );
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            Assert.That(
                (await S3.GetObjectMetadataAsync(Bucket, "capability/probe")).ETag,
                Is.Not.EqualTo(original.ETag)
            );

            TestContext.Out.WriteLine("This MinIO supports conditional DeleteObject and preserved the replaced probe.");
        }
    }

    [Test]
    public async Task ChangedPublicationMetadataIsRejectedBeforeCreatingAttempt()
    {
        await SeedPublicationAsync();
        await PutAsync("own/result.bin", [9], Guid.NewGuid().ToString("D"));
        await OidcBrowser.SignInAsync(Factory, Client);
        using var response = await DeleteAsync();
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        using var scope = Factory.Services.CreateScope();

        Assert.That(
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Attempts.CountAsync(),
            Is.EqualTo(1)
        );
    }

    [Test]
    public async Task FailureSavingDeletionLeavesDurableIntentForReconciliation()
    {
        await SeedPublicationAsync();
        await OidcBrowser.SignInAsync(Factory, Client);

        using (var scope = Factory.Services.CreateScope())
            await DatabaseFailures.InstallAsync(
                scope.ServiceProvider.GetRequiredService<AppDbContext>(),
                "Downloads",
                "UPDATE",
                onlyDeleted: true
            );

        using var response = await DeleteAsync();
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
        using var check = Factory.Services.CreateScope();
        var saved = await check.ServiceProvider.GetRequiredService<AppDbContext>().Downloads.SingleAsync();
        Assert.That(saved.State, Is.EqualTo(DownloadState.Deleting));
        Assert.That(saved.Intent, Is.EqualTo(DownloadIntent.ExecuteStage));

        Assert.That(
            (await S3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = Bucket })).S3Objects,
            Is.Null.Or.Empty
        );
    }

    private Task<HttpResponseMessage> DeleteAsync() => Client.DeleteAsync(
        $"/api/library/{LibraryFile.Create("own/result.bin", 3, DateTimeOffset.UtcNow).Id}"
    );

    private async Task<DownloadRecord> SeedPublicationAsync(bool cleanup = false)
    {
        var now = DateTimeOffset.UtcNow;

        var aggregate = Download.Restore(
            new DownloadSnapshot(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                cleanup ? DownloadState.Failed : DownloadState.Completed,
                cleanup ? AttemptStatus.Failed : AttemptStatus.Succeeded,
                ResumeState: cleanup ? DownloadState.CleaningUp : null,
                FailedState: cleanup ? DownloadState.CleaningUp : null,
                PublicationState: PublicationState.Available
            )
        );

        var record = DownloadRecord.Create(
            aggregate,
            "https://example.test/video",
            "https://example.test/video",
            Guid.NewGuid(),
            now
        );

        record.Intent = DownloadIntent.None;

        var artifact = new DownloadArtifactRecord
        {
            Id = Guid.NewGuid(),
            DownloadId = record.Id,
            WorkspaceGeneration = record.WorkspaceGeneration,
            Kind = ArtifactKind.Prepared,
            RelativePath = "result.bin",
            Size = 3,
            IsReady = true
        };

        using var scope = Factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        database.AddRange(
            record,
            artifact,
            new DownloadAttemptRecord
            {
                Id = record.CurrentAttemptId,
                DownloadId = record.Id,
                Number = 1,
                Reason = AttemptReason.Initial,
                InitialState = DownloadState.Downloading,
                Status = aggregate.AttemptStatus,
                QueuedAt = now,
                FinishedAt = now
            },
            new StorageTransferRecord
            {
                Id = Guid.NewGuid(),
                DownloadId = record.Id,
                ArtifactId = artifact.Id,
                PublicationId = record.PublicationId,
                ObjectKey = "own/result.bin",
                Status = TransferStatus.Published,
                ExpectedSize = 3,
                CreatedAt = now,
                UpdatedAt = now
            }
        );

        await database.SaveChangesAsync();
        await PutAsync("own/result.bin", [1, 2, 3], record.PublicationId.ToString("D"));

        return record;
    }
}
