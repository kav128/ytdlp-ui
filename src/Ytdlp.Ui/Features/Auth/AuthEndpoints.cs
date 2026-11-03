using AdminAuthentication = Ytdlp.Ui.Infrastructure.Authentication.AuthenticationServiceCollectionExtensions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Ytdlp.Ui.Infrastructure.Authentication;

namespace Ytdlp.Ui.Features.Auth;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/auth");

        group.AddEndpointFilter(async (context, next) =>
            {
                context.HttpContext.Response.Headers.CacheControl = "no-store";

                return await next(context);
            }
        );

        group.MapGet(
            "/session",
            (HttpContext context) => Results.Ok(
                new
                {
                    authenticated = context.User.IsInRole("Admin"),
                    sessionId = context.User.IsInRole("Admin")
                                    ? context.User.FindFirst("ui_session_id")?.Value
                                    : null
                }
            )
        );

        group.MapGet(
            "/login",
            (string? returnUrl) =>
            {
                var target = returnUrl ?? "/";

                if (!IsLocalPath(target)) return Results.BadRequest();

                return Results.Challenge(
                    new AuthenticationProperties { RedirectUri = target },
                    [AdminAuthentication.OidcScheme]
                );
            }
        );

        group.MapGet(
                  "/csrf",
                  (IAntiforgery antiforgery, HttpContext context) =>
                      Results.Ok(new { requestToken = antiforgery.GetAndStoreTokens(context).RequestToken })
              )
             .RequireAuthorization("Admin");

        group.MapPost(
                  "/logout",
                  async (HttpContext context) =>
                  {
                      await context.SignOutAsync(AdminAuthentication.CookieScheme);

                      return Results.NoContent();
                  }
              )
             .RequireAuthorization("Admin")
             .AddEndpointFilter<AdminAntiforgeryFilter>();

        return endpoints;
    }

    private static bool IsLocalPath(string value) => value.StartsWith('/')
                                                  && !value.StartsWith("//")
                                                  && !value.StartsWith("/\\")
                                                  && !value.Any(char.IsControl)
                                                  && !value.Contains('\\');
}
