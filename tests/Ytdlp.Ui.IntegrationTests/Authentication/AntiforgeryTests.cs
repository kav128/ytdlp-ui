using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Ytdlp.Ui.Infrastructure.Authentication;
using Ytdlp.Ui.IntegrationTests.Tools;

namespace Ytdlp.Ui.IntegrationTests.Authentication;

[TestFixture]
public class AntiforgeryTests : SetupBase
{
    [TestCase("POST", "/api/downloads")]
    [TestCase("POST", "/api/downloads/00000000-0000-0000-0000-000000000000/retry")]
    [TestCase("POST", "/api/downloads/00000000-0000-0000-0000-000000000000/cancel")]
    [TestCase("DELETE", "/api/library/weekend")]
    [TestCase("POST", "/api/auth/logout")]
    public async Task MissingOrForgedCsrfCannotChangeState(string method, string path)
    {
        await OidcBrowser.SignInAsync(Factory, Client);
        var before = await Client.GetStringAsync("/api/downloads");

        foreach (var token in new[]
                 {
                     "",
                     "invalid-token"
                 })
        {
            Client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            if (token.Length > 0) Client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);
            using var request = new HttpRequestMessage(new HttpMethod(method), path);

            if (path == "/api/downloads")
                request.Content = JsonContent.Create(new { url = "https://example.com/forged" });

            using var response = await Client.SendAsync(request);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("antiforgery_failed"));
            Assert.That(response.Headers.Location, Is.Null);
            Assert.That(await Client.GetStringAsync("/api/downloads"), Is.EqualTo(before));
            using var stillSignedIn = await Client.GetAsync("/api/auth/csrf");
            Assert.That(stillSignedIn.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }

        using var file = await Client.GetAsync("/api/library/weekend/content");
        Assert.That(file.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task AuthenticatedIdentityWithoutAdminRoleReceivesJson403()
    {
        await OidcBrowser.SignInAsync(Factory, Client);
        using var scope = Factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<Ytdlp.Ui.Infrastructure.Persistence.AppDbContext>();
        var record = database.AdminSessions.Single();
        var store = scope.ServiceProvider.GetRequiredService<SqliteTicketStore>();
        var ticket = (await store.RetrieveAsync(record.Id))!;
        var identity = (ClaimsIdentity)ticket.Principal.Identity!;
        foreach (var claim in identity.FindAll(ClaimTypes.Role).ToArray()) identity.RemoveClaim(claim);
        await store.RenewAsync(record.Id, ticket);
        using var response = await Client.GetAsync("/api/downloads");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(response.Headers.Location, Is.Null);
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/problem+json"));
    }
}
