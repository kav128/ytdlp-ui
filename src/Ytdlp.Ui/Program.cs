using Ytdlp.Ui.Features.Hello;
using Ytdlp.Ui.Features.Auth;
using Ytdlp.Ui.Features.Downloads;
using Ytdlp.Ui.Features.Library;
using Ytdlp.Ui.Infrastructure.Persistence;
using Ytdlp.Ui.Infrastructure.Authentication;
using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Ytdlp.Ui.Configuration;
using Ytdlp.Ui.Infrastructure.Storage;
using Ytdlp.Ui.Infrastructure.Media;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();

builder.Services.AddOptions<ReverseProxyOptions>()
       .Bind(builder.Configuration.GetSection("ReverseProxy"))
       .Validate(
            settings => settings.KnownProxies.All(value => IPAddress.TryParse(value, out _)),
            "ReverseProxy:KnownProxies must contain IP addresses."
        )
       .Validate(
            settings => settings.KnownNetworks.All(value => System.Net.IPNetwork.TryParse(value, out _)),
            "ReverseProxy:KnownNetworks must contain CIDR networks."
        )
       .ValidateOnStart();

builder.Services.AddOptions<ForwardedHeadersOptions>()
       .Configure<IOptions<ReverseProxyOptions>>((options, configured) =>
            {
                var settings = configured.Value;
                options.KnownProxies.Clear();
                options.KnownIPNetworks.Clear();
                foreach (var proxy in settings.KnownProxies) options.KnownProxies.Add(IPAddress.Parse(proxy));

                foreach (var network in settings.KnownNetworks)
                    options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));

                if (settings.KnownProxies.Length > 0 || settings.KnownNetworks.Length > 0)
                    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
                                             | ForwardedHeaders.XForwardedProto
                                             | ForwardedHeaders.XForwardedHost;

                options.AllowedHosts = settings.AllowedHosts;
            }
        );

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddAppDatabase(builder.Configuration);
builder.Services.AddAdminAuthentication(builder.Configuration);
builder.Services.AddLibraryStorage(builder.Configuration);
builder.Services.AddScoped<DownloadQueries>();
builder.Services.AddScoped<DownloadCommands>();
builder.Services.AddScoped<DownloadQueue>();
builder.Services.AddScoped<StartupRecovery>();
builder.Services.AddScoped<DownloadRetryPlanner>();
builder.Services.AddMediaProcessing(builder.Configuration);

var app = builder.Build();

app.UseExceptionHandler();
app.UseForwardedHeaders();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapHelloEndpoints();
app.MapAuthEndpoints();
app.MapDownloadEndpoints();
app.MapLibraryEndpoints();
app.MapFallback("/api/{**path}", () => Results.NotFound());
app.MapFallbackToFile("index.html");

await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().InitializeAsync();
    await scope.ServiceProvider.GetRequiredService<StartupRecovery>().RecoverAsync();
}

await app.RunAsync();
