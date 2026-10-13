using System.Diagnostics;

namespace IGW.Abstractions;

/// <summary>Bounded request metadata; -1 means an unavailable count/size, never a measured zero.</summary>
public sealed record ProtocolRequestDescriptor(string Operation, string Scope, int TagCount, int PayloadBytes, int GapBytes);
public sealed record ProtocolRequestSample(ProtocolRequestDescriptor? Descriptor, double ElapsedMs, bool Failed, bool Cancelled);

public sealed record ProtocolTimingSnapshot(int Requests, int FailedRequests, int CancelledRequests,
    double RequestTotalMs, double RequestMaxMs, double QueueMs,
    IReadOnlyList<ProtocolRequestSample>? Samples = null, int OmittedSamples = 0, int OpaqueOperations = 0)
{
    public int? PhysicalRequests => OpaqueOperations == 0 ? Requests : null;
}

/// <summary>One logical operation, inherited by awaited child work. No global driver "last operation" state.</summary>
public sealed class ProtocolOperationTiming : IDisposable
{
    private static readonly AsyncLocal<ProtocolOperationTiming?> Current = new();
    private readonly ProtocolOperationTiming? _parent;
    private readonly object _gate = new();
    private readonly List<ProtocolRequestSample> _samples = new(64);
    private int _requests, _failed, _cancelled, _omitted, _opaque;
    private double _total, _max, _queue;
    private ProtocolOperationTiming(ProtocolOperationTiming? parent) => _parent = parent;
    public static ProtocolOperationTiming Begin()
    {
        var scope = new ProtocolOperationTiming(Current.Value);
        Current.Value = scope;
        return scope;
    }

    public static void AddQueue(double ms)
    {
        var scope = Current.Value;
        if (scope is null || !double.IsFinite(ms) || ms < 0) return;
        lock (scope._gate) scope._queue += ms;
    }

    /// <summary>Used only when an invoked client has no observable raw request boundary.</summary>
    public static void MarkOpaqueOperation()
    {
        var scope = Current.Value;
        if (scope is not null) lock (scope._gate) scope._opaque++;
    }

    public static T Measure<T>(Func<T> action, ProtocolRequestDescriptor? descriptor = null) =>
        MeasureResult(action, static _ => false, descriptor);

    public static T MeasureResult<T>(Func<T> action, Func<T, bool> isFailure,
        ProtocolRequestDescriptor? descriptor = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        var scope = Current.Value;
        if (scope is null) return action();
        long started = Stopwatch.GetTimestamp();
        bool failed = false, cancelled = false;
        try { var result = action(); failed = isFailure(result); return result; }
        catch (OperationCanceledException) { cancelled = true; throw; }
        catch { failed = true; throw; }
        finally { scope.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, descriptor, failed, cancelled); }
    }

    public static Task<T> MeasureAsync<T>(Func<Task<T>> action) => MeasureAsync(action, null);
    public static async Task<T> MeasureAsync<T>(Func<Task<T>> action, ProtocolRequestDescriptor? descriptor)
    {
        ArgumentNullException.ThrowIfNull(action);
        var scope = Current.Value;
        if (scope is null) return await action().ConfigureAwait(false);
        long started = Stopwatch.GetTimestamp();
        bool failed = false, cancelled = false;
        try { return await action().ConfigureAwait(false); }
        catch (OperationCanceledException) { cancelled = true; throw; }
        catch { failed = true; throw; }
        finally { scope.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, descriptor, failed, cancelled); }
    }

    private void Record(double ms, ProtocolRequestDescriptor? descriptor, bool failed, bool cancelled)
    {
        lock (_gate)
        {
            _requests++;
            if (failed) _failed++;
            if (cancelled) _cancelled++;
            _total += ms;
            _max = Math.Max(_max, ms);
            if (_samples.Count == 64) { _omitted++; return; }
            if (descriptor is not null)
                descriptor = descriptor with { Operation = Bound(descriptor.Operation, 48), Scope = Bound(descriptor.Scope, 160) };
            _samples.Add(new(descriptor, ms, failed, cancelled));
        }
    }

    private static string Bound(string value, int max) => value.Length <= max ? value : value[..max];
    public ProtocolTimingSnapshot Snapshot()
    {
        lock (_gate) return new(_requests, _failed, _cancelled, _total, _max, _queue,
            Array.AsReadOnly(_samples.ToArray()), _omitted, _opaque);
    }
    public void Dispose()
    {
        if (ReferenceEquals(Current.Value, this)) Current.Value = _parent;
    }
}
