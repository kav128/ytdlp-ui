namespace Ytdlp.Ui.Infrastructure.Authentication;

public sealed class JwtOptions
{
    public string? Issuer { get; set; }
    public string? Audience { get; set; }
    public string? SigningKey { get; set; }

    public byte[] GetSigningKeyBytes() => Convert.FromBase64String(SigningKey ?? string.Empty);
}
