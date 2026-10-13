using IGW.Abstractions;
using IGW.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class DeviceLoopWriteQueueSafetyTests
{
    [Fact]
    public async Task Caller_cancellation_before_dispatch_never_reaches_the_PLC()
    {
        await using var hub = new DataHub();
        var binding = Binding(IgwDataType.Int16);
        hub.Register(binding.TagId, binding.Def);
        var driver = new RecordingDriver();
        var loop = new DeviceLoop(driver, [binding], hub, NullLogger.Instance, "Line.Plc", idlePollMs: 1);
        using var caller = new CancellationTokenSource();

        Task<StatusCode> pending = loop.EnqueueWriteAsync(Request(binding, (short)7), caller.Token);
        caller.Cancel();

        Assert.Equal(StatusCode.BadRequestCancelled, await pending.WaitAsync(TimeSpan.FromSeconds(1)));

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        loop.Start(stop.Token);
        await Task.Delay(100, stop.Token);

        Assert.Empty(driver.WrittenValues);
        stop.Cancel();
        await loop.Completion;
    }

    [Fact]
    public async Task All_values_mode_preserves_same_tag_order_without_duplicate_batch_keys()
    {
        await using var hub = new DataHub();
        var binding = Binding(IgwDataType.Int16);
        hub.Register(binding.TagId, binding.Def);
        var driver = new RecordingDriver();
        var loop = new DeviceLoop(driver, [binding], hub, NullLogger.Instance, "Line.Plc",
            writeOpt: "all", idlePollMs: 1);

        Task<StatusCode> first = loop.EnqueueWriteAsync(Request(binding, (short)1));
        Task<StatusCode> second = loop.EnqueueWriteAsync(Request(binding, (short)2));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        loop.Start(stop.Token);

        Assert.Equal(StatusCode.Good, await first.WaitAsync(stop.Token));
        Assert.Equal(StatusCode.Good, await second.WaitAsync(stop.Token));
        Assert.Equal([(short)1, (short)2], driver.WrittenValues);

        stop.Cancel();
        await loop.Completion;
    }

    [Fact]
    public async Task Latest_mode_reports_replaced_intent_as_superseded_instead_of_good()
    {
        await using var hub = new DataHub();
        var binding = Binding(IgwDataType.Int16);
        hub.Register(binding.TagId, binding.Def);
        var driver = new RecordingDriver();
        var loop = new DeviceLoop(driver, [binding], hub, NullLogger.Instance, "Line.Plc",
            writeOpt: "latest", idlePollMs: 1);

        Task<StatusCode> first = loop.EnqueueWriteAsync(Request(binding, (short)1));
        Task<StatusCode> second = loop.EnqueueWriteAsync(Request(binding, (short)2));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        loop.Start(stop.Token);

        Assert.Equal(StatusCode.BadWriteSuperseded, await first.WaitAsync(stop.Token));
        Assert.Equal(StatusCode.Good, await second.WaitAsync(stop.Token));
        Assert.Equal([(short)2], driver.WrittenValues);

        stop.Cancel();
        await loop.Completion;
    }

    [Fact]
    public async Task Default_mode_preserves_boolean_pulse_edges()
    {
        await using var hub = new DataHub();
        var binding = Binding(IgwDataType.Bool);
        hub.Register(binding.TagId, binding.Def);
        var driver = new RecordingDriver();
        var loop = new DeviceLoop(driver, [binding], hub, NullLogger.Instance, "Line.Plc", idlePollMs: 1);

        Task<StatusCode> rising = loop.EnqueueWriteAsync(Request(binding, true));
        Task<StatusCode> falling = loop.EnqueueWriteAsync(Request(binding, false));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        loop.Start(stop.Token);

        Assert.Equal(StatusCode.Good, await rising.WaitAsync(stop.Token));
        Assert.Equal(StatusCode.Good, await falling.WaitAsync(stop.Token));
        Assert.Equal([true, false], driver.WrittenValues);

        stop.Cancel();
        await loop.Completion;
    }

    [Fact]
    public async Task Queue_capacity_returns_an_explicit_overload_without_silent_drop()
    {
        await using var hub = new DataHub();
        var binding = Binding(IgwDataType.Int16);
        hub.Register(binding.TagId, binding.Def);
        var driver = new RecordingDriver();
        var loop = new DeviceLoop(driver, [binding], hub, NullLogger.Instance, "Line.Plc",
            writeOpt: "all", idlePollMs: 1, maxPendingWrites: 2);

        Task<StatusCode> first = loop.EnqueueWriteAsync(Request(binding, (short)1));
        Task<StatusCode> second = loop.EnqueueWriteAsync(Request(binding, (short)2));
        Task<StatusCode> overloaded = loop.EnqueueWriteAsync(Request(binding, (short)3));

        Assert.Equal(StatusCode.BadTooManyOperations, await overloaded.WaitAsync(TimeSpan.FromSeconds(1)));

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        loop.Start(stop.Token);
        Assert.Equal(StatusCode.Good, await first.WaitAsync(stop.Token));
        Assert.Equal(StatusCode.Good, await second.WaitAsync(stop.Token));
        Assert.Equal([(short)1, (short)2], driver.WrittenValues);
        stop.Cancel();
        await loop.Completion;
    }

    [Fact]
    public async Task Driver_timeout_cancellation_does_not_stop_the_device_loop()
    {
        await using var hub = new DataHub();
        var binding = Binding(IgwDataType.Int16);
        hub.Register(binding.TagId, binding.Def);
        var driver = new ConnectTimeoutThenSuccessDriver();
        var loop = new DeviceLoop(driver, [binding], hub, NullLogger.Instance, "Line.Plc", idlePollMs: 1);
        Task<StatusCode> write = loop.EnqueueWriteAsync(Request(binding, (short)9));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4));

        loop.Start(stop.Token);

        Assert.Equal(StatusCode.Good, await write.WaitAsync(stop.Token));
        Assert.True(driver.ConnectCalls >= 2);
        Assert.Equal(ConnectionState.Connected, driver.State);
        stop.Cancel();
        await loop.Completion;
    }

    [Fact]
    public async Task New_generation_stops_rewriting_an_obsolete_value()
    {
        await using var hub = new DataHub();
        var binding = Binding(IgwDataType.Int16);
        hub.Register(binding.TagId, binding.Def);
        var driver = new SupersedingDriver();
        var loop = new DeviceLoop(driver, [binding], hub, NullLogger.Instance, "Line.Plc",
            writeOpt: "all", idlePollMs: 1);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        loop.Start(stop.Token);

        Task<StatusCode> oldValue = loop.EnqueueWriteAsync(Request(binding, (short)1));
        await driver.FirstWrite.Task.WaitAsync(stop.Token);
        Task<StatusCode> newValue = loop.EnqueueWriteAsync(Request(binding, (short)2));

        Assert.Equal(StatusCode.BadWriteSuperseded, await oldValue.WaitAsync(stop.Token));
        Assert.Equal(StatusCode.Good, await newValue.WaitAsync(stop.Token));
        Assert.True(driver.OldValueWrites < 11, $"旧值仍被写了 {driver.OldValueWrites} 次");

        stop.Cancel();
        await loop.Completion;
    }

    [Fact]
    public async Task Concurrent_capable_driver_dispatches_write_while_a_large_scan_read_is_in_flight()
    {
        await using var hub = new DataHub();
        var binding = Binding(IgwDataType.Int16);
        hub.Register(binding.TagId, binding.Def);
        var demands = new ScanDemandRegistry(hub);
        using var demand = demands.Acquire("process", [binding.TagId], 30_000);
        var driver = new BlockingScanConcurrentDriver();
        var loop = new DeviceLoop(driver, [binding], hub, NullLogger.Instance, "Line.Plc",
            idlePollMs: 1, scanDemands: demands);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        loop.Start(stop.Token);

        try
        {
            await driver.FirstScanStarted.Task.WaitAsync(stop.Token);
            Task<StatusCode> write = loop.EnqueueWriteAsync(Request(binding, (short)9));

            Task completed = await Task.WhenAny(write, Task.Delay(250, stop.Token));

            Assert.Same(write, completed);
            Assert.Equal(StatusCode.Good, await write);
        }
        finally
        {
            driver.ReleaseFirstScan.TrySetResult();
            stop.Cancel();
            await loop.Completion;
        }
    }

    [Fact]
    public async Task Scan_started_before_a_verified_write_cannot_overwrite_the_new_QC_value()
    {
        await using var hub = new DataHub();
        var binding = Binding(IgwDataType.Int16);
        hub.Register(binding.TagId, binding.Def);
        var demands = new ScanDemandRegistry(hub);
        using var demand = demands.Acquire("process", [binding.TagId], 30_000);
        var driver = new BlockingScanConcurrentDriver();
        var loop = new DeviceLoop(driver, [binding], hub, NullLogger.Instance, "Line.Plc",
            idlePollMs: 1, scanDemands: demands);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        loop.Start(stop.Token);

        try
        {
            await driver.FirstScanStarted.Task.WaitAsync(stop.Token);
            Assert.Equal(StatusCode.Good,
                await loop.EnqueueWriteAsync(Request(binding, (short)9)).WaitAsync(stop.Token));

            driver.ReleaseFirstScan.TrySetResult();
            await driver.FirstScanCompleted.Task.WaitAsync(stop.Token);
            await Task.Delay(50, stop.Token);

            Assert.True(hub.TryGet(binding.TagId, out var cached));
            Assert.Equal((short)9, cached.Value);
        }
        finally
        {
            driver.ReleaseFirstScan.TrySetResult();
            stop.Cancel();
            await loop.Completion;
        }
    }

    private static TagBinding Binding(IgwDataType type)
    {
        var definition = new TagDef
        {
            Name = "Value", Address = "Value", DataType = type,
            Access = TagAccess.ReadWrite, ScanRateMs = 30_000,
        };
        return new TagBinding
        {
            TagId = "Line.Plc.DB.Value", Def = definition,
            Address = new TagAddress(0, 0, type == IgwDataType.Bool ? 1 : 2),
        };
    }

    private static WriteRequest Request(TagBinding binding, object value) => new()
    {
        TagId = binding.TagId, Def = binding.Def, Address = binding.Address, Value = value,
    };

    private class RecordingDriver : IDeviceDriver, IDriverCapabilityProvider
    {
        private object? _value;
        public List<object> WrittenValues { get; } = [];
        public virtual string DriverId => "Recording";
        public ConnectionState State { get; protected set; }
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser { get; } = new Parser();
        public DriverLimits Limits { get; } = new() { MaxTagsPerRead = 100, MaxTagsPerWrite = 100 };
        public virtual DriverCapabilitySnapshot Capabilities => DriverCapabilitySnapshot.FromLimits(Limits) with
        {
            SuggestedVerificationDelayMs = 0,
        };

        public virtual Task ConnectAsync(CancellationToken ct)
        {
            State = ConnectionState.Connected;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public virtual Task DisconnectAsync()
        {
            State = ConnectionState.Disconnected;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public virtual Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            return Task.FromResult<IReadOnlyList<TagSample>>(tags.Select(tag =>
                new TagSample(tag.TagId, new DataValue(_value, StatusCode.Good, now, now))).ToArray());
        }

        public virtual Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct)
        {
            foreach (var write in writes)
            {
                _value = write.Value;
                WrittenValues.Add(write.Value);
            }
            return Task.FromResult<IReadOnlyList<WriteResult>>(writes.Select(write =>
                new WriteResult { TagId = write.TagId, Status = StatusCode.Good }).ToArray());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class Parser : IAddressParser
        {
            public bool TryParse(string address, IgwDataType type, out TagAddress result, out string? error)
            {
                result = new TagAddress(0, 0, type == IgwDataType.Bool ? 1 : 2);
                error = null;
                return true;
            }
        }
    }

    private sealed class ConnectTimeoutThenSuccessDriver : RecordingDriver
    {
        public int ConnectCalls { get; private set; }
        public override async Task ConnectAsync(CancellationToken ct)
        {
            ConnectCalls++;
            if (ConnectCalls == 1)
            {
                await Task.Yield();
                throw new OperationCanceledException("driver connect timeout");
            }
            await base.ConnectAsync(ct);
        }
    }

    private sealed class SupersedingDriver : RecordingDriver
    {
        public TaskCompletionSource FirstWrite { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int OldValueWrites { get; private set; }
        public override DriverCapabilitySnapshot Capabilities => base.Capabilities with
        {
            SuggestedVerificationDelayMs = 100,
        };

        public override Task<IReadOnlyList<WriteResult>> WriteAsync(
            IReadOnlyList<WriteRequest> writes,
            CancellationToken ct)
        {
            foreach (var write in writes)
            {
                if (Convert.ToInt16(write.Value) == 1) OldValueWrites++;
                FirstWrite.TrySetResult();
            }
            return base.WriteAsync(writes, ct);
        }

        public override Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            object value = WrittenValues.Count > 0 && Convert.ToInt16(WrittenValues[^1]) == 2 ? (short)2 : (short)-1;
            return Task.FromResult<IReadOnlyList<TagSample>>(tags.Select(tag =>
                new TagSample(tag.TagId, new DataValue(value, StatusCode.Good, now, now))).ToArray());
        }
    }

    private sealed class BlockingScanConcurrentDriver : RecordingDriver
    {
        private int _readCalls;
        public TaskCompletionSource FirstScanStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstScan { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstScanCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override DriverCapabilitySnapshot Capabilities => base.Capabilities with
        {
            MaxInFlight = 2,
            SuggestedVerificationDelayMs = 0,
        };

        public override async Task<IReadOnlyList<TagSample>> ReadAsync(
            IReadOnlyList<TagBinding> tags,
            CancellationToken ct)
        {
            if (Interlocked.Increment(ref _readCalls) == 1)
            {
                var capturedAt = DateTime.UtcNow;
                FirstScanStarted.TrySetResult();
                await ReleaseFirstScan.Task.WaitAsync(ct);
                FirstScanCompleted.TrySetResult();
                return tags.Select(tag => new TagSample(tag.TagId,
                    new DataValue((short)0, StatusCode.Good, capturedAt, capturedAt))).ToArray();
            }
            return await base.ReadAsync(tags, ct);
        }
    }
}
