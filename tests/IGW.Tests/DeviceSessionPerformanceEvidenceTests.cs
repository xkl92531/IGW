using IGW.Abstractions;
using IGW.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class DeviceSessionPerformanceEvidenceTests
{
    [Fact]
    public async Task Polling_and_priority_write_share_primary_and_reconnect_changes_only_affected_generation()
    {
        await using var hub = new DataHub();
        var definitions = new[] { "A", "B" }.Select(name => new TagBinding
        {
            TagId = $"Line.{name}.Value", Address = new(1, 0, 2),
            Def = new TagDef { Name = "Value", Address = "DB1.DBW0", DataType = IgwDataType.Int16,
                Access = TagAccess.ReadWrite, ScanRateMs = 10 }
        }).ToArray();
        foreach (var binding in definitions) hub.Register(binding.TagId, binding.Def);
        var demands = new ScanDemandRegistry(hub);
        using var demand = demands.Acquire("session-evidence", definitions.Select(t => t.TagId).ToArray(), 10);
        var drivers = new[] { new SessionDriver(), new SessionDriver() };
        var loops = drivers.Select((driver, i) => new DeviceLoop(driver, [definitions[i]], hub,
            NullLogger.Instance, i == 0 ? "Line.A" : "Line.B", scanDemands: demands,
            configuredRemoteAddress: "127.0.0.1:102", protocol: "Siemens.S7", deviceIdentity: $"DEVICE-{i}")).ToArray();
        using var stop = new CancellationTokenSource();
        foreach (var loop in loops) loop.Start(stop.Token);
        try
        {
            await Until(() => drivers.All(d => Volatile.Read(ref d.Reads) > 0));
            var before = loops.Select(l => l.ScanTimingSummary()).ToArray();
            Assert.All(before, summary => Assert.Equal(1, summary.SessionGeneration));
            Assert.NotEqual(before[0].DeviceSessionFingerprint, before[1].DeviceSessionFingerprint);
            var tag = definitions[0];
            var result = await loops[0].EnqueueWriteAsync(new WriteRequest
                { TagId = tag.TagId, Def = tag.Def, Address = tag.Address, Value = (short)7 }).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(result.IsGood);
            Assert.Equal(1, drivers[0].Connects);
            Assert.Equal(1, drivers[0].WriteGeneration);
            Assert.True(drivers[0].Writes > 0);
            Assert.True(hub.TryGet(tag.TagId, out var written)); Assert.Equal((short)7, written.Value);
            Assert.True(await loops[0].ReconnectNowAsync(default).WaitAsync(TimeSpan.FromSeconds(3)));
            await Until(() => loops[0].ScanTimingSummary().SessionGeneration == 2);
            Assert.Equal(1, loops[1].ScanTimingSummary().SessionGeneration);
            Assert.Equal(before[0].DeviceSessionFingerprint, loops[0].ScanTimingSummary().DeviceSessionFingerprint);
            Assert.Equal(2, drivers[0].Connects); Assert.Equal(1, drivers[1].Connects);
            Assert.All(drivers, driver => { Assert.Equal(1, driver.Active); Assert.Equal(1, driver.Maximum); });
        }
        finally { stop.Cancel(); await Task.WhenAll(loops.Select(l => l.Completion)); }
        Assert.All(drivers, driver => Assert.Equal(0, driver.Active));
    }

    private static async Task Until(Func<bool> condition)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(5, stop.Token);
    }

    private sealed class SessionDriver : IDeviceDriver
    {
        public string DriverId => "Siemens.S7";
        public ConnectionState State { get; private set; }
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser { get; } = new Parser();
        public DriverLimits Limits { get; } = new();
        public int Reads, Writes, Connects, Active, Maximum, WriteGeneration;
        private short _value;
        public Task ConnectAsync(CancellationToken ct)
        {
            Connects++; Active++; Maximum = Math.Max(Maximum, Active);
            State = ConnectionState.Connected; StateChanged?.Invoke(State); return Task.CompletedTask;
        }
        public Task DisconnectAsync()
        {
            if (State == ConnectionState.Connected) Active--;
            State = ConnectionState.Disconnected; StateChanged?.Invoke(State); return Task.CompletedTask;
        }
        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
        {
            Interlocked.Increment(ref Reads);
            return Task.FromResult<IReadOnlyList<TagSample>>(tags.Select(t => new TagSample(t.TagId,
                DataValue.GoodAt(_value, DateTime.UtcNow))).ToArray());
        }
        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct)
        {
            Writes++; WriteGeneration = Connects; _value = Convert.ToInt16(writes[^1].Value);
            return Task.FromResult<IReadOnlyList<WriteResult>>(writes.Select(w => new WriteResult
                { TagId = w.TagId, Status = StatusCode.Good }).ToArray());
        }
        public async ValueTask DisposeAsync() => await DisconnectAsync();
        private sealed class Parser : IAddressParser
        {
            public bool TryParse(string text, IgwDataType type, out TagAddress address, out string? error)
                { address = new(1, 0, 2); error = null; return true; }
        }
    }
}
