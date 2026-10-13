using IGW.Abstractions;
using IGW.Drivers.IoTClient;
using Xunit;
namespace IGW.Tests;
public sealed class SiemensPlusAdaptiveBatchTests
{
    [Fact]
    public async Task Capacity_failure_retries_only_affected_symbols_on_same_session()
    {
        var native = new Native();
        await using var transport = new SiemensPlusTransport(native);
        await transport.ConnectAsync("plc", 102, TimeSpan.FromSeconds(1), false, default);
        var tags = Enumerable.Range(0, 100).Select(i => new TagBinding {
            TagId = "Line.Plc.T" + i, Def = new TagDef { Address = "PLC.T" + i, DataType = IgwDataType.Int16 }, Address = new(0, 0, 2)
        }).ToArray();
        using var timing = ProtocolOperationTiming.Begin();
        var samples = await transport.ReadAsync(tags, default);
        Assert.Equal(5, timing.Snapshot().Requests);
        Assert.Equal(1, timing.Snapshot().FailedRequests);
        Assert.Equal(new[] {32,32,32,4}, native.Successes);
        Assert.Equal(tags.Select(t => t.TagId), samples.Select(s => s.TagId));
        Assert.All(samples, s => Assert.True(s.Value.IsGood));
        Assert.Equal(0, native.Disconnects);
        Assert.Equal(0, native.Probes);
    }
    [Fact]
    public async Task Transport_failure_is_not_split_or_reconnected_by_the_reader()
    {
        var native = new Native { Failure = SiemensPlusResultCode.TransportFailure };
        await using var transport = new SiemensPlusTransport(native);
        await transport.ConnectAsync("plc", 102, TimeSpan.FromSeconds(1), false, default);
        var error = await Assert.ThrowsAsync<SiemensPlusException>(() => transport.ReadAsync([
            new() { TagId = "T", Def = new TagDef { Address = "PLC.T" }, Address = new(0,0,2) }], default));
        Assert.Equal(SiemensPlusResultCode.TransportFailure, error.Code);
        Assert.Equal(1, native.Reads);
        Assert.Equal(0, native.Disconnects);
    }
    [Fact]
    public async Task Failed_native_disconnect_does_not_claim_the_session_is_closed()
    {
        var native = new Native();
        var transport = new SiemensPlusTransport(native);
        await transport.ConnectAsync("plc", 102, TimeSpan.FromSeconds(1), false, default);
        native.DisconnectFailure = true;
        try
        {
            await Assert.ThrowsAsync<SiemensPlusException>(() => transport.DisconnectAsync());
            Assert.True(transport.IsConnected);
        }
        finally { native.DisconnectFailure = false; await transport.DisposeAsync(); }
    }

    [Theory]
    [InlineData(32)]
    [InlineData(16)]
    public async Task Entire_failed_span_is_excluded_and_later_independent_requests_resume_learning(int limit)
    {
        long clock = 0;
        var window = new AdaptiveRequestWindow(64, 128, () => clock);
        var native = new Native { Limit = limit };
        await using var transport = new SiemensPlusTransport(native, window);
        await transport.ConnectAsync("plc", 102, TimeSpan.FromSeconds(1), false, default);
        var tags = Enumerable.Range(0, 64 + limit).Select(i => new TagBinding {
            TagId = "T" + i, Def = new TagDef { Address = "PLC.T" + i, DataType = IgwDataType.Int16 }, Address = new(0, 0, 2)
        }).ToArray();

        Assert.All(await transport.ReadAsync(tags, default), s => Assert.True(s.Value.IsGood));
        Assert.Equal(Enumerable.Repeat(limit, 64 / limit + 1), native.Successes);
        Assert.Equal(limit, window.Current);
        // Only the final independent request may count: neither 64 -> 32 + 32 child,
        // nor any nested 64 -> 32 -> 16 recovery child, is a normal success.
        clock = 60_000;
        for (int i = 0; i < 18; i++) Assert.False(window.OnSuccess(limit, 1));
        Assert.True(window.OnSuccess(limit, 1));
        Assert.Equal(0, native.Disconnects);
    }

    private sealed class Native : ISiemensPlusNativeApi
    {
        public List<int> Successes = [];
        public int Limit = 32;
        public int Disconnects, Reads, Probes;
        public SiemensPlusResultCode? Failure;
        public bool DisconnectFailure;
        public SiemensPlusResultCode Connect(string host, ushort port, uint timeoutMs, bool legacy) => SiemensPlusResultCode.Ok;
        public SiemensPlusResultCode Authenticate(string username, string password) => SiemensPlusResultCode.Ok;
        public SiemensPlusResultCode Probe() { Probes++; return SiemensPlusResultCode.Ok; }
        public SiemensPlusNativeBrowseResult Browse() => new(SiemensPlusResultCode.Ok, []);
        public SiemensPlusNativeReadResult Read(IReadOnlyList<string> symbols)
        {
            Reads++;
            if (Failure is {} code) return new(code, []);
            if (symbols.Count > Limit) return new(SiemensPlusResultCode.CapacityFailure, []);
            Successes.Add(symbols.Count);
            return new(SiemensPlusResultCode.Ok, symbols.Select(s => new SiemensPlusNativeValue(SiemensPlusResultCode.Ok, (short)42)).ToArray());
        }
        public SiemensPlusResultCode Write(IReadOnlyList<SiemensPlusNativeWrite> writes) => SiemensPlusResultCode.Ok;
        public SiemensPlusResultCode Disconnect() { Disconnects++; return DisconnectFailure ? SiemensPlusResultCode.TransportFailure : SiemensPlusResultCode.Ok; }
        public void Dispose() { }
    }
}
