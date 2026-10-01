using Ytdlp.Ui.Features.Downloads;
using Ytdlp.Ui.Infrastructure.Mocking;

namespace Ytdlp.Ui.Features.Library;

public static class LibraryEndpoints
{
    public static IEndpointRouteBuilder MapLibraryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/library");
        group.MapGet("/", (DemoStore store) => store.GetFiles());
        group.MapGet("/{id}/content", (string id, DemoStore store) =>
            store.GetFile(id) is { } file && store.GetContent(id) is { } content
                ? Results.Stream(new MemoryStream(content, writable: false), "text/plain; charset=utf-8", file.Name)
                : Results.NotFound());
        group.MapDelete("/{id}", (string id, DemoStore store) => DownloadEndpoints.ToResponse(store.DeleteFile(id))).RequireAuthorization("Admin");
        return endpoints;
    }
}
