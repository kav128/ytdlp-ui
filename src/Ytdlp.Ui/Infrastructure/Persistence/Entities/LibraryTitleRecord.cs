namespace Ytdlp.Ui.Infrastructure.Persistence.Entities;

public sealed class LibraryTitleRecord
{
    public required string ObjectKey { get; init; }
    public required string ETag { get; init; }
    public long ModifiedSecond { get; init; }
    public required string Title { get; init; }
}
