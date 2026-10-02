using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Ytdlp.Ui.Infrastructure.Persistence;

public sealed class SqliteConnectionInterceptor : DbConnectionInterceptor
{
    public static readonly SqliteConnectionInterceptor Instance = new();

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 5000;";
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 5000;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
