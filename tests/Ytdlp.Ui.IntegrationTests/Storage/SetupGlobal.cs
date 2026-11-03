using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;

namespace Ytdlp.Ui.IntegrationTests.Storage;

[SetUpFixture]
public sealed class SetupGlobal
{
    public static IContainer Container { get; } = new ContainerBuilder(
                                                      Environment.GetEnvironmentVariable("MINIO_TEST_IMAGE")
                                                   ?? "bitnamilegacy/minio:2025.7.23-debian-12-r5@sha256:6dabb4a2088c9a79908de3bc05f4586c23ad2182c8908e7e3acbf61c1467fb20"
                                                  )
                                                 .WithImagePullPolicy(PullPolicy.Missing)
                                                 .WithEnvironment("MINIO_ROOT_USER", "integration-user")
                                                 .WithEnvironment("MINIO_ROOT_PASSWORD", "integration-password-only")
                                                 .WithEntrypoint("minio")
                                                 .WithCommand("server", "/tmp/minio-data")
                                                 .WithPortBinding(9000, true)
                                                 .WithWaitStrategy(
                                                      Wait.ForUnixContainer()
                                                          .UntilHttpRequestIsSucceeded(request =>
                                                                                           request.ForPort(9000)
                                                                                                  .ForPath(
                                                                                                       "/minio/health/ready"
                                                                                                   )
                                                           )
                                                  )
                                                 .Build();

    public static string Endpoint => $"http://{Container.Hostname}:{Container.GetMappedPublicPort(9000)}";

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        try
        {
            await Container.StartAsync();
        }
        catch
        {
            await Container.DisposeAsync();

            throw;
        }
    }

    [OneTimeTearDown]
    public async Task StopAsync() => await Container.DisposeAsync();
}
