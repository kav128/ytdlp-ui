using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Ytdlp.Ui.Features.Library;
using Ytdlp.Ui.Infrastructure.Storage;

namespace Ytdlp.Ui.IntegrationTests.Tools;

public sealed class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    public FakeOidcProvider Provider { get; } = new();
    public ILibraryStorage Storage { get; } = Substitute.For<ILibraryStorage>();
    private readonly bool _ownsDatabase;
    private HttpClient? _backchannel;
    public TestDatabase Database { get; }
    private readonly Action<IWebHostBuilder>? _configure;

    public CustomWebApplicationFactory(TestDatabase? database = null, Action<IWebHostBuilder>? configure = null)
    {
        _ownsDatabase = database is null;
        Database = database ?? new TestDatabase();
        _configure = configure;
        Storage.ListAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(Array.Empty<LibraryFile>()));
        Storage.CheckReadyAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
    }

    public HttpClient CreateBrowserClient() => CreateClient(
        new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        }
    );

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseStaticWebAssets();

        builder.UseSetting(
            "ConnectionStrings:AppDatabase",
            Database.ConnectionString
        );

        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                                              new Dictionary<string, string?>
                                              {
                                                  ["Oidc:Authority"] = FakeOidcProvider.Authority,
                                                  ["Oidc:ClientId"] = FakeOidcProvider.ClientId,
                                                  ["Oidc:ClientSecret"] = FakeOidcProvider.ClientSecret,
                                                  ["Oidc:GroupsClaim"] = "groups",
                                                  ["Oidc:AdminGroup"] = "admins",
                                                  ["Worker:Enabled"] = "false",
                                                  ["Paths:WorkDirectory"] =
                                                      Path.Combine(Database.DirectoryPath, "work"),
                                                  ["Paths:DataProtectionKeysDirectory"] = Path.Combine(
                                                      Database.DirectoryPath,
                                                      "keys"
                                                  )
                                              }
                                          )
        );

        _backchannel = new HttpClient(Provider, disposeHandler: false);

        builder.ConfigureTestServices(services => services.Configure<OpenIdConnectOptions>(
                                          "Authentik",
                                          options => options.Backchannel = _backchannel
                                      )
        );

        builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ILibraryStorage>();
                services.AddSingleton(Storage);
            }
        );

        builder.ConfigureLogging(logging =>
            {
                if (OperatingSystem.IsWindows()) logging.AddFilter<EventLogLoggerProvider>(_ => false);
            }
        );

        _configure?.Invoke(builder);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            _backchannel?.Dispose();
            Provider.Dispose();
        }

        if (disposing && _ownsDatabase) Database.Dispose();
    }
}
