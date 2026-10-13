using System.Collections.Concurrent;
using IGW.Abstractions;
using IGW.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class DeviceLoopInvalidNodeTests
{
    [Fact]
    public async Task Mixed_batch_isolates_unknown_nodes_and_probes_recovery_without_republishing_cached_values()
    {
        await using var hub = new DataHub();
        var bindings = Bindings(hub, 59);
        var clock = new AdvanceClock();
        var driver = new Driver(46);
        var demands = new ScanDemandRegistry(hub);
        using var demand = demands.Acquire("test", bindings.Select(b => b.TagId).ToArray(), 20);
        var loop = new DeviceLoop(driver, bindings, hub, NullLogger.Instance, "Line.Plc", scanDemands: demands, clock: clock);
        using var stop = new CancellationTokenSource();
        loop.Start(stop.Token);
        try
        {
            await Until(() => driver.Counts.GetValueOrDefault(bindings[0].TagId) >= 8);
            Assert.All(bindings.Skip(46), tag => Assert.Equal(3, driver.Counts[tag.TagId]));
            Assert.True(hub.TryGet(bindings[46].TagId, out var before));
            Assert.Equal(new StatusCode(0x80340000), before.Quality);
            await Until(() => driver.Counts[bindings[0].TagId] >= 12);
            var actualRequest = Assert.Single(loop.ScanHistory(1, null));
            Assert.Equal(46, actualRequest.TagCount);
            Assert.Equal(0, actualRequest.BadCount);
            Assert.Equal("good", actualRequest.Status);
            Assert.DoesNotContain(bindings[46].TagId, actualRequest.TagIds);
            Assert.True(hub.TryGet(bindings[46].TagId, out var after));
            Assert.Equal(before.SourceTimestamp, after.SourceTimestamp);
            Assert.Equal(before.ServerTimestamp, after.ServerTimestamp);
            driver.Recovered = true;
            clock.Advance(30_001);
            await Until(() => driver.Counts[bindings[46].TagId] >= 6);
            Assert.True(hub.TryGet(bindings[46].TagId, out var recovered) && recovered.IsGood);
        }
        finally { stop.Cancel(); await loop.Completion.WaitAsync(TimeSpan.FromSeconds(2)); }
    }

    [Fact]
    public async Task All_unknown_nodes_keep_original_quality_and_stop_high_frequency_requests()
    {
        await using var hub = new DataHub();
        var bindings = Bindings(hub, 1);
        var driver = new Driver(0);
        var demands = new ScanDemandRegistry(hub);
        using var demand = demands.Acquire("test", bindings.Select(b => b.TagId).ToArray(), 20);
        var loop = new DeviceLoop(driver, bindings, hub, NullLogger.Instance, "Line.Plc", scanDemands: demands);
        using var stop = new CancellationTokenSource();
        loop.Start(stop.Token);
        try
        {
            await Until(() => driver.Counts.GetValueOrDefault(bindings[0].TagId) >= 3);
            await Task.Delay(600);
            Assert.Equal(3, driver.Counts[bindings[0].TagId]);
            Assert.True(hub.TryGet(bindings[0].TagId, out var value));
            Assert.Equal(new StatusCode(0x80340000), value.Quality);
        }
        finally { stop.Cancel(); await loop.Completion.WaitAsync(TimeSpan.FromSeconds(2)); }
    }

    [Theory]
    [InlineData(0x80000000u)]
    [InlineData(0x80050000u)]
    public async Task Ordinary_bad_and_communication_quality_do_not_isolate(uint code)
    {
        await using var hub = new DataHub();
        var bindings = Bindings(hub, 2);
        var driver = new Driver(1) { Failure = new StatusCode(code) };
        var demands = new ScanDemandRegistry(hub);
        using var demand = demands.Acquire("test", bindings.Select(b => b.TagId).ToArray(), 20);
        var loop = new DeviceLoop(driver, bindings, hub, NullLogger.Instance, "Line.Plc", scanDemands: demands);
        using var stop = new CancellationTokenSource();
        loop.Start(stop.Token);
        try
        {
            await Until(() => driver.Counts.GetValueOrDefault(bindings[0].TagId) >= 8);
            Assert.True(driver.Counts[bindings[1].TagId] >= 8);
        }
        finally { stop.Cancel(); await loop.Completion; }
    }

    /// <summary>
    /// 类型配错的点位要停读，和「符号找不到」一样。
    /// 这是 Ordinary_bad_and_communication_quality_do_not_isolate 的对照组：同样是坏值，
    /// 配置错误要收敛到隔离，链路故障要一直重试。
    /// </summary>
    [Theory]
    [InlineData(0x80070000u)]   // BadDecodeError：返回的字节按配置的类型解不开
    [InlineData(0x80740000u)]   // BadTypeMismatch：PLC 侧实际类型与点位声明不一致
    public async Task Tags_that_cannot_be_decoded_stop_being_requested(uint code)
    {
        await using var hub = new DataHub();
        var bindings = Bindings(hub, 2);
        var driver = new Driver(1) { Failure = new StatusCode(code) };
        var demands = new ScanDemandRegistry(hub);
        using var demand = demands.Acquire("test", bindings.Select(b => b.TagId).ToArray(), 20);
        var loop = new DeviceLoop(driver, bindings, hub, NullLogger.Instance, "Line.Plc", scanDemands: demands);
        using var stop = new CancellationTokenSource();
        loop.Start(stop.Token);
        try
        {
            await Until(() => driver.Counts.GetValueOrDefault(bindings[0].TagId) >= 8);
            // 好点照常轮询，坏点连续失败 3 次后停在那里等复探
            Assert.Equal(3, driver.Counts[bindings[1].TagId]);
            Assert.True(hub.TryGet(bindings[1].TagId, out var value));
            Assert.Equal(new StatusCode(code), value.Quality);
        }
        finally { stop.Cancel(); await loop.Completion.WaitAsync(TimeSpan.FromSeconds(2)); }
    }

    [Fact]
    public async Task Interrupted_unknown_node_failures_must_be_consecutive()
    {
        await using var hub = new DataHub();
        var bindings = Bindings(hub, 2);
        var driver = new Driver(1) { InterruptFailures = true };
        var demands = new ScanDemandRegistry(hub);
        using var demand = demands.Acquire("test", bindings.Select(b => b.TagId).ToArray(), 20);
        var loop = new DeviceLoop(driver, bindings, hub, NullLogger.Instance, "Line.Plc", scanDemands: demands);
        using var stop = new CancellationTokenSource();
        loop.Start(stop.Token);
        try
        {
            await Until(() => driver.Counts.GetValueOrDefault(bindings[0].TagId) >= 12);
            Assert.True(driver.Counts[bindings[1].TagId] >= 12);
        }
        finally { stop.Cancel(); await loop.Completion; }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Verified_write_restores_polling_even_when_an_old_unknown_response_arrives_later(bool blockThird)
    {
        await using var hub = new DataHub();
        var bindings = Bindings(hub, 1);
        bindings[0].Def.Access = TagAccess.ReadWrite;
        var driver = new RecoveringWriteDriver(blockThird);
        var demands = new ScanDemandRegistry(hub);
        using var demand = demands.Acquire("test", [bindings[0].TagId], 20);
        var loop = new DeviceLoop(driver, bindings, hub, NullLogger.Instance, "Line.Plc", scanDemands: demands);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        loop.Start(stop.Token);
        try
        {
            await driver.ThirdRead.Task.WaitAsync(stop.Token);
            if (!blockThird) await Until(() => loop.ScanHistory(1, null).FirstOrDefault()?.Sequence >= 3);
            Assert.Equal(StatusCode.Good, await loop.EnqueueWriteAsync(new WriteRequest
            {
                TagId = bindings[0].TagId, Def = bindings[0].Def, Address = bindings[0].Address, Value = (short)42,
            }).WaitAsync(stop.Token));
            driver.ReleaseThird.TrySetResult();
            using var resumed = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (driver.Reads < 6) await Task.Delay(10, resumed.Token);
            Assert.True(hub.TryGet(bindings[0].TagId, out var value) && value.IsGood);
            Assert.Equal((short)42, value.Value);
        }
        finally { driver.ReleaseThird.TrySetResult(); stop.Cancel(); await loop.Completion.WaitAsync(TimeSpan.FromSeconds(2)); }
    }

    private sealed class RecoveringWriteDriver(bool blockThird) : IDeviceDriver, IDriverCapabilityProvider
    {
        private int _reads;
        private volatile bool _recovered;
        public int Reads => Volatile.Read(ref _reads);
        public TaskCompletionSource ThirdRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseThird { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string DriverId => "OpcUa.Client";
        public ConnectionState State { get; private set; }
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser => throw new NotSupportedException();
        public DriverLimits Limits { get; } = new() { MaxTagsPerRead = 100, MaxTagsPerWrite = 100 };
        public DriverCapabilitySnapshot Capabilities => DriverCapabilitySnapshot.FromLimits(Limits) with
            { MaxInFlight = 2, SuggestedVerificationDelayMs = 0 };
        public Task ConnectAsync(CancellationToken ct) { State = ConnectionState.Connected; StateChanged?.Invoke(State); return Task.CompletedTask; }
        public Task DisconnectAsync() { State = ConnectionState.Disconnected; StateChanged?.Invoke(State); return Task.CompletedTask; }
        public async Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
        {
            bool recovered = _recovered;
            if (Interlocked.Increment(ref _reads) == 3)
            {
                ThirdRead.TrySetResult();
                if (blockThird) await ReleaseThird.Task.WaitAsync(ct);
            }
            return tags.Select(tag => new TagSample(tag.TagId, recovered
                ? DataValue.GoodAt((short)42, DateTime.UtcNow)
                : DataValue.BadAt(new StatusCode(0x80340000), DateTime.UtcNow))).ToArray();
        }
        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct)
        {
            _recovered = true;
            return Task.FromResult<IReadOnlyList<WriteResult>>(writes.Select(write =>
                new WriteResult { TagId = write.TagId, Status = StatusCode.Good }).ToArray());
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static TagBinding[] Bindings(DataHub hub, int count) => Enumerable.Range(0, count).Select(i =>
    {
        var def = new TagDef { Name = "N" + i, Address = "ns=2;s=N" + i, DataType = IgwDataType.Int16, ScanRateMs = 20 };
        var id = "Line.Plc.N" + i;
        hub.Register(id, def);
        return new TagBinding { TagId = id, Def = def, Address = new TagAddress(1, i * 2, 2) };
    }).ToArray();

    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(5, timeout.Token);
    }

    private sealed class AdvanceClock : IMonotonicClock
    {
        private long _offset;
        public long NowMilliseconds => Environment.TickCount64 + Interlocked.Read(ref _offset);
        public void Advance(long ms) => Interlocked.Add(ref _offset, ms);
    }

    private sealed class Driver(int goodCount) : IDeviceDriver
    {
        public ConcurrentDictionary<string, int> Counts { get; } = new();
        public volatile bool Recovered;
        public StatusCode Failure { get; init; } = new(0x80340000);
        public bool InterruptFailures { get; init; }
        public string DriverId => "OpcUaClient";
        public ConnectionState State { get; private set; }
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser => throw new NotSupportedException();
        public DriverLimits Limits { get; } = new() { MaxTagsPerRead = 100, MaxReadUnits = 1000, MaxGap = 1000 };
        public Task ConnectAsync(CancellationToken ct) { State = ConnectionState.Connected; StateChanged?.Invoke(State); return Task.CompletedTask; }
        public Task DisconnectAsync() { State = ConnectionState.Disconnected; StateChanged?.Invoke(State); return Task.CompletedTask; }
        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
        {
            var samples = tags.Select(tag => new TagSample(tag.TagId, Recovered || tag.Address.Offset / 2 < goodCount
                ? DataValue.GoodAt((short)42, DateTime.UtcNow) : DataValue.BadAt(
                    InterruptFailures && Counts.GetValueOrDefault(tag.TagId) % 3 == 2 ? StatusCode.BadCommFailure : Failure,
                    DateTime.UtcNow))).ToArray();
            foreach (var tag in tags) Counts.AddOrUpdate(tag.TagId, 1, (_, n) => n + 1);
            return Task.FromResult<IReadOnlyList<TagSample>>(samples);
        }
        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
