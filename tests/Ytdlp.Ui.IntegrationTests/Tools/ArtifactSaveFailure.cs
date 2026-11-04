using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Ytdlp.Ui.Domain.Primitives;
using Ytdlp.Ui.Infrastructure.Persistence.Entities;

namespace Ytdlp.Ui.IntegrationTests.Tools;

public sealed class ArtifactSaveFailure : SaveChangesInterceptor
{
    public ArtifactKind? Target { get; set; }
    private int _used;

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default
    )
    {
        if (Target is { } target
         && eventData.Context!.ChangeTracker.Entries<DownloadArtifactRecord>()
                     .Any(entry =>
                              entry.State == EntityState.Modified && entry.Entity.IsReady && entry.Entity.Kind == target
                      )
         && Interlocked.CompareExchange(ref _used, 1, 0) == 0)
            throw new DbUpdateException("Injected failure after final artifact was written.");

        return ValueTask.FromResult(result);
    }
}
