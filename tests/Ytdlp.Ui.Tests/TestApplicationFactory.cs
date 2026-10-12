using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;

namespace Ytdlp.Ui.Tests;

public sealed class TestApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string? _directory;
    private readonly string _databasePath;

    public TestApplicationFactory(string? databasePath = null)
    {
        if (databasePath is null)
        {
            _directory = Path.Combine(Path.GetTempPath(), "ytdlp-ui-tests", Guid.NewGuid().ToString("N"));
            databasePath = Path.Combine(_directory, "db", "app.db");
        }
        _databasePath = databasePath;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:AppDatabase",
            new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString());
        builder.ConfigureServices(services => services.AddDataProtection().UseEphemeralDataProtectionProvider());
        builder.ConfigureLogging(logging =>
        {
            if (OperatingSystem.IsWindows()) logging.AddFilter<EventLogLoggerProvider>(_ => false);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && _directory is not null && Directory.Exists(_directory))
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ytdlp-ui-tests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(_directory).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Test cleanup path is outside the temporary test root.");
            Directory.Delete(_directory, recursive: true);
        }
    }
}
