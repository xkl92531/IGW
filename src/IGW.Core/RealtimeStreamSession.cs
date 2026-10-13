using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace IGW.Core;

internal sealed class RealtimeStreamSession
{
    private const int MaxPendingEvents = 4_096;
    private readonly object _gate = new();
    private readonly Dictionary<int, RealtimeValueState> _dirtyStates = new();
    private readonly Dictionary<string, RealtimeRuntimeEvent> _runtimeStates = new(StringComparer.Ordinal);
    private readonly Queue<RealtimeRuntimeEvent> _events = new();
    private readonly Queue<RealtimeOutboundMessage> _snapshotFrames = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<byte> _signal = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false,
    });
    private string? _resetReason;
    private RealtimeProjectChanged? _projectChanged;
    private bool _completed;
    private bool _snapshotActive;
    private long _coalescedStates;
    private long _droppedEvents;
    private long _resetCount;
    private long _snapshotsStarted;
    private long _snapshotsCompleted;
    private long _snapshotFailures;
    private long _sentMessages;
    private long _sentValues;
    private long _sentBytes;
    private readonly RealtimeLatencyHistogram _latency = new();

    public RealtimeStreamSession(
        Guid streamId,
        RealtimeStreamOpenRequest request,
        IReadOnlyList<int> handles,
        IReadOnlyList<string> acceptedTagIds,
        IReadOnlyList<string> missingTagIds)
    {
        StreamId = streamId;
        ClientId = request.ClientId;
        UserName = request.UserName;
        ProjectRevision = request.ProjectRevision;
        IncludeSnapshot = request.IncludeSnapshot;
        HeartbeatSeconds = request.HeartbeatSeconds;
        Handles = handles;
        AcceptedTagIds = acceptedTagIds;
        MissingTagIds = missingTagIds;
        Topics = new HashSet<string>(request.Topics, StringComparer.Ordinal);
    }

    public Guid StreamId { get; }
    public string ClientId { get; }
    public string UserName { get; }
    public string ProjectRevision { get; }
    public bool IncludeSnapshot { get; }
    public int HeartbeatSeconds { get; }
    public IReadOnlyList<int> Handles { get; }
    public IReadOnlyList<string> AcceptedTagIds { get; }
    public IReadOnlyList<string> MissingTagIds { get; }
    public HashSet<string> Topics { get; }
    public CancellationToken LifetimeToken => _lifetime.Token;

    public bool ReserveSnapshot()
    {
        lock (_gate)
        {
            if (_completed || _snapshotActive || _snapshotFrames.Count > 0) return false;
            _snapshotActive = true;
            _snapshotsStarted++;
            return true;
        }
    }

    public bool EnqueueSnapshotFrame(RealtimeOutboundMessage frame)
    {
        lock (_gate)
        {
            if (_completed || !_snapshotActive) return false;
            _snapshotFrames.Enqueue(frame);
        }
        Signal();
        return true;
    }

    public void AbortSnapshot(string reason)
    {
        lock (_gate)
        {
            if (_completed) return;
            _snapshotFrames.Clear();
            _snapshotActive = false;
            _snapshotFailures++;
            SetResetLocked(reason);
        }
        Signal();
    }

    public void ProjectChanged(string projectRevision)
    {
        lock (_gate)
        {
            if (_completed) return;
            _snapshotFrames.Clear();
            _dirtyStates.Clear();
            _runtimeStates.Clear();
            _events.Clear();
            _snapshotActive = false;
            _resetReason = null;
            _projectChanged = new RealtimeProjectChanged(projectRevision, DateTime.UtcNow);
            _completed = true;
        }
        _lifetime.Cancel();
        Signal();
        _signal.Writer.TryComplete();
    }

    public long MarkDirty(in RealtimeValueState state)
    {
        long coalesced = 0;
        lock (_gate)
        {
            if (_completed) return 0;
            if (_dirtyStates.ContainsKey(state.Handle))
            {
                coalesced = 1;
                _coalescedStates++;
            }
            _dirtyStates[state.Handle] = state;
        }
        Signal();
        return coalesced;
    }

    public long MarkDirtyBatch(IReadOnlyList<RealtimeValueState> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        long coalesced = 0;
        lock (_gate)
        {
            if (_completed) return 0;
            foreach (var state in states)
            {
                if (_dirtyStates.ContainsKey(state.Handle))
                {
                    coalesced++;
                    _coalescedStates++;
                }
                _dirtyStates[state.Handle] = state;
            }
        }
        if (states.Count > 0) Signal();
        return coalesced;
    }

    public bool OfferEvent(RealtimeRuntimeEvent value)
    {
        lock (_gate)
        {
            if (_completed) return false;
            if (_events.Count >= MaxPendingEvents)
            {
                _droppedEvents++;
                SetResetLocked("runtime-event-backpressure");
                Signal();
                return false;
            }
            _events.Enqueue(value);
        }
        Signal();
        return true;
    }

    /// <summary>状态类运行消息按精确 key 合并，慢客户端只接收每个设备/任务的最终状态。</summary>
    public void OfferState(string stateKey, RealtimeRuntimeEvent value)
    {
        lock (_gate)
        {
            if (_completed) return;
            _runtimeStates[stateKey] = value;
        }
        Signal();
    }

    public void RemoveRuntimeStates(string prefix)
    {
        lock (_gate)
        {
            string[] keys = _runtimeStates.Keys
                .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
                .ToArray();
            foreach (string key in keys) _runtimeStates.Remove(key);
        }
    }

    public void RequireReset(string reason)
    {
        lock (_gate)
        {
            if (_completed) return;
            SetResetLocked(reason);
        }
        Signal();
    }

    public void RecordSent(RealtimeOutboundMessage? message, int bytes, long completedTimestamp)
    {
        Interlocked.Increment(ref _sentMessages);
        Interlocked.Add(ref _sentBytes, Math.Max(0, bytes));
        switch (message)
        {
            case RealtimeStateBatch batch:
                Interlocked.Add(ref _sentValues, batch.Values.Count);
                foreach (var value in batch.Values)
                    _latency.RecordElapsed(value.ArrivedTimestamp, completedTimestamp);
                break;
            case RealtimeSnapshotChunk chunk:
                Interlocked.Add(ref _sentValues, chunk.Values.Count);
                break;
            case RealtimeSnapshotComplete:
                Interlocked.Increment(ref _snapshotsCompleted);
                break;
        }
    }

    public async IAsyncEnumerable<RealtimeOutboundMessage> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (await _signal.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_signal.Reader.TryRead(out _)) { }

            RealtimeOutboundMessage? message;
            bool hasMore;
            lock (_gate)
            {
                if (_projectChanged is { } projectChanged)
                {
                    _projectChanged = null;
                    message = projectChanged;
                }
                else if (_resetReason is { } resetReason)
                {
                    _resetReason = null;
                    message = new RealtimeResetRequired(resetReason);
                }
                else if (_snapshotFrames.Count > 0)
                {
                    message = _snapshotFrames.Dequeue();
                    if (message is RealtimeSnapshotComplete) _snapshotActive = false;
                }
                else if (!_snapshotActive && _dirtyStates.Count > 0)
                {
                    var states = _dirtyStates.Values.ToArray();
                    Array.Sort(states, static (left, right) => left.Handle.CompareTo(right.Handle));
                    _dirtyStates.Clear();
                    message = new RealtimeStateBatch(states);
                }
                else if (!_snapshotActive && _runtimeStates.Count > 0)
                {
                    string key = _runtimeStates.Keys.Min(StringComparer.Ordinal)!;
                    message = new RealtimeOrderedEvent(_runtimeStates[key]);
                    _runtimeStates.Remove(key);
                }
                else if (!_snapshotActive && _events.Count > 0)
                {
                    message = new RealtimeOrderedEvent(_events.Dequeue());
                }
                else
                {
                    message = null;
                }

                hasMore = _projectChanged != null
                    || _resetReason != null
                    || _snapshotFrames.Count > 0
                    || (!_snapshotActive && (_dirtyStates.Count > 0 || _runtimeStates.Count > 0 || _events.Count > 0));
                if (hasMore) Signal();
                if (message is null && _completed) yield break;
            }

            if (message is not null) yield return message;
        }
    }

    public RealtimeSessionMetrics SnapshotMetrics()
    {
        lock (_gate)
            return new RealtimeSessionMetrics(
                StreamId,
                ClientId,
                Handles.Count,
                Topics.Count,
                _dirtyStates.Count,
                _events.Count + _runtimeStates.Count,
                _coalescedStates,
                _droppedEvents,
                _resetCount,
                _snapshotsStarted,
                Interlocked.Read(ref _snapshotsCompleted),
                _snapshotFailures,
                Interlocked.Read(ref _sentMessages),
                Interlocked.Read(ref _sentValues),
                Interlocked.Read(ref _sentBytes),
                _latency.Snapshot());
    }

    public void Complete()
    {
        lock (_gate)
        {
            if (_completed) return;
            _completed = true;
        }
        _lifetime.Cancel();
        _signal.Writer.TryComplete();
    }

    private void Signal() => _signal.Writer.TryWrite(0);

    private void SetResetLocked(string reason)
    {
        if (_resetReason is not null) return;
        _resetReason = reason;
        _resetCount++;
    }
}

public sealed class RealtimeStreamLease : IAsyncDisposable
{
    private readonly RealtimeStreamCoordinator _owner;
    private readonly RealtimeStreamSession _session;
    private int _disposed;

    internal RealtimeStreamLease(RealtimeStreamCoordinator owner, RealtimeStreamSession session)
    {
        _owner = owner;
        _session = session;
    }

    public Guid StreamId => _session.StreamId;
    public IReadOnlyList<string> AcceptedTagIds => _session.AcceptedTagIds;
    public IReadOnlyList<string> MissingTagIds => _session.MissingTagIds;

    public IAsyncEnumerable<RealtimeOutboundMessage> ReadAllAsync(CancellationToken cancellationToken) =>
        _session.ReadAllAsync(cancellationToken);

    public ValueTask DisposeAsync() =>
        Interlocked.Exchange(ref _disposed, 1) == 0
            ? _owner.CloseAsync(_session.StreamId)
            : ValueTask.CompletedTask;
}
