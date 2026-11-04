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
    IHostApplicationLifetime lifetime,
    ILogger<DownloadWorker> logger
) : BackgroundService
{
    private bool _stopClaiming;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;

        while (!stoppingToken.IsCancellationRequested && !_stopClaiming)
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
        Exception? failure = null;

        try
        {
            while (!execution.IsCompleted)
            {
                await Task.WhenAny(execution, Task.Delay(200, stoppingToken));

                if (execution.IsCompleted) break;

                stoppingToken.ThrowIfCancellationRequested();
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
        catch (Exception error)
        {
            failure = error;
            operation.Cancel();

            try
            {
                await execution;
            }
            catch (ProcessStopUnconfirmedException stop)
            {
                failure = stop;
            }
            catch (Exception)
            {
            }
        }

        if (failure is null) return;

        using var persistence = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        if (failure is ProcessStopUnconfirmedException)
        {
            _stopClaiming = true;

            try
            {
                await pipeline.FailAsync(
                    work,
                    "process_stop_unconfirmed",
                    null,
                    persistence.Token
                );
            }
            finally
            {
                lifetime.StopApplication();
            }

            return;
        }

        if (stoppingToken.IsCancellationRequested)
        {
            await pipeline.FailAsync(
                work,
                "interrupted",
                null,
                persistence.Token
            );

            return;
        }

        using var reading = scopes.CreateScope();

        var current = await reading.ServiceProvider.GetRequiredService<AppDbContext>()
                                   .Downloads.AsNoTracking()
                                   .SingleAsync(item => item.Id == work.DownloadId, persistence.Token);

        if (current.CurrentAttemptId != work.AttemptId) return;

        if (current.State == DownloadState.Canceling)
        {
            try
            {
                await pipeline.CancelAsync(work, persistence.Token);
            }
            catch (Exception error)
            {
                await FailAsync(work, error, persistence.Token);
            }
        }
        else await FailAsync(work, failure, persistence.Token);
    }

    private async Task FailAsync(DownloadWorkItem work, Exception error, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();

        var record = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                                .Downloads.AsNoTracking()
                                .SingleAsync(item => item.Id == work.DownloadId, cancellationToken);

        var code = error switch
        {
            MediaProcessingException media => media.Code, StorageConflictException  => "publication_changed",
            StorageUnavailableException    => "storage_unavailable", Win32Exception => "tool_unavailable",
            _ => record.State switch
            {
                DownloadState.CleaningUp => "cleanup_failed", DownloadState.Uploading => "upload_failed",
                DownloadState.Preparing  => "prepare_failed", DownloadState.Canceling => "cancel_failed",
                DownloadState.Deleting   => "delete_failed", _                        => "download_failed"
            }
        };

        var details = error is MediaProcessingException mediaFailure ? mediaFailure.DiagnosticDetails : null;

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
