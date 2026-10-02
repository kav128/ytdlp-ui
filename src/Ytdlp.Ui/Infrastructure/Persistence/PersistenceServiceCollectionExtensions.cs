using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Ytdlp.Ui.Infrastructure.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    private const string _connectionStringName = "AppDatabase";

    public static IServiceCollection AddAppDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<AppDbContext>(builder =>
        {
            var connectionString = configuration.GetConnectionString(_connectionStringName);
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException($"ConnectionStrings:{_connectionStringName} is required.");

            var connection = new SqliteConnectionStringBuilder(connectionString);
            if (string.IsNullOrWhiteSpace(connection.DataSource) || connection.DataSource == ":memory:" ||
                connection.Mode == SqliteOpenMode.Memory)
                throw new InvalidOperationException("SQLite requires a persistent database file.");

            connection.DataSource = Path.GetFullPath(connection.DataSource);
            connection.ForeignKeys = true;
            connection.DefaultTimeout = 5;
            connection.Pooling = false;
            builder.UseSqlite(connection.ToString()).AddInterceptors(SqliteConnectionInterceptor.Instance);
        });
        return services;
    }
}
