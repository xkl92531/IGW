using IGW.Abstractions;
using IGW.Drivers.IoTClient;
using Xunit;

namespace IGW.Tests;

/// <summary>PLC 程序重新下载后的自愈：原生层请求重建时，传输层断开会话，驱动下一轮重连并重新认证。</summary>
public sealed class SiemensPlusResyncTests
{
    [Fact]
    public async Task A_resync_request_disconnects_after_returning_the_values_already_read()
    {
        var native = new ResyncNative { Resync = true };
        await using var transport = new SiemensPlusTransport(native, new AdaptiveRequestWindow(8, 8, () => 0));
        await transport.ConnectAsync("plc", 102, TimeSpan.FromSeconds(1), false, default);

        var values = await transport.ReadAsync(Tags(3), default);

        Assert.All(values, value => Assert.True(value.Value.IsGood));
        Assert.False(transport.IsConnected);
        Assert.Equal(1, native.Disconnects);
        Assert.Contains("PLC 程序可能已重新下载", transport.LastReadError);
    }

    [Fact]
    public async Task Without_a_request_the_session_stays_connected()
    {
        var native = new ResyncNative();
        await using var transport = new SiemensPlusTransport(native);
        await transport.ConnectAsync("plc", 102, TimeSpan.FromSeconds(1), false, default);
        await transport.ReadAsync(Tags(3), default);
        Assert.True(transport.IsConnected);
        Assert.Equal(0, native.Disconnects);
    }

    [Fact]
    public async Task The_driver_reauthenticates_after_the_transport_dropped_its_session()
    {
        var plus = new CountingPlus();
        await using var driver = new SiemensHybridDeviceDriver("plc", 102, TimeSpan.FromSeconds(1), true,
            new NoClassic(), plus, new FixedCredential());
        await driver.ReadAsync([Binding("Line.Plc.Symbol", "PLC.Value")], default);
        Assert.Equal(1, plus.Authentications);

        plus.DropSession();   // 如传输层为自愈主动断开
        await driver.ReadAsync([Binding("Line.Plc.Symbol", "PLC.Value")], default);

        Assert.Equal(2, plus.Connects);
        Assert.Equal(2, plus.Authentications);
    }

    private static TagBinding[] Tags(int count) => Enumerable.Range(0, count).Select(i => new TagBinding
    {
        TagId = $"Line.Plc.T{i}", Def = new TagDef { Address = $"PLC.T{i}", DataType = IgwDataType.Int16 }, Address = new(0, 0, 2)
    }).ToArray();

    private static TagBinding Binding(string id, string address) => new()
    {
        TagId = id, Def = new TagDef { Address = address, DataType = IgwDataType.Int16 }, Address = new(0, 0, 2)
    };

    private sealed class ResyncNative : ISiemensPlusNativeApi
    {
        public bool Resync;
        public int Disconnects;
        public SiemensPlusResultCode Connect(string host, ushort port, uint timeoutMs, bool legacy) => SiemensPlusResultCode.Ok;
        public SiemensPlusResultCode Authenticate(string username, string password) => SiemensPlusResultCode.Ok;
        public SiemensPlusResultCode Probe() => SiemensPlusResultCode.Ok;
        public SiemensPlusNativeBrowseResult Browse() => new(SiemensPlusResultCode.Ok, []);
        public SiemensPlusNativeReadResult Read(IReadOnlyList<string> symbols) => new(SiemensPlusResultCode.Ok,
            symbols.Select(_ => new SiemensPlusNativeValue(SiemensPlusResultCode.Ok, (short)1)).ToArray());
        public SiemensPlusResultCode Write(IReadOnlyList<SiemensPlusNativeWrite> writes) => SiemensPlusResultCode.Ok;
        public SiemensPlusResultCode Disconnect() { Disconnects++; return SiemensPlusResultCode.Ok; }
        public bool TakeResyncRequest() { bool value = Resync; Resync = false; return value; }
        public void Dispose() { }
    }

    private sealed class FixedCredential : ISiemensCredentialResolver
    {
        public Task<SiemensCredential?> ResolveAsync(CancellationToken ct) =>
            Task.FromResult<SiemensCredential?>(new SiemensCredential("user", "secret"));
    }

    private sealed class CountingPlus : ISiemensPlusTransport
    {
        public int Connects, Authentications;
        public bool IsConnected { get; private set; }
        public void DropSession() => IsConnected = false;
        public Task ConnectAsync(string host, ushort port, TimeSpan timeout, bool legacy, CancellationToken ct)
        {
            Connects++;
            IsConnected = true;
            return Task.CompletedTask;
        }
        public Task AuthenticateAsync(string username, string password, CancellationToken ct)
        {
            Authentications++;
            return Task.CompletedTask;
        }
        public Task ProbeAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<SiemensPlusNativeBrowseTag>> BrowseAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SiemensPlusNativeBrowseTag>>([]);
        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TagSample>>(tags.Select(tag =>
                new TagSample(tag.TagId, DataValue.GoodAt((short)1, DateTime.UtcNow))).ToArray());
        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WriteResult>>([]);
        public Task DisconnectAsync() { IsConnected = false; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoClassic : ISiemensClassicTransport
    {
        public bool IsConnected => false;
        public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TagSample>>([]);
        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WriteResult>>([]);
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
