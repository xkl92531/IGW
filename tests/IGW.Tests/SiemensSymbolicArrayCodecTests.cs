using IGW.Abstractions;
using IGW.Drivers.IoTClient;
using Xunit;

namespace IGW.Tests;

/// <summary>
/// S7 Plus 数组符号分片的自适应行为:大报文会被高负载 PLC 掐会话,
/// 分片窗口要在容量拒绝/断会话时自动收缩、成功后可增长。
/// </summary>
public sealed class SiemensSymbolicArrayCodecTests
{
    [Fact]
    public void Fixed_window_read_capacity_failure_returns_without_retrying_identical_request()
    {
        var native = new Native { Limit = 1, MaxCalls = 1 };
        var error = Assert.Throws<SiemensPlusException>(() => SiemensSymbolicArrayCodec.Read(native, Binding(2)));
        Assert.Equal(SiemensPlusResultCode.CapacityFailure, error.Code);
        Assert.Equal(1, native.Calls);
    }

    [Fact]
    public void Fixed_window_write_capacity_failure_reports_all_failed_elements_without_retry()
    {
        var native = new Native { Limit = 1, MaxCalls = 1 };
        var value = PrimitiveArrayValue<bool>.Create(ArrayShape.Create([2], [0]), [true, false]);
        var result = SiemensSymbolicArrayCodec.Write(native,
            new ArrayWriteRequest(Binding(2), new ArraySlice([0], [2]), value));
        Assert.Equal(0, result.Succeeded);
        Assert.Equal(2, result.Failures.Count);
        Assert.Equal(1, native.Calls);
    }

    [Fact]
    public async Task Default_transport_preserves_256_element_healthy_array_batches()
    {
        var native = new Native();
        await using var transport = new SiemensPlusTransport(native);
        var sample = Assert.Single(await transport.ReadAsync([Binding(512)], default));

        Assert.Equal([256, 256], native.Successes);
        Assert.True(sample.Value.Quality.IsGood);
        Assert.Equal(512, Assert.IsAssignableFrom<ArrayRuntimeValue>(sample.Value.Value).Count);
    }

    [Fact]
    public void Capacity_failure_halves_and_retries_array_chunks()
    {
        var native = new Native { Limit = 8 };
        var window = new AdaptiveRequestWindow(64, 256, () => 0);
        var value = SiemensSymbolicArrayCodec.Read(native, Binding(20), window);

        // 20 → 容量拒绝 → 10 → 容量拒绝 → 5,5,5,5
        Assert.Equal([5, 5, 5, 5], native.Successes);
        Assert.Equal(20, value.Count);
        Assert.True(value.OverallQuality.IsGood);
    }

    [Fact]
    public void Transport_failure_propagates_and_shrinks_window_for_next_session()
    {
        var native = new Native { Failure = SiemensPlusResultCode.TransportFailure };
        var window = new AdaptiveRequestWindow(64, 256, () => 0);
        var error = Assert.Throws<SiemensPlusException>(
            () => SiemensSymbolicArrayCodec.Read(native, Binding(20), window));
        Assert.Equal(SiemensPlusResultCode.TransportFailure, error.Code);
        // 实际请求 20 个元素(min(64,20)),减半 → 10;下个会话首批更温和
        Assert.Equal(10, window.Current);
    }

    [Fact]
    public void Success_grows_window_back_toward_ceiling()
    {
        long clock = 0;
        var native = new Native { Limit = 256 };
        var window = new AdaptiveRequestWindow(64, 256, () => clock);
        SiemensSymbolicArrayCodec.Read(native, Binding(64), window);   // 64 一次读完
        Assert.Equal([64], native.Successes);
        clock = 60_000;   // 越过增长冷却
        for (int i = 0; i < 20; i++)
            SiemensSymbolicArrayCodec.Read(native, Binding(64), window);
        Assert.True(window.Current > 64, $"窗口应增长,实际 {window.Current}");
    }

    private static TagBinding Binding(int elements) => new()
    {
        TagId = "通用.S1500.OP010.EXEC_BITS",
        Def = new TagDef
        {
            Address = "DB160_MainGraph.RT_DATA.EXEC_BITS",
            DataType = IgwDataType.Bool,
            Array = new ArrayDef { ElementType = IgwDataType.Bool, Dimensions = [elements], LowerBounds = [0] },
        },
        Address = new(0, 0, 1),
    };

    private sealed class Native : ISiemensPlusNativeApi
    {
        public List<int> Successes = [];
        public int Limit = 256;
        public SiemensPlusResultCode? Failure;
        public int Calls;
        public int MaxCalls = int.MaxValue;

        private void CountCall()
        {
            if (++Calls > MaxCalls) throw new InvalidOperationException("Repeated an unchanged rejected request");
        }

        public SiemensPlusResultCode Connect(string host, ushort port, uint timeoutMs, bool legacy) => SiemensPlusResultCode.Ok;
        public SiemensPlusResultCode Authenticate(string username, string password) => SiemensPlusResultCode.Ok;
        public SiemensPlusResultCode Probe() => SiemensPlusResultCode.Ok;
        public SiemensPlusNativeBrowseResult Browse() => new(SiemensPlusResultCode.Ok, []);
        public SiemensPlusNativeReadResult Read(IReadOnlyList<string> symbols)
        {
            CountCall();
            if (Failure is { } code) return new(code, []);
            if (symbols.Count > Limit) return new(SiemensPlusResultCode.CapacityFailure, []);
            Successes.Add(symbols.Count);
            return new(SiemensPlusResultCode.Ok, symbols.Select(s => new SiemensPlusNativeValue(SiemensPlusResultCode.Ok, true)).ToArray());
        }
        public SiemensPlusResultCode Write(IReadOnlyList<SiemensPlusNativeWrite> writes)
        {
            CountCall();
            return writes.Count > Limit ? SiemensPlusResultCode.CapacityFailure : SiemensPlusResultCode.Ok;
        }
        public SiemensPlusResultCode Disconnect() => SiemensPlusResultCode.Ok;
        public void Dispose() { }
    }
}
