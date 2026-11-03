using Ytdlp.Ui.Features.Downloads;

namespace Ytdlp.Ui.Tests.Downloads;

[TestFixture]
public sealed class SourceUrlTests
{
    [TestCase("  HTTPS://Example.COM:443/Case?b=2&a=1#Fragment  ", "https://example.com/Case?b=2&a=1#Fragment")]
    [TestCase("HTTP://EXAMPLE.COM:80/a", "http://example.com/a")]
    [TestCase("https://EXAMPLE.COM:8443/a", "https://example.com:8443/a")]
    [TestCase("https://example.com/a/../B?q=%7e&x=1", "https://example.com/a/../B?q=%7e&x=1")]
    [TestCase("http://[::1]:80/path", "http://[::1]/path")]
    [TestCase("https://example.com", "https://example.com")]
    public void NormalizesOnlyAgreedUrlComponents(string input, string expected)
    {
        Assert.That(SourceUrl.TryNormalize(input, out var normalized), Is.True);
        Assert.That(normalized, Is.EqualTo(expected));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("not a url")]
    [TestCase("ftp://example.com/a")]
    [TestCase("https://user:password@example.com/a")]
    [TestCase("https://example.com/a b")]
    [TestCase("https://example.com/a\\b")]
    [TestCase("https://example.com/a\nsecond")]
    public void RejectsInvalidOrCredentialBearingUrl(string? input) =>
        Assert.That(SourceUrl.TryNormalize(input, out _), Is.False);
}
