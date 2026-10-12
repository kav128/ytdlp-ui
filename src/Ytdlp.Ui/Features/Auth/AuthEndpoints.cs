using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Ytdlp.Ui.Infrastructure.Authentication;

namespace Ytdlp.Ui.Features.Auth;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/auth");

        group.MapGet("/session", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (context.Request.Headers.ContainsKey("Authorization") && context.User.Identity?.IsAuthenticated != true)
                return Results.Unauthorized();
            return Results.Ok(new { authenticated = context.User.IsInRole("Admin") });
        });

        group.MapPost("/login", async (LoginRequest request, AuthenticationService authentication, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var tokens = await authentication.LoginAsync(request.Username, request.Password, context.RequestAborted);
            return tokens is null ? Results.Unauthorized() : Results.Ok(tokens);
        });

        group.MapPost("/refresh", async (RefreshRequest request, AuthenticationService authentication, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var tokens = await authentication.RefreshAsync(request.RefreshToken, context.RequestAborted);
            return tokens is null ? Results.Unauthorized() : Results.Ok(tokens);
        });

        group.MapPost("/logout", async (ClaimsPrincipal user, AuthenticationService authentication, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var id = user.FindFirstValue(JwtRegisteredClaimNames.Jti)!;
            await authentication.RevokeAsync(id, context.RequestAborted);
            return Results.NoContent();
        }).RequireAuthorization("Admin");

        return endpoints;
    }
}
