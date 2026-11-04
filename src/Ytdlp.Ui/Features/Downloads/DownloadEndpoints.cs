using Ytdlp.Ui.Infrastructure.Authentication;
using Ytdlp.Ui.Infrastructure.Storage;

namespace Ytdlp.Ui.Features.Downloads;

public static class DownloadEndpoints
{
    public static IEndpointRouteBuilder MapDownloadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/downloads")
                             .RequireAuthorization("Admin")
                             .AddEndpointFilter<AdminAntiforgeryFilter>();

        group.AddEndpointFilter(async (context, next) =>
            {
                try
                {
                    return await next(context);
                }
                catch (StorageUnavailableException)
                {
                    return Results.Problem(
                        statusCode: 503,
                        extensions: new Dictionary<string, object?> { ["code"] = "storage_unavailable" }
                    );
                }
                catch (StorageConflictException)
                {
                    return Results.Problem(
                        statusCode: 409,
                        extensions: new Dictionary<string, object?> { ["code"] = "publication_changed" }
                    );
                }
            }
        );

        group.MapGet(
            "/",
            async (DownloadQueries queries, CancellationToken cancellationToken) =>
                await queries.ListAsync(cancellationToken)
        );

        group.MapGet(
            "/{id:guid}",
            async (Guid id, DownloadQueries queries, CancellationToken cancellationToken) =>
                await queries.GetAsync(id, cancellationToken) is { } download
                    ? Results.Ok(download)
                    : Results.NotFound()
        );

        group.MapPost(
            "/",
            async (CreateRequest request, DownloadCommands commands, CancellationToken cancellationToken) =>
                ToResponse(await commands.CreateAsync(request.Url, cancellationToken))
        );

        group.MapPost(
            "/{id:guid}/retry",
            async (Guid id, DownloadCommands commands, CancellationToken cancellationToken) =>
                ToResponse(await commands.RetryAsync(id, cancellationToken))
        );

        group.MapPost(
            "/{id:guid}/cancel",
            async (Guid id, DownloadCommands commands, CancellationToken cancellationToken) =>
                ToResponse(await commands.CancelAsync(id, cancellationToken))
        );

        return endpoints;
    }

    public static IResult ToResponse(DownloadCommandResult result) => result.Code is { } code
                                                                          ? Results.Problem(
                                                                              statusCode: result.Status,
                                                                              extensions: new
                                                                                  Dictionary<string, object?>
                                                                                  {
                                                                                      ["code"] = code,
                                                                                      ["existingDownloadId"] =
                                                                                          result.ExistingDownloadId
                                                                                  }
                                                                          )
                                                                          : result.Status == 204
                                                                              ? Results.NoContent()
                                                                              : result.Status == 201
                                                                                  ? Results.Created(
                                                                                      $"/api/downloads/{result.Download!.Id}",
                                                                                      result.Download
                                                                                  )
                                                                                  : result.Status == 202
                                                                                      ? Results.Accepted(
                                                                                          $"/api/downloads/{result.Download!.Id}",
                                                                                          result.Download
                                                                                      )
                                                                                      : Results.Json(
                                                                                          result.Download,
                                                                                          statusCode: result.Status
                                                                                      );

    public sealed record CreateRequest(string? Url);
}
