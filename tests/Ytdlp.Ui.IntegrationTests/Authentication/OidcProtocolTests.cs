using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ytdlp.Ui.Infrastructure.Persistence;
using Ytdlp.Ui.IntegrationTests.Tools;
using AdminAuthentication = Ytdlp.Ui.Infrastructure.Authentication.AuthenticationServiceCollectionExtensions;

namespace Ytdlp.Ui.IntegrationTests.Authentication;

[TestFixture]
public class OidcProtocolTests : SetupBase
{
    [Test]
    public async Task RealCodeFlowUsesPkceRsaAndGroupsOnlyFromUserInfo()
    {
        using var response = await OidcBrowser.LoginAsync(Factory, Client);
        Assert.That(response.Headers.Location?.OriginalString, Is.EqualTo("/"));
        Assert.That(Factory.Provider.TokenRequests, Is.EqualTo(1));
        Assert.That(Factory.Provider.UserInfoRequests, Is.EqualTo(1));
        Assert.That(Factory.Provider.LastCodeVerifier, Is.Not.Empty);

        Assert.That(
            Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(
                SHA256.HashData(Encoding.ASCII.GetBytes(Factory.Provider.LastCodeVerifier!))
            ),
            Is.EqualTo(Factory.Provider.LastCodeChallenge)
        );

        var cookie = response.Headers.GetValues("Set-Cookie")
                             .Single(value => value.StartsWith(
                                         AdminAuthentication.CookieName + "=",
                                         StringComparison.Ordinal
                                     )
                              );

        Assert.That(
            cookie.ToLowerInvariant(),
            Does.Contain("httponly").And.Contain("secure").And.Contain("samesite=lax")
        );

        Assert.That(cookie.ToLowerInvariant(), Does.Not.Contain("expires="));
        using var session = await Client.GetAsync("/api/auth/session");

        Assert.That(
            (await session.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("authenticated").GetBoolean(),
            Is.True
        );

        await using var scope = Factory.Services.CreateAsyncScope();
        var record = await scope.ServiceProvider.GetRequiredService<AppDbContext>().AdminSessions.SingleAsync();
        Assert.That(record.Issuer, Is.EqualTo(FakeOidcProvider.Authority));
        Assert.That(record.Subject, Is.EqualTo("oidc-user"));
        Assert.That(record.ProtectedTicket, Is.Not.Null.And.Not.Empty);

        Assert.That(
            Encoding.UTF8.GetString(record.ProtectedTicket!),
            Does.Not.Contain("oidc-user").And.Not.Contain("access_token")
        );

        Assert.That(record.ExpiresAt - record.IssuedAt, Is.EqualTo(TimeSpan.FromHours(12)));
    }

    [TestCase("state")]
    [TestCase("missing_state")]
    [TestCase("nonce")]
    [TestCase("missing_nonce")]
    [TestCase("signature")]
    [TestCase("issuer")]
    [TestCase("audience")]
    [TestCase("expired")]
    [TestCase("unsigned")]
    [TestCase("pkce")]
    [TestCase("userinfo_sub")]
    public async Task InvalidProtocolResponseNeverCreatesSession(string defect)
    {
        Factory.Provider.Defect = defect;
        using var response = await OidcBrowser.LoginAsync(Factory, Client);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        Assert.That(response.Headers.Location?.OriginalString, Is.EqualTo("/?authError=login_failed"));

        Assert.That(
            response.Headers.TryGetValues("Set-Cookie", out var cookies)
         && cookies.Any(value => value.StartsWith(AdminAuthentication.CookieName + "=")),
            Is.False
        );

        await using var scope = Factory.Services.CreateAsyncScope();
        Assert.That(await scope.ServiceProvider.GetRequiredService<AppDbContext>().AdminSessions.CountAsync(), Is.Zero);
        using var access = await Client.GetAsync("/api/downloads");
        Assert.That(access.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(access.Headers.Location, Is.Null);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task MissingOrWrongGroupDeniesManagementAndKeepsLibraryPublic(bool missing)
    {
        Factory.Provider.Groups = ["visitors"];
        if (missing) Factory.Provider.Defect = "missing_groups";
        using var response = await OidcBrowser.LoginAsync(Factory, Client);
        Assert.That(response.Headers.Location?.OriginalString, Is.EqualTo("/?authError=access_denied"));
        await using var scope = Factory.Services.CreateAsyncScope();
        Assert.That(await scope.ServiceProvider.GetRequiredService<AppDbContext>().AdminSessions.CountAsync(), Is.Zero);
        using var library = await Client.GetAsync("/api/library");
        Assert.That(library.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var forged = await Client.GetAsync("/api/downloads?groups=admins&role=Admin");
        Assert.That(forged.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task CallbackWithoutCorrelationCookieCannotSignIn()
    {
        using var challenge = await Client.GetAsync("/api/auth/login");
        using var otherBrowser = Factory.CreateBrowserClient();
        using var response = await otherBrowser.GetAsync(Factory.Provider.Authorize(challenge.Headers.Location!));
        Assert.That(response.Headers.Location?.OriginalString, Is.EqualTo("/?authError=login_failed"));
        Assert.That(Factory.Provider.TokenRequests, Is.Zero);
    }

    [TestCase("https://evil.test/")]
    [TestCase("//evil.test/")]
    [TestCase("/\\evil.test/")]
    [TestCase("\\evil.test/")]
    public async Task LoginRejectsExternalReturnUrl(string returnUrl)
    {
        using var response = await Client.GetAsync("/api/auth/login?returnUrl=" + Uri.EscapeDataString(returnUrl));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(response.Headers.Location, Is.Null);
    }

    [Test]
    public async Task LocalReturnUrlSurvivesSuccessfulFlow()
    {
        using var challenge = await Client.GetAsync("/api/auth/login?returnUrl=/client-route");
        using var response = await Client.GetAsync(Factory.Provider.Authorize(challenge.Headers.Location!));
        Assert.That(response.Headers.Location?.OriginalString, Is.EqualTo("/client-route"));
    }

    [Test]
    public async Task OldPasswordRefreshAndBearerDoNotAuthenticate()
    {
        using var oldLogin = await Client.PostAsJsonAsync(
                                 "/api/auth/login",
                                 new
                                 {
                                     username = "admin",
                                     password = "demo-password"
                                 }
                             );

        Assert.That(oldLogin.IsSuccessStatusCode, Is.False);
        using var refresh = await Client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = "old-refresh" });
        Assert.That(refresh.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "old-jwt");
        Client.DefaultRequestHeaders.Add("Cookie", "access_token=old-jwt");
        using var access = await Client.GetAsync("/api/downloads?access_token=old-jwt");
        Assert.That(access.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(access.Headers.Location, Is.Null);
    }

    [Test]
    public async Task UiSessionIdentifierIsStableIndependentAndCannotAuthenticateRequests()
    {
        await OidcBrowser.SignInAsync(Factory, Client);
        var first = await Client.GetFromJsonAsync<JsonElement>("/api/auth/session");
        var id = first.GetProperty("sessionId").GetString()!;
        var repeated = await Client.GetFromJsonAsync<JsonElement>("/api/auth/session");
        Assert.That(repeated.GetProperty("sessionId").GetString(), Is.EqualTo(id));
        await using var scope = Factory.Services.CreateAsyncScope();
        var record = await scope.ServiceProvider.GetRequiredService<AppDbContext>().AdminSessions.SingleAsync();
        Assert.That(id, Is.Not.EqualTo(record.Id));
        using var attacker = Factory.CreateBrowserClient();
        attacker.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", id);
        attacker.DefaultRequestHeaders.Add("X-Session-Id", id);
        attacker.DefaultRequestHeaders.Add("Cookie", AdminAuthentication.CookieName + "=" + id);
        using var forged = await attacker.GetAsync("/api/downloads?sessionId=" + id);
        Assert.That(forged.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        await OidcBrowser.SignInAsync(Factory, Client);
        var next = await Client.GetFromJsonAsync<JsonElement>("/api/auth/session");
        Assert.That(next.GetProperty("sessionId").GetString(), Is.Not.EqualTo(id));
    }
}
