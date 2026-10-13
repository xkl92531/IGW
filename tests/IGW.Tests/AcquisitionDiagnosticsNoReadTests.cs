using System.Collections;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Threading.Channels;
using IGW.Abstractions;
using IGW.Core;
using IGW.Northbound.RestApi;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class AcquisitionDiagnosticsNoReadTests
{
    [Theory]
    [InlineData("Siemens.S7", 102)]
    [InlineData("Modbus.Tcp", 502)]
    public async Task Implicit_and_explicit_default_ports_share_counts_but_nondefault_port_stays_distinct(string protocol, int port)
    {
        await using var hub = new DataHub();
        var project = Project();
        var channel = project.Channels[0];
        channel.DriverId = protocol;
        channel.Settings.Clear();
        channel.Devices[0].Id = "IMPLICIT_DEVICE";
        channel.Devices[0].Endpoint = "127.0.0.1";
        channel.Devices.Add(new DeviceDef { Id = "EXPLICIT_DEVICE", Name = "Other", Endpoint = $"127.0.0.1:{port}",
            Tags = [new TagDef { Name = "Value", Address = "1", DataType = IgwDataType.Int16, ScanRateMs = 100 }] });
        channel.Devices.Add(new DeviceDef { Id = "NONDEFAULT_DEVICE", Name = "Alternate", Endpoint = $"127.0.0.1:{port + 1}",
            Tags = [new TagDef { Name = "Value", Address = "1", DataType = IgwDataType.Int16, ScanRateMs = 100 }] });
        var factory = new SeparateFactory(protocol);
        var registry = new DriverRegistry(); registry.Register(factory);
        await using var probes = new NetworkPerformanceProbeCoordinator(new CountingEcho(), cadence: TimeSpan.FromHours(1));
        await using var engine = new RuntimeEngine(project, registry, hub, NullLoggerFactory.Instance, networkProbes: probes);
        await engine.StartAsync();
        foreach (var device in channel.Devices) engine.Touch($"Line.{device.Name}.Value");
        foreach (var driver in factory.Drivers.Values) await driver.WaitForReadAsync(1);
        var snapshot = engine.ScanTimings();
        Assert.Equal(3, snapshot.Devices.Select(device => device.DeviceSessionFingerprint).Distinct().Count());
        Assert.All(snapshot.Devices, device =>
        {
            int expected = device.Name == "Line.Alternate" ? 1 : 2;
            Assert.Equal(expected, device.SameEndpointDeviceCount);
            Assert.Equal(expected, device.SameEndpointSessionCount);
            Assert.Equal(1, device.SessionGeneration);
        });
        await using var stream = await AcquisitionDiagnosticsExport.CreateAsync(engine.AcquisitionDiagnostics(), "ports", CancellationToken.None);
        using var zip = new ZipArchive(stream);
        using var reader = new StreamReader(zip.GetEntry("summary.json")!.Open());
        using var json = System.Text.Json.JsonDocument.Parse(await reader.ReadToEndAsync());
        foreach (var device in json.RootElement.GetProperty("devices").EnumerateArray())
        {
            int expected = device.GetProperty("name").GetString() == "Line.Alternate" ? 1 : 2;
            Assert.Equal(expected, device.GetProperty("sameEndpointDeviceCount").GetInt32());
            Assert.Equal(expected, device.GetProperty("sameEndpointSessionCount").GetInt32());
        }
        Assert.All(factory.Drivers.Values, driver => { Assert.Equal(1, driver.Connects); Assert.Equal(1, driver.Reads); Assert.Equal(0, driver.Writes); });
        foreach (var driver in factory.Drivers.Values) driver.ReleaseRead();
    }

    [Theory]
    [InlineData("OpcUa.Client", 4840)]
    [InlineData("Siemens.S7", 102)]
    [InlineData("Modbus.Tcp", 502)]
    [InlineData("Omron.Fins", 9600)]
    [InlineData("Omron.Cip", 44818)]
    [InlineData("AB.Eip", 44818)]
    [InlineData("Mitsubishi.Mc", 6000)]
    [InlineData("Mqtt.Client", 1883)]
    public async Task Built_in_endpoint_defaults_follow_runtime_factories_without_DNS_or_connection(string protocol, int port)
    {
        await using var hub = new DataHub();
        var driver = new CountingDriver(protocol);
        DeviceLoop Loop(string endpoint) => new(driver, [], hub, NullLogger.Instance, "device",
            protocol: protocol, configuredRemoteAddress: endpoint);
        Assert.Equal(Loop("PLC.example.invalid").DiagnosticEndpointIdentity,
            Loop($"plc.example.invalid:{port}").DiagnosticEndpointIdentity);
        Assert.NotEqual(Loop("PLC.example.invalid").DiagnosticEndpointIdentity,
            Loop($"plc.example.invalid:{port + 1}").DiagnosticEndpointIdentity);
        Assert.Equal(0, driver.Connects);
        Assert.Equal(0, driver.Reads);
    }
    [Fact]
    public async Task Same_endpoint_devices_expose_one_shared_session_without_diagnostic_IO()
    {
        await using var hub = new DataHub();
        var project = Project();
        project.Channels[0].Devices[0].Id = "DEVICE_ONE_ID";
        project.Channels[0].Devices.Add(new DeviceDef { Id = "DEVICE_TWO_ID", Name = "Other", Tags =
            [new TagDef { Name = "Value", Address = "ns=2;s=DB1.Value", DataType = IgwDataType.Int16, ScanRateMs = 100 }] });
        project.Channels[0].Settings["password"] = "PASSWORD_LITERAL";
        var factory = new SeparateFactory();
        var registry = new DriverRegistry(); registry.Register(factory);
        await using var probes = new NetworkPerformanceProbeCoordinator(new CountingEcho(), cadence: TimeSpan.FromHours(1));
        await using var engine = new RuntimeEngine(project, registry, hub, NullLoggerFactory.Instance, networkProbes: probes);
        await engine.StartAsync();
        ScanTimingSnapshot? idle = null;
        await EventuallyAsync(() =>
        {
            var snapshot = engine.ScanTimings();
            if (snapshot.Devices.Count != 2 || snapshot.Devices.Any(device =>
                    device.SameEndpointSessionCount != 1 || device.SessionGeneration != 1)) return false;
            idle = snapshot;
            return true;
        });
        Assert.NotNull(idle);
        Assert.All(idle.Devices, device => { Assert.Equal(2, device.SameEndpointDeviceCount); Assert.Equal(1, device.SameEndpointSessionCount); Assert.Equal(1, device.SessionGeneration); });
        Assert.All(factory.Drivers.Values, driver => { Assert.Equal(1, driver.Connects); Assert.Equal(0, driver.Reads); Assert.Equal(0, driver.Writes); });
        engine.Touch("Line.Plc.Value"); engine.Touch("Line.Other.Value");
        Assert.Single(factory.Drivers.Values);
        foreach (var driver in factory.Drivers.Values)
        {
            await driver.WaitForReadAsync(1);
            await driver.WaitForReadAsync(2);
        }
        var connected = engine.ScanTimings();
        Assert.Equal(2, connected.Devices.Select(device => device.DeviceSessionFingerprint).Distinct().Count());
        Assert.All(connected.Devices, device =>
        {
            Assert.Equal(2, device.SameEndpointDeviceCount); Assert.Equal(1, device.SameEndpointSessionCount);
            Assert.Equal(1, device.SessionGeneration); Assert.Null(device.KnownSessionLimit); Assert.Null(device.SessionLimitSource);
            Assert.Matches("^[a-f0-9]{16}-p$", device.DeviceSessionFingerprint!);
        });
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(connected.Devices, engine.ScanTimings().Devices);
            await using var stream = await AcquisitionDiagnosticsExport.CreateAsync(engine.AcquisitionDiagnostics(), "r", CancellationToken.None);
            using var zip = new ZipArchive(stream);
            using var reader = new StreamReader(zip.GetEntry("summary.json")!.Open());
            string json = await reader.ReadToEndAsync();
            Assert.DoesNotContain("DEVICE_ONE_ID", json); Assert.DoesNotContain("DEVICE_TWO_ID", json);
            Assert.DoesNotContain("PASSWORD_LITERAL", json);
            Assert.All(factory.Drivers.Values, driver => { Assert.Equal(1, driver.Connects); Assert.Equal(2, driver.Reads); Assert.Equal(0, driver.Writes); });
        }
        foreach (var driver in factory.Drivers.Values) { driver.ReleaseRead(); driver.ReleaseRead(); }
    }

    private sealed class SeparateFactory(string protocol = "OpcUa.Client") : IDriverFactory
    {
        public string DriverId => protocol;
        public Dictionary<string, CountingDriver> Drivers { get; } = new();
        public DriverDescriptor Describe() => new() { DriverId = DriverId, DisplayName = DriverId };
        public IDeviceDriver CreateDevice(ChannelDef channel, DeviceDef device) => Drivers[device.Name] = new CountingDriver(protocol);
    }
    [Fact]
    public async Task Cached_diagnostics_and_zip_do_not_add_IO_to_active_scheduled_collection()
    {
        await using var hub = new DataHub();
        var driver = new CountingDriver();
        var factory = new CountingFactory(driver);
        var registry = new DriverRegistry();
        registry.Register(factory);
        var echo = new CountingEcho();
        await using var probes = new NetworkPerformanceProbeCoordinator(
            echo, cadence: TimeSpan.FromHours(1), timeout: TimeSpan.FromSeconds(1));
        await using var engine = new RuntimeEngine(Project(), registry, hub, NullLoggerFactory.Instance,
            networkProbes: probes);

        await engine.StartAsync();
        await echo.WaitForCallAsync(1);
        echo.Release();
        await EventuallyAsync(() => engine.AcquisitionDiagnostics().Network.Count == 1);
        engine.Touch("Line.Plc.Value");

        await driver.WaitForReadAsync(1);
        driver.ReleaseRead();
        await EventuallyAsync(() => engine.ScanTimings("Line.Plc", limit: 200).Records.Count == 1);
        await driver.WaitForReadAsync(2); // Keep a real scheduled read and scheduler permit active.

        var scheduler = SharedScheduler(engine);
        var baseline = Counters(engine, driver, factory, echo, probes, scheduler);
        Assert.Equal(new IoCounters(
            Reads: 2, Writes: 0, Connects: 1, ActiveSessions: 1, Resets: 0,
            BrowseCalls: 0, DescribeCalls: 0, DriverCreations: 1,
            SameSessionRetries: 0, BatchDownshifts: 0,
            SchedulerActive: 1, SchedulerQueued: 0,
            ProbeEchoCalls: 1, ProbeWorkers: 1, ProbeReferences: 1), baseline);

        for (int attempt = 0; attempt < 5; attempt++)
        {
            var timings = engine.ScanTimings("Line.Plc", "Value", 200);
            Assert.Single(timings.Devices);
            var retained = Assert.Single(timings.Records);
            Assert.NotNull(retained.Network); // This is the cached trailing network window.
            Assert.Null(retained.LatestProtocolRequestCount); // The fake driver has no raw instrumentation.
            Assert.Equal(1, retained.LogicalBatchCount);
            Assert.Equal(1, timings.Devices[0].SameEndpointSessionCount);
            Assert.Equal(1, timings.Devices[0].SessionGeneration);
            Assert.NotNull(timings.Devices[0].DeviceSessionFingerprint);

            var snapshot = engine.AcquisitionDiagnostics();
            Assert.Single(snapshot.Network);
            Assert.NotNull(Assert.Single(snapshot.Devices).Records[0].Network);
            await using var archive = await AcquisitionDiagnosticsExport.CreateAsync(
                snapshot, "no-io-proof", CancellationToken.None);
            using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
            Assert.Equal(
                ["README.txt", "batches.csv", "connection-diagnostics.json", "network.csv", "requests.csv", "summary.json"],
                zip.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal).ToArray());

            Assert.Equal(baseline, Counters(engine, driver, factory, echo, probes, scheduler));
        }

        driver.ReleaseRead();
        await EventuallyAsync(() => engine.ScanTimings("Line.Plc", limit: 200).Records.Count >= 2);
        await driver.WaitForReadAsync(3);
        var resumed = Counters(engine, driver, factory, echo, probes, scheduler);
        Assert.Equal(3, resumed.Reads);
        Assert.Equal(1, resumed.SchedulerActive);
        Assert.Equal(0, resumed.SchedulerQueued);
        Assert.Equal(0, resumed.Writes);
        Assert.Equal(1, resumed.Connects);
        Assert.Equal(1, resumed.ActiveSessions);
        Assert.Equal(0, resumed.SameSessionRetries);
        Assert.Equal(0, resumed.BatchDownshifts);
        Assert.Equal(1, resumed.ProbeEchoCalls);
        Assert.Equal(1, resumed.ProbeReferences);

        driver.ReleaseRead();
    }

    private static IgwProject Project() => new()
    {
        Name = "No I/O proof",
        Channels =
        [
            new ChannelDef
            {
                Name = "Line",
                DriverId = "OpcUa.Client",
                Settings = new() { ["url"] = "opc.tcp://127.0.0.1:4840" },
                Devices =
                [
                    new DeviceDef
                    {
                        Name = "Plc",
                        Tags =
                        [
                            new TagDef
                            {
                                Name = "Value",
                                Address = "ns=2;s=DB1.Value",
                                DataType = IgwDataType.Int16,
                                ScanRateMs = 100,
                            },
                        ],
                    },
                ],
            },
        ],
    };

    private static PriorityOperationScheduler SharedScheduler(RuntimeEngine engine)
    {
        var devices = (IEnumerable)typeof(RuntimeEngine)
            .GetField("_devices", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(engine)!;
        var runtime = devices.Cast<object>().Single();
        var lease = (SharedOpcUaDriverLease)runtime.GetType().GetProperty("Driver")!.GetValue(runtime)!;
        var entry = typeof(SharedOpcUaDriverLease)
            .GetField("entry", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(lease)!;
        return (PriorityOperationScheduler)entry.GetType().GetField("Scheduler")!.GetValue(entry)!;
    }

    private static IoCounters Counters(
        RuntimeEngine engine,
        CountingDriver driver,
        CountingFactory factory,
        CountingEcho echo,
        NetworkPerformanceProbeCoordinator probes,
        PriorityOperationScheduler scheduler)
    {
        var status = Assert.Single(engine.DeviceStatuses());
        var workers = (IDictionary)typeof(NetworkPerformanceProbeCoordinator)
            .GetField("_workers", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(probes)!;
        var worker = Assert.Single(workers.Values.Cast<object>());
        int references = (int)worker.GetType().GetProperty("References")!.GetValue(worker)!;
        int active = (int)typeof(PriorityOperationScheduler)
            .GetField("_active", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(scheduler)!;
        return new(
            driver.Reads, driver.Writes, driver.Connects, driver.ActiveSessions, driver.Resets,
            driver.BrowseCalls, factory.DescribeCalls, factory.DriverCreations,
            (int)status.Stats.SameSessionRetries, (int)status.Stats.BatchDownshifts,
            active, scheduler.Queued, echo.Calls, workers.Count, references);
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed record IoCounters(
        int Reads,
        int Writes,
        int Connects,
        int ActiveSessions,
        int Resets,
        int BrowseCalls,
        int DescribeCalls,
        int DriverCreations,
        int SameSessionRetries,
        int BatchDownshifts,
        int SchedulerActive,
        int SchedulerQueued,
        int ProbeEchoCalls,
        int ProbeWorkers,
        int ProbeReferences);

    private sealed class CountingFactory(CountingDriver driver) : IDriverFactory
    {
        public string DriverId => "OpcUa.Client";
        public int DescribeCalls;
        public int DriverCreations;

        public DriverDescriptor Describe()
        {
            Interlocked.Increment(ref DescribeCalls);
            return new() { DriverId = DriverId, DisplayName = DriverId };
        }

        public IDeviceDriver CreateDevice(ChannelDef channel, DeviceDef device)
        {
            Interlocked.Increment(ref DriverCreations);
            return driver;
        }
    }

    private sealed class CountingDriver(string protocol = "OpcUa.Client") : IDeviceDriver, IConnectionResettableDriver, IBrowsableDriver
    {
        private readonly Channel<int> _enteredReads = Channel.CreateUnbounded<int>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        private readonly SemaphoreSlim _readReleases = new(0);
        public string DriverId => protocol;
        public ConnectionState State { get; private set; }
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser { get; } = new Parser();
        public DriverLimits Limits { get; } = new();
        public int Reads;
        public int Writes;
        public int Connects;
        public int ActiveSessions;
        public int Resets;
        public int BrowseCalls;

        public Task ConnectAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Connects);
            Interlocked.Increment(ref ActiveSessions);
            State = ConnectionState.Connected;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            if (State != ConnectionState.Disconnected) Interlocked.Decrement(ref ActiveSessions);
            State = ConnectionState.Disconnected;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public async Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
        {
            int call = Interlocked.Increment(ref Reads);
            await _enteredReads.Writer.WriteAsync(call, ct);
            await _readReleases.WaitAsync(ct);
            return tags.Select(tag => new TagSample(tag.TagId,
                new DataValue(call, StatusCode.Good, DateTime.UtcNow, DateTime.UtcNow))).ToArray();
        }

        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct)
        {
            Interlocked.Increment(ref Writes);
            return Task.FromResult<IReadOnlyList<WriteResult>>([]);
        }

        public Task ResetConnectionAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Resets);
            return Task.CompletedTask;
        }

        public Task<BrowseResult> BrowseAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref BrowseCalls);
            return Task.FromResult(new BrowseResult([], false));
        }

        public async Task WaitForReadAsync(int expected)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Assert.Equal(expected, await _enteredReads.Reader.ReadAsync(timeout.Token));
        }

        public void ReleaseRead() => _readReleases.Release();

        public async ValueTask DisposeAsync()
        {
            await DisconnectAsync();
            _readReleases.Dispose();
        }
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

    private sealed class CountingEcho : INetworkEchoClient
    {
        private readonly Channel<int> _enteredCalls = Channel.CreateUnbounded<int>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        private readonly SemaphoreSlim _releases = new(0);
        public int Calls;

        public async Task<NetworkEchoResult> SendAsync(
            IPAddress remote,
            IPAddress? source,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            int call = Interlocked.Increment(ref Calls);
            await _enteredCalls.Writer.WriteAsync(call, cancellationToken);
            await _releases.WaitAsync(cancellationToken);
            return new(true, 1.25, "success", null, RouteVerified: false);
        }

        public async Task WaitForCallAsync(int expected)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Assert.Equal(expected, await _enteredCalls.Reader.ReadAsync(timeout.Token));
        }

        public void Release() => _releases.Release();
    }
}
