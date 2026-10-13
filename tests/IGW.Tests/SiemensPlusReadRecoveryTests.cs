using IGW.Abstractions;
using IGW.Core;
using IGW.Drivers.IoTClient;
using Xunit;

namespace IGW.Tests;

public sealed class SiemensPlusReadRecoveryTests
{
    [Theory]
    [InlineData(SiemensPlusResultCode.ProtocolFailure)]
    [InlineData(SiemensPlusResultCode.BufferTooSmall)]
    public async Task Noncapacity_top_level_errors_never_train_or_split(SiemensPlusResultCode code)
    {
        var window = new AdaptiveRequestWindow(4, 8, () => 0);
        var native = new Fake { ReadResult = _ => new(code, []) };
        await using var transport = new SiemensPlusTransport(native, window);
        await transport.ConnectAsync("plc", 102, TimeSpan.FromSeconds(1), false, default);
        // 会话仍可用：只把这一片标 Bad(不再抛出让驱动把全部点标成设备故障)
        var values = await transport.ReadAsync(Tags(4), default);
        Assert.All(values, value => Assert.False(value.Value.IsGood));
        Assert.Contains(code.ToString(), transport.LastReadError);
        Assert.Equal(4, window.Current); Assert.Equal(0, window.Version);
        Assert.Single(native.Calls); Assert.Equal(0, native.Disconnects); Assert.True(transport.IsConnected);
    }

    [Theory]
    [InlineData(SiemensPlusResultCode.TransportFailure)]
    [InlineData(SiemensPlusResultCode.Timeout)]
    [InlineData(SiemensPlusResultCode.AuthenticationRequired)]
    public async Task Session_failures_still_fail_the_whole_read(SiemensPlusResultCode code)
    {
        var native = new Fake { ReadResult = _ => new(code, []) };
        await using var transport = new SiemensPlusTransport(native, new AdaptiveRequestWindow(4, 8, () => 0));
        await transport.ConnectAsync("plc", 102, TimeSpan.FromSeconds(1), false, default);
        Assert.Equal(code, (await Assert.ThrowsAsync<SiemensPlusException>(() => transport.ReadAsync(Tags(4), default))).Code);
    }

    [Fact]
    public async Task One_missing_symbol_is_bad_node_id_unknown_and_gets_quarantined_while_the_rest_stay_good()
    {
        var native = new Fake { ReadResult = symbols => new(SiemensPlusResultCode.Ok, symbols.Select(symbol => symbol == "PLC.T7"
            ? new SiemensPlusNativeValue(SiemensPlusResultCode.SymbolNotFound, null)
            : new SiemensPlusNativeValue(SiemensPlusResultCode.Ok, (short)42)).ToArray()) };
        await using var transport = new SiemensPlusTransport(native, new AdaptiveRequestWindow(1000, 1000, () => 0));
        await transport.ConnectAsync("plc", 102, TimeSpan.FromSeconds(1), false, default);
        var tags = Tags(1000);
        var values = await transport.ReadAsync(tags, default);
        Assert.Equal(999, values.Count(value => value.Value.IsGood));
        Assert.Equal(StatusCode.BadNodeIdUnknown, values[7].Value.Quality);
        // 单个符号找不到是点位级问题：不写设备级的 LastReadError。
        // 写了会让驱动把整台设备标成「部分在线」，而这个点每 30 秒复探一次，
        // 设备状态就跟着复探周期在在线/异常之间反复跳。原因和处置记在监控中心的「点位异常」里。
        Assert.Equal("", transport.LastReadError);

        // 接入现有坏点隔离：连续 3 次后不再读取，30 秒后复探
        var policy = new InvalidNodeReadPolicy(tags.ToDictionary(tag => tag.TagId));
        for (int cycle = 0; cycle < 3; cycle++)
            policy.Record(tags, await transport.ReadAsync(tags, default), completedMs: cycle);
        var due = policy.SelectDue(tags, nowMs: 3, out _);
        Assert.Equal(999, due.Count);
        Assert.DoesNotContain(due, tag => tag.TagId == "Line.Plc.T7");
        Assert.Contains(policy.SelectDue(tags, nowMs: 31_000, out _), tag => tag.TagId == "Line.Plc.T7");
    }

    [Fact]
    public async Task A_failed_later_slice_keeps_the_samples_already_read()
    {
        var native = new Fake { ReadResult = symbols => symbols.Contains("PLC.T5")
            ? new(SiemensPlusResultCode.ProtocolFailure, []) : Good(symbols) };
        await using var transport = new SiemensPlusTransport(native, new AdaptiveRequestWindow(4, 4, () => 0));
        await transport.ConnectAsync("plc", 102, TimeSpan.FromSeconds(1), false, default);
        var values = await transport.ReadAsync(Tags(12), default);
        Assert.All(values.Take(4), value => Assert.True(value.Value.IsGood));     // 第 1 片
        Assert.All(values.Skip(4).Take(4), value => Assert.Equal(StatusCode.BadDecodeError, value.Value.Quality));
        Assert.All(values.Skip(8), value => Assert.True(value.Value.IsGood));     // 第 3 片照常读取
        Assert.Equal(3, native.Calls.Count);
    }

    [Fact]
    public async Task Item_writes_fail_only_the_missing_or_rejected_points()
    {
        var native = new ItemWriteFake { ItemResult = symbol => symbol switch
        {
            "PLC.T1" => SiemensPlusResultCode.SymbolNotFound,
            "PLC.T2" => SiemensPlusResultCode.ItemRejected,
            _ => SiemensPlusResultCode.Ok,
        } };
        await using var transport = new SiemensPlusTransport(native);
        await transport.ConnectAsync("plc", 102, TimeSpan.FromSeconds(1), false, default);
        var results = await transport.WriteAsync(Writes(4), default);
        Assert.Equal([StatusCode.Good, StatusCode.BadNodeIdUnknown, StatusCode.BadDeviceFailure, StatusCode.Good],
            results.Select(result => result.Status));
        Assert.Single(native.WriteCalls);
    }

    [Fact]
    public async Task A_whole_rejected_write_request_is_split_until_the_rejected_point_is_isolated()
    {
        var native = new ItemWriteFake { RejectWhole = symbols => symbols.Contains("PLC.T5") };
        await using var transport = new SiemensPlusTransport(native);
        await transport.ConnectAsync("plc", 102, TimeSpan.FromSeconds(1), false, default);
        var results = await transport.WriteAsync(Writes(8), default);
        Assert.Equal(7, results.Count(result => result.Status.IsGood));
        Assert.False(results[5].Status.IsGood);
    }

    [Fact]
    public async Task Old_native_libraries_without_item_writes_keep_whole_batch_semantics()
    {
        var native = new Fake();   // 只实现 Write(整批)，走接口默认的 WriteItems
        await using var transport = new SiemensPlusTransport(native);
        await transport.ConnectAsync("plc", 102, TimeSpan.FromSeconds(1), false, default);
        Assert.All(await transport.WriteAsync(Writes(3), default), result => Assert.Equal(StatusCode.Good, result.Status));
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    public void Native_libraries_without_write_items_fall_back_to_one_whole_batch_write(uint abi)
    {
        int batches = 0;
        var writes = new[] { new SiemensPlusNativeWrite("PLC.A", (short)1), new SiemensPlusNativeWrite("PLC.B", (short)2) };
        var ok = SiemensPlusNative.WriteItems(abi, writes, _ => { batches++; return SiemensPlusResultCode.Ok; },
            _ => throw new InvalidOperationException("ABI < 3 has no igw_s7plus_write_items export"));
        Assert.Equal(1, batches);
        Assert.Equal([SiemensPlusResultCode.Ok, SiemensPlusResultCode.Ok], ok.Items);

        var failed = SiemensPlusNative.WriteItems(abi, writes, _ => SiemensPlusResultCode.TransportFailure,
            _ => throw new InvalidOperationException("ABI < 3 has no igw_s7plus_write_items export"));
        Assert.Equal(SiemensPlusResultCode.TransportFailure, failed.Code);
        Assert.Empty(failed.Items);
    }

    [Fact]
    public void Native_libraries_with_write_items_use_per_item_results()
    {
        var writes = new[] { new SiemensPlusNativeWrite("PLC.A", (short)1) };
        var result = SiemensPlusNative.WriteItems(3, writes, _ => throw new InvalidOperationException("whole batch not expected"),
            _ => new(SiemensPlusResultCode.Ok, [SiemensPlusResultCode.ItemRejected]));
        Assert.Equal([SiemensPlusResultCode.ItemRejected], result.Items);
    }

    private static WriteRequest[] Writes(int count) => Tags(count)
        .Select(tag => new WriteRequest { TagId = tag.TagId, Def = tag.Def, Address = tag.Address, Value = (short)1 }).ToArray();

    private sealed class ItemWriteFake : Fake, ISiemensPlusNativeApi
    {
        public Func<string, SiemensPlusResultCode> ItemResult = static _ => SiemensPlusResultCode.Ok;
        public Func<IReadOnlyList<string>, bool> RejectWhole = static _ => false;
        public List<string[]> WriteCalls = [];
        public SiemensPlusNativeWriteResult WriteItems(IReadOnlyList<SiemensPlusNativeWrite> writes)
        {
            var symbols = writes.Select(write => write.Symbol).ToArray();
            WriteCalls.Add(symbols);
            if (RejectWhole(symbols)) return new(SiemensPlusResultCode.CapacityFailure, []);
            return new(SiemensPlusResultCode.Ok, symbols.Select(ItemResult).ToArray());
        }
    }

    [Theory]
    [InlineData(0u, false)]
    [InlineData(1u, true)]    // Linux .so 暂为 v1：照常加载，只是没有单项 SymbolNotFound
    [InlineData(2u, true)]
    [InlineData(3u, true)]     // v3：逐项写入
    [InlineData(4u, true)]     // v4：变化订阅
    [InlineData(5u, true)]     // v5：在线列出数据块(按块名查 DB 号)
    [InlineData(6u, false)]    // 比随附版本新的原生库不加载：结构体布局可能已变
    public void Native_abi_versions_up_to_the_shipped_one_are_accepted(uint version, bool supported) =>
        Assert.Equal(supported, SiemensPlusNative.IsSupportedAbi(version));

    [Fact]
    public void Buffer_size_learned_from_a_short_buffer_avoids_reading_the_plc_twice_next_time()
    {
        int attempts = 0, hint = 0;
        SiemensPlusNative.NativeResult Read(SiemensPlusNative.NativeReadItem[] _, SiemensPlusNative.NativeReadResult[] results, byte[] payload)
        {
            attempts++;
            if (payload.Length < 5000) return new(SiemensPlusResultCode.BufferTooSmall, 5000);
            results[0] = new(SiemensPlusResultCode.Ok, SiemensPlusNative.NativeValueType.Text, 0, 0, 0, 0, 5000);
            return new(SiemensPlusResultCode.Ok, 5000);
        }
        for (int i = 0; i < 5; i++)
            Assert.Equal(SiemensPlusResultCode.Ok, SiemensPlusNative.ReadBuffered(["PLC.S"], Read, static () => { },
                new("Read", "S7 Plus symbols", 1, -1, 0), ref hint).Code);
        Assert.Equal(6, attempts);   // 只有第一次需要扩容重读
        Assert.Equal(5000, hint);
    }

    [Fact]
    public async Task Singleton_capacity_rejection_preserves_earlier_and_later_points_and_reports_exact_path()
    {
        var native = new Fake { ReadResult = symbols => symbols.Contains("PLC.T5")
            ? new(SiemensPlusResultCode.CapacityFailure, []) : Good(symbols) };
        var window = new AdaptiveRequestWindow(4, 8, () => 0);
        await using var transport = new SiemensPlusTransport(native, window);
        await transport.ConnectAsync("plc", 102, TimeSpan.FromSeconds(1), false, default);
        var tags = Tags(10);
        var values = await transport.ReadAsync(tags, default);
        Assert.Equal(tags.Select(t => t.TagId), values.Select(v => v.TagId));
        Assert.Equal(StatusCode.BadDeviceFailure, values[5].Value.Quality);
        Assert.All(values.Where((_, i) => i != 5), value => Assert.True(value.Value.IsGood));
        Assert.DoesNotContain(native.Calls.Skip(1), call => call.Contains("PLC.T0"));
        Assert.Contains("Line.Plc.T5", transport.LastReadError);
        Assert.Contains("PLC.T5", transport.LastReadError);
        Assert.Contains("CapacityFailure", transport.LastReadError);
        Assert.Contains("quality=", transport.LastReadError);
        Assert.Equal(0, native.Disconnects); Assert.True(transport.IsConnected);
    }

    [Fact]
    public async Task Real_buffer_loop_counts_both_attempts_and_excludes_local_retry_from_learning()
    {
        var window = new AdaptiveRequestWindow(1, 2, () => 0);
        var native = new BufferFake();
        await using var transport = new SiemensPlusTransport(native, window);
        await transport.ConnectAsync("plc", 102, TimeSpan.FromSeconds(1), false, default);
        using var timing = ProtocolOperationTiming.Begin();
        for (int i = 0; i < 20; i++)
        {
            var value = Assert.Single(await transport.ReadAsync(Tags(1), default));
            Assert.Equal(new string('X', 5000), value.Value.Value);
        }
        Assert.Equal(40, native.Attempts);
        Assert.Equal(40, timing.Snapshot().PhysicalRequests);
        Assert.Equal(40, timing.Snapshot().Samples!.Count);
        Assert.Equal(0, timing.Snapshot().FailedRequests); // PLC reads succeeded; only local output storage grew.
        Assert.Equal(1, window.Current); Assert.Equal(0, window.Version);
        Assert.Equal(20, native.BufferSizes.Count(size => size == 4096));
        Assert.Equal(20, native.BufferSizes.Count(size => size == 8192));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Buffer_retry_observes_priority_or_cancellation_before_second_native_read(bool cancel)
    {
        using var stop = new CancellationTokenSource();
        bool priority = false;
        var native = new BufferFake { AfterAttempt = () => { if (cancel) stop.Cancel(); else priority = true; } };
        await using var transport = new SiemensPlusTransport(native);
        await transport.ConnectAsync("plc", 102, TimeSpan.FromSeconds(1), false, default);
        transport.SetPriorityWorkProbe(() => priority);
        using var timing = ProtocolOperationTiming.Begin();
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.ReadAsync(Tags(1), stop.Token));
        else await Assert.ThrowsAsync<DriverReadYieldException>(() => transport.ReadAsync(Tags(1), stop.Token));
        Assert.Equal(1, native.Attempts); Assert.Equal(1, timing.Snapshot().PhysicalRequests);
        Assert.Equal(0, native.Disconnects); Assert.True(transport.IsConnected);
    }

    private static TagBinding[] Tags(int count) => Enumerable.Range(0, count).Select(i => new TagBinding
    {
        TagId = $"Line.Plc.T{i}", Def = new TagDef { Address = $"PLC.T{i}", DataType = IgwDataType.Int16 }, Address = new(0, 0, 2)
    }).ToArray();
    private static SiemensPlusNativeReadResult Good(IReadOnlyList<string> symbols) => new(SiemensPlusResultCode.Ok,
        symbols.Select(_ => new SiemensPlusNativeValue(SiemensPlusResultCode.Ok, (short)42)).ToArray());

    private class Fake : ISiemensPlusNativeApi
    {
        public Func<IReadOnlyList<string>, SiemensPlusNativeReadResult> ReadResult = Good;
        public List<string[]> Calls = [];
        public int Disconnects;
        public SiemensPlusNativeReadResult Read(IReadOnlyList<string> symbols) { Calls.Add(symbols.ToArray()); return ReadResult(symbols); }
        public SiemensPlusResultCode Connect(string host, ushort port, uint timeoutMs, bool legacy) => SiemensPlusResultCode.Ok;
        public SiemensPlusResultCode Authenticate(string username, string password) => SiemensPlusResultCode.Ok;
        public SiemensPlusResultCode Probe() => SiemensPlusResultCode.Ok;
        public SiemensPlusNativeBrowseResult Browse() => new(SiemensPlusResultCode.Ok, []);
        public SiemensPlusResultCode Write(IReadOnlyList<SiemensPlusNativeWrite> writes) => SiemensPlusResultCode.Ok;
        public SiemensPlusResultCode Disconnect() { Disconnects++; return SiemensPlusResultCode.Ok; }
        public void Dispose() { }
    }
    private sealed class BufferFake : Fake, ISiemensPlusNativeApi
    {
        public int Attempts;
        public List<int> BufferSizes = [];
        public Action? AfterAttempt;
        public SiemensPlusNativeReadResult ReadMeasured(IReadOnlyList<string> symbols, Action beforeRequest, ProtocolRequestDescriptor descriptor)
            => SiemensPlusNative.ReadBuffered(symbols, (_, results, payload) =>
            {
                Attempts++; BufferSizes.Add(payload.Length); AfterAttempt?.Invoke();
                if (payload.Length < 5000) return new(SiemensPlusResultCode.BufferTooSmall, 5000);
                Array.Fill(payload, (byte)'X', 0, 5000);
                results[0] = new(SiemensPlusResultCode.Ok, SiemensPlusNative.NativeValueType.Text, 0, 0, 0, 0, 5000);
                return new(SiemensPlusResultCode.Ok, 5000);
            }, beforeRequest, descriptor);
    }
}
