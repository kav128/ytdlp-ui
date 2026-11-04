using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Ytdlp.Ui.IntegrationTests.Tools;

namespace Ytdlp.Ui.IntegrationTests.Authentication;

[TestFixture]
public class AuthenticationOptionsTests
{
    [TestCase("Oidc:Authority", null)]
    [TestCase("Oidc:Authority", "http://auth.test/")]
    [TestCase("Oidc:Authority", "not-an-issuer")]
    [TestCase("Oidc:ClientId", "")]
    [TestCase("Oidc:ClientSecret", "")]
    [TestCase("Oidc:GroupsClaim", "")]
    [TestCase("Oidc:AdminGroup", "")]
    [TestCase("Paths:DataProtectionKeysDirectory", "")]
    public void MissingOrInvalidRequiredConfigurationPreventsStartup(string key, string? value)
    {
        using var factory = new CustomWebApplicationFactory(
            configure: builder => builder.ConfigureAppConfiguration((_, configuration) =>
                                                                        configuration.AddInMemoryCollection(
                                                                            new Dictionary<string, string?>
                                                                            {
                                                                                [key] = value
                                                                            }
                                                                        )
            )
        );

        var error = Assert.Throws<OptionsValidationException>(() => factory.CreateBrowserClient());
        Assert.That(error!.Message, Does.Not.Contain(FakeOidcProvider.ClientSecret));
    }

    [Test]
    public async Task LocalAdminAndJwtConfigurationAreNotRequired()
    {
        using var factory = new CustomWebApplicationFactory(
            configure: builder => builder.ConfigureAppConfiguration((_, configuration) =>
                                                                        configuration.AddInMemoryCollection(
                                                                            new Dictionary<string, string?>
                                                                            {
                                                                                ["Admin:Username"] = null,
                                                                                ["Admin:Password"] = null,
                                                                                ["Jwt:SigningKey"] = null
                                                                            }
                                                                        )
            )
        );

        using var client = factory.CreateBrowserClient();
        await OidcBrowser.SignInAsync(factory, client);
    }
}
