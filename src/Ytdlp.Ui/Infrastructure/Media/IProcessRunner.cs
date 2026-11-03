namespace Ytdlp.Ui.Infrastructure.Media;

public sealed record ProcessCommand(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    int OutputLimit = 16384
);

public sealed record ProcessResult(int ExitCode, string Output, string Diagnostics, bool OutputTruncated);

public sealed class ProcessStopUnconfirmedException(Exception? inner = null)
    : Exception("The process or its redirected I/O did not stop within the shutdown grace period.", inner);

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        ProcessCommand command,
        Func<string, Task>? onOutputLine,
        CancellationToken cancellationToken
    );
}
