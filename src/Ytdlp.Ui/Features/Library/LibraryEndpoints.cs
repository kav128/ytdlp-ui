using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using Ytdlp.Ui.Infrastructure.Authentication;
using Ytdlp.Ui.Infrastructure.Storage;

namespace Ytdlp.Ui.Features.Library;

public static class LibraryEndpoints
{
    public static IEndpointRouteBuilder MapLibraryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/library");

        group.AddEndpointFilter(async (context, next) =>
            {
                try
                {
                    return await next(context);
                }
                catch (StorageUnavailableException)
                {
                    return Error(503, "storage_unavailable");
                }
                catch (StorageConflictException)
                {
                    return Error(409, "publication_changed");
                }
            }
        );

        group.MapGet(
            "/",
            async (LibraryTitles titles, CancellationToken cancellationToken) =>
                await titles.ListAsync(cancellationToken)
        );

        group.MapGet("/{id}/content", ContentAsync);

        group.MapPut("/{id}/title", SetTitleAsync)
             .RequireAuthorization("Admin")
             .AddEndpointFilter<AdminAntiforgeryFilter>();

        group.MapDelete(
                  "/{id}",
                  async (string id, LibraryDeletionService deletion, CancellationToken cancellationToken) =>
                  {
                      if (!LibraryFile.TryDecode(id, out var key)) return Results.NotFound();

                      var result = await deletion.DeleteAsync(key, cancellationToken);

                      return result.DownloadId is { } downloadId
                                 ? Results.Accepted($"/api/downloads/{downloadId}", new { downloadId })
                                 : Results.NoContent();
                  }
              )
             .RequireAuthorization("Admin")
             .AddEndpointFilter<AdminAntiforgeryFilter>();

        group.MapPost("/uploads", UploadAsync)
             .RequireAuthorization("Admin")
             .AddEndpointFilter<AdminAntiforgeryFilter>();

        return endpoints;
    }

    private static async Task<IResult> SetTitleAsync(
        string id,
        SetTitleRequest request,
        LibraryTitles titles,
        CancellationToken cancellationToken
    )
    {
        if (!LibraryFile.TryDecode(id, out var key)) return Results.NotFound();

        var title = request.Title?.Trim();

        if (title is { Length: > 255 } || request.Title?.Any(char.IsControl) == true)
            return Error(400, "invalid_title");

        return await titles.SetAsync(key, string.IsNullOrEmpty(title) ? null : title, cancellationToken)
                   ? Results.NoContent()
                   : Error(404, "not_found");
    }

    private sealed record SetTitleRequest([property: JsonRequired] string? Title);

    private static async Task<IResult> ContentAsync(string id, HttpContext context, ILibraryStorage storage)
    {
        if (!LibraryFile.TryDecode(id, out var key)) return Results.NotFound();

        var content = await storage.OpenAsync(key, context.RequestAborted);

        if (content is null) return Results.NotFound();

        context.Response.RegisterForDispose(content);
        context.Response.ContentLength = content.Length;
        context.Response.Headers.XContentTypeOptions = "nosniff";

        return Results.Stream(content.Stream, "application/octet-stream", LibraryFile.SafeName(key));
    }

    private static async Task<IResult> UploadAsync(HttpContext context, ILibraryStorage storage)
    {
        var maximum = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (maximum is { IsReadOnly: false }) maximum.MaxRequestBodySize = null;

        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var mediaType)
         || !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            return Error(400, "invalid_upload");

        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;

        if (string.IsNullOrWhiteSpace(boundary) || boundary.Length > 128) return Error(400, "invalid_upload");

        try
        {
            var reader = new MultipartReader(boundary, context.Request.Body);
            var section = await ReadSectionAsync(reader, context.RequestAborted);

            if (section is null
             || !ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
             || !disposition.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase)
             || HeaderUtilities.RemoveQuotes(disposition.Name).Value != "file"
             || (!disposition.FileName.HasValue && !disposition.FileNameStar.HasValue))
                return Error(400, "invalid_upload");

            var name = HeaderUtilities.RemoveQuotes(
                                           disposition.FileNameStar.HasValue
                                               ? disposition.FileNameStar
                                               : disposition.FileName
                                       )
                                      .Value!;

            await using var upload = await storage.BeginUploadAsync(
                                         name,
                                         context.Request.ContentLength,
                                         context.RequestAborted
                                     );

            await upload.WriteAsync(section.Body, context.RequestAborted);

            if (await ReadSectionAsync(reader, context.RequestAborted) is not null) return Error(400, "invalid_upload");

            var file = await upload.CompleteAsync(context.RequestAborted);

            return Results.Created(file.ContentUrl, file);
        }
        catch (InvalidDataException)
        {
            return Error(400, "invalid_upload");
        }
        catch (BadHttpRequestException)
        {
            return Error(400, "invalid_upload");
        }
    }

    private static IResult Error(int status, string code) => Results.Problem(
        statusCode: status,
        extensions: new Dictionary<string, object?> { ["code"] = code }
    );

    private static async Task<MultipartSection?> ReadSectionAsync(
        MultipartReader reader,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await reader.ReadNextSectionAsync(cancellationToken);
        }
        catch (IOException error)
        {
            throw new InvalidDataException("The multipart form is incomplete.", error);
        }
    }
}
