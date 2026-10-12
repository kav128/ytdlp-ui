using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Ytdlp.Ui.Infrastructure.Authentication;
using Ytdlp.Ui.Infrastructure.Persistence;

namespace Ytdlp.Ui.Tests.Persistence;

[TestFixture]
[Category("SQLite")]
public class AuthenticationServiceTimeTests
{
    [Test]
    public async Task ControlledTimeDefinesTokenPrecisionAndAbsoluteSessionBoundary()
    {
        var now = new DateTimeOffset(2026, 10, 12, 10, 0, 0, 123, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);

        using var factory = new TestApplicationFactory();
        using var application = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(clock)));
        await using var scope = application.Services.CreateAsyncScope();
        var authentication = scope.ServiceProvider.GetRequiredService<AuthenticationService>();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var login = (await authentication.LoginAsync("admin", "demo-password", CancellationToken.None))!;
        var session = await database.AdminSessions.AsNoTracking().SingleAsync();
        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds());

        Assert.Multiple(() =>
        {
            Assert.That(session.IssuedAt, Is.EqualTo(issuedAt));
            Assert.That(session.ExpiresAt, Is.EqualTo(issuedAt.AddHours(12)));
            Assert.That(login.ExpiresAt, Is.EqualTo(issuedAt.AddMinutes(5)));
            Assert.That(new JwtSecurityTokenHandler().ReadJwtToken(login.AccessToken).ValidTo,
                Is.EqualTo(login.ExpiresAt.UtcDateTime));
        });

        clock.SetUtcNow(session.ExpiresAt.AddMinutes(-1).AddMilliseconds(123));
        Assert.That(await authentication.IsSessionActiveAsync(session.Id, CancellationToken.None), Is.True);
        var refreshed = await authentication.RefreshAsync(login.RefreshToken, CancellationToken.None);
        Assert.That(refreshed, Is.Not.Null);
        Assert.That(refreshed!.ExpiresAt, Is.EqualTo(session.ExpiresAt));
        Assert.That(new JwtSecurityTokenHandler().ReadJwtToken(refreshed.AccessToken).ValidTo,
            Is.EqualTo(session.ExpiresAt.UtcDateTime));
        Assert.That((await database.AdminSessions.AsNoTracking().SingleAsync()).ExpiresAt,
            Is.EqualTo(session.ExpiresAt));

        clock.SetUtcNow(session.ExpiresAt);
        Assert.That(await authentication.IsSessionActiveAsync(session.Id, CancellationToken.None), Is.False);
        Assert.That(await authentication.RefreshAsync(refreshed.RefreshToken, CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task RevocationPersistsTheInjectedTimeAndRejectsRefresh()
    {
        var now = new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);

        using var factory = new TestApplicationFactory();
        using var application = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(clock)));
        await using var scope = application.Services.CreateAsyncScope();
        var authentication = scope.ServiceProvider.GetRequiredService<AuthenticationService>();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var login = (await authentication.LoginAsync("admin", "demo-password", CancellationToken.None))!;
        var session = await database.AdminSessions.AsNoTracking().SingleAsync();

        clock.Advance(TimeSpan.FromMinutes(3).Add(TimeSpan.FromMilliseconds(456)));
        await authentication.RevokeAsync(session.Id, CancellationToken.None);

        Assert.That((await database.AdminSessions.AsNoTracking().SingleAsync()).RevokedAt, Is.EqualTo(now.AddMinutes(3).AddMilliseconds(456)));
        Assert.That(await authentication.IsSessionActiveAsync(session.Id, CancellationToken.None), Is.False);
        Assert.That(await authentication.RefreshAsync(login.RefreshToken, CancellationToken.None), Is.Null);
    }
}
