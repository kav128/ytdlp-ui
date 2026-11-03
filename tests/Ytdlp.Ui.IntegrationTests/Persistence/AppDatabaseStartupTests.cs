using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ytdlp.Ui.Infrastructure.Persistence;
using Ytdlp.Ui.IntegrationTests.Tools;

namespace Ytdlp.Ui.IntegrationTests.Persistence;

[TestFixture]
[Category("SQLite")]
public class AppDatabaseStartupTests
{
    private string _directory = null!;
    private string _databasePath = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(SetupGlobal.DirectoryPath, Guid.NewGuid().ToString("N"));
        _databasePath = Path.Combine(_directory, "db", "app.db");
    }

    [TearDown]
    public void TearDown()
    {
        var root = Path.GetFullPath(SetupGlobal.DirectoryPath) + Path.DirectorySeparatorChar;

        if (!Path.GetFullPath(_directory).StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected test directory.");

        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [Test]
    public async Task StartupCreatesMigratedDatabaseInWalModeBeforeRequests()
    {
        Assert.That(Directory.Exists(_directory), Is.False);
        using var factory = new CustomWebApplicationFactory(_databasePath);
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.That(File.Exists(_databasePath), Is.True);
        Assert.That((await database.Database.GetAppliedMigrationsAsync()).Count(), Is.EqualTo(2));
        Assert.That(await database.Database.GetPendingMigrationsAsync(), Is.Empty);
        await database.Database.OpenConnectionAsync();
        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        Assert.That(await command.ExecuteScalarAsync(), Is.EqualTo("wal"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SessionAndRevocationSurviveRestartWithTheSameDatabaseAndKeys(bool revoke)
    {
        string cookie;
        string id;

        using (var first = new CustomWebApplicationFactory(_databasePath))
        {
            using var client = first.CreateBrowserClient();
            cookie = await OidcBrowser.SignInAsync(first, client);
            await using var scope = first.Services.CreateAsyncScope();
            id = (await scope.ServiceProvider.GetRequiredService<AppDbContext>().AdminSessions.SingleAsync()).Id;

            if (revoke)
            {
                using var logout = await client.PostAsync("/api/auth/logout", null);
                Assert.That(logout.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            }
        }

        using var restarted = new CustomWebApplicationFactory(_databasePath);
        using var browser = restarted.CreateBrowserClient();
        browser.DefaultRequestHeaders.Add("Cookie", cookie);
        using var access = await browser.GetAsync("/api/downloads");
        Assert.That(access.StatusCode, Is.EqualTo(revoke ? HttpStatusCode.Unauthorized : HttpStatusCode.OK));
        await using var restoredScope = restarted.Services.CreateAsyncScope();
        var restored = restoredScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.That((await restored.AdminSessions.SingleAsync()).Id, Is.EqualTo(id));
        Assert.That((await restored.Database.GetAppliedMigrationsAsync()).Count(), Is.EqualTo(2));
        Assert.That(restarted.Provider.TokenRequests, Is.Zero);
    }

    [Test]
    public void DatabaseInitializationFailurePreventsStartup()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "db"), "This is a file.");
        using var factory = new CustomWebApplicationFactory(_databasePath);
        Assert.Throws<IOException>(() => factory.CreateBrowserClient());
        Assert.That(File.Exists(_databasePath), Is.False);
    }
}
