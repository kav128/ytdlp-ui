namespace Ytdlp.Ui.Infrastructure.Persistence.Entities;

public sealed class AdminSessionRecord
{
    public string Id { get; set; } = null!;
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
