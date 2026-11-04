using Ytdlp.Ui.Configuration;

namespace Ytdlp.Ui.Features.Health;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                      "/health/live",
                      (HttpContext context) =>
                      {
                          context.Response.Headers.CacheControl = "no-store";

                          return Results.Text("Healthy", "text/plain");
                      }
                  )
                 .AllowAnonymous();

        endpoints.MapGet("/health/ready", ReadyAsync).AllowAnonymous();
        endpoints.MapFallback("/health/{**path}", () => Results.NotFound());

        return endpoints;
    }

    public static void InitializePaths(PathsOptions paths)
    {
        Directory.CreateDirectory(paths.WorkDirectory);
        Directory.CreateDirectory(paths.DataProtectionKeysDirectory);
        ProbePaths(paths);
    }

    private static async Task<IResult> ReadyAsync(
        HttpContext context,
        ReadinessProbe probe
    )
    {
        context.Response.Headers.CacheControl = "no-store";

        return await probe.CheckAsync(context.RequestAborted)
                   ? Results.Text("Healthy", "text/plain")
                   : Unhealthy();
    }

    internal static void ProbePaths(PathsOptions paths)
    {
        foreach (var directory in new[]
                 {
                     paths.WorkDirectory,
                     paths.DataProtectionKeysDirectory
                 }.Distinct())
        {
            var path = Path.Combine(directory, ".readiness-" + Guid.NewGuid().ToString("N"));

            using var probe = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1,
                FileOptions.DeleteOnClose
            );

            probe.WriteByte(0);
        }
    }

    private static IResult Unhealthy() => Results.Text("Unhealthy", "text/plain", statusCode: 503);
}
