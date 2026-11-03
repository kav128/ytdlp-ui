using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ytdlp.Ui.Infrastructure.Persistence;
using Ytdlp.Ui.IntegrationTests.Tools;

namespace Ytdlp.Ui.IntegrationTests.Persistence;

[TestFixture]
[Category("SQLite")]
public class AuthSessionPersistenceTests : SetupBase
{
    [Test]
    public async Task LoginPersistsProtectedTicketBeforeReturningCookie()
    {
        await OidcBrowser.SignInAsync(Factory, Client);
        await using var scope = Factory.Services.CreateAsyncScope();
        var session = await scope.ServiceProvider.GetRequiredService<AppDbContext>().AdminSessions.SingleAsync();
        Assert.That(session.ProtectedTicket, Is.Not.Null.And.Not.Empty);

        Assert.That(
            Encoding.UTF8.GetString(session.ProtectedTicket!),
            Does.Not.Contain("oidc-user").And.Not.Contain("test-client-secret")
        );

        Assert.That(session.Subject, Is.EqualTo("oidc-user"));
        Assert.That(session.Issuer, Is.EqualTo(FakeOidcProvider.Authority));
        Assert.That(session.ExpiresAt - session.IssuedAt, Is.EqualTo(TimeSpan.FromHours(12)));
    }

    [Test]
    public async Task SessionWriteFailurePreventsSuccessfulLogin()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await database.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER RejectSessionInsert BEFORE INSERT ON AdminSessions BEGIN SELECT RAISE(ABORT, 'Session writes are disabled by this test.'); END;"
        );

        using var response = await OidcBrowser.LoginAsync(Factory, Client);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/problem+json"));
        var problem = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(problem);
        Assert.That(document.RootElement.GetProperty("status").GetInt32(), Is.EqualTo(500));

        foreach (var secret in new[]
                 {
                     "accessToken",
                     "refreshToken",
                     "test-client-secret",
                     "Session writes are disabled",
                     "DbUpdateException",
                     "SqliteException"
                 })
            Assert.That(problem, Does.Not.Contain(secret));

        Assert.That(await database.AdminSessions.CountAsync(), Is.Zero);
        await database.Database.ExecuteSqlRawAsync("DROP TRIGGER RejectSessionInsert;");
        await OidcBrowser.SignInAsync(Factory, Client);
    }

    [Test]
    public async Task FailedLogoutDoesNotConfirmRevocation()
    {
        await OidcBrowser.SignInAsync(Factory, Client);
        await using var scope = Factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await database.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER RejectSessionUpdate BEFORE UPDATE ON AdminSessions BEGIN SELECT RAISE(ABORT, 'Test failure.'); END;"
        );

        using var failure = await Client.PostAsync("/api/auth/logout", null);
        Assert.That(failure.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
        Assert.That((await database.AdminSessions.AsNoTracking().SingleAsync()).RevokedAt, Is.Null);
        using var access = await Client.GetAsync("/api/downloads");
        Assert.That(access.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        await database.Database.ExecuteSqlRawAsync("DROP TRIGGER RejectSessionUpdate;");
        using var retry = await Client.PostAsync("/api/auth/logout", null);
        Assert.That(retry.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
    }
}
