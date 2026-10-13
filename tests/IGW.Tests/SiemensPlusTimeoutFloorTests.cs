using IGW.Abstractions;
using IGW.Drivers.IoTClient;
using Xunit;

namespace IGW.Tests;

/// <summary>
/// S7 Plus 的符号地址必须先向 PLC 取 DB 列表/类型信息才能解析，大工程上这一步是秒级的。
/// 现场 32 万点的设备按 Classic 那套 2~3 秒超时判定，正常解析被判成「连接失效」，
/// 断开后缓存重建更慢，陷入断线-重连自锁。所以 Plus 通道的套接字超时有下限。
/// </summary>
public sealed class SiemensPlusTimeoutFloorTests
{
    [Fact]
    public async Task A_short_device_timeout_is_raised_to_the_plus_floor()
    {
        var plus = new RecordingPlus();
        var driver = new SiemensHybridDeviceDriver("plc", 102, TimeSpan.FromSeconds(3), true,
            new IdleClassic(), plus,
            configuredPoints: [("Line.Plc.Flag", new TagDef { Address = "DB1.Flag", DataType = IgwDataType.Bool })]);

        await driver.ConnectAsync(default);

        Assert.Equal(SiemensHybridDeviceDriver.PlusMinimumTimeout, plus.Timeout);
    }

    [Fact]
    public async Task A_longer_device_timeout_wins()
    {
        var configured = TimeSpan.FromSeconds(40);
        var plus = new RecordingPlus();
        var driver = new SiemensHybridDeviceDriver("plc", 102, configured, true,
            new IdleClassic(), plus,
            configuredPoints: [("Line.Plc.Flag", new TagDef { Address = "DB1.Flag", DataType = IgwDataType.Bool })]);

        await driver.ConnectAsync(default);

        Assert.Equal(configured, plus.Timeout);
    }

    private sealed class RecordingPlus : ISiemensPlusTransport
    {
        public TimeSpan Timeout { get; private set; }
        public bool IsConnected { get; private set; }

        public Task ConnectAsync(string host, ushort port, TimeSpan timeout, bool legacy, CancellationToken ct)
        {
            Timeout = timeout;
            IsConnected = true;
            return Task.CompletedTask;
        }

        public Task AuthenticateAsync(string username, string password, CancellationToken ct) => Task.CompletedTask;
        public Task ProbeAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<SiemensPlusNativeBrowseTag>> BrowseAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SiemensPlusNativeBrowseTag>>([]);
        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TagSample>>([]);
        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WriteResult>>([]);
        public Task DisconnectAsync() { IsConnected = false; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class IdleClassic : ISiemensClassicTransport
    {
        public bool IsConnected { get; private set; }
        public Task ConnectAsync(CancellationToken ct) { IsConnected = true; return Task.CompletedTask; }
        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TagSample>>([]);
        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WriteResult>>([]);
        public Task DisconnectAsync() { IsConnected = false; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
