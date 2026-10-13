using System.Collections.Concurrent;
using IGW.Abstractions;
using IGW.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class DeviceLoopScanDemandTests
{
    [Fact]
    public async Task Default_scan_mode_reads_only_the_touched_tag_instead_of_the_whole_device()
    {
        await using var hub = new DataHub();
        const string touchedTag = "Line.Plc.DB.Touched";
        const string idleTag = "Line.Plc.DB.Idle";
        var touchedDef = new TagDef { Name = "Touched", Address = "DB1.DBW0", DataType = IgwDataType.Int16, ScanRateMs = 20 };
        var idleDef = new TagDef { Name = "Idle", Address = "DB1.DBW2", DataType = IgwDataType.Int16, ScanRateMs = 20 };
        hub.Register(touchedTag, touchedDef);
        hub.Register(idleTag, idleDef);
        var driver = new RecordingDriver();
        var loop = new DeviceLoop(driver,
            [
                new TagBinding { TagId = touchedTag, Def = touchedDef, Address = new TagAddress(1, 0, 2) },
                new TagBinding { TagId = idleTag, Def = idleDef, Address = new TagAddress(1, 2, 2) },
            ],
            hub, NullLogger.Instance, "Line.Plc", scanDemands: new ScanDemandRegistry(hub));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        loop.Start(stop.Token);
        hub.Touch(touchedTag);
        await WaitUntilAsync(() => driver.HasRead(touchedTag), TimeSpan.FromSeconds(1));

        Assert.False(driver.HasRead(idleTag));
        stop.Cancel();
        await loop.Completion;
    }

    [Fact]
    public async Task Device_connects_without_mes_and_qc_touch_starts_on_demand_read()
    {
        await using var hub = new DataHub();
        const string qcTag = "Line.Plc.DB.PlcFlag";
        var qcDef = new TagDef { Name = "PlcFlag", Address = "DB1.DBW2", DataType = IgwDataType.Int16, ScanRateMs = 20 };
        hub.Register(qcTag, qcDef);

        var driver = new RecordingDriver();
        var loop = new DeviceLoop(driver,
            [new TagBinding { TagId = qcTag, Def = qcDef, Address = new TagAddress(1, 2, 2) }],
            hub, NullLogger.Instance, "Line.Plc", scanMode: "all", scanDemands: new ScanDemandRegistry(hub));
        using var stop = new CancellationTokenSource();

        loop.Start(stop.Token);
        await WaitUntilAsync(() => driver.State == ConnectionState.Connected, TimeSpan.FromSeconds(2));
        await Task.Delay(100);
        Assert.False(driver.HasRead(qcTag));

        hub.Touch(qcTag);

        await WaitUntilAsync(() => driver.HasRead(qcTag), TimeSpan.FromSeconds(2));
        Assert.Equal(1, loop.ActiveTagCount);
        stop.Cancel();
        await loop.Completion;
    }

    [Fact]
    public async Task All_scan_mode_expands_mes_plan_when_qc_touches_another_tag()
    {
        await using var hub = new DataHub();
        const string mesTag = "Line.Plc.DB.MesFlag";
        const string qcTag = "Line.Plc.DB.PlcFlag";
        var mesDef = new TagDef { Name = "MesFlag", Address = "DB1.DBW0", DataType = IgwDataType.Int16, ScanRateMs = 20 };
        var qcDef = new TagDef { Name = "PlcFlag", Address = "DB1.DBW2", DataType = IgwDataType.Int16, ScanRateMs = 20 };
        hub.Register(mesTag, mesDef);
        hub.Register(qcTag, qcDef);

        var demands = new ScanDemandRegistry(hub);
        using var mesDemand = demands.Acquire("mes", [mesTag], 20);
        var driver = new RecordingDriver();
        var loop = new DeviceLoop(driver,
            [
                new TagBinding { TagId = mesTag, Def = mesDef, Address = new TagAddress(1, 0, 2) },
                new TagBinding { TagId = qcTag, Def = qcDef, Address = new TagAddress(1, 2, 2) },
            ],
            hub, NullLogger.Instance, "Line.Plc", scanMode: "all", scanDemands: demands);
        using var stop = new CancellationTokenSource();

        loop.Start(stop.Token);
        await WaitUntilAsync(() => driver.HasRead(mesTag), TimeSpan.FromSeconds(2));
        Assert.False(driver.HasRead(qcTag));

        hub.Touch(qcTag);

        await WaitUntilAsync(() => driver.HasRead(qcTag), TimeSpan.FromSeconds(2));
        Assert.Equal(2, loop.ActiveTagCount);
        stop.Cancel();
        await loop.Completion;
    }

    [Fact]
    public async Task Pending_write_preempts_remaining_due_read_blocks()
    {
        await using var hub = new DataHub();
        var bindings = new List<TagBinding>();
        for (int index = 0; index < 70; index++)
        {
            string tagId = $"Line.Plc.Group.Value{index}";
            var definition = new TagDef
            {
                Name = $"Value{index}",
                Address = $"Area{index}",
                DataType = IgwDataType.Int16,
                ScanRateMs = 1,
                Access = TagAccess.ReadWrite,
            };
            hub.Register(tagId, definition);
            bindings.Add(new TagBinding
            {
                TagId = tagId,
                Def = definition,
                Address = new TagAddress(index, 0, 2),
            });
        }

        var demands = new ScanDemandRegistry(hub);
        using var demand = demands.Acquire("test", bindings.Select(binding => binding.TagId).ToArray(), 1);
        var driver = new SlowMultiBlockDriver();
        var loop = new DeviceLoop(driver, bindings, hub, NullLogger.Instance, "Line.Plc",
            scanDemands: demands);
        using var stop = new CancellationTokenSource();

        loop.Start(stop.Token);
        try
        {
            await driver.FirstReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var target = bindings[0];
            Task<StatusCode> write = loop.EnqueueWriteAsync(new WriteRequest
            {
                TagId = target.TagId,
                Def = target.Def,
                Address = target.Address,
                Value = (short)1,
            });

            Task completed = await Task.WhenAny(write, Task.Delay(300));

            Assert.Same(write, completed);
            Assert.True((await write).IsGood);
            Assert.True(driver.ReadCalls < 20,
                $"写请求等待了过多读取块，写入前已读取 {driver.ReadCalls} 个块");
        }
        finally
        {
            stop.Cancel();
            await loop.Completion;
        }
    }

    [Fact]
    public async Task Write_timeout_does_not_disconnect_a_still_connected_device()
    {
        await using var hub = new DataHub();
        const string tagId = "Line.Plc.Group.Value";
        var definition = new TagDef
        {
            Name = "Value",
            Address = "Area1",
            DataType = IgwDataType.Int16,
            ScanRateMs = 20,
            Access = TagAccess.ReadWrite,
        };
        hub.Register(tagId, definition);
        var binding = new TagBinding
        {
            TagId = tagId,
            Def = definition,
            Address = new TagAddress(1, 0, 2),
        };
        var demands = new ScanDemandRegistry(hub);
        using var demand = demands.Acquire("test", [tagId], 20);
        var driver = new WriteFailureDriver(new TimeoutException(
            "single write timed out while the session remains connected"));
        var loop = new DeviceLoop(driver, [binding], hub, NullLogger.Instance, "Line.Plc",
            scanDemands: demands);
        using var stop = new CancellationTokenSource();

        loop.Start(stop.Token);
        try
        {
            await WaitUntilAsync(() => driver.ReadCalls > 0, TimeSpan.FromSeconds(2));
            StatusCode status = await loop.EnqueueWriteAsync(new WriteRequest
            {
                TagId = tagId,
                Def = definition,
                Address = binding.Address,
                Value = (short)1,
            }).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(StatusCode.BadTimeout, status);
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
    public async Task Point_level_write_exception_does_not_disconnect_a_still_connected_device()
    {
        await using var hub = new DataHub();
        const string tagId = "Line.Plc.Group.Value";
        var definition = new TagDef
        {
            Name = "Value", Address = "Area1", DataType = IgwDataType.Int16,
            ScanRateMs = 20, Access = TagAccess.ReadWrite,
        };
        hub.Register(tagId, definition);
        var binding = new TagBinding { TagId = tagId, Def = definition, Address = new TagAddress(1, 0, 2) };
        var demands = new ScanDemandRegistry(hub);
        using var demand = demands.Acquire("test", [tagId], 20);
        var driver = new WriteFailureDriver(new InvalidDataException("value is outside this point definition"));
        var loop = new DeviceLoop(driver, [binding], hub, NullLogger.Instance, "Line.Plc",
            scanDemands: demands);
        using var stop = new CancellationTokenSource();

        loop.Start(stop.Token);
        try
        {
            await WaitUntilAsync(() => driver.ReadCalls > 0, TimeSpan.FromSeconds(2));
            StatusCode status = await loop.EnqueueWriteAsync(new WriteRequest
            {
                TagId = tagId, Def = definition, Address = binding.Address, Value = (short)1,
            }).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(StatusCode.BadDecodeError, status);
            Assert.Equal(0, driver.DisconnectCalls);
            Assert.Equal(ConnectionState.Connected, driver.State);
        }
        finally
        {
            stop.Cancel();
            await loop.Completion;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Timed out waiting for scan plan update.");
            await Task.Delay(10);
        }
    }

    private sealed class RecordingDriver : IDeviceDriver
    {
        private readonly ConcurrentDictionary<string, byte> _readTags = new(StringComparer.OrdinalIgnoreCase);
        public string DriverId => "Recording";
        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser { get; } = new Parser();
        public DriverLimits Limits { get; } = new() { MaxTagsPerRead = 100 };

        public bool HasRead(string tagId) => _readTags.ContainsKey(tagId);

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

        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            var samples = new TagSample[tags.Count];
            for (int i = 0; i < tags.Count; i++)
            {
                _readTags.TryAdd(tags[i].TagId, 0);
                samples[i] = new TagSample(tags[i].TagId, DataValue.GoodAt((short)1, now));
            }
            return Task.FromResult<IReadOnlyList<TagSample>>(samples);
        }

        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WriteResult>>([]);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class Parser : IAddressParser
        {
            public bool TryParse(string address, IgwDataType type, out TagAddress result, out string? error)
            {
                result = new TagAddress(1, address.EndsWith("2", StringComparison.Ordinal) ? 2 : 0, 2);
                error = null;
                return true;
            }
        }
    }

    private sealed class SlowMultiBlockDriver : IDeviceDriver
    {
        private int _readCalls;
        private readonly Dictionary<string, short> _values = new(StringComparer.Ordinal);
        public string DriverId => "SlowMultiBlock";
        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser { get; } = new Parser();
        public DriverLimits Limits { get; } = new() { MaxReadUnits = 2, MaxGap = 0, MaxTagsPerRead = 1 };
        public TaskCompletionSource FirstReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ReadCalls => Volatile.Read(ref _readCalls);

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
            Interlocked.Increment(ref _readCalls);
            FirstReadStarted.TrySetResult();
            await Task.Delay(30, ct);
            var now = DateTime.UtcNow;
            return tags.Select(tag => new TagSample(tag.TagId,
                DataValue.GoodAt(_values.GetValueOrDefault(tag.TagId), now))).ToArray();
        }

        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct)
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
                int area = int.Parse(address.AsSpan("Area".Length));
                result = new TagAddress(area, 0, 2);
                error = null;
                return true;
            }
        }
    }

    private sealed class WriteFailureDriver(Exception error) : IDeviceDriver
    {
        private int _readCalls;
        public string DriverId => "WriteTimeout";
        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser { get; } = new Parser();
        public DriverLimits Limits { get; } = new() { MaxTagsPerRead = 1 };
        public int ReadCalls => Volatile.Read(ref _readCalls);
        public int DisconnectCalls { get; private set; }

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

        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
        {
            Interlocked.Increment(ref _readCalls);
            return Task.FromResult<IReadOnlyList<TagSample>>(
                [new TagSample(tags[0].TagId, DataValue.GoodAt((short)0, DateTime.UtcNow))]);
        }

        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) =>
            throw error;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class Parser : IAddressParser
        {
            public bool TryParse(string address, IgwDataType type, out TagAddress result, out string? error)
            {
                result = new TagAddress(1, 0, 2);
                error = null;
                return true;
            }
        }
    }
}
