using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ytdlp.Ui.Domain.Events;
using Ytdlp.Ui.Domain.Primitives;
using Ytdlp.Ui.Features.Downloads;
using Ytdlp.Ui.Infrastructure.Persistence;
using Ytdlp.Ui.IntegrationTests.Data;
using Ytdlp.Ui.IntegrationTests.Tools;

namespace Ytdlp.Ui.IntegrationTests;

[TestFixture]
public sealed class DownloadQueueTests : SetupBase
{
    [Test]
    public async Task SimultaneousClaimersStartOnlyOneAttemptAndKeepRemainingPending()
    {
        await DownloadData.SeedAsync(Factory.Services, DownloadState.Queued, url: "https://example.test/one");
        await DownloadData.SeedAsync(Factory.Services, DownloadState.Queued, url: "https://example.test/two");
        using var first = Factory.Services.CreateScope();
        using var second = Factory.Services.CreateScope();

        var results = await Task.WhenAll(
                          first.ServiceProvider.GetRequiredService<DownloadQueue>()
                               .TryClaimAsync(CancellationToken.None),
                          second.ServiceProvider.GetRequiredService<DownloadQueue>()
                                .TryClaimAsync(CancellationToken.None)
                      );

        Assert.That(results.Count(result => result is not null), Is.EqualTo(1));
        using var check = Factory.Services.CreateScope();
        var database = check.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.That(
            await database.Attempts.CountAsync(attempt => attempt.Status == AttemptStatus.Running),
            Is.EqualTo(1)
        );

        Assert.That(
            await database.Attempts.CountAsync(attempt => attempt.Status == AttemptStatus.Pending),
            Is.EqualTo(1)
        );

        Assert.That(await database.Stages.CountAsync(stage => stage.Status == StageStatus.Running), Is.EqualTo(1));
    }

    [Test]
    public async Task ClaimFollowsFifoAndManualRetryJoinsTheEnd()
    {
        var one = await DownloadData.SeedAsync(Factory.Services, DownloadState.Queued, url: "https://example.test/one");
        var two = await DownloadData.SeedAsync(Factory.Services, DownloadState.Queued, url: "https://example.test/two");
        Guid expected;

        using (var scope = Factory.Services.CreateScope())
            expected = (await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                                   .Attempts.OrderBy(attempt => attempt.QueuedAt)
                                   .ThenBy(attempt => attempt.Id)
                                   .FirstAsync()).DownloadId;

        DownloadWorkItem claimed;

        using (var scope = Factory.Services.CreateScope())
            claimed = (await scope.ServiceProvider.GetRequiredService<DownloadQueue>()
                                  .TryClaimAsync(CancellationToken.None))!;

        Assert.That(claimed.DownloadId, Is.EqualTo(expected));

        using (var scope = Factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var record = await database.Downloads.SingleAsync(item => item.Id == claimed.DownloadId);
            var aggregate = record.RestoreAggregate();

            var failure = aggregate.Apply(
                new StageFailed(claimed.AttemptId, DownloadState.Downloading, new("test_failure"))
            );

            record.Capture(aggregate, failure.Intent, DateTimeOffset.UtcNow);
            var attempt = await database.Attempts.SingleAsync(item => item.Id == claimed.AttemptId);
            attempt.Status = AttemptStatus.Failed;
            attempt.FinishedAt = DateTimeOffset.UtcNow;
            await database.SaveChangesAsync();
        }

        await OidcBrowser.SignInAsync(Factory, Client);
        using var retry = await Client.PostAsync($"/api/downloads/{claimed.DownloadId}/retry", null);
        Assert.That(retry.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.Accepted));
        using var nextScope = Factory.Services.CreateScope();

        var next = await nextScope.ServiceProvider.GetRequiredService<DownloadQueue>()
                                  .TryClaimAsync(CancellationToken.None);

        Assert.That(next!.DownloadId, Is.EqualTo(expected == one.Id ? two.Id : one.Id));
    }

    [Test]
    public async Task FailedClaimRollsBackLifecycleAndAllExecutionMarkers()
    {
        var original = await DownloadData.SeedAsync(Factory.Services, DownloadState.Queued);

        using (var scope = Factory.Services.CreateScope())
            await DatabaseFailures.InstallAsync(
                scope.ServiceProvider.GetRequiredService<AppDbContext>(),
                "Downloads",
                "UPDATE"
            );

        using (var scope = Factory.Services.CreateScope())
            await Assert.ThrowsAsync<DbUpdateException>(async () => await scope.ServiceProvider
                                                                               .GetRequiredService<DownloadQueue>()
                                                                               .TryClaimAsync(CancellationToken.None)
            );

        using var check = Factory.Services.CreateScope();
        var database = check.ServiceProvider.GetRequiredService<AppDbContext>();
        var saved = await database.Downloads.SingleAsync();
        Assert.That(saved.State, Is.EqualTo(DownloadState.Queued));
        Assert.That(saved.Version, Is.EqualTo(original.Version));
        Assert.That(saved.Intent, Is.EqualTo(DownloadIntent.EnqueueAttempt));
        Assert.That((await database.Attempts.SingleAsync()).StartedAt, Is.Null);
        Assert.That((await database.Stages.SingleAsync()).Status, Is.EqualTo(StageStatus.Pending));
    }

    [Test]
    public async Task NewHostKeepsPendingQueueAndHistoryWithoutResubmittingUrls()
    {
        using var database = new TestDatabase();
        Guid id;
        string cookie;

        using (var first = new CustomWebApplicationFactory(database))
        {
            using var client = first.CreateBrowserClient();
            cookie = await OidcBrowser.SignInAsync(first, client);

            using var created = await client.PostAsJsonAsync(
                                    "/api/downloads",
                                    new { url = "https://example.test/restart" }
                                );

            id = (await created.Content.ReadFromJsonAsync<DownloadView>())!.Id;
        }

        using var restarted = new CustomWebApplicationFactory(database);
        using var replay = restarted.CreateBrowserClient();
        replay.DefaultRequestHeaders.Add("Cookie", cookie);
        var saved = (await replay.GetFromJsonAsync<DownloadView>($"/api/downloads/{id}"))!;
        Assert.That(saved.State, Is.EqualTo("Queued"));
        Assert.That(saved.Attempts, Has.Length.EqualTo(1));
        Assert.That(saved.Attempts[0].Stages.Single().Status, Is.EqualTo("Pending"));
        Assert.That(restarted.Provider.TokenRequests, Is.Zero);
        using var scope = restarted.Services.CreateScope();

        Assert.That(
            (await scope.ServiceProvider.GetRequiredService<DownloadQueue>().TryClaimAsync(CancellationToken.None))!
           .DownloadId,
            Is.EqualTo(id)
        );
    }
}
