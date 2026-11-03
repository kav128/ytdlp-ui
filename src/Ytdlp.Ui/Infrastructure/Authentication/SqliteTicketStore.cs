using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Ytdlp.Ui.Infrastructure.Persistence;

namespace Ytdlp.Ui.Infrastructure.Authentication;

public sealed class SqliteTicketStore(
    IServiceScopeFactory scopes,
    IDataProtectionProvider protection,
    TimeProvider clock
) : ITicketStore
{
    private readonly IDataProtector _protector = protection.CreateProtector("Ytdlp.Ui.AdminSession.Ticket.v1");

    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var id = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var issuedAt = ticket.Properties.IssuedUtc ?? clock.GetUtcNow();
        var expiresAt = issuedAt.AddHours(12);
        ticket.Properties.IssuedUtc = issuedAt;
        ticket.Properties.ExpiresUtc = expiresAt;
        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        database.AdminSessions.Add(
            new()
            {
                Id = id,
                IssuedAt = issuedAt,
                ExpiresAt = expiresAt,
                Issuer =
                    ticket.Principal.FindFirst("oidc_issuer")?.Value
                 ?? throw new InvalidOperationException("Verified issuer is required."),
                Subject =
                    ticket.Principal.FindFirst("sub")?.Value
                 ?? throw new InvalidOperationException("Verified subject is required."),
                ProtectedTicket = _protector.Protect(TicketSerializer.Default.Serialize(ticket))
            }
        );

        await database.SaveChangesAsync();

        return id;
    }

    public async Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        var now = clock.GetUtcNow();
        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var record = await database.AdminSessions.AsNoTracking()
                                   .SingleOrDefaultAsync(session =>
                                                             session.Id == key
                                                          && session.RevokedAt == null
                                                          && session.ExpiresAt > now
                                                          && session.ProtectedTicket != null
                                    );

        if (record?.ProtectedTicket is null) return null;

        try
        {
            return TicketSerializer.Default.Deserialize(_protector.Unprotect(record.ProtectedTicket));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public async Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        var now = clock.GetUtcNow();
        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var record = await database.AdminSessions.AsNoTracking()
                                   .SingleOrDefaultAsync(session =>
                                                             session.Id == key
                                                          && session.RevokedAt == null
                                                          && session.ExpiresAt > now
                                    );

        if (record is null) return;

        ticket.Properties.IssuedUtc = record.IssuedAt;
        ticket.Properties.ExpiresUtc = record.ExpiresAt;
        var bytes = _protector.Protect(TicketSerializer.Default.Serialize(ticket));

        await database.AdminSessions
                      .Where(session => session.Id == key && session.RevokedAt == null && session.ExpiresAt > now)
                      .ExecuteUpdateAsync(update => update.SetProperty(session => session.ProtectedTicket, bytes));
    }

    public async Task RemoveAsync(string key)
    {
        var now = clock.GetUtcNow();
        await using var scope = scopes.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                   .AdminSessions
                   .Where(session => session.Id == key && session.RevokedAt == null)
                   .ExecuteUpdateAsync(update => update.SetProperty(session => session.RevokedAt, now)
                                                       .SetProperty(session => session.ProtectedTicket, (byte[]?)null)
                    );
    }
}
