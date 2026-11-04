using System.Net;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;
using Ytdlp.Ui.Configuration;
using Ytdlp.Ui.Infrastructure.Storage;

namespace Ytdlp.Ui.IntegrationTests;

[TestFixture]
public sealed class HealthTests : SetupBase
{
    [TestCase("/health/live")]
    [TestCase("/health/ready")]
    public async Task HealthIsPublicGenericAndDoesNotCacheOrContactOidc(string path)
    {
        using var response = await Client.GetAsync(path);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/plain"));
        Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("Healthy"));
        Assert.That(response.Headers.CacheControl?.NoStore, Is.True);
        Assert.That(response.Headers.Contains("Set-Cookie"), Is.False);
        Assert.That(Factory.Provider.TokenRequests, Is.Zero);
        var paths = Factory.Services.GetRequiredService<IOptions<PathsOptions>>().Value;
        Assert.That(Directory.GetFiles(paths.WorkDirectory, ".readiness-*"), Is.Empty);
        Assert.That(Directory.GetFiles(paths.DataProtectionKeysDirectory, ".readiness-*"), Is.Empty);
    }

    [Test]
    public async Task StorageFailureMakesReadinessUnhealthyWithoutAffectingLivenessOrExposingDetails()
    {
        Factory.Storage.CheckReadyAsync(Arg.Any<CancellationToken>())
               .Returns(
                    Task.FromException(
                        new StorageUnavailableException(new Exception("private-endpoint password=secret"))
                    )
                );

        using var ready = await Client.GetAsync("/health/ready");
        Assert.That(ready.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That(await ready.Content.ReadAsStringAsync(), Is.EqualTo("Unhealthy"));
        using var live = await Client.GetAsync("/health/live");
        Assert.That(live.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        await Factory.Storage.Received(1).CheckReadyAsync(Arg.Any<CancellationToken>());
        await Factory.Storage.DidNotReceive().ListAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task IgnoredDependencyCancellationHasAHardHttpDeadlineAndOneOutstandingProbeUntilRecovery()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Factory.Storage.CheckReadyAsync(Arg.Any<CancellationToken>())
               .Returns(_ =>
                    {
                        entered.TrySetResult();

                        return release.Task;
                    }
                );

        var elapsed = Stopwatch.StartNew();
        var pending = Client.GetAsync("/health/ready");

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            using (var busy = await Client.GetAsync("/health/ready").WaitAsync(TimeSpan.FromSeconds(1)))
                Assert.That(busy.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));

            using var timedOut = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(timedOut.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(await timedOut.Content.ReadAsStringAsync(), Is.EqualTo("Unhealthy"));
            Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)));
            using var live = await Client.GetAsync("/health/live");
            Assert.That(live.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            using var stillBusy = await Client.GetAsync("/health/ready");
            Assert.That(stillBusy.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            await Factory.Storage.Received(1).CheckReadyAsync(Arg.Any<CancellationToken>());
        }
        finally
        {
            Factory.Storage.CheckReadyAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
            release.TrySetResult();
            using var completed = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        }

        // Wait for the released operation to finish its own cleanup, rather than assume scheduler order.
        var recoveryDeadline = Stopwatch.StartNew();

        while (true)
        {
            using var recovered = await Client.GetAsync("/health/ready");

            if (recovered.StatusCode == HttpStatusCode.OK) break;

            Assert.That(recoveryDeadline.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)));
            await Task.Delay(20);
        }
    }

    [Test]
    public async Task ActualDatabaseConnectionFailureIsReadiness503AndLiveness200()
    {
        await using var admin = new NpgsqlConnection(SetupGlobal.PostgreSqlConnectionString);
        await admin.OpenAsync();
        var name = Factory.Database.Name;
        await using var disable = new NpgsqlCommand($"ALTER DATABASE \"{name}\" ALLOW_CONNECTIONS false", admin);
        await disable.ExecuteNonQueryAsync();

        try
        {
            using var ready = await Client.GetAsync("/health/ready");
            Assert.That(ready.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(await ready.Content.ReadAsStringAsync(), Is.EqualTo("Unhealthy"));
            using var live = await Client.GetAsync("/health/live");
            Assert.That(live.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }
        finally
        {
            await using var enable = new NpgsqlCommand($"ALTER DATABASE \"{name}\" ALLOW_CONNECTIONS true", admin);
            await enable.ExecuteNonQueryAsync();
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task UnwritableLocalStateCannotReportReady(bool keys)
    {
        var paths = Factory.Services.GetRequiredService<IOptions<PathsOptions>>().Value;
        var previous = keys ? paths.DataProtectionKeysDirectory : paths.WorkDirectory;
        var blocked = Path.Combine(Factory.Database.DirectoryPath, "blocked-path");
        await File.WriteAllTextAsync(blocked, "not a writable directory");

        if (keys) paths.DataProtectionKeysDirectory = blocked;
        else paths.WorkDirectory = blocked;

        try
        {
            using var ready = await Client.GetAsync("/health/ready");
            Assert.That(ready.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(await ready.Content.ReadAsStringAsync(), Is.EqualTo("Unhealthy"));
        }
        finally
        {
            if (keys) paths.DataProtectionKeysDirectory = previous;
            else paths.WorkDirectory = previous;
        }
    }

    [Test]
    public async Task UnknownHealthRouteDoesNotReturnSpaHtml()
    {
        using var response = await Client.GetAsync("/health/missing");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.Not.EqualTo("text/html"));
    }
}
