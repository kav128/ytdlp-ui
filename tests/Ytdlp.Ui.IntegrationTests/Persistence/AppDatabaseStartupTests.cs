using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ytdlp.Ui.Infrastructure.Persistence;
using Ytdlp.Ui.IntegrationTests.Tools;

namespace Ytdlp.Ui.IntegrationTests.Persistence;

[TestFixture, Category("PostgreSQL"), NonParallelizable]
public class AppDatabaseStartupTests
{
    private TestDatabase _database = null!;

    [SetUp]
    public void SetUp() => _database = new TestDatabase();

    [TearDown]
    public void TearDown() => _database.Dispose();

    [Test]
    public async Task StartupMigratesPostgreSqlBeforeAcceptingRequests()
    {
        using var factory = new CustomWebApplicationFactory(_database);
        using var client = factory.CreateBrowserClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.That((await database.Database.GetAppliedMigrationsAsync()).Single(), Does.EndWith("_InitialPostgreSql"));
        Assert.That(await database.Database.GetPendingMigrationsAsync(), Is.Empty);
        using var response = await client.GetAsync("/api/auth/session");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SessionAndRevocationSurviveRestartWithTheSameDatabaseAndKeys(bool revoke)
    {
        string cookie, id;

        using (var first = new CustomWebApplicationFactory(_database))
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

        using var restarted = new CustomWebApplicationFactory(_database);
        using var replay = restarted.CreateBrowserClient();
        replay.DefaultRequestHeaders.Add("Cookie", cookie);
        using var response = await replay.GetAsync("/api/downloads");
        Assert.That(response.StatusCode, Is.EqualTo(revoke ? HttpStatusCode.Unauthorized : HttpStatusCode.OK));
        await using var check = restarted.Services.CreateAsyncScope();

        Assert.That(
            (await check.ServiceProvider.GetRequiredService<AppDbContext>().AdminSessions.SingleAsync()).Id,
            Is.EqualTo(id)
        );

        Assert.That(restarted.Provider.TokenRequests, Is.Zero);
    }

    [Test]
    public void UnavailablePostgreSqlPreventsStartupWithoutExposingPassword()
    {
        var connection =
            new NpgsqlConnectionStringBuilder(_database.ConnectionString) { Password = "private-wrong-password" };

        using var factory = new CustomWebApplicationFactory(
            _database,
            builder => builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                                                             new Dictionary<string, string?>
                                                             {
                                                                 ["ConnectionStrings:AppDatabase"] =
                                                                     connection.ToString()
                                                             }
                                                         )
            )
        );

        var error = Assert.Throws<PostgresException>(() => factory.CreateBrowserClient());
        Assert.That(error!.SqlState, Is.EqualTo(PostgresErrorCodes.InvalidPassword));
        Assert.That(error.ToString(), Does.Not.Contain("private-wrong-password"));
    }

    [Test]
    public async Task CleanPostgreSqlDoesNotImportOldTicketsOrTouchLegacyFile()
    {
        var legacy = Path.Combine(_database.DirectoryPath, "old-sqlite.db");
        File.WriteAllText(legacy, "legacy data must remain untouched");
        string cookie;

        using (var old = new CustomWebApplicationFactory(_database))
        {
            using var client = old.CreateBrowserClient();
            cookie = await OidcBrowser.SignInAsync(old, client);
        }

        using var clean = new TestDatabase();

        using var fresh = new CustomWebApplicationFactory(
            clean,
            builder => builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                                                             new Dictionary<string, string?>
                                                             {
                                                                 ["Paths:DataProtectionKeysDirectory"] =
                                                                     Path.Combine(_database.DirectoryPath, "keys")
                                                             }
                                                         )
            )
        );

        using var replay = fresh.CreateBrowserClient();
        replay.DefaultRequestHeaders.Add("Cookie", cookie);
        using var response = await replay.GetAsync("/api/downloads");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        await using var scope = fresh.Services.CreateAsyncScope();
        Assert.That(await scope.ServiceProvider.GetRequiredService<AppDbContext>().AdminSessions.CountAsync(), Is.Zero);
        Assert.That(File.ReadAllText(legacy), Is.EqualTo("legacy data must remain untouched"));
    }
}
