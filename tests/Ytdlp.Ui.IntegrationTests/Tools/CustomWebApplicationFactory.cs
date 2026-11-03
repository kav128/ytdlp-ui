using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;

namespace Ytdlp.Ui.IntegrationTests.Tools;

public sealed class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string? _directory;
    private readonly string _databasePath;
    private readonly Action<IWebHostBuilder>? _configure;

    public CustomWebApplicationFactory(string? databasePath = null, Action<IWebHostBuilder>? configure = null)
    {
        if (databasePath is null)
        {
            _directory = Path.Combine(SetupGlobal.DirectoryPath, Guid.NewGuid().ToString("N"));
            databasePath = Path.Combine(_directory, "db", "app.db");
        }

        _databasePath = databasePath;
        _configure = configure;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseStaticWebAssets();

        builder.UseSetting(
            "ConnectionStrings:AppDatabase",
            new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString()
        );

        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                                              new Dictionary<string, string?>
                                              {
                                                  ["Admin:Username"] = "admin",
                                                  ["Admin:Password"] = "demo-password",
                                                  ["Jwt:Issuer"] = "ytdlp-ui-demo",
                                                  ["Jwt:Audience"] = "ytdlp-ui",
                                                  ["Jwt:SigningKey"] =
                                                      "ZGVtby1vbmx5LXNpZ25pbmcta2V5LXJlcGxhY2UtYmVmb3JlLXByb2R1Y3Rpb24tMjAyNg=="
                                              }
                                          )
        );

        builder.ConfigureTestServices(services => services.AddDataProtection()
                                                          .PersistKeysToFileSystem(
                                                               new DirectoryInfo(
                                                                   Path.Combine(
                                                                       Path.GetDirectoryName(_databasePath)!,
                                                                       "keys"
                                                                   )
                                                               )
                                                           )
                                                          .UseEphemeralDataProtectionProvider()
        );

        builder.ConfigureLogging(logging =>
            {
                if (OperatingSystem.IsWindows()) logging.AddFilter<EventLogLoggerProvider>(_ => false);
            }
        );

        _configure?.Invoke(builder);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing && _directory is not null && Directory.Exists(_directory))
        {
            var root = Path.GetFullPath(SetupGlobal.DirectoryPath) + Path.DirectorySeparatorChar;

            if (!Path.GetFullPath(_directory)
                     .StartsWith(
                          root,
                          OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal
                      ))
                throw new InvalidOperationException("Test cleanup path is outside the suite directory.");

            Directory.Delete(_directory, recursive: true);
        }
    }
}
