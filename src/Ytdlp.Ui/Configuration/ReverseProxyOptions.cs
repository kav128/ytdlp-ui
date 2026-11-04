namespace Ytdlp.Ui.Configuration;

public sealed class ReverseProxyOptions
{
    public string[] KnownProxies { get; set; } = [];
    public string[] KnownNetworks { get; set; } = [];
    public string[] AllowedHosts { get; set; } = [];
}
