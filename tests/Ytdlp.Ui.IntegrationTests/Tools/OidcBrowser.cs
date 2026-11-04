using System.Net;
using System.Net.Http.Json;
using Ytdlp.Ui.Infrastructure.Authentication;
using AdminAuthentication = Ytdlp.Ui.Infrastructure.Authentication.AuthenticationServiceCollectionExtensions;

namespace Ytdlp.Ui.IntegrationTests.Tools;

public static class OidcBrowser
{
    public static async Task<HttpResponseMessage> LoginAsync(CustomWebApplicationFactory factory, HttpClient client)
    {
        using var challenge = await client.GetAsync("/api/auth/login");
        Assert.That(challenge.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));

        return await client.GetAsync(factory.Provider.Authorize(challenge.Headers.Location!));
    }

    public static async Task<string> SignInAsync(CustomWebApplicationFactory factory, HttpClient client)
    {
        using var callback = await LoginAsync(factory, client);
        Assert.That(callback.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        Assert.That(callback.Headers.Location?.OriginalString, Is.EqualTo("/"));

        var cookie = callback.Headers.GetValues("Set-Cookie")
                             .Single(value => value.StartsWith(
                                         AdminAuthentication.CookieName + "=",
                                         StringComparison.Ordinal
                                     )
                              )
                             .Split(';')[0];

        using var csrf = await client.GetAsync("/api/auth/csrf");
        Assert.That(csrf.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var token = (await csrf.Content.ReadFromJsonAsync<CsrfResponse>())!.RequestToken;
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);

        return cookie;
    }

    private sealed record CsrfResponse(string RequestToken);
}
