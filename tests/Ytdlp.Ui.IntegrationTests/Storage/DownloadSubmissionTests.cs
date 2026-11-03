using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ytdlp.Ui.Domain.Primitives;
using Ytdlp.Ui.Features.Downloads;
using Ytdlp.Ui.Features.Library;
using Ytdlp.Ui.Infrastructure.Persistence;
using Ytdlp.Ui.Infrastructure.Persistence.Entities;
using Ytdlp.Ui.IntegrationTests.Data;
using Ytdlp.Ui.IntegrationTests.Tools;

namespace Ytdlp.Ui.IntegrationTests.Storage;

[TestFixture]
public sealed class DownloadSubmissionTests : SetupBase
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task ConfirmedExternalAbsenceLetsAdminQueueSameDownloadFromEitherCommand(bool retry)
    {
        var original = await SeedPublishedAsync(present: false);
        var file = LibraryFile.Create("published/result.bin", 3, DateTimeOffset.UtcNow);
        using var missing = await Client.GetAsync(file.ContentUrl);
        Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        using (var before = Factory.Services.CreateScope())
        {
            var record = await before.ServiceProvider.GetRequiredService<AppDbContext>().Downloads.SingleAsync();
            Assert.That(record.State, Is.EqualTo(DownloadState.Completed));
            Assert.That(record.PublicationState, Is.EqualTo(PublicationState.Available));
        }

        await OidcBrowser.SignInAsync(Factory, Client);

        using var response = retry
                                 ? await Client.PostAsync($"/api/downloads/{original.Id}/retry", null)
                                 : await Client.PostAsJsonAsync("/api/downloads", new { url = original.OriginalUrl });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        var view = (await response.Content.ReadFromJsonAsync<DownloadView>())!;
        Assert.That(view.Id, Is.EqualTo(original.Id));
        Assert.That(view.PublicationId, Is.Not.EqualTo(original.PublicationId));
        Assert.That(view.State, Is.EqualTo("Queued"));
        Assert.That(view.ResumeState, Is.EqualTo("Downloading"));
        Assert.That(view.Attempts, Has.Length.EqualTo(2));

        Assert.That(
            view.Attempts[0].Stages.Any(stage => stage.Stage == "DeleteResult" && stage.Status == "Succeeded"),
            Is.True
        );
    }

    [Test]
    public async Task PresentPublicationIsDuplicateAndChangedMetadataConflictsWithoutNewAttempt()
    {
        var record = await SeedPublishedAsync(present: true);
        await OidcBrowser.SignInAsync(Factory, Client);
        using var duplicate = await Client.PostAsJsonAsync("/api/downloads", new { url = record.OriginalUrl });
        Assert.That(duplicate.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(await duplicate.Content.ReadAsStringAsync(), Does.Contain("download_already_exists"));
        await PutAsync("published/result.bin", [9], Guid.NewGuid().ToString("D"));
        using var changed = await Client.PostAsync($"/api/downloads/{record.Id}/retry", null);
        Assert.That(changed.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(await changed.Content.ReadAsStringAsync(), Does.Contain("publication_changed"));
        using var scope = Factory.Services.CreateScope();

        Assert.That(
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Attempts.CountAsync(),
            Is.EqualTo(1)
        );
    }

    [Test]
    public async Task MissingBucketDoesNotMarkCompletedPublicationDeletedOrQueueNewAttempt()
    {
        var record = await SeedPublishedAsync(present: false);
        await S3.DeleteBucketAsync(Bucket);
        await OidcBrowser.SignInAsync(Factory, Client);
        using var response = await Client.PostAsJsonAsync("/api/downloads", new { url = record.OriginalUrl });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        using var scope = Factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.That((await database.Downloads.SingleAsync()).State, Is.EqualTo(DownloadState.Completed));
        Assert.That(await database.Attempts.CountAsync(), Is.EqualTo(1));
    }

    private async Task<DownloadRecord> SeedPublishedAsync(bool present)
    {
        var record = await DownloadData.SeedAsync(Factory.Services, DownloadState.Completed);
        using var scope = Factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();

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

        database.AddRange(
            artifact,
            new StorageTransferRecord
            {
                Id = Guid.NewGuid(),
                DownloadId = record.Id,
                ArtifactId = artifact.Id,
                PublicationId = record.PublicationId,
                ObjectKey = "published/result.bin",
                ExpectedSize = 3,
                Status = TransferStatus.Published,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            }
        );

        await database.SaveChangesAsync();
        if (present) await PutAsync("published/result.bin", [1, 2, 3], record.PublicationId.ToString("D"));

        return record;
    }
}
