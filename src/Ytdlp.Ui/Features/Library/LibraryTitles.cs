using Microsoft.EntityFrameworkCore;
using Ytdlp.Ui.Infrastructure.Persistence;
using Ytdlp.Ui.Infrastructure.Storage;

namespace Ytdlp.Ui.Features.Library;

public sealed class LibraryTitles(AppDbContext database, ILibraryStorage storage)
{
    public async Task<LibraryFile[]> ListAsync(CancellationToken cancellationToken)
    {
        var files = await storage.ListAsync(cancellationToken);

        if (files.Length == 0) return [];

        var titles = await database.LibraryTitles.AsNoTracking()
                                   .ToDictionaryAsync(
                                        record => (record.ObjectKey, record.ETag, record.ModifiedSecond),
                                        record => record.Title,
                                        cancellationToken
                                    );

        return files.Select(file => LibraryFile.Create(
                                file.Key,
                                file.Size,
                                file.Modified,
                                titles.GetValueOrDefault((file.Key, file.ETag, file.Modified.ToUnixTimeSeconds()))
                            )
                     )
                    .OrderByDescending(file => file.LastModified)
                    .ThenBy(file => file.Id, StringComparer.Ordinal)
                    .ToArray();
    }

    public async Task<bool> SetAsync(string key, string? title, CancellationToken cancellationToken)
    {
        var file = await storage.FindAsync(key, cancellationToken);

        if (file is null) return false;

        var modifiedSecond = file.Modified.ToUnixTimeSeconds();

        if (title is null)
        {
            await database.LibraryTitles
                          .Where(record => record.ObjectKey == key
                                        && record.ETag == file.ETag
                                        && record.ModifiedSecond == modifiedSecond
                           )
                          .ExecuteDeleteAsync(cancellationToken);
        }
        else
        {
            await database.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 INSERT INTO "LibraryTitles" ("ObjectKey", "ETag", "ModifiedSecond", "Title")
                 VALUES ({key}, {file.ETag}, {modifiedSecond}, {title})
                 ON CONFLICT ("ObjectKey", "ETag", "ModifiedSecond") DO UPDATE SET "Title" = EXCLUDED."Title"
                 """,
                cancellationToken
            );
        }

        return true;
    }
}
