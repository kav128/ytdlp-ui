using Ytdlp.Ui.Infrastructure.Mocking;

namespace Ytdlp.Ui.Features.Downloads;

public static class DownloadEndpoints
{
    public static IEndpointRouteBuilder MapDownloadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/downloads").RequireAuthorization("Admin");
        group.MapGet("/", (DemoStore store) => store.GetDownloads());
        group.MapGet("/{id:guid}", (Guid id, DemoStore store) => store.GetDownload(id) is { } download ? Results.Ok(download) : Results.NotFound());
        group.MapPost("/", (CreateRequest request, DemoStore store) => ToResponse(store.Create(request.Url)));
        group.MapPost("/{id:guid}/retry", (Guid id, DemoStore store) => ToResponse(store.Retry(id)));
        group.MapPost("/{id:guid}/cancel", (Guid id, DemoStore store) => ToResponse(store.Cancel(id)));
        return endpoints;
    }

    public static IResult ToResponse(DemoResult result) => result.Code is { } code
        ? Results.Problem(statusCode: result.Status, extensions: new Dictionary<string, object?> { ["code"] = code })
        : result.Status == 204 ? Results.NoContent()
        : result.Status == 201 ? Results.Created($"/api/downloads/{result.Download!.Id}", result.Download)
        : Results.Json(result.Download, statusCode: result.Status);

    public sealed record CreateRequest(string? Url);
}
