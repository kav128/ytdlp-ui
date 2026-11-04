using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ytdlp.Ui.Domain.Primitives;
using Ytdlp.Ui.Features.Downloads;
using Ytdlp.Ui.Infrastructure.Media;
using Ytdlp.Ui.Infrastructure.Persistence;
using Ytdlp.Ui.IntegrationTests.Data;
using Ytdlp.Ui.IntegrationTests.Tools;

namespace Ytdlp.Ui.IntegrationTests;

[TestFixture, NonParallelizable]
public sealed class StartupRecoveryTests
{
    [TestCase(DownloadState.Downloading)]
    [TestCase(DownloadState.Preparing)]
    [TestCase(DownloadState.Uploading)]
    [TestCase(DownloadState.CleaningUp)]
    [TestCase(DownloadState.Deleting)]
    [TestCase(DownloadState.Canceling)]
    public async Task RestartRecoversInterruptedOperationBeforeHttpAndLeavesPendingWorkQueued(DownloadState state)
    {
        using var database = new TestDatabase();
        Guid id, pendingId;
        string cookie, file;

        using (var first = new CustomWebApplicationFactory(database))
        {
            using var client = first.CreateBrowserClient();
            cookie = await OidcBrowser.SignInAsync(first, client);
            var record = await DownloadData.SeedAsync(first.Services, state, state);
            id = record.Id;
            var workspace = first.Services.GetRequiredService<WorkspaceStorage>();
            workspace.Create(id, record.WorkspaceGeneration);
            file = workspace.Resolve(id, record.WorkspaceGeneration, "retained.bin");
            await File.WriteAllTextAsync(file, "retained data");

            pendingId = (await DownloadData.SeedAsync(
                             first.Services,
                             DownloadState.Queued,
                             url: "https://example.test/pending"
                         )).Id;
        }

        using var restarted = new CustomWebApplicationFactory(database);
        using var replay = restarted.CreateBrowserClient();
        replay.DefaultRequestHeaders.Add("Cookie", cookie);
        var recovered = (await replay.GetFromJsonAsync<DownloadView>($"/api/downloads/{id}"))!;
        Assert.That(recovered.State, Is.EqualTo("Failed"));
        Assert.That(recovered.FailedState, Is.EqualTo(state.ToString()));
        Assert.That(recovered.ResumeState, Is.EqualTo(state.ToString()));
        Assert.That(recovered.ErrorCode, Is.EqualTo("interrupted"));
        Assert.That(recovered.Attempts.Single().Status, Is.EqualTo("Failed"));
        Assert.That(recovered.Attempts.Single().Stages.Single().ErrorCode, Is.EqualTo("interrupted"));
        Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo("retained data"));
        using var scope = restarted.Services.CreateScope();
        var persisted = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.That(
            (await persisted.Downloads.SingleAsync(item => item.Id == pendingId)).State,
            Is.EqualTo(DownloadState.Queued)
        );

        Assert.That(
            (await scope.ServiceProvider.GetRequiredService<DownloadQueue>().TryClaimAsync(CancellationToken.None))!
           .DownloadId,
            Is.EqualTo(pendingId)
        );
    }
}
