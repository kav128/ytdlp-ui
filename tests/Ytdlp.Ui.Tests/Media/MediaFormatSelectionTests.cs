using System.Text.Json;
using Ytdlp.Ui.Infrastructure.Media;

namespace Ytdlp.Ui.Tests.Media;

[TestFixture]
public sealed class MediaFormatSelectionTests
{
    [TestCase("mpeg4", "aac", true)]
    [TestCase("h264", "opus", false)]
    public void ActualProbeCodecAliasesPreserveMp4Compatibility(string video, string audio, bool compatible) =>
        Assert.That(MediaFormatSelection.CompatibleMp4(video, audio), Is.EqualTo(compatible));

    [Test]
    public void PrefersSuitableMp4AndNeverSelectsKnownAbove1080()
    {
        using var json = JsonDocument.Parse(
            """{"formats":[{"format_id":"4k","ext":"mp4","height":2160,"vcodec":"avc1","acodec":"aac"},{"format_id":"webm","ext":"webm","height":1080,"vcodec":"vp9","acodec":"opus"},{"format_id":"mp4","ext":"mp4","height":720,"vcodec":"avc1","acodec":"aac"}]}"""
        );

        Assert.That(MediaFormatSelection.Select(json.RootElement).Tracks.Single().FormatId, Is.EqualTo("mp4"));
    }

    [Test]
    public void SelectsSeparateTracksAndCompatibleContainerWithoutTranscoding()
    {
        using var json = JsonDocument.Parse(
            """{"formats":[{"format_id":"0","ext":"mp4","height":1080,"vcodec":"avc1.6400","acodec":"none"},{"format_id":"1","ext":"m4a","vcodec":"none","acodec":"mp4a.40.2"}]}"""
        );

        var plan = MediaFormatSelection.Select(json.RootElement);
        Assert.That(plan.Tracks, Has.Length.EqualTo(2));
        Assert.That(plan.OutputExtension, Is.EqualTo("mp4"));
    }

    [Test]
    public void UnknownGenericMp4RemainsAFallbackButDoesNotDisplaceKnownSuitableVideo()
    {
        using var json = JsonDocument.Parse(
            """{"formats":[{"format_id":"mp4","ext":"mp4","height":null,"vcodec":null,"acodec":null},{"format_id":"webm","ext":"webm","height":1080,"vcodec":"vp9","acodec":"opus"}]}"""
        );

        Assert.That(MediaFormatSelection.Select(json.RootElement).Tracks.Single().FormatId, Is.EqualTo("webm"));

        using var direct = JsonDocument.Parse(
            """{"format_id":"mp4","ext":"mp4","height":null,"vcodec":null,"acodec":null}"""
        );

        Assert.That(MediaFormatSelection.Select(direct.RootElement).Tracks.Single().FormatId, Is.EqualTo("mp4"));
    }

    [TestCase("{\"_type\":\"playlist\",\"entries\":[]}", "playlist_not_supported")]
    [TestCase("{\"is_live\":true}", "live_not_supported")]
    [TestCase("{\"live_status\":\"is_upcoming\"}", "live_not_supported")]
    [TestCase(
        "{\"formats\":[{\"format_id\":\"4k\",\"ext\":\"mp4\",\"height\":2160,\"vcodec\":\"h264\",\"acodec\":\"aac\"}]}",
        "format_unavailable"
    )]
    public void RejectsUnsupportedSourceBeforeDownloading(string metadata, string code)
    {
        using var json = JsonDocument.Parse(metadata);
        var error = Assert.Throws<MediaProcessingException>(() => MediaFormatSelection.Select(json.RootElement));
        Assert.That(error!.Code, Is.EqualTo(code));
    }
}
