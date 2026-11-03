using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
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
public sealed class DownloadTransitionRaceTests : SetupBase
{
    private TransitionSaveGate _gate = null!;

    protected override void ConfigureApplication(IWebHostBuilder builder) => builder.ConfigureTestServices(services =>
        {
            _gate = new TransitionSaveGate();
            services.AddDbContext<AppDbContext>(options => options.AddInterceptors(_gate));
        }
    );

    [Test]
    public async Task CleanupCommittedFirstRejectsLateCancelWithoutAnyCleanupIntent()
    {
        var original = await DownloadData.SeedAsync(Factory.Services, DownloadState.Uploading);
        await OidcBrowser.SignInAsync(Factory, Client);
        _gate.Target = DownloadState.Canceling;
        var cancel = Client.PostAsync($"/api/downloads/{original.Id}/cancel", null);

        try
        {
            await _gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var scope = Factory.Services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var record = await database.Downloads.SingleAsync();
            var aggregate = record.RestoreAggregate();

            var transition = aggregate.Apply(
                new StageSucceeded(record.CurrentAttemptId, DownloadState.Uploading, PublicationConfirmed: true)
            );

            Assert.That(transition.IsAccepted, Is.True);
            record.Capture(aggregate, transition.Intent, DateTimeOffset.UtcNow);
            await database.SaveChangesAsync();
        }
        finally
        {
            _gate.Released.TrySetResult();
        }

        using var response = await cancel;
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        using var check = Factory.Services.CreateScope();
        var saved = await check.ServiceProvider.GetRequiredService<AppDbContext>().Downloads.SingleAsync();
        Assert.That(saved.State, Is.EqualTo(DownloadState.CleaningUp));
        Assert.That(saved.CancelRequested, Is.False);
        Assert.That(saved.Intent, Is.EqualTo(DownloadIntent.ExecuteStage));

        Assert.That(
            await check.ServiceProvider.GetRequiredService<AppDbContext>()
                       .Stages.CountAsync(stage => stage.Stage == ProcessingStage.CancelCleanup),
            Is.Zero
        );
    }

    [Test]
    public async Task CancelCommittedFirstMakesStaleClaimRollBackAndNextClaimPerformsCleanup()
    {
        var original = await DownloadData.SeedAsync(Factory.Services, DownloadState.Queued);
        await OidcBrowser.SignInAsync(Factory, Client);
        _gate.Target = DownloadState.Downloading;
        using var claiming = Factory.Services.CreateScope();
        var claim = claiming.ServiceProvider.GetRequiredService<DownloadQueue>().TryClaimAsync(CancellationToken.None);

        try
        {
            await _gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var cancel = await Client.PostAsync($"/api/downloads/{original.Id}/cancel", null);
            Assert.That(cancel.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        }
        finally
        {
            _gate.Released.TrySetResult();
        }

        Assert.That(await claim, Is.Null);
        using var next = Factory.Services.CreateScope();
        var work = await next.ServiceProvider.GetRequiredService<DownloadQueue>().TryClaimAsync(CancellationToken.None);
        Assert.That(work!.State, Is.EqualTo(DownloadState.Canceling));
        var database = next.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.That((await database.Downloads.SingleAsync()).Intent, Is.EqualTo(DownloadIntent.StopAndCleanUp));
        Assert.That(await database.Attempts.CountAsync(), Is.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RetryAndCancelOfTheSameFailedSnapshotCommitOnlyOneNewAttempt(bool cancellationWins)
    {
        var original = await DownloadData.SeedAsync(Factory.Services, DownloadState.Failed, DownloadState.Uploading);
        await OidcBrowser.SignInAsync(Factory, Client);
        _gate.Target = cancellationWins ? DownloadState.Queued : DownloadState.Canceling;
        var losingAction = cancellationWins ? "retry" : "cancel";
        var winningAction = cancellationWins ? "cancel" : "retry";
        var loser = Client.PostAsync($"/api/downloads/{original.Id}/{losingAction}", null);

        try
        {
            await _gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var winner = await Client.PostAsync($"/api/downloads/{original.Id}/{winningAction}", null);
            Assert.That(winner.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        }
        finally
        {
            _gate.Released.TrySetResult();
        }

        using var rejected = await loser;
        Assert.That(rejected.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        using var check = Factory.Services.CreateScope();
        var database = check.ServiceProvider.GetRequiredService<AppDbContext>();
        var record = await database.Downloads.SingleAsync();
        Assert.That(record.State, Is.EqualTo(cancellationWins ? DownloadState.Canceling : DownloadState.Queued));

        Assert.That(
            record.Intent,
            Is.EqualTo(cancellationWins ? DownloadIntent.StopAndCleanUp : DownloadIntent.EnqueueAttempt)
        );

        var attempts = await database.Attempts.OrderBy(attempt => attempt.Number).ToArrayAsync();
        Assert.That(attempts, Has.Length.EqualTo(2));
        Assert.That(attempts[0].Status, Is.EqualTo(AttemptStatus.Failed));
        Assert.That(attempts[0].ErrorCode, Is.EqualTo("seed_failure"));

        Assert.That(
            attempts.Count(attempt => attempt.Status is AttemptStatus.Pending or AttemptStatus.Running),
            Is.EqualTo(1)
        );
    }
}
