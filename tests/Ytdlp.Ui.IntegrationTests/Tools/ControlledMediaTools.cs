using System.Collections.Concurrent;
using System.Text.Json;
using NSubstitute;
using Ytdlp.Ui.Infrastructure.Media;

namespace Ytdlp.Ui.IntegrationTests.Tools;

public sealed class ControlledMediaTools
{
    public IProcessRunner Runner { get; } = Substitute.For<IProcessRunner>();
    public string Metadata { get; set; } = "{}";
    public bool FailPrepare { get; set; }
    public bool PauseDownloads { get; set; }
    public bool KnownProgress { get; set; } = true;
    public bool CoverArtOnly { get; set; }
    public int PayloadLength { get; set; } = 100;
    public string VideoCodec { get; set; } = "h264";
    public string AudioCodec { get; set; } = "aac";
    public string TrackFormat { get; set; } = "mov,mp4,m4a,3gp,3g2,mj2";
    public string CombinedFormat { get; set; } = "mov,mp4,m4a,3gp,3g2,mj2";
    public TaskCompletionSource DownloadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ContinueDownload { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ConcurrentQueue<ProcessCommand> Commands { get; } = new();
    public int MaximumConcurrency => _maximum;
    private readonly ConcurrentDictionary<string, string> _kinds = new();
    private int _active;
    private int _maximum;

    public ControlledMediaTools() => Runner.RunAsync(
                                                Arg.Any<ProcessCommand>(),
                                                Arg.Any<Func<string, Task>?>(),
                                                Arg.Any<CancellationToken>()
                                            )
                                           .Returns(call => RunAsync(
                                                        call.ArgAt<ProcessCommand>(0),
                                                        call.ArgAt<Func<string, Task>?>(1),
                                                        call.ArgAt<CancellationToken>(2)
                                                    )
                                            );

    private async Task<ProcessResult> RunAsync(
        ProcessCommand command,
        Func<string, Task>? output,
        CancellationToken cancellationToken
    )
    {
        Commands.Enqueue(command);
        var active = Interlocked.Increment(ref _active);
        int observed;

        do
        {
            observed = _maximum;
        } while (active > observed && Interlocked.CompareExchange(ref _maximum, active, observed) != observed);

        try
        {
            await Task.Delay(10, cancellationToken);

            if (command.Arguments.Contains("--dump-single-json")) return new(0, Metadata, "", false);

            if (command.Arguments.Contains("--format"))
            {
                var selector = Value(command, "--format");

                if (!selector.StartsWith("all[", StringComparison.Ordinal))
                    return new(1, "", "Requested format is not available", false);

                if (output is not null)
                    await output(
                        KnownProgress
                            ? "progress:{\"downloaded_bytes\":25,\"total_bytes\":100}"
                            : "progress:{\"downloaded_bytes\":25,\"total_bytes\":null}"
                    );

                DownloadEntered.TrySetResult();
                if (PauseDownloads) await ContinueDownload.Task.WaitAsync(cancellationToken);
                var path = Value(command, "--output");

                _kinds[path] = selector.Contains("\"1\"", StringComparison.Ordinal)   ? "audio"
                               : selector.Contains("\"0\"", StringComparison.Ordinal) ? "video" : "combined";

                await WritePayloadAsync(path, PayloadLength, cancellationToken);

                return new(0, "", "", false);
            }

            if (command.Arguments.Contains("-show_entries"))
            {
                var path = command.Arguments.Last();
                var kind = _kinds.GetValueOrDefault(path, "combined");
                var streams = new List<object>();

                if (kind != "audio")
                    streams.Add(
                        new
                        {
                            codec_type = "video",
                            codec_name = VideoCodec,
                            height = 720,
                            disposition = new { attached_pic = CoverArtOnly ? 1 : 0 }
                        }
                    );

                if (kind != "video")
                    streams.Add(
                        new
                        {
                            codec_type = "audio",
                            codec_name = AudioCodec,
                            height = (int?)null
                        }
                    );

                var format = kind == "combined" ? CombinedFormat : TrackFormat;

                return new(
                    0,
                    JsonSerializer.Serialize(
                        new
                        {
                            format = new { format_name = format },
                            streams
                        }
                    ),
                    "",
                    false
                );
            }

            if (command.Arguments.Contains("-c"))
            {
                if (FailPrepare) return new(1, "", "merge failed https://private.test/path?token=secret", false);

                var path = command.Arguments.Last();
                _kinds[path] = "combined";
                await WritePayloadAsync(path, PayloadLength * 2, cancellationToken);

                return new(0, "", "", false);
            }

            throw new InvalidOperationException("Unexpected media tool command.");
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    public static string Value(ProcessCommand command, string flag) =>
        command.Arguments[command.Arguments.ToList().IndexOf(flag) + 1];

    private static async Task WritePayloadAsync(string path, int size, CancellationToken cancellationToken)
    {
        var buffer = Enumerable.Repeat((byte)42, 128 * 1024).ToArray();

        await using var file = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            buffer.Length,
            FileOptions.Asynchronous
        );

        for (var remaining = size; remaining > 0; remaining -= Math.Min(remaining, buffer.Length))
            await file.WriteAsync(buffer.AsMemory(0, Math.Min(remaining, buffer.Length)), cancellationToken);
    }
}
