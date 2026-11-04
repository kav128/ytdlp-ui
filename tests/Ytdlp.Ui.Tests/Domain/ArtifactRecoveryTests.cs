using Ytdlp.Ui.Domain;
using Ytdlp.Ui.Domain.Events;
using Ytdlp.Ui.Domain.Primitives;
using Ytdlp.Ui.Domain.ValueObjects;

namespace Ytdlp.Ui.Tests.Domain;

[TestFixture]
public sealed class ArtifactRecoveryTests
{
    [TestCase(DownloadState.Preparing, DownloadState.Downloading, true)]
    [TestCase(DownloadState.Uploading, DownloadState.Preparing, true)]
    [TestCase(DownloadState.Uploading, DownloadState.Downloading, true)]
    [TestCase(DownloadState.CleaningUp, DownloadState.Downloading, false)]
    [TestCase(DownloadState.Deleting, DownloadState.Downloading, false)]
    [TestCase(DownloadState.Canceling, DownloadState.Downloading, false)]
    public void OnlyMediaStagesCanRewindToRestoreMissingArtifacts(
        DownloadState stage,
        DownloadState resume,
        bool accepted
    )
    {
        var download = Download.Restore(
            new DownloadSnapshot(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                stage,
                AttemptStatus.Running,
                ResumeState: stage
            )
        );

        var transition = download.Apply(new ArtifactsUnavailable(download.AttemptId, stage, resume));
        Assert.That(transition.IsAccepted, Is.EqualTo(accepted));
        Assert.That(download.State, Is.EqualTo(accepted ? resume : stage));
    }
}
