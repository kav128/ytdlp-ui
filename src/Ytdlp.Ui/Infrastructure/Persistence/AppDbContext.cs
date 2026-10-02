using Microsoft.EntityFrameworkCore;
using Ytdlp.Ui.Infrastructure.Persistence.Entities;

namespace Ytdlp.Ui.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<DownloadRecord> Downloads => Set<DownloadRecord>();
    public DbSet<DownloadAttemptRecord> Attempts => Set<DownloadAttemptRecord>();
    public DbSet<StageExecutionRecord> Stages => Set<StageExecutionRecord>();
    public DbSet<DownloadArtifactRecord> Artifacts => Set<DownloadArtifactRecord>();
    public DbSet<StorageTransferRecord> Transfers => Set<StorageTransferRecord>();
    public DbSet<AdminSessionRecord> AdminSessions => Set<AdminSessionRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AdminSessionRecord>(builder =>
        {
            builder.ToTable("AdminSessions", table => table.HasCheckConstraint("CK_Sessions_Expiration", "\"ExpiresAt\" > \"IssuedAt\""));
            builder.HasKey(record => record.Id);
            builder.Property(record => record.Id).ValueGeneratedNever();
            builder.HasIndex(record => record.ExpiresAt);
        });

        modelBuilder.Entity<DownloadArtifactRecord>(builder =>
        {
            builder.ToTable("DownloadArtifacts", table => table.HasCheckConstraint("CK_Artifacts_Size", "\"Size\" IS NULL OR \"Size\" >= 0"));
            builder.HasKey(record => record.Id);
            builder.Property(record => record.Id).ValueGeneratedNever();
            builder.HasAlternateKey(record => new { record.DownloadId, record.Id });
            builder.HasOne<DownloadRecord>().WithMany().HasForeignKey(record => record.DownloadId).OnDelete(DeleteBehavior.Restrict);
            builder.Property(record => record.Kind).HasConversion<string>();
            builder.Property(record => record.RelativePath).IsRequired();
            builder.HasIndex(record => new { record.DownloadId, record.WorkspaceGeneration, record.RelativePath }).IsUnique();
        });

        modelBuilder.Entity<DownloadAttemptRecord>(builder =>
        {
            builder.ToTable("DownloadAttempts", table => table.HasCheckConstraint("CK_Attempts_Number", "\"Number\" >= 1"));
            builder.HasKey(record => record.Id);
            builder.Property(record => record.Id).ValueGeneratedNever();
            builder.HasAlternateKey(record => new { record.DownloadId, record.Id });
            builder.HasOne<DownloadRecord>().WithMany().HasForeignKey(record => record.DownloadId).OnDelete(DeleteBehavior.Restrict);
            builder.Property(record => record.Reason).HasConversion<string>();
            builder.Property(record => record.InitialState).HasConversion<string>();
            builder.Property(record => record.Status).HasConversion<string>();
            builder.HasIndex(record => new { record.DownloadId, record.Number }).IsUnique();
            builder.HasIndex(record => record.DownloadId).IsUnique().HasDatabaseName("IX_Attempts_OneActive")
                .HasFilter("\"Status\" IN ('Pending', 'Running')");
            builder.HasIndex(record => new { record.Status, record.QueuedAt, record.Id });
        });

        modelBuilder.Entity<DownloadRecord>(builder =>
        {
            builder.ToTable("Downloads", table => table.HasCheckConstraint("CK_Downloads_Version", "\"Version\" >= 1"));
            builder.HasKey(record => record.Id);
            builder.Property(record => record.Id).ValueGeneratedNever();
            builder.Property(record => record.OriginalUrl).IsRequired();
            builder.Property(record => record.NormalizedUrl).IsRequired();
            builder.HasIndex(record => record.NormalizedUrl).IsUnique();
            builder.Property(record => record.State).HasConversion<string>();
            builder.Property(record => record.AttemptStatus).HasConversion<string>();
            builder.Property(record => record.ResumeState).HasConversion<string>();
            builder.Property(record => record.FailedState).HasConversion<string>();
            builder.Property(record => record.PublicationState).HasConversion<string>();
            builder.Property(record => record.Intent).HasConversion<string>();
            builder.Property(record => record.Version).IsConcurrencyToken().ValueGeneratedNever();
        });

        modelBuilder.Entity<StageExecutionRecord>(builder =>
        {
            builder.ToTable("StageExecutions", table => table.HasCheckConstraint("CK_Stages_Progress",
                "\"Progress\" IS NULL OR (\"Progress\" >= 0 AND \"Progress\" <= 100)"));
            builder.HasKey(record => record.Id);
            builder.Property(record => record.Id).ValueGeneratedNever();
            builder.HasAlternateKey(record => new { record.DownloadId, record.Id });
            builder.Property(record => record.Stage).HasConversion<string>();
            builder.Property(record => record.Status).HasConversion<string>();
            builder.HasIndex(record => new { record.AttemptId, record.Stage }).IsUnique();
            builder.HasOne<DownloadAttemptRecord>().WithMany().HasForeignKey(record => new { record.DownloadId, record.AttemptId })
                .HasPrincipalKey(record => new { record.DownloadId, record.Id }).OnDelete(DeleteBehavior.Restrict);
            builder.HasOne<DownloadArtifactRecord>().WithMany().HasForeignKey(record => new { record.DownloadId, record.ArtifactId })
                .HasPrincipalKey(record => new { record.DownloadId, record.Id }).OnDelete(DeleteBehavior.Restrict);
            builder.HasOne<StageExecutionRecord>().WithMany().HasForeignKey(record => new { record.DownloadId, record.ReusedFromStageId })
                .HasPrincipalKey(record => new { record.DownloadId, record.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<StorageTransferRecord>(builder =>
        {
            builder.ToTable("StorageTransfers", table => table.HasCheckConstraint("CK_Transfers_Size", "\"ExpectedSize\" >= 0"));
            builder.HasKey(record => record.Id);
            builder.Property(record => record.Id).ValueGeneratedNever();
            builder.Property(record => record.Status).HasConversion<string>();
            builder.Property(record => record.ObjectKey).IsRequired();
            builder.HasIndex(record => record.ObjectKey).IsUnique();
            builder.HasIndex(record => new { record.DownloadId, record.PublicationId }).IsUnique();
            builder.HasOne<DownloadArtifactRecord>().WithMany().HasForeignKey(record => new { record.DownloadId, record.ArtifactId })
                .HasPrincipalKey(record => new { record.DownloadId, record.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(entity => entity.GetProperties()))
        {
            if (property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?))
            {
                property.SetValueConverter(UnixMillisecondsConverter.Instance);
                property.SetColumnType("INTEGER");
            }
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var connection = Database.GetDbConnection();
        Directory.CreateDirectory(Path.GetDirectoryName(connection.DataSource)!);
        await Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode = WAL;";
            var mode = await command.ExecuteScalarAsync(cancellationToken);
            if (!string.Equals(mode?.ToString(), "wal", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("SQLite WAL mode could not be enabled.");

            await Database.MigrateAsync(cancellationToken);
        }
        finally
        {
            await Database.CloseConnectionAsync();
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        PrepareVersions();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        PrepareVersions();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void PrepareVersions()
    {
        foreach (var entry in ChangeTracker.Entries<DownloadRecord>())
        {
            if (entry.State == EntityState.Added)
                entry.Entity.Version = 1;
            else if (entry.State == EntityState.Modified)
                entry.Property(record => record.Version).CurrentValue =
                    checked(entry.Property(record => record.Version).OriginalValue + 1);
        }
    }
}
