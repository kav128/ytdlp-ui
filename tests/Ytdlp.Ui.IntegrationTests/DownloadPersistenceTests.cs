using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ytdlp.Ui.Domain.Primitives;
using Ytdlp.Ui.Features.Downloads;
using Ytdlp.Ui.Infrastructure.Persistence;
using Ytdlp.Ui.IntegrationTests.Data;
using Ytdlp.Ui.IntegrationTests.Tools;

namespace Ytdlp.Ui.IntegrationTests;

[TestFixture]
public sealed class DownloadPersistenceTests : SetupBase
{
    [Test]
    public async Task ValidLongIncompressibleUrlCanBeDurablyQueued()
    {
        var url = "https://example.test/video?data="
                + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4096));

        using var database = new TestDatabase();
        Guid id;
        string cookie;

        using (var first = new CustomWebApplicationFactory(database))
        {
            using var client = first.CreateBrowserClient();
            cookie = await OidcBrowser.SignInAsync(first, client);

            var responses = await Task.WhenAll(
                                Enumerable.Range(0, 3)
                                          .Select(_ => client.PostAsJsonAsync("/api/downloads", new { url }))
                            );

            try
            {
                Assert.That(responses.Count(response => response.StatusCode == HttpStatusCode.Created), Is.EqualTo(1));
                Assert.That(responses.Count(response => response.StatusCode == HttpStatusCode.Conflict), Is.EqualTo(2));

                var view = (await responses.Single(response => response.StatusCode == HttpStatusCode.Created)
                                           .Content.ReadFromJsonAsync<DownloadView>())!;

                Assert.That(view.Url, Is.EqualTo(url));
                id = view.Id;
            }
            finally
            {
                foreach (var response in responses) response.Dispose();
            }
        }

        using var restarted = new CustomWebApplicationFactory(database);
        using var replay = restarted.CreateBrowserClient();
        replay.DefaultRequestHeaders.Add("Cookie", cookie);
        var restored = (await replay.GetFromJsonAsync<DownloadView>($"/api/downloads/{id}"))!;
        Assert.That(restored.Url, Is.EqualTo(url));
        var csrf = await replay.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        replay.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf.GetProperty("requestToken").GetString());
        using var duplicate = await replay.PostAsJsonAsync("/api/downloads", new { url });
        Assert.That(duplicate.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));

        Assert.That(
            (await duplicate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("existingDownloadId").GetGuid(),
            Is.EqualTo(id)
        );
    }

    [Test]
    public async Task CreationPersistsExactOriginalAndAgreedNormalizationBeforeReplying()
    {
        await OidcBrowser.SignInAsync(Factory, Client);
        const string original = "  HTTPS://Example.COM:443/Case?b=2&a=1#Part  ";
        using var response = await Client.PostAsJsonAsync("/api/downloads", new { url = original });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        var view = (await response.Content.ReadFromJsonAsync<DownloadView>())!;
        Assert.That(response.Headers.Location?.OriginalString, Is.EqualTo($"/api/downloads/{view.Id}"));
        Assert.That(view.Url, Is.EqualTo(original));
        Assert.That(view.State, Is.EqualTo("Queued"));
        Assert.That(view.Progress, Is.Null);
        Assert.That(view.AllowedActions, Is.EqualTo(new[] { "cancel" }));
        using var scope = Factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var saved = await database.Downloads.SingleAsync();
        Assert.That(saved.OriginalUrl, Is.EqualTo(original));
        Assert.That(saved.NormalizedUrl, Is.EqualTo("https://example.com/Case?b=2&a=1#Part"));
        Assert.That(saved.Intent, Is.EqualTo(DownloadIntent.EnqueueAttempt));
        Assert.That((await database.Attempts.SingleAsync()).Status, Is.EqualTo(AttemptStatus.Pending));
        Assert.That((await database.Stages.SingleAsync()).Status, Is.EqualTo(StageStatus.Pending));
    }

    [TestCase("https://example.test/Path?a=1&b=2", "https://example.test/path?a=1&b=2")]
    [TestCase("https://example.test/Path?a=1&b=2", "https://example.test/Path?b=2&a=1")]
    [TestCase("https://example.test/Path?a=1", "https://example.test/Path?a=2")]
    public async Task SignificantUrlDifferencesRemainSeparateDownloads(string first, string second)
    {
        await OidcBrowser.SignInAsync(Factory, Client);
        using var one = await Client.PostAsJsonAsync("/api/downloads", new { url = first });
        using var two = await Client.PostAsJsonAsync("/api/downloads", new { url = second });
        Assert.That(one.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.That(two.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.That((await Client.GetFromJsonAsync<DownloadView[]>("/api/downloads"))!, Has.Length.EqualTo(2));
    }

    [Test]
    public async Task SimultaneousNormalizedCreatesReturnOneDurableDownloadAndExistingId()
    {
        await OidcBrowser.SignInAsync(Factory, Client);

        var requests = Enumerable.Range(0, 8)
                                 .Select(index => Client.PostAsJsonAsync(
                                             "/api/downloads",
                                             new
                                             {
                                                 url = index % 2 == 0
                                                           ? "  HTTPS://EXAMPLE.test:443/same  "
                                                           : "https://example.test/same"
                                             }
                                         )
                                  )
                                 .ToArray();

        var responses = await Task.WhenAll(requests);

        try
        {
            Assert.That(responses.Count(response => response.StatusCode == HttpStatusCode.Created), Is.EqualTo(1));
            Assert.That(responses.Count(response => response.StatusCode == HttpStatusCode.Conflict), Is.EqualTo(7));

            var winner = (await responses.Single(response => response.StatusCode == HttpStatusCode.Created)
                                         .Content.ReadFromJsonAsync<DownloadView>())!;

            foreach (var duplicate in responses.Where(response => response.StatusCode == HttpStatusCode.Conflict))
            {
                var problem = await duplicate.Content.ReadFromJsonAsync<JsonElement>();
                Assert.That(problem.GetProperty("existingDownloadId").GetGuid(), Is.EqualTo(winner.Id));
                Assert.That(problem.GetProperty("code").GetString(), Is.EqualTo("download_already_exists"));
            }

            using var scope = Factory.Services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.That(await database.Downloads.CountAsync(), Is.EqualTo(1));
            Assert.That(await database.Attempts.CountAsync(), Is.EqualTo(1));
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    [TestCase(DownloadState.Deleted, true)]
    [TestCase(DownloadState.Failed, false)]
    [TestCase(DownloadState.Canceled, false)]
    public async Task CreateVersusRetryUsesOneMechanismAndOneActiveAttempt(DownloadState state, bool recreationAllowed)
    {
        var saved = await DownloadData.SeedAsync(
                        Factory.Services,
                        state,
                        state == DownloadState.Failed ? DownloadState.Uploading : null
                    );

        await OidcBrowser.SignInAsync(Factory, Client);

        var responses = await Task.WhenAll(
                            Client.PostAsJsonAsync("/api/downloads", new { url = saved.OriginalUrl }),
                            Client.PostAsync($"/api/downloads/{saved.Id}/retry", null)
                        );

        try
        {
            Assert.That(responses.Count(response => response.StatusCode == HttpStatusCode.Accepted), Is.EqualTo(1));
            Assert.That(responses.Count(response => response.StatusCode == HttpStatusCode.Conflict), Is.EqualTo(1));
            if (!recreationAllowed) Assert.That(responses[0].StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            var success = responses.Single(response => response.StatusCode == HttpStatusCode.Accepted);
            var view = (await success.Content.ReadFromJsonAsync<DownloadView>())!;
            Assert.That(success.Headers.Location?.OriginalString, Is.EqualTo($"/api/downloads/{saved.Id}"));
            Assert.That(view.Id, Is.EqualTo(saved.Id));
            Assert.That(view.Url, Is.EqualTo(saved.OriginalUrl));

            Assert.That(
                view.Attempts.Select(attempt => attempt.Number),
                Is.EqualTo(
                    new[]
                    {
                        1,
                        2
                    }
                )
            );

            Assert.That(view.ResumeState, Is.EqualTo(state == DownloadState.Failed ? "Uploading" : "Downloading"));
            Assert.That(view.PublicationId == saved.PublicationId, Is.EqualTo(state == DownloadState.Failed));
            using var scope = Factory.Services.CreateScope();

            Assert.That(
                await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                           .Attempts.CountAsync(attempt => attempt.Status == AttemptStatus.Pending
                                                        || attempt.Status == AttemptStatus.Running
                            ),
                Is.EqualTo(1)
            );
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    [Test]
    public async Task RepeatedAndConcurrentCancelPersistOneCleanupIntentWithoutPretendingCompletion()
    {
        await OidcBrowser.SignInAsync(Factory, Client);
        using var create = await Client.PostAsJsonAsync("/api/downloads", new { url = "https://example.test/cancel" });
        var initial = (await create.Content.ReadFromJsonAsync<DownloadView>())!;

        var responses = await Task.WhenAll(
                            Enumerable.Range(0, 5)
                                      .Select(_ => Client.PostAsync($"/api/downloads/{initial.Id}/cancel", null))
                        );

        try
        {
            Assert.That(responses.All(response => response.StatusCode == HttpStatusCode.Accepted), Is.True);
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }

        var canceled = (await Client.GetFromJsonAsync<DownloadView>($"/api/downloads/{initial.Id}"))!;
        Assert.That(canceled.State, Is.EqualTo("Canceling"));
        Assert.That(canceled.Attempts, Has.Length.EqualTo(1));
        Assert.That(canceled.AllowedActions, Is.Empty);
        using var scope = Factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.That((await database.Downloads.SingleAsync()).Intent, Is.EqualTo(DownloadIntent.StopAndCleanUp));
        Assert.That((await database.Attempts.SingleAsync()).FinishedAt, Is.Null);

        Assert.That(
            await database.Stages.CountAsync(stage => stage.Stage == ProcessingStage.CancelCleanup),
            Is.EqualTo(1)
        );
    }

    [Test]
    public async Task FailedCommandWriteRollsBackLifecycleAttemptAndIntent()
    {
        var original = await DownloadData.SeedAsync(Factory.Services, DownloadState.Failed, DownloadState.Uploading);
        await OidcBrowser.SignInAsync(Factory, Client);

        using (var scope = Factory.Services.CreateScope())
            await DatabaseFailures.InstallAsync(
                scope.ServiceProvider.GetRequiredService<AppDbContext>(),
                "Downloads",
                "UPDATE"
            );

        using var response = await Client.PostAsync($"/api/downloads/{original.Id}/retry", null);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
        using var check = Factory.Services.CreateScope();
        var database = check.ServiceProvider.GetRequiredService<AppDbContext>();
        var saved = await database.Downloads.SingleAsync();
        Assert.That(saved.State, Is.EqualTo(DownloadState.Failed));
        Assert.That(saved.CurrentAttemptId, Is.EqualTo(original.CurrentAttemptId));
        Assert.That(saved.Version, Is.EqualTo(original.Version));
        Assert.That(saved.Intent, Is.EqualTo(DownloadIntent.None));
        Assert.That(await database.Attempts.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task HashCollisionDoesNotExposeAnotherDownloadIdOrMergeDifferentUrl()
    {
        var existing = await DownloadData.SeedAsync(Factory.Services, DownloadState.Queued);
        const string different = "https://example.test/different";
        var hash = Ytdlp.Ui.Infrastructure.Persistence.Entities.DownloadRecord.ComputeNormalizedUrlHash(different);

        using (var scope = Factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                       .Database.ExecuteSqlAsync(
                            $"UPDATE \"Downloads\" SET \"NormalizedUrlHash\" = {hash} WHERE \"Id\" = {existing.Id}"
                        );

        await OidcBrowser.SignInAsync(Factory, Client);
        using var response = await Client.PostAsJsonAsync("/api/downloads", new { url = different });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        var problem = await response.Content.ReadAsStringAsync();
        Assert.That(problem, Does.Contain("url_conflict").And.Not.Contain(existing.Id.ToString()));
        using var check = Factory.Services.CreateScope();

        Assert.That(
            await check.ServiceProvider.GetRequiredService<AppDbContext>().Downloads.CountAsync(),
            Is.EqualTo(1)
        );
    }
}
