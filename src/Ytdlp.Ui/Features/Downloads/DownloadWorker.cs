using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ytdlp.Ui.Configuration;
using Ytdlp.Ui.Domain.Primitives;
using Ytdlp.Ui.Infrastructure.Media;
using Ytdlp.Ui.Infrastructure.Persistence;
using Ytdlp.Ui.Infrastructure.Storage;

namespace Ytdlp.Ui.Features.Downloads;

public sealed class DownloadWorker(
    IServiceScopeFactory scopes,
    MediaPipeline pipeline,
    IOptions<WorkerOptions> options,
    TimeProvider clock,
    ILogger<DownloadWorker> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            DownloadWorkItem? work = null;

            try
            {
                using (var scope = scopes.CreateScope())
                    work = await scope.ServiceProvider.GetRequiredService<DownloadQueue>().TryClaimAsync(stoppingToken);

                if (work is not null) await ExecuteWorkAsync(work, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                logger.LogWarning(
                    "Worker could not continue {DownloadId}: {ErrorType}",
                    work?.DownloadId,
                    error.GetType().Name
                );
            }

            if (work is null)
                try
                {
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(options.Value.PollIntervalMilliseconds),
                        clock,
                        stoppingToken
                    );
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
        }
    }

    private async Task ExecuteWorkAsync(DownloadWorkItem work, CancellationToken stoppingToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var execution = pipeline.RunAsync(work, operation.Token);

        try
        {
            while (!execution.IsCompleted)
            {
                await Task.WhenAny(execution, Task.Delay(TimeSpan.FromMilliseconds(200), stoppingToken));

                if (execution.IsCompleted) break;

                if (stoppingToken.IsCancellationRequested)
                {
                    operation.Cancel();

                    break;
                }

                using var scope = scopes.CreateScope();

                var record = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                                        .Downloads.AsNoTracking()
                                        .SingleAsync(item => item.Id == work.DownloadId, stoppingToken);

                if (record.CurrentAttemptId != work.AttemptId
                 || record.CancelRequested && work.State != DownloadState.Canceling)
                {
                    operation.Cancel();

                    break;
                }
            }

            await execution;
        }
        catch (OperationCanceledException)
        {
            operation.Cancel();

            try
            {
                await execution;
            }
            catch (OperationCanceledException)
            {
            }

            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            if (stoppingToken.IsCancellationRequested)
                await pipeline.FailAsync(
                    work,
                    "interrupted",
                    "The process was stopped before the attempt finished.",
                    cleanup.Token
                );
            else
                try
                {
                    await pipeline.CancelAsync(work, cleanup.Token);
                }
                catch (Exception error)
                {
                    await FailAsync(work, error, cleanup.Token);
                }
        }
        catch (Exception error)
        {
            operation.Cancel();

            try
            {
                await execution;
            }
            catch (Exception)
            {
            }

            using var persistence = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await FailAsync(work, error, persistence.Token);
        }
    }

    private async Task FailAsync(DownloadWorkItem work, Exception error, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();

        var record = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                                .Downloads.AsNoTracking()
                                .SingleAsync(item => item.Id == work.DownloadId, cancellationToken);

        var code = error switch
        {
            MediaProcessingException media => media.Code,
            StorageConflictException       => "publication_changed",
            Win32Exception                 => "tool_unavailable",
            _ => record.State switch
            {
                DownloadState.CleaningUp => "cleanup_failed", DownloadState.Uploading => "upload_failed",
                DownloadState.Preparing  => "prepare_failed", DownloadState.Canceling => "cancel_failed",
                _                        => "download_failed"
            }
        };

        var details = error is MediaProcessingException
                          ? error.Message
                          : "The stage failed; its files were retained for a manual retry.";

        logger.LogWarning(
            "Download {DownloadId} attempt {AttemptId} failed at {Stage}: {Code}",
            work.DownloadId,
            work.AttemptId,
            record.State,
            code
        );

        await pipeline.FailAsync(work, code, details, cancellationToken);
    }
}
