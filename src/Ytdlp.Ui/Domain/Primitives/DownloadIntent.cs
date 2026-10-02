namespace Ytdlp.Ui.Domain.Primitives;

public enum DownloadIntent
{
    None,
    EnqueueAttempt,
    ExecuteStage,
    StopAndCleanUp
}
