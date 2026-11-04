using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Ytdlp.Ui.Domain.Primitives;
using Ytdlp.Ui.Infrastructure.Persistence;
using Ytdlp.Ui.Infrastructure.Persistence.Entities;
using Ytdlp.Ui.Infrastructure.Storage;
using Ytdlp.Ui.IntegrationTests.Data;
using Ytdlp.Ui.IntegrationTests.Tools;

namespace Ytdlp.Ui.IntegrationTests;

[TestFixture]
public sealed class DownloadPublicationRaceTests : SetupBase
{
    [TestCase("forbidden")]
    [TestCase("network")]
    [TestCase("timeout")]
    public async Task UncertainStorageResultNeverDeletesOrRequeuesCompletedPublication(string fault)
    {
        var original = await SeedCompletedAsync();

        Factory.Storage.FindAsync("published/result.bin", Arg.Any<CancellationToken>())
               .Returns<Task<StoredObject?>>(_ => throw new StorageUnavailableException(new IOException(fault)));

        await OidcBrowser.SignInAsync(Factory, Client);
        using var create = await Client.PostAsJsonAsync("/api/downloads", new { url = original.OriginalUrl });
        using var retry = await Client.PostAsync($"/api/downloads/{original.Id}/retry", null);
        Assert.That(create.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That(retry.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        using var scope = Factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var saved = await database.Downloads.SingleAsync();
        Assert.That(saved.State, Is.EqualTo(DownloadState.Completed));
        Assert.That(saved.PublicationId, Is.EqualTo(original.PublicationId));
        Assert.That(saved.Version, Is.EqualTo(original.Version));
        Assert.That(await database.Attempts.CountAsync(), Is.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task LateAbsentObservationCannotOverwriteAChangedPublicationOrCreateAttempt(bool retry)
    {
        var original = await SeedCompletedAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource<StoredObject?>(TaskCreationOptions.RunContinuationsAsynchronously);

        Factory.Storage.FindAsync("published/result.bin", Arg.Any<CancellationToken>())
               .Returns(_ =>
                    {
                        entered.TrySetResult();

                        return released.Task;
                    }
                );

        await OidcBrowser.SignInAsync(Factory, Client);

        var command = retry
                          ? Client.PostAsync($"/api/downloads/{original.Id}/retry", null)
                          : Client.PostAsJsonAsync("/api/downloads", new { url = original.OriginalUrl });

        var newPublication = Guid.NewGuid();

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var scope = Factory.Services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var current = await database.Downloads.SingleAsync();
            current.PublicationId = newPublication;
            await database.SaveChangesAsync();
        }
        finally
        {
            released.TrySetResult(null);
        }

        using var response = await command;
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        using var check = Factory.Services.CreateScope();
        var savedDatabase = check.ServiceProvider.GetRequiredService<AppDbContext>();
        var saved = await savedDatabase.Downloads.SingleAsync();
        Assert.That(saved.PublicationId, Is.EqualTo(newPublication));
        Assert.That(saved.State, Is.EqualTo(DownloadState.Completed));
        Assert.That(await savedDatabase.Attempts.CountAsync(), Is.EqualTo(1));

        Assert.That(
            await savedDatabase.Stages.CountAsync(stage => stage.Stage == ProcessingStage.DeleteResult),
            Is.Zero
        );
    }

    private async Task<DownloadRecord> SeedCompletedAsync()
    {
        var record = await DownloadData.SeedAsync(Factory.Services, DownloadState.Completed);
        using var scope = Factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var artifact = new DownloadArtifactRecord
        {
            Id = Guid.NewGuid(),
            DownloadId = record.Id,
            WorkspaceGeneration = record.WorkspaceGeneration,
            RelativePath = "result.bin",
            Kind = ArtifactKind.Prepared,
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

        return record;
    }
}
