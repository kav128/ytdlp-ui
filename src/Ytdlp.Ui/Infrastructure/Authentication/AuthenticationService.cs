using System.Buffers.Text;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Ytdlp.Ui.Features.Auth;
using Ytdlp.Ui.Infrastructure.Persistence;

namespace Ytdlp.Ui.Infrastructure.Authentication;

public sealed class AuthenticationService(
    IOptions<AdminOptions> adminOptions,
    IOptions<JwtOptions> jwtOptions,
    AppDbContext database,
    TimeProvider timeProvider) : IDisposable
{
    private readonly HashAlgorithm _hashAlgorithm = SHA256.Create();

    private int RefreshTokenSizeBytes => _hashAlgorithm.HashSize / 8;

    public async Task<AuthTokens?> LoginAsync(string username, string password, CancellationToken cancellationToken)
    {
        if (username != adminOptions.Value.Username || password != adminOptions.Value.Password)
            return null;

        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(timeProvider.GetUtcNow().ToUnixTimeSeconds());
        var expiresAt = issuedAt.AddHours(12);
        var id = Guid.NewGuid().ToString();
        var tokens = CreateTokens(id, expiresAt);

        database.AdminSessions.Add(new()
        {
            Id = id,
            IssuedAt = issuedAt,
            ExpiresAt = expiresAt,
            RevokedAt = null,
            RefreshTokenHash = HashRefreshToken(tokens.RefreshToken)
        });

        await database.SaveChangesAsync(cancellationToken);

        return tokens;
    }

    public async Task<AuthTokens?> RefreshAsync(string? refreshToken, CancellationToken cancellationToken)
    {
        if (refreshToken is null || refreshToken.Length != Base64.GetMaxEncodedToUtf8Length(RefreshTokenSizeBytes))
            return null;

        Span<byte> decoded = stackalloc byte[RefreshTokenSizeBytes];
        if (!Convert.TryFromBase64String(refreshToken, decoded, out var bytesWritten) || bytesWritten != decoded.Length)
            return null;

        var oldHash = HashRefreshToken(refreshToken);
        var now = timeProvider.GetUtcNow();
        var session = await database.AdminSessions
            .AsNoTracking()
            .SingleOrDefaultAsync(record =>
                record.RefreshTokenHash == oldHash &&
                record.RevokedAt == null &&
                record.ExpiresAt > now,
                cancellationToken);

        if (session is null)
            return null;

        var tokens = CreateTokens(session.Id, session.ExpiresAt);
        var newHash = HashRefreshToken(tokens.RefreshToken);

        now = timeProvider.GetUtcNow();
        var updated = await database.AdminSessions
            .Where(record =>
                record.Id == session.Id &&
                record.RefreshTokenHash == oldHash &&
                record.RevokedAt == null &&
                record.ExpiresAt > now)
            .ExecuteUpdateAsync(
                update => update.SetProperty(record => record.RefreshTokenHash, newHash),
                cancellationToken);

        return updated == 1 ? tokens : null;
    }

    public Task<bool> IsSessionActiveAsync(string? id, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        return database.AdminSessions.AnyAsync(session =>
            session.Id == id &&
            session.RefreshTokenHash != null &&
            session.RevokedAt == null &&
            session.ExpiresAt > now,
            cancellationToken);
    }

    public async Task RevokeAsync(string id, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await database.AdminSessions
            .Where(session => session.Id == id && session.RevokedAt == null)
            .ExecuteUpdateAsync(
                update => update.SetProperty(session => session.RevokedAt, now),
                cancellationToken);
    }

    public void Dispose() => _hashAlgorithm.Dispose();

    private byte[] HashRefreshToken(string refreshToken) =>
        _hashAlgorithm.ComputeHash(Encoding.UTF8.GetBytes(refreshToken));

    private AuthTokens CreateTokens(string id, DateTimeOffset sessionExpiresAt)
    {
        var expiresAt = timeProvider.GetUtcNow().AddMinutes(5);

        if (expiresAt > sessionExpiresAt)
            expiresAt = sessionExpiresAt;

        expiresAt = DateTimeOffset.FromUnixTimeSeconds(expiresAt.ToUnixTimeSeconds());

        var token = new JwtSecurityToken(
            issuer: jwtOptions.Value.Issuer,
            audience: jwtOptions.Value.Audience,
            claims:
            [
                new(JwtRegisteredClaimNames.Sub, "admin"),
                new(JwtRegisteredClaimNames.Jti, id),
                new(ClaimTypes.Role, "Admin")
            ],
            expires: expiresAt.UtcDateTime,
            signingCredentials: new(
                new SymmetricSecurityKey(jwtOptions.Value.GetSigningKeyBytes()),
                SecurityAlgorithms.HmacSha256));

        return new(
            new JwtSecurityTokenHandler().WriteToken(token),
            "Bearer",
            expiresAt,
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(RefreshTokenSizeBytes)));
    }
}
