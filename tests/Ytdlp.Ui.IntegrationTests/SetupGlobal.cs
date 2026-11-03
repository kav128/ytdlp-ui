namespace Ytdlp.Ui.IntegrationTests;

[SetUpFixture]
public sealed class SetupGlobal
{
    public static string DirectoryPath { get; } = Path.Combine(
        Path.GetTempPath(),
        "ytdlp-ui-tests",
        Guid.NewGuid().ToString("N")
    );

    [OneTimeSetUp]
    public void SetUp() => Directory.CreateDirectory(DirectoryPath);

    [OneTimeTearDown]
    public void TearDown()
    {
        var testRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ytdlp-ui-tests"))
                     + Path.DirectorySeparatorChar;

        if (!Path.GetFullPath(DirectoryPath)
                 .StartsWith(
                      testRoot,
                      OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal
                  ))
            throw new InvalidOperationException("Test directory is outside the temporary test root.");

        if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true);
    }
}
