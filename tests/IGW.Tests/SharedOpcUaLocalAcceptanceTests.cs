using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using IGW.Abstractions;
using IGW.Core;
using IGW.Drivers.OpcUaClient;
using IGW.Northbound.OpcUaServer;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace IGW.Tests;

public sealed class SharedOpcUaLocalAcceptanceTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Two_real_subscriptions_share_one_session_and_peer_survives_lease_disposal()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await using var fixture = await LocalServer.StartAsync(1);
        await using var first = fixture.Acquire();
        await using var second = fixture.Acquire();
        await Task.WhenAll(first.ConnectAsync(timeout.Token), second.ConnectAsync(timeout.Token));
        Assert.Equal(1, fixture.CreatedDrivers);
        Assert.Equal(1, fixture.Server.SessionCount);
        var firstValues = new ConcurrentQueue<short>();
        var secondValues = new ConcurrentQueue<short>();
        await using var firstSubscription = await first.SubscribeAsync([fixture.Bindings[0]],
            samples => Record(samples, firstValues), timeout.Token);
        await using var secondSubscription = await second.SubscribeAsync([fixture.Bindings[0]],
            samples => Record(samples, secondValues), timeout.Token);
        Assert.Single(firstSubscription.TagIds);
        Assert.Single(secondSubscription.TagIds);

        await WriteAndReadAsync(first, fixture.Bindings, 41, timeout.Token);
        await EventuallyAsync(() => firstValues.Contains((short)41) && secondValues.Contains((short)41), timeout.Token);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.ReadAsync(fixture.Bindings, cancelled.Token));
        }
        var missing = new TagBinding { TagId = "SIM.PLC.Missing", Address = new TagAddress(0, 0, 2),
            Def = new TagDef { Name = "Missing", Address = "ns=2;s=SIM.PLC.Missing", DataType = IgwDataType.Int16 } };
        var bad = Assert.Single(await first.ReadAsync([missing], timeout.Token));
        Assert.True(bad.Value.Quality.IsBad);
        Assert.Equal(1, fixture.Server.SessionCount);
        await first.DisposeAsync();
        int firstNotifications = firstValues.Count;
        Assert.Equal(ConnectionState.Disconnected, first.State);
        Assert.Equal(ConnectionState.Connected, second.State);
        Assert.Equal(1, fixture.Server.SessionCount);

        await WriteAndReadAsync(second, fixture.Bindings, 42, timeout.Token);
        await EventuallyAsync(() => secondValues.Contains((short)42), timeout.Token);
        Assert.Equal(firstNotifications, firstValues.Count);
        Assert.DoesNotContain((short)42, firstValues);
        await second.DisposeAsync();
        await EventuallyAsync(() => fixture.Server.SessionCount == 0, timeout.Token);
        output.WriteLine("Local SDK acceptance: leases=2; created drivers=1; active sessions=1; peer subscription survived first disposal; final sessions=0.");
    }

    [Fact]
    public async Task Four_real_clients_batch_write_and_read_without_losing_values_or_rebuilding_session()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await using var fixture = await LocalServer.StartAsync(32);
        var leases = Enumerable.Range(0, 4).Select(_ => fixture.Acquire()).ToArray();
        try
        {
            await Task.WhenAll(leases.Select(lease => lease.ConnectAsync(timeout.Token)));
            Assert.Equal(1, fixture.Server.SessionCount);
            var elapsed = Stopwatch.StartNew();
            await Task.WhenAll(leases.Select(async (lease, device) =>
            {
                var bindings = fixture.Bindings.Skip(device * 8).Take(8).ToArray();
                for (int iteration = 1; iteration <= 20; iteration++)
                    await WriteAndReadAsync(lease, bindings, (short)(device * 100 + iteration), timeout.Token);
            }));
            elapsed.Stop();
            Assert.Equal(640, fixture.AcceptedWrites);
            Assert.Equal(1, fixture.CreatedDrivers);
            Assert.Equal(1, fixture.Server.SessionCount);
            for (int device = 0; device < 4; device++)
                foreach (var binding in fixture.Bindings.Skip(device * 8).Take(8))
                {
                    Assert.True(fixture.Hub.TryGet(binding.TagId, out var value));
                    Assert.Equal((short)(device * 100 + 20), Assert.IsType<short>(value.Value));
                }
            output.WriteLine($"Local loopback SDK acceptance only: sessions=1; logical devices=4; batch width=8; write batches=80; read batches=80; acknowledged values=640; elapsed={elapsed.Elapsed.TotalMilliseconds:F1}ms. Not a field PLC throughput claim.");
        }
        finally
        {
            foreach (var lease in leases) await lease.DisposeAsync();
        }
        await EventuallyAsync(() => fixture.Server.SessionCount == 0, timeout.Token);
    }

    [Fact]
    public async Task Real_subscription_groups_periods_and_creates_monitored_items_in_small_batches()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await using var fixture = await LocalServer.StartAsync(32);
        await using var client = fixture.CreateDirectClient();
        await client.ConnectAsync(timeout.Token);
        // 把单次创建上限压到 5，强制走分批 CreateMonitoredItems；两种采样周期分成不同订阅。
        typeof(OpcUaClientDeviceDriver)
            .GetField("_maxMonitoredItemsPerCall", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(client, 5);
        var bindings = fixture.Bindings.Select((binding, index) => new TagBinding
        {
            TagId = binding.TagId, Def = binding.Def, Address = binding.Address,
            RequestedSamplingIntervalMs = index % 2 == 0 ? 50 : 500,
        }).ToArray();
        var received = new ConcurrentDictionary<string, short>(StringComparer.Ordinal);

        await using var subscription = await client.SubscribeAsync(bindings, samples =>
        {
            foreach (var sample in samples)
                if (sample.Value.Quality.IsGood && sample.Value.Value is short value) received[sample.TagId] = value;
        }, timeout.Token);

        Assert.Equal(32, subscription.TagIds.Count);
        var session = (Opc.Ua.Client.Session)typeof(OpcUaClientDeviceDriver)
            .GetField("_session", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(client)!;
        var subscriptions = session.Subscriptions.ToArray();
        Assert.Equal(2, subscriptions.Length);
        Assert.All(subscriptions, item => Assert.Equal(16, (int)item.MonitoredItemCount));
        Assert.Equal(new[] { 50, 500 }, subscriptions.Select(item => (int)item.PublishingInterval).Order().ToArray());

        var written = await client.WriteAsync(bindings.Select(binding => new WriteRequest
        {
            TagId = binding.TagId, Def = binding.Def, Address = binding.Address, Value = (short)77,
        }).ToArray(), timeout.Token);
        Assert.All(written, item => Assert.True(item.Status.IsGood, item.Status.ToString()));
        await EventuallyAsync(() => bindings.All(binding => received.TryGetValue(binding.TagId, out var value) && value == 77),
            timeout.Token);
    }

    private static void Record(IReadOnlyList<TagSample> samples, ConcurrentQueue<short> target)
    {
        foreach (var sample in samples)
            if (sample.Value.Quality.IsGood && sample.Value.Value is short value) target.Enqueue(value);
    }

    private static async Task WriteAndReadAsync(IDeviceDriver lease, IReadOnlyList<TagBinding> bindings,
        short value, CancellationToken ct)
    {
        var written = await lease.WriteAsync(bindings.Select(binding => new WriteRequest
        {
            TagId = binding.TagId, Def = binding.Def, Address = binding.Address, Value = value,
        }).ToArray(), ct);
        Assert.Equal(bindings.Count, written.Count);
        Assert.All(written, item => Assert.True(item.Status.IsGood, item.Status.ToString()));
        var read = await lease.ReadAsync(bindings, ct);
        Assert.Equal(bindings.Count, read.Count);
        Assert.Equal(bindings.Select(binding => binding.TagId).Order(), read.Select(sample => sample.TagId).Order());
        Assert.All(read, sample =>
        {
            Assert.True(sample.Value.Quality.IsGood, sample.Value.Quality.ToString());
            Assert.Equal(value, Assert.IsType<short>(sample.Value.Value));
        });
    }

    private static async Task EventuallyAsync(Func<bool> predicate, CancellationToken ct)
    {
        while (!predicate()) await Task.Delay(20, ct);
    }

    private sealed class LocalServer : IAsyncDisposable
    {
        private readonly OpcUaConnectionPool _pool = new();
        private readonly string _temp = Path.Combine(Path.GetTempPath(), "igw-shared-opcua-" + Guid.NewGuid().ToString("N"));
        private int _createdDrivers;
        private int _acceptedWrites;
        private string _endpoint = "";
        public DataHub Hub { get; } = new();
        public OpcUaServerHost Server { get; } = new();
        public TagBinding[] Bindings { get; private set; } = [];
        public int CreatedDrivers => Volatile.Read(ref _createdDrivers);
        public int AcceptedWrites => Volatile.Read(ref _acceptedWrites);

        public SharedOpcUaDriverLease Acquire() => _pool.Acquire(_endpoint, () =>
        {
            Interlocked.Increment(ref _createdDrivers);
            return new(new OpcUaClientDeviceDriver(_endpoint, 5_000, true, "", "", "None"), null);
        });

        public OpcUaClientDeviceDriver CreateDirectClient() => new(_endpoint, 5_000, true, "", "", "None");

        public static async Task<LocalServer> StartAsync(int count)
        {
            var fixture = new LocalServer();
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                fixture._endpoint = $"opc.tcp://127.0.0.1:{port}";
                var device = new DeviceDef { Name = "PLC" };
                fixture.Bindings = Enumerable.Range(0, count).Select(index =>
                {
                    string id = $"SIM.PLC.Value{index}";
                    var def = new TagDef { Name = $"Value{index}", Address = $"ns=2;s={id}",
                        DataType = IgwDataType.Int16, Access = TagAccess.ReadWrite, ScanRateMs = 20 };
                    device.Tags.Add(def);
                    fixture.Hub.Register(id, def);
                    fixture.Hub.Update(id, GoodValue(0));
                    return new TagBinding { TagId = id, Def = def, Address = new TagAddress(0, 0, 2) };
                }).ToArray();
                var project = new IgwProject { Name = "Local shared session acceptance",
                    OpcUa = new OpcUaSettings { Enabled = true, Port = port, AllowAnonymous = true, AllowEncryption = false },
                    Channels = [new ChannelDef { Name = "SIM", Devices = [device] }] };
                await fixture.Server.StartAsync(project, fixture.Hub, (id, value) =>
                {
                    fixture.Hub.Update(id, GoodValue(Convert.ToInt16(value)));
                    Interlocked.Increment(ref fixture._acceptedWrites);
                    return Task.FromResult(true);
                }, (_, _, _) => Task.FromResult(ArrayWriteResult.Failed(StatusCode.Bad, "Not used by this scalar acceptance test")),
                    NullLogger.Instance, Path.Combine(fixture._temp, "pki"));
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        private static DataValue GoodValue(short value) => new(value, StatusCode.Good, DateTime.UtcNow, DateTime.UtcNow);

        public async ValueTask DisposeAsync()
        {
            try { await _pool.DisposeAsync(); }
            finally
            {
                try { await Server.DisposeAsync(); }
                finally
                {
                    await Hub.DisposeAsync();
                    if (Directory.Exists(_temp)) Directory.Delete(_temp, recursive: true);
                }
            }
        }
    }
}
