namespace Ytdlp.Ui.Domain.Primitives;

public enum ProcessingStage
{
    Download,
    Prepare,
    Upload,
    Cleanup,
    DeleteResult,
    CancelCleanup
}
