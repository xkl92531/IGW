using System.Collections.Concurrent;
using IGW.Abstractions;
using IGW.Core;
using IGW.Drivers.IoTClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class DeviceLoopTransientReadTests
{
    [Fact]
    public async Task Repeated_read_errors_are_summarized_without_hiding_recovery_or_new_failure()
    {
        await using var hub = new DataHub();
        const string id = "Line.Plc.Value";
        var def = new TagDef { Name = "Value", Address = "DB1.DBW0", DataType = IgwDataType.Int16, ScanRateMs = 50 };
        hub.Register(id, def);
        var demands = new ScanDemandRegistry(hub);
        using var demand = demands.Acquire("test", [id], 50);
        var driver = new SequenceDriver([false, false, false, true, false, true], reportDbDetail: false);
        var logger = new CollectingLogger();
        var loop = new DeviceLoop(driver,
            [new TagBinding { TagId = id, Def = def, Address = new TagAddress(1, 0, 2) }],
            hub, logger, "Line.Plc", scanDemands: demands);
        using var stop = new CancellationTokenSource();
        loop.Start(stop.Token);
        try
        {
            await WaitUntilAsync(() => driver.ReadCalls >= 6, TimeSpan.FromSeconds(3));
            Assert.Equal(2, logger.Messages.Count(message => message.Contains("读取批次异常")));
            Assert.Contains(logger.Messages, message => message.Contains("读取质量恢复") && message.Contains("合并重复=2"));
            Assert.True(hub.TryGet(id, out var value) && value.IsGood);
            Assert.Equal(0, driver.DisconnectCalls);
        }
        finally { stop.Cancel(); await loop.Completion; }
    }

    [Fact]
    public async Task One_bad_db_does_not_block_another_db_on_the_same_device()
    {
        await using var hub = new DataHub();
        var bad = new TagDef { Name = "Bad", Address = "DB2100.DBW0", DataType = IgwDataType.Int16, ScanRateMs = 50 };
        var good = new TagDef { Name = "Good", Address = "DB2101.DBW0", DataType = IgwDataType.Int16, ScanRateMs = 50 };
        const string badId = "Line.Plc.DB2100.Bad";
        const string goodId = "Line.Plc.DB2101.Good";
        hub.Register(badId, bad);
        hub.Register(goodId, good);
        var parser = new SiemensScanAddressParser();
        Assert.True(parser.TryParse(bad.Address, bad.DataType, out var badAddress, out _));
        Assert.True(parser.TryParse(good.Address, good.DataType, out var goodAddress, out _));
        var bindings = new[]
        {
            new TagBinding { TagId = badId, Def = bad, Address = badAddress },
            new TagBinding { TagId = goodId, Def = good, Address = goodAddress },
        };
        var demands = new ScanDemandRegistry(hub);
        using var demand = demands.Acquire("test", [badId, goodId], 50);
        var driver = new DbIsolationDriver();
        var loop = new DeviceLoop(driver, bindings, hub, NullLogger.Instance, "Line.Plc", scanDemands: demands);
        using var stop = new CancellationTokenSource();

        loop.Start(stop.Token);
        await WaitUntilAsync(() => hub.TryGet(goodId, out var value) && value.IsGood, TimeSpan.FromSeconds(2));

        Assert.True(hub.TryGet(badId, out var badValue));
        Assert.False(badValue.IsGood);
        Assert.All(driver.ReadBatches, batch => Assert.Single(batch));
        Assert.Equal(ConnectionState.Connected, driver.State);
        Assert.Equal(0, driver.DisconnectCalls);
        stop.Cancel();
        await loop.Completion;
    }

    [Fact]
    public async Task Persistently_bad_db_is_retried_with_its_own_backoff_while_good_db_keeps_its_period()
    {
        await using var hub = new DataHub();
        var bad = new TagDef { Name = "Bad", Address = "DB2100.DBW0", DataType = IgwDataType.Int16, ScanRateMs = 20 };
        var good = new TagDef { Name = "Good", Address = "DB2101.DBW0", DataType = IgwDataType.Int16, ScanRateMs = 20 };
        const string badId = "Line.Plc.DB2100.Bad";
        const string goodId = "Line.Plc.DB2101.Good";
        hub.Register(badId, bad);
        hub.Register(goodId, good);
        var parser = new SiemensScanAddressParser();
        Assert.True(parser.TryParse(bad.Address, bad.DataType, out var badAddress, out _));
        Assert.True(parser.TryParse(good.Address, good.DataType, out var goodAddress, out _));
        var demands = new ScanDemandRegistry(hub);
        using var demand = demands.Acquire("test", [badId, goodId], 20);
        var driver = new SlowBadDbDriver();
        var loop = new DeviceLoop(driver,
            [
                new TagBinding { TagId = badId, Def = bad, Address = badAddress },
                new TagBinding { TagId = goodId, Def = good, Address = goodAddress },
            ],
            hub, NullLogger.Instance, "Line.Plc", scanDemands: demands);
        using var stop = new CancellationTokenSource();

        loop.Start(stop.Token);
        await WaitUntilAsync(() => driver.GoodReads >= 5, TimeSpan.FromSeconds(2));

        Assert.True(driver.BadReads <= 2, $"坏 DB 被过度重试: {driver.BadReads} 次");
        Assert.True(hub.TryGet(goodId, out var goodValue) && goodValue.IsGood);
        stop.Cancel();
        await loop.Completion;
    }

    [Fact]
    public async Task Driver_DB_detail_creates_data_incident_without_marking_transport_offline_and_recovers_on_good_read()
    {
        await using var hub = new DataHub();
        const string tagId = "Line.Plc.DB.Value";
        var def = new TagDef { Name = "Value", Address = "DB2100.DBW0", DataType = IgwDataType.Int16 };
        hub.Register(tagId, def);
        var demands = new ScanDemandRegistry(hub);
        using var demand = demands.Acquire("test", [tagId], 500);
        var incidents = new ConnectionIncidentRegistry();
        var driver = new SequenceDriver([false, true]);
        var loop = new DeviceLoop(driver,
            [new TagBinding { TagId = tagId, Def = def, Address = new TagAddress(2100, 0, 2) }],
            hub, NullLogger.Instance, "Line.Plc", scanDemands: demands,
            incidents: incidents, channelName: "Line", protocol: "Siemens.S7");
        using var stop = new CancellationTokenSource();

        loop.Start(stop.Token);
        await WaitUntilAsync(() => incidents.Current("Line.Plc") is not null, TimeSpan.FromSeconds(2));

        var failure = incidents.Current("Line.Plc")!;
        Assert.Equal(ConnectionDiagnosticCategory.DataAddressMismatch, failure.Category);
        Assert.Equal(ConnectionDiagnosticScope.DataAccess, failure.Scope);
        Assert.Equal(ConnectionState.Connected, driver.State);

        await WaitUntilAsync(() => driver.ReadCalls >= 2 && incidents.Current("Line.Plc") is null, TimeSpan.FromSeconds(2));
        Assert.Single(incidents.Snapshot().Recovered);
        Assert.Equal(ConnectionState.Connected, driver.State);
        stop.Cancel();
        await loop.Completion;
    }

    [Fact]
    public async Task Single_all_bad_read_does_not_disconnect_healthy_transport()
    {
        await using var hub = new DataHub();
        const string tagId = "Line.Plc.DB.Value";
        var def = new TagDef { Name = "Value", Address = "DB1.DBW0", DataType = IgwDataType.Int16 };
        hub.Register(tagId, def);
        var demands = new ScanDemandRegistry(hub);
        using var demand = demands.Acquire("test", [tagId], 200);
        var driver = new SequenceDriver([false, true, true]);
        var loop = new DeviceLoop(driver,
            [new TagBinding { TagId = tagId, Def = def, Address = new TagAddress(1, 0, 2) }],
            hub, NullLogger.Instance, "Line.Plc", scanDemands: demands);
        using var stop = new CancellationTokenSource();

        loop.Start(stop.Token);
        await WaitUntilAsync(() => driver.ReadCalls >= 2, TimeSpan.FromSeconds(2));

        Assert.Equal(0, driver.DisconnectCalls);
        Assert.Equal(ConnectionState.Connected, driver.State);
        stop.Cancel();
        await loop.Completion;
    }

    [Fact]
    public async Task Failed_read_log_contains_endpoints_field_permission_capacity_and_status()
    {
        await using var hub = new DataHub();
        const string tagId = "Line.Plc.DB.Message";
        var def = new TagDef
        {
            Name = "Message", Address = "ns=3;s=DB1.Message", DataType = IgwDataType.String,
            Access = TagAccess.ReadWrite, StringLength = 32, ScanRateMs = 500,
        };
        hub.Register(tagId, def);
        var demands = new ScanDemandRegistry(hub);
        using var demand = demands.Acquire("test", [tagId], 500);
        var driver = new SequenceDriver([false, true]);
        var logger = new CollectingLogger();
        var loop = new DeviceLoop(driver,
            [new TagBinding { TagId = tagId, Def = def, Address = new TagAddress(1, 0, 32) }],
            hub, logger, "Line.Plc", scanDemands: demands);
        using var stop = new CancellationTokenSource();

        loop.Start(stop.Token);
        await WaitUntilAsync(() => logger.Messages.Any(message => message.Contains("读取批次异常")),
            TimeSpan.FromSeconds(2));

        string diagnostic = logger.Messages.Last(message => message.Contains("读取批次异常"));
        Assert.Contains("远程地址=opc.tcp://10.217.152.80:4840", diagnostic);
        Assert.Contains("本地地址=opc.tcp://127.0.0.1:52000", diagnostic);
        Assert.Contains("NodeId=ns=3;s=DB1.Message", diagnostic);
        Assert.Contains("类型=String", diagnostic);
        Assert.Contains("权限=ReadWrite", diagnostic);
        Assert.Contains("字段长度=32", diagnostic);
        Assert.Contains("状态=BadCommFailure", diagnostic);
        stop.Cancel();
        await loop.Completion;
    }

    [Fact]
    public async Task Consecutive_all_bad_reads_preserve_transport_until_driver_reports_a_real_io_failure()
    {
        await using var hub = new DataHub();
        const string tagId = "Line.Plc.DB.Value";
        var def = new TagDef { Name = "Value", Address = "DB1.DBW0", DataType = IgwDataType.Int16 };
        hub.Register(tagId, def);
        var demands = new ScanDemandRegistry(hub);
        using var demand = demands.Acquire("test", [tagId], 50);
        var driver = new SequenceDriver([false, false, false, true]);
        var loop = new DeviceLoop(driver,
            [new TagBinding { TagId = tagId, Def = def, Address = new TagAddress(1, 0, 2) }],
            hub, NullLogger.Instance, "Line.Plc", scanDemands: demands);
        using var stop = new CancellationTokenSource();

        loop.Start(stop.Token);
        await WaitUntilAsync(() => driver.ReadCalls >= 4, TimeSpan.FromSeconds(2));

        Assert.Equal(0, driver.DisconnectCalls);
        Assert.Equal(ConnectionState.Connected, driver.State);
        stop.Cancel();
        await loop.Completion;
    }

    [Fact]
    public async Task Repeated_generic_bad_reads_remain_one_incident_until_a_clean_cycle_recovers_it()
    {
        await using var hub = new DataHub();
        const string tagId = "Line.Plc.DB.Value";
        var def = new TagDef { Name = "Value", Address = "DB1.DBW0", DataType = IgwDataType.Int16, ScanRateMs = 50 };
        hub.Register(tagId, def);
        var demands = new ScanDemandRegistry(hub);
        using var demand = demands.Acquire("test", [tagId], 500);
        var incidents = new ConnectionIncidentRegistry();
        var driver = new SequenceDriver([false, false, true], reportDbDetail: false);
        var loop = new DeviceLoop(driver,
            [new TagBinding { TagId = tagId, Def = def, Address = new TagAddress(1, 0, 2) }],
            hub, NullLogger.Instance, "Line.Plc", scanDemands: demands,
            incidents: incidents, channelName: "Line", protocol: "Sequence");
        using var stop = new CancellationTokenSource();

        loop.Start(stop.Token);
        await WaitUntilAsync(() => driver.ReadCalls >= 3 && incidents.Current("Line.Plc") is null, TimeSpan.FromSeconds(2));

        var recovered = Assert.Single(incidents.Snapshot().Recovered);
        Assert.Equal(ConnectionDiagnosticCategory.PartialDataFailure, recovered.Category);
        Assert.True(recovered.OccurrenceCount >= 2);
        stop.Cancel();
        await loop.Completion;
    }

    [Fact]
    public async Task Mixed_good_and_bad_points_report_partial_data_without_disconnect_and_recover_when_all_are_good()
    {
        await using var hub = new DataHub();
        var first = new TagDef { Name = "A", Address = "DB1.DBW0", DataType = IgwDataType.Int16, ScanRateMs = 50 };
        var second = new TagDef { Name = "B", Address = "DB1.DBW2", DataType = IgwDataType.Int16, ScanRateMs = 50 };
        hub.Register("Line.Plc.DB.A", first);
        hub.Register("Line.Plc.DB.B", second);
        var demands = new ScanDemandRegistry(hub);
        using var demand = demands.Acquire("test", ["Line.Plc.DB.A", "Line.Plc.DB.B"], 500);
        var incidents = new ConnectionIncidentRegistry();
        var driver = new MixedSequenceDriver();
        var loop = new DeviceLoop(driver,
            [
                new TagBinding { TagId = "Line.Plc.DB.A", Def = first, Address = new TagAddress(1, 0, 2) },
                new TagBinding { TagId = "Line.Plc.DB.B", Def = second, Address = new TagAddress(1, 2, 2) },
            ],
            hub, NullLogger.Instance, "Line.Plc", scanDemands: demands,
            incidents: incidents, channelName: "Line", protocol: "Sequence");
        using var stop = new CancellationTokenSource();

        loop.Start(stop.Token);
        await WaitUntilAsync(() => incidents.Current("Line.Plc") is not null, TimeSpan.FromSeconds(2));
        var partial = incidents.Current("Line.Plc")!;
        Assert.Equal(ConnectionDiagnosticCategory.PartialDataFailure, partial.Category);
        Assert.Contains("IGW路径=Line.Plc.DB.B", partial.RawError);
        Assert.Contains("地址=DB1.DBW2", partial.RawError);
        Assert.DoesNotContain("IGW路径=Line.Plc.DB.A", partial.RawError);
        Assert.Equal(ConnectionState.Connected, driver.State);

        await WaitUntilAsync(() => driver.ReadCalls >= 2 && incidents.Current("Line.Plc") is null, TimeSpan.FromSeconds(2));
        Assert.Single(incidents.Snapshot().Recovered);
        Assert.Equal(0, driver.DisconnectCalls);
        stop.Cancel();
        await loop.Completion;
    }

    [Fact]
    public async Task Timed_out_read_incident_contains_the_affected_paths_and_actual_opc_ua_node_ids()
    {
        await using var hub = new DataHub();
        var mesFlag = new TagDef { Name = "MESFlag", Address = "ns=3;s=OP020.StationEntrance.MESFlag", DataType = IgwDataType.Int16, ScanRateMs = 50 };
        var plcFlag = new TagDef { Name = "PLCFlag", Address = "ns=3;s=OP020.StationEntrance.PLCFlag", DataType = IgwDataType.Int16, ScanRateMs = 50 };
        const string mesFlagId = "PLC9.OP020.StationEntrance.MESFlag";
        const string plcFlagId = "PLC9.OP020.StationEntrance.PLCFlag";
        hub.Register(mesFlagId, mesFlag);
        hub.Register(plcFlagId, plcFlag);
        var demands = new ScanDemandRegistry(hub);
        using var demand = demands.Acquire("qc", [mesFlagId, plcFlagId], 50);
        var incidents = new ConnectionIncidentRegistry();
        var driver = new TimedOutReadDriver();
        var loop = new DeviceLoop(driver,
            [
                new TagBinding { TagId = mesFlagId, Def = mesFlag, Address = new TagAddress(0, 0, 2) },
                new TagBinding { TagId = plcFlagId, Def = plcFlag, Address = new TagAddress(0, 0, 2) },
            ],
            hub, NullLogger.Instance, "PLC9.OP020", scanDemands: demands,
            incidents: incidents, channelName: "PLC9", protocol: "OpcUa.Client");
        using var stop = new CancellationTokenSource();

        loop.Start(stop.Token);
        await WaitUntilAsync(() => incidents.Current("PLC9.OP020") is not null, TimeSpan.FromSeconds(2));

        var incident = incidents.Current("PLC9.OP020")!;
        Assert.Equal(ConnectionState.Connected, driver.State);
        Assert.Contains("BadRequestTimeout", incident.RawError);
        Assert.Contains($"IGW路径={mesFlagId}", incident.RawError);
        Assert.Contains($"NodeId={mesFlag.Address}", incident.RawError);
        Assert.Contains($"IGW路径={plcFlagId}", incident.RawError);
        Assert.Contains($"NodeId={plcFlag.Address}", incident.RawError);
        stop.Cancel();
        await loop.Completion;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Successful_opc_ua_batch_log_exports_queue_and_protocol_request_timings(bool hasNetwork)
    {
        await using var hub = new DataHub();
        const string tagId = "PLC9.OP020.DB.Value";
        var def = new TagDef
        {
            Name = "Value", Address = "ns=3;s=DB.Value", DataType = IgwDataType.Int16, ScanRateMs = 50,
        };
        hub.Register(tagId, def);
        var demands = new ScanDemandRegistry(hub);
        using var demand = demands.Acquire("mes", [tagId], 50);
        var logger = new CollectingLogger();
        var loop = new DeviceLoop(new ProtocolLoggingDriver(fail: false),
            [new TagBinding { TagId = tagId, Def = def, Address = new TagAddress(0, 0, 2) }],
            hub, logger, "PLC9.OP020", scanDemands: demands,
            channelName: "PLC9", protocol: "OpcUa.Client", networkWindow: hasNetwork ? DiagnosticNetwork : null);
        using var stop = new CancellationTokenSource();

        loop.Start(stop.Token);
        await WaitUntilAsync(() => logger.Messages.Any(message => message.Contains("读取批次完成")),
            TimeSpan.FromSeconds(2));
        stop.Cancel();
        await loop.Completion;

        string diagnostic = logger.Messages.Last(message => message.Contains("读取批次完成"));
        Assert.Contains("实际协议请求数=1", diagnostic);
        Assert.Contains("请求累计=", diagnostic);
        Assert.Contains("请求最大=", diagnostic);
        Assert.Contains("共享队列等待=12ms", diagnostic);
        Assert.Contains("失败请求=0", diagnostic);
        Assert.Contains("取消请求=0", diagnostic);
        AssertNetworkDiagnostic(diagnostic, hasNetwork, loop.ScanHistory(200, null).Last());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_opc_ua_batch_log_exports_failed_request_and_queue_timings(bool hasNetwork)
    {
        await using var hub = new DataHub();
        const string tagId = "PLC9.OP020.DB.Value";
        var def = new TagDef
        {
            Name = "Value", Address = "ns=3;s=DB.Value", DataType = IgwDataType.Int16, ScanRateMs = 50,
        };
        hub.Register(tagId, def);
        var demands = new ScanDemandRegistry(hub);
        using var demand = demands.Acquire("mes", [tagId], 50);
        var logger = new CollectingLogger();
        var loop = new DeviceLoop(new ProtocolLoggingDriver(fail: true),
            [new TagBinding { TagId = tagId, Def = def, Address = new TagAddress(0, 0, 2) }],
            hub, logger, "PLC9.OP020", scanDemands: demands,
            channelName: "PLC9", protocol: "OpcUa.Client", networkWindow: hasNetwork ? DiagnosticNetwork : null);
        using var stop = new CancellationTokenSource();

        loop.Start(stop.Token);
        await WaitUntilAsync(() => logger.Messages.Any(message => message.Contains("读取批次异常")),
            TimeSpan.FromSeconds(2));
        stop.Cancel();
        await loop.Completion;

        string diagnostic = logger.Messages.Last(message => message.Contains("读取批次异常"));
        Assert.Contains("实际协议请求数=1", diagnostic);
        Assert.Contains("请求累计=", diagnostic);
        Assert.Contains("请求最大=", diagnostic);
        Assert.Contains("共享队列等待=12ms", diagnostic);
        Assert.Contains("失败请求=1", diagnostic);
        Assert.Contains("取消请求=0", diagnostic);
        AssertNetworkDiagnostic(diagnostic, hasNetwork, loop.ScanHistory(200, null).First());
    }

    private static NetworkProbeWindow DiagnosticNetwork(DateTimeOffset at) =>
        new(at.AddSeconds(-30), at, 8, 6, 2, 4, 6, 4, 25, "degraded", "packet loss", true);

    [Fact]
    public async Task Throwing_success_batch_logger_preserves_good_points_record_and_retry_state_and_scans_continue()
    {
        await using var hub = new DataHub();
        const string tagId = "PLC9.OP020.DB.Value";
        var def = new TagDef { Name = "Value", Address = "ns=3;s=DB.Value", DataType = IgwDataType.Int16, ScanRateMs = 30 };
        hub.Register(tagId, def);
        var demands = new ScanDemandRegistry(hub);
        using var demand = demands.Acquire("mes", [tagId], 30);
        var logger = new ThrowOnceBatchLogger();
        var continueReads = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var driver = new ProtocolLoggingDriver(false, continueReads.Task);
        var loop = new DeviceLoop(driver, [new TagBinding { TagId = tagId, Def = def, Address = new(0, 0, 2) }],
            hub, logger, "PLC9.OP020", scanDemands: demands);
        using var stop = new CancellationTokenSource();
        try
        {
            loop.Start(stop.Token);
            await WaitUntilAsync(() => logger.Throws == 1 && driver.ReadCalls >= 2, TimeSpan.FromSeconds(3));
            Assert.True(hub.TryGet(tagId, out var point));
            Assert.True(point.IsGood);
            Assert.Equal(0, loop.Stats.ReadsErr);
            Assert.Equal(0, loop.Stats.SameSessionRetries);
            Assert.Equal(0, loop.Stats.BatchDownshifts);
            Assert.DoesNotContain(logger.Messages, message => message.Contains("读取批次异常"));
            var record = Assert.Single(loop.ScanHistory(200, null));
            Assert.Equal("good", record.Status);
            Assert.Null(record.Error);
            Assert.Equal(0, record.SameSessionRetries);
            Assert.Equal(0, record.BatchDownshifts);
            continueReads.SetResult();
            await WaitUntilAsync(() => loop.ScanHistory(200, null).Count >= 3, TimeSpan.FromSeconds(2));
            Assert.All(loop.ScanHistory(200, null), batch => Assert.Equal("good", batch.Status));
        }
        finally { stop.Cancel(); await loop.Completion; }
    }

    private sealed class ThrowOnceBatchLogger : ILogger
    {
        public readonly ConcurrentQueue<string> Messages = new();
        public int Throws;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            string message = formatter(state, exception);
            Messages.Enqueue(message);
            if (message.Contains("读取批次完成") && Interlocked.CompareExchange(ref Throws, 1, 0) == 0)
                throw new InvalidOperationException("diagnostic provider failed once");
        }
    }

    private static void AssertNetworkDiagnostic(string diagnostic, bool hasNetwork, ScanTimingRecord record)
    {
        Assert.Contains(hasNetwork ? "网络探测状态=degraded" : "网络探测状态=未提供", diagnostic);
        Assert.Contains(hasNetwork ? "网络RTT=4ms" : "网络RTT=未提供", diagnostic);
        Assert.Contains(hasNetwork ? "窗口丢包率=25%" : "窗口丢包率=未提供", diagnostic);
        Assert.Contains("归因=证据不足", diagnostic);
        Assert.Contains("置信度=low", diagnostic);
        Assert.Contains($"归因证据={string.Join(" | ", record.Attribution!.Evidence)}", diagnostic);
        Assert.DoesNotContain("网络RTT=0", diagnostic);
        Assert.DoesNotContain("窗口丢包率=0", diagnostic);
    }

    [Fact]
    public async Task Partially_bad_array_keeps_good_elements_and_reports_actual_failed_indices()
    {
        await using var hub = new DataHub();
        const string tagId = "Line.Plc.DB.Values";
        var def = new TagDef
        {
            Name = "Values", Address = "DB1.DBW0", DataType = IgwDataType.Int16, ScanRateMs = 500,
            IsArray = true,
            Array = new ArrayDef
            {
                ElementType = IgwDataType.Int16, Dimensions = [200], LowerBounds = [1], ElementStrideBytes = 2,
            },
        };
        hub.Register(tagId, def);
        var demands = new ScanDemandRegistry(hub);
        using var demand = demands.Acquire("test", [tagId], 500);
        var incidents = new ConnectionIncidentRegistry();
        var driver = new PartialArrayDriver(def.Array.Shape);
        var loop = new DeviceLoop(driver,
            [new TagBinding { TagId = tagId, Def = def, Address = new TagAddress(1, 0, 400) }],
            hub, NullLogger.Instance, "Line.Plc", scanDemands: demands,
            incidents: incidents, channelName: "Line", protocol: "Array");
        using var stop = new CancellationTokenSource();

        loop.Start(stop.Token);
        await WaitUntilAsync(() => loop.Stats.ArrayBad == 20 && incidents.Current("Line.Plc") is not null,
            TimeSpan.FromSeconds(2));

        Assert.True(hub.TryGet(tagId, out var cached));
        var array = Assert.IsAssignableFrom<ArrayRuntimeValue>(cached.Value);
        Assert.Equal(180, array.GoodCount);
        Assert.Equal(ConnectionState.Connected, driver.State);
        Assert.Equal(0, driver.DisconnectCalls);
        Assert.Equal(180, loop.Stats.ArrayGood);
        Assert.Contains("[100..119]", loop.Stats.ArrayFailedRanges);
        var incident = incidents.Current("Line.Plc");
        Assert.NotNull(incident);
        Assert.Equal(ConnectionDiagnosticCategory.PartialDataFailure, incident.Category);
        Assert.Equal(ConnectionState.Connected, incident.State);
        Assert.Contains("[100..119]", incident.RawError);
        stop.Cancel();
        await loop.Completion;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Timed out waiting for device loop.");
            await Task.Delay(10);
        }
    }

    private sealed class DbIsolationDriver : IDeviceDriver
    {
        public string DriverId => "Siemens.S7";
        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser { get; } = new SiemensScanAddressParser();
        public DriverLimits Limits { get; } = new() { MaxReadUnits = 4096, MaxGap = 0, MaxTagsPerRead = 1000 };
        public List<string[]> ReadBatches { get; } = new();
        public int DisconnectCalls { get; private set; }

        public Task ConnectAsync(CancellationToken ct)
        {
            State = ConnectionState.Connected;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            DisconnectCalls++;
            State = ConnectionState.Disconnected;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
        {
            ReadBatches.Add(tags.Select(tag => tag.Def.Address).ToArray());
            var now = DateTime.UtcNow;
            return Task.FromResult<IReadOnlyList<TagSample>>(tags.Select(tag =>
                new TagSample(tag.TagId, tag.Def.Address.StartsWith("DB2100.", StringComparison.OrdinalIgnoreCase)
                    ? DataValue.BadAt(StatusCode.Bad, now)
                    : DataValue.GoodAt((short)42, now))).ToArray());
        }

        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WriteResult>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SlowBadDbDriver : IDeviceDriver
    {
        private int _badReads;
        private int _goodReads;
        public string DriverId => "Siemens.S7";
        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser { get; } = new SiemensScanAddressParser();
        public DriverLimits Limits { get; } = new() { MaxReadUnits = 4096, MaxGap = 0, MaxTagsPerRead = 1000 };
        public int BadReads => Volatile.Read(ref _badReads);
        public int GoodReads => Volatile.Read(ref _goodReads);

        public Task ConnectAsync(CancellationToken ct)
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

        public async Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
        {
            bool badDb = tags[0].Def.Address.StartsWith("DB2100.", StringComparison.OrdinalIgnoreCase);
            if (badDb)
            {
                Interlocked.Increment(ref _badReads);
                await Task.Delay(120, ct);
            }
            else Interlocked.Increment(ref _goodReads);

            var now = DateTime.UtcNow;
            return tags.Select(tag => new TagSample(tag.TagId, badDb
                ? DataValue.BadAt(StatusCode.Bad, now)
                : DataValue.GoodAt((short)42, now))).ToArray();
        }

        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WriteResult>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TimedOutReadDriver : IDeviceDriver
    {
        public string DriverId => "OpcUa.Client";
        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser { get; } = new TimeoutAddressParser();
        public DriverLimits Limits { get; } = new() { MaxTagsPerRead = 512 };

        public Task ConnectAsync(CancellationToken ct)
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

        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct) =>
            throw new DriverOperationException(
                "读取请求失败；会话仍在线；[80850000] (BadRequestTimeout)",
                DriverFailureScope.Capacity,
                DriverRetryDisposition.ReduceBatchAndRetry,
                new StatusCode(0x8085_0000));

        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WriteResult>>([]);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class TimeoutAddressParser : IAddressParser
        {
            public bool TryParse(string address, IgwDataType type, out TagAddress result, out string? error)
            {
                result = new TagAddress(0, 0, 2);
                error = null;
                return true;
            }
        }
    }

    private sealed class ProtocolLoggingDriver(bool fail, Task? continueReads = null) : IDeviceDriver
    {
        public int ReadCalls;
        public string DriverId => "OpcUa.Client";
        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser { get; } = new TimeoutAddressParser();
        public DriverLimits Limits { get; } = new() { MaxTagsPerRead = 512 };

        public Task ConnectAsync(CancellationToken ct)
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

        public async Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
        {
            if (Interlocked.Increment(ref ReadCalls) > 1 && continueReads is not null)
                await continueReads.WaitAsync(ct);
            ProtocolOperationTiming.AddQueue(12);
            return await ProtocolOperationTiming.MeasureAsync<IReadOnlyList<TagSample>>(async () =>
            {
                await Task.Yield();
                if (fail)
                    throw new DriverOperationException("现场协议请求失败", DriverFailureScope.Block,
                        DriverRetryDisposition.RetrySameConnection, new StatusCode(0x8085_0000));
                var now = DateTime.UtcNow;
                return tags.Select(tag => new TagSample(tag.TagId, DataValue.GoodAt((short)42, now))).ToArray();
            });
        }

        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WriteResult>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class TimeoutAddressParser : IAddressParser
        {
            public bool TryParse(string address, IgwDataType type, out TagAddress result, out string? error)
            {
                result = new TagAddress(0, 0, 2);
                error = null;
                return true;
            }
        }
    }

    private sealed class SequenceDriver(IReadOnlyList<bool> goodReads, bool reportDbDetail = true) : IDeviceDriver, IDeviceConnectionDiagnostics, IRemoteEndpointDiagnostics
    {
        private int _readCalls;
        private volatile bool _lastReadGood = true;
        public string DriverId => "Sequence";
        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser { get; } = new Parser();
        public DriverLimits Limits { get; } = new() { MaxTagsPerRead = 1 };
        public int ReadCalls => Volatile.Read(ref _readCalls);
        public int DisconnectCalls { get; private set; }

        public Task ConnectAsync(CancellationToken ct)
        {
            State = ConnectionState.Connected;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            DisconnectCalls++;
            State = ConnectionState.Disconnected;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
        {
            int index = Interlocked.Increment(ref _readCalls) - 1;
            bool good = index < goodReads.Count ? goodReads[index] : true;
            _lastReadGood = good;
            DataValue value = good
                ? DataValue.GoodAt((short)1, DateTime.UtcNow)
                : DataValue.BadAt(StatusCode.BadCommFailure, DateTime.UtcNow);
            return Task.FromResult<IReadOnlyList<TagSample>>([new TagSample(tags[0].TagId, value)]);
        }

        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WriteResult>>([]);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public DeviceConnectionDetail ConnectionDetail() => _lastReadGood || !reportDbDetail
            ? new DeviceConnectionDetail("Connected", "连接正常")
            : new DeviceConnectionDetail("DbAddressMismatch", "DB2100 地址不存在或长度不匹配");

        public RemoteEndpointSnapshot RemoteEndpoint() => new(
            "opc.tcp://10.217.152.80:4840",
            "opc.tcp://10.217.152.80:4840",
            "opc.tcp://127.0.0.1:52000");

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

    private sealed class MixedSequenceDriver : IDeviceDriver, IDeviceConnectionDiagnostics
    {
        private int _readCalls;
        public string DriverId => "Sequence";
        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser { get; } = new Parser();
        public DriverLimits Limits { get; } = new() { MaxTagsPerRead = 16 };
        public int ReadCalls => Volatile.Read(ref _readCalls);
        public int DisconnectCalls { get; private set; }

        public Task ConnectAsync(CancellationToken ct)
        {
            State = ConnectionState.Connected;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            DisconnectCalls++;
            State = ConnectionState.Disconnected;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
        {
            int call = Interlocked.Increment(ref _readCalls);
            var now = DateTime.UtcNow;
            IReadOnlyList<TagSample> result = tags.Select((tag, index) =>
                new TagSample(tag.TagId, call == 1 && index == 1
                    ? DataValue.BadAt(StatusCode.Bad, now)
                    : DataValue.GoodAt((short)(index + 1), now))).ToArray();
            return Task.FromResult(result);
        }

        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WriteResult>>([]);

        public DeviceConnectionDetail ConnectionDetail() => new("Connected", "连接正常");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

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

    private sealed class PartialArrayDriver(ArrayShape shape) : IDeviceDriver, IArrayCapableDriver
    {
        public string DriverId => "Array";
        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser { get; } = new Parser();
        public DriverLimits Limits { get; } = new() { MaxTagsPerRead = 1 };
        public ArrayDriverCapabilities ArrayCapabilities { get; } =
            new(true, true, true, true, true, false, 64, 128);
        public int DisconnectCalls { get; private set; }

        public Task ConnectAsync(CancellationToken ct)
        {
            State = ConnectionState.Connected;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            DisconnectCalls++;
            State = ConnectionState.Disconnected;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
        {
            var quality = Enumerable.Repeat(StatusCode.Good, checked((int)shape.ElementCount)).ToArray();
            for (int index = 99; index <= 118; index++) quality[index] = StatusCode.BadCommFailure;
            var array = PrimitiveArrayValue<short>.Create(shape, new short[checked((int)shape.ElementCount)], quality);
            return Task.FromResult<IReadOnlyList<TagSample>>(
                [new TagSample(tags[0].TagId, new DataValue(array, array.OverallQuality, DateTime.UtcNow, DateTime.UtcNow))]);
        }

        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WriteResult>>([]);
        public Task<ArrayWriteResult> WriteArrayAsync(ArrayWriteRequest request, CancellationToken ct) =>
            Task.FromResult(new ArrayWriteResult(StatusCode.Good, request.Value.Count, []));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class Parser : IAddressParser
        {
            public bool TryParse(string address, IgwDataType type, out TagAddress result, out string? error)
            {
                result = new TagAddress(1, 0, 400);
                error = null;
                return true;
            }
        }
    }

    private sealed class CollectingLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Messages) Messages.Add(formatter(state, exception));
        }
    }
}
