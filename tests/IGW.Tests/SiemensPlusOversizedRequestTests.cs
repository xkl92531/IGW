using IGW.Abstractions;
using IGW.Drivers.IoTClient;
using Xunit;

namespace IGW.Tests;

/// <summary>
/// 请求过大时 PLC 会直接掐断连接，而不是回「容量不足」。
///
/// 现场实测：同一台 S7-1500 上 93/118 点一个请求必断，46~56 点能成；而断连原先不缩小请求窗口，
/// 于是重连后又发同样大的请求、又被掐断，每分钟十几次连接失效，设备几乎采不上数据。
/// 所以断连也要当作容量压力处理，让自适应窗口收敛到 PLC 能接受的大小。
/// </summary>
public sealed class SiemensPlusOversizedRequestTests
{
    private static IReadOnlyList<TagBinding> Tags(int count) =>
        Enumerable.Range(0, count).Select(index => new TagBinding
        {
            TagId = "Line.Plc.T" + index,
            Def = new TagDef { Name = "T" + index, Address = "DB1.T" + index, DataType = IgwDataType.Int16 },
            Address = new TagAddress(0, 0, 2),
        }).ToArray();

    [Theory]
    [InlineData(SiemensPlusResultCode.TransportFailure)]
    [InlineData(SiemensPlusResultCode.Timeout)]
    public async Task A_connection_dropped_mid_read_halves_the_request_window(SiemensPlusResultCode code)
    {
        var window = new AdaptiveRequestWindow(96, 4096);
        var native = new DroppingFake(code);
        await using var transport = new SiemensPlusTransport(native, window);
        await transport.ConnectAsync("plc", 102, TimeSpan.FromSeconds(1), false, default);

        await Assert.ThrowsAsync<SiemensPlusException>(() => transport.ReadAsync(Tags(96), default));

        Assert.Equal(48, window.Current);
        Assert.Equal(96, native.AttemptedSize);
    }

    /// <summary>单点请求已经缩无可缩，再砍会把窗口砍成 0；这种断连纯属通信故障，不动窗口。</summary>
    [Fact]
    public async Task A_single_point_request_does_not_shrink_the_window()
    {
        var window = new AdaptiveRequestWindow(1, 4096);
        var native = new DroppingFake(SiemensPlusResultCode.TransportFailure);
        await using var transport = new SiemensPlusTransport(native, window);
        await transport.ConnectAsync("plc", 102, TimeSpan.FromSeconds(1), false, default);

        await Assert.ThrowsAsync<SiemensPlusException>(() => transport.ReadAsync(Tags(1), default));

        Assert.Equal(1, window.Current);
    }

    private sealed class DroppingFake(SiemensPlusResultCode code) : ISiemensPlusNativeApi
    {
        public int AttemptedSize { get; private set; }

        public SiemensPlusNativeReadResult Read(IReadOnlyList<string> symbols)
        {
            AttemptedSize = symbols.Count;
            return new SiemensPlusNativeReadResult(code, [], false);
        }

        public SiemensPlusResultCode Connect(string host, ushort port, uint timeoutMs, bool legacy) => SiemensPlusResultCode.Ok;
        public SiemensPlusResultCode Authenticate(string username, string password) => SiemensPlusResultCode.Ok;
        public SiemensPlusResultCode Probe() => SiemensPlusResultCode.Ok;
        public SiemensPlusNativeBrowseResult Browse() => new(SiemensPlusResultCode.Ok, []);
        public SiemensPlusResultCode Write(IReadOnlyList<SiemensPlusNativeWrite> writes) => SiemensPlusResultCode.Ok;
        public SiemensPlusResultCode Disconnect() => SiemensPlusResultCode.Ok;
        public void Dispose() { }
    }
}
