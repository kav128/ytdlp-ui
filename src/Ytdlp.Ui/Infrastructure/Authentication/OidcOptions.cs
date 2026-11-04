namespace Ytdlp.Ui.Infrastructure.Authentication;

public sealed class OidcOptions
{
    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string GroupsClaim { get; set; } = "groups";
    public string AdminGroup { get; set; } = "";
}
