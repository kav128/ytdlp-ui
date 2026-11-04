using Ytdlp.Ui.Domain.Primitives;

namespace Ytdlp.Ui.Features.Downloads;

public static class DownloadStages
{
    public static ProcessingStage FromState(DownloadState state) => state switch
    {
        DownloadState.Downloading => ProcessingStage.Download,
        DownloadState.Preparing   => ProcessingStage.Prepare,
        DownloadState.Uploading   => ProcessingStage.Upload,
        DownloadState.CleaningUp  => ProcessingStage.Cleanup,
        DownloadState.Canceling   => ProcessingStage.CancelCleanup,
        DownloadState.Deleting    => ProcessingStage.DeleteResult,
        _                         => throw new ArgumentOutOfRangeException(nameof(state))
    };
}
