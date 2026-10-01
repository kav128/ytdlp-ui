using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Ytdlp.Ui.Infrastructure.Mocking;

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
        group.MapPost("/login", (LoginRequest request, IConfiguration configuration, DemoSessions sessions, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (request.Username != configuration["Admin:Username"] || request.Password != configuration["Admin:Password"])
                return Results.Unauthorized();
            return Results.Ok(DemoAuthentication.IssueToken(configuration, sessions));
        });
        group.MapPost("/logout", (ClaimsPrincipal user, DemoSessions sessions, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            sessions.Revoke(user.FindFirstValue(JwtRegisteredClaimNames.Jti)!);
            return Results.NoContent();
        }).RequireAuthorization("Admin");
        return endpoints;
    }

    public sealed record LoginRequest(string Username, string Password);
}
