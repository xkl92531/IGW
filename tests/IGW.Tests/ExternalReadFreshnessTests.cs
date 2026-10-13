using IGW.Abstractions;
using IGW.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

/// <summary>
/// 外部 GET 读取：只采集流程用到的点以后，没在采集的点被外部系统读取时要先读 PLC，拿到最新值。
/// </summary>
public sealed class ExternalReadFreshnessTests
{
    [Fact]
    public async Task Scan_demand_membership_is_visible_per_tag()
    {
        await using var hub = new DataHub();
        hub.Register("Line.Plc.A", Def("A"));
        hub.Register("Line.Plc.B", Def("B"));
        var demands = new ScanDemandRegistry(hub);
        using var lease = demands.Acquire("mes:task", ["Line.Plc.A"], 200);

        Assert.True(demands.IsDemanded("Line.Plc.A"));
        Assert.False(demands.IsDemanded("Line.Plc.B"));
        Assert.False(demands.IsDemanded("Other.Plc.A"));
    }

    [Fact]
    public async Task Idle_tag_is_read_from_the_plc_before_an_external_read_returns()
    {
        var factory = new CountingFactory();
        var registry = new DriverRegistry();
        registry.Register(factory);
        await using var hub = new DataHub();
        var project = new IgwProject
        {
            Channels =
            [
                new ChannelDef
                {
                    Name = "Line", DriverId = factory.DriverId,
                    Devices = [new DeviceDef { Name = "Plc", Tags = [Def("Polled"), Def("Idle")] }],
                },
            ],
        };
        await using var engine = new RuntimeEngine(project, registry, hub, NullLoggerFactory.Instance);
        await engine.StartAsync();
        using var demand = engine.ScanDemands.Acquire("mes:task", ["Line.Plc.Polled"], 200);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (engine.DeviceStatuses().Any(status => status.State != ConnectionState.Connected))
            await Task.Delay(20, timeout.Token);

        Assert.True(engine.IsAcquiring("Line.Plc.Polled"));
        Assert.False(engine.IsAcquiring("Line.Plc.Idle"));
        Assert.False(engine.IsAcquiring("Line.Plc.Unknown"));
        factory.Driver!.Value = 99;

        await engine.RefreshIdleAsync(["Line.Plc.Idle", "Line.Plc.Idle", "Line.Plc.Unknown"], TimeSpan.FromSeconds(5), timeout.Token);

        Assert.True(hub.TryGet("Line.Plc.Idle", out var value));
        Assert.True(value.IsGood);
        Assert.Equal((short)99, value.Value);
        Assert.Contains("Line.Plc.Idle", factory.Driver.ReadTagIds);
        // 取一次当前值不再把点拉进活动窗口：一次诊断读上千个点、
        // 读完就走，没必要让它们被轮询 activeWindowSec。
        Assert.False(engine.IsAcquiring("Line.Plc.Idle"));

        // 持续盯屏的调用方显式要求时，照旧进活动窗口
        await engine.RefreshIdleAsync(["Line.Plc.Idle"], TimeSpan.FromSeconds(5), timeout.Token, activate: true);
        Assert.True(engine.IsAcquiring("Line.Plc.Idle"));
    }

    [Fact]
    public async Task Refresh_times_out_quietly_and_leaves_the_cached_value()
    {
        var factory = new CountingFactory { HoldReads = true };
        var registry = new DriverRegistry();
        registry.Register(factory);
        await using var hub = new DataHub();
        var project = new IgwProject
        {
            Channels = [new ChannelDef { Name = "Line", DriverId = factory.DriverId, Devices = [new DeviceDef { Name = "Plc", Tags = [Def("Idle")] }] }],
        };
        await using var engine = new RuntimeEngine(project, registry, hub, NullLoggerFactory.Instance);
        await engine.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (engine.DeviceStatuses().Any(status => status.State != ConnectionState.Connected))
            await Task.Delay(20, timeout.Token);

        var started = System.Diagnostics.Stopwatch.StartNew();
        await engine.RefreshIdleAsync(["Line.Plc.Idle"], TimeSpan.FromMilliseconds(200), timeout.Token);

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(3), $"elapsed={started.Elapsed}");
    }

    private static TagDef Def(string name) => new() { Name = name, Address = "D" + name, DataType = IgwDataType.Int16, ScanRateMs = 200 };

    private sealed class CountingFactory : IDriverFactory
    {
        public bool HoldReads { get; init; }
        public CountingDriver? Driver { get; private set; }
        public string DriverId => "Counting";
        public DriverDescriptor Describe() => new() { DriverId = DriverId, DisplayName = DriverId };
        public IDeviceDriver CreateDevice(ChannelDef channel, DeviceDef device) => Driver = new CountingDriver(HoldReads);
    }

    private sealed class CountingDriver(bool holdReads) : IDeviceDriver, IDriverCapabilityProvider
    {
        public volatile int Value = 1;
        public System.Collections.Concurrent.ConcurrentBag<string> ReadTagIds { get; } = [];
        public string DriverId => "Counting";
        public ConnectionState State { get; private set; }
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser { get; } = new Parser();
        public DriverLimits Limits { get; } = new() { MaxTagsPerRead = 100, MaxTagsPerWrite = 100 };
        public DriverCapabilitySnapshot Capabilities => DriverCapabilitySnapshot.FromLimits(Limits) with { MaxInFlight = 2 };

        public Task ConnectAsync(CancellationToken ct) { State = ConnectionState.Connected; StateChanged?.Invoke(State); return Task.CompletedTask; }
        public Task DisconnectAsync() { State = ConnectionState.Disconnected; StateChanged?.Invoke(State); return Task.CompletedTask; }

        public async Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
        {
            foreach (var tag in tags) ReadTagIds.Add(tag.TagId);
            if (holdReads) await Task.Delay(Timeout.Infinite, ct);
            var now = DateTime.UtcNow;
            return tags.Select(tag => new TagSample(tag.TagId, new DataValue((short)Value, StatusCode.Good, now, now))).ToArray();
        }

        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WriteResult>>(writes.Select(w => new WriteResult { TagId = w.TagId, Status = StatusCode.Good }).ToArray());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Parser : IAddressParser
    {
        public bool TryParse(string address, IgwDataType type, out TagAddress result, out string? error)
        {
            result = new(1, 0, 2);
            error = null;
            return true;
        }
    }
}
