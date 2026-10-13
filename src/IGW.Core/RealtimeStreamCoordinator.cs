using System.Diagnostics;
using System.Collections.Concurrent;
using System.Threading.Channels;
using IGW.Abstractions;
using Microsoft.Extensions.Logging;

namespace IGW.Core;

/// <summary>
/// 进程级实时流协调器：多个客户端共享一份 DataHub 点注册；变化先按句柄合并，再分发到会话。
/// 回调路径只做有界入队，绝不等待网络客户端。
/// </summary>
public sealed class RealtimeStreamCoordinator : IAsyncDisposable
{
    private readonly record struct IngressChange(int Handle, string TagId, DataValue Value, long ArrivedTimestamp);

    private sealed class Route
    {
        public required int Handle;
        public required string TagId;
        public HashSet<Guid> Sessions { get; } = [];
        public RealtimeStreamSession[] Targets = [];
    }

    private sealed class IngressShard
    {
        public required Channel<IngressChange> Queue;
        public Task Pump { get; set; } = Task.CompletedTask;
    }

    private readonly DataHub _hub;
    private readonly string _projectRevision;
    private readonly TagStreamSettings _settings;
    private readonly ILogger<RealtimeStreamCoordinator> _logger;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<int, Route> _routes = new();
    private readonly Dictionary<Guid, RealtimeStreamSession> _sessions = new();
    // 被摘掉的旧路由所属点位:在真正的一次性代际回退(RetireGenerationAsync)发生前，
    // HasRoutesForTags 仍要把它们算作"有实时流在关注过"，否则 GatewayHost 会误判为
    // "没人订阅这个被删的点，不用回退"，错过唯一一次通知客户端重连的机会。
    private readonly HashSet<string> _evictedTagIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RealtimeRuntimeEvent> _retainedRuntimeStates = new(StringComparer.Ordinal);
    private readonly IPointSubscription _pointSubscription;
    private readonly IngressShard[] _ingress;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _snapshotSlots;
    private readonly ConcurrentDictionary<Guid, Task> _snapshotTasks = new();
    private int _peakStreams;
    private int _disposed;
    private int _retired;
    private long _relationships;
    private long _ingressChanges;
    private long _ingressDropped;
    private long _coalescedChanges;
    private long _deliveredChanges;
    private long _runtimeSequence;
    private int _ingressQueueDepth;
    private int _peakIngressQueueDepth;
    private long _sentMessages;
    private long _sentValues;
    private long _sentBytes;
    private readonly RealtimeLatencyHistogram _latency = new();
    private readonly object _metricsCacheGate = new();
    private RealtimeStreamMetrics? _cachedMetrics;
    private long _cachedMetricsAt;

    public RealtimeStreamCoordinator(
        DataHub hub,
        string projectRevision,
        TagStreamSettings settings,
        ILogger<RealtimeStreamCoordinator> logger)
    {
        _hub = hub;
        _projectRevision = projectRevision;
        _settings = settings;
        _logger = logger;
        _snapshotSlots = new SemaphoreSlim(Math.Clamp(settings.MaxConcurrentSnapshots, 1, 32));

        _pointSubscription = hub.SubscribePoints(
            OnPointChange,
            "realtime-streams",
            new PointSubscriptionOptions
            {
                QueueCapacity = settings.CoordinatorQueueCapacity,
                ItemDropped = OnSubscriptionDropped,
                // QC 监控窗只看已经采集到的值，打开窗口不能让范围内所有点持续采集；
                // 需要 PLC 实时值时由刷新/定时刷新显式读取(POST /api/v1/values/read)。
                CreatesDemand = false,
            });

        int shardCount = Math.Clamp(Environment.ProcessorCount, 1, 8);
        int capacityPerShard = Math.Max(1_024, settings.CoordinatorQueueCapacity / shardCount);
        _ingress = new IngressShard[shardCount];
        for (int i = 0; i < _ingress.Length; i++)
        {
            var shard = new IngressShard
            {
                Queue = Channel.CreateBounded<IngressChange>(new BoundedChannelOptions(capacityPerShard)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false,
                }),
            };
            shard.Pump = Task.Run(() => PumpIngressAsync(shard));
            _ingress[i] = shard;
        }
    }

    public ValueTask<RealtimeStreamLease> OpenAsync(
        RealtimeStreamOpenRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (Volatile.Read(ref _retired) != 0)
            throw new RealtimeAdmissionException("generation-retired", "工程点表已经变化，请重新连接实时数据流。");
        // TagStream.Enabled 是早期试验版遗留字段。SSE 已成为 QC/运行状态的正式数据面后，
        // 旧工程里持久化的 false 不能再关闭点值流，否则升级后 QC 会永久收到 HTTP 503，
        // 且没有可用的界面入口恢复。保留字段只为 JSON/SQLite 往返兼容，运行时不再把它当门禁。
        if (!string.Equals(request.ProjectRevision, _projectRevision, StringComparison.Ordinal))
            throw new RealtimeAdmissionException("project-revision-mismatch", "工程点表已经变化，请使用最新工程版本重新连接。");

        var exactTagIds = request.TagIds.Distinct(StringComparer.Ordinal).ToArray();
        if (exactTagIds.Length > _settings.MaxTagsPerStream)
            throw new RealtimeAdmissionException("too-many-tags", $"单个实时流最多允许 {_settings.MaxTagsPerStream} 个点位。");

        var resolved = new List<(int Handle, string TagId)>(exactTagIds.Length);
        var missing = new List<string>();
        foreach (string tagId in exactTagIds)
        {
            if (_hub.TryGetHandleId(tagId, out int handle)) resolved.Add((handle, tagId));
            else missing.Add(tagId);
        }

        RealtimeStreamSession session;
        KeyValuePair<string, RealtimeRuntimeEvent>[] retainedStates;
        bool startSnapshots;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (Volatile.Read(ref _retired) != 0)
                throw new RealtimeAdmissionException("generation-retired", "工程点表已经变化，请重新连接实时数据流。");
            if (_sessions.Count >= _settings.MaxStreams)
                throw new RealtimeAdmissionException("too-many-streams", $"实时客户端数量已达到上限 {_settings.MaxStreams}。");

            Guid streamId = Guid.NewGuid();
            var accepted = new List<(int Handle, string TagId)>(resolved.Count);
            foreach (var item in resolved)
            {
                // 锁外解析的句柄可能已经过期(又一次范围化应用把该点挪到了别的句柄，
                // 或者把这个句柄让给了第三个点位)。锁内按点名重新解析一次，不一致就当缺失处理，
                // 绝不能拿着过期的 (Handle, TagId) 配对继续往下建路由。
                if (!_hub.TryGetHandleId(item.TagId, out int currentHandle) || currentHandle != item.Handle)
                {
                    missing.Add(item.TagId);
                    continue;
                }

                if (_routes.TryGetValue(item.Handle, out var route)
                    && !string.Equals(route.TagId, item.TagId, StringComparison.Ordinal))
                {
                    // 句柄被复用给了不同点位(旧点已从 Hub 注销、新点 LIFO 拿到同一槽位号)，
                    // 但旧路由还没被 RetireGenerationAsync/CloseAsync 摘掉(还有会话挂在上面未关闭)。
                    // 此时决不能让新会话"顺路"加入这个挂着旧点位标识的路由——否则它会跟旧点位的
                    // 残留会话混在一起，谁都可能收到跟自己订阅点位无关的数据。摘掉旧注册，按新点位重建。
                    EvictStaleRouteLocked(route, item.Handle);
                    route = null;
                }
                if (route is null)
                {
                    if (!_pointSubscription.Add(item.Handle))
                    {
                        missing.Add(item.TagId);
                        continue;
                    }
                    route = new Route { Handle = item.Handle, TagId = item.TagId };
                    _routes[item.Handle] = route;
                }
                if (route.Sessions.Add(streamId))
                {
                    _relationships++;
                    accepted.Add(item);
                }
            }

            session = new RealtimeStreamSession(
                streamId,
                request,
                accepted.Select(static item => item.Handle).ToArray(),
                accepted.Select(static item => item.TagId).ToArray(),
                missing.ToArray());
            _sessions.Add(streamId, session);
            foreach (var item in accepted)
                if (_routes.TryGetValue(item.Handle, out var route)) RefreshRouteTargetsLocked(route);
            retainedStates = _retainedRuntimeStates
                .Where(item => TopicMatches(session, item.Value.Topic))
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .ToArray();
            if (_sessions.Count > _peakStreams) _peakStreams = _sessions.Count;
            startSnapshots = session.IncludeSnapshot && session.ReserveSnapshot();
        }

        if (startSnapshots) StartSnapshotLoop(session);
        foreach (var retained in retainedStates) session.OfferState(retained.Key, retained.Value);

        return ValueTask.FromResult(new RealtimeStreamLease(this, session));
    }

    public void PublishRuntimeEvent(RealtimeRuntimeEvent value)
    {
        RealtimeStreamSession[] targets;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            targets = _sessions.Values.Where(session => TopicMatches(session, value.Topic)).ToArray();
        }
        foreach (var session in targets) session.OfferEvent(value);
    }

    public void PublishDeviceState(DeviceStatus status) => PublishRuntimeState(
        "device:" + status.Name,
        new RealtimeRuntimeEvent(
            "devices",
            NextRuntimeSequence(),
            "device-state",
            new RealtimeDeviceStatus(
                status.Name,
                !status.DemandActive && status.State == ConnectionState.Disconnected
                    ? "Standby"
                    : status.State.ToString(),
                status.TagCount,
                status.ActiveTags,
                status.DemandActive,
                status.Stats,
                status.Detail?.Code,
                status.Detail?.Message,
                ToRealtimeDiagnostic(status.Diagnostic)),
            DateTime.UtcNow));

    private static RealtimeConnectionDiagnostic? ToRealtimeDiagnostic(ConnectionIncident? incident) =>
        incident is null ? null : new RealtimeConnectionDiagnostic(
            incident.Device,
            incident.Channel,
            incident.Protocol,
            incident.Code,
            incident.CategoryLabel,
            incident.Scope.ToString(),
            incident.State.ToString(),
            incident.Category == ConnectionDiagnosticCategory.PartialDataFailure
                && incident.State == ConnectionState.Connected
                    ? "PartialOnline"
                    : incident.State.ToString(),
            incident.Cause,
            incident.Suggestion,
            incident.RawError,
            incident.FirstOccurredAt,
            incident.LastOccurredAt,
            incident.RecoveredAt,
            incident.OccurrenceCount,
            incident.Retrying,
            incident.NextRetryMs,
            incident.Active);

    public void PublishMesAvailability(bool enabled) => PublishRuntimeState(
        "mes:availability",
        new RealtimeRuntimeEvent("mes-availability", NextRuntimeSequence(), "mes-availability", new { enabled }, DateTime.UtcNow));

    public void ClearMesRuntimeStates()
    {
        string[] taskIds;
        RealtimeStreamSession[] sessions;
        lock (_gate)
        {
            string[] keys = _retainedRuntimeStates.Keys
                .Where(static key => key.StartsWith("mes:", StringComparison.Ordinal))
                .ToArray();
            taskIds = keys
                .Where(static key => key.StartsWith("mes:task:", StringComparison.Ordinal))
                .Select(static key => key["mes:task:".Length..])
                .Where(static id => id.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            foreach (string key in keys) _retainedRuntimeStates.Remove(key);
            sessions = _sessions.Values.ToArray();
        }
        foreach (RealtimeStreamSession session in sessions) session.RemoveRuntimeStates("mes:");
        foreach (string taskId in taskIds)
            PublishRuntimeState("mes:task:" + taskId, new RealtimeRuntimeEvent(
                "mes-state:" + taskId,
                NextRuntimeSequence(),
                "mes-task-removed",
                new { id = taskId },
                DateTime.UtcNow));
    }

    public void PublishMesState(MesTaskStatus status) => PublishRuntimeState(
        "mes:task:" + status.Id,
        new RealtimeRuntimeEvent("mes-state:" + status.Id, NextRuntimeSequence(), "mes-task-state", status, DateTime.UtcNow));

    public void PublishMesRuntimeEvent(string taskId, string taskName, MesFlowEvent value) => PublishRuntimeEvent(
        new RealtimeRuntimeEvent(
            "mes-task:" + taskId,
            NextRuntimeSequence(),
            "mes-flow-transition",
            new { taskId, taskName, value },
            value.TsUtc));

    private void PublishRuntimeState(string stateKey, RealtimeRuntimeEvent value)
    {
        RealtimeStreamSession[] targets;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            _retainedRuntimeStates[stateKey] = value;
            targets = _sessions.Values.Where(session => TopicMatches(session, value.Topic)).ToArray();
        }
        foreach (var session in targets) session.OfferState(stateKey, value);
    }

    private long NextRuntimeSequence() => Interlocked.Increment(ref _runtimeSequence);

    private static bool TopicMatches(RealtimeStreamSession session, string topic) =>
        session.Topics.Contains(topic)
        || (topic.StartsWith("mes-state:", StringComparison.Ordinal) && session.Topics.Contains("mes-state"))
        // “mes”保留为旧客户端兼容主题；新界面不再订阅它，避免所有任务的高频流转广播到每个页面。
        || ((topic == "mes-state" || topic.StartsWith("mes-state:", StringComparison.Ordinal)
             || topic.StartsWith("mes-task:", StringComparison.Ordinal))
            && session.Topics.Contains("mes"));

    public async ValueTask RetireGenerationAsync(
        string nextProjectRevision,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(nextProjectRevision))
            throw new ArgumentException("工程版本不能为空。", nameof(nextProjectRevision));
        if (Interlocked.Exchange(ref _retired, 1) != 0) return;

        RealtimeStreamSession[] sessions;
        int[] handles;
        lock (_gate)
        {
            sessions = _sessions.Values.ToArray();
            handles = _routes.Keys.ToArray();
            _sessions.Clear();
            _routes.Clear();
            _relationships = 0;
            _evictedTagIds.Clear();
        }
        foreach (int handle in handles) _pointSubscription.Remove(handle);
        foreach (var session in sessions) session.ProjectChanged(nextProjectRevision);

        Task[] snapshotTasks = _snapshotTasks.Values.ToArray();
        if (snapshotTasks.Length > 0)
            await Task.WhenAll(snapshotTasks).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 是否有任一当前打开的实时流路由指向给定点位之一(含刚被"槽位复用"摘掉、
    /// 还没走到 <see cref="RetireGenerationAsync"/> 的点位——见 <see cref="_evictedTagIds"/>)。
    /// 供范围化设备应用(单设备增量)后判断:被移除的点如果没人在实时订阅，就不必为它退回北向(重连所有流)；
    /// 一旦命中，调用方应触发一次 <see cref="RetireGenerationAsync"/> 让相关客户端重新连接取到新点表。
    /// </summary>
    public bool HasRoutesForTags(IEnumerable<string> tagIds)
    {
        var set = new HashSet<string>(tagIds, StringComparer.Ordinal);
        if (set.Count == 0) return false;
        lock (_gate)
        {
            foreach (var route in _routes.Values)
                if (set.Contains(route.TagId)) return true;
            foreach (var tagId in _evictedTagIds)
                if (set.Contains(tagId)) return true;
        }
        return false;
    }

    public RealtimeStreamMetrics SnapshotMetrics()
    {
        RealtimeSessionMetrics[] sessions;
        int uniqueTags;
        int activeStreams;
        lock (_gate)
        {
            activeStreams = _sessions.Count;
            uniqueTags = _routes.Count;
            sessions = _sessions.Values.Select(static session => session.SnapshotMetrics()).ToArray();
        }
        return new RealtimeStreamMetrics(
            activeStreams,
            Volatile.Read(ref _peakStreams),
            uniqueTags,
            Interlocked.Read(ref _relationships),
            Interlocked.Read(ref _ingressChanges),
            Interlocked.Read(ref _ingressDropped),
            Interlocked.Read(ref _coalescedChanges),
            Interlocked.Read(ref _deliveredChanges),
            Volatile.Read(ref _ingressQueueDepth),
            Volatile.Read(ref _peakIngressQueueDepth),
            sessions.Sum(static session => session.ResetCount),
            sessions.Sum(static session => session.SnapshotsStarted),
            sessions.Sum(static session => session.SnapshotsCompleted),
            sessions.Sum(static session => session.SnapshotFailures),
            Interlocked.Read(ref _sentMessages),
            Interlocked.Read(ref _sentValues),
            Interlocked.Read(ref _sentBytes),
            _latency.Snapshot(),
            sessions);
    }

    public RealtimeStreamMetrics SnapshotMetricsCached()
    {
        long now = Stopwatch.GetTimestamp();
        var cached = Volatile.Read(ref _cachedMetrics);
        if (cached is not null && Stopwatch.GetElapsedTime(Volatile.Read(ref _cachedMetricsAt), now) < TimeSpan.FromSeconds(1))
            return cached;
        lock (_metricsCacheGate)
        {
            now = Stopwatch.GetTimestamp();
            if (_cachedMetrics is not null
                && Stopwatch.GetElapsedTime(_cachedMetricsAt, now) < TimeSpan.FromSeconds(1))
                return _cachedMetrics;
            _cachedMetrics = SnapshotMetrics();
            Volatile.Write(ref _cachedMetricsAt, now);
            return _cachedMetrics;
        }
    }

    public bool TryGetMissingTagIds(
        Guid streamId,
        string userName,
        int skip,
        int take,
        out int total,
        out IReadOnlyList<string> items)
    {
        total = 0;
        items = Array.Empty<string>();
        lock (_gate)
        {
            if (!_sessions.TryGetValue(streamId, out RealtimeStreamSession? session) ||
                !string.Equals(session.UserName, userName, StringComparison.Ordinal))
                return false;
            total = session.MissingTagIds.Count;
            items = session.MissingTagIds.Skip(skip).Take(take).ToArray();
            return true;
        }
    }

    /// <summary>传输层完成一次网络刷新后记录交付量与 DataHub 到客户端的实际延迟。</summary>
    public void RecordSent(Guid streamId, RealtimeOutboundMessage? message, int bytes)
    {
        RealtimeStreamSession? session;
        lock (_gate) _sessions.TryGetValue(streamId, out session);
        if (session is null) return;

        long completedTimestamp = Stopwatch.GetTimestamp();
        session.RecordSent(message, bytes, completedTimestamp);
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
        }
    }

    internal ValueTask CloseAsync(Guid streamId)
    {
        RealtimeStreamSession? session;
        lock (_gate)
        {
            if (!_sessions.Remove(streamId, out session)) return ValueTask.CompletedTask;
            foreach (int handle in session.Handles)
            {
                if (!_routes.TryGetValue(handle, out var route) || !route.Sessions.Remove(streamId)) continue;
                _relationships--;
                if (route.Sessions.Count != 0)
                {
                    RefreshRouteTargetsLocked(route);
                    continue;
                }
                _routes.TryRemove(handle, out _);
                _pointSubscription.Remove(handle);
            }
        }
        session.Complete();
        return ValueTask.CompletedTask;
    }

    private ValueTask OnPointChange(TagChange change)
    {
        Interlocked.Increment(ref _ingressChanges);
        var item = new IngressChange(change.Handle, change.TagId, change.Value, Stopwatch.GetTimestamp());
        int index = (change.Handle & int.MaxValue) % _ingress.Length;
        int depth = Interlocked.Increment(ref _ingressQueueDepth);
        UpdatePeak(ref _peakIngressQueueDepth, depth);
        if (!_ingress[index].Queue.Writer.TryWrite(item))
        {
            Interlocked.Decrement(ref _ingressQueueDepth);
            Interlocked.Increment(ref _ingressDropped);
            RequireResetForHandle(change.Handle, "coordinator-backpressure");
        }
        return ValueTask.CompletedTask;
    }

    private void OnSubscriptionDropped(TagChange change)
    {
        Interlocked.Increment(ref _ingressDropped);
        RequireResetForHandle(change.Handle, "subscription-backpressure");
    }

    private void StartSnapshotLoop(RealtimeStreamSession session)
    {
        Task task = Task.Run(() => SnapshotLoopAsync(session));
        _snapshotTasks[session.StreamId] = task;
        _ = task.ContinueWith(
            static (completed, state) =>
            {
                var tuple = ((ConcurrentDictionary<Guid, Task> Tasks, Guid StreamId))state!;
                tuple.Tasks.TryRemove(tuple.StreamId, out _);
            },
            (_snapshotTasks, session.StreamId),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task SnapshotLoopAsync(RealtimeStreamSession session)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, session.LifetimeToken);
        CancellationToken cancellationToken = linked.Token;
        try
        {
            await SendReservedSnapshotAsync(session, cancellationToken).ConfigureAwait(false);

            int intervalSeconds = Math.Clamp(_settings.SnapshotIntervalSeconds, 1, 3_600);
            int phaseMilliseconds = (session.StreamId.GetHashCode() & int.MaxValue)
                                    % Math.Max(1, intervalSeconds * 1_000);
            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds)
                             + TimeSpan.FromMilliseconds(phaseMilliseconds), cancellationToken).ConfigureAwait(false);

            while (!cancellationToken.IsCancellationRequested)
            {
                if (session.ReserveSnapshot())
                    await SendReservedSnapshotAsync(session, cancellationToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            session.AbortSnapshot("snapshot-failed");
            _logger.LogWarning(ex, "实时流 {streamId} 快照任务失败", session.StreamId);
        }
    }

    private async Task SendReservedSnapshotAsync(
        RealtimeStreamSession session,
        CancellationToken cancellationToken)
    {
        await _snapshotSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            const int chunkSize = 256;
            var values = new List<RealtimeValueState>(session.AcceptedTagIds.Count);
            long capturedTimestamp = Stopwatch.GetTimestamp();
            for (int i = 0; i < session.AcceptedTagIds.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string tagId = session.AcceptedTagIds[i];
                if (_hub.TryGet(tagId, out var value))
                    values.Add(new RealtimeValueState(session.Handles[i], tagId, value, capturedTimestamp));
            }

            string snapshotId = Guid.NewGuid().ToString("N");
            int chunkCount = (values.Count + chunkSize - 1) / chunkSize;
            if (!session.EnqueueSnapshotFrame(new RealtimeSnapshotBegin(
                    snapshotId, values.Count, chunkCount, DateTime.UtcNow))) return;
            for (int index = 0; index < chunkCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunk = values.Skip(index * chunkSize).Take(chunkSize).ToArray();
                if (!session.EnqueueSnapshotFrame(new RealtimeSnapshotChunk(snapshotId, index, chunk))) return;
            }
            session.EnqueueSnapshotFrame(new RealtimeSnapshotComplete(
                snapshotId, values.Count, chunkCount, DateTime.UtcNow));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            session.AbortSnapshot("snapshot-failed");
            throw;
        }
        finally
        {
            _snapshotSlots.Release();
        }
    }

    private async Task PumpIngressAsync(IngressShard shard)
    {
        var latest = new Dictionary<int, IngressChange>();
        try
        {
            while (await shard.Queue.Reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                if (!shard.Queue.Reader.TryRead(out var first)) continue;
                Interlocked.Decrement(ref _ingressQueueDepth);
                latest[first.Handle] = first;

                int windowMs = Math.Clamp(_settings.DeltaBatchWindowMs, 1, 100);
                await Task.Delay(windowMs, _cts.Token).ConfigureAwait(false);
                while (shard.Queue.Reader.TryRead(out var next))
                {
                    Interlocked.Decrement(ref _ingressQueueDepth);
                    latest[next.Handle] = next;
                }

                RouteLatestBatch(latest.Values);
                latest.Clear();
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "实时流协调器分片异常退出");
        }
    }

    private void RouteLatestBatch(IEnumerable<IngressChange> changes)
    {
        var batches = new Dictionary<RealtimeStreamSession, List<RealtimeValueState>>();
        foreach (var item in changes)
        {
            if (!_routes.TryGetValue(item.Handle, out var route)) continue;
            var state = new RealtimeValueState(item.Handle, item.TagId, item.Value, item.ArrivedTimestamp);
            foreach (var session in Volatile.Read(ref route.Targets))
            {
                if (!batches.TryGetValue(session, out var batch))
                {
                    batch = [];
                    batches.Add(session, batch);
                }
                batch.Add(state);
                Interlocked.Increment(ref _deliveredChanges);
            }
        }
        foreach (var pair in batches)
            Interlocked.Add(ref _coalescedChanges, pair.Key.MarkDirtyBatch(pair.Value));
    }

    private void RequireResetForHandle(int handle, string reason)
    {
        if (!_routes.TryGetValue(handle, out var route)) return;
        RealtimeStreamSession[] targets = Volatile.Read(ref route.Targets);
        foreach (var session in targets) session.RequireReset(reason);
    }

    /// <summary>
    /// 摘掉一个因句柄被复用而失效的路由(须持 <see cref="_gate"/>)：
    /// 撤销该句柄上的点注册、从 <see cref="_routes"/> 摘除、把还挂在它上面的会话计入 <see cref="_relationships"/>
    /// 扣减(不能指望它们各自关闭时再补扣——那时 <see cref="_routes"/>[handle] 早已指向新点位，
    /// 按 streamId 找不到自己会静默 no-op)，通知这些会话重新取快照(而不是像 <see cref="RetireGenerationAsync"/>
    /// 那样整条 <see cref="RealtimeStreamSession.ProjectChanged"/> 断线重连——它们可能还订阅着其它仍然
    /// 有效的点，没必要连坐)，并把旧点位记进 <see cref="_evictedTagIds"/>，
    /// 让 <see cref="HasRoutesForTags"/> 在真正的一次性代际回退发生前仍然如实报告"曾经有人订阅"。
    /// </summary>
    private void EvictStaleRouteLocked(Route staleRoute, int handle)
    {
        _pointSubscription.Remove(handle);
        _routes.TryRemove(handle, out _);
        _relationships -= staleRoute.Sessions.Count;
        _evictedTagIds.Add(staleRoute.TagId);
        foreach (Guid strandedStreamId in staleRoute.Sessions)
            if (_sessions.TryGetValue(strandedStreamId, out var strandedSession))
                strandedSession.RequireReset("tag-removed");
    }

    private void RefreshRouteTargetsLocked(Route route)
    {
        var targets = route.Sessions
            .Select(id => _sessions.TryGetValue(id, out var session) ? session : null)
            .Where(static session => session is not null)
            .Cast<RealtimeStreamSession>()
            .ToArray();
        Volatile.Write(ref route.Targets, targets);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(RealtimeStreamCoordinator));
    }

    private static void UpdatePeak(ref int target, int candidate)
    {
        int observed = Volatile.Read(ref target);
        while (candidate > observed)
        {
            int prior = Interlocked.CompareExchange(ref target, candidate, observed);
            if (prior == observed) return;
            observed = prior;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        foreach (var shard in _ingress) shard.Queue.Writer.TryComplete();
        foreach (var shard in _ingress)
        {
            try { await shard.Pump.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        RealtimeStreamSession[] sessions;
        lock (_gate)
        {
            sessions = _sessions.Values.ToArray();
            _sessions.Clear();
            _routes.Clear();
            _relationships = 0;
        }
        foreach (var session in sessions) session.Complete();
        Task[] snapshotTasks = _snapshotTasks.Values.ToArray();
        if (snapshotTasks.Length > 0)
        {
            try { await Task.WhenAll(snapshotTasks).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _pointSubscription.Dispose();
        _snapshotSlots.Dispose();
        _cts.Dispose();
    }
}
