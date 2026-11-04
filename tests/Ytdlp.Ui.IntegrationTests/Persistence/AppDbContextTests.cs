using Npgsql;
using Ytdlp.Ui.IntegrationTests.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Ytdlp.Ui.Domain;
using Ytdlp.Ui.Domain.Events;
using Ytdlp.Ui.Domain.Primitives;
using Ytdlp.Ui.Infrastructure.Persistence;
using Ytdlp.Ui.Infrastructure.Persistence.Entities;

namespace Ytdlp.Ui.IntegrationTests.Persistence;

[TestFixture]
[Category("PostgreSQL")]
public class AppDbContextTests
{
    private static readonly DateTimeOffset _createdAt = new(2026, 10, 2, 10, 20, 30, 123, TimeSpan.FromHours(3));
    private TestDatabase _database = null!;
    private ServiceProvider _provider = null!;

    [SetUp]
    public async Task SetUp()
    {
        _database = new TestDatabase();
        var services = new ServiceCollection();
        services.AddAppDatabase(CreateConfiguration(_database.ConnectionString));
        _provider = services.BuildServiceProvider();
        await using var context = CreateContext();
        await context.InitializeAsync();
    }

    [TearDown]
    public void TearDown()
    {
        _provider?.Dispose();
        _database?.Dispose();
    }

    [Test]
    public async Task MigrationIsIdempotentAndMatchesTheCurrentModel()
    {
        await using var context = CreateContext();
        var migrations = (await context.Database.GetAppliedMigrationsAsync()).ToArray();
        Assert.That(migrations, Is.EqualTo(context.Database.GetMigrations()));
        Assert.That(migrations[0], Does.EndWith("_InitialPostgreSql"));
        Assert.That(migrations, Has.Some.EndsWith("_LibraryTitles"));
        Assert.That(await context.Database.GetPendingMigrationsAsync(), Is.Empty);
        Assert.That(context.Database.HasPendingModelChanges(), Is.False);
        await context.InitializeAsync();
        Assert.That(await context.Database.GetAppliedMigrationsAsync(), Is.EqualTo(migrations));
    }

    [Test]
    public async Task EveryConnectionUsesTheConfiguredPostgreSqlDatabaseAndUtc()
    {
        for (var index = 0; index < 2; index++)
        {
            await using var context = CreateContext();
            Assert.That(await ScalarAsync(context, "SELECT current_database();"), Is.EqualTo(_database.Name));
            Assert.That(await ScalarAsync(context, "SHOW TimeZone;"), Is.AnyOf("UTC", "Etc/UTC"));
        }
    }

    [Test]
    public async Task ProtectedTicketsRoundTripAndRevokedLegacyRowsRemainReadable()
    {
        var ticket = new byte[]
        {
            0,
            1,
            2,
            255
        };

        await using (var context = CreateContext())
        {
            context.AdminSessions.AddRange(
                new AdminSessionRecord
                {
                    Id = "active",
                    IssuedAt = _createdAt,
                    ExpiresAt = _createdAt.AddHours(12),
                    Issuer = "issuer",
                    Subject = "subject",
                    ProtectedTicket = ticket
                },
                new AdminSessionRecord
                {
                    Id = "legacy",
                    IssuedAt = _createdAt,
                    ExpiresAt = _createdAt.AddHours(12),
                    RevokedAt = _createdAt
                }
            );

            await context.SaveChangesAsync();
        }

        await using var reopened = CreateContext();
        var stored = await reopened.AdminSessions.SingleAsync(session => session.Id == "active");
        Assert.That(stored.ProtectedTicket, Is.EqualTo(ticket));
        Assert.That(stored.Issuer, Is.EqualTo("issuer"));
        Assert.That(stored.Subject, Is.EqualTo("subject"));
        var legacy = await reopened.AdminSessions.SingleAsync(session => session.Id == "legacy");
        Assert.That(legacy.ProtectedTicket, Is.Null);
        Assert.That(legacy.RevokedAt, Is.Not.Null);
    }

    [Test]
    public async Task ApplyingInitialMigrationAgainKeepsExistingDownloadHistory()
    {
        await using var context = CreateContext();
        var download = await AddDownloadAsync(context);
        await context.InitializeAsync();
        Assert.That((await context.Downloads.SingleAsync()).Id, Is.EqualTo(download.Id));
        Assert.That(await context.Attempts.CountAsync(), Is.EqualTo(1));
        Assert.That(await context.AdminSessions.CountAsync(), Is.Zero);
        Assert.That(await context.Database.GetPendingMigrationsAsync(), Is.Empty);
    }

    [Test]
    public async Task MediaPlanMigrationKeepsEarlierJobsStageHistoryAndTickets()
    {
        await using var context = CreateContext();
        var download = await AddDownloadAsync(context);

        context.Stages.Add(
            new StageExecutionRecord
            {
                Id = Guid.NewGuid(),
                DownloadId = download.Id,
                AttemptId = download.CurrentAttemptId,
                Stage = ProcessingStage.Download,
                Status = StageStatus.Pending
            }
        );

        context.AdminSessions.Add(
            new AdminSessionRecord
            {
                Id = "before-media-plan",
                IssuedAt = _createdAt,
                ExpiresAt = _createdAt.AddHours(12),
                Issuer = "issuer",
                Subject = "subject",
                ProtectedTicket = new byte[]
                {
                    1,
                    2,
                    3
                }
            }
        );

        await context.SaveChangesAsync();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20261103181835_InitialPostgreSql");
        context.ChangeTracker.Clear();
        await context.InitializeAsync();
        var reopened = await context.Downloads.SingleAsync();
        Assert.That(reopened.Id, Is.EqualTo(download.Id));
        Assert.That(reopened.MediaPlanJson, Is.Null);
        Assert.That(await context.Attempts.CountAsync(), Is.EqualTo(1));
        Assert.That(await context.Stages.CountAsync(), Is.EqualTo(1));

        Assert.That(
            (await context.AdminSessions.SingleAsync()).ProtectedTicket,
            Is.EqualTo(
                new byte[]
                {
                    1,
                    2,
                    3
                }
            )
        );

        Assert.That(await context.Database.GetPendingMigrationsAsync(), Is.Empty);
    }

    [TestCase("The media plan is missing; downloading must be restored.", "media_plan_missing", true)]
    [TestCase(
        "A required artifact is missing or has a different size/SHA-256; the earliest necessary stage will be restored.",
        "artifacts_unavailable",
        true
    )]
    [TestCase("The existing publication will be reconciled before any upload.", "publication_reconciliation", true)]
    [TestCase("The stage failed; its files were retained for a manual retry.", null, true)]
    [TestCase("playlist_not_supported", null, true)]
    [TestCase("ffmpeg stderr: unsupported packet at 00:01", null, false)]
    public async Task ContinuationMigrationPreservesHistoryAndRealDiagnosticsWhileReplacingExactOwnedMessages(
        string details,
        string? reason,
        bool cleared
    )
    {
        await using var context = CreateContext();
        var record = await AddDownloadAsync(context);
        var attempt = await context.Attempts.SingleAsync();
        var id = attempt.Id;
        attempt.ErrorCode = "prepare_failed";
        attempt.ErrorDetails = details;
        await context.SaveChangesAsync();
        await context.GetService<IMigrator>().MigrateAsync("20261103185351_MediaPlan");
        context.ChangeTracker.Clear();
        await context.InitializeAsync();
        var restored = await context.Attempts.SingleAsync();
        Assert.That(restored.Id, Is.EqualTo(id));
        Assert.That(restored.DownloadId, Is.EqualTo(record.Id));
        Assert.That(restored.ErrorCode, Is.EqualTo("prepare_failed"));
        Assert.That(restored.ContinuationReasonCode, Is.EqualTo(reason));
        Assert.That(restored.ErrorDetails, Is.EqualTo(cleared ? null : details));
        Assert.That(await context.Database.GetPendingMigrationsAsync(), Is.Empty);
    }

    [Test]
    public async Task AggregateAndRelatedHistorySurviveReopeningTheDatabase()
    {
        Guid downloadId;
        Guid artifactId;
        Guid firstStageId;
        Guid secondAttemptId;
        string sessionId;
        Download aggregate;

        await using (var context = CreateContext())
        {
            var record = NewDownload();
            aggregate = record.RestoreAggregate();
            Assert.That(aggregate.Apply(new StartAttempt(aggregate.AttemptId)).IsAccepted, Is.True);

            Assert.That(
                aggregate.Apply(new StageSucceeded(aggregate.AttemptId, DownloadState.Downloading)).IsAccepted,
                Is.True
            );

            Assert.That(
                aggregate.Apply(new StageSucceeded(aggregate.AttemptId, DownloadState.Preparing)).IsAccepted,
                Is.True
            );

            Assert.That(
                aggregate.Apply(
                              new StageFailed(
                                  aggregate.AttemptId,
                                  DownloadState.Uploading,
                                  new("upload_failed", "diagnostic"),
                                  PublicationUncertain: true
                              )
                          )
                         .IsAccepted,
                Is.True
            );

            record.Capture(aggregate, DownloadIntent.None, _createdAt.AddMinutes(1));
            record.Title = "Example";
            context.Downloads.Add(record);
            var firstAttempt = NewAttempt(record);
            firstAttempt.Status = AttemptStatus.Failed;
            firstAttempt.StartedAt = _createdAt;
            firstAttempt.FinishedAt = _createdAt.AddMinutes(1);
            firstAttempt.ErrorCode = "upload_failed";
            context.Attempts.Add(firstAttempt);
            downloadId = record.Id;
            artifactId = Guid.NewGuid();

            context.Artifacts.Add(
                new DownloadArtifactRecord
                {
                    Id = artifactId,
                    DownloadId = downloadId,
                    WorkspaceGeneration = record.WorkspaceGeneration,
                    Kind = ArtifactKind.Prepared,
                    RelativePath = "result.mp4",
                    Size = 12345,
                    IsReady = true,
                    Sha256 = new string('a', 64)
                }
            );

            firstStageId = Guid.NewGuid();

            context.Stages.Add(
                new StageExecutionRecord
                {
                    Id = firstStageId,
                    DownloadId = downloadId,
                    AttemptId = firstAttempt.Id,
                    Stage = ProcessingStage.Prepare,
                    Status = StageStatus.Succeeded,
                    ArtifactId = artifactId,
                    StartedAt = _createdAt,
                    FinishedAt = _createdAt.AddSeconds(30)
                }
            );

            context.Transfers.Add(
                new StorageTransferRecord
                {
                    Id = Guid.NewGuid(),
                    DownloadId = downloadId,
                    ArtifactId = artifactId,
                    PublicationId = record.PublicationId,
                    ObjectKey = $"downloads/{downloadId}/{record.PublicationId}/result.mp4",
                    ExpectedSize = 12345,
                    Sha256 = new string('a', 64),
                    MultipartUploadId = "upload-id",
                    Status = TransferStatus.Failed,
                    CreatedAt = _createdAt,
                    UpdatedAt = _createdAt.AddMinutes(1)
                }
            );

            await context.SaveChangesAsync();

            secondAttemptId = Guid.NewGuid();
            var retry = aggregate.Apply(new Retry(secondAttemptId));
            Assert.That(retry.IsAccepted, Is.True);
            record.Capture(aggregate, retry.Intent, _createdAt.AddMinutes(2));
            context.Attempts.Add(NewAttempt(record, number: 2));

            context.Stages.Add(
                new StageExecutionRecord
                {
                    Id = Guid.NewGuid(),
                    DownloadId = downloadId,
                    AttemptId = secondAttemptId,
                    Stage = ProcessingStage.Upload,
                    Status = StageStatus.Pending,
                    ReusedFromStageId = firstStageId,
                    ArtifactId = artifactId
                }
            );

            sessionId = Guid.NewGuid().ToString();

            context.AdminSessions.Add(
                new AdminSessionRecord
                {
                    Id = sessionId,
                    IssuedAt = _createdAt,
                    ExpiresAt = _createdAt.AddHours(12),
                    RevokedAt = _createdAt.AddHours(1)
                }
            );

            await context.SaveChangesAsync();
        }

        await using var reopened = CreateContext();
        var saved = await reopened.Downloads.SingleAsync(record => record.Id == downloadId);

        Assert.Multiple(() =>
            {
                Assert.That(saved.RestoreAggregate().Snapshot, Is.EqualTo(aggregate.Snapshot));
                Assert.That(saved.Version, Is.EqualTo(2));
                Assert.That(saved.Title, Is.EqualTo("Example"));
                Assert.That(saved.Intent, Is.EqualTo(DownloadIntent.EnqueueAttempt));
                Assert.That(saved.CreatedAt.Offset, Is.EqualTo(TimeSpan.Zero));
                Assert.That(saved.CreatedAt, Is.EqualTo(_createdAt));
            }
        );

        var attempts = await reopened.Attempts.OrderBy(attempt => attempt.Number).ToArrayAsync();

        Assert.That(
            attempts.Select(attempt => attempt.Status),
            Is.EqualTo(
                new[]
                {
                    AttemptStatus.Failed,
                    AttemptStatus.Pending
                }
            )
        );

        Assert.That(attempts[1].Id, Is.EqualTo(secondAttemptId));
        Assert.That((await reopened.Artifacts.SingleAsync()).Id, Is.EqualTo(artifactId));
        var stages = await reopened.Stages.ToArrayAsync();
        Assert.That(stages, Has.Length.EqualTo(2));

        Assert.That(
            stages.Single(stage => stage.Stage == ProcessingStage.Upload).ReusedFromStageId,
            Is.EqualTo(firstStageId)
        );

        var transfer = await reopened.Transfers.SingleAsync();
        Assert.That(transfer.MultipartUploadId, Is.EqualTo("upload-id"));
        Assert.That(transfer.Sha256, Is.EqualTo(new string('a', 64)));
        var session = await reopened.AdminSessions.SingleAsync();
        Assert.That(session.Id, Is.EqualTo(sessionId));
        Assert.That(session.RevokedAt, Is.EqualTo(_createdAt.AddHours(1)));

        Assert.That(
            await ScalarAsync(reopened, "SELECT pg_typeof(\"CreatedAt\")::text FROM \"Downloads\" LIMIT 1;"),
            Is.EqualTo("timestamp with time zone")
        );

        Assert.That(
            await ScalarAsync(reopened, "SELECT pg_typeof(\"QueuedAt\")::text FROM \"DownloadAttempts\" LIMIT 1;"),
            Is.EqualTo("timestamp with time zone")
        );
    }

    [Test]
    public async Task DuplicateNormalizedUrlIsRejectedByTheDatabase()
    {
        await using var context = CreateContext();
        var first = NewDownload();
        context.Downloads.Add(first);
        await context.SaveChangesAsync();
        var duplicate = NewDownload("https://EXAMPLE.com/video", first.NormalizedUrl);
        context.Downloads.Add(duplicate);
        await AssertConstraintViolation(() => context.SaveChangesAsync());
    }

    [Test]
    public async Task PersistenceOwnsUrlHashAndRecomputesItForAllSaveMethods()
    {
        await using var context = CreateContext();
        var record = NewDownload();
        record.NormalizedUrlHash = [1, 2, 3];
        context.Downloads.Add(record);
        context.SaveChanges();

        Assert.That(
            record.NormalizedUrlHash,
            Is.EqualTo(DownloadRecord.ComputeNormalizedUrlHash(record.NormalizedUrl))
        );

        record.NormalizedUrl = "https://example.test/changed";
        record.NormalizedUrlHash = new byte[32];
        await context.SaveChangesAsync();
        await using var reopened = CreateContext();
        var saved = await reopened.Downloads.SingleAsync();
        Assert.That(saved.NormalizedUrlHash, Is.EqualTo(DownloadRecord.ComputeNormalizedUrlHash(saved.NormalizedUrl)));
        Assert.That(saved.NormalizedUrlHash, Has.Length.EqualTo(32));
        Assert.That(saved.CreatedAt.Offset, Is.EqualTo(TimeSpan.Zero));
    }

    [Test]
    public async Task SimultaneousDuplicateUrlsCannotCreateTwoDownloads()
    {
        using var barrier = new Barrier(2);

        var outcomes = await Task.WhenAll(
                           Enumerable.Range(0, 2)
                                     .Select(_ => Task.Run(() =>
                                                 {
                                                     using var context = CreateContext();
                                                     context.Downloads.Add(NewDownload());

                                                     Assert.That(
                                                         barrier.SignalAndWait(TimeSpan.FromSeconds(10)),
                                                         Is.True
                                                     );

                                                     try
                                                     {
                                                         context.SaveChanges();

                                                         return true;
                                                     }
                                                     catch (DbUpdateException error) when
                                                         (error.InnerException is PostgresException
                                                          {
                                                              SqlState: PostgresErrorCodes.UniqueViolation
                                                          })
                                                     {
                                                         return false;
                                                     }
                                                 }
                                             )
                                      )
                       );

        Assert.That(outcomes.Count(success => success), Is.EqualTo(1));
        await using var reopened = CreateContext();
        Assert.That(await reopened.Downloads.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task AttemptNumbersAreUniqueWithinADownload()
    {
        await using var context = CreateContext();
        var record = await AddDownloadAsync(context);
        var first = await context.Attempts.SingleAsync();
        first.Status = AttemptStatus.Failed;
        await context.SaveChangesAsync();
        context.Attempts.Add(NewAttempt(record, id: Guid.NewGuid()));
        await AssertConstraintViolation(() => context.SaveChangesAsync());
    }

    [TestCase(AttemptStatus.Pending)]
    [TestCase(AttemptStatus.Running)]
    public async Task ASecondActiveAttemptIsRejected(AttemptStatus status)
    {
        await using var context = CreateContext();
        var record = await AddDownloadAsync(context);
        context.Attempts.Add(NewAttempt(record, number: 2, id: Guid.NewGuid(), status: status));
        await AssertConstraintViolation(() => context.SaveChangesAsync());
    }

    [Test]
    public async Task SimultaneousAttemptsCannotBothBecomeActive()
    {
        DownloadRecord record;

        await using (var context = CreateContext())
        {
            record = await AddDownloadAsync(context);
            (await context.Attempts.SingleAsync()).Status = AttemptStatus.Failed;
            await context.SaveChangesAsync();
        }

        using var barrier = new Barrier(2);

        var outcomes = await Task.WhenAll(
                           Enumerable.Range(2, 2)
                                     .Select(number => Task.Run(() =>
                                                 {
                                                     using var context = CreateContext();
                                                     context.Attempts.Add(NewAttempt(record, number, Guid.NewGuid()));

                                                     Assert.That(
                                                         barrier.SignalAndWait(TimeSpan.FromSeconds(10)),
                                                         Is.True
                                                     );

                                                     try
                                                     {
                                                         context.SaveChanges();

                                                         return true;
                                                     }
                                                     catch (DbUpdateException error) when
                                                         (error.InnerException is PostgresException
                                                          {
                                                              SqlState: PostgresErrorCodes.UniqueViolation
                                                          })
                                                     {
                                                         return false;
                                                     }
                                                 }
                                             )
                                      )
                       );

        Assert.That(outcomes.Count(success => success), Is.EqualTo(1));
        await using var reopened = CreateContext();
        Assert.That(await reopened.Attempts.CountAsync(), Is.EqualTo(2));
    }

    [Test]
    public async Task PersistedQueueCanBeOrderedByTimestampAndId()
    {
        Guid[] ids =
        [
            Guid.Parse("10000000-0000-0000-0000-000000000001"), Guid.Parse("10000000-0000-0000-0000-000000000002"),
            Guid.Parse("10000000-0000-0000-0000-000000000003")
        ];

        await using (var context = CreateContext())
        {
            for (var index = 2; index >= 0; index--)
            {
                var record = NewDownload($"https://example.com/{index}", attemptId: ids[index]);
                context.Downloads.Add(record);
                var attempt = NewAttempt(record);
                attempt.QueuedAt = index == 2 ? _createdAt.AddMinutes(1) : _createdAt;
                context.Attempts.Add(attempt);
            }

            await context.SaveChangesAsync();
        }

        await using var reopened = CreateContext();

        var queue = await reopened.Attempts.Where(attempt => attempt.Status == AttemptStatus.Pending)
                                  .OrderBy(attempt => attempt.QueuedAt)
                                  .ThenBy(attempt => attempt.Id)
                                  .Select(attempt => attempt.Id)
                                  .ToArrayAsync();

        Assert.That(queue, Is.EqualTo(ids));
    }

    [Test]
    public async Task VersionAdvancesForBothSaveMethodsButNotForUnchangedRecords()
    {
        await using var context = CreateContext();
        var record = await AddDownloadAsync(context);
        Assert.That(record.Version, Is.EqualTo(1));
        record.Title = "Changed";
        context.SaveChanges();
        Assert.That(record.Version, Is.EqualTo(2));
        await context.SaveChangesAsync();
        Assert.That(record.Version, Is.EqualTo(2));
        record.Title = "Changed again";
        await context.SaveChangesAsync();
        Assert.That(record.Version, Is.EqualTo(3));
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task ConcurrentUploadAndCancelTransitionsUseTheSavedVersion(bool cancellationWins)
    {
        Guid downloadId;

        await using (var seed = CreateContext())
        {
            var record = NewDownload();
            var aggregate = record.RestoreAggregate();
            aggregate.Apply(new StartAttempt(aggregate.AttemptId));
            aggregate.Apply(new StageSucceeded(aggregate.AttemptId, DownloadState.Downloading));
            aggregate.Apply(new StageSucceeded(aggregate.AttemptId, DownloadState.Preparing));
            record.Capture(aggregate, DownloadIntent.ExecuteStage, _createdAt);
            seed.Downloads.Add(record);
            seed.Attempts.Add(NewAttempt(record));
            await seed.SaveChangesAsync();
            downloadId = record.Id;
        }

        await using var first = CreateContext();
        await using var second = CreateContext();
        var winner = await first.Downloads.SingleAsync();
        var loser = await second.Downloads.SingleAsync();
        var winningAggregate = winner.RestoreAggregate();
        var losingAggregate = loser.RestoreAggregate();
        DownloadEvent cancel = new RequestCancel();

        DownloadEvent uploaded = new StageSucceeded(
            winner.CurrentAttemptId,
            DownloadState.Uploading,
            PublicationConfirmed: true
        );

        var winningTransition = winningAggregate.Apply(cancellationWins ? cancel : uploaded);
        var losingTransition = losingAggregate.Apply(cancellationWins ? uploaded : cancel);
        Assert.That(winningTransition.IsAccepted && losingTransition.IsAccepted, Is.True);
        winner.Capture(winningAggregate, winningTransition.Intent, _createdAt.AddSeconds(1));
        loser.Capture(losingAggregate, losingTransition.Intent, _createdAt.AddSeconds(1));

        second.Stages.Add(
            new StageExecutionRecord
            {
                Id = Guid.NewGuid(),
                DownloadId = downloadId,
                AttemptId = loser.CurrentAttemptId,
                Stage = ProcessingStage.Upload,
                Status = StageStatus.Succeeded
            }
        );

        await first.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());

        await using var reopened = CreateContext();
        var persisted = await reopened.Downloads.SingleAsync();
        Assert.That(persisted.Version, Is.EqualTo(2));
        Assert.That(persisted.State, Is.EqualTo(cancellationWins ? DownloadState.Canceling : DownloadState.CleaningUp));
        Assert.That(await reopened.Stages.CountAsync(), Is.Zero);
        Assert.That(persisted.RestoreAggregate().Apply(cancellationWins ? uploaded : cancel).IsAccepted, Is.False);
    }

    [Test]
    public async Task RetryAndNewAttemptCanBeSavedAtomicallyWithoutLosingHistory()
    {
        Guid downloadId;

        await using (var context = CreateContext())
        {
            var record = await AddDownloadAsync(context);
            downloadId = record.Id;
            var oldAttempt = await context.Attempts.SingleAsync();
            await using var transaction = await context.Database.BeginTransactionAsync();
            oldAttempt.Status = AttemptStatus.Failed;
            oldAttempt.ErrorCode = "interrupted";
            await context.SaveChangesAsync();
            var aggregate = record.RestoreAggregate();
            aggregate.Apply(new StartAttempt(aggregate.AttemptId));
            aggregate.Apply(new ProcessInterrupted(aggregate.AttemptId, DownloadState.Downloading));
            aggregate.Apply(new Retry(Guid.NewGuid()));
            record.Capture(aggregate, DownloadIntent.EnqueueAttempt, _createdAt.AddMinutes(1));
            context.Attempts.Add(NewAttempt(record, number: 2));
            await context.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        await using var reopened = CreateContext();
        Assert.That((await reopened.Downloads.SingleAsync()).Id, Is.EqualTo(downloadId));
        var attempts = await reopened.Attempts.OrderBy(attempt => attempt.Number).ToArrayAsync();
        Assert.That(attempts, Has.Length.EqualTo(2));
        Assert.That(attempts[0].ErrorCode, Is.EqualTo("interrupted"));
        Assert.That(attempts[1].Status, Is.EqualTo(AttemptStatus.Pending));
    }

    [Test]
    public async Task RollbackDoesNotLeaveANewAttemptOrChangedLifecycle()
    {
        Guid downloadId;

        await using (var context = CreateContext())
        {
            var record = await AddDownloadAsync(context);
            downloadId = record.Id;
            await using var transaction = await context.Database.BeginTransactionAsync();
            (await context.Attempts.SingleAsync()).Status = AttemptStatus.Failed;
            await context.SaveChangesAsync();
            var aggregate = record.RestoreAggregate();
            aggregate.Apply(new StartAttempt(aggregate.AttemptId));
            var transition = aggregate.Apply(new RequestCancel());
            record.Capture(aggregate, transition.Intent, _createdAt.AddMinutes(1));
            context.Attempts.Add(NewAttempt(record, number: 2, id: Guid.NewGuid()));
            await context.SaveChangesAsync();
            await transaction.RollbackAsync();
        }

        await using var reopened = CreateContext();
        var saved = await reopened.Downloads.SingleAsync();
        Assert.That(saved.Id, Is.EqualTo(downloadId));
        Assert.That(saved.State, Is.EqualTo(DownloadState.Queued));
        Assert.That(saved.Version, Is.EqualTo(1));
        Assert.That(await reopened.Attempts.CountAsync(), Is.EqualTo(1));
        Assert.That((await reopened.Attempts.SingleAsync()).Status, Is.EqualTo(AttemptStatus.Pending));
    }

    [Test]
    public async Task TransferCannotUseAnotherDownloadsArtifact()
    {
        await using var context = CreateContext();
        var first = await AddDownloadAsync(context);
        var second = await AddDownloadAsync(context, "https://example.com/second");

        var artifact = new DownloadArtifactRecord
        {
            Id = Guid.NewGuid(),
            DownloadId = first.Id,
            WorkspaceGeneration = first.WorkspaceGeneration,
            Kind = ArtifactKind.Prepared,
            RelativePath = "result.mp4",
            IsReady = true
        };

        context.Artifacts.Add(artifact);
        await context.SaveChangesAsync();

        context.Transfers.Add(
            new StorageTransferRecord
            {
                Id = Guid.NewGuid(),
                DownloadId = second.Id,
                ArtifactId = artifact.Id,
                PublicationId = second.PublicationId,
                ObjectKey = "result.mp4",
                ExpectedSize = 0,
                CreatedAt = _createdAt,
                UpdatedAt = _createdAt
            }
        );

        await AssertConstraintViolation(() => context.SaveChangesAsync());
    }

    [Test]
    public async Task StageCannotReferenceAnotherDownloadsAttempt()
    {
        await using var context = CreateContext();
        var first = await AddDownloadAsync(context);
        var second = await AddDownloadAsync(context, "https://example.com/second");

        context.Stages.Add(
            new StageExecutionRecord
            {
                Id = Guid.NewGuid(),
                DownloadId = second.Id,
                AttemptId = first.CurrentAttemptId,
                Stage = ProcessingStage.Download,
                Status = StageStatus.Pending
            }
        );

        await AssertConstraintViolation(() => context.SaveChangesAsync());
    }

    [Test]
    public async Task DeletingADownloadCannotCascadeAndEraseHistory()
    {
        await using var context = CreateContext();
        var record = await AddDownloadAsync(context);
        context.ChangeTracker.Clear();
        context.Downloads.Remove(record);
        await AssertConstraintViolation(() => context.SaveChangesAsync());
    }

    [TestCase(-1)]
    [TestCase(101)]
    public async Task InvalidStageProgressIsRejected(double progress)
    {
        await using var context = CreateContext();
        var record = await AddDownloadAsync(context);

        context.Stages.Add(
            new StageExecutionRecord
            {
                Id = Guid.NewGuid(),
                DownloadId = record.Id,
                AttemptId = record.CurrentAttemptId,
                Stage = ProcessingStage.Download,
                Status = StageStatus.Running,
                Progress = progress
            }
        );

        await AssertConstraintViolation(() => context.SaveChangesAsync());
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("Data Source=:memory:")]
    [TestCase("Data Source=shared;Mode=Memory")]
    public void RegistrationRejectsInvalidDatabaseConfiguration(string? connectionString)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                                                           new Dictionary<string, string?>
                                                           {
                                                               ["ConnectionStrings:AppDatabase"] = connectionString
                                                           }
                                                       )
                                                      .Build();

        var services = new ServiceCollection();
        services.AddAppDatabase(configuration);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RegistrationUsesTheConfiguredPostgreSqlDatabase(bool pooling)
    {
        var connection = new NpgsqlConnectionStringBuilder(_database.ConnectionString) { Pooling = pooling };
        var services = new ServiceCollection();
        services.AddAppDatabase(CreateConfiguration(connection.ToString()));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await context.InitializeAsync();
        Assert.That(await ScalarAsync(context, "SELECT current_database();"), Is.EqualTo(_database.Name));
        using var pooled = new NpgsqlConnection(connection.ToString());
        NpgsqlConnection.ClearPool(pooled);
    }

    private AppDbContext CreateContext() => new(_provider.GetRequiredService<DbContextOptions<AppDbContext>>());

    private static IConfiguration CreateConfiguration(string connectionString) =>
        new ConfigurationBuilder().AddInMemoryCollection(
                                       new Dictionary<string, string?>
                                       {
                                           ["ConnectionStrings:AppDatabase"] =
                                               connectionString
                                       }
                                   )
                                  .Build();

    private static DownloadRecord NewDownload(
        string url = "https://example.com/video",
        string? normalizedUrl = null,
        Guid? attemptId = null
    ) =>
        DownloadRecord.Create(
            Download.Create(Guid.NewGuid(), attemptId ?? Guid.NewGuid(), Guid.NewGuid()),
            url,
            normalizedUrl ?? url,
            Guid.NewGuid(),
            _createdAt
        );

    private static DownloadAttemptRecord
        NewAttempt(DownloadRecord record, int number = 1, Guid? id = null, AttemptStatus? status = null) => new()
    {
        Id = id ?? record.CurrentAttemptId,
        DownloadId = record.Id,
        Number = number,
        Reason = number == 1 ? AttemptReason.Initial : AttemptReason.Retry,
        InitialState = record.ResumeState ?? DownloadState.Downloading,
        Status = status ?? record.AttemptStatus,
        QueuedAt = _createdAt
    };

    private static async Task<DownloadRecord> AddDownloadAsync(
        AppDbContext context,
        string url = "https://example.com/video"
    )
    {
        var record = NewDownload(url);
        context.Downloads.Add(record);
        context.Attempts.Add(NewAttempt(record));
        await context.SaveChangesAsync();

        return record;
    }

    private static async Task AssertConstraintViolation(Func<Task<int>> action)
    {
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => action());
        Assert.That(error!.InnerException, Is.TypeOf<PostgresException>());
        Assert.That(((PostgresException)error.InnerException!).SqlState, Does.StartWith("23"));
    }

    private static async Task<object?> ScalarAsync(AppDbContext context, string sql)
    {
        await context.Database.OpenConnectionAsync();

        try
        {
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = sql;

            return await command.ExecuteScalarAsync();
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }
}
