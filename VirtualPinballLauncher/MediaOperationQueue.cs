namespace VirtualPinballLauncher;

// Each player has its own queue: native calls stay ordered without blocking WPF.
internal sealed class MediaOperationQueue
{
    private readonly object sync = new();
    private Task pending = Task.CompletedTask;
    private bool completed;

    public Task Enqueue(Action action, CancellationToken token = default)
    {
        lock (sync)
            return completed ? Task.CompletedTask : Schedule(action, token);
    }

    public Task CompleteAsync(Action cleanup)
    {
        lock (sync)
        {
            if (completed) return pending;
            completed = true;
            return Schedule(cleanup, CancellationToken.None);
        }
    }

    private Task Schedule(Action action, CancellationToken token)
    {
        var previous = pending;
        return pending = Task.Run(async () =>
        {
            try { await previous.ConfigureAwait(false); }
            catch (Exception) { /* The caller observes failures; later work must still run. */ }

            token.ThrowIfCancellationRequested();
            action();
        });
    }
}
