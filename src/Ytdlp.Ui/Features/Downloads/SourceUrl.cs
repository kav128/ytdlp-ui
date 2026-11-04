using System.Text.RegularExpressions;

namespace Ytdlp.Ui.Features.Downloads;

public static partial class SourceUrl
{
    public static bool TryNormalize(string? original, out string normalized)
    {
        normalized = "";

        if (string.IsNullOrWhiteSpace(original)) return false;

        var trimmed = original.Trim();

        if (trimmed.Any(char.IsWhiteSpace)
         || trimmed.Any(char.IsControl)
         || trimmed.Contains('\\')
         || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
         || uri.Scheme is not ("http" or "https")
         || uri.Host.Length == 0
         || uri.UserInfo.Length > 0
         || !uri.IsWellFormedOriginalString()) return false;

        var match = AbsoluteHttpUrl().Match(trimmed);

        if (!match.Success) return false;

        var authority = match.Groups["authority"].Value;
        var separator = authority.StartsWith('[') ? authority.IndexOf(']') + 1 : authority.LastIndexOf(':');
        if (separator < 0) separator = authority.Length;
        var host = authority[..separator].ToLowerInvariant();
        var port = uri.IsDefaultPort ? "" : authority[separator..];
        // Keep the raw suffix. Uri.AbsoluteUri also canonicalizes dot segments and escaping,
        // which would merge source strings beyond the agreed comparison rules.
        normalized = uri.Scheme.ToLowerInvariant() + "://" + host + port + match.Groups["suffix"].Value;

        return true;
    }

    [GeneratedRegex(
        "^https?://(?<authority>[^/?#]+)(?<suffix>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex AbsoluteHttpUrl();
}
