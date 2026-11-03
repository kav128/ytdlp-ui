using Microsoft.Extensions.Options;
using Ytdlp.Ui.Configuration;

namespace Ytdlp.Ui.Infrastructure.Media;

public class WorkspaceStorage(IOptions<PathsOptions> options)
{
    private readonly string _root = Path.GetFullPath(options.Value.WorkDirectory);

    public string DirectoryFor(Guid downloadId, Guid generation)
    {
        if (downloadId == Guid.Empty || generation == Guid.Empty)
            throw new InvalidDataException("Workspace identity is required.");

        var directory = Path.GetFullPath(
            Path.Combine(_root, "downloads", downloadId.ToString("N"), generation.ToString("N"))
        );

        VerifyInside(directory, _root);
        CheckLinks(directory);

        return directory;
    }

    public string Resolve(Guid downloadId, Guid generation, string relativePath)
    {
        var directory = DirectoryFor(downloadId, generation);

        if (Path.IsPathRooted(relativePath)) throw new InvalidDataException("Artifact path must be relative.");

        var file = Path.GetFullPath(Path.Combine(directory, relativePath));
        VerifyInside(file, directory);
        CheckLinks(file);

        return file;
    }

    public void Create(Guid downloadId, Guid generation) =>
        Directory.CreateDirectory(DirectoryFor(downloadId, generation));

    public virtual void Clean(Guid downloadId, Guid generation)
    {
        var directory = DirectoryFor(downloadId, generation);

        if (!Directory.Exists(directory)) return;

        CheckTree(directory);
        // The absolute target was checked against its configured root and generation above.
        Directory.Delete(directory, recursive: true);
    }

    private void CheckLinks(string path)
    {
        var current = path;

        while (current.Length >= _root.Length)
        {
            if ((File.Exists(current) || Directory.Exists(current))
             && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Workspace links are not allowed.");

            current = Path.GetDirectoryName(current) ?? "";
        }
    }

    private static void CheckTree(string path)
    {
        foreach (var item in Directory.EnumerateFileSystemEntries(path))
        {
            var attributes = File.GetAttributes(item);

            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Workspace links are not allowed.");

            if ((attributes & FileAttributes.Directory) != 0) CheckTree(item);
        }
    }

    private static void VerifyInside(string target, string root)
    {
        if (!target.StartsWith(
                root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal
            ))
            throw new InvalidDataException("Artifact is outside its workspace.");
    }
}
