using IGW.Abstractions;
using IGW.Drivers.IoTClient;
using Xunit;

namespace IGW.Tests;

/// <summary>
/// 驱动必须向引擎声明自己支持订阅，否则那条链路一次都不会被调用。
///
/// 1.6.86 上就是这样：ABI v4、原生库导出、传输层租约、驱动的 SubscribeAsync 全做了，
/// 唯独 Capabilities 里漏了 SupportsSubscriptions，而引擎的
/// ReconcileRealtimeSubscriptionAsync 第一个条件就是它，于是订阅从未生效，
/// 现场日志里连一条订阅记录都没有。
///
/// 只有 S7-1200/1500 且原生库可用时才声明支持：纯 Classic 设备没有订阅机制，
/// 声明了只会让引擎每轮白跑一次协调。
/// </summary>
public sealed class SiemensSubscriptionCapabilityTests
{
    [Fact]
    public async Task A_plus_capable_device_tells_the_engine_it_can_subscribe()
    {
        await using var driver = Driver(supportsPlus: true);

        Assert.True(driver.Capabilities.SupportsSubscriptions);
    }

    /// <summary>S7-300/400 只有绝对地址，没有订阅可言。</summary>
    [Fact]
    public async Task A_classic_only_device_does_not_claim_subscription_support()
    {
        await using var driver = Driver(supportsPlus: false);

        Assert.False(driver.Capabilities.SupportsSubscriptions);
    }

    /// <summary>声明支持不等于每次都能订上：没有符号点位时要给出空租约，让调用方继续轮询。</summary>
    [Fact]
    public async Task Claiming_support_still_yields_an_empty_lease_when_nothing_is_symbolic()
    {
        await using var driver = Driver(supportsPlus: true);

        var lease = await driver.SubscribeAsync(
            [Binding("Line.Plc.Absolute", "DB1.DBW0")], _ => { }, default);

        Assert.Empty(lease.TagIds);
        await lease.DisposeAsync();
    }

    private static SiemensHybridDeviceDriver Driver(bool supportsPlus) =>
        new("plc", 102, TimeSpan.FromSeconds(1), supportsPlus,
            new StubClassic(), new StubPlus());

    private static TagBinding Binding(string tagId, string address) => new()
    {
        TagId = tagId,
        Def = new TagDef { Name = tagId, Address = address, DataType = IgwDataType.Int16, ScanRateMs = 500 },
        Address = new TagAddress(1, 0, 2),
    };

    private sealed class StubClassic : ISiemensClassicTransport
    {
        public bool IsConnected { get; private set; }
        public Task ConnectAsync(CancellationToken ct) { IsConnected = true; return Task.CompletedTask; }
        public Task DisconnectAsync() { IsConnected = false; return Task.CompletedTask; }
        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TagSample>>([]);
        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WriteResult>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubPlus : ISiemensPlusTransport
    {
        public bool IsConnected { get; private set; }
        public Task ConnectAsync(string host, ushort port, TimeSpan timeout, bool legacy, CancellationToken ct)
        { IsConnected = true; return Task.CompletedTask; }
        public Task DisconnectAsync() { IsConnected = false; return Task.CompletedTask; }
        public Task AuthenticateAsync(string username, string password, CancellationToken ct) => Task.CompletedTask;
        public Task ProbeAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<SiemensPlusNativeBrowseTag>> BrowseAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SiemensPlusNativeBrowseTag>>([]);
        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TagSample>>([]);
        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WriteResult>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
