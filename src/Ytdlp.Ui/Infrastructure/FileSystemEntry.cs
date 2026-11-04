namespace Ytdlp.Ui.Infrastructure;

internal static class FileSystemEntry
{
    public static FileAttributes? GetAttributesIfPresent(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }
}
