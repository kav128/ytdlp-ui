namespace Ytdlp.Ui.Features.Auth;

public sealed record AuthTokens(string AccessToken, string TokenType, DateTimeOffset ExpiresAt, string RefreshToken);
