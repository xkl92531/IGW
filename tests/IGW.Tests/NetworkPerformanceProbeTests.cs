using System.Collections.Concurrent;
using System.Net;
using IGW.Core;
using Microsoft.Extensions.Logging;
using Xunit;

namespace IGW.Tests;

public sealed class NetworkPerformanceProbeTests
{
    private static readonly NetworkProbeKey Key = new("192.0.2.10", "192.0.2.20", true);

    [Fact]
    public async Task Same_endpoint_leases_share_one_worker_regardless_of_route_metadata()
    {
        var time = new ProbeTime();
        var echo = new Echo();
        using var coordinator = new NetworkPerformanceProbeCoordinator(echo, time);
        using var first = coordinator.Acquire(Key);
        using var second = coordinator.Acquire(Key with { RouteVerified = false });
        await Until(() => first.Snapshot().Count == 1);
        Assert.Single(echo.Calls);
        Assert.Equal(first.Snapshot(), second.Snapshot());
        first.Dispose();
        time.Advance(TimeSpan.FromSeconds(5));
        await Until(() => second.Snapshot().Count == 2);
        Assert.Equal(2, echo.Calls.Count);
    }

    [Fact]
    public async Task Different_remote_or_source_creates_distinct_workers_and_preserves_source()
    {
        var echo = new Echo();
        using var coordinator = new NetworkPerformanceProbeCoordinator(echo, new ProbeTime());
        using var first = coordinator.Acquire(Key);
        using var second = coordinator.Acquire(Key with { RemoteAddress = "192.0.2.11" });
        using var third = coordinator.Acquire(Key with { SourceAddress = "192.0.2.21" });
        await Until(() => first.Snapshot().Count + second.Snapshot().Count + third.Snapshot().Count == 3);
        Assert.Contains(echo.Calls, c => c.Remote.ToString() == "192.0.2.10" && c.Source!.ToString() == "192.0.2.20");
        Assert.Contains(echo.Calls, c => c.Remote.ToString() == "192.0.2.11" && c.Source!.ToString() == "192.0.2.20");
        Assert.Contains(echo.Calls, c => c.Remote.ToString() == "192.0.2.10" && c.Source!.ToString() == "192.0.2.21");
    }

    [Fact]
    public async Task Default_cadence_is_five_seconds_and_timeout_one_second()
    {
        var time = new ProbeTime();
        var echo = new Echo();
        using var coordinator = new NetworkPerformanceProbeCoordinator(echo, time);
        using var lease = coordinator.Acquire(Key);
        await Until(() => lease.Snapshot().Count == 1);
        time.Advance(TimeSpan.FromMilliseconds(4999));
        Assert.Single(echo.Calls);
        time.Advance(TimeSpan.FromMilliseconds(1));
        await Until(() => lease.Snapshot().Count == 2);
        Assert.All(echo.Calls, c => Assert.Equal(TimeSpan.FromSeconds(1), c.Timeout));
    }

    [Fact]
    public async Task Last_dispose_stops_samples_and_reacquire_starts_fresh_history()
    {
        var time = new ProbeTime();
        var echo = new Echo();
        using var coordinator = new NetworkPerformanceProbeCoordinator(echo, time);
        var lease = coordinator.Acquire(Key);
        await Until(() => lease.Snapshot().Count == 1);
        lease.Dispose();
        await lease.Completion;
        var count = lease.Snapshot().Count;
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(count, lease.Snapshot().Count);
        Assert.Single(echo.Calls);
        using var next = coordinator.Acquire(Key);
        await Until(() => next.Snapshot().Count == 1);
        Assert.Equal(2, echo.Calls.Count);
    }

    [Fact]
    public async Task Timeout_unavailable_exception_and_recovery_are_evidence_not_owner_errors()
    {
        var time = new ProbeTime();
        var echo = new Echo();
        echo.Results.Enqueue(() => Task.FromResult(new NetworkEchoResult(false, null, "timeout", "ICMP timed out", true)));
        echo.Results.Enqueue(() => throw new InvalidOperationException("ICMP unavailable"));
        using var coordinator = new NetworkPerformanceProbeCoordinator(echo, time);
        using var lease = coordinator.Acquire(Key);
        await Until(() => lease.Snapshot().Count == 1);
        await Step(time, lease, 2);
        await Step(time, lease, 3);
        Assert.Equal(new[] { "timeout", "unavailable", "success" }, lease.Snapshot().Select(s => s.Status));
        Assert.Equal("ICMP unavailable", lease.Snapshot()[1].Error);
        Assert.Equal("unavailable", lease.Window(time.GetUtcNow(), TimeSpan.FromSeconds(10)).Status);
    }

    [Fact]
    public async Task Host_cancellation_records_inflight_cancellation_and_stops_worker()
    {
        using var host = new CancellationTokenSource();
        var time = new ProbeTime();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var echo = new Echo { Handler = async token => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return Success(); } };
        using var coordinator = new NetworkPerformanceProbeCoordinator(echo, time, cancellationToken: host.Token);
        using var lease = coordinator.Acquire(Key);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        host.Cancel();
        await lease.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("cancelled", Assert.Single(lease.Snapshot()).Status);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Single(echo.Calls);
    }

    [Fact]
    public async Task Reacquire_during_native_call_shutdown_never_overlaps_the_old_echo()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var echo = new Echo
        {
            Handler = async token =>
            {
                entered.TrySetResult();
                using var registration = token.Register(() => cancelled.TrySetResult());
                await finish.Task;
                return Success();
            }
        };
        using var coordinator = new NetworkPerformanceProbeCoordinator(echo, new ProbeTime());
        var first = coordinator.Acquire(Key);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposing = Task.Run(first.Dispose);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var second = coordinator.Acquire(Key);
        Assert.Single(echo.Calls);
        Assert.False(disposing.IsCompleted);
        finish.TrySetResult();
        await disposing.WaitAsync(TimeSpan.FromSeconds(5));
        await Until(() => second.Snapshot().Count == 1);
        Assert.Equal(2, echo.Calls.Count);
        Assert.Equal("cancelled", Assert.Single(first.Snapshot()).Status);
    }

    [Fact]
    public async Task Invalid_source_is_unavailable_without_sending_via_the_default_route()
    {
        var echo = new Echo();
        var time = new ProbeTime();
        using var coordinator = new NetworkPerformanceProbeCoordinator(echo, time);
        using var lease = coordinator.Acquire(Key with { SourceAddress = "invalid-source" });
        await Until(() => lease.Snapshot().Count == 1);
        Assert.Empty(echo.Calls);
        Assert.Equal("unavailable", Assert.Single(lease.Snapshot()).Status);
        Assert.Contains("source", lease.Snapshot()[0].Error);
        Assert.False(lease.Snapshot()[0].RouteVerified);
    }

    [Fact]
    public async Task Host_concurrency_cap_limits_echoes_and_cancellation_releases_a_slot()
    {
        using var host = new CancellationTokenSource();
        int active = 0;
        int maximum = 0;
        var echo = new Echo
        {
            Handler = async token =>
            {
                int current = Interlocked.Increment(ref active);
                Interlocked.Exchange(ref maximum, Math.Max(Volatile.Read(ref maximum), current));
                try { await Task.Delay(Timeout.Infinite, token); return Success(); }
                finally { Interlocked.Decrement(ref active); }
            }
        };
        using var coordinator = new NetworkPerformanceProbeCoordinator(echo, new ProbeTime(),
            cancellationToken: host.Token, maxConcurrentProbes: 2);
        using var first = coordinator.Acquire(Key);
        using var second = coordinator.Acquire(Key with { RemoteAddress = "192.0.2.11" });
        await Until(() => Volatile.Read(ref active) == 2);
        using var third = coordinator.Acquire(Key with { RemoteAddress = "192.0.2.12" });
        Assert.Equal(2, echo.Calls.Count);
        first.Dispose();
        await Until(() => echo.Calls.Count == 3);
        host.Cancel();
        await Task.WhenAll(first.Completion, second.Completion, third.Completion).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, maximum);
        Assert.Equal(0, active);
        Assert.All(new[] { first, second, third }, lease => Assert.Equal("cancelled", Assert.Single(lease.Snapshot()).Status));
    }

    [Fact]
    public async Task History_is_bounded_and_snapshots_do_not_change_after_capture()
    {
        var time = new ProbeTime();
        using var coordinator = new NetworkPerformanceProbeCoordinator(new Echo(), time);
        using var lease = coordinator.Acquire(Key);
        await Until(() => lease.Snapshot().Count == 1);
        var snapshot = lease.Snapshot();
        for (var i = 2; i <= 125; i++)
        {
            time.Advance(TimeSpan.FromSeconds(5));
            await Until(() => lease.Snapshot().Last().CapturedAt == time.GetUtcNow());
        }
        Assert.Single(snapshot);
        Assert.Equal(120, lease.Snapshot().Count);
        Assert.Equal(time.Start.AddSeconds(25), lease.Snapshot()[0].CapturedAt);
        Assert.Throws<NotSupportedException>(() => ((IList<NetworkProbeSample>)snapshot).Clear());
    }

    [Fact]
    public async Task Window_filters_range_inclusively_and_uses_only_prior_successful_baseline()
    {
        var time = new ProbeTime();
        var echo = new Echo();
        foreach (double rtt in new[] { 10d, 20, 30, 40, 50 })
            echo.Results.Enqueue(() => Task.FromResult(Success(rtt)));
        using var coordinator = new NetworkPerformanceProbeCoordinator(echo, time);
        using var lease = coordinator.Acquire(Key);
        await Until(() => lease.Snapshot().Count == 1);
        for (var i = 2; i <= 5; i++) await Step(time, lease, i);
        var window = lease.Window(time.Start.AddSeconds(10), TimeSpan.FromSeconds(5));
        Assert.Equal(3, window.Count);
        Assert.Equal(3, window.SuccessCount);
        Assert.Equal(0, window.FailedCount);
        Assert.Equal(40, window.LatestRttMs);
        Assert.Equal(40, window.P95RttMs);
        Assert.Equal(10, window.BaselineP95RttMs);
        Assert.Equal(0, window.LossPercent);
        Assert.Equal(window, lease.Window(time.Start.AddSeconds(5), time.Start.AddSeconds(15)));
        Assert.Throws<ArgumentOutOfRangeException>(() => lease.Window(time.Start.AddSeconds(5), time.Start));
        Assert.Null(lease.Window(time.Start, TimeSpan.Zero).BaselineP95RttMs);
        Assert.Equal("unavailable", lease.Window(time.Start.AddDays(1), TimeSpan.Zero).Status);
    }

    [Fact]
    public async Task Partial_timeout_is_loss_but_all_timeouts_are_unavailable()
    {
        var time = new ProbeTime();
        var echo = new Echo();
        echo.Results.Enqueue(() => Task.FromResult(new NetworkEchoResult(false, null, "timeout", "TimedOut", true)));
        using var coordinator = new NetworkPerformanceProbeCoordinator(echo, time);
        using var lease = coordinator.Acquire(Key);
        await Until(() => lease.Snapshot().Count == 1);
        Assert.Equal("unavailable", lease.Window(time.Start, TimeSpan.Zero).Status);
        await Step(time, lease, 2);
        var window = lease.Window(time.Start, TimeSpan.FromSeconds(5));
        Assert.Equal("degraded", window.Status);
        Assert.Equal(2, window.Count);
        Assert.Equal(1, window.FailedCount);
        Assert.Equal(50, window.LossPercent);
    }

    [Fact]
    public async Task Unverified_echo_never_claims_verified_route_even_when_key_does()
    {
        var echo = new Echo { Handler = _ => Task.FromResult(Success() with { RouteVerified = false }) };
        var time = new ProbeTime();
        using var coordinator = new NetworkPerformanceProbeCoordinator(echo, time);
        using var lease = coordinator.Acquire(Key);
        await Until(() => lease.Snapshot().Count == 1);
        Assert.False(Assert.Single(lease.Snapshot()).RouteVerified);
        Assert.False(lease.Window(time.Start, TimeSpan.Zero).RouteVerified);
    }

    [Fact]
    public async Task Logging_merges_continuing_failures_for_thirty_seconds_and_logs_recovery()
    {
        var time = new ProbeTime();
        var logger = new ProbeLogger();
        var echo = new Echo();
        echo.Results.Enqueue(() => Task.FromResult(Success()));
        for (int i = 0; i < 7; i++)
            echo.Results.Enqueue(() => Task.FromResult(new NetworkEchoResult(false, null, "timeout", "TimedOut", true)));
        using var coordinator = new NetworkPerformanceProbeCoordinator(echo, time, logger);
        using var lease = coordinator.Acquire(Key);
        await Until(() => lease.Snapshot().Count == 1);
        for (int i = 2; i <= 9; i++) await Step(time, lease, i);
        await Until(() => logger.Messages.Count == 3);
        Assert.Equal(new[] { LogLevel.Warning, LogLevel.Warning, LogLevel.Information }, logger.Messages.Select(m => m.Level));
        Assert.All(logger.Messages, m => { Assert.Contains("192.0.2.10", m.Text); Assert.Contains("192.0.2.20", m.Text); });
        Assert.Contains("TimedOut", logger.Messages.First().Text);
    }

    [Fact]
    public void Native_reply_parser_rejects_short_and_inconsistent_buffers_and_maps_status()
    {
        Assert.Equal("unavailable", SourceBoundIcmpEchoClient.ParseReply(new byte[12], 1).Status);
        var bytes = new byte[80];
        Assert.Equal("unavailable", SourceBoundIcmpEchoClient.ParseReply(bytes, 100).Status);
        BitConverter.GetBytes((ushort)100).CopyTo(bytes, 12);
        Assert.Equal("unavailable", SourceBoundIcmpEchoClient.ParseReply(bytes, 1).Status);
        BitConverter.GetBytes((ushort)0).CopyTo(bytes, 12);
        BitConverter.GetBytes(11010u).CopyTo(bytes, 4);
        Assert.Equal("timeout", SourceBoundIcmpEchoClient.ParseReply(bytes, 1).Status);
        BitConverter.GetBytes(0u).CopyTo(bytes, 4);
        BitConverter.GetBytes(12u).CopyTo(bytes, 8);
        Assert.Equal(12, SourceBoundIcmpEchoClient.ParseReply(bytes, 1).RttMs);
    }

    private static NetworkEchoResult Success(double rtt = 2) => new(true, rtt, "success", null, true);

    private static async Task Step(ProbeTime time, INetworkProbe lease, int count)
    {
        time.Advance(TimeSpan.FromSeconds(5));
        await Until(() => lease.Snapshot().Count == count);
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) { timeout.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
    }

    private sealed class Echo : INetworkEchoClient
    {
        public ConcurrentQueue<(IPAddress Remote, IPAddress? Source, TimeSpan Timeout)> Calls { get; } = new();
        public ConcurrentQueue<Func<Task<NetworkEchoResult>>> Results { get; } = new();
        public Func<CancellationToken, Task<NetworkEchoResult>>? Handler { get; init; }
        public Task<NetworkEchoResult> SendAsync(IPAddress remote, IPAddress? source, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls.Enqueue((remote, source, timeout));
            return Handler?.Invoke(cancellationToken) ?? (Results.TryDequeue(out var result) ? result() : Task.FromResult(Success()));
        }
    }

    private sealed class ProbeLogger : ILogger<NetworkPerformanceProbeCoordinator>
    {
        public ConcurrentQueue<(LogLevel Level, string Text)> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Enqueue((logLevel, formatter(state, exception)));
    }

    private sealed class ProbeTime : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ProbeTimer> _timers = [];
        private long _ticks;
        public DateTimeOffset Start { get; } = new(2026, 9, 7, 8, 0, 0, TimeSpan.Zero);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (_gate) return _ticks; }
        public override DateTimeOffset GetUtcNow() => Start.AddTicks(GetTimestamp());
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                var timer = new ProbeTimer(this, callback, state);
                _timers.Add(timer);
                timer.Change(dueTime, period);
                return timer;
            }
        }
        public void Advance(TimeSpan amount)
        {
            List<Action> callbacks = [];
            lock (_gate)
            {
                _ticks += amount.Ticks;
                foreach (var timer in _timers.ToArray())
                {
                    if (timer.Next > _ticks) continue;
                    timer.Next = timer.Period > 0 ? _ticks + timer.Period : long.MaxValue;
                    callbacks.Add(timer.Fire);
                }
            }
            foreach (var callback in callbacks) callback();
        }
        private sealed class ProbeTimer(ProbeTime owner, TimerCallback callback, object? state) : ITimer
        {
            public long Next { get; set; }
            public long Period { get; private set; }
            public void Fire() => callback(state);
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._gate)
                {
                    Next = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner._ticks + dueTime.Ticks;
                    Period = period.Ticks;
                    return true;
                }
            }
            public void Dispose() { lock (owner._gate) owner._timers.Remove(this); }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
