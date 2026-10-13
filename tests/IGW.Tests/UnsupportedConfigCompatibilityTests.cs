using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using IGW.Drivers.Simulator;
using IGW.Northbound.Mes;
using Microsoft.Extensions.Logging;
using Xunit;

namespace IGW.Tests;

/// <summary>旧工程里本版本不支持的驱动和 MES 配置只停用自身，工程照常加载，其它设备和任务照常运行。</summary>
public sealed class UnsupportedConfigCompatibilityTests
{
    private const string Target = "Line.Plc.Ack";

    [Fact]
    public async Task Channel_with_unregistered_driver_is_skipped_with_warning_while_other_devices_run()
    {
        var project = new IgwProject
        {
            Channels =
            [
                new ChannelDef { Name = "Gun", DriverId = "Legacy.Tcp", Devices = [new DeviceDef { Name = "Tool", Endpoint = "127.0.0.1:4545",
                    Tags = [new TagDef { Name = "Torque", Address = "Torque", DataType = IgwDataType.Double, Access = TagAccess.Read }] }] },
                new ChannelDef { Name = "Line", DriverId = SimulatorDriverFactory.Id, Devices = [new DeviceDef { Name = "Plc",
                    Tags = [new TagDef { Name = "Counter", Address = "RAMP", DataType = IgwDataType.Int32 }] }] },
            ],
        };
        ProjectStore.Validate(project);
        var registry = new DriverRegistry();
        registry.Register(new SimulatorDriverFactory());
        var logs = new CapturingLoggerFactory();
        var hub = new DataHub();
        await using var engine = new RuntimeEngine(project, registry, hub, logs);
        await engine.StartAsync();

        engine.Touch("Line.Plc.Counter");
        await Until(() => hub.TryGet("Line.Plc.Counter", out var value) && value.IsGood);
        Assert.Equal("Line.Plc", Assert.Single(engine.DeviceStatuses()).Name);
        Assert.False(hub.TryGetDef("Gun.Tool.Torque", out _));
        Assert.Contains(logs.Entries, entry => entry.Level == LogLevel.Warning && entry.Message == "通道 Gun 的驱动 Legacy.Tcp 未注册,跳过");
    }

    [Fact]
    public async Task Task_with_unsupported_node_type_is_disabled_with_visible_reason_while_other_tasks_run()
    {
        var legacy = LegacyTask("""
            {"id":"legacy","name":"旧任务","collectIntervalMs":30,
             "flow":{"nodes":[{"id":"trigger","type":"Trigger"},
               {"id":"command","type":"LegacyCommand","command":{"action":"Send"}},
               {"id":"upload","type":"Upload","http":{"url":"http://local/legacy"}}],
              "edges":[{"from":"trigger","to":"command"},{"from":"command","to":"upload"}]}}
            """);
        await AssertOnlyLegacyTaskIsDisabled(legacy, "含本版本不支持的配置(节点类型 LegacyCommand)，任务已停用");
    }

    [Fact]
    public async Task Task_with_unsupported_trigger_is_disabled_and_never_falls_back_to_periodic_upload()
    {
        var legacy = LegacyTask("""
            {"id":"legacy","name":"旧事件任务","collectIntervalMs":30,"trigger":{"mode":"LegacyEvent"},
             "flow":{"nodes":[{"id":"trigger","type":"Trigger"},{"id":"upload","type":"Upload","http":{"url":"http://local/legacy"}}],
              "edges":[{"from":"trigger","to":"upload"}]}}
            """);
        await AssertOnlyLegacyTaskIsDisabled(legacy, "含本版本不支持的配置(触发方式 LegacyEvent)，任务已停用");
    }

    [Fact]
    public async Task Unknown_node_type_never_runs_the_downstream_actions()
    {
        var task = new MesTask { Id = "mystery", CollectIntervalMs = 30, Flow = new() {
            Nodes = [new() { Id = "trigger", Type = "Trigger" }, new() { Id = "unknown", Type = "Mystery" }, WritebackNode("action")],
            Edges = [new() { From = "trigger", To = "unknown" }, new() { From = "unknown", To = "action" }] } };
        int writes = 0;
        await using var hub = Hub();
        await using var engine = new MesUploadEngine(hub, new() { Tasks = [task], LogDbPath = "" },
            (_, _, _) => { Interlocked.Increment(ref writes); return Task.FromResult(true); }, new CapturingLogger());
        await Task.Delay(300);
        var status = engine.Snapshot().Single();
        Assert.False(status.Enabled);
        Assert.Equal("含本版本不支持的配置(节点类型 Mystery)，任务已停用", status.LastError);
        Assert.Equal(0, Volatile.Read(ref writes));
    }

    [Fact]
    public void Old_project_with_unsupported_configuration_still_loads_and_validates()
    {
        var project = JsonSerializer.Deserialize<IgwProject>("""
            {"channels":[{"name":"Gun","driverId":"Legacy.Tcp","devices":[{"name":"Tool","endpoint":"127.0.0.1:4545",
               "settings":{"keepAliveMs":"5000"},
               "tags":[{"name":"Code","address":"Code","dataType":"String","access":"Write","stringLength":25,
                 "writeConfirmation":{"mode":"ProtocolAcknowledged"}}]}]}],
             "mesUpload":{"tasks":[
               {"id":"command","name":"指令","flow":{"nodes":[{"id":"n","type":"LegacyCommand","command":{"action":"Custom"}}],"edges":[]}},
               {"id":"event","name":"事件","trigger":{"mode":"LegacyEvent"}},
               {"id":"source","name":"旧来源","payload":{"extraFields":[{"name":"sn","source":"Legacy","path":"barcode"}]},
                "flow":{"nodes":[{"id":"c","type":"Condition","branches":[{"conditions":[{"source":"Legacy","path":"state","value":"ready"}]}]}],"edges":[]}}]}}
            """, ProjectStore.Options)!;
        ProjectStore.Validate(project);
        Assert.Equal(
            ["含本版本不支持的配置(节点类型 LegacyCommand)，任务已停用", "含本版本不支持的配置(触发方式 LegacyEvent)，任务已停用",
             "含本版本不支持的配置(取值来源 Legacy)，任务已停用"],
            project.MesUpload!.Tasks.Select(MesUnsupportedConfig.Describe));
        Assert.Null(MesUnsupportedConfig.Describe(new MesTask { Trigger = new() { Mode = "Interval" }, Flow = new() {
            Nodes = [new() { Type = "Trigger" }, new() { Type = "Upload" }, new() { Type = "ResponseWriteback" }] } }));
    }

    private static async Task AssertOnlyLegacyTaskIsDisabled(MesTask legacy, string reason)
    {
        var normal = new MesTask { Id = "normal", Name = "正常任务", CollectIntervalMs = 30, Flow = new() {
            Nodes = [new() { Id = "trigger", Type = "Trigger" }, WritebackNode("action")],
            Edges = [new() { From = "trigger", To = "action" }] } };
        int writes = 0;
        var log = new CapturingLogger();
        using var handler = new CountingHandler();
        await using var hub = Hub();
        await using var engine = new MesUploadEngine(hub, new() { Tasks = [legacy, normal], LogDbPath = "" },
            (_, _, _) => { Interlocked.Increment(ref writes); return Task.FromResult(true); }, log, httpMessageHandler: handler);

        await Until(() => Volatile.Read(ref writes) >= 3);
        var status = engine.Snapshot().Single(s => s.Id == "legacy");
        Assert.False(status.Enabled);
        Assert.Equal(reason, status.LastError);
        var trigger = status.Nodes.Single(n => n.Id == "trigger");
        Assert.Equal("fail", trigger.State);
        Assert.Equal(reason, trigger.Info);
        Assert.Equal(0, handler.Requests);
        Assert.True(engine.Snapshot().Single(s => s.Id == "normal").Enabled);
        Assert.Contains(log.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains(reason));
    }

    private static MesTask LegacyTask(string json)
    {
        var task = JsonSerializer.Deserialize<MesTask>(json, ProjectStore.Options)!;
        Assert.NotNull(MesUnsupportedConfig.Describe(task));
        return task;
    }

    private static MesNode WritebackNode(string id) =>
        new() { Id = id, Type = "Writeback", WriteBack = new() { Manual = [new() { TagId = Target, Value = "1" }] } };

    private static DataHub Hub()
    {
        var hub = new DataHub();
        hub.Register(Target, new() { DataType = IgwDataType.Int32, Access = TagAccess.ReadWrite }, "Line.Plc");
        return hub;
    }

    private static async Task Until(Func<bool> done)
    { using var timeout = new CancellationTokenSource(6000); while (!done()) await Task.Delay(10, timeout.Token); }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"httpStatusCode\":200}") });
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, formatter(state, exception)));
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        private readonly CapturingLogger _logger = new();
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries => _logger.Entries;
        public ILogger CreateLogger(string categoryName) => _logger;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
    }
}
