using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Ytdlp.Ui.Domain.Primitives;
using Ytdlp.Ui.Infrastructure.Persistence.Entities;

namespace Ytdlp.Ui.IntegrationTests.Tools;

public sealed class TransitionSaveGate : SaveChangesInterceptor
{
    public DownloadState Target { get; set; }
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _used;

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default
    )
    {
        if (eventData.Context!.ChangeTracker.Entries<DownloadRecord>()
                     .Any(entry => entry.State == EntityState.Modified && entry.Entity.State == Target)
         && Interlocked.CompareExchange(ref _used, 1, 0) == 0)
        {
            Entered.TrySetResult();
            await Released.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        }

        return result;
    }
}
