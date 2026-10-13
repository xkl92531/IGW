using System.Diagnostics;
using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using Microsoft.Extensions.Logging.Abstractions;

var options = PerformanceOptions.Parse(args);
var scenario = new RealtimeScenario(options);
PerformanceReport report;
try
{
    report = await scenario.RunAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new
    {
        status = "failed",
        error = ex.ToString(),
        options,
    }, RealtimeScenario.JsonOptions));
    return 2;
}

Console.WriteLine(JsonSerializer.Serialize(report, RealtimeScenario.JsonOptions));
return report.Passed ? 0 : 3;

internal sealed record PerformanceOptions(
    int Tags,
    int Active,
    int Clients,
    int TagsPerClient,
    int Rate,
    int BurstRate,
    int DurationSeconds,
    int SlowClients,
    int ReloadEverySeconds)
{
    public static PerformanceOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"未知参数: {args[i]}");
            string name = args[i][2..];
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"参数 --{name} 缺少值");
            values[name] = args[++i];
        }

        int Read(string name, int fallback, int min, int max)
        {
            if (!values.TryGetValue(name, out string? raw)) return fallback;
            if (!int.TryParse(raw, out int value) || value < min || value > max)
                throw new ArgumentOutOfRangeException(name, $"--{name} 必须在 {min}..{max} 范围内");
            return value;
        }

        int tags = Read("tags", 100_000, 1, 1_000_000);
        int active = Read("active", Math.Min(tags, 50_000), 1, tags);
        int clients = Read("clients", 10, 1, 100);
        int tagsPerClient = Read("tags-per-client", 1_000, 1, 10_000);
        return new PerformanceOptions(
            tags,
            active,
            clients,
            Math.Min(tagsPerClient, active),
            Read("rate", 50_000, 1, 1_000_000),
            Read("burst-rate", 200_000, 0, 2_000_000),
            Read("duration", 30, 1, 86_400),
            Read("slow-client", 0, 0, clients),
            Read("reload-every", 0, 0, 86_400));
    }
}

internal sealed record RuntimeMetadata(
    string Framework,
    string OperatingSystem,
    int ProcessorCount,
    long WorkingSetBytes,
    bool ServerGc);

internal sealed record PerformanceReport(
    string Status,
    bool Passed,
    PerformanceOptions Options,
    RuntimeMetadata Runtime,
    double RegistrationSeconds,
    double ElapsedSeconds,
    long AllocatedBytes,
    long WorkingSetBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    int Reloads,
    int FinalValueMismatches,
    int DeviceReconnects,
    int SessionsAfterCleanup,
    int TagsAfterCleanup,
    DataHub.DataplaneStats Dataplane,
    RealtimeStreamMetrics Steady,
    RealtimeStreamMetrics Final,
    IReadOnlyList<string> Failures);

internal sealed class RealtimeScenario
{
    private sealed class ClientState
    {
        public required string Id;
        public required string[] TagIds;
        public required long[] Received;
        public required Dictionary<int, int> Positions;
        public required RealtimeStreamLease Lease;
        public required CancellationTokenSource Cancellation;
        public Task Reader { get; set; } = Task.CompletedTask;
        public int DelayMilliseconds;
    }

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly PerformanceOptions _options;
    private readonly long[] _expected;
    private readonly object[] _handles;
    private readonly DataHub _hub;
    private readonly List<ClientState> _clients = [];
    private RealtimeStreamCoordinator? _coordinator;
    private int _generation;
    private int _reloads;
    private long _valueSequence;

    public RealtimeScenario(PerformanceOptions options)
    {
        _options = options;
        _expected = new long[options.Tags];
        _handles = new object[options.Tags];
        _hub = new DataHub(shards: null, busCapacityPerShard: 262_144);
    }

    public async Task<PerformanceReport> RunAsync()
    {
        long allocatedBefore = GC.GetTotalAllocatedBytes(true);
        int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
        var total = Stopwatch.StartNew();
        var registration = Stopwatch.StartNew();
        RegisterPoints();
        registration.Stop();

        await OpenGenerationAsync();
        await RunSteadyAsync();
        await DrainAsync(TimeSpan.FromSeconds(10));
        RealtimeStreamMetrics steady = _coordinator!.SnapshotMetrics();

        if (_options.BurstRate > 0)
        {
            PublishCount(_options.BurstRate);
            await DrainAsync(TimeSpan.FromSeconds(10));
        }

        PublishFinalSweep();
        int mismatches = await WaitForConsistencyAsync(TimeSpan.FromSeconds(20));
        RealtimeStreamMetrics final = _coordinator!.SnapshotMetrics();
        DataHub.DataplaneStats dataplane = _hub.Stats();

        await CloseGenerationAsync();
        RealtimeStreamMetrics cleanup = _coordinator?.SnapshotMetrics() ?? EmptyMetrics();
        await _hub.DisposeAsync();
        total.Stop();

        var failures = new List<string>();
        if (steady.IngressDropped != 0) failures.Add($"normal_ingress_dropped={steady.IngressDropped}");
        // 设计指标是整条 DataHub→SSE 通道的总览 P95/P99，不是“所有客户端各自
        // P95 的最大值”。后者会随着客户端数量增加而放大一次 OS 调度抖动，并与
        // health/验证报告展示的聚合直方图口径不一致。慢客户端仍由有界队列、重置、
        // 最终一致性和清理指标单独验证，不能拖垮正常通道。
        if (steady.Latency.P95Microseconds > 20_000)
            failures.Add($"aggregate_p95_us={steady.Latency.P95Microseconds}");
        if (steady.Latency.P99Microseconds > 50_000)
            failures.Add($"aggregate_p99_us={steady.Latency.P99Microseconds}");
        if (dataplane.BusDropped != 0 || dataplane.SubDropped != 0)
            failures.Add($"dataplane_bus_dropped={dataplane.BusDropped},sub_dropped={dataplane.SubDropped}");
        if (mismatches != 0) failures.Add($"final_value_mismatches={mismatches}");
        if (cleanup.ActiveStreams != 0 || cleanup.UniqueTags != 0)
            failures.Add($"cleanup_streams={cleanup.ActiveStreams},cleanup_tags={cleanup.UniqueTags}");

        return new PerformanceReport(
            failures.Count == 0 ? "passed" : "failed",
            failures.Count == 0,
            _options,
            new RuntimeMetadata(
                System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                Environment.ProcessorCount,
                Environment.WorkingSet,
                System.Runtime.GCSettings.IsServerGC),
            registration.Elapsed.TotalSeconds,
            total.Elapsed.TotalSeconds,
            GC.GetTotalAllocatedBytes(false) - allocatedBefore,
            Environment.WorkingSet,
            GC.CollectionCount(0) - gen0,
            GC.CollectionCount(1) - gen1,
            GC.CollectionCount(2) - gen2,
            _reloads,
            mismatches,
            0,
            cleanup.ActiveStreams,
            cleanup.UniqueTags,
            dataplane,
            steady,
            final,
            failures);
    }

    private void RegisterPoints()
    {
        for (int i = 0; i < _options.Tags; i++)
        {
            string id = TagId(i);
            _hub.Register(id, new TagDef
            {
                Id = i.ToString(),
                Name = "T" + i,
                Address = i.ToString(),
                DataType = IgwDataType.Int64,
                Access = TagAccess.ReadWrite,
            }, "Simulator");
            _handles[i] = _hub.GetHandle(id)!;
        }
    }

    private async Task OpenGenerationAsync()
    {
        _generation++;
        _coordinator = new RealtimeStreamCoordinator(
            _hub,
            Revision,
            new TagStreamSettings
            {
                Enabled = true,
                MaxStreams = 100,
                MaxTagsPerStream = 10_000,
                CoordinatorQueueCapacity = 1_048_576,
                DeltaBatchWindowMs = 1,
                SnapshotIntervalSeconds = 3_600,
            },
            NullLogger<RealtimeStreamCoordinator>.Instance);

        for (int clientIndex = 0; clientIndex < _options.Clients; clientIndex++)
        {
            int start = (int)((long)clientIndex * Math.Max(1, _options.TagsPerClient / 2) % _options.Active);
            var tagIds = new string[_options.TagsPerClient];
            var positions = new Dictionary<int, int>(_options.TagsPerClient);
            for (int offset = 0; offset < tagIds.Length; offset++)
            {
                int index = (start + offset) % _options.Active;
                tagIds[offset] = TagId(index);
                positions[index] = offset;
            }

            var lease = await _coordinator.OpenAsync(new RealtimeStreamOpenRequest(
                $"client-{clientIndex}",
                "benchmark",
                Revision,
                tagIds,
                [],
                false,
                30), CancellationToken.None);
            var state = new ClientState
            {
                Id = $"client-{clientIndex}",
                TagIds = tagIds,
                Received = new long[tagIds.Length],
                Positions = positions,
                Lease = lease,
                Cancellation = new CancellationTokenSource(),
                DelayMilliseconds = clientIndex < _options.SlowClients ? 100 : 0,
            };
            state.Reader = ConsumeAsync(state, _coordinator);
            _clients.Add(state);
        }
    }

    private async Task ConsumeAsync(ClientState client, RealtimeStreamCoordinator owner)
    {
        try
        {
            await foreach (RealtimeOutboundMessage message in client.Lease.ReadAllAsync(client.Cancellation.Token))
            {
                if (message is RealtimeStateBatch batch)
                {
                    foreach (var value in batch.Values)
                    {
                        int index = ParseIndex(value.TagId);
                        if (client.Positions.TryGetValue(index, out int position))
                            client.Received[position] = Convert.ToInt64(value.Value.Value);
                    }
                }
                owner.RecordSent(client.Lease.StreamId, message, EstimateBytes(message));
                if (client.DelayMilliseconds > 0)
                    await Task.Delay(client.DelayMilliseconds, client.Cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (client.Cancellation.IsCancellationRequested) { }
    }

    private async Task RunSteadyAsync()
    {
        int remaining = checked(_options.Rate * _options.DurationSeconds);
        int perSlice = Math.Max(1, _options.Rate / 10);
        var clock = Stopwatch.StartNew();
        long nextReloadAt = _options.ReloadEverySeconds > 0 ? _options.ReloadEverySeconds * 1_000L : long.MaxValue;
        while (remaining > 0)
        {
            long sliceStarted = clock.ElapsedMilliseconds;
            int count = Math.Min(perSlice, remaining);
            PublishCount(count);
            remaining -= count;

            if (clock.ElapsedMilliseconds >= nextReloadAt && remaining > 0)
            {
                await ReloadAsync();
                nextReloadAt += _options.ReloadEverySeconds * 1_000L;
            }

            long delay = 100 - (clock.ElapsedMilliseconds - sliceStarted);
            if (delay > 0) await Task.Delay((int)delay);
        }
    }

    private void PublishCount(int count)
    {
        const int batchSize = 1_024;
        var batch = new DataHubUpdate[Math.Min(batchSize, count)];
        int published = 0;
        while (published < count)
        {
            int take = Math.Min(batch.Length, count - published);
            DateTime now = DateTime.UtcNow;
            for (int i = 0; i < take; i++)
            {
                long sequence = Interlocked.Increment(ref _valueSequence);
                int index = (int)(sequence % _options.Active);
                _expected[index] = sequence;
                batch[i] = new DataHubUpdate(_handles[index], DataValue.GoodAt(sequence, now));
            }
            _hub.UpdateBatch(batch.AsSpan(0, take));
            published += take;
        }
    }

    private void PublishFinalSweep()
    {
        var union = new HashSet<int>();
        foreach (var client in _clients)
            foreach (string tagId in client.TagIds) union.Add(ParseIndex(tagId));

        const int batchSize = 1_024;
        var updates = new DataHubUpdate[batchSize];
        int used = 0;
        foreach (int index in union)
        {
            long sequence = Interlocked.Increment(ref _valueSequence);
            _expected[index] = sequence;
            updates[used++] = new DataHubUpdate(_handles[index], DataValue.GoodAt(sequence, DateTime.UtcNow));
            if (used != updates.Length) continue;
            _hub.UpdateBatch(updates);
            used = 0;
        }
        if (used > 0) _hub.UpdateBatch(updates.AsSpan(0, used));
    }

    private async Task<int> WaitForConsistencyAsync(TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        int mismatches;
        do
        {
            mismatches = CountMismatches();
            if (mismatches == 0) return 0;
            await Task.Delay(100);
        } while (clock.Elapsed < timeout);
        return mismatches;
    }

    private int CountMismatches()
    {
        int mismatches = 0;
        foreach (var client in _clients)
        {
            for (int i = 0; i < client.TagIds.Length; i++)
            {
                int index = ParseIndex(client.TagIds[i]);
                if (client.Received[i] != Volatile.Read(ref _expected[index])) mismatches++;
            }
        }
        return mismatches;
    }

    private async Task DrainAsync(TimeSpan timeout)
    {
        long previous = -1;
        int stable = 0;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            long delivered = _coordinator!.SnapshotMetrics().DeliveredChanges;
            stable = delivered == previous ? stable + 1 : 0;
            if (stable >= 5) return;
            previous = delivered;
            await Task.Delay(50);
        }
    }

    private async Task ReloadAsync()
    {
        await CloseGenerationAsync();
        _reloads++;
        await OpenGenerationAsync();
    }

    private async Task CloseGenerationAsync()
    {
        foreach (var client in _clients) client.Cancellation.Cancel();
        foreach (var client in _clients) await client.Lease.DisposeAsync();
        await Task.WhenAll(_clients.Select(static client => client.Reader));
        foreach (var client in _clients) client.Cancellation.Dispose();
        _clients.Clear();
        if (_coordinator is not null) await _coordinator.DisposeAsync();
    }

    private string Revision => "benchmark-rev-" + _generation;
    private static string TagId(int index) => "Simulator.Device.T" + index;
    private static int ParseIndex(string tagId) => int.Parse(tagId.AsSpan(tagId.LastIndexOf('T') + 1));
    private static int EstimateBytes(RealtimeOutboundMessage message) => message switch
    {
        RealtimeStateBatch batch => 64 + batch.Values.Count * 96,
        RealtimeSnapshotChunk chunk => 64 + chunk.Values.Count * 96,
        _ => 128,
    };

    private static RealtimeStreamMetrics EmptyMetrics() => new(
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        new RealtimeLatencySnapshot(0, 0, 0, 0), []);
}
