using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ytdlp.Ui.Infrastructure.Persistence;
using Ytdlp.Ui.Infrastructure.Persistence.Entities;

namespace Ytdlp.Ui.Tests.Persistence;

[TestFixture]
[Category("SQLite")]
public class AppDatabaseStartupTests
{
    private string _directory = null!;
    private string _databasePath = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "ytdlp-ui-tests", Guid.NewGuid().ToString("N"));
        _databasePath = Path.Combine(_directory, "db", "app.db");
    }

    [TearDown]
    public void TearDown()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ytdlp-ui-tests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(_directory).StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test cleanup path is outside the temporary test root.");
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Test]
    public async Task StartupCreatesMigratedDatabaseInWalModeBeforeRequests()
    {
        Assert.That(Directory.Exists(_directory), Is.False);
        using var factory = new TestApplicationFactory(_databasePath);
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.That(File.Exists(_databasePath), Is.True);
        Assert.That((await context.Database.GetAppliedMigrationsAsync()).ToArray(), Has.Length.EqualTo(1));
        Assert.That(await context.Database.GetPendingMigrationsAsync(), Is.Empty);
        await context.Database.OpenConnectionAsync();
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        Assert.That(await command.ExecuteScalarAsync(), Is.EqualTo("wal"));
    }

    [Test]
    public async Task RestartPreservesExistingDataAndMigrationHistory()
    {
        var issuedAt = DateTimeOffset.UtcNow;
        var sessionId = Guid.NewGuid().ToString("N");
        string[] migrations;
        using (var first = new TestApplicationFactory(_databasePath))
        {
            await using var scope = first.Services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            context.AdminSessions.Add(new AdminSessionRecord
            {
                Id = sessionId, IssuedAt = issuedAt, ExpiresAt = issuedAt.AddHours(1)
            });
            await context.SaveChangesAsync();
            migrations = (await context.Database.GetAppliedMigrationsAsync()).ToArray();
        }

        using var restarted = new TestApplicationFactory(_databasePath);
        await using var restartedScope = restarted.Services.CreateAsyncScope();
        var restored = restartedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.That((await restored.AdminSessions.SingleAsync()).Id, Is.EqualTo(sessionId));
        Assert.That(await restored.Database.GetAppliedMigrationsAsync(), Is.EqualTo(migrations));
        Assert.That(await restored.Database.GetPendingMigrationsAsync(), Is.Empty);
    }

    [Test]
    public void DatabaseInitializationFailurePreventsStartup()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "db"), "This is a file, not a database directory.");
        using var factory = new TestApplicationFactory(_databasePath);
        Assert.Throws<IOException>(() => factory.CreateClient());
        Assert.That(File.Exists(_databasePath), Is.False);
    }
}
