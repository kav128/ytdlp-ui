using Npgsql;
using Testcontainers.PostgreSql;

namespace Ytdlp.Ui.IntegrationTests;

[SetUpFixture]
public sealed class SetupGlobal
{
    private const string _postgresAlias = "ytdlp-postgres-test";
    public static string? DockerNetwork { get; } = Environment.GetEnvironmentVariable("YTDLP_TEST_DOCKER_NETWORK");
    public static PostgreSqlContainer PostgreSql { get; } = BuildPostgreSql();

    private static PostgreSqlContainer BuildPostgreSql()
    {
        var builder = new PostgreSqlBuilder(
                          "postgres:18.6-alpine@sha256:10a5c69a83035ab5239f5620d708520e8b28f5aa1f2b20143e6024532edadfb5"
                      )
                     .WithDatabase("integration_suite")
                     .WithUsername("integration")
                     .WithPassword("integration-password-only");

        if (DockerNetwork is { Length: > 0 } network)
            builder = builder.WithNetwork(network).WithNetworkAliases(_postgresAlias);

        return builder.Build();
    }

    public static string PostgreSqlConnectionString
    {
        get
        {
            var connection = new NpgsqlConnectionStringBuilder(PostgreSql.GetConnectionString())
            {
                GssEncryptionMode = GssEncryptionMode.Disable
            };

            if (DockerNetwork is { Length: > 0 })
            {
                connection.Host = _postgresAlias;
                connection.Port = 5432;
            }

            return connection.ConnectionString;
        }
    }

    public static string DirectoryPath { get; } = Path.Combine(
        Path.GetTempPath(),
        "ytdlp-ui-tests",
        Guid.NewGuid().ToString("N")
    );

    [OneTimeSetUp]
    public async Task SetUp()
    {
        Directory.CreateDirectory(DirectoryPath);

        try
        {
            await PostgreSql.StartAsync();
        }
        catch
        {
            await PostgreSql.DisposeAsync();

            throw;
        }
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        await PostgreSql.DisposeAsync();

        var testRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ytdlp-ui-tests"))
                     + Path.DirectorySeparatorChar;

        if (!Path.GetFullPath(DirectoryPath)
                 .StartsWith(
                      testRoot,
                      OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal
                  ))
            throw new InvalidOperationException("Test directory is outside the temporary test root.");

        if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true);
    }
}
