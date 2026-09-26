namespace DualNet.Core.Services;

public static class WatchdogCoordinator
{
    public static async Task<bool> WaitAndRecoverAsync(
        SnapshotStore store,
        Guid operationId,
        TimeSpan timeout,
        Func<Task> restore,
        Func<TimeSpan, Task>? delay = null)
    {
        await (delay ?? Task.Delay)(timeout).ConfigureAwait(false);
        if (store.IsWatchdogConfirmed(operationId))
        {
            store.ClearWatchdog(operationId);
            return false;
        }
        if (!store.IsWatchdogArmed(operationId) || !store.HasBaseline) return false;
        try
        {
            await restore().ConfigureAwait(false);
            return true;
        }
        finally
        {
            store.ClearWatchdog(operationId);
        }
    }
}
