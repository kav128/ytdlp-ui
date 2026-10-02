using Ytdlp.Ui.Features.Hello;
using Ytdlp.Ui.Features.Auth;
using Ytdlp.Ui.Features.Downloads;
using Ytdlp.Ui.Features.Library;
using Ytdlp.Ui.Infrastructure.Mocking;
using Ytdlp.Ui.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAppDatabase(builder.Configuration);
builder.Services.AddDemoBackend(builder.Configuration);
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

app.Run();

public partial class Program;
