using System.Collections.Concurrent;
using IGW.Abstractions;
using IGW.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class ConcurrentReadIsolationTests
{
    [Fact]
    public async Task Scan_started_during_a_write_cannot_restore_an_old_value_after_verification()
    {
        await using var hub = new DataHub();
        var binding = Binding("Observed");
        binding.Def.ScanRateMs = 30_000;
        hub.Register(binding.TagId, binding.Def);
        var demands = new ScanDemandRegistry(hub);
        using var demand = demands.Acquire("qc", [binding.TagId], 30_000);
        var driver = new ConcurrentReadDriver(binding.TagId, 2) { HoldFirstObservedRead = true };
        var loop = new DeviceLoop(driver, [binding], hub, NullLogger.Instance, "Line.Plc", scanDemands: demands);
        Task<StatusCode> written = loop.EnqueueWriteAsync(Request(binding, 7));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        loop.Start(stop.Token);
        try
        {
            await Task.WhenAll(driver.FirstWriteStarted.Task, driver.ObservedReadCaptured.Task).WaitAsync(stop.Token);
            driver.ReleaseFirstWrite.TrySetResult();
            Assert.Equal(StatusCode.Good, await written.WaitAsync(stop.Token));
            Assert.True(hub.TryGet(binding.TagId, out var verified));
            Assert.Equal((short)7, verified.Value);
            driver.ReleaseObservedRead.TrySetResult();
            while (loop.Stats.ReadsOk == 0) await Task.Delay(10, stop.Token);
            Assert.True(hub.TryGet(binding.TagId, out var afterScan));
            Assert.Equal((short)7, afterScan.Value);
        }
        finally
        {
            driver.ReleaseFirstWrite.TrySetResult();
            driver.ReleaseObservedRead.TrySetResult();
            stop.Cancel();
            await loop.Completion;
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(1)]
    public async Task Polling_and_live_updates_follow_driver_concurrency_while_a_write_is_in_flight_and_another_is_queued(int maxInFlight)
    {
        await using var hub = new DataHub();
        var firstBinding = Binding("FirstWrite");
        var secondBinding = Binding("QueuedWrite");
        var observedBinding = Binding("Observed");
        TagBinding[] bindings = [firstBinding, secondBinding, observedBinding];
        foreach (var binding in bindings) hub.Register(binding.TagId, binding.Def);
        var demands = new ScanDemandRegistry(hub);
        using var demand = demands.Acquire("qc-observed", [observedBinding.TagId], 1);
        var published = new TaskCompletionSource<DataValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = hub.SubscribePoints(change =>
        {
            if (change.Value.IsGood && Equals(change.Value.Value, (short)42))
                published.TrySetResult(change.Value);
            return ValueTask.CompletedTask;
        }, "qc-live-test");
        subscription.Add(observedBinding.TagId);
        var driver = new ConcurrentReadDriver(observedBinding.TagId, maxInFlight);
        var loop = new DeviceLoop(driver, bindings, hub, NullLogger.Instance, "Line.Plc",
            idlePollMs: 1, scanDemands: demands);
        Task<StatusCode> first = loop.EnqueueWriteAsync(Request(firstBinding, 7));
        Task<StatusCode> second = loop.EnqueueWriteAsync(Request(secondBinding, 8));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        loop.Start(stop.Token);
        try
        {
            await driver.FirstWriteStarted.Task.WaitAsync(stop.Token);
            driver.SetObservedValue(42);

            if (maxInFlight > 1)
            {
                // The writer gate remains closed throughout: this proves independent
                // scan progress and delivery to a QC-style point subscription, not merely
                // that reads eventually run after the write queue drains.
                DataValue observed = await published.Task.WaitAsync(TimeSpan.FromSeconds(2), stop.Token);
                Assert.Equal((short)42, observed.Value);
                Assert.False(first.IsCompleted);
                Assert.False(second.IsCompleted);
                Assert.True(driver.ReadsDuringWrite > 0);
                Assert.True(hub.TryGet(observedBinding.TagId, out var cached));
                Assert.Equal((short)42, cached.Value);
            }
            else
            {
                await Task.Delay(100, stop.Token);
                Assert.False(published.Task.IsCompleted);
                Assert.Equal(0, driver.ReadsDuringWrite);
                Assert.False(first.IsCompleted);
                Assert.False(second.IsCompleted);
            }

            driver.ReleaseFirstWrite.TrySetResult();
            Assert.Equal(StatusCode.Good, await first.WaitAsync(stop.Token));
            Assert.Equal(StatusCode.Good, await second.WaitAsync(stop.Token));
            Assert.Equal((short)42, (await published.Task.WaitAsync(stop.Token)).Value);
            if (maxInFlight == 1) Assert.Equal(0, driver.ReadsDuringWrite);
        }
        finally
        {
            driver.ReleaseFirstWrite.TrySetResult();
            stop.Cancel();
            await loop.Completion;
        }
    }

    private static TagBinding Binding(string name) => new()
    {
        TagId = "Line.Plc.DB." + name,
        Def = new TagDef { Name = name, Address = name, DataType = IgwDataType.Int16,
            Access = TagAccess.ReadWrite, ScanRateMs = 1 },
        Address = new TagAddress(0, 0, 2),
    };

    private static WriteRequest Request(TagBinding binding, short value) => new()
    {
        TagId = binding.TagId, Def = binding.Def, Address = binding.Address, Value = value,
    };

    private sealed class ConcurrentReadDriver(string observedTag, int maxInFlight) : IDeviceDriver, IDriverCapabilityProvider
    {
        private readonly ConcurrentDictionary<string, short> _values = new();
        private int _writeCalls;
        private int _activeWrites;
        private int _readsDuringWrite;
        private int _observedValue;
        private int _observedReads;
        public bool HoldFirstObservedRead { get; init; }
        public TaskCompletionSource ObservedReadCaptured { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseObservedRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstWriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ReadsDuringWrite => Volatile.Read(ref _readsDuringWrite);
        public void SetObservedValue(short value) => Volatile.Write(ref _observedValue, value);
        public string DriverId => "ConcurrentReadIsolation";
        public ConnectionState State { get; private set; }
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser { get; } = new Parser();
        public DriverLimits Limits { get; } = new() { MaxTagsPerRead = 1, MaxTagsPerWrite = 1 };
        public DriverCapabilitySnapshot Capabilities => DriverCapabilitySnapshot.FromLimits(Limits) with
        { MaxInFlight = maxInFlight, SuggestedVerificationDelayMs = 0 };

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
            if (Volatile.Read(ref _activeWrites) > 0) Interlocked.Increment(ref _readsDuringWrite);
            var now = DateTime.UtcNow;
            var samples = tags.Select(tag => new TagSample(tag.TagId,
                new DataValue(tag.TagId == observedTag ? (short)Volatile.Read(ref _observedValue) :
                    _values.GetValueOrDefault(tag.TagId), StatusCode.Good, now, now))).ToArray();
            if (HoldFirstObservedRead && tags.Any(tag => tag.TagId == observedTag) &&
                Interlocked.Increment(ref _observedReads) == 1)
            {
                ObservedReadCaptured.TrySetResult();
                await ReleaseObservedRead.Task.WaitAsync(ct);
            }
            return samples;
        }
        public async Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct)
        {
            Interlocked.Increment(ref _activeWrites);
            try
            {
                if (Interlocked.Increment(ref _writeCalls) == 1)
                {
                    FirstWriteStarted.TrySetResult();
                    await ReleaseFirstWrite.Task.WaitAsync(ct);
                }
                foreach (var write in writes)
                {
                    _values[write.TagId] = Convert.ToInt16(write.Value);
                    if (write.TagId == observedTag) SetObservedValue(Convert.ToInt16(write.Value));
                }
                return writes.Select(write => new WriteResult { TagId = write.TagId, Status = StatusCode.Good }).ToArray();
            }
            finally { Interlocked.Decrement(ref _activeWrites); }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Parser : IAddressParser
    {
        public bool TryParse(string address, IgwDataType type, out TagAddress result, out string? error)
        {
            result = new TagAddress(0, 0, 2);
            error = null;
            return true;
        }
    }
}
