using Npgsql;
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

                NpgsqlConnectionStringBuilder connection;

                try
                {
                    connection = new NpgsqlConnectionStringBuilder(connectionString);
                }
                catch (ArgumentException)
                {
                    throw new InvalidOperationException(
                        "ConnectionStrings:AppDatabase must be a PostgreSQL connection string."
                    );
                }

                if (string.IsNullOrWhiteSpace(connection.Host)
                 || string.IsNullOrWhiteSpace(connection.Database)
                 || string.IsNullOrWhiteSpace(connection.Username))
                    throw new InvalidOperationException("PostgreSQL host, database and username are required.");

                builder.UseNpgsql(connection.ToString());
            }
        );

        return services;
    }
}
