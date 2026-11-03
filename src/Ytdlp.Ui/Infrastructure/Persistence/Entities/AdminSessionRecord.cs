namespace Ytdlp.Ui.Infrastructure.Persistence.Entities;

public sealed class AdminSessionRecord
{
    public string Id { get; init; } = null!;
    public DateTimeOffset IssuedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }
    public string? Issuer { get; init; }
    public string? Subject { get; init; }
    public byte[]? ProtectedTicket { get; init; }
}
