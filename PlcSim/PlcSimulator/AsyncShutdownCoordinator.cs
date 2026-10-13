namespace PlcSimulator;

public sealed class AsyncShutdownCoordinator
{
    private int _started;
    private int _completed;

    public bool IsStarted => Volatile.Read(ref _started) != 0;
    public bool IsCompleted => Volatile.Read(ref _completed) != 0;

    public async Task<bool> RunAsync(Func<Task> cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) return false;

        try
        {
            await cleanup();
        }
        finally
        {
            Volatile.Write(ref _completed, 1);
        }

        return true;
    }
}
