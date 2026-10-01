using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Ytdlp.Ui.Infrastructure.Mocking;

public sealed class DemoSessions
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> sessions = new();

    public string Create(DateTimeOffset expiresAt)
    {
        var id = Guid.NewGuid().ToString();
        sessions[id] = expiresAt;
        return id;
    }

    public bool IsActive(string? id) =>
        id is not null && sessions.TryGetValue(id, out var expiresAt) && expiresAt > DateTimeOffset.UtcNow;

    public void Revoke(string id) => sessions.TryRemove(id, out _);
}

public static class DemoAuthentication
{
    public static IServiceCollection AddDemoBackend(this IServiceCollection services, IConfiguration configuration)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(configuration["Jwt:SigningKey"]!));
        services.AddSingleton<DemoSessions>();
        services.AddSingleton<DemoStore>();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = configuration["Jwt:Issuer"],
                ValidateAudience = true,
                ValidAudience = configuration["Jwt:Audience"],
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = key,
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero
            };
            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = context =>
                {
                    var id = context.Principal?.FindFirstValue(JwtRegisteredClaimNames.Jti);
                    if (!context.HttpContext.RequestServices.GetRequiredService<DemoSessions>().IsActive(id))
                        context.Fail("Session is inactive.");
                    return Task.CompletedTask;
                }
            };
        });
        services.AddAuthorizationBuilder().AddPolicy("Admin", policy => policy.RequireRole("Admin"));
        return services;
    }

    public static object IssueToken(IConfiguration configuration, DemoSessions sessions)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddHours(12);
        var id = sessions.Create(expiresAt);
        var token = new JwtSecurityToken(
            issuer: configuration["Jwt:Issuer"],
            audience: configuration["Jwt:Audience"],
            claims: [new Claim(JwtRegisteredClaimNames.Sub, "admin"), new Claim(JwtRegisteredClaimNames.Jti, id), new Claim(ClaimTypes.Role, "Admin")],
            expires: expiresAt.UtcDateTime,
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(configuration["Jwt:SigningKey"]!)),
                SecurityAlgorithms.HmacSha256));
        return new { accessToken = new JwtSecurityTokenHandler().WriteToken(token), tokenType = "Bearer", expiresAt };
    }
}
