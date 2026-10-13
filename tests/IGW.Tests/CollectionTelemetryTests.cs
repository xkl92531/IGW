using System.Collections.Concurrent;
using IGW.Abstractions;
using IGW.Core;
using Microsoft.Extensions.Logging;
using Xunit;

namespace IGW.Tests;

public sealed class CollectionTelemetryTests
{
    [Fact]
    public async Task Device_loop_accounts_for_every_batch_in_summarized_records_with_the_values_read()
    {
        await using var hub = new DataHub();
        TagBinding[] bindings = Enumerable.Range(0, 2).Select(index =>
        {
            string id = $"PLC8.OP140.DB.Value{index}";
            var definition = new TagDef
            {
                Name = $"Value{index}", Address = $"DB1.DBW{index * 2}",
                DataType = IgwDataType.Int16, ScanRateMs = 20
            };
            hub.Register(id, definition);
            return new TagBinding { TagId = id, Def = definition, Address = new TagAddress(1, index * 2, 2) };
        }).ToArray();
        var demands = new ScanDemandRegistry(hub);
        using var demand = demands.Acquire(
            "mes:task-stable-id", bindings.Select(item => item.TagId), 20,
            ownerName: "OP140 出站", scope: "PLC8.OP140.StationEXIT");
        var logger = new StructuredLogger();
        var loop = new DeviceLoop(
            new StableDriver(), bindings, hub, logger, "PLC8.OP140",
            scanDemands: demands, channelName: "PLC8", protocol: "siemens.s7");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        loop.Start(stop.Token);
        await WaitUntilAsync(() => loop.ScanHistory(200, null).Count >= 3, TimeSpan.FromSeconds(2));
        stop.Cancel();
        await loop.Completion;

        // 成功批次按块汇总：第一次成功立即记一条，其余在 30 秒窗口内攒着，停止时补记——
        // 几秒内只有 2 条，但每个批次都计入了 igw_scan_count。
        IReadOnlyList<IReadOnlyDictionary<string, object?>> records = logger.ByCategory("collection");
        Assert.Equal(loop.ScanHistory(200, null).Count, records.Sum(record => Convert.ToInt32(record["igw_scan_count"])));
        Assert.Equal(2, records.Count);
        Assert.Equal("采集批次完成", records[0]["{OriginalFormat}"]?.ToString());
        Assert.Equal("采集批次汇总", records[1]["{OriginalFormat}"]?.ToString());
        Assert.All(records, record =>
        {
            Assert.Equal("PLC8", record["igw_channel"]);
            Assert.Equal("PLC8.OP140", record["igw_device"]);
            Assert.Equal("acquisition", record["igw_module"]);
            Assert.Equal("plc_scan", record["igw_feature"]);
            Assert.Equal("collection_batch", record["igw_event_kind"]);
            Assert.Equal("read", record["igw_operation"]);
            Assert.Equal("mes", record["igw_collection_source"]);
            Assert.Equal("task-stable-id", record["igw_task_id"]);
            Assert.Equal("OP140 出站", record["igw_task_name"]);
            Assert.Equal("PLC8.OP140.StationEXIT", record["igw_scope"]);
            Assert.Equal(2, record["igw_tag_count"]);
            Assert.True(Convert.ToDouble(record["igw_duration_ms"]) >= 0);
            Assert.False(record.ContainsKey("igw_tag_id"));
            Assert.DoesNotContain("Value0", record["{OriginalFormat}"]?.ToString());
            // 点位值集中在一个字段里(首条和汇总都带)，消息本身不变
            Assert.Contains("[\"PLC8.OP140.DB.Value0\",7]", record["igw_tag_values"]?.ToString());
        });
    }

    [Fact]
    public async Task Batch_owner_summary_keeps_true_count_and_all_source_kinds_when_display_list_is_bounded()
    {
        await using var hub = new DataHub();
        const string tagId = "PLC8.OP140.DB.Value";
        var definition = new TagDef
        {
            Name = "Value", Address = "DB1.DBW0", DataType = IgwDataType.Int16, ScanRateMs = 20
        };
        hub.Register(tagId, definition);
        TagBinding[] bindings =
        [
            new TagBinding { TagId = tagId, Def = definition, Address = new TagAddress(1, 0, 2) }
        ];
        var demands = new ScanDemandRegistry(hub);
        var leases = new List<IScanDemandLease>();
        try
        {
            for (int index = 0; index < 32; index++)
                leases.Add(demands.Acquire($"mes:task-{index:D2}", [tagId], 20, ownerName: $"Task {index:D2}"));
            leases.Add(demands.Acquire("subscription:qc", [tagId], 20, ownerName: "QC"));
            var logger = new StructuredLogger();
            var loop = new DeviceLoop(new StableDriver(), bindings, hub, logger, "PLC8.OP140",
                scanDemands: demands, channelName: "PLC8", protocol: "siemens.s7");
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));

            loop.Start(stop.Token);
            await WaitUntilAsync(() => logger.ByCategory("collection").Count > 0, TimeSpan.FromSeconds(2));
            stop.Cancel();
            await loop.Completion;

            IReadOnlyDictionary<string, object?> record = logger.ByCategory("collection")[0];
            Assert.Equal("shared", record["igw_collection_source"]);
            Assert.Contains("subscription", record["igw_collection_sources"]?.ToString(), StringComparison.Ordinal);
            Assert.Equal(33, record["igw_owner_count"]);
            Assert.Equal(true, record["igw_owner_list_truncated"]);
        }
        finally
        {
            foreach (IScanDemandLease lease in leases) lease.Dispose();
        }
    }

    [Fact]
    public async Task Connection_failure_and_recovery_emit_state_transitions_once()
    {
        await using var hub = new DataHub();
        var logger = new StructuredLogger();
        var loop = new DeviceLoop(
            new FailFirstConnectDriver(), [], hub, logger, "PLC9.OP230",
            channelName: "PLC9", protocol: "siemens.s7");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        loop.Start(stop.Token);
        await WaitUntilAsync(
            () => logger.ByCategory("state").Any(record => Equals(record["igw_status"], "online")),
            TimeSpan.FromSeconds(4));
        stop.Cancel();
        await loop.Completion;

        string[] states = logger.ByCategory("state")
            .Select(record => record["igw_status"]?.ToString() ?? "")
            .ToArray();
        Assert.Contains("disconnected", states);
        Assert.Contains("faulted", states);
        Assert.Contains("online", states);
        Assert.Equal(1, states.Count(state => state == "faulted"));
        Assert.Equal(1, states.Count(state => state == "online"));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("condition not met");
            await Task.Delay(10);
        }
    }

    private sealed class StructuredLogger : ILogger
    {
        private readonly ConcurrentQueue<IReadOnlyDictionary<string, object?>> _records = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
                _records.Enqueue(values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
        }

        public IReadOnlyList<IReadOnlyDictionary<string, object?>> ByCategory(string category) =>
            _records.Where(record => record.TryGetValue("igw_category", out object? value)
                && string.Equals(value?.ToString(), category, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private class StableDriver : IDeviceDriver
    {
        public string DriverId => "siemens.s7";
        public ConnectionState State { get; protected set; } = ConnectionState.Disconnected;
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser { get; } = new Parser();
        public DriverLimits Limits { get; } = new() { MaxTagsPerRead = 100, MaxReadUnits = 100, MaxGap = 16 };

        public virtual Task ConnectAsync(CancellationToken cancellationToken)
        {
            State = ConnectionState.Connected;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            State = ConnectionState.Disconnected;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken cancellationToken)
        {
            DateTime now = DateTime.UtcNow;
            return Task.FromResult<IReadOnlyList<TagSample>>(tags.Select(tag =>
                new TagSample(tag.TagId, DataValue.GoodAt((short)7, now))).ToArray());
        }

        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WriteResult>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailFirstConnectDriver : StableDriver
    {
        private int _attempts;
        public override Task ConnectAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _attempts) == 1) throw new IOException("temporary secret=do-not-log");
            return base.ConnectAsync(cancellationToken);
        }
    }

    private sealed class Parser : IAddressParser
    {
        public bool TryParse(string address, IgwDataType type, out TagAddress result, out string? error)
        {
            result = new TagAddress(1, 0, 2);
            error = null;
            return true;
        }
    }
}
