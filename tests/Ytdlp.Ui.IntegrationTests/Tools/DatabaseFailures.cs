using Microsoft.EntityFrameworkCore;
using Ytdlp.Ui.Infrastructure.Persistence;

namespace Ytdlp.Ui.IntegrationTests.Tools;

public static class DatabaseFailures
{
    public static Task InstallAtStateAsync(AppDbContext database, Ytdlp.Ui.Domain.Primitives.DownloadState state)
    {
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));

        var sql = $"""
                   CREATE FUNCTION reject_test_write() RETURNS trigger LANGUAGE plpgsql AS $body$
                   BEGIN RAISE EXCEPTION 'Test database writes are disabled'; END; $body$;
                   CREATE TRIGGER reject_test_write BEFORE UPDATE ON "Downloads"
                   FOR EACH ROW WHEN (NEW."State" = '{state}') EXECUTE FUNCTION reject_test_write();
                   """;

        return database.Database.ExecuteSqlRawAsync(sql);
    }

    public static Task InstallAsync(AppDbContext database, string table, string operation, bool onlyDeleted = false)
    {
        if (table is not ("AdminSessions" or "Downloads") || operation is not ("INSERT" or "UPDATE"))
            throw new ArgumentException("Unsupported test trigger target.");

        var condition = onlyDeleted ? "WHEN (NEW.\"State\" = 'Deleted')" : "";

        var sql = $"""
                   CREATE FUNCTION reject_test_write() RETURNS trigger LANGUAGE plpgsql AS $body$
                   BEGIN RAISE EXCEPTION 'Test database writes are disabled'; END; $body$;
                   CREATE TRIGGER reject_test_write BEFORE {operation} ON "{table}"
                   FOR EACH ROW {condition} EXECUTE FUNCTION reject_test_write();
                   """;

        return database.Database.ExecuteSqlRawAsync(sql);
    }

    public static Task RemoveAsync(AppDbContext database, string table)
    {
        if (table is not ("AdminSessions" or "Downloads"))
            throw new ArgumentException("Unsupported test trigger target.");

        var sql = $"DROP TRIGGER reject_test_write ON \"{table}\"; DROP FUNCTION reject_test_write();";

        return database.Database.ExecuteSqlRawAsync(sql);
    }
}
