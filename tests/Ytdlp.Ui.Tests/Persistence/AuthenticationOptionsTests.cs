using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Ytdlp.Ui.Features.Auth;
using Ytdlp.Ui.Infrastructure.Authentication;

namespace Ytdlp.Ui.Tests.Persistence;

[TestFixture]
[Category("SQLite")]
public class AuthenticationOptionsTests
{
    [Test]
    public async Task BoundOptionsConfigureCredentialsAndBothJwtIssuanceAndValidation()
    {
        var signingKeyBytes = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        using var factory = new TestApplicationFactory();
        using var application = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Admin:Username"] = "configured-admin",
                ["Admin:Password"] = "configured-password",
                ["Jwt:Issuer"] = "configured-issuer",
                ["Jwt:Audience"] = "configured-audience",
                ["Jwt:SigningKey"] = Convert.ToBase64String(signingKeyBytes)
            })));
        using var client = application.CreateClient();
        var admin = application.Services.GetRequiredService<IOptions<AdminOptions>>().Value;
        var jwt = application.Services.GetRequiredService<IOptions<JwtOptions>>().Value;

        Assert.That(admin.Username, Is.EqualTo("configured-admin"));
        using var rejected = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "demo-password" });
        Assert.That(rejected.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { username = admin.Username, password = admin.Password });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var tokens = (await response.Content.ReadFromJsonAsync<AuthTokens>())!;
        var token = new JwtSecurityTokenHandler().ReadJwtToken(tokens.AccessToken);

        Assert.Multiple(() =>
        {
            Assert.That(jwt.Issuer, Is.EqualTo("configured-issuer"));
            Assert.That(jwt.Audience, Is.EqualTo("configured-audience"));
            Assert.That(token.Issuer, Is.EqualTo(jwt.Issuer));
            Assert.That(token.Audiences, Is.EqualTo(new[] { jwt.Audience }));
            Assert.That(jwt.GetSigningKeyBytes(), Is.EqualTo(signingKeyBytes));
        });

        using var hmac = new HMACSHA256(signingKeyBytes);
        var expectedSignature = Base64UrlEncoder.Encode(hmac.ComputeHash(
            Encoding.ASCII.GetBytes($"{token.RawHeader}.{token.RawPayload}")));
        Assert.That(token.RawSignature, Is.EqualTo(expectedSignature));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        using var session = await client.GetAsync("/api/auth/session");
        Assert.That(session.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(" ")]
    [TestCase("not-base64!")]
    [TestCase("AAAA")]
    [TestCase("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHg==")]
    public void InvalidSigningKeyPreventsStartup(string? signingKey)
    {
        using var factory = new TestApplicationFactory();
        using var application = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:SigningKey"] = signingKey })));

        var exception = Assert.Throws<OptionsValidationException>(() => application.CreateClient());
        Assert.That(exception!.Message, Does.Contain("Jwt:SigningKey must be valid Base64 encoding at least 32 bytes."));
        if (!string.IsNullOrWhiteSpace(signingKey))
            Assert.That(exception.Message, Does.Not.Contain(signingKey));
    }

    [Test]
    public async Task AbsentAdminOptionsDoNotDefaultToEmptyCredentials()
    {
        using var factory = new TestApplicationFactory();
        using var application = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Admin:Username"] = null,
                ["Admin:Password"] = null
            })));
        using var client = application.CreateClient();

        var admin = application.Services.GetRequiredService<IOptions<AdminOptions>>().Value;
        Assert.That(admin.Username, Is.Null);
        Assert.That(admin.Password, Is.Null);
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { username = "", password = "" });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }
}
