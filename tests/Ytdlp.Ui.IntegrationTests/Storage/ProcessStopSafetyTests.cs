using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Ytdlp.Ui.Configuration;
using Ytdlp.Ui.Domain.Primitives;
using Ytdlp.Ui.Features.Downloads;
using Ytdlp.Ui.Infrastructure.Media;
using Ytdlp.Ui.Infrastructure.Persistence;
using Ytdlp.Ui.IntegrationTests.Data;
using Ytdlp.Ui.IntegrationTests.Tools;

namespace Ytdlp.Ui.IntegrationTests.Storage;

[TestFixture]
public sealed class ProcessStopSafetyTests : SetupBase
{
    private ControlledMediaTools _tools = null!;

    protected override void ConfigureApplication(IWebHostBuilder builder)
    {
        _tools = new ControlledMediaTools
        {
            Metadata = MediaMetadata.Combined,
            PauseDownloads = true,
            StopUnconfirmedOnCancellation = true
        };

        builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IProcessRunner>();
                services.AddSingleton(_tools.Runner);
            }
        );
    }

    [TestCase(true, false)]
    [TestCase(false, false)]
    [TestCase(true, true)]
    public async Task UnconfirmedStopRetainsFilesStopsClaimsAndRequestsHostShutdownEvenWhenPersistenceFails(
        bool userCancel,
        bool databaseFailure
    )
    {
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lifetime.When(item => item.StopApplication()).Do(_ => stopped.TrySetResult());
        using var hostStop = new CancellationTokenSource();

        using var worker = new DownloadWorker(
            Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            Factory.Services.GetRequiredService<MediaPipeline>(),
            Options.Create(new WorkerOptions { PollIntervalMilliseconds = 20 }),
            TimeProvider.System,
            lifetime,
            Factory.Services.GetRequiredService<ILogger<DownloadWorker>>()
        );

        await worker.StartAsync(hostStop.Token);

        try
        {
            await OidcBrowser.SignInAsync(Factory, Client);

            using var createdResponse = await Client.PostAsJsonAsync(
                                            "/api/downloads",
                                            new { url = "https://example.test/stop-unconfirmed" }
                                        );

            var created = (await createdResponse.Content.ReadFromJsonAsync<DownloadView>())!;
            await _tools.DownloadEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            string retained;

            using (var scope = Factory.Services.CreateScope())
            {
                var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var record = await database.Downloads.SingleAsync();

                retained = Factory.Services.GetRequiredService<WorkspaceStorage>()
                                  .Resolve(record.Id, record.WorkspaceGeneration, "keep.partial");

                await File.WriteAllTextAsync(retained, "retained");
                if (databaseFailure) await DatabaseFailures.InstallAtStateAsync(database, DownloadState.Failed);
            }

            using var next = await Client.PostAsJsonAsync(
                                 "/api/downloads",
                                 new { url = "https://example.test/next-must-wait" }
                             );

            var waiting = (await next.Content.ReadFromJsonAsync<DownloadView>())!;

            if (userCancel)
            {
                using var response = await Client.PostAsync($"/api/downloads/{created.Id}/cancel", null);
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
            }
            else hostStop.Cancel();

            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var check = Factory.Services.CreateScope();

            var saved = await check.ServiceProvider.GetRequiredService<AppDbContext>()
                                   .Downloads.SingleAsync(item => item.Id == created.Id);

            Assert.That(saved.State, Is.EqualTo(databaseFailure ? DownloadState.Canceling : DownloadState.Failed));

            if (!databaseFailure)
            {
                Assert.That(
                    saved.ResumeState,
                    Is.EqualTo(userCancel ? DownloadState.Canceling : DownloadState.Downloading)
                );

                Assert.That(saved.ErrorCode, Is.EqualTo("process_stop_unconfirmed"));
            }

            Assert.That(saved.CancelRequested, Is.EqualTo(userCancel));
            Assert.That(await File.ReadAllTextAsync(retained), Is.EqualTo("retained"));
            await Task.Delay(100);

            var queued = await check.ServiceProvider.GetRequiredService<AppDbContext>()
                                    .Downloads.AsNoTracking()
                                    .SingleAsync(item => item.Id == waiting.Id);

            Assert.That(queued.State, Is.EqualTo(DownloadState.Queued));
            Assert.That(_tools.Commands.Count(command => command.Arguments.Contains("--format")), Is.EqualTo(1));
            lifetime.Received(1).StopApplication();
        }
        finally
        {
            _tools.ContinueDownload.TrySetResult();
            await worker.StopAsync(CancellationToken.None);
        }
    }
}
