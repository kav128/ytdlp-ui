using Microsoft.Extensions.Options;
using Ytdlp.Ui.Configuration;
using Ytdlp.Ui.Infrastructure;
using Ytdlp.Ui.Infrastructure.Media;
using Ytdlp.Ui.Infrastructure.Storage;

namespace Ytdlp.Ui.Tests.Storage;

[TestFixture]
public sealed class OwnedCleanupTests
{
    private string _root = null!;
    private readonly Guid _downloadId = Guid.NewGuid();
    private readonly Guid _generation = Guid.NewGuid();

    [SetUp]
    public void SetUp() => _root = Directory.CreateTempSubdirectory("owned-cleanup-").FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(_root, recursive: true);

    [TestCase(false)]
    [TestCase(true)]
    public void ConfirmedMissingDirectoryIsIdempotent(bool spool)
    {
        Assert.DoesNotThrow(() => Clean(spool));
        Assert.That(FileSystemEntry.GetAttributesIfPresent(PathFor(spool)), Is.Null);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ExistingFileAtTheOwnedDirectoryPathIsAnErrorAndIsPreserved(bool spool)
    {
        var directory = PathFor(spool);
        Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
        File.WriteAllText(directory, "preserved");
        Assert.Throws<IOException>(() => Clean(spool));
        Assert.That(File.ReadAllText(directory), Is.EqualTo("preserved"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SuccessfulCleanupRemovesOnlyItsOwnDirectoryAndCanBeRepeated(bool spool)
    {
        var directory = PathFor(spool);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, spool ? "part" : "media.bin"), "own");
        var foreign = Directory.CreateDirectory(Path.Combine(_root, "uploads", Guid.NewGuid().ToString("N"))).FullName;
        File.WriteAllText(Path.Combine(foreign, "part"), "foreign");
        Clean(spool);
        Clean(spool);
        Assert.That(FileSystemEntry.GetAttributesIfPresent(directory), Is.Null);
        Assert.That(File.ReadAllText(Path.Combine(foreign, "part")), Is.EqualTo("foreign"));
    }

    private string PathFor(bool spool) => spool
                                              ? Path.Combine(_root, "uploads", _generation.ToString("N"))
                                              : Path.Combine(
                                                  _root,
                                                  "downloads",
                                                  _downloadId.ToString("N"),
                                                  _generation.ToString("N")
                                              );

    private void Clean(bool spool)
    {
        if (spool) S3StorageUpload.CleanSpool(_root, _generation);
        else
            new WorkspaceStorage(Options.Create(new PathsOptions { WorkDirectory = _root })).Clean(
                _downloadId,
                _generation
            );
    }
}
