using IGW.Abstractions;
using IGW.Drivers.IoTClient;
using Xunit;

namespace IGW.Tests;

/// <summary>
/// 导入不带 DB 号的 DB 源文件时，按块名从 PLC 在线查 DB 号：
/// 设备走 S7 Plus 时借用现有会话；走 S7 Classic 时临时另开一条 Plus 连接，列完立即断开。
/// </summary>
public sealed class SiemensDataBlockDirectoryTests
{
    private static readonly SiemensPlusNativeDataBlock[] Blocks =
        [new("ST430_Data Collection_A(数据收集)", 4306), new("OP051_MES", 51)];

    [Fact]
    public async Task Plus_session_lists_data_blocks_through_the_existing_connection()
    {
        var plus = new FakePlus(Blocks);
        var driver = new SiemensHybridDeviceDriver("plc", 102, TimeSpan.FromSeconds(1), true,
            new NoClassic(), plus, plusFactory: () => throw new InvalidOperationException("不应另开连接"),
            configuredPoints: [("Line.Plc.Value", new TagDef { Address = "\"ST430\".Value" })]);

        var blocks = await driver.ListDataBlocksAsync(default);

        Assert.Equal([("ST430_Data Collection_A(数据收集)", 4306), ("OP051_MES", 51)], blocks!.Select(b => (b.Name, b.Number)).ToArray());
        Assert.Equal(1, plus.Connects);
        await driver.DisposeAsync();
    }

    [Fact]
    public async Task Classic_session_opens_a_temporary_plus_connection_authenticates_and_closes_it()
    {
        var sessionPlus = new FakePlus(Blocks);
        var temporary = new FakePlus(Blocks);
        var driver = new SiemensHybridDeviceDriver("plc", 102, TimeSpan.FromSeconds(1), true,
            new NoClassic(), sessionPlus, new Credentials(), plusFactory: () => temporary,
            configuredPoints: [("Line.Plc.Value", new TagDef { Address = "DB4306.DBW0" })]);

        var blocks = await driver.ListDataBlocksAsync(default);

        Assert.Equal(4306, blocks!.Single(b => b.Name.StartsWith("ST430")).Number);
        Assert.Equal(0, sessionPlus.Connects);                 // 设备的 Classic 会话不受影响
        Assert.Equal((1, "mes", true, true), (temporary.Connects, temporary.User, temporary.Disconnected, temporary.Disposed));
        await driver.DisposeAsync();
    }

    [Fact]
    public async Task Older_native_library_or_classic_only_plc_returns_null_so_the_user_types_the_number()
    {
        var old = new SiemensHybridDeviceDriver("plc", 102, TimeSpan.FromSeconds(1), true,
            new NoClassic(), new FakePlus(null), plusFactory: () => new FakePlus(null),
            configuredPoints: [("Line.Plc.Value", new TagDef { Address = "\"ST430\".Value" })]);
        Assert.Null(await old.ListDataBlocksAsync(default));
        await old.DisposeAsync();

        var classicOnly = new SiemensHybridDeviceDriver("plc", 102, TimeSpan.FromSeconds(1), false,
            new NoClassic(), new FakePlus(Blocks));
        Assert.Null(await classicOnly.ListDataBlocksAsync(default));
        await classicOnly.DisposeAsync();
    }

    private sealed class Credentials : ISiemensCredentialResolver
    {
        public Task<SiemensCredential?> ResolveAsync(CancellationToken ct) => Task.FromResult<SiemensCredential?>(new("mes", "secret"));
    }

    private sealed class FakePlus(IReadOnlyList<SiemensPlusNativeDataBlock>? blocks) : ISiemensPlusTransport
    {
        public int Connects;
        public string? User;
        public bool Disconnected, Disposed;
        public bool IsConnected { get; private set; }
        public Task ConnectAsync(string host, ushort port, TimeSpan timeout, bool legacy, CancellationToken ct)
        { Connects++; IsConnected = true; return Task.CompletedTask; }
        public Task AuthenticateAsync(string username, string password, CancellationToken ct) { User = username; return Task.CompletedTask; }
        public Task ProbeAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<SiemensPlusNativeDataBlock>?> ListDataBlocksAsync(CancellationToken ct) => Task.FromResult(blocks);
        public Task<IReadOnlyList<SiemensPlusNativeBrowseTag>> BrowseAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) => throw new NotSupportedException();
        public Task DisconnectAsync() { Disconnected = true; IsConnected = false; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class NoClassic : ISiemensClassicTransport
    {
        public bool IsConnected => false;
        public Task ConnectAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
