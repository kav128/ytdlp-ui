using Ytdlp.Ui.Features.Hello;
using Ytdlp.Ui.Features.Auth;
using Ytdlp.Ui.Features.Downloads;
using Ytdlp.Ui.Features.Library;
using Ytdlp.Ui.Infrastructure.Mocking;
using Ytdlp.Ui.Infrastructure.Persistence;
using Ytdlp.Ui.Infrastructure.Authentication;

var builder = WebApplication.CreateBuilder(args);


builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddAppDatabase(builder.Configuration);
builder.Services.AddAdminAuthentication(builder.Configuration);
builder.Services.AddSingleton<DemoStore>();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
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
}

await app.RunAsync();
