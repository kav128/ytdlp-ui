using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Ytdlp.Ui.IntegrationTests.Tools;

namespace Ytdlp.Ui.IntegrationTests.Authentication;

[TestFixture]
public class ForwardedHeadersTests
{
    [TestCase("10.0.0.5", true)]
    [TestCase("10.0.0.6", false)]
    public async Task OnlyTrustedProxyCanChangeCallbackSchemeAndHost(string remoteAddress, bool trusted)
    {
        using var factory = new CustomWebApplicationFactory(
            configure: builder => builder.ConfigureAppConfiguration((_, configuration) =>
                                                                        configuration.AddInMemoryCollection(
                                                                            new Dictionary<string, string?>
                                                                            {
                                                                                ["ReverseProxy:KnownProxies:0"] =
                                                                                    "10.0.0.5",
                                                                                ["ReverseProxy:AllowedHosts:0"] =
                                                                                    "media.example.test"
                                                                            }
                                                                        )
            )
        );

        var response = await factory.Server.SendAsync(context =>
                           {
                               context.Request.Method = "GET";
                               context.Request.Scheme = "http";
                               context.Request.Host = new("internal-host:8080");
                               context.Request.Path = "/api/auth/login";
                               context.Connection.RemoteIpAddress = IPAddress.Parse(remoteAddress);
                               context.Request.Headers["X-Forwarded-Proto"] = "https";
                               context.Request.Headers["X-Forwarded-Host"] = "media.example.test";
                           }
                       );

        Assert.That(response.Response.StatusCode, Is.EqualTo(302));
        var location = new Uri(response.Response.Headers.Location.ToString());

        Assert.That(
            QueryHelpers.ParseQuery(location.Query)["redirect_uri"].ToString(),
            Is.EqualTo(trusted ? "https://media.example.test/signin-oidc" : "http://internal-host:8080/signin-oidc")
        );
    }
}
