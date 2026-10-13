using IGW.Abstractions;
using IGW.Core;
using IGW.Drivers.EtherNetIP;
using IGW.Drivers.IoTClient;
using IGW.Drivers.Mqtt;
using IGW.Drivers.Mitsubishi;
using IGW.Drivers.Simulator;
using libplctag;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class ProtocolWriteLatencySchedulingTests
{
    [Fact]
    public async Task Actual_mc_logical_read_yields_before_remaining_range_and_allows_priority_write()
    {
        await using var server = new DriverProtocolTimingTests.PlcServer(false);
        bool writePending = false;
        server.OnRequest = number => { if (number == 1) Volatile.Write(ref writePending, true); };
        server.Start();
        await using var driver = new McTcpDeviceDriver("127.0.0.1", server.Port, 2000);
        await driver.ConnectAsync(default);
        driver.SetPriorityWorkProbe(() => Volatile.Read(ref writePending));
        var first = DriverProtocolTimingTests.Binding("D100");
        var remaining = DriverProtocolTimingTests.Binding("D1000");
        using var timing = ProtocolOperationTiming.Begin();
        await Assert.ThrowsAsync<DriverReadYieldException>(() => driver.ReadAsync([first, remaining], default));
        Assert.Equal(1, server.Requests);
        Assert.Equal(1, timing.Snapshot().Requests);
        var written = await driver.WriteAsync([Request(first, (short)9)], default);
        Assert.True(Assert.Single(written).Status.IsGood);
        Assert.Equal(2, server.Requests);
        Volatile.Write(ref writePending, false);
        Assert.True(Assert.Single(await driver.ReadAsync([remaining], default)).Value.IsGood);
        Assert.Equal(new[] { "Read", "Write", "Read" }, timing.Snapshot().Samples!.Select(s => s.Descriptor!.Operation));
        Assert.Equal(ConnectionState.Connected, driver.State);
    }

    [Fact]
    public void Serial_iot_driver_exposes_one_native_batch_as_the_read_slice()
    {
        var driver = new IoTPlcDeviceDriver("test", () => throw new NotSupportedException(), batchSize: 19);

        Assert.Equal(19, driver.Capabilities.MaxTagsPerRead);
        Assert.Equal(1, driver.Capabilities.MaxInFlight);
    }

    [Fact]
    public void Concurrent_safe_drivers_use_the_independent_write_pipeline()
    {
        var etherNetIp = new EtherNetIpDeviceDriver(
            "Omron.Cip", "127.0.0.1", null, PlcType.Omron, 100, 128,
            transportProbe: _ => Task.FromResult(true));
        var mqtt = new MqttDeviceDriver("127.0.0.1", 1883, "", "", "test");
        var simulator = new SimulatorDeviceDriver();

        Assert.True(etherNetIp.Capabilities.MaxInFlight > 1);
        Assert.True(mqtt.Capabilities.MaxInFlight > 1);
        Assert.True(simulator.Capabilities.MaxInFlight > 1);
    }

    [Fact]
    public void Every_single_session_protocol_can_yield_at_a_safe_frame_boundary()
    {
        var iot = new IoTPlcDeviceDriver("test", () => throw new NotSupportedException(), batchSize: 19);
        var modbus = new IoTModbusDeviceDriver("127.0.0.1", 502, 1,
            IoTClient.Enums.EndianFormat.ABCD, "ABCD", 100, zeroBased: true);
        var mitsubishi = new McTcpDeviceDriver("127.0.0.1", 5000, 100);
        var siemens = new SiemensHybridDeviceDriver("127.0.0.1", 102, TimeSpan.FromMilliseconds(100),
            false, new EmptyClassic(), new EmptyPlus());

        Assert.IsAssignableFrom<IDriverPriorityWorkAware>(iot);
        Assert.IsAssignableFrom<IDriverPriorityWorkAware>(modbus);
        Assert.IsAssignableFrom<IDriverPriorityWorkAware>(mitsubishi);
        Assert.IsAssignableFrom<IDriverPriorityWorkAware>(siemens);
        Assert.Equal(19, siemens.Capabilities.MaxTagsPerRead);
        Assert.Equal(1, siemens.Capabilities.MaxInFlight);
    }

    [Fact]
    public async Task Serial_driver_yields_between_protocol_frames_without_faulting_the_read_path()
    {
        await using var hub = new DataHub();
        var binding = Binding();
        hub.Register(binding.TagId, binding.Def);
        var demands = new ScanDemandRegistry(hub);
        using var demand = demands.Acquire("process", [binding.TagId], 30_000);
        var driver = new YieldingSerialDriver();
        var loop = new DeviceLoop(driver, [binding], hub, NullLogger.Instance, "Line.Plc",
            idlePollMs: 1, scanDemands: demands);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        loop.Start(stop.Token);

        try
        {
            await driver.FirstFrameStarted.Task.WaitAsync(stop.Token);
            Task<StatusCode> write = loop.EnqueueWriteAsync(Request(binding, (short)9));

            Assert.Equal(StatusCode.Good, await write.WaitAsync(TimeSpan.FromMilliseconds(500), stop.Token));
            Assert.True(driver.YieldedForWrite);
            Assert.Equal(0, loop.Stats.ReadsErr);
            Assert.Equal(0, driver.DisconnectCalls);
            Assert.Equal(ConnectionState.Connected, driver.State);
        }
        finally
        {
            stop.Cancel();
            await loop.Completion;
        }
    }

    [Fact]
    public async Task Pending_next_write_does_not_interrupt_current_write_verification()
    {
        await using var hub = new DataHub();
        var first = Binding("First");
        var second = Binding("Second");
        hub.Register(first.TagId, first.Def);
        hub.Register(second.TagId, second.Def);
        var demands = new ScanDemandRegistry(hub);
        using var demand = demands.Acquire("process", [first.TagId, second.TagId], 30_000);
        var driver = new YieldingSerialDriver();
        var loop = new DeviceLoop(driver, [first, second], hub, NullLogger.Instance, "Line.Plc",
            idlePollMs: 1, scanDemands: demands);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        loop.Start(stop.Token);

        try
        {
            await driver.FirstFrameStarted.Task.WaitAsync(stop.Token);
            Task<StatusCode> firstWrite = loop.EnqueueWriteAsync(Request(first, (short)9));
            Task<StatusCode> secondWrite = loop.EnqueueWriteAsync(Request(second, (short)10));

            Assert.Equal(StatusCode.Good, await firstWrite.WaitAsync(TimeSpan.FromMilliseconds(750), stop.Token));
            Assert.Equal(StatusCode.Good, await secondWrite.WaitAsync(TimeSpan.FromMilliseconds(750), stop.Token));
            Assert.Equal(0, loop.Stats.WritesErr);
            Assert.Equal(ConnectionState.Connected, driver.State);
        }
        finally
        {
            stop.Cancel();
            await loop.Completion;
        }
    }

    private static TagBinding Binding(string name = "Value")
    {
        var definition = new TagDef
        {
            Name = name, Address = name, DataType = IgwDataType.Int16,
            Access = TagAccess.ReadWrite, ScanRateMs = 30_000,
        };
        return new TagBinding
        {
            TagId = $"Line.Plc.Group.{name}", Def = definition,
            Address = new TagAddress(0, 0, 2),
        };
    }

    private static WriteRequest Request(TagBinding binding, object value) => new()
    {
        TagId = binding.TagId,
        Def = binding.Def,
        Address = binding.Address,
        Value = value,
    };

    private sealed class YieldingSerialDriver : IDeviceDriver, IDriverCapabilityProvider, IDriverPriorityWorkAware
    {
        private Func<bool> _priorityWorkPending = static () => false;
        private readonly Dictionary<string, short> _values = new(StringComparer.Ordinal);
        private int _readCalls;

        public TaskCompletionSource FirstFrameStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool YieldedForWrite { get; private set; }
        public int DisconnectCalls { get; private set; }
        public string DriverId => "YieldingSerial";
        public ConnectionState State { get; private set; }
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser { get; } = new Parser();
        public DriverLimits Limits { get; } = new() { MaxTagsPerRead = 16, MaxTagsPerWrite = 1 };
        public DriverCapabilitySnapshot Capabilities => DriverCapabilitySnapshot.FromLimits(Limits) with
        {
            SuggestedVerificationDelayMs = 0,
        };

        public void SetPriorityWorkProbe(Func<bool> priorityWorkPending) =>
            _priorityWorkPending = priorityWorkPending;

        public Task ConnectAsync(CancellationToken ct)
        {
            State = ConnectionState.Connected;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            DisconnectCalls++;
            State = ConnectionState.Disconnected;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public async Task<IReadOnlyList<TagSample>> ReadAsync(
            IReadOnlyList<TagBinding> tags,
            CancellationToken ct)
        {
            if (Interlocked.Increment(ref _readCalls) == 1)
            {
                FirstFrameStarted.TrySetResult();
                for (int index = 0; index < 50; index++)
                {
                    await Task.Delay(5, ct);
                    if (_priorityWorkPending())
                    {
                        YieldedForWrite = true;
                        throw new DriverReadYieldException();
                    }
                }
            }

            var now = DateTime.UtcNow;
            return tags.Select(tag => new TagSample(tag.TagId,
                DataValue.GoodAt(_values.GetValueOrDefault(tag.TagId), now))).ToArray();
        }

        public Task<IReadOnlyList<WriteResult>> WriteAsync(
            IReadOnlyList<WriteRequest> writes,
            CancellationToken ct)
        {
            foreach (var write in writes) _values[write.TagId] = Convert.ToInt16(write.Value);
            return Task.FromResult<IReadOnlyList<WriteResult>>(writes.Select(write =>
                new WriteResult { TagId = write.TagId, Status = StatusCode.Good }).ToArray());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

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

    private sealed class EmptyClassic : ISiemensClassicTransport
    {
        public bool IsConnected => false;
        public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TagSample>>([]);
        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WriteResult>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EmptyPlus : ISiemensPlusTransport
    {
        public bool IsConnected => false;
        public Task ConnectAsync(string host, ushort port, TimeSpan timeout, bool legacy, CancellationToken ct) => Task.CompletedTask;
        public Task AuthenticateAsync(string username, string password, CancellationToken ct) => Task.CompletedTask;
        public Task ProbeAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<SiemensPlusNativeBrowseTag>> BrowseAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SiemensPlusNativeBrowseTag>>([]);
        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TagSample>>([]);
        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WriteResult>>([]);
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
