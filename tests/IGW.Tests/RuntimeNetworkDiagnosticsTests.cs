using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using IGW.Abstractions;
using IGW.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class RuntimeNetworkDiagnosticsTests
{
    [Fact]
    public async Task Failover_and_return_attach_network_window_for_the_driver_that_served_the_batch()
    {
        var echo = new PathEcho();
        using var coordinator = new NetworkPerformanceProbeCoordinator(echo, cadence: TimeSpan.FromMilliseconds(10));
        var project = Project("unused", "A");
        var channel = project.Channels[0];
        channel.Settings["url"] = "";
        channel.Devices[0].Endpoint = "opc.tcp://127.0.0.1:4840";
        channel.Devices[0].Settings["secondaryEndpoint"] = "opc.tcp://127.0.0.2:4840";
        channel.Devices[0].Settings["demoteAfter"] = "1";
        channel.Devices[0].Settings["monitorIntervalSec"] = "1";
        var factory = new PathFactory();
        var registry = new DriverRegistry();
        registry.Register(factory);
        await using var engine = new RuntimeEngine(project, registry, new DataHub(), NullLoggerFactory.Instance, networkProbes: coordinator);
        await engine.StartAsync();
        using var demand = engine.ScanDemands.Acquire("mes", ["C.A.Value"], 30);
        await Until(() => engine.ScanTimings("C.A").Records.FirstOrDefault()?.Network?.LatestRttMs == 11);
        var primary = factory.Drivers["127.0.0.1"];
        primary.TransportFailed = true;
        await Until(() => engine.Hub.TryGet("C.A.Value", out var value) && Equals(value.Value, (short)22));
        await Until(() => engine.ScanTimings("C.A").Records.FirstOrDefault()?.Network?.LatestRttMs == 22);
        long secondarySequence = engine.ScanTimings("C.A").Records[0].Sequence;
        primary.TransportFailed = false;
        await Until(() => engine.Hub.TryGet("C.A.Value", out var value) && Equals(value.Value, (short)11)
            && engine.ScanTimings("C.A").Records[0].Sequence > secondarySequence
            && engine.ScanTimings("C.A").Records[0].Network?.LatestRttMs == 11);
        Assert.Contains(echo.Calls, call => call.Remote == "127.0.0.2");
        Assert.Equal(2, factory.Drivers.Count);
    }

    [Fact]
    public async Task Prepared_adapter_without_actual_relay_connection_cannot_claim_route_verification()
    {
        using var coordinator = new NetworkPerformanceProbeCoordinator(new SuccessfulEcho(), cadence: TimeSpan.FromMilliseconds(10));
        var registry = new DriverRegistry();
        registry.Register(new Factory()); // This fake never opens the rewritten relay endpoint.
        await using var engine = new RuntimeEngine(Project("192.0.2.10", "A"), registry, new DataHub(), NullLoggerFactory.Instance,
            networkAdapters: new Catalog(), networkProbes: coordinator);
        await engine.StartAsync();
        using var demand = engine.ScanDemands.Acquire("mes", ["C.A.Value"], 30);
        await Until(() => engine.ScanTimings("C.A").Records.Count >= 5);
        Assert.Equal(NetworkRelayState.Listening, engine.NetworkBindingSnapshot("C.A")!.State);
        Assert.All(engine.ScanTimings("C.A").Records, batch => Assert.False(batch.Network?.RouteVerified ?? false));
    }

    [Fact]
    public async Task Adapter_change_between_actual_relay_connections_refreshes_probe_without_rebuilding_device()
    {
        await using var target = new LocalEchoServer();
        var catalog = new ChangingCatalog();
        var echo = new PathEcho();
        using var coordinator = new NetworkPerformanceProbeCoordinator(echo, cadence: TimeSpan.FromMilliseconds(10));
        var factory = new SocketFactory();
        var registry = new DriverRegistry();
        registry.Register(factory);
        // The trailing dot resolves locally but goes through NetworkBindingPlan's hostname relay path.
        var project = Project($"localhost.", "A");
        project.Channels[0].Settings["url"] = $"opc.tcp://localhost.:{target.Port}";
        await using var engine = new RuntimeEngine(project, registry, new DataHub(), NullLoggerFactory.Instance,
            networkAdapters: catalog, networkProbes: coordinator);
        await engine.StartAsync();
        using var demand = engine.ScanDemands.Acquire("mes", ["C.A.Value"], 30);
        await Until(() => engine.ScanTimings("C.A").Records.FirstOrDefault()?.Network is { LatestRttMs: 11, RouteVerified: true });
        Assert.Equal("127.0.0.1", target.Sources.Last().ToString());
        var physicalDriver = factory.Driver!;
        catalog.Address = "127.0.0.2";
        physicalDriver.ReconnectOnNextRead = true;
        await Until(() => target.Sources.LastOrDefault()?.ToString() == "127.0.0.2");
        await Until(() => engine.ScanTimings("C.A").Records.FirstOrDefault()?.Network is { LatestRttMs: 22, RouteVerified: true });
        // The reconnect occurred inside this read: neither the old path's nor the new path's window belongs to it.
        Assert.Null(engine.ScanTimings("C.A").Records.Single(batch => batch.Sequence == physicalDriver.ReconnectRead).Network);
        Assert.Equal(1, factory.Created);
        Assert.Same(physicalDriver, factory.Driver);
        int oldSourceCalls = echo.Calls.Count(call => call.Source == "127.0.0.1");
        long sequence = engine.ScanTimings("C.A").Records[0].Sequence;
        await Until(() => engine.ScanTimings("C.A").Records[0].Sequence >= sequence + 3);
        Assert.Equal(oldSourceCalls, echo.Calls.Count(call => call.Source == "127.0.0.1"));
        await engine.StopAsync();
        int stoppedCalls = echo.Calls.Count;
        await Task.Delay(40);
        Assert.Equal(stoppedCalls, echo.Calls.Count);
    }
    [Fact]
    public async Task Overlapping_relay_connections_clear_batch_network_until_original_socket_is_the_only_survivor()
    {
        await using var target = new LocalEchoServer();
        var catalog = new ChangingCatalog();
        using var coordinator = new NetworkPerformanceProbeCoordinator(new PathEcho(), cadence: TimeSpan.FromMilliseconds(10));
        var factory = new SocketFactory();
        var registry = new DriverRegistry();
        registry.Register(factory);
        var project = Project("localhost.", "A");
        project.Channels[0].Settings["url"] = $"opc.tcp://localhost.:{target.Port}";
        await using var engine = new RuntimeEngine(project, registry, new DataHub(), NullLoggerFactory.Instance,
            networkAdapters: catalog, networkProbes: coordinator);
        await engine.StartAsync();
        using var demand = engine.ScanDemands.Acquire("mes", ["C.A.Value"], 30);
        await Until(() => engine.ScanTimings("C.A").Records.FirstOrDefault()?.Network is { LatestRttMs: 11, RouteVerified: true });
        catalog.Address = "127.0.0.2";
        using var overlap = new TcpClient(AddressFamily.InterNetwork);
        await overlap.ConnectAsync(engine.NetworkBindingSnapshot("C.A")!.LoopbackEndpoint);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await overlap.GetStream().WriteAsync(new byte[] { 7 }, timeout.Token);
        await overlap.GetStream().ReadExactlyAsync(new byte[1], timeout.Token);
        long sequence = engine.ScanTimings("C.A").Records[0].Sequence;
        await Until(() => engine.ScanTimings("C.A").Records[0].Sequence >= sequence + 4);
        Assert.All(engine.ScanTimings("C.A").Records.Take(3), batch =>
        {
            Assert.Equal("good", batch.Status);
            Assert.Null(batch.Network);
        });
        Assert.Null(engine.ScanTimings("C.A").Devices[0].NetworkP95RttMs);
        Assert.Null(engine.ScanTimings("C.A").Devices[0].NetworkLossPercent);
        await overlap.GetStream().WriteAsync(new byte[] { 8 }, timeout.Token);
        await overlap.GetStream().ReadExactlyAsync(new byte[1], timeout.Token);
        overlap.Dispose();
        await Until(() => engine.ScanTimings("C.A").Records[0].Network is { LatestRttMs: 11, RouteVerified: true });
        Assert.Equal(1, factory.Created);
    }

    [Fact]
    public async Task Logical_devices_share_resolved_remote_with_unverified_system_route_and_release_last_lease()
    {
        var echo = new PendingEcho();
        using var coordinator = new NetworkPerformanceProbeCoordinator(echo);
        var factory = new Factory();
        var registry = new DriverRegistry();
        registry.Register(factory);
        int dnsCalls = 0;
        await using var engine = new RuntimeEngine(RedundantDirectProject("plc.example", "A", "B"), registry, new DataHub(),
            NullLoggerFactory.Instance, networkAdapters: new Catalog(), networkProbes: coordinator,
            resolveNetworkHost: (_, _) => { Interlocked.Increment(ref dnsCalls); return Task.FromResult(new[] { IPAddress.Parse("192.0.2.10") }); });
        await engine.StartAsync();
        using var demand = engine.ScanDemands.Acquire("mes", ["C.A.Value", "C.B.Value"], 30);
        await Until(() => echo.Calls.Count == 1 && engine.ScanTimings("C.A").Records.Count > 0 && engine.ScanTimings("C.B").Records.Count > 0);
        var call = Assert.Single(echo.Calls);
        Assert.Equal("192.0.2.10", call.Remote);
        Assert.Null(call.Source); // A direct fake driver does not prove a bound source address.
        Assert.Equal(1, dnsCalls);
        Assert.Equal(1, factory.Created); // Matching endpoint/configuration shares one physical OPC UA session.

        await engine.ApplyAsync(RedundantDirectProject("plc.example", "B"));
        Assert.Equal(0, echo.Cancelled);
        Assert.Single(echo.Calls);
        await engine.ApplyAsync(RedundantDirectProject("192.0.2.11", "B"));
        await Until(() => echo.Calls.Count == 2);
        Assert.Equal(1, echo.Cancelled);
        Assert.Equal("192.0.2.11", echo.Calls.Last().Remote);
        await engine.StopAsync();
        Assert.Equal(2, echo.Cancelled);
        // An injected host coordinator remains usable after this runtime's leases have ended.
        using var retained = coordinator.Acquire(new("192.0.2.12", "127.0.0.2", true));
        await Until(() => echo.Calls.Count == 3);
    }

    [Fact]
    public async Task Pending_DNS_does_not_delay_collection_and_stop_cancels_initialization()
    {
        var echo = new PendingEcho();
        using var coordinator = new NetworkPerformanceProbeCoordinator(echo);
        var registry = new DriverRegistry();
        registry.Register(new Factory());
        var resolving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = new RuntimeEngine(DirectProject("pending.example", "A"), registry, new DataHub(),
            NullLoggerFactory.Instance, networkAdapters: new Catalog(), networkProbes: coordinator,
            resolveNetworkHost: async (_, ct) =>
            {
                resolving.SetResult();
                try { await Task.Delay(Timeout.Infinite, ct); }
                finally { cancelled.SetResult(); }
                return [];
            });
        await engine.StartAsync().WaitAsync(TimeSpan.FromSeconds(2));
        using var demand = engine.ScanDemands.Acquire("mes", ["C.A.Value"], 30);
        await resolving.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Until(() => engine.ScanTimings("C.A").Records.Count >= 2);
        Assert.Null(engine.ScanTimings("C.A").Records[0].Network);
        await engine.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(echo.Calls);
    }

    [Fact]
    public async Task Unresolved_host_and_throwing_probe_leave_good_scans_running()
    {
        foreach (bool dnsFails in new[] { true, false })
        {
            using var coordinator = new NetworkPerformanceProbeCoordinator(new ThrowingEcho());
            var registry = new DriverRegistry();
            registry.Register(new Factory());
            await using var engine = new RuntimeEngine(DirectProject("plc.example", "A"), registry, new DataHub(),
                NullLoggerFactory.Instance, networkAdapters: new Catalog(), networkProbes: coordinator,
                resolveNetworkHost: (_, _) => dnsFails ? Task.FromException<IPAddress[]>(new Exception("DNS failed"))
                    : Task.FromResult(new[] { IPAddress.Parse("192.0.2.10") }));
            await engine.StartAsync();
            using var demand = engine.ScanDemands.Acquire("mes", ["C.A.Value"], 30);
            await Until(() => engine.ScanTimings("C.A").Records.Count >= 3);
            Assert.All(engine.ScanTimings("C.A").Records, record => Assert.Equal("good", record.Status));
            var latest = engine.ScanTimings("C.A").Records[0];
            Assert.Equal("insufficient-evidence", latest.Attribution!.Code);
            if (dnsFails) Assert.Null(latest.Network);
            else Assert.Equal("unavailable", latest.Network!.Status);
        }
    }

    [Fact]
    public async Task Batch_reads_one_cached_window_at_completion_and_summary_queries_do_not_read_driver()
    {
        await using var hub = new DataHub();
        var driver = new Driver();
        var binding = Binding();
        hub.Register(binding.TagId, binding.Def);
        hub.Touch(binding.TagId);
        using var stop = new CancellationTokenSource();
        var windows = new ConcurrentQueue<DateTimeOffset>();
        var loop = new DeviceLoop(driver, [binding], hub, NullLogger.Instance, "C.A", scanMode: "all",
            networkWindow: at =>
            {
                windows.Enqueue(at);
                return windows.Count == 1 ? Window(at) with { P95RttMs = 77, LossPercent = 50, SuccessCount = 3, FailedCount = 3 }
                    : Window(at);
            });
        loop.Start(stop.Token);
        await Until(() => loop.ScanHistory(200, null).Count >= 3);
        stop.Cancel();
        await loop.Completion;
        var records = loop.ScanHistory(200, null);
        Assert.Equal(records.Count, windows.Count);
        foreach (var record in records)
        {
            Assert.Equal(record.StartedAt.AddMilliseconds(record.TotalMs), record.Network!.To);
            Assert.Equal(TimeSpan.FromSeconds(30), record.Network.To - record.Network.From);
            Assert.Equal(6, record.Network.Count);
        }
        Assert.DoesNotContain(records.Last().Attribution!.Evidence, evidence => evidence.StartsWith("protocol-baseline-p95="));
        Assert.Contains(records[0].Attribution!.Evidence, evidence => evidence.StartsWith("protocol-baseline-p95="));
        int reads = driver.Reads;
        int probes = windows.Count;
        for (int i = 0; i < 20; i++)
        {
            var summary = loop.ScanTimingSummary();
            Assert.Equal(7, summary.QueueP95Ms);
            Assert.Equal(4, summary.NetworkP95RttMs);
            Assert.Equal(0, summary.NetworkLossPercent);
            Assert.Equal(records[0].Attribution!.Label, summary.LatestAttribution);
            Assert.Equal(records[0].Attribution!.Confidence, summary.LatestAttributionConfidence);
        }
        Assert.Equal(reads, driver.Reads);
        Assert.Equal(probes, windows.Count);
    }

    [Fact]
    public void History_uses_only_prior_bounded_protocol_baseline_and_per_batch_retry_evidence()
    {
        var history = new ScanTimingHistory();
        var block = new CompiledScanBlock
        {
            Id = 1, PeriodMs = 100, Area = 0, Start = 0, Count = 1, Priority = ScanDemandPriority.Background,
            Tags = [Binding()], Handles = [], HubHandles = [],
        };
        var now = DateTimeOffset.UtcNow;
        history.Record(block, now, 0, 10, 10, 1, 0, null, 0, new(1, 0, 0, 10, 10, 0));
        var slow = history.Record(block, now, 100, 200, 200, 1, 0, null, 0, new(1, 0, 0, 100, 100, 0),
            network: Window(now), sameSessionRetries: 1);
        Assert.Equal("plc-opc-server-suspected", slow.Attribution!.Code);
        Assert.Contains("protocol-baseline-p95=10 ms", slow.Attribution.Evidence);
        Assert.Contains("request-split-or-retry", slow.Attribution.Factors);
        for (int i = 0; i < 200; i++)
            history.Record(block, now, 300, 320, 320, 1, 0, null, 0, new(1, 0, 0, 20, 20, 0));
        var clean = history.Record(block, now, 400, 500, 500, 1, 0, null, 0, new(1, 0, 0, 100, 100, 0), network: Window(now));
        Assert.Contains("protocol-baseline-p95=20 ms", clean.Attribution!.Evidence);
        Assert.DoesNotContain("request-split-or-retry", clean.Attribution.Factors);
        Assert.Equal(200, history.Snapshot(999, null).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Throwing_snapshot_is_unavailable_and_retry_evidence_does_not_leak_to_next_batch(bool downshift)
    {
        await using var hub = new DataHub();
        var binding = Binding();
        hub.Register(binding.TagId, binding.Def);
        hub.Touch(binding.TagId);
        using var stop = new CancellationTokenSource();
        var loop = new DeviceLoop(new Driver(failFirst: true, downshift: downshift), [binding], hub, NullLogger.Instance, "C.A",
            scanMode: "all", networkWindow: _ => throw new InvalidOperationException("snapshot failed"));
        loop.Start(stop.Token);
        await Until(() => loop.ScanHistory(200, null).Count >= 3);
        stop.Cancel();
        await loop.Completion;
        var records = loop.ScanHistory(200, null);
        Assert.Contains(downshift ? "same-session-retries=0; batch-downshifts=1" : "same-session-retries=1; batch-downshifts=0", records.Last().Attribution!.Evidence);
        Assert.Equal(downshift ? 0 : 1, records.Last().SameSessionRetries);
        Assert.Equal(downshift ? 1 : 0, records.Last().BatchDownshifts);
        Assert.Equal("good", records[0].Status);
        Assert.Null(records[0].Network);
        Assert.DoesNotContain("request-split-or-retry", records[0].Attribution!.Factors);
    }

    [Fact]
    public async Task Runtime_uses_an_explicit_trailing_thirty_second_window_with_at_least_six_echoes()
    {
        // Short cadence is injected only to populate a real coordinator window without a 30-second test.
        using var coordinator = new NetworkPerformanceProbeCoordinator(new SuccessfulEcho(), cadence: TimeSpan.FromMilliseconds(10));
        var registry = new DriverRegistry();
        registry.Register(new Factory());
        await using var engine = new RuntimeEngine(DirectProject("192.0.2.10", "A"), registry, new DataHub(),
            NullLoggerFactory.Instance, networkAdapters: new Catalog(), networkProbes: coordinator);
        await engine.StartAsync();
        using var demand = engine.ScanDemands.Acquire("mes", ["C.A.Value"], 30);
        await Until(() => engine.ScanTimings("C.A").Records.FirstOrDefault()?.Network is { Count: >= 6 });
        var record = engine.ScanTimings("C.A").Records[0];
        Assert.Equal(record.StartedAt.AddMilliseconds(record.TotalMs), record.Network!.To);
        Assert.Equal(TimeSpan.FromSeconds(30), record.Network.To - record.Network.From);
        Assert.Equal(4, record.Network.P95RttMs);
        Assert.Equal(0, record.Network.LossPercent);
        Assert.False(record.Network.RouteVerified);
    }

    [Fact]
    public async Task Latest_batch_without_network_does_not_reuse_previous_healthy_summary()
    {
        await using var hub = new DataHub();
        var binding = Binding();
        hub.Register(binding.TagId, binding.Def);
        hub.Touch(binding.TagId);
        using var stop = new CancellationTokenSource();
        int snapshots = 0;
        var loop = new DeviceLoop(new Driver(), [binding], hub, NullLogger.Instance, "C.A", scanMode: "all",
            networkWindow: at => Interlocked.Increment(ref snapshots) == 1 ? Window(at) : null);
        try
        {
            loop.Start(stop.Token);
            await Until(() => loop.ScanHistory(200, null).Count >= 3);
            var records = loop.ScanHistory(200, null);
            Assert.NotNull(records.Last().Network);
            Assert.Null(records[0].Network);
            var summary = loop.ScanTimingSummary();
            Assert.Null(summary.NetworkP95RttMs);
            Assert.Null(summary.NetworkLossPercent);
            Assert.Equal("证据不足", summary.LatestAttribution);
        }
        finally { stop.Cancel(); await loop.Completion; }
    }

    [Fact]
    public async Task Empty_summary_preserves_absent_measurements_and_attribution()
    {
        await using var hub = new DataHub();
        var loop = new DeviceLoop(new Driver(), [], hub, NullLogger.Instance, "C.A");
        var summary = loop.ScanTimingSummary();
        Assert.Null(summary.QueueP95Ms);
        Assert.Null(summary.ProtocolRequestP95Ms);
        Assert.Null(summary.ProtocolRequestMaxMs);
        Assert.Null(summary.LocalP95Ms);
        Assert.Null(summary.NetworkP95RttMs);
        Assert.Null(summary.NetworkLossPercent);
        Assert.Null(summary.LatestAttribution);
        Assert.Null(summary.LatestAttributionConfidence);
    }

    private static NetworkProbeWindow Window(DateTimeOffset to) => new(to.AddSeconds(-30), to, 6, 6, 0, 3, 4, 4, 0, "healthy", null, true);
    private static TagBinding Binding() => new()
    {
        TagId = "C.A.Value", Address = new(0, 0, 2),
        Def = new() { Name = "Value", Address = "ns=2;s=Value", DataType = IgwDataType.Int16, ScanRateMs = 30 },
    };
    private static IgwProject Project(string remote, params string[] names) => new()
    {
        Channels = [new()
        {
            Name = "C", DriverId = "OpcUa.Client",
            Settings = { ["url"] = $"opc.tcp://{remote}:4840/server", ["networkAdapter"] = "PLC",
                ["networkAdapterId"] = "nic", ["networkAdapterIpv4"] = "127.0.0.1" },
            Devices = names.Select(name => new DeviceDef
            {
                Name = name, Settings = { ["scanMode"] = "请求所有数据" },
                Tags = [Binding().Def],
            }).ToList(),
        }],
    };
    private static IgwProject DirectProject(string remote, params string[] names)
    {
        var project = Project(remote, names);
        project.Channels[0].Settings["networkAdapter"] = "默认值";
        return project;
    }
    private static IgwProject RedundantDirectProject(string remote, params string[] names)
    {
        var project = DirectProject(remote, names);
        foreach (var device in project.Channels[0].Devices)
            device.Settings["secondaryEndpoint"] = project.Channels[0].Settings["url"];
        return project;
    }
    private static async Task Until(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (!predicate() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(predicate(), "Expected runtime diagnostic state within four seconds");
    }
    private sealed class Catalog : INetworkAdapterCatalog
    {
        public IReadOnlyList<NetworkAdapterInfo> Available() => [new("nic", "PLC", "127.0.0.2", 8)];
    }
    private sealed class PendingEcho : INetworkEchoClient
    {
        public ConcurrentQueue<(string Remote, string? Source)> Calls { get; } = new();
        public int Cancelled;
        public async Task<NetworkEchoResult> SendAsync(IPAddress remote, IPAddress? source, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls.Enqueue((remote.ToString(), source?.ToString()));
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            finally { Interlocked.Increment(ref Cancelled); }
            return new(true, 4, "success", null, true);
        }
    }
    private sealed class ThrowingEcho : INetworkEchoClient
    {
        public Task<NetworkEchoResult> SendAsync(IPAddress remote, IPAddress? source, TimeSpan timeout, CancellationToken cancellationToken) => throw new Exception("ICMP failed");
    }
    private sealed class SuccessfulEcho : INetworkEchoClient
    {
        public Task<NetworkEchoResult> SendAsync(IPAddress remote, IPAddress? source, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(new NetworkEchoResult(true, 4, "success", null, true));
    }
    private sealed class Factory : IDriverFactory
    {
        public int Created;
        public string DriverId => "OpcUa.Client";
        public DriverDescriptor Describe() => new() { DriverId = DriverId, DisplayName = DriverId };
        public IDeviceDriver CreateDevice(ChannelDef channel, DeviceDef device) { Created++; return new Driver(); }
    }
    private sealed class Driver(bool failFirst = false, bool downshift = false, short value = 42) : IDeviceDriver
    {
        public volatile bool TransportFailed;
        public int Reads;
        public string DriverId => "OpcUa.Client";
        public ConnectionState State { get; private set; }
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser { get; } = new Parser();
        public DriverLimits Limits { get; } = new() { MaxTagsPerRead = 512 };
        public Task ConnectAsync(CancellationToken ct) { State = ConnectionState.Connected; StateChanged?.Invoke(State); return Task.CompletedTask; }
        public Task DisconnectAsync() { State = ConnectionState.Disconnected; return Task.CompletedTask; }
        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
        {
            int read = Interlocked.Increment(ref Reads);
            ProtocolOperationTiming.AddQueue(7);
            return ProtocolOperationTiming.MeasureAsync<IReadOnlyList<TagSample>>(() =>
            {
                if (TransportFailed) throw new DriverOperationException("transport unavailable", DriverFailureScope.Transport,
                    DriverRetryDisposition.RebuildConnection, StatusCode.BadCommFailure);
                if (failFirst && read == 1) throw new DriverOperationException("retry", DriverFailureScope.Block,
                    downshift ? DriverRetryDisposition.ReduceBatchAndRetry : DriverRetryDisposition.RetrySameConnection, StatusCode.Bad);
                return Task.FromResult<IReadOnlyList<TagSample>>(tags.Select(tag => new TagSample(tag.TagId, DataValue.GoodAt(value, DateTime.UtcNow))).ToArray());
            });
        }
        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) => Task.FromResult<IReadOnlyList<WriteResult>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class PathFactory : IDriverFactory
    {
        public Dictionary<string, Driver> Drivers { get; } = new();
        public string DriverId => "OpcUa.Client";
        public DriverDescriptor Describe() => new() { DriverId = DriverId, DisplayName = DriverId };
        public IDeviceDriver CreateDevice(ChannelDef channel, DeviceDef device)
        {
            string host = new Uri(device.Endpoint).Host;
            return Drivers[host] = new Driver(value: host == "127.0.0.2" ? (short)22 : (short)11);
        }
    }
    private sealed class PathEcho : INetworkEchoClient
    {
        public ConcurrentQueue<(string Remote, string? Source)> Calls { get; } = new();
        public Task<NetworkEchoResult> SendAsync(IPAddress remote, IPAddress? source, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls.Enqueue((remote.ToString(), source?.ToString()));
            double rtt = remote.ToString() == "127.0.0.2" || source?.ToString() == "127.0.0.2" ? 22 : 11;
            return Task.FromResult(new NetworkEchoResult(true, rtt, "success", null, true));
        }
    }
    private sealed class ChangingCatalog : INetworkAdapterCatalog
    {
        public volatile string Address = "127.0.0.1";
        public IReadOnlyList<NetworkAdapterInfo> Available() => [new("nic", "PLC", Address, 8)];
    }
    private sealed class SocketFactory : IDriverFactory
    {
        public SocketDriver? Driver;
        public int Created;
        public string DriverId => "OpcUa.Client";
        public DriverDescriptor Describe() => new() { DriverId = DriverId, DisplayName = DriverId };
        public IDeviceDriver CreateDevice(ChannelDef channel, DeviceDef device) { Created++; return Driver = new SocketDriver(new Uri(device.Endpoint)); }
    }
    private sealed class SocketDriver(Uri endpoint) : IDeviceDriver
    {
        private TcpClient? _client;
        private int _reads;
        public volatile bool ReconnectOnNextRead;
        public int ReconnectRead;
        public string DriverId => "OpcUa.Client";
        public ConnectionState State { get; private set; }
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser { get; } = new Parser();
        public DriverLimits Limits { get; } = new() { MaxTagsPerRead = 512 };
        public async Task ConnectAsync(CancellationToken ct)
        {
            _client?.Dispose();
            _client = new TcpClient(AddressFamily.InterNetwork);
            await _client.ConnectAsync(endpoint.Host, endpoint.Port, ct);
            await RoundTrip(ct);
            State = ConnectionState.Connected;
            StateChanged?.Invoke(State);
        }
        public Task DisconnectAsync() { _client?.Dispose(); State = ConnectionState.Disconnected; return Task.CompletedTask; }
        public async Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
        {
            int read = Interlocked.Increment(ref _reads);
            if (ReconnectOnNextRead) { ReconnectRead = read; ReconnectOnNextRead = false; await ConnectAsync(ct); }
            await RoundTrip(ct);
            return tags.Select(tag => new TagSample(tag.TagId, DataValue.GoodAt((short)42, DateTime.UtcNow))).ToArray();
        }
        private async Task RoundTrip(CancellationToken ct)
        {
            await _client!.GetStream().WriteAsync(new byte[] { 42 }, ct);
            var response = new byte[1];
            await _client.GetStream().ReadExactlyAsync(response, ct);
        }
        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) => Task.FromResult<IReadOnlyList<WriteResult>>([]);
        public ValueTask DisposeAsync() { _client?.Dispose(); return ValueTask.CompletedTask; }
    }
    private sealed class LocalEchoServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentBag<Task> _connections = [];
        private readonly Task _accept;
        public ConcurrentQueue<IPAddress> Sources { get; } = new();
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public LocalEchoServer() { _listener.Start(); _accept = Accept(); }
        private async Task Accept()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    Sources.Enqueue(((IPEndPoint)client.Client.RemoteEndPoint!).Address);
                    _connections.Add(Echo(client));
                }
            }
            catch (OperationCanceledException) { }
        }
        private async Task Echo(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var buffer = new byte[32];
                    while (true)
                    {
                        int count = await client.GetStream().ReadAsync(buffer, _stop.Token);
                        if (count == 0) break;
                        await client.GetStream().WriteAsync(buffer.AsMemory(0, count), _stop.Token);
                    }
                }
                catch (Exception error) when (error is IOException or SocketException or OperationCanceledException) { }
            }
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            await _accept;
            await Task.WhenAll(_connections);
            _stop.Dispose();
        }
    }
    private sealed class Parser : IAddressParser
    {
        public bool TryParse(string address, IgwDataType type, out TagAddress result, out string? error)
        { result = new(0, 0, 2); error = null; return true; }
    }
}
