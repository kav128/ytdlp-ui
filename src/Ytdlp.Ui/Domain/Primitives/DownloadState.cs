namespace Ytdlp.Ui.Domain.Primitives;

public enum DownloadState
{
    Queued,
    Downloading,
    Preparing,
    Uploading,
    CleaningUp,
    Completed,
    Deleting,
    Deleted,
    Canceling,
    Failed,
    Canceled
}
