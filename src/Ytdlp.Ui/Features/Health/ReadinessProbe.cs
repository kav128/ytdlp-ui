using Microsoft.Extensions.Options;
using Npgsql;
using Ytdlp.Ui.Configuration;
using Ytdlp.Ui.Infrastructure.Storage;

namespace Ytdlp.Ui.Features.Health;

public sealed class ReadinessProbe(
    IConfiguration configuration,
    ILibraryStorage storage,
    IOptions<PathsOptions> paths
)
{
    private readonly SemaphoreSlim _singleFlight = new(1, 1);
    private static readonly TimeSpan _budget = TimeSpan.FromSeconds(3);

    public async Task<bool> CheckAsync(CancellationToken cancellationToken)
    {
        // A dependency may take longer to finish cancellation than the HTTP budget.
        // It owns its resources until completion; concurrent probes fail without multiplying work.
        if (!_singleFlight.Wait(0)) return false;

        var pending = ProbeAsync(cancellationToken);

        try
        {
            return await pending.WaitAsync(_budget, cancellationToken);
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private async Task<bool> ProbeAsync(CancellationToken requestAborted)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
            timeout.CancelAfter(_budget);

            // Probe-only settings: a new connection and real SQL cannot mistake a cached socket for readiness.
            // Neither this connection nor its cancellation/disposal belongs to the request's DbContext scope.
            var settings = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("AppDatabase"))
            {
                Pooling = false,
                Enlist = false,
                Timeout = 2,
                CommandTimeout = 2,
                CancellationTimeout = -1
            };

            await using (var connection = new NpgsqlConnection(settings.ToString()))
            {
                await connection.OpenAsync(timeout.Token);
                await using var command = new NpgsqlCommand("SELECT 1", connection);

                if (!Equals(await command.ExecuteScalarAsync(timeout.Token), 1)) return false;
            }

            HealthEndpoints.ProbePaths(paths.Value);
            await storage.CheckReadyAsync(timeout.Token);

            return !timeout.IsCancellationRequested;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            _singleFlight.Release();
        }
    }
}
