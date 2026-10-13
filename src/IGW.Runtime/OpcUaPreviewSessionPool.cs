using IGW.Abstractions;

namespace IGW.Runtime;

/// <summary>Short-lived, credential-isolated browse sessions; operations on one session are serialized.</summary>
internal sealed class OpcUaPreviewSessionPool : IAsyncDisposable
{
    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public IDeviceDriver? Driver;
        public int Users;
        public DateTimeOffset LastUsed;

        public async Task CloseAsync()
        {
            await Gate.WaitAsync();
            try
            {
                var driver = Driver;
                Driver = null;
                if (driver is not null) await driver.DisposeAsync();
            }
            finally { Gate.Release(); }
        }
    }

    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly HashSet<Entry> _retiring = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly TimeProvider _time;
    private readonly TimeSpan _idleTime;
    private readonly int _capacity;
    private readonly Task _sweeper;
    private bool _disposed;

    public OpcUaPreviewSessionPool(TimeProvider? time = null, TimeSpan? idleTime = null, int capacity = 16)
    {
        _time = time ?? TimeProvider.System;
        _idleTime = idleTime ?? TimeSpan.FromMinutes(2);
        _capacity = capacity;
        _sweeper = SweepLoopAsync();
    }

    public async Task<T> UseAsync<T>(string key, Func<IDeviceDriver> factory,
        Func<IDeviceDriver, CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Entry entry;
        Entry? retired = null;
        CancellationTokenSource linked;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryGetValue(key, out var existing) && existing.Users == 0 &&
                _time.GetUtcNow() - existing.LastUsed >= _idleTime)
            {
                retired = existing;
                _entries.Remove(key);
            }
            if (!_entries.TryGetValue(key, out entry!))
            {
                if (_entries.Count >= _capacity)
                {
                    var oldest = _entries.Where(pair => pair.Value.Users == 0).OrderBy(pair => pair.Value.LastUsed).FirstOrDefault();
                    if (oldest.Value is null) throw new InvalidOperationException("正在浏览的 PLC 连接过多，请稍后重试。");
                    retired = oldest.Value;
                    _entries.Remove(oldest.Key);
                }
                entry = new Entry();
                _entries.Add(key, entry);
            }
            if (retired is not null) _retiring.Add(retired);
            entry.Users++;
            linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        }
        using (linked)
        {
            bool acquired = false;
            try
            {
                if (retired is not null) await RetireAsync(retired);
                await entry.Gate.WaitAsync(linked.Token);
                acquired = true;
                linked.Token.ThrowIfCancellationRequested();
                entry.Driver ??= factory();
                return await operation(entry.Driver, linked.Token);
            }
            catch
            {
                // A cancelled/failed browse may leave continuation points or an unusable session.
                if (acquired)
                {
                    var driver = entry.Driver;
                    entry.Driver = null;
                    if (driver is not null) { try { await driver.DisposeAsync(); } catch { } }
                }
                throw;
            }
            finally
            {
                if (acquired) entry.Gate.Release();
                lock (_sync)
                {
                    entry.Users--;
                    entry.LastUsed = _time.GetUtcNow();
                }
            }
        }
    }

    private async Task RetireAsync(Entry entry)
    {
        try { await entry.CloseAsync(); }
        catch { /* Closing an idle preview must not fail another PLC's browse. */ }
        finally { lock (_sync) _retiring.Remove(entry); }
    }

    internal async Task SweepAsync()
    {
        Entry[] expired;
        lock (_sync)
        {
            if (_disposed) return;
            var pairs = _entries.Where(pair => pair.Value.Users == 0 &&
                _time.GetUtcNow() - pair.Value.LastUsed >= _idleTime).ToArray();
            expired = pairs.Select(pair => pair.Value).ToArray();
            foreach (var pair in pairs)
            {
                _entries.Remove(pair.Key);
                _retiring.Add(pair.Value);
            }
        }
        await Task.WhenAll(expired.Select(RetireAsync));
    }

    private async Task SweepLoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15), _time);
            while (await timer.WaitForNextTickAsync(_stop.Token)) await SweepAsync();
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        Entry[] entries;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            entries = _entries.Values.Concat(_retiring).Distinct().ToArray();
            _entries.Clear();
        }
        await _stop.CancelAsync();
        await _sweeper;
        await Task.WhenAll(entries.Select(RetireAsync));
        _stop.Dispose();
    }
}
