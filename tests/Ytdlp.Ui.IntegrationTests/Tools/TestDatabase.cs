using Npgsql;

namespace Ytdlp.Ui.IntegrationTests.Tools;

public sealed class TestDatabase : IDisposable
{
    public string Name { get; } = "test_" + Guid.NewGuid().ToString("N");
    public string DirectoryPath { get; }
    public string ConnectionString { get; }
    private bool _disposed;

    public TestDatabase()
    {
        DirectoryPath = Path.Combine(SetupGlobal.DirectoryPath, Name);

        var settings = new NpgsqlConnectionStringBuilder(SetupGlobal.PostgreSql.GetConnectionString())
        {
            Database = Name,
            Pooling = false,
            Timeout = 5,
            CommandTimeout = 5
        };

        ConnectionString = settings.ToString();
        Directory.CreateDirectory(DirectoryPath);

        try
        {
            using var connection = new NpgsqlConnection(SetupGlobal.PostgreSql.GetConnectionString());
            connection.Open();
            using var command = new NpgsqlCommand($"CREATE DATABASE \"{Name}\"", connection);
            command.ExecuteNonQuery();
        }
        catch
        {
            Directory.Delete(DirectoryPath);

            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;

        try
        {
            using var connection = new NpgsqlConnection(SetupGlobal.PostgreSql.GetConnectionString());
            connection.Open();
            using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{Name}\" WITH (FORCE)", connection);
            command.ExecuteNonQuery();
        }
        finally
        {
            var root = Path.GetFullPath(SetupGlobal.DirectoryPath) + Path.DirectorySeparatorChar;

            if (!Path.GetFullPath(DirectoryPath)
                     .StartsWith(
                          root,
                          OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal
                      ))
                throw new InvalidOperationException("Test cleanup path is outside its suite directory.");

            if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
