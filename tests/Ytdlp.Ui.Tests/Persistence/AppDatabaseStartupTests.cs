using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ytdlp.Ui.Infrastructure.Persistence;
using Ytdlp.Ui.Features.Auth;

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
        string token;
        string refreshToken;
        string sessionId;
        string[] migrations;
        using (var first = new TestApplicationFactory(_databasePath))
        {
            using var client = first.CreateClient();
            using var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "demo-password" });
            Assert.That(login.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var tokens = (await login.Content.ReadFromJsonAsync<AuthTokens>())!;
            token = tokens.AccessToken;
            refreshToken = tokens.RefreshToken;
            await using var scope = first.Services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            sessionId = (await context.AdminSessions.SingleAsync()).Id;
            migrations = (await context.Database.GetAppliedMigrationsAsync()).ToArray();
        }

        using var restarted = new TestApplicationFactory(_databasePath);
        await using var restartedScope = restarted.Services.CreateAsyncScope();
        var restored = restartedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.That((await restored.AdminSessions.SingleAsync()).Id, Is.EqualTo(sessionId));
        Assert.That(await restored.Database.GetAppliedMigrationsAsync(), Is.EqualTo(migrations));
        Assert.That(await restored.Database.GetPendingMigrationsAsync(), Is.Empty);
        using var restartedClient = restarted.CreateClient();
        restartedClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var session = await restartedClient.GetAsync("/api/auth/session");
        Assert.That(session.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        restartedClient.DefaultRequestHeaders.Authorization = null;
        using var refresh = await restartedClient.PostAsJsonAsync("/api/auth/refresh", new { refreshToken });
        Assert.That(refresh.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var next = (await refresh.Content.ReadFromJsonAsync<AuthTokens>())!;
        restartedClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", next.AccessToken);
        using var logout = await restartedClient.PostAsync("/api/auth/logout", null);
        Assert.That(logout.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
    }

    [Test]
    public async Task LogoutRevocationSurvivesRestart()
    {
        AuthTokens tokens;
        using (var first = new TestApplicationFactory(_databasePath))
        {
            using var client = first.CreateClient();
            using var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "demo-password" });
            tokens = (await login.Content.ReadFromJsonAsync<AuthTokens>())!;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
            using var logout = await client.PostAsync("/api/auth/logout", null);
            Assert.That(logout.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        }
        using var restarted = new TestApplicationFactory(_databasePath);
        using var restartedClient = restarted.CreateClient();
        restartedClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        using var access = await restartedClient.GetAsync("/api/downloads");
        Assert.That(access.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        restartedClient.DefaultRequestHeaders.Authorization = null;
        using var refresh = await restartedClient.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = tokens.RefreshToken });
        Assert.That(refresh.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
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
