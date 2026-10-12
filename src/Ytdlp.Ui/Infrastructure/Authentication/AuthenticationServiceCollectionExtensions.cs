using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Ytdlp.Ui.Infrastructure.Authentication;

public static class AuthenticationServiceCollectionExtensions
{
    public static IServiceCollection AddAdminAuthentication(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        services.AddOptions<AdminOptions>()
                .Bind(configuration.GetSection("Admin"));

        services.AddScoped<AuthenticationService>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer();

        services.AddOptions<JwtOptions>()
                .Bind(configuration.GetSection("Jwt"))
                .Validate(
                     settings =>
                     {
                         try
                         {
                             return settings.GetSigningKeyBytes().Length >= 32;
                         }
                         catch (FormatException)
                         {
                             return false;
                         }
                     },
                     "Jwt:SigningKey must be valid Base64 encoding at least 32 bytes."
                 )
                .ValidateOnStart();

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
                .Configure<IOptions<JwtOptions>>((options, jwtOptions) =>
                     {
                         var settings = jwtOptions.Value;
                         var key = new SymmetricSecurityKey(settings.GetSigningKeyBytes());

                         options.TokenValidationParameters = new()
                         {
                             ValidateIssuer = true,
                             ValidIssuer = settings.Issuer,
                             ValidateAudience = true,
                             ValidAudience = settings.Audience,
                             ValidateIssuerSigningKey = true,
                             IssuerSigningKey = key,
                             ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                             ValidateLifetime = true,
                             ClockSkew = TimeSpan.Zero
                         };
                         options.Events = new()
                         {
                             OnTokenValidated = async context =>
                             {
                                 var id = context.Principal?.FindFirstValue(JwtRegisteredClaimNames.Jti);
                                 var authentication = context.HttpContext.RequestServices.GetRequiredService<AuthenticationService>();

                                 if (!await authentication.IsSessionActiveAsync(id, context.HttpContext.RequestAborted))
                                     context.Fail("Session is inactive.");
                             }
                         };
                     }
                 );

        services.AddAuthorizationBuilder()
                .AddPolicy("Admin", policy => policy.RequireRole("Admin"));

        return services;
    }
}
