using System.Text.Json;
using System.Text.RegularExpressions;
using Ytdlp.Ui.Domain.Primitives;

namespace Ytdlp.Ui.Infrastructure.Media;

public static partial class MediaFormatSelection
{
    public static MediaPlan Select(JsonElement metadata)
    {
        var type = Text(metadata, "_type");

        if (type is "playlist" or "multi_video" || metadata.TryGetProperty("entries", out _))
            throw new MediaProcessingException("playlist_not_supported");

        if (Flag(metadata, "is_live") || Text(metadata, "live_status") is "is_live" or "is_upcoming")
            throw new MediaProcessingException("live_not_supported");

        var formats = metadata.TryGetProperty("formats", out var values) && values.ValueKind == JsonValueKind.Array
                          ? values.EnumerateArray().ToArray()
                          : new[] { metadata };

        var parsed = formats.Select(Parse).Where(format => format is not null).Cast<Format>().ToArray();
        var candidates = new List<(Format Video, Format? Audio, bool Mp4)>();

        foreach (var video in parsed.Where(format => format.Video && format.Height is null or <= 1080))
        {
            if (video.Audio) candidates.Add((video, null, video.Extension == "mp4"));
            else
                foreach (var audio in parsed.Where(format => format.Audio && !format.Video))
                    candidates.Add((video, audio, CompatibleMp4(video.VideoCodec, audio.AudioCodec)));
        }

        var selected = candidates.OrderByDescending(item => item.Video.Height.HasValue)
                                 .ThenByDescending(item => item.Mp4)
                                 .ThenByDescending(item => item.Video.Height ?? 0)
                                 .ThenByDescending(item => item.Video.Quality)
                                 .ThenByDescending(item => item.Audio?.Quality ?? 0)
                                 .FirstOrDefault();

        if (selected.Video is null) throw new MediaProcessingException("format_unavailable");

        var tracks = new List<MediaTrack>
        {
            Track(selected.Video, selected.Audio is null ? ArtifactKind.Combined : ArtifactKind.Video)
        };

        if (selected.Audio is not null) tracks.Add(Track(selected.Audio, ArtifactKind.Audio));

        var extension = selected.Audio is null ? selected.Video.Extension
                        : selected.Mp4 ? "mp4"
                        : CompatibleWebm(selected.Video.VideoCodec, selected.Audio.AudioCodec) ? "webm" : "mkv";

        var outputId = selected.Audio is null ? tracks[0].ArtifactId : Guid.NewGuid();
        var outputPath = selected.Audio is null ? tracks[0].RelativePath : $"prepared-{outputId:N}.{extension}";

        return new(Text(metadata, "title") ?? "video", tracks.ToArray(), outputId, outputPath, extension);
    }

    private static MediaTrack Track(Format format, ArtifactKind kind)
    {
        var id = Guid.NewGuid();

        return new(
            id,
            kind,
            format.Id,
            format.Extension,
            format.VideoCodec,
            format.AudioCodec,
            format.Height,
            format.Size,
            $"source-{id:N}.{format.Extension}"
        );
    }

    private static Format? Parse(JsonElement value)
    {
        var id = Text(value, "format_id");
        var extension = Text(value, "ext");

        if (string.IsNullOrEmpty(id)
         || id.Any(char.IsControl)
         || extension is null
         || !Extension().IsMatch(extension)) return null;

        var video = Text(value, "vcodec") ?? "unknown";
        var audio = Text(value, "acodec") ?? "unknown";

        return new(
            id,
            extension.ToLowerInvariant(),
            video,
            audio,
            Number(value, "height") is { } height ? (int)height : null,
            Number(value, "filesize") is { } size ? (long)size : null,
            Number(value, "tbr") ?? Number(value, "abr") ?? 0
        );
    }

    public static bool CompatibleMp4(string video, string audio) =>
        new[]
        {
            "avc",
            "h264",
            "hev",
            "hvc",
            "hevc",
            "av01",
            "av1",
            "mp4v",
            "mpeg4"
        }.Any(video.StartsWith)
     && new[]
        {
            "mp4a",
            "aac",
            "mp3",
            "ac3",
            "eac3",
            "alac"
        }.Any(audio.StartsWith);

    public static bool CompatibleWebm(string video, string audio) =>
        new[]
        {
            "vp8",
            "vp9",
            "vp09",
            "av1",
            "av01"
        }.Any(video.StartsWith)
     && audio is "opus" or "vorbis";

    private static string? Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;

    private static bool Flag(JsonElement value, string name) =>
        value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.True;

    private static double? Number(JsonElement value, string name) =>
        value.TryGetProperty(name, out var item)
     && item.ValueKind == JsonValueKind.Number
     && item.TryGetDouble(out var number)
     && double.IsFinite(number)
            ? number
            : null;

    [GeneratedRegex("^[A-Za-z0-9]{1,12}$")]
    private static partial Regex Extension();

    private sealed record Format(
        string Id,
        string Extension,
        string VideoCodec,
        string AudioCodec,
        int? Height,
        long? Size,
        double Quality
    )
    {
        public bool Video => VideoCodec != "none";
        public bool Audio => AudioCodec != "none";
    }
}
