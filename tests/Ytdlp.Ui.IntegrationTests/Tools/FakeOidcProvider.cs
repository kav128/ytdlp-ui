using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.Tokens;

namespace Ytdlp.Ui.IntegrationTests.Tools;

public sealed class FakeOidcProvider : HttpMessageHandler
{
    public const string Authority = "https://auth.test/application/o/ytdlp-ui/";
    public const string ClientId = "test-client";
    public const string ClientSecret = "test-client-secret";
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly ConcurrentDictionary<string, Authorization> _codes = new();
    private readonly ConcurrentDictionary<string, string> _accessTokens = new();
    public string[] Groups { get; set; } = ["admins"];
    public string? Defect { get; set; }
    public string? LastCodeVerifier { get; private set; }
    public string? LastCodeChallenge { get; private set; }
    public int TokenRequests { get; private set; }
    public int UserInfoRequests { get; private set; }

    public Uri Authorize(Uri challenge)
    {
        var query = QueryHelpers.ParseQuery(challenge.Query);
        Assert.That(query["response_type"].ToString(), Is.EqualTo("code"));
        Assert.That(query["client_id"].ToString(), Is.EqualTo(ClientId));
        Assert.That(query["code_challenge_method"].ToString(), Is.EqualTo("S256"));
        var redirect = query["redirect_uri"].ToString();
        Assert.That(redirect, Is.EqualTo("https://localhost/signin-oidc"));
        LastCodeChallenge = query["code_challenge"].ToString();
        Assert.That(LastCodeChallenge, Is.Not.Empty);
        var code = Guid.NewGuid().ToString("N");

        _codes[code] = new(
            query["nonce"].ToString(),
            Defect == "pkce" ? "wrong-challenge" : LastCodeChallenge,
            redirect
        );

        var state = Defect == "state" ? "invalid-state" : query["state"].ToString();

        return new Uri(
            QueryHelpers.AddQueryString(
                redirect,
                new Dictionary<string, string?>
                {
                    ["code"] = code,
                    ["state"] = Defect == "missing_state" ? null : state
                }
            )
        );
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        if (request.RequestUri!.AbsoluteUri == Authority + ".well-known/openid-configuration")
            return Json(
                new
                {
                    issuer = Authority,
                    authorization_endpoint = Authority + "authorize",
                    token_endpoint = Authority + "token",
                    userinfo_endpoint = Authority + "userinfo",
                    jwks_uri = Authority + "jwks",
                    response_types_supported = new[] { "code" },
                    subject_types_supported = new[] { "public" },
                    id_token_signing_alg_values_supported = new[] { "RS256" },
                    code_challenge_methods_supported = new[] { "S256" }
                }
            );

        if (request.RequestUri.AbsoluteUri == Authority + "jwks")
        {
            var key = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(_rsa) { KeyId = "test-key" });

            return Json(
                new
                {
                    keys = new[]
                    {
                        new
                        {
                            kty = key.Kty,
                            kid = key.Kid,
                            n = key.N,
                            e = key.E,
                            use = "sig",
                            alg = "RS256"
                        }
                    }
                }
            );
        }

        if (request.RequestUri.AbsoluteUri == Authority + "token")
        {
            TokenRequests++;
            var form = QueryHelpers.ParseQuery("?" + await request.Content!.ReadAsStringAsync(cancellationToken));
            LastCodeVerifier = form["code_verifier"].ToString();

            if (request.Method != HttpMethod.Post
             || form["grant_type"] != "authorization_code"
             || form["client_id"] != ClientId
             || form["client_secret"] != ClientSecret
             || !_codes.TryRemove(form["code"].ToString(), out var authorization)
             || form["redirect_uri"] != authorization.RedirectUri
             || WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(LastCodeVerifier)))
             != authorization.Challenge)
                return Json(new { error = "invalid_grant" }, HttpStatusCode.BadRequest);

            var access = Guid.NewGuid().ToString("N");
            _accessTokens[access] = "oidc-user";
            var now = DateTime.UtcNow;

            var claims = new List<Claim>
            {
                new("sub", "oidc-user"),
                new(
                    "iat",
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                                  .ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ClaimValueTypes.Integer64
                ),
                new("nonce", Defect == "nonce" ? "wrong-nonce" : authorization.Nonce)
            };

            if (Defect == "missing_nonce") claims.RemoveAll(claim => claim.Type == "nonce");
            using var otherRsa = Defect == "signature" ? RSA.Create(2048) : null;
            var signing = new RsaSecurityKey(otherRsa ?? _rsa) { KeyId = "test-key" };

            var token = new JwtSecurityToken(
                Defect == "issuer" ? "https://wrong.test/" : Authority,
                Defect == "audience" ? "wrong-client" : ClientId,
                claims,
                now.AddMinutes(-10),
                Defect == "expired" ? now.AddSeconds(-1) : now.AddMinutes(5),
                Defect == "unsigned" ? null : new SigningCredentials(signing, SecurityAlgorithms.RsaSha256)
            );

            return Json(
                new
                {
                    access_token = access,
                    token_type = "Bearer",
                    expires_in = 300,
                    id_token = new JwtSecurityTokenHandler().WriteToken(token)
                }
            );
        }

        if (request.RequestUri.AbsoluteUri == Authority + "userinfo")
        {
            UserInfoRequests++;

            if (request.Headers.Authorization?.Scheme != "Bearer"
             || !_accessTokens.ContainsKey(request.Headers.Authorization.Parameter ?? ""))
                return Json(new { error = "invalid_token" }, HttpStatusCode.Unauthorized);

            var body = new Dictionary<string, object>
            {
                ["sub"] = Defect == "userinfo_sub" ? "different-user" : "oidc-user",
                ["name"] = "Test administrator"
            };

            if (Defect != "missing_groups") body["groups"] = Groups;

            return Json(body);
        }

        throw new InvalidOperationException("Unexpected OIDC backchannel request.");
    }

    private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = JsonContent.Create(body) };

    protected override void Dispose(bool disposing)
    {
        if (disposing) _rsa.Dispose();
        base.Dispose(disposing);
    }

    private sealed record Authorization(string Nonce, string Challenge, string RedirectUri);
}
