using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace Ytdlp.Ui.Features.Library;

public sealed record LibraryFile(
    string Id,
    string Name,
    long Size,
    DateTimeOffset LastModified,
    string ContentUrl,
    string? Title = null
)
{
    public static LibraryFile Create(string key, long size, DateTimeOffset modified, string? title = null)
    {
        var id = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(key));

        return new(id, SafeName(key), size, modified, $"/api/library/{id}/content", title);
    }

    public static bool TryDecode(string id, out string key)
    {
        key = "";

        try
        {
            key = new UTF8Encoding(false, true).GetString(WebEncoders.Base64UrlDecode(id));

            return key.Length > 0
                && Encoding.UTF8.GetByteCount(key) <= 1024
                && WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(key)) == id;
        }
        catch (Exception error) when (error is FormatException or DecoderFallbackException)
        {
            return false;
        }
    }

    public static string SafeName(string input)
    {
        var name = input.Replace('\\', '/').Split('/').Last();
        name = new string(name.Where(character => !char.IsControl(character)).ToArray()).Trim();

        return name is "" or "." or ".." ? "file" : name;
    }
}
