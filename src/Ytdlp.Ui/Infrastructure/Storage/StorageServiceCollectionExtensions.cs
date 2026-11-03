using Amazon.Runtime;
using Amazon.S3;
using Microsoft.Extensions.Options;
using Ytdlp.Ui.Configuration;
using Ytdlp.Ui.Features.Library;

namespace Ytdlp.Ui.Infrastructure.Storage;

public static class StorageServiceCollectionExtensions
{
    public static IServiceCollection AddLibraryStorage(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<S3Options>()
                .Bind(configuration.GetSection("S3"))
                .Validate(
                     options => Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var uri)
                             && uri.Scheme is "http" or "https"
                             && uri.UserInfo.Length == 0,
                     "S3:Endpoint must be an absolute HTTP(S) URL."
                 )
                .Validate(
                     options => !string.IsNullOrWhiteSpace(options.Bucket)
                             && !string.IsNullOrWhiteSpace(options.Region)
                             && !string.IsNullOrWhiteSpace(options.AccessKey)
                             && !string.IsNullOrWhiteSpace(options.SecretKey),
                     "S3 bucket, region and credentials must be configured."
                 )
                .ValidateOnStart();

        services.AddOptions<PathsOptions>()
                .Bind(configuration.GetSection("Paths"))
                .Validate(
                     options => !string.IsNullOrWhiteSpace(options.WorkDirectory),
                     "Paths:WorkDirectory is required."
                 );

        services.AddSingleton<IAmazonS3>(provider =>
            {
                var options = provider.GetRequiredService<IOptions<S3Options>>().Value;

                return new AmazonS3Client(
                    new BasicAWSCredentials(options.AccessKey, options.SecretKey),
                    new AmazonS3Config
                    {
                        ServiceURL = options.Endpoint,
                        AuthenticationRegion = options.Region,
                        ForcePathStyle = options.ForcePathStyle,
                        RetryMode = RequestRetryMode.Standard,
                        MaxErrorRetry = 0,
                        Timeout = System.Threading.Timeout.InfiniteTimeSpan
                    }
                );
            }
        );

        services.AddSingleton<ILibraryStorage, S3LibraryStorage>();
        services.AddScoped<LibraryDeletionService>();

        return services;
    }
}
