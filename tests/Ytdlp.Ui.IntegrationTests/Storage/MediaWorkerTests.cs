using System.Net;
using System.Net.Http.Json;
using Amazon.S3;
using Microsoft.Extensions.Options;
using NSubstitute;
using Ytdlp.Ui.Configuration;
using Ytdlp.Ui.IntegrationTests.Storage.Tools;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ytdlp.Ui.Domain.Primitives;
using Ytdlp.Ui.Features.Downloads;
using Ytdlp.Ui.Infrastructure.Media;
using Ytdlp.Ui.Infrastructure.Persistence;
using Ytdlp.Ui.IntegrationTests.Data;
using Ytdlp.Ui.IntegrationTests.Tools;

namespace Ytdlp.Ui.IntegrationTests.Storage;

[TestFixture]
public sealed class MediaWorkerTests : SetupBase
{
    private ControlledMediaTools _tools = null!;
    private FaultingS3Client _faults = null!;
    private bool _failCleanup;

    protected override void ConfigureApplication(IWebHostBuilder builder)
    {
        _tools = new ControlledMediaTools { Metadata = MediaMetadata.Combined };
        _failCleanup = false;

        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                                              new Dictionary<string, string?>
                                              {
                                                  ["Worker:Enabled"] = "true",
                                                  ["Worker:PollIntervalMilliseconds"] = "20"
                                              }
                                          )
        );

        builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IProcessRunner>();
                services.AddSingleton(_tools.Runner);
                services.RemoveAll<IAmazonS3>();
                services.AddSingleton<IAmazonS3>(_ => _faults = new FaultingS3Client(SetupGlobal.Endpoint));
                services.RemoveAll<WorkspaceStorage>();

                services.AddSingleton(provider =>
                    {
                        var workspace =
                            Substitute.ForPartsOf<WorkspaceStorage>(
                                provider.GetRequiredService<IOptions<PathsOptions>>()
                            );

                        workspace.When(item => item.Clean(Arg.Any<Guid>(), Arg.Any<Guid>()))
                                 .Do(_ =>
                                      {
                                          if (_failCleanup) throw new IOException("Injected cleanup failure");
                                      }
                                  );

                        return workspace;
                    }
                );
            }
        );
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RealWorkerPublishesConfirmedMediaAndKeepsAllFourStages(bool separate)
    {
        _tools.Metadata = separate ? MediaMetadata.Separate : MediaMetadata.UnknownCombined;
        await OidcBrowser.SignInAsync(Factory, Client);
        var created = await CreateAsync("  https://example.test/video?x=1&y=2  ");
        var finished = await WaitForAsync(created.Id, "Completed");

        Assert.That(
            finished.Attempts.Single().Stages.Select(stage => stage.Stage),
            Is.EqualTo(
                new[]
                {
                    "Download",
                    "Prepare",
                    "Upload",
                    "Cleanup"
                }
            )
        );

        Assert.That(finished.Attempts.Single().Stages.All(stage => stage.Status == "Succeeded"), Is.True);

        Assert.That(
            _tools.Commands.Count(command => command.Arguments.Contains("--format")),
            Is.EqualTo(separate ? 2 : 1)
        );

        var merges = _tools.Commands.Where(command => command.Arguments.Contains("-c")).ToArray();
        Assert.That(merges, Has.Length.EqualTo(separate ? 1 : 0));
        foreach (var command in merges) Assert.That(ControlledMediaTools.Value(command, "-c"), Is.EqualTo("copy"));

        foreach (var command in _tools.Commands.Where(command => command.Arguments.Contains("--format")))
        {
            Assert.That(ControlledMediaTools.Value(command, "--format"), Does.StartWith("all[format_id=\""));
            Assert.That(ControlledMediaTools.Value(command, "--fixup"), Is.EqualTo("never"));
            Assert.That(command.Arguments[^2], Is.EqualTo("--"));
            Assert.That(command.Arguments[^1], Is.EqualTo("https://example.test/video?x=1&y=2"));
        }

        using var scope = Factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var transfer = await database.Transfers.SingleAsync();
        Assert.That(transfer.Status, Is.EqualTo(TransferStatus.Published));
        Assert.That(transfer.MultipartUploadId, Is.Not.Empty);
        var stored = await S3.GetObjectMetadataAsync(Bucket, transfer.ObjectKey);
        Assert.That(stored.Metadata["x-amz-meta-publication-id"], Is.EqualTo(finished.PublicationId.ToString("D")));
        Assert.That(stored.Metadata["x-amz-meta-sha256"], Is.EqualTo(transfer.Sha256));
        var record = await database.Downloads.SingleAsync();

        Assert.That(
            Factory.Services.GetRequiredService<WorkspaceStorage>().DirectoryFor(record.Id, record.WorkspaceGeneration),
            Does.Not.Exist
        );

        using var anonymous = Factory.CreateBrowserClient();

        var file = (await anonymous.GetFromJsonAsync<Ytdlp.Ui.Features.Library.LibraryFile[]>("/api/library"))!
           .Single();

        using var download = await anonymous.GetAsync(file.ContentUrl);
        Assert.That(await download.Content.ReadAsByteArrayAsync(), Has.Length.EqualTo(separate ? 200 : 100));
    }

    [TestCase("{\"_type\":\"playlist\",\"entries\":[]}", "playlist_not_supported")]
    [TestCase("{\"is_live\":true}", "live_not_supported")]
    [TestCase("{\"live_status\":\"is_upcoming\"}", "live_not_supported")]
    public async Task UnsupportedSourceFailsBeforeAnyMediaDownload(string metadata, string code)
    {
        _tools.Metadata = metadata;
        await OidcBrowser.SignInAsync(Factory, Client);
        var created = await CreateAsync("https://example.test/unsupported");
        var failed = await WaitForAsync(created.Id, "Failed");
        Assert.That(failed.ErrorCode, Is.EqualTo(code));
        Assert.That(failed.ResumeState, Is.EqualTo("Downloading"));
        Assert.That(_tools.Commands.Any(command => command.Arguments.Contains("--format")), Is.False);
        Assert.That((await S3.ListObjectsV2Async(new() { BucketName = Bucket })).S3Objects, Is.Null.Or.Empty);
    }

    [Test]
    public async Task FailedMergeKeepsTracksAndManualRetryDoesNotDownloadThemAgain()
    {
        _tools.Metadata = MediaMetadata.Separate;
        _tools.FailPrepare = true;
        await OidcBrowser.SignInAsync(Factory, Client);
        var created = await CreateAsync("https://example.test/merge");
        var failed = await WaitForAsync(created.Id, "Failed");
        Assert.That(failed.FailedState, Is.EqualTo("Preparing"));
        Assert.That(failed.Error, Does.Not.Contain("private.test").And.Not.Contain("token=secret"));

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.That(await db.Artifacts.CountAsync(artifact => artifact.IsReady), Is.EqualTo(2));
        }

        _tools.FailPrepare = false;
        using var retry = await Client.PostAsync($"/api/downloads/{created.Id}/retry", null);
        Assert.That(retry.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        var completed = await WaitForAsync(created.Id, "Completed");
        Assert.That(completed.Attempts, Has.Length.EqualTo(2));
        Assert.That(completed.Attempts[0].State, Is.EqualTo("Failed"));
        Assert.That(_tools.Commands.Count(command => command.Arguments.Contains("--format")), Is.EqualTo(2));
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task PersistedProgressUsesKnownBytesAndOtherJobsWait(bool known)
    {
        _tools.PauseDownloads = true;
        _tools.KnownProgress = known;
        await OidcBrowser.SignInAsync(Factory, Client);
        var first = await CreateAsync("https://example.test/one");
        await _tools.DownloadEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = await CreateAsync("https://example.test/two");
        var running = (await Client.GetFromJsonAsync<DownloadView>($"/api/downloads/{first.Id}"))!;
        var waiting = (await Client.GetFromJsonAsync<DownloadView>($"/api/downloads/{second.Id}"))!;
        Assert.That(running.State, Is.EqualTo("Downloading"));
        Assert.That(waiting.State, Is.EqualTo("Queued"));
        Assert.That(running.Progress, Is.EqualTo(known ? 25.0 : (double?)null));
        _tools.ContinueDownload.TrySetResult();
        await WaitForAsync(first.Id, "Completed");
        await WaitForAsync(second.Id, "Completed");
        Assert.That(_tools.MaximumConcurrency, Is.EqualTo(1));
    }

    [Test]
    public async Task ContainerMismatchRenamesPublicationWithoutUnnecessaryMerge()
    {
        _tools.CombinedFormat = "matroska,webm";
        await OidcBrowser.SignInAsync(Factory, Client);
        var created = await CreateAsync("https://example.test/mismatch");
        await WaitForAsync(created.Id, "Completed");
        var file = (await Client.GetFromJsonAsync<Ytdlp.Ui.Features.Library.LibraryFile[]>("/api/library"))!.Single();
        Assert.That(file.Name, Does.EndWith(".mkv"));
        Assert.That(_tools.Commands.Any(command => command.Arguments.Contains("-c")), Is.False);
    }

    [Test]
    public async Task CancelStopsActiveDownloadBeforeCleanupAndNextJobCanRun()
    {
        _tools.PauseDownloads = true;
        await OidcBrowser.SignInAsync(Factory, Client);
        var first = await CreateAsync("https://example.test/cancel");
        await _tools.DownloadEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var cancel = await Client.PostAsync($"/api/downloads/{first.Id}/cancel", null);
        Assert.That(cancel.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        var canceled = await WaitForAsync(first.Id, "Canceled");
        Assert.That(canceled.PublicationState, Is.EqualTo("None"));
        _tools.PauseDownloads = false;
        var next = await CreateAsync("https://example.test/after-cancel");
        await WaitForAsync(next.Id, "Completed");
    }

    [Test]
    public async Task AudioWithOnlyCoverArtIsNotPublishedAsVideo()
    {
        _tools.CoverArtOnly = true;
        await OidcBrowser.SignInAsync(Factory, Client);
        var created = await CreateAsync("https://example.test/cover-art");
        var failed = await WaitForAsync(created.Id, "Failed");
        Assert.That(failed.ErrorCode, Is.EqualTo("format_unavailable"));
        Assert.That((await S3.ListObjectsV2Async(new() { BucketName = Bucket })).S3Objects, Is.Null.Or.Empty);
    }

    [Test]
    public async Task MatroskaAliasWithH264AndOpusUsesMkvCopyInsteadOfWebm()
    {
        _tools.Metadata = MediaMetadata.Separate;
        _tools.AudioCodec = "opus";
        _tools.TrackFormat = _tools.CombinedFormat = "matroska,webm";
        await OidcBrowser.SignInAsync(Factory, Client);
        var created = await CreateAsync("https://example.test/h264-opus");
        await WaitForAsync(created.Id, "Completed");
        var merge = _tools.Commands.Single(command => command.Arguments.Contains("-c"));
        Assert.That(ControlledMediaTools.Value(merge, "-c"), Is.EqualTo("copy"));
        Assert.That(merge.Arguments[^1], Does.EndWith(".mkv"));
        var file = (await Client.GetFromJsonAsync<Ytdlp.Ui.Features.Library.LibraryFile[]>("/api/library"))!.Single();
        Assert.That(file.Name, Does.EndWith(".mkv"));
    }

    [Test]
    public async Task FailedJobUploadKeepsPreparedFileAndUploadIdentityForManualRetry()
    {
        _tools.PayloadLength = 11 * 1024 * 1024;
        await OidcBrowser.SignInAsync(Factory, Client);
        _faults.Fault = "upload_error";
        var created = await CreateAsync("https://example.test/upload-failure");
        var failed = await WaitForAsync(created.Id, "Failed");
        Assert.That(failed.FailedState, Is.EqualTo("Uploading"));

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var transfer = await db.Transfers.SingleAsync();
            Assert.That(transfer.MultipartUploadId, Is.Not.Empty);
            Assert.That(transfer.Status, Is.EqualTo(TransferStatus.Failed));
            var artifact = await db.Artifacts.SingleAsync(item => item.Id == transfer.ArtifactId);
            var record = await db.Downloads.SingleAsync();

            Assert.That(
                File.Exists(
                    Factory.Services.GetRequiredService<WorkspaceStorage>()
                           .Resolve(record.Id, record.WorkspaceGeneration, artifact.RelativePath)
                ),
                Is.True
            );
        }

        Assert.That(
            (await S3.ListMultipartUploadsAsync(
                 new Amazon.S3.Model.ListMultipartUploadsRequest { BucketName = Bucket }
             )).MultipartUploads,
            Is.Null.Or.Empty
        );

        _faults.Fault = null;
        using var retry = await Client.PostAsync($"/api/downloads/{created.Id}/retry", null);
        Assert.That(retry.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        await WaitForAsync(created.Id, "Completed");
        Assert.That(_tools.Commands.Count(command => command.Arguments.Contains("--format")), Is.EqualTo(1));
    }

    [Test]
    public async Task CleanupFailureKeepsConfirmedPublicationAndRetryOnlyCleansOwnWorkspace()
    {
        _failCleanup = true;
        await OidcBrowser.SignInAsync(Factory, Client);
        var created = await CreateAsync("https://example.test/cleanup-failure");
        var failed = await WaitForAsync(created.Id, "Failed");
        Assert.That(failed.ResumeState, Is.EqualTo("CleaningUp"));
        Assert.That(failed.PublicationState, Is.EqualTo("Available"));
        Assert.That((await S3.ListObjectsV2Async(new() { BucketName = Bucket })).S3Objects, Has.Count.EqualTo(1));
        using var forbidden = await Client.PostAsync($"/api/downloads/{created.Id}/cancel", null);
        Assert.That(forbidden.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        var commands = _tools.Commands.Count;
        _failCleanup = false;
        using var retry = await Client.PostAsync($"/api/downloads/{created.Id}/retry", null);
        Assert.That(retry.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        await WaitForAsync(created.Id, "Completed");
        Assert.That(_tools.Commands.Count, Is.EqualTo(commands));
    }

    private async Task<DownloadView> CreateAsync(string url)
    {
        using var response = await Client.PostAsJsonAsync("/api/downloads", new { url });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));

        return (await response.Content.ReadFromJsonAsync<DownloadView>())!;
    }

    private async Task<DownloadView> WaitForAsync(Guid id, string state)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        while (true)
        {
            var view = (await Client.GetFromJsonAsync<DownloadView>($"/api/downloads/{id}", timeout.Token))!;

            if (view.State == state) return view;

            if (view.State == "Failed" && state != "Failed")
                Assert.Fail($"Unexpected media failure: {view.ErrorCode}: {view.Error}");

            await Task.Delay(25, timeout.Token);
        }
    }
}
