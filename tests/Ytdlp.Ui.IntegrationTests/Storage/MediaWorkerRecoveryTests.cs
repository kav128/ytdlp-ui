using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Ytdlp.Ui.Configuration;
using Ytdlp.Ui.Domain.Primitives;
using Ytdlp.Ui.Features.Downloads;
using Ytdlp.Ui.Features.Library;
using Ytdlp.Ui.Infrastructure.Media;
using Ytdlp.Ui.Infrastructure.Persistence;
using Ytdlp.Ui.IntegrationTests.Data;
using Ytdlp.Ui.IntegrationTests.Tools;

namespace Ytdlp.Ui.IntegrationTests.Storage;

public sealed partial class MediaWorkerTests
{
    [TestCase("retry")]
    [TestCase("cancel")]
    [TestCase("complete")]
    public async Task KnownJobSpoolLeftByCrashIsRemovedWithoutTouchingOtherOperations(string action)
    {
        _tools.PayloadLength = 11 * 1024 * 1024;
        await OidcBrowser.SignInAsync(Factory, Client);

        if (action == "complete")
        {
            using var scope = Factory.Services.CreateScope();

            await DatabaseFailures.InstallAtStateAsync(
                scope.ServiceProvider.GetRequiredService<AppDbContext>(),
                DownloadState.CleaningUp
            );
        }
        else _faults.Fault = "upload_error";

        var original = await CreateAsync("https://example.test/crash-spool");
        await WaitForAsync(original.Id, "Failed");
        _faults.Fault = null;

        if (action == "complete")
        {
            using var scope = Factory.Services.CreateScope();
            await DatabaseFailures.RemoveAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), "Downloads");
        }

        var workRoot = Factory.Services.GetRequiredService<IOptions<PathsOptions>>().Value.WorkDirectory;
        var ownDirectory = Path.Combine(workRoot, "uploads", original.PublicationId.ToString("N"));
        var foreignDirectory = Path.Combine(workRoot, "uploads", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ownDirectory);
        Directory.CreateDirectory(foreignDirectory);
        await File.WriteAllTextAsync(Path.Combine(ownDirectory, "part"), "own orphan part");
        var foreign = Path.Combine(foreignDirectory, "part");
        await File.WriteAllTextAsync(foreign, "another manual operation");
        var completes = _faults.CompletedCalls;

        using var accepted = await Client.PostAsync(
                                 $"/api/downloads/{original.Id}/{(action == "cancel" ? "cancel" : "retry")}",
                                 null
                             );

        Assert.That(accepted.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        await WaitForAsync(original.Id, action == "cancel" ? "Canceled" : "Completed");
        Assert.That(Directory.Exists(ownDirectory), Is.False);
        Assert.That(await File.ReadAllTextAsync(foreign), Is.EqualTo("another manual operation"));
        if (action == "complete") Assert.That(_faults.CompletedCalls, Is.EqualTo(completes));
    }

    [Test]
    public async Task ContinuationReasonSurvivesARealLaterFailureWithoutReplacingEitherFailureHistory()
    {
        _tools.Metadata = MediaMetadata.Separate;
        _tools.FailPrepare = true;
        await OidcBrowser.SignInAsync(Factory, Client);
        var original = await CreateAsync("https://example.test/continuation-reason");
        await WaitForAsync(original.Id, "Failed");

        using (var scope = Factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var record = await database.Downloads.SingleAsync();
            var artifact = await database.Artifacts.SingleAsync(item => item.Kind == ArtifactKind.Video);

            File.Delete(
                Factory.Services.GetRequiredService<WorkspaceStorage>()
                       .Resolve(record.Id, record.WorkspaceGeneration, artifact.RelativePath)
            );
        }

        using var retry = await Client.PostAsync($"/api/downloads/{original.Id}/retry", null);
        Assert.That(retry.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        DownloadView failed;

        do
        {
            await Task.Delay(25, timeout.Token);
            failed = (await Client.GetFromJsonAsync<DownloadView>($"/api/downloads/{original.Id}", timeout.Token))!;
        } while (failed.State != "Failed" || failed.Attempts.Length != 2);

        Assert.That(failed.Attempts[1].ContinuationReasonCode, Is.EqualTo("artifacts_unavailable"));
        Assert.That(failed.Attempts[1].ErrorCode, Is.EqualTo("prepare_failed"));
        Assert.That(failed.Attempts[1].Error, Does.StartWith("merge failed").And.Not.Contain("private.test"));
        Assert.That(failed.Attempts[0].ErrorCode, Is.EqualTo("prepare_failed"));
        Assert.That(failed.Attempts[0].ContinuationReasonCode, Is.Null);
        _tools.FailPrepare = false;
        using var next = await Client.PostAsync($"/api/downloads/{original.Id}/retry", null);
        Assert.That(next.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        var completed = await WaitForAsync(original.Id, "Completed");
        Assert.That(completed.Attempts[1].ContinuationReasonCode, Is.EqualTo("artifacts_unavailable"));
        Assert.That(completed.Attempts[1].ErrorCode, Is.EqualTo("prepare_failed"));
    }

    [Test]
    public async Task RetryRechecksAnArtifactLostWhileWaitingBehindAnotherJob()
    {
        _tools.PayloadLength = 11 * 1024 * 1024;
        await OidcBrowser.SignInAsync(Factory, Client);
        _faults.Fault = "upload_error";
        var original = await CreateAsync("https://example.test/queued-artifact");
        await WaitForAsync(original.Id, "Failed");
        _faults.Fault = null;
        _tools.PauseDownloads = true;
        var blocker = await CreateAsync("https://example.test/artifact-blocker");
        await WaitForAsync(blocker.Id, "Downloading");
        using var retry = await Client.PostAsync($"/api/downloads/{original.Id}/retry", null);
        Assert.That(retry.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        Assert.That((await retry.Content.ReadFromJsonAsync<DownloadView>())!.ResumeState, Is.EqualTo("Uploading"));

        using (var scope = Factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var transfer = await database.Transfers.SingleAsync(item => item.DownloadId == original.Id);
            var artifact = await database.Artifacts.SingleAsync(item => item.Id == transfer.ArtifactId);
            var record = await database.Downloads.SingleAsync(item => item.Id == original.Id);

            File.Delete(
                Factory.Services.GetRequiredService<WorkspaceStorage>()
                       .Resolve(record.Id, record.WorkspaceGeneration, artifact.RelativePath)
            );
        }

        _tools.ContinueDownload.TrySetResult();
        await WaitForAsync(blocker.Id, "Completed");
        var completed = await WaitForAsync(original.Id, "Completed");

        Assert.That(
            completed.Attempts[1].Stages.Single(stage => stage.Stage == "Upload").ErrorCode,
            Is.EqualTo("artifacts_unavailable")
        );

        Assert.That(completed.Attempts[0].Status, Is.EqualTo("Failed"));
        Assert.That(_tools.Commands.Count(command => command.Arguments.Contains("--format")), Is.EqualTo(2));

        Assert.That(
            completed.Attempts[1].Stages.Single(stage => stage.Stage == "Download").ReusedFromStageId,
            Is.Not.Null
        );
    }

    [Test]
    public async Task CancelAbortsUnknownOwnMultipartButPreservesOtherUploadsAndFiles()
    {
        _tools.PayloadLength = 11 * 1024 * 1024;
        await OidcBrowser.SignInAsync(Factory, Client);
        _faults.Fault = "upload_error";
        var original = await CreateAsync("https://example.test/orphan-multipart");
        await WaitForAsync(original.Id, "Failed");
        _faults.Fault = null;
        string key;

        using (var scope = Factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var transfer = await database.Transfers.SingleAsync();
            key = transfer.ObjectKey;
            transfer.MultipartUploadId = null;
            await database.SaveChangesAsync();
        }

        await S3.InitiateMultipartUploadAsync(
            new Amazon.S3.Model.InitiateMultipartUploadRequest
            {
                BucketName = Bucket,
                Key = key
            }
        );

        var other = await S3.InitiateMultipartUploadAsync(
                        new Amazon.S3.Model.InitiateMultipartUploadRequest
                        {
                            BucketName = Bucket,
                            Key = key + ".other"
                        }
                    );

        await PutAsync("manual/retained.txt", [7, 8]);
        using var cancel = await Client.PostAsync($"/api/downloads/{original.Id}/cancel", null);
        Assert.That(cancel.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        await WaitForAsync(original.Id, "Canceled");

        var remaining =
            (await S3.ListMultipartUploadsAsync(
                 new Amazon.S3.Model.ListMultipartUploadsRequest { BucketName = Bucket }
             )).MultipartUploads;

        Assert.That(remaining!.Single().UploadId, Is.EqualTo(other.UploadId));

        Assert.That(
            (await Client.GetFromJsonAsync<LibraryFile[]>("/api/library"))!.Single().Name,
            Is.EqualTo("retained.txt")
        );
    }

    [Test]
    public async Task DeletedRetryCreatesNewPlanWorkspaceAndPublicationAndRejectsAllOldActions()
    {
        await OidcBrowser.SignInAsync(Factory, Client);
        var original = await CreateAsync("https://example.test/new-generation");
        var first = await WaitForAsync(original.Id, "Completed");
        var file = (await Client.GetFromJsonAsync<LibraryFile[]>("/api/library"))!.Single();
        DownloadWorkItem oldWork;

        using (var scope = Factory.Services.CreateScope())
        {
            var record = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Downloads.SingleAsync();

            oldWork = new(
                record.Id,
                record.CurrentAttemptId,
                record.PublicationId,
                record.WorkspaceGeneration,
                record.OriginalUrl,
                DownloadState.Canceling
            );
        }

        using var deletion = await Client.DeleteAsync($"/api/library/{file.Id}");
        Assert.That(deletion.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        await WaitForAsync(original.Id, "Deleted");
        _tools.PauseDownloads = true;
        using var retry = await Client.PostAsync($"/api/downloads/{original.Id}/retry", null);
        Assert.That(retry.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        await WaitForAsync(original.Id, "Downloading");
        using var staleDelete = await Client.DeleteAsync($"/api/library/{file.Id}");
        Assert.That(staleDelete.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));

        await Assert.ThrowsAsync(
            Is.InstanceOf<OperationCanceledException>(),
            async () => await Factory.Services.GetRequiredService<MediaPipeline>()
                                     .CancelAsync(oldWork, CancellationToken.None)
        );

        _tools.ContinueDownload.TrySetResult();
        var completed = await WaitForAsync(original.Id, "Completed");
        Assert.That(completed.PublicationId, Is.Not.EqualTo(first.PublicationId));
        Assert.That(completed.Attempts, Has.Length.EqualTo(3));
        using var missing = await Client.GetAsync(file.ContentUrl);
        Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await Client.GetFromJsonAsync<LibraryFile[]>("/api/library"))!, Has.Length.EqualTo(1));
        Assert.That(_tools.Commands.Count(command => command.Arguments.Contains("--dump-single-json")), Is.EqualTo(2));
    }

    [TestCase("mismatch_metadata")]
    [TestCase("mismatch_size")]
    public async Task UncertainCompleteNeverPublishesOrDeletesAnUnmatchedReplacement(string fault)
    {
        await OidcBrowser.SignInAsync(Factory, Client);
        _faults.Fault = fault;
        var original = await CreateAsync("https://example.test/replaced-upload");
        var failed = await WaitForAsync(original.Id, "Failed");
        Assert.That(failed.ResumeState, Is.EqualTo("Uploading"));
        _faults.Fault = null;
        using var retry = await Client.PostAsync($"/api/downloads/{original.Id}/retry", null);
        Assert.That(retry.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        using var cancel = await Client.PostAsync($"/api/downloads/{original.Id}/cancel", null);
        Assert.That(cancel.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        DownloadView cancellation;

        do
        {
            await Task.Delay(25, timeout.Token);

            cancellation =
                (await Client.GetFromJsonAsync<DownloadView>($"/api/downloads/{original.Id}", timeout.Token))!;
        } while (cancellation.State != "Failed" || cancellation.ResumeState != "Canceling");

        Assert.That(cancellation.ErrorCode, Is.EqualTo("publication_changed"));
        Assert.That((await Client.GetFromJsonAsync<LibraryFile[]>("/api/library"))!, Has.Length.EqualTo(1));
        Assert.That(_faults.DeleteCalls, Is.Zero);
    }

    [TestCase(ArtifactKind.Combined)]
    [TestCase(ArtifactKind.Prepared)]
    public async Task FinalArtifactSurvivesReadySaveFailureAndRetryDoesNotRepeatTheExternalEffect(ArtifactKind kind)
    {
        _tools.Metadata = kind == ArtifactKind.Prepared ? MediaMetadata.Separate : MediaMetadata.Combined;
        _artifactFailure.Target = kind;
        await OidcBrowser.SignInAsync(Factory, Client);
        var created = await CreateAsync("https://example.test/ready-save-failure");
        var failed = await WaitForAsync(created.Id, "Failed");
        Assert.That(failed.ResumeState, Is.EqualTo(kind == ArtifactKind.Prepared ? "Preparing" : "Downloading"));
        var downloads = _tools.Commands.Count(command => command.Arguments.Contains("--format"));
        var merges = _tools.Commands.Count(command => command.Arguments.Contains("-c"));
        using var retry = await Client.PostAsync($"/api/downloads/{created.Id}/retry", null);
        Assert.That(retry.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        await WaitForAsync(created.Id, "Completed");
        Assert.That(_tools.Commands.Count(command => command.Arguments.Contains("--format")), Is.EqualTo(downloads));
        Assert.That(_tools.Commands.Count(command => command.Arguments.Contains("-c")), Is.EqualTo(merges));
    }

    [TestCase(DownloadState.CleaningUp)]
    [TestCase(DownloadState.Completed)]
    public async Task ExternalPublicationOrCleanupSurvivesLostTransitionWithoutRepeatingUpload(
        DownloadState failedWrite
    )
    {
        await OidcBrowser.SignInAsync(Factory, Client);

        using (var scope = Factory.Services.CreateScope())
            await DatabaseFailures.InstallAtStateAsync(
                scope.ServiceProvider.GetRequiredService<AppDbContext>(),
                failedWrite
            );

        var created = await CreateAsync("https://example.test/after-effect");
        var failed = await WaitForAsync(created.Id, "Failed");

        Assert.That(
            failed.ResumeState,
            Is.EqualTo(failedWrite == DownloadState.CleaningUp ? "Uploading" : "CleaningUp")
        );

        Assert.That((await S3.ListObjectsV2Async(new() { BucketName = Bucket })).S3Objects, Has.Count.EqualTo(1));
        var commands = _tools.Commands.Count;
        var uploads = _faults.CompletedCalls;

        using (var scope = Factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await DatabaseFailures.RemoveAsync(database, "Downloads");
            var record = await database.Downloads.SingleAsync();
            Factory.Services.GetRequiredService<WorkspaceStorage>().Clean(record.Id, record.WorkspaceGeneration);
        }

        using var retry = await Client.PostAsync($"/api/downloads/{created.Id}/retry", null);
        Assert.That(retry.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        var completed = await WaitForAsync(created.Id, "Completed");
        Assert.That(completed.Attempts, Has.Length.EqualTo(2));
        Assert.That(completed.Attempts[0].Status, Is.EqualTo("Failed"));

        Assert.That(
            completed.Attempts[1].ContinuationReasonCode,
            Is.EqualTo(failedWrite == DownloadState.CleaningUp ? "publication_reconciliation" : null)
        );

        Assert.That(_tools.Commands.Count, Is.EqualTo(commands));
        Assert.That(_faults.CompletedCalls, Is.EqualTo(uploads));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CorruptOrLostTrackReturnsToDownloadAndReusesTheOtherTrack(bool corrupt)
    {
        _tools.Metadata = MediaMetadata.Separate;
        _tools.FailPrepare = true;
        await OidcBrowser.SignInAsync(Factory, Client);
        var created = await CreateAsync("https://example.test/track-loss");
        await WaitForAsync(created.Id, "Failed");

        using (var scope = Factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var record = await database.Downloads.SingleAsync();
            var artifact = await database.Artifacts.SingleAsync(item => item.Kind == ArtifactKind.Video);

            var path = Factory.Services.GetRequiredService<WorkspaceStorage>()
                              .Resolve(record.Id, record.WorkspaceGeneration, artifact.RelativePath);

            if (corrupt)
                await File.WriteAllBytesAsync(path, Enumerable.Repeat((byte)99, (int)artifact.Size!).ToArray());
            else File.Delete(path);
        }

        _tools.FailPrepare = false;
        using var retry = await Client.PostAsync($"/api/downloads/{created.Id}/retry", null);
        Assert.That(retry.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        var completed = await WaitForAsync(created.Id, "Completed");
        Assert.That(_tools.Commands.Count(command => command.Arguments.Contains("--format")), Is.EqualTo(3));
        Assert.That(completed.Attempts[1].ContinuationReasonCode, Is.EqualTo("artifacts_unavailable"));
        Assert.That(completed.Attempts[0].ErrorCode, Is.EqualTo("prepare_failed"));
    }

    [Test]
    public async Task CleanupQueuedBehindAnotherJobStillForbidsCancel()
    {
        _failCleanup = true;
        await OidcBrowser.SignInAsync(Factory, Client);
        var original = await CreateAsync("https://example.test/cleanup-owner");
        await WaitForAsync(original.Id, "Failed");
        _tools.PauseDownloads = true;
        var blocker = await CreateAsync("https://example.test/cleanup-blocker");
        await WaitForAsync(blocker.Id, "Downloading");
        using var retry = await Client.PostAsync($"/api/downloads/{original.Id}/retry", null);
        Assert.That(retry.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        var queued = (await Client.GetFromJsonAsync<DownloadView>($"/api/downloads/{original.Id}"))!;
        Assert.That(queued.State, Is.EqualTo("Queued"));
        Assert.That(queued.AllowedActions, Does.Not.Contain("cancel"));
        using var cancel = await Client.PostAsync($"/api/downloads/{original.Id}/cancel", null);
        Assert.That(cancel.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        _failCleanup = false;
        _tools.ContinueDownload.TrySetResult();
        await WaitForAsync(blocker.Id, "Completed");
        await WaitForAsync(original.Id, "Completed");
    }

    [Test]
    public async Task OwnDeleteWaitsForTheSingleWorkerAndLostDeleteSaveRetriesOnlyConfirmation()
    {
        await OidcBrowser.SignInAsync(Factory, Client);
        var original = await CreateAsync("https://example.test/delete-owner");
        await WaitForAsync(original.Id, "Completed");
        var file = (await Client.GetFromJsonAsync<LibraryFile[]>("/api/library"))!.Single();
        _tools.PauseDownloads = true;
        var blocker = await CreateAsync("https://example.test/delete-blocker");
        await WaitForAsync(blocker.Id, "Downloading");
        using var accepted = await Client.DeleteAsync($"/api/library/{file.Id}");
        Assert.That(accepted.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));

        Assert.That(
            (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("downloadId").GetGuid(),
            Is.EqualTo(original.Id)
        );

        Assert.That((await Client.GetFromJsonAsync<LibraryFile[]>("/api/library"))!, Has.Length.EqualTo(1));
        using var replay = await Client.DeleteAsync($"/api/library/{file.Id}");
        Assert.That(replay.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));

        using (var scope = Factory.Services.CreateScope())
            await DatabaseFailures.InstallAtStateAsync(
                scope.ServiceProvider.GetRequiredService<AppDbContext>(),
                DownloadState.Deleted
            );

        _tools.ContinueDownload.TrySetResult();
        await WaitForAsync(blocker.Id, "Completed");
        var failed = await WaitForAsync(original.Id, "Failed");
        Assert.That(failed.ResumeState, Is.EqualTo("Deleting"));
        var deletes = _faults.DeleteCalls;
        var commands = _tools.Commands.Count;

        using (var scope = Factory.Services.CreateScope())
            await DatabaseFailures.RemoveAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), "Downloads");

        using var retry = await Client.PostAsync($"/api/downloads/{original.Id}/retry", null);
        Assert.That(retry.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        var deleted = await WaitForAsync(original.Id, "Deleted");
        Assert.That(deleted.Attempts[^1].State, Is.EqualTo("Deleted"));
        Assert.That(_faults.DeleteCalls, Is.EqualTo(deletes));
        Assert.That(_tools.Commands.Count, Is.EqualTo(commands));
        Assert.That((await Client.GetFromJsonAsync<LibraryFile[]>("/api/library"))!, Has.Length.EqualTo(1));
    }

    [Test]
    public async Task CancelCommittedBeforeUploadSuccessCleansOnlyItsOwnUnfinishedPublication()
    {
        _saveGate.Target = DownloadState.CleaningUp;
        await OidcBrowser.SignInAsync(Factory, Client);
        await PutAsync("manual/keep.txt", [4, 5]);
        var created = await CreateAsync("https://example.test/upload-cancel");

        try
        {
            await _saveGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var cancel = await Client.PostAsync($"/api/downloads/{created.Id}/cancel", null);
            Assert.That(cancel.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        }
        finally
        {
            _saveGate.Released.TrySetResult();
        }

        var canceled = await WaitForAsync(created.Id, "Canceled");
        Assert.That(canceled.CancelRequested, Is.True);
        var listing = await Client.GetFromJsonAsync<LibraryFile[]>("/api/library");
        Assert.That(listing!.Single().Name, Is.EqualTo("keep.txt"));

        Assert.That(
            (await S3.ListMultipartUploadsAsync(
                 new Amazon.S3.Model.ListMultipartUploadsRequest { BucketName = Bucket }
             )).MultipartUploads,
            Is.Null.Or.Empty
        );
    }

    [Test]
    public async Task CleanupCommittedBeforeCancelKeepsPublicationAndRejectsCancel()
    {
        _failCleanup = true;
        await OidcBrowser.SignInAsync(Factory, Client);
        var created = await CreateAsync("https://example.test/cleanup-cancel");
        var failed = await WaitForAsync(created.Id, "Failed");
        Assert.That(failed.ResumeState, Is.EqualTo("CleaningUp"));
        using var cancel = await Client.PostAsync($"/api/downloads/{created.Id}/cancel", null);
        Assert.That(cancel.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That((await Client.GetFromJsonAsync<LibraryFile[]>("/api/library"))!, Has.Length.EqualTo(1));
    }

    [Test]
    public async Task FailedCancellationRetriesOnlyCleanupAndThenNewGenerationDownloadsAgain()
    {
        _tools.PauseDownloads = true;
        _failCleanup = true;
        _partialCleanup = true;
        await OidcBrowser.SignInAsync(Factory, Client);
        var created = await CreateAsync("https://example.test/failed-cancel");
        await _tools.DownloadEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var workspace = Factory.Services.GetRequiredService<WorkspaceStorage>();

        var other = await DownloadData.SeedAsync(
                        Factory.Services,
                        DownloadState.Failed,
                        DownloadState.Downloading,
                        "https://example.test/other-retained-job"
                    );

        workspace.Create(other.Id, other.WorkspaceGeneration);
        var otherFile = workspace.Resolve(other.Id, other.WorkspaceGeneration, "other.bin");
        await File.WriteAllTextAsync(otherFile, "other job data");
        string first, second;

        using (var scope = Factory.Services.CreateScope())
        {
            var record = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                                    .Downloads.SingleAsync(item => item.Id == created.Id);

            first = workspace.Resolve(record.Id, record.WorkspaceGeneration, "partial-cleanup-first");
            second = workspace.Resolve(record.Id, record.WorkspaceGeneration, "partial-cleanup-second");
            await File.WriteAllTextAsync(first, "first");
            await File.WriteAllTextAsync(second, "second");
        }

        using var cancel = await Client.PostAsync($"/api/downloads/{created.Id}/cancel", null);
        Assert.That(cancel.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        var failed = await WaitForAsync(created.Id, "Failed");
        Assert.That(failed.ResumeState, Is.EqualTo("Canceling"));
        Assert.That(File.Exists(first), Is.False);
        Assert.That(File.Exists(second), Is.True);
        Assert.That(await File.ReadAllTextAsync(otherFile), Is.EqualTo("other job data"));
        var commands = _tools.Commands.Count;
        _failCleanup = false;
        using var retry = await Client.PostAsync($"/api/downloads/{created.Id}/retry", null);
        Assert.That(retry.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        var canceled = await WaitForAsync(created.Id, "Canceled");
        Assert.That(_tools.Commands.Count, Is.EqualTo(commands));
        Assert.That(File.Exists(second), Is.False);
        Assert.That(await File.ReadAllTextAsync(otherFile), Is.EqualTo("other job data"));
        _tools.PauseDownloads = false;
        using var again = await Client.PostAsync($"/api/downloads/{created.Id}/retry", null);
        Assert.That(again.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        var completed = await WaitForAsync(created.Id, "Completed");
        Assert.That(completed.Id, Is.EqualTo(created.Id));
        Assert.That(completed.PublicationId, Is.Not.EqualTo(canceled.PublicationId));
        Assert.That(_tools.Commands.Count(command => command.Arguments.Contains("--dump-single-json")), Is.EqualTo(2));
    }
}
