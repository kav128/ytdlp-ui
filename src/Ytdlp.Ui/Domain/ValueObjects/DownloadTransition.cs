using Ytdlp.Ui.Domain.Primitives;

namespace Ytdlp.Ui.Domain.ValueObjects;

public sealed record DownloadTransition(
    DownloadSnapshot Snapshot,
    DownloadIntent Intent = DownloadIntent.None,
    TransitionError? Error = null)
{
    public bool IsAccepted => Error is null;
}
