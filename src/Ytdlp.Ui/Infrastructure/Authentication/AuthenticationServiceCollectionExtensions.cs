using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Ytdlp.Ui.Configuration;

namespace Ytdlp.Ui.Infrastructure.Authentication;

public static class AuthenticationServiceCollectionExtensions
{
    public const string CookieScheme = "AdminSession";
    public const string OidcScheme = "Authentik";
    public const string CookieName = "ytdlp-ui.session";

    public static IServiceCollection AddAdminAuthentication(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        services.AddOptions<OidcOptions>()
                .Bind(configuration.GetSection("Oidc"))
                .Validate(
                     settings => Uri.TryCreate(settings.Authority, UriKind.Absolute, out var uri)
                              && uri.Scheme == "https"
                              && uri.UserInfo.Length == 0
                              && uri.Query.Length == 0
                              && uri.Fragment.Length == 0,
                     "Oidc:Authority must be an absolute HTTPS issuer URL."
                 )
                .Validate(settings => !string.IsNullOrWhiteSpace(settings.ClientId), "Oidc:ClientId is required.")
                .Validate(
                     settings => !string.IsNullOrWhiteSpace(settings.ClientSecret),
                     "Oidc:ClientSecret is required."
                 )
                .Validate(settings => !string.IsNullOrWhiteSpace(settings.GroupsClaim), "Oidc:GroupsClaim is required.")
                .Validate(settings => !string.IsNullOrWhiteSpace(settings.AdminGroup), "Oidc:AdminGroup is required.")
                .ValidateOnStart();

        services.AddOptions<PathsOptions>()
                .Bind(configuration.GetSection("Paths"))
                .Validate(
                     settings => !string.IsNullOrWhiteSpace(settings.DataProtectionKeysDirectory),
                     "Paths:DataProtectionKeysDirectory is required."
                 )
                .ValidateOnStart();

        services.AddDataProtection().SetApplicationName("Ytdlp.Ui");

        services.AddOptions<KeyManagementOptions>()
                .Configure<IOptions<PathsOptions>, ILoggerFactory>((keys, paths, logging) =>
                                                                       keys.XmlRepository = new FileSystemXmlRepository(
                                                                           new DirectoryInfo(
                                                                               Path.GetFullPath(
                                                                                   paths.Value
                                                                                        .DataProtectionKeysDirectory
                                                                               )
                                                                           ),
                                                                           logging
                                                                       )
                 );

        services.AddSingleton<DatabaseTicketStore>();

        services.AddAntiforgery(options =>
            {
                options.HeaderName = "X-CSRF-TOKEN";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
            }
        );

        services.AddAuthentication(options =>
                     {
                         options.DefaultScheme = CookieScheme;
                         options.DefaultChallengeScheme = CookieScheme;
                     }
                 )
                .AddCookie(CookieScheme)
                .AddOpenIdConnect(OidcScheme, _ => { });

        services.AddOptions<CookieAuthenticationOptions>(CookieScheme)
                .Configure<DatabaseTicketStore, TimeProvider, IHostEnvironment>((options, store, clock, environment) =>
                     {
                         options.SessionStore = store;
                         options.TimeProvider = clock;
                         options.Cookie.Name = CookieName;
                         options.Cookie.HttpOnly = true;
                         options.Cookie.SameSite = SameSiteMode.Lax;

                         options.Cookie.SecurePolicy = environment.IsDevelopment()
                                                           ? CookieSecurePolicy.SameAsRequest
                                                           : CookieSecurePolicy.Always;

                         options.ExpireTimeSpan = TimeSpan.FromHours(12);
                         options.SlidingExpiration = false;

                         options.Events.OnRedirectToLogin = context => WriteAccessProblem(
                             context.HttpContext,
                             401,
                             "unauthorized"
                         );

                         options.Events.OnRedirectToAccessDenied = context =>
                             WriteAccessProblem(context.HttpContext, 403, "forbidden");

                         options.Events.OnSigningIn = context =>
                         {
                             context.Properties.IsPersistent = false;
                             context.Properties.AllowRefresh = false;
                             context.Properties.IssuedUtc = clock.GetUtcNow();
                             context.Properties.ExpiresUtc = context.Properties.IssuedUtc.Value.AddHours(12);

                             return Task.CompletedTask;
                         };
                     }
                 );

        services.AddOptions<OpenIdConnectOptions>(OidcScheme)
                .Configure<IOptions<OidcOptions>>((options, configured) =>
                     {
                         var settings = configured.Value;
                         options.Authority = settings.Authority;
                         options.ClientId = settings.ClientId;
                         options.ClientSecret = settings.ClientSecret;
                         options.SignInScheme = CookieScheme;
                         options.ResponseType = OpenIdConnectResponseType.Code;
                         options.ResponseMode = OpenIdConnectResponseMode.Query;
                         options.UsePkce = true;
                         options.SaveTokens = false;
                         options.MapInboundClaims = false;
                         options.GetClaimsFromUserInfoEndpoint = true;
                         options.CallbackPath = "/signin-oidc";
                         options.RemoteSignOutPath = PathString.Empty;
                         options.SignedOutCallbackPath = PathString.Empty;
                         options.Scope.Clear();
                         options.Scope.Add("openid");
                         options.Scope.Add("profile");
                         options.ClaimActions.MapJsonKey(settings.GroupsClaim, settings.GroupsClaim);
                         options.TokenValidationParameters.ClockSkew = TimeSpan.Zero;

                         options.Events.OnTokenValidated = context =>
                         {
                             if (string.IsNullOrEmpty(context.SecurityToken.RawSignature)
                              || context.SecurityToken.Header.Alg == "none")
                             {
                                 context.Fail("A signed ID token is required.");

                                 return Task.CompletedTask;
                             }

                             var identity = (ClaimsIdentity)context.Principal!.Identity!;

                             foreach (var claim in identity.FindAll("oidc_issuer").ToArray())
                                 identity.RemoveClaim(claim);

                             identity.AddClaim(new Claim("oidc_issuer", context.SecurityToken.Issuer));

                             return Task.CompletedTask;
                         };

                         options.Events.OnUserInformationReceived = context =>
                         {
                             if (!context.User.RootElement.TryGetProperty("sub", out var subject)
                              || subject.GetString() != context.Principal!.FindFirst("sub")?.Value)
                                 context.Fail("User information does not match the verified identity.");

                             return Task.CompletedTask;
                         };

                         options.Events.OnTicketReceived = context =>
                         {
                             if (!context.Principal!.HasClaim(settings.GroupsClaim, settings.AdminGroup))
                             {
                                 context.HandleResponse();
                                 context.Response.Redirect("/?authError=access_denied");

                                 return Task.CompletedTask;
                             }

                             var identity = (ClaimsIdentity)context.Principal.Identity!;

                             foreach (var claim in identity.FindAll("ui_session_id").ToArray())
                                 identity.RemoveClaim(claim);

                             identity.AddClaim(new Claim("ui_session_id", Guid.NewGuid().ToString("N")));
                             identity.AddClaim(new Claim(ClaimTypes.Role, "Admin"));

                             return Task.CompletedTask;
                         };

                         options.Events.OnRemoteFailure = context =>
                         {
                             context.HandleResponse();
                             context.Response.Redirect("/?authError=login_failed");

                             return Task.CompletedTask;
                         };
                     }
                 );

        services.AddAuthorizationBuilder()
                .AddPolicy("Admin", policy => policy.RequireAuthenticatedUser().RequireRole("Admin"));

        return services;
    }

    private static Task WriteAccessProblem(HttpContext context, int status, string code) =>
        Results.Problem(statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code })
               .ExecuteAsync(context);
}
