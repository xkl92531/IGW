using IGW.Abstractions;
using IGW.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

/// <summary>
/// 分层采集的按需读取来自 MES 线程。串行协议(Modbus/MC/FINS…)驱动内部不加锁，
/// 只能由设备循环串行调用；并发协议才允许直接下发。
/// </summary>
public sealed class OnDemandReadSerializationTests
{
    [Fact]
    public async Task Serial_driver_never_sees_an_on_demand_read_overlap_a_scan()
    {
        await using var hub = new DataHub();
        var scanned = Binding("Scanned");
        var payload = Binding("Payload");
        foreach (var binding in new[] { scanned, payload }) hub.Register(binding.TagId, binding.Def);
        var demands = new ScanDemandRegistry(hub);
        using var demand = demands.Acquire("process", [scanned.TagId], 50);
        var driver = new GatedDriver(maxInFlight: 1) { HoldFirstReadOf = scanned.TagId };
        var loop = new DeviceLoop(driver, [scanned, payload], hub, NullLogger.Instance, "Line.Plc", scanDemands: demands);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        loop.Start(stop.Token);
        try
        {
            await driver.HeldReadStarted.Task.WaitAsync(stop.Token);
            Task<OnDemandReadResult> pulled = loop.ReadOnDemandAsync([payload], stop.Token);
            await Task.Delay(150, stop.Token);
            // 周期读取还在途，按需读取必须排队，而不是同时发到同一条连接上。
            Assert.False(pulled.IsCompleted);
            Assert.Equal(0, driver.Overlaps);

            driver.ReleaseHeldRead.TrySetResult();
            OnDemandReadResult result = await pulled.WaitAsync(stop.Token);
            Assert.True(result.Completed, result.Error);
            Assert.Equal(1, result.GoodCount);
            Assert.True(hub.TryGet(payload.TagId, out var value));
            Assert.Equal((short)42, value.Value);
            Assert.Equal(0, driver.Overlaps);
        }
        finally
        {
            driver.ReleaseHeldRead.TrySetResult();
            stop.Cancel();
            await loop.Completion;
        }
    }

    [Fact]
    public async Task Concurrent_driver_serves_an_on_demand_read_while_a_scan_is_in_flight()
    {
        await using var hub = new DataHub();
        var scanned = Binding("Scanned");
        var payload = Binding("Payload");
        foreach (var binding in new[] { scanned, payload }) hub.Register(binding.TagId, binding.Def);
        var demands = new ScanDemandRegistry(hub);
        using var demand = demands.Acquire("process", [scanned.TagId], 50);
        var driver = new GatedDriver(maxInFlight: 2) { HoldFirstReadOf = scanned.TagId };
        var loop = new DeviceLoop(driver, [scanned, payload], hub, NullLogger.Instance, "Line.Plc", scanDemands: demands);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        loop.Start(stop.Token);
        try
        {
            await driver.HeldReadStarted.Task.WaitAsync(stop.Token);
            OnDemandReadResult result = await loop.ReadOnDemandAsync([payload], stop.Token)
                .WaitAsync(TimeSpan.FromSeconds(2), stop.Token);
            Assert.True(result.Completed, result.Error);
            Assert.Equal(1, result.GoodCount);
        }
        finally
        {
            driver.ReleaseHeldRead.TrySetResult();
            stop.Cancel();
            await loop.Completion;
        }
    }

    [Fact]
    public async Task Queued_on_demand_read_completes_with_an_error_when_the_loop_stops()
    {
        await using var hub = new DataHub();
        var scanned = Binding("Scanned");
        var payload = Binding("Payload");
        foreach (var binding in new[] { scanned, payload }) hub.Register(binding.TagId, binding.Def);
        var demands = new ScanDemandRegistry(hub);
        using var demand = demands.Acquire("process", [scanned.TagId], 50);
        var driver = new GatedDriver(maxInFlight: 1) { HoldFirstReadOf = scanned.TagId };
        var loop = new DeviceLoop(driver, [scanned, payload], hub, NullLogger.Instance, "Line.Plc", scanDemands: demands);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        loop.Start(stop.Token);
        await driver.HeldReadStarted.Task.WaitAsync(caller.Token);
        Task<OnDemandReadResult> pulled = loop.ReadOnDemandAsync([payload], caller.Token);

        // 在途读取不放行，直接停机：排队的按需读取永远轮不到执行。
        stop.Cancel();
        await loop.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        // MES 流程不能被一条永远不会被处理的排队请求挂住。
        OnDemandReadResult result = await pulled.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(result.Completed);
        Assert.DoesNotContain(payload.TagId, driver.ReadTagIds);
        driver.ReleaseHeldRead.TrySetResult();
    }

    [Fact]
    public async Task Cancelled_caller_does_not_leave_a_read_for_the_loop_to_execute()
    {
        await using var hub = new DataHub();
        var scanned = Binding("Scanned");
        var payload = Binding("Payload");
        foreach (var binding in new[] { scanned, payload }) hub.Register(binding.TagId, binding.Def);
        var demands = new ScanDemandRegistry(hub);
        using var demand = demands.Acquire("process", [scanned.TagId], 50);
        var driver = new GatedDriver(maxInFlight: 1) { HoldFirstReadOf = scanned.TagId };
        var loop = new DeviceLoop(driver, [scanned, payload], hub, NullLogger.Instance, "Line.Plc", scanDemands: demands);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        loop.Start(stop.Token);
        try
        {
            await driver.HeldReadStarted.Task.WaitAsync(stop.Token);
            using var caller = new CancellationTokenSource();
            Task<OnDemandReadResult> pulled = loop.ReadOnDemandAsync([payload], caller.Token);
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pulled);

            driver.ReleaseHeldRead.TrySetResult();
            await Task.Delay(300, stop.Token);
            Assert.DoesNotContain(payload.TagId, driver.ReadTagIds);
        }
        finally
        {
            driver.ReleaseHeldRead.TrySetResult();
            stop.Cancel();
            await loop.Completion;
        }
    }

    private static TagBinding Binding(string name) => new()
    {
        TagId = "Line.Plc." + name,
        Def = new TagDef { Name = name, Address = "D" + name, DataType = IgwDataType.Int16, ScanRateMs = 50 },
        Address = new(1, 0, 2),
    };

    private sealed class GatedDriver(int maxInFlight) : IDeviceDriver, IDriverCapabilityProvider
    {
        private int _inFlight;
        private int _overlaps;
        private int _heldOnce;
        public string? HoldFirstReadOf { get; init; }
        public int Overlaps => Volatile.Read(ref _overlaps);
        public System.Collections.Concurrent.ConcurrentBag<string> ReadTagIds { get; } = [];
        public TaskCompletionSource HeldReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseHeldRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string DriverId => "Gated";
        public ConnectionState State { get; private set; }
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser { get; } = new Parser();
        public DriverLimits Limits { get; } = new() { MaxTagsPerRead = 100, MaxTagsPerWrite = 100 };
        public DriverCapabilitySnapshot Capabilities => DriverCapabilitySnapshot.FromLimits(Limits) with
        {
            MaxInFlight = maxInFlight,
            SuggestedVerificationDelayMs = 0,
        };

        public Task ConnectAsync(CancellationToken ct)
        {
            State = ConnectionState.Connected;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            State = ConnectionState.Disconnected;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public async Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _inFlight) > 1 && maxInFlight == 1) Interlocked.Increment(ref _overlaps);
            try
            {
                foreach (var tag in tags) ReadTagIds.Add(tag.TagId);
                if (tags.Any(tag => tag.TagId == HoldFirstReadOf) && Interlocked.Exchange(ref _heldOnce, 1) == 0)
                {
                    HeldReadStarted.TrySetResult();
                    await ReleaseHeldRead.Task.WaitAsync(ct);
                }
                else await Task.Yield();
                var now = DateTime.UtcNow;
                return tags.Select(tag => new TagSample(tag.TagId, new DataValue((short)42, StatusCode.Good, now, now))).ToArray();
            }
            finally { Interlocked.Decrement(ref _inFlight); }
        }

        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WriteResult>>(writes.Select(write =>
                new WriteResult { TagId = write.TagId, Status = StatusCode.Good }).ToArray());

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
