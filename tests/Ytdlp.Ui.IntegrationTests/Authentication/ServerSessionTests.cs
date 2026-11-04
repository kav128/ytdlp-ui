using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Ytdlp.Ui.Infrastructure.Persistence;
using Ytdlp.Ui.IntegrationTests.Tools;

namespace Ytdlp.Ui.IntegrationTests.Authentication;

[TestFixture]
[Category("PostgreSQL")]
public class ServerSessionTests : SetupBase
{
    private FakeTimeProvider _clock = null!;

    protected override void ConfigureApplication(IWebHostBuilder builder)
    {
        _clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(_clock));
    }

    [Test]
    public async Task ActivityDoesNotExtendAbsoluteSessionAndExpirationIsEnforced()
    {
        await OidcBrowser.SignInAsync(Factory, Client);
        await using var scope = Factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await database.AdminSessions.AsNoTracking().SingleAsync();
        Assert.That(session.IssuedAt, Is.EqualTo(_clock.GetUtcNow()));
        _clock.Advance(TimeSpan.FromHours(11).Add(TimeSpan.FromMinutes(59)));
        using var before = await Client.GetAsync("/api/downloads");
        Assert.That(before.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(before.Headers.TryGetValues("Set-Cookie", out _), Is.False);

        Assert.That(
            (await database.AdminSessions.AsNoTracking().SingleAsync()).ExpiresAt,
            Is.EqualTo(session.ExpiresAt)
        );

        _clock.SetUtcNow(session.ExpiresAt);
        using var expired = await Client.GetAsync("/api/downloads");
        Assert.That(expired.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(expired.Headers.Location, Is.Null);
    }

    [Test]
    public async Task LogoutRevokesTheTicketAtTheInjectedTime()
    {
        var cookie = await OidcBrowser.SignInAsync(Factory, Client);
        _clock.Advance(TimeSpan.FromMinutes(3));
        using var logout = await Client.PostAsync("/api/auth/logout", null);
        Assert.That(logout.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        await using var scope = Factory.Services.CreateAsyncScope();
        var session = await scope.ServiceProvider.GetRequiredService<AppDbContext>().AdminSessions.SingleAsync();
        Assert.That(session.RevokedAt, Is.EqualTo(_clock.GetUtcNow()));
        Assert.That(session.ProtectedTicket, Is.Null);
        using var replay = Factory.CreateBrowserClient();
        replay.DefaultRequestHeaders.Add("Cookie", cookie);
        using var response = await replay.GetAsync("/api/downloads");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task MissingOrCorruptTicketDoesNotAuthenticate()
    {
        await OidcBrowser.SignInAsync(Factory, Client);
        await using var scope = Factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await database.AdminSessions.ExecuteUpdateAsync(update => update.SetProperty(
                                                            session => session.ProtectedTicket,
                                                            new byte[]
                                                            {
                                                                1,
                                                                2,
                                                                3
                                                            }
                                                        )
        );

        using var corrupt = await Client.GetAsync("/api/downloads");
        Assert.That(corrupt.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        await database.AdminSessions.ExecuteDeleteAsync();
        using var missing = await Client.GetAsync("/api/downloads");
        Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }
}
