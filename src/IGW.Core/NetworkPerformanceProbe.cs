using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IGW.Core;

public sealed record NetworkProbeKey(string RemoteAddress, string SourceAddress, bool RouteVerified);

public sealed record NetworkEchoResult(bool Success, double? RttMs, string Status, string? Error, bool RouteVerified = false);

public interface INetworkEchoClient
{
    Task<NetworkEchoResult> SendAsync(IPAddress remote, IPAddress? source, TimeSpan timeout, CancellationToken cancellationToken);
}

public interface INetworkProbe : IDisposable
{
    IReadOnlyList<NetworkProbeSample> Snapshot();
    NetworkProbeWindow Window(DateTimeOffset at, TimeSpan radius);
    NetworkProbeWindow Window(DateTimeOffset from, DateTimeOffset to);
    Task Completion { get; }
}

/// <summary>
/// Own one coordinator per host (or use Shared). Echo work runs independently of acquisition.
/// An injected echo client must honor timeout/cancellation; the native client is bounded to one second.
/// </summary>
public sealed class NetworkPerformanceProbeCoordinator : IDisposable, IAsyncDisposable
{
    public static NetworkPerformanceProbeCoordinator Shared { get; } = new();
    private readonly object _gate = new();
    private readonly Dictionary<(string Remote, string Source), Worker> _workers = [];
    private readonly HashSet<Worker> _running = [];
    private readonly INetworkEchoClient _echo;
    private readonly TimeProvider _time;
    private readonly ILogger<NetworkPerformanceProbeCoordinator> _logger;
    private readonly TimeSpan _cadence;
    private readonly TimeSpan _timeout;
    private readonly CancellationToken _hostCancellation;
    private readonly SemaphoreSlim _echoSlots;
    private int _slotsDisposed;
    private bool _disposed;

    public NetworkPerformanceProbeCoordinator(INetworkEchoClient? echoClient = null, TimeProvider? timeProvider = null,
        ILogger<NetworkPerformanceProbeCoordinator>? logger = null, TimeSpan? cadence = null, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default, int maxConcurrentProbes = 8)
    {
        _echo = echoClient ?? new SourceBoundIcmpEchoClient();
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<NetworkPerformanceProbeCoordinator>.Instance;
        _cadence = cadence ?? TimeSpan.FromSeconds(5);
        _timeout = timeout ?? TimeSpan.FromSeconds(1);
        if (_cadence <= TimeSpan.Zero || _cadence.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(cadence));
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromSeconds(1))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrentProbes);
        _echoSlots = new SemaphoreSlim(maxConcurrentProbes, maxConcurrentProbes);
        _hostCancellation = cancellationToken;
    }

    public INetworkProbe Acquire(NetworkProbeKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        key = key with { RemoteAddress = Normalize(key.RemoteAddress), SourceAddress = Normalize(key.SourceAddress) };
        var identity = (key.RemoteAddress, key.SourceAddress);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _workers.TryGetValue(identity, out var previous);
            if (previous is { Stopping: false } && !previous.Completion.IsCompleted)
            {
                previous.References++;
                previous.RestrictRouteVerification(key.RouteVerified);
                return new Lease(this, previous);
            }
            var worker = new Worker(this, key);
            _workers[identity] = worker;
            _running.Add(worker);
            // A reacquire during last-lease disposal must not overlap the old native echo.
            worker.Start(previous?.Completion);
            return new Lease(this, worker);
        }
    }

    private void Release(Worker worker)
    {
        lock (_gate)
        {
            if (--worker.References != 0) return;
            worker.Stopping = true;
        }
        worker.Cancel();
        worker.Completion.GetAwaiter().GetResult();
    }

    private void Finished(Worker worker)
    {
        lock (_gate)
        {
            _running.Remove(worker);
            var identity = (worker.Key.RemoteAddress, worker.Key.SourceAddress);
            if (_workers.TryGetValue(identity, out var current) && ReferenceEquals(current, worker))
                _workers.Remove(identity);
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        Worker[] workers;
        lock (_gate)
        {
            _disposed = true;
            workers = _running.ToArray();
            foreach (var worker in workers) worker.Stopping = true;
        }
        foreach (var worker in workers) worker.Cancel();
        await Task.WhenAll(workers.Select(worker => worker.Completion)).ConfigureAwait(false);
        if (Interlocked.Exchange(ref _slotsDisposed, 1) == 0) _echoSlots.Dispose();
    }

    private static string Normalize(string? address) => IPAddress.TryParse(address, out var parsed)
        ? parsed.ToString() : address?.Trim() ?? "";

    private sealed class Lease(NetworkPerformanceProbeCoordinator owner, Worker worker) : INetworkProbe
    {
        private int _disposed;
        public Task Completion => worker.Completion;
        public IReadOnlyList<NetworkProbeSample> Snapshot() => worker.Snapshot();
        public NetworkProbeWindow Window(DateTimeOffset at, TimeSpan radius) => worker.Window(at, radius);
        public NetworkProbeWindow Window(DateTimeOffset from, DateTimeOffset to) => worker.Window(from, to);
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Release(worker); }
    }

    private sealed class Worker
    {
        private const int Capacity = 120;
        private static readonly TimeSpan FailureMergeInterval = TimeSpan.FromSeconds(30);
        private readonly NetworkPerformanceProbeCoordinator _owner;
        private readonly CancellationTokenSource _cancellation;
        private readonly object _samplesGate = new();
        private readonly Queue<NetworkProbeSample> _samples = new(Capacity);
        private bool _routeVerified;
        private bool _failed;
        private long _lastFailureLog;
        private int _suppressed;
        public NetworkProbeKey Key { get; }
        public int References { get; set; } = 1;
        public bool Stopping { get; set; }
        public Task Completion { get; private set; } = Task.CompletedTask;

        public Worker(NetworkPerformanceProbeCoordinator owner, NetworkProbeKey key)
        {
            _owner = owner;
            Key = key;
            _routeVerified = key.RouteVerified;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(owner._hostCancellation);
        }

        public void RestrictRouteVerification(bool verified)
        {
            if (!verified) Volatile.Write(ref _routeVerified, false);
        }

        public void Start(Task? predecessor) => Completion = Task.Run(async () =>
        {
            try
            {
                if (predecessor is not null) await predecessor.ConfigureAwait(false);
                await RunAsync().ConfigureAwait(false);
            }
            finally
            {
                _owner.Finished(this);
                _cancellation.Dispose();
            }
        });

        public void Cancel()
        {
            try { _cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        private async Task RunAsync()
        {
            var token = _cancellation.Token;
            using var timer = new PeriodicTimer(_owner._cadence, _owner._time);
            while (!token.IsCancellationRequested)
            {
                NetworkEchoResult result;
                try
                {
                    if (!IPAddress.TryParse(Key.RemoteAddress, out var remote) || remote.AddressFamily != AddressFamily.InterNetwork)
                        result = new(false, null, "unavailable", "Remote address is not an IPv4 address");
                    else if (Key.SourceAddress.Length > 0 && (!IPAddress.TryParse(Key.SourceAddress, out var local) || local.AddressFamily != AddressFamily.InterNetwork))
                        result = new(false, null, "unavailable", "Configured source address is not an IPv4 address");
                    else
                    {
                        IPAddress.TryParse(Key.SourceAddress, out var source);
                        await _owner._echoSlots.WaitAsync(token).ConfigureAwait(false);
                        try { result = await _owner._echo.SendAsync(remote, source, _owner._timeout, token).ConfigureAwait(false); }
                        finally { _owner._echoSlots.Release(); }
                    }
                    if (token.IsCancellationRequested) result = new(false, null, "cancelled", "Network probe cancelled");
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    result = new(false, null, "cancelled", "Network probe cancelled");
                }
                catch (Exception error)
                {
                    result = new(false, null, "unavailable", error.Message);
                }

                var validRtt = result.RttMs is double rtt && double.IsFinite(rtt) && rtt >= 0;
                var status = result.Success && validRtt ? "success"
                    : result.Status is "timeout" or "cancelled" ? result.Status : "unavailable";
                var sample = new NetworkProbeSample(_owner._time.GetUtcNow(), status, status == "success" ? result.RttMs : null,
                    result.Error, Key.SourceAddress, Key.RemoteAddress,
                    Volatile.Read(ref _routeVerified) && result.RouteVerified && Key.SourceAddress.Length > 0 && Key.SourceAddress != "0.0.0.0");
                LogTransition(sample);
                lock (_samplesGate)
                {
                    if (_samples.Count == Capacity) _samples.Dequeue();
                    _samples.Enqueue(sample);
                }
                if (token.IsCancellationRequested) break;
                try { if (!await timer.WaitForNextTickAsync(token).ConfigureAwait(false)) break; }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            }
        }

        public IReadOnlyList<NetworkProbeSample> Snapshot()
        {
            lock (_samplesGate) return Array.AsReadOnly(_samples.ToArray());
        }

        public NetworkProbeWindow Window(DateTimeOffset at, TimeSpan radius)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(radius, TimeSpan.Zero);
            return Window(at - radius, at + radius);
        }

        public NetworkProbeWindow Window(DateTimeOffset from, DateTimeOffset to)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(to, from);
            var history = Snapshot();
            var selected = history.Where(sample => sample.CapturedAt >= from && sample.CapturedAt <= to).ToArray();
            var successes = selected.Where(sample => sample.Status == "success").ToArray();
            int failures = selected.Count(sample => sample.Status == "timeout");
            int count = successes.Length + failures;
            bool unavailable = successes.Length == 0 || selected.Any(sample => sample.Status is "unavailable" or "cancelled");
            return new(from, to, count, successes.Length, failures,
                selected.LastOrDefault()?.RttMs,
                Percentile(successes),
                Percentile(history.Where(sample => sample.CapturedAt < from && sample.Status == "success")),
                count == 0 ? null : failures * 100d / count,
                unavailable ? "unavailable" : failures > 0 ? "degraded" : "healthy",
                selected.LastOrDefault(sample => sample.Error is not null)?.Error,
                selected.Length > 0 && selected.All(sample => sample.RouteVerified));
        }

        private static double? Percentile(IEnumerable<NetworkProbeSample> samples)
        {
            var values = samples.Where(sample => sample.RttMs is double value && double.IsFinite(value) && value >= 0)
                .Select(sample => sample.RttMs!.Value).Order().ToArray();
            return values.Length == 0 ? null : values[(int)Math.Ceiling(values.Length * 0.95) - 1];
        }

        private void LogTransition(NetworkProbeSample sample)
        {
            if (sample.Status == "cancelled") return;
            var now = _owner._time.GetTimestamp();
            bool recovered = sample.Status == "success";
            if (recovered && !_failed) return;
            if (!recovered && _failed && _owner._time.GetElapsedTime(_lastFailureLog, now) < FailureMergeInterval)
            {
                _suppressed++;
                return;
            }
            try
            {
                _owner._logger.Log(recovered ? LogLevel.Information : LogLevel.Warning,
                    "网络探测 {transition}：source={source}; remote={remote}; status={status}; rtt={rtt}ms; error={error}; 合并重复={suppressed}",
                    recovered ? "恢复" : "不可用", sample.SourceAddress, sample.RemoteAddress, sample.Status,
                    sample.RttMs, sample.Error, _suppressed);
            }
            catch (Exception) { /* A diagnostic log provider must not terminate probing. */ }
            _failed = !recovered;
            _lastFailureLog = now;
            _suppressed = 0;
        }
    }
}
