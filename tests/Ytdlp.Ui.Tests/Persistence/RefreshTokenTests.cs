using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Ytdlp.Ui.Features.Auth;
using Ytdlp.Ui.Infrastructure.Persistence;

namespace Ytdlp.Ui.Tests.Persistence;

[TestFixture]
[Category("SQLite")]
public class RefreshTokenTests
{
    private TestApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    [SetUp]
    public void SetUp() { _factory = new TestApplicationFactory(); _client = _factory.CreateClient(); }

    [TearDown]
    public void TearDown() { _client.Dispose(); _factory.Dispose(); }

    [Test]
    public async Task RefreshRotatesSingleUseTokenWithoutExtendingSession()
    {
        var login = await Login();
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var original = await database.AdminSessions.AsNoTracking().SingleAsync();
        using var response = await Refresh(login.RefreshToken);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Headers.CacheControl?.NoStore, Is.True);
        var next = (await response.Content.ReadFromJsonAsync<AuthTokens>())!;
        var stored = await database.AdminSessions.AsNoTracking().SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(next.RefreshToken, Is.Not.EqualTo(login.RefreshToken));
            Assert.That(Convert.FromBase64String(login.RefreshToken), Has.Length.EqualTo(32));
            Assert.That(Convert.FromBase64String(next.RefreshToken), Has.Length.EqualTo(32));
            Assert.That(stored.RefreshTokenHash, Is.Not.EqualTo(original.RefreshTokenHash));
            Assert.That(stored.RefreshTokenHash, Is.EqualTo(SHA256.HashData(Encoding.UTF8.GetBytes(next.RefreshToken))));
            Assert.That(stored.IssuedAt, Is.EqualTo(original.IssuedAt));
            Assert.That(stored.ExpiresAt, Is.EqualTo(original.ExpiresAt));
            Assert.That(new JwtSecurityTokenHandler().ReadJwtToken(next.AccessToken).Id, Is.EqualTo(original.Id));
        });
        using var reused = await Refresh(login.RefreshToken);
        Assert.That(reused.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        using var again = await Refresh(next.RefreshToken);
        Assert.That(again.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task ConcurrentRefreshHasExactlyOneWinner()
    {
        var login = await Login();
        var responses = await Task.WhenAll(Refresh(login.RefreshToken), Refresh(login.RefreshToken));
        try
        {
            Assert.That(responses.Select(response => response.StatusCode), Is.EquivalentTo(new[] { HttpStatusCode.OK, HttpStatusCode.Unauthorized }));
            var winner = (await responses.Single(response => response.IsSuccessStatusCode).Content.ReadFromJsonAsync<AuthTokens>())!;
            using var next = await Refresh(winner.RefreshToken);
            Assert.That(next.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Test]
    public async Task LogoutPersistsRevocationAndRejectsBothTokens()
    {
        var login = await Login();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        using var logout = await _client.PostAsync("/api/auth/logout", null);
        Assert.That(logout.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        await using var scope = _factory.Services.CreateAsyncScope();
        Assert.That((await scope.ServiceProvider.GetRequiredService<AppDbContext>().AdminSessions.SingleAsync()).RevokedAt, Is.Not.Null);
        using var access = await _client.GetAsync("/api/downloads");
        Assert.That(access.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        _client.DefaultRequestHeaders.Authorization = null;
        using var refresh = await Refresh(login.RefreshToken);
        Assert.That(refresh.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task ConcurrentLogoutAndRefreshCannotLeaveLiveTokens()
    {
        var login = await Login();
        using var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        logoutRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        var logoutTask = _client.SendAsync(logoutRequest);
        var refreshTask = Refresh(login.RefreshToken);
        using var logout = await logoutTask;
        using var refresh = await refreshTask;
        Assert.That(logout.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(refresh.StatusCode, Is.AnyOf(HttpStatusCode.OK, HttpStatusCode.Unauthorized));
        if (refresh.IsSuccessStatusCode)
        {
            var next = (await refresh.Content.ReadFromJsonAsync<AuthTokens>())!;
            using var reused = await Refresh(next.RefreshToken);
            Assert.That(reused.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", next.AccessToken);
            using var access = await _client.GetAsync("/api/downloads");
            Assert.That(access.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        }
    }

    [Test]
    public async Task AccessTokenCannotOutliveAbsoluteSessionExpiration()
    {
        var login = await Login();
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()).AddMinutes(1);
        await database.AdminSessions.ExecuteUpdateAsync(update => update.SetProperty(session => session.ExpiresAt, expiresAt));
        using var response = await Refresh(login.RefreshToken);
        var next = (await response.Content.ReadFromJsonAsync<AuthTokens>())!;
        Assert.That(next.ExpiresAt, Is.EqualTo(expiresAt));
        Assert.That(new JwtSecurityTokenHandler().ReadJwtToken(next.AccessToken).ValidTo, Is.EqualTo(expiresAt.UtcDateTime));
        await database.AdminSessions.ExecuteUpdateAsync(update => update
            .SetProperty(session => session.IssuedAt, DateTimeOffset.UtcNow.AddHours(-13))
            .SetProperty(session => session.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
        using var expired = await Refresh(next.RefreshToken);
        Assert.That(expired.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", next.AccessToken);
        using var access = await _client.GetAsync("/api/downloads");
        Assert.That(access.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("invalid")]
    [TestCase("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!")]
    [TestCase("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA!")]
    [TestCase("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [TestCase("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [TestCase("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task InvalidRefreshIsRejected(string? refreshToken)
    {
        using var response = await Refresh(refreshToken);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task FailedRotationDoesNotReturnTokensOrConsumeOldRefresh()
    {
        var login = await Login();
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await database.Database.ExecuteSqlRawAsync("CREATE TRIGGER RejectSessionUpdate BEFORE UPDATE ON AdminSessions BEGIN SELECT RAISE(ABORT, 'Test failure.'); END;");
        using var failure = await Refresh(login.RefreshToken);
        Assert.That(failure.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
        Assert.That(await failure.Content.ReadAsStringAsync(), Does.Not.Contain("accessToken"));
        await database.Database.ExecuteSqlRawAsync("DROP TRIGGER RejectSessionUpdate;");
        using var retry = await Refresh(login.RefreshToken);
        Assert.That(retry.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [TestCase("signature")]
    [TestCase("issuer")]
    [TestCase("audience")]
    [TestCase("algorithm")]
    [TestCase("expired")]
    [TestCase("session")]
    [TestCase("missing_refresh")]
    public async Task InvalidAccessTokensAreRejected(string defect)
    {
        var login = await Login();
        var id = new JwtSecurityTokenHandler().ReadJwtToken(login.AccessToken).Id;
        if (defect == "missing_refresh")
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().AdminSessions.ExecuteUpdateAsync(update => update.SetProperty(session => session.RefreshTokenHash, (byte[]?)null));
        }
        var configuration = _factory.Services.GetRequiredService<IConfiguration>();
        var jwt = new JwtSecurityToken(
            issuer: defect == "issuer" ? "wrong" : configuration["Jwt:Issuer"],
            audience: defect == "audience" ? "wrong" : configuration["Jwt:Audience"],
            claims: [new Claim(JwtRegisteredClaimNames.Jti, defect == "session" ? "missing" : id), new Claim(ClaimTypes.Role, "Admin")],
            expires: defect == "expired" ? DateTime.UtcNow.AddSeconds(-1) : DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(
                defect == "signature" ? Encoding.UTF8.GetBytes(new string('x', 64)) : Convert.FromBase64String(configuration["Jwt:SigningKey"]!)),
                defect == "algorithm" ? SecurityAlgorithms.HmacSha384 : SecurityAlgorithms.HmacSha256));
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
        using var response = await _client.GetAsync("/api/downloads");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    private async Task<AuthTokens> Login()
    {
        using var response = await _client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "demo-password" });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return (await response.Content.ReadFromJsonAsync<AuthTokens>())!;
    }

    private Task<HttpResponseMessage> Refresh(string? refreshToken) => _client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken });
}
