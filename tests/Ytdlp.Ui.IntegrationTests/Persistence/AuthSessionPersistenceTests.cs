using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ytdlp.Ui.Infrastructure.Persistence;

namespace Ytdlp.Ui.IntegrationTests.Persistence;

[TestFixture]
[Category("SQLite")]
public class AuthSessionPersistenceTests : SetupBase
{
    [Test]
    public async Task LoginPersistsSessionMatchingTokenAndResponseBeforeReturning()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        using var response = await Client.PostAsJsonAsync(
                                 "/api/auth/login",
                                 new
                                 {
                                     username = "admin",
                                     password = "demo-password"
                                 }
                             );

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Headers.CacheControl?.NoStore, Is.True);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var accessToken = body.GetProperty("accessToken").GetString()!;
        var token = new JwtSecurityTokenHandler().ReadJwtToken(accessToken);
        await using var scope = Factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await database.AdminSessions.SingleAsync();

        Assert.Multiple(() =>
            {
                Assert.That(body.GetProperty("tokenType").GetString(), Is.EqualTo("Bearer"));
                Assert.That(session.Id, Is.EqualTo(token.Id));
                Assert.That(session.IssuedAt, Is.InRange(before, DateTimeOffset.UtcNow));
                Assert.That(session.ExpiresAt - session.IssuedAt, Is.EqualTo(TimeSpan.FromHours(12)));

                Assert.That(
                    token.ValidTo - session.IssuedAt.UtcDateTime,
                    Is.InRange(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5).Add(TimeSpan.FromSeconds(1)))
                );

                Assert.That(body.GetProperty("expiresAt").GetDateTimeOffset().UtcDateTime, Is.EqualTo(token.ValidTo));
                var refreshToken = body.GetProperty("refreshToken").GetString()!;
                Assert.That(Convert.FromBase64String(refreshToken), Has.Length.EqualTo(32));

                Assert.That(
                    session.RefreshTokenHash,
                    Is.EqualTo(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)))
                );

                Assert.That(session.RefreshTokenHash, Has.Length.EqualTo(32));
                Assert.That(session.RevokedAt, Is.Null);
            }
        );

        await database.Database.OpenConnectionAsync();
        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT typeof(RefreshTokenHash) || ':' || length(RefreshTokenHash) FROM AdminSessions;";
        Assert.That(await command.ExecuteScalarAsync(), Is.EqualTo("blob:32"));

        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var authenticated = await Client.GetAsync("/api/downloads");
        Assert.That(authenticated.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [TestCase("incorrect", "demo-password")]
    [TestCase("admin", "incorrect")]
    public async Task IncorrectCredentialsDoNotPersistSession(string username, string password)
    {
        using var response = await Client.PostAsJsonAsync(
                                 "/api/auth/login",
                                 new
                                 {
                                     username,
                                     password
                                 }
                             );

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(response.Headers.CacheControl?.NoStore, Is.True);
        await using var scope = Factory.Services.CreateAsyncScope();
        Assert.That(await scope.ServiceProvider.GetRequiredService<AppDbContext>().AdminSessions.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task SessionWriteFailurePreventsSuccessfulLogin()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await database.Database.ExecuteSqlRawAsync(
            """
            CREATE TRIGGER RejectSessionInsert BEFORE INSERT ON AdminSessions
            BEGIN SELECT RAISE(ABORT, 'Session writes are disabled by this test.'); END;
            """
        );

        using var response = await Client.PostAsJsonAsync(
                                 "/api/auth/login",
                                 new
                                 {
                                     username = "admin",
                                     password = "demo-password"
                                 }
                             );

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/problem+json"));
        var problem = await response.Content.ReadAsStringAsync();
        using var problemDocument = JsonDocument.Parse(problem);
        Assert.That(problemDocument.RootElement.GetProperty("status").GetInt32(), Is.EqualTo(500));
        Assert.That(problem, Does.Not.Contain("accessToken"));
        Assert.That(problem, Does.Not.Contain("refreshToken"));
        Assert.That(problem, Does.Not.Contain("demo-password"));
        Assert.That(problem, Does.Not.Contain("Session writes are disabled"));
        Assert.That(problem, Does.Not.Contain("DbUpdateException"));
        Assert.That(problem, Does.Not.Contain("SqliteException"));
        Assert.That(await database.AdminSessions.CountAsync(), Is.Zero);

        await database.Database.ExecuteSqlRawAsync("DROP TRIGGER RejectSessionInsert;");

        using var retry = await Client.PostAsJsonAsync(
                              "/api/auth/login",
                              new
                              {
                                  username = "admin",
                                  password = "demo-password"
                              }
                          );

        Assert.That(retry.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await database.AdminSessions.CountAsync(), Is.EqualTo(1));
    }
}
