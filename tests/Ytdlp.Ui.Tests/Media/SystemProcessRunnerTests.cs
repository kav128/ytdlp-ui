using System.Diagnostics;
using System.Text.Json;
using Ytdlp.Ui.Infrastructure.Media;

namespace Ytdlp.Ui.Tests.Media;

[TestFixture, NonParallelizable]
public sealed class SystemProcessRunnerTests
{
    private string _directory = null!;
    private static string RuntimeConfig => Path.Combine(AppContext.BaseDirectory, "Ytdlp.Ui.Tests.runtimeconfig.json");

    private static string[] Arguments(params string[] arguments) => new[]
        {
            "exec",
            "--runtimeconfig",
            RuntimeConfig,
            typeof(ProcessFixtureMarker).Assembly.Location
        }.Concat(arguments)
         .ToArray();

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "ytdlp-process-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ytdlp-process-tests"))
                 + Path.DirectorySeparatorChar;

        if (!Path.GetFullPath(_directory).StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException();

        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [Test]
    public async Task ArgumentsPreserveShellCharactersAsOneLiteralValue()
    {
        const string url = "https://example.test/video?q=$(echo unsafe)&x=1;quoted=\"yes\"";

        var result = await new SystemProcessRunner().RunAsync(
                         new("dotnet", Arguments("echo", url), _directory),
                         null,
                         CancellationToken.None
                     );

        Assert.That(result.ExitCode, Is.Zero);
        Assert.That(JsonSerializer.Deserialize<string[]>(result.Output), Is.EqualTo(new[] { url }));
    }

    [Test]
    public async Task DrainsBothStreamsWithoutCumulativeDurationOrOutputLimit()
    {
        var lines = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var result = await new SystemProcessRunner().RunAsync(
                         new("dotnet", Arguments("flood"), _directory, 2048),
                         _ =>
                         {
                             Interlocked.Increment(ref lines);

                             return Task.CompletedTask;
                         },
                         timeout.Token
                     );

        Assert.That(result.ExitCode, Is.EqualTo(7));
        Assert.That(lines, Is.EqualTo(12000));
        Assert.That(result.Output.Length, Is.LessThanOrEqualTo(2048));
        Assert.That(result.Diagnostics.Length, Is.LessThanOrEqualTo(16384));
        Assert.That(result.OutputTruncated, Is.True);
    }

    [Test]
    public async Task CancellationKillsTheProcessTreeAndWaitsForPipeShutdown()
    {
        var marker = Path.Combine(_directory, "child-ticks");
        var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();

        var execution = new SystemProcessRunner().RunAsync(
            new("dotnet", Arguments("tree", marker, RuntimeConfig), _directory),
            line =>
            {
                if (line.StartsWith("child:", StringComparison.Ordinal)) started.TrySetResult(int.Parse(line[6..]));

                return Task.CompletedTask;
            },
            cancel.Token
        );

        var pid = await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancel.Cancel();

        await Assert.ThrowsAsync(
            Is.InstanceOf<OperationCanceledException>(),
            async () => await execution.WaitAsync(TimeSpan.FromSeconds(10))
        );

        try
        {
            using var child = Process.GetProcessById(pid);

            if (OperatingSystem.IsLinux())
            {
                // A container's PID 1 may leave an exited child in the process table.
                var elapsed = Stopwatch.StartNew();

                while (!child.HasExited && !IsLinuxZombie(pid))
                {
                    Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)), "Child is still running.");
                    await Task.Delay(25);
                }
            }
            else
            {
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        catch (ArgumentException)
        {
        }

        var length = File.Exists(marker) ? new FileInfo(marker).Length : 0;
        await Task.Delay(150);
        Assert.That(File.Exists(marker) ? new FileInfo(marker).Length : 0, Is.EqualTo(length));
    }

    private static bool IsLinuxZombie(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            var state = stat.LastIndexOf(')') + 2;

            return state < stat.Length && stat[state] is 'Z' or 'X';
        }
        catch (IOException error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }
    }

    [Test]
    public async Task CancellationWithUnfinishedIoReportsStopUnconfirmedWithinTheGracePeriod()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();

        var execution = new SystemProcessRunner().RunAsync(
            new("dotnet", Arguments("exit-line"), _directory),
            async _ =>
            {
                entered.TrySetResult();
                await release.Task;
            },
            cancel.Token
        );

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(100);
            var stopped = Stopwatch.StartNew();
            cancel.Cancel();

            await Assert.ThrowsAsync(
                Is.TypeOf<ProcessStopUnconfirmedException>(),
                async () => await execution.WaitAsync(TimeSpan.FromSeconds(10))
            );

            Assert.That(stopped.Elapsed, Is.LessThan(TimeSpan.FromSeconds(8)));
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Test]
    public async Task CallbackFailureStopsTheProcessInsteadOfBlockingItsFullOutputPipe()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await new SystemProcessRunner().RunAsync(
                                                                            new(
                                                                                "dotnet",
                                                                                Arguments("flood"),
                                                                                _directory
                                                                            ),
                                                                            _ => throw new InvalidOperationException(
                                                                                     "callback failed"
                                                                                 ),
                                                                            timeout.Token
                                                                        )
        );
    }
}
