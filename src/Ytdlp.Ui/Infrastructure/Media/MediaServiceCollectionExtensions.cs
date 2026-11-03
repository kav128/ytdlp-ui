using Ytdlp.Ui.Configuration;
using Ytdlp.Ui.Features.Downloads;

namespace Ytdlp.Ui.Infrastructure.Media;

public static class MediaServiceCollectionExtensions
{
    public static IServiceCollection AddMediaProcessing(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<MediaToolsOptions>()
                .Bind(configuration.GetSection("MediaTools"))
                .Validate(
                     options => new[]
                         {
                             options.YtDlpPath,
                             options.FfmpegPath,
                             options.FfprobePath,
                             options.NodePath
                         }
                        .All(value => !string.IsNullOrWhiteSpace(value) && !value.Contains('\0')),
                     "Media tool paths are required."
                 )
                .ValidateOnStart();

        services.AddOptions<WorkerOptions>()
                .Bind(configuration.GetSection("Worker"))
                .Validate(options => options.PollIntervalMilliseconds > 0, "Worker poll interval must be positive.")
                .ValidateOnStart();

        services.AddSingleton<IProcessRunner, SystemProcessRunner>();
        services.AddSingleton<MediaProcessor>();
        services.AddSingleton<WorkspaceStorage>();
        services.AddSingleton<ArtifactIntegrity>();
        services.AddSingleton<MediaPipeline>();
        services.AddHostedService<DownloadWorker>();

        return services;
    }
}
