using System.Diagnostics;
using System.Text;

namespace Ytdlp.Ui.Infrastructure.Media;

public sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        ProcessCommand command,
        Func<string, Task>? onOutputLine,
        CancellationToken cancellationToken
    )
    {
        var start = new ProcessStartInfo(command.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = command.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in command.Arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        cancellationToken.ThrowIfCancellationRequested();

        if (!process.Start()) throw new InvalidOperationException("Media process could not be started.");

        void Kill()
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }

        async Task<(string Text, bool Truncated)> ReadPipe(
            StreamReader pipe,
            int limit,
            bool tail,
            Func<string, Task>? line
        )
        {
            try
            {
                return await PumpAsync(pipe, limit, tail, line, cancellationToken);
            }
            catch
            {
                Kill();

                throw;
            }
        }

        var output = ReadPipe(process.StandardOutput, command.OutputLimit, false, onOutputLine);
        var errors = ReadPipe(process.StandardError, 16384, true, null);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            var stdout = await output.WaitAsync(cancellationToken);
            var stderr = await errors.WaitAsync(cancellationToken);

            return new(process.ExitCode, stdout.Text, stderr.Text, stdout.Truncated);
        }
        catch
        {
            Kill();
            process.StandardOutput.Dispose();
            process.StandardError.Dispose();
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            try
            {
                await process.WaitForExitAsync(shutdown.Token);
                await Task.WhenAll(output, errors).WaitAsync(shutdown.Token);
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException)
            {
            }

            throw;
        }
    }

    private static async Task<(string Text, bool Truncated)> PumpAsync(
        StreamReader reader,
        int limit,
        bool tail,
        Func<string, Task>? onLine,
        CancellationToken cancellationToken
    )
    {
        var retained = new StringBuilder();
        var line = new StringBuilder();
        var buffer = new char[4096];
        var truncated = false;
        int count;

        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            if (tail)
            {
                retained.Append(buffer, 0, count);

                if (retained.Length > limit)
                {
                    retained.Remove(0, retained.Length - limit);
                    truncated = true;
                }
            }
            else
            {
                var available = Math.Min(count, limit - retained.Length);
                retained.Append(buffer, 0, available);
                if (available != count) truncated = true;
            }

            if (onLine is null) continue;

            for (var index = 0; index < count; index++)
            {
                if (buffer[index] is '\r' or '\n')
                {
                    if (line.Length > 0)
                    {
                        await onLine(line.ToString()).WaitAsync(cancellationToken);
                        line.Clear();
                    }
                }
                else if (line.Length < 16384) line.Append(buffer[index]);
            }
        }

        if (onLine is not null && line.Length > 0) await onLine(line.ToString()).WaitAsync(cancellationToken);

        return (retained.ToString(), truncated);
    }
}
