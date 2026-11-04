using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Ytdlp.Ui.Configuration;

namespace Ytdlp.Ui.Infrastructure.Media;

public sealed partial class MediaProcessor(IProcessRunner runner, IOptions<MediaToolsOptions> options)
{
    public async Task<MediaPlan> PlanAsync(string url, string workspace, CancellationToken cancellationToken)
    {
        var arguments = CommonArguments()
                       .Concat(
                            new[]
                            {
                                "--skip-download",
                                "--dump-single-json",
                                "--flat-playlist",
                                "--playlist-items",
                                "1",
                                "--",
                                url.Trim()
                            }
                        )
                       .ToArray();

        var result = await runner.RunAsync(
                         new(options.Value.YtDlpPath, arguments, workspace, 8 * 1024 * 1024),
                         null,
                         cancellationToken
                     );

        RequireSuccess(result, "metadata_failed");

        if (result.OutputTruncated)
            throw new MediaProcessingException("metadata_failed", "Metadata exceeded the technical JSON buffer.");

        try
        {
            using var document = JsonDocument.Parse(result.Output);

            return MediaFormatSelection.Select(document.RootElement);
        }
        catch (JsonException)
        {
            throw new MediaProcessingException("metadata_failed", "The source returned invalid metadata.");
        }
    }

    public async Task DownloadAsync(
        string url,
        MediaTrack track,
        string outputPath,
        string workspace,
        Func<long, long?, Task> progress,
        CancellationToken cancellationToken
    )
    {
        var temporary = outputPath + ".incoming." + track.Extension;

        var arguments = CommonArguments()
                       .Concat(
                            new[]
                            {
                                "--no-simulate",
                                "--fixup",
                                "never",
                                "--newline",
                                "--progress",
                                "--progress-delta",
                                "1",
                                "--progress-template",
                                "download:progress:%(progress)j",
                                "--format",
                                "all[format_id=\""
                              + track.FormatId.Replace("\\", "\\\\").Replace("\"", "\\\"")
                              + "\"]",
                                "--output",
                                temporary,
                                "--",
                                url.Trim()
                            }
                        )
                       .ToArray();

        var result = await runner.RunAsync(
                         new(options.Value.YtDlpPath, arguments, workspace),
                         async line =>
                         {
                             if (!line.StartsWith("progress:", StringComparison.Ordinal)) return;

                             try
                             {
                                 using var json = JsonDocument.Parse(line[9..]);
                                 var root = json.RootElement;

                                 if (!root.TryGetProperty("downloaded_bytes", out var bytes)
                                  || bytes.ValueKind != JsonValueKind.Number
                                  || !bytes.TryGetInt64(out var downloaded)) return;

                                 long? total =
                                     root.TryGetProperty("total_bytes", out var size)
                                  && size.ValueKind == JsonValueKind.Number
                                  && size.TryGetInt64(out var known)
                                  && known > 0
                                         ? known
                                         : null;

                                 await progress(downloaded, total);
                             }
                             catch (JsonException)
                             {
                             }
                         },
                         cancellationToken
                     );

        RequireSuccess(result, "download_failed");

        if (!File.Exists(temporary))
            throw new MediaProcessingException("download_failed", "The selected track was not produced.");

        await VerifyTrackAsync(track, temporary, workspace, cancellationToken);
        File.Move(temporary, outputPath, overwrite: true);
    }

    public async Task VerifyTrackAsync(
        MediaTrack track,
        string path,
        string workspace,
        CancellationToken cancellationToken
    )
    {
        var probe = await ProbeAsync(path, workspace, cancellationToken);

        var expectedVideo =
            track.Kind is Domain.Primitives.ArtifactKind.Video or Domain.Primitives.ArtifactKind.Combined;

        var expectedAudio =
            track.Kind is Domain.Primitives.ArtifactKind.Audio or Domain.Primitives.ArtifactKind.Combined;

        if (expectedVideo && (!probe.HasVideo || probe.Height is null or > 1080) || expectedAudio && !probe.HasAudio)
            throw new MediaProcessingException(
                "format_unavailable",
                "The downloaded track does not match the media requirements."
            );
    }

    public async Task<MediaPlan> PlanPreparationAsync(
        MediaPlan plan,
        Func<string, string> resolve,
        string workspace,
        CancellationToken cancellationToken
    )
    {
        string extension;

        if (plan.Tracks.Length > 1)
        {
            var video = await ProbeAsync(resolve(plan.Tracks[0].RelativePath), workspace, cancellationToken);
            var audio = await ProbeAsync(resolve(plan.Tracks[1].RelativePath), workspace, cancellationToken);

            extension = MediaFormatSelection.CompatibleMp4(video.VideoCodec ?? "", audio.AudioCodec ?? "")
                            ? "mp4"
                            : MediaFormatSelection.CompatibleWebm(video.VideoCodec ?? "", audio.AudioCodec ?? "")
                                ? "webm"
                                : "mkv";
        }
        else
            extension = ExtensionFor(
                await ValidateCombinedAsync(resolve(plan.Tracks[0].RelativePath), workspace, cancellationToken),
                plan.OutputExtension
            );

        var id = plan.Tracks.Any(track => track.ArtifactId == plan.OutputArtifactId)
                     ? Guid.NewGuid()
                     : plan.OutputArtifactId;

        return plan with
        {
            OutputArtifactId = id,
            OutputExtension = extension,
            OutputPath = $"prepared-{id:N}.{extension}"
        };
    }

    public async Task<MediaPlan> PrepareAsync(
        MediaPlan plan,
        Func<string, string> resolve,
        string workspace,
        CancellationToken cancellationToken
    )
    {
        var output = resolve(plan.OutputPath);
        var temporary = output + ".partial." + plan.OutputExtension;

        if (plan.Tracks.Length > 1)
        {
            var arguments = new List<string>
            {
                "-nostdin",
                "-hide_banner",
                "-loglevel",
                "error",
                "-y"
            };

            foreach (var track in plan.Tracks)
            {
                arguments.Add("-i");
                arguments.Add(resolve(track.RelativePath));
            }

            arguments.AddRange(["-map", "0:v:0", "-map", "1:a:0", "-c", "copy", temporary]);

            var result = await runner.RunAsync(
                             new(options.Value.FfmpegPath, arguments, workspace),
                             null,
                             cancellationToken
                         );

            RequireSuccess(result, "prepare_failed");
        }
        else
        {
            await using var source = new FileStream(
                resolve(plan.Tracks[0].RelativePath),
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan
            );

            await using var destination = new FileStream(
                temporary,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan
            );

            await source.CopyToAsync(destination, 128 * 1024, cancellationToken);
        }

        var verified = await ValidateCombinedAsync(temporary, workspace, cancellationToken);
        var actualExtension = ExtensionFor(verified, plan.OutputExtension);

        if (actualExtension != plan.OutputExtension)
            throw new MediaProcessingException(
                "prepare_failed",
                "The produced container differs from the persisted preparation plan."
            );

        File.Move(temporary, output, overwrite: true);

        return plan;
    }

    public async Task<MediaProbe> ProbeAsync(string path, string workspace, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
                         new(
                             options.Value.FfprobePath,
                             [
                                 "-v", "error", "-show_entries",
                                 "format=format_name:stream=codec_type,codec_name,height:stream_disposition=attached_pic",
                                 "-of", "json", path
                             ],
                             workspace
                         ),
                         null,
                         cancellationToken
                     );

        RequireSuccess(result, "probe_failed");

        try
        {
            using var json = JsonDocument.Parse(result.Output);
            var streams = json.RootElement.GetProperty("streams").EnumerateArray().ToArray();

            var videos = streams.Where(item => item.TryGetProperty("codec_type", out var type)
                                            && type.GetString() == "video"
                                            && !(item.TryGetProperty("disposition", out var disposition)
                                              && disposition.TryGetProperty("attached_pic", out var cover)
                                              && cover.ValueKind == JsonValueKind.Number
                                              && cover.GetInt32() != 0)
                                 )
                                .ToArray();

            var audio = streams.Any(item => item.TryGetProperty("codec_type", out var type)
                                         && type.GetString() == "audio"
            );

            var heights = videos
                         .Select(item => item.TryGetProperty("height", out var height)
                                      && height.TryGetInt32(out var number)
                                             ? number
                                             : (int?)null
                          )
                         .ToArray();

            var format = json.RootElement.GetProperty("format").GetProperty("format_name").GetString() ?? "";

            static string? Codec(JsonElement item) =>
                item.TryGetProperty("codec_name", out var codec) ? codec.GetString() : null;

            return new(
                videos.Length > 0,
                audio,
                heights.Length > 0 && heights.All(height => height > 0) ? heights.Max() : null,
                format,
                videos.Length > 0 ? Codec(videos[0]) : null,
                streams.Where(item => item.TryGetProperty("codec_type", out var type) && type.GetString() == "audio")
                       .Select(Codec)
                       .FirstOrDefault()
            );
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new MediaProcessingException("probe_failed", "The downloaded media could not be verified.");
        }
    }

    public static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );

        return Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken)).ToLowerInvariant();
    }

    private async Task<MediaProbe> ValidateCombinedAsync(
        string path,
        string workspace,
        CancellationToken cancellationToken
    )
    {
        if (!File.Exists(path)) throw new MediaProcessingException("prepare_failed", "Prepared media is missing.");

        var probe = await ProbeAsync(path, workspace, cancellationToken);

        if (!probe.HasVideo || !probe.HasAudio || probe.Height is null or > 1080)
            throw new MediaProcessingException(
                "format_unavailable",
                "Prepared media must contain audio and video up to 1080p."
            );

        return probe;
    }

    private static string ExtensionFor(MediaProbe probe, string expected)
    {
        var format = probe.Format;
        var formats = format.Split(',');

        if (formats.Contains("matroska"))
            return expected == "webm"
                && MediaFormatSelection.CompatibleWebm(probe.VideoCodec ?? "", probe.AudioCodec ?? "")
                       ? "webm"
                       : "mkv";

        if (formats.Contains(expected, StringComparer.OrdinalIgnoreCase)) return expected;
        if (formats.Contains("mov")) return expected is "mp4" or "mov" or "m4v" or "3gp" ? expected : "mp4";
        if (format == "mpegts") return "ts";

        var extension = formats[0];

        if (!ContainerExtension().IsMatch(extension))
            throw new MediaProcessingException("probe_failed", "Unknown media container.");

        return extension;
    }

    private string[] CommonArguments() =>
    [
        "--ignore-config", "--no-playlist", "--no-warnings", "--no-color",
        "--no-js-runtimes", "--js-runtimes", "node:" + options.Value.NodePath
    ];

    private static void RequireSuccess(ProcessResult result, string code)
    {
        if (result.ExitCode != 0)
            throw new MediaProcessingException(
                code,
                Url().Replace(result.Diagnostics, "[source URL]"),
                toolDiagnostic: true
            );
    }

    [GeneratedRegex("https?://[^\\s\\\"']+", RegexOptions.IgnoreCase)]
    private static partial Regex Url();

    [GeneratedRegex("^[a-zA-Z0-9]{1,12}$")]
    private static partial Regex ContainerExtension();
}
