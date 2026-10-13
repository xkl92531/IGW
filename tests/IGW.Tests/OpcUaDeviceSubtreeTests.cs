using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using IGW.Abstractions;
using IGW.Core;
using IGW.Drivers.OpcUaClient;
using IGW.Northbound.OpcUaServer;
using Microsoft.Extensions.Logging.Abstractions;
using Opc.Ua;
using Xunit;
using DataValue = IGW.Abstractions.DataValue;
using StatusCode = IGW.Abstractions.StatusCode;

namespace IGW.Tests;

/// <summary>运行中的 OPC UA 服务只替换单个设备子树:其它设备的会话/订阅不受影响。</summary>
public sealed class OpcUaDeviceSubtreeTests
{
    [Fact]
    public async Task Replacing_one_device_keeps_peer_subscriptions_and_exposes_new_tags()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var devA = new DeviceDef { Name = "A", Tags = [Tag("x"), Tag("w")] };
        var devB = new DeviceDef { Name = "B", Tags = [Tag("y")] };
        var channel = new ChannelDef { Name = "C", Devices = [devA, devB] };
        await using var fx = await Fixture.StartAsync(new[] { channel });
        fx.Set("C.A.x", 1); fx.Set("C.A.w", 2); fx.Set("C.B.y", 3);

        await using var client = fx.CreateClient();
        await client.ConnectAsync(timeout.Token);
        var yValues = new ConcurrentQueue<short>();
        var xValues = new ConcurrentQueue<short>();
        await using var ySub = await client.SubscribeAsync([Binding("C.B.y")], s => Record(s, yValues), timeout.Token);
        await using var xSub = await client.SubscribeAsync([Binding("C.A.x")], s => Record(s, xValues), timeout.Token);
        fx.Set("C.B.y", 10);
        fx.Set("C.A.x", 20);
        await EventuallyAsync(() => yValues.Contains((short)10) && xValues.Contains((short)20), timeout.Token);

        // A' = x + z + 组 G/t;w 被移除
        var devA2 = new DeviceDef
        {
            Name = "A",
            Tags = [Tag("x"), Tag("z")],
            Groups = [new TagGroupDef { Name = "G", Tags = [Tag("t")] }],
        };
        fx.Hub.Register("C.A.z", devA2.Tags[1]);
        fx.Hub.Register("C.A.G.t", devA2.Groups[0].Tags[0]);
        fx.Set("C.A.z", 30); fx.Set("C.A.G.t", 31);
        fx.Server.ReplaceDevice(channel, devA2);

        var session = fx.Session(client);
        Assert.Equal(new[] { "G", "x", "z" }, await BrowseNamesAsync(session, "C.A", timeout.Token));
        Assert.Equal(new[] { "t" }, await BrowseNamesAsync(session, "C.A.G", timeout.Token));
        Assert.Equal(new[] { "A", "B" }, await BrowseNamesAsync(session, "C", timeout.Token));

        var read = await client.ReadAsync([Binding("C.A.x"), Binding("C.A.z"), Binding("C.A.G.t"), Binding("C.B.y")], timeout.Token);
        Assert.Equal(new short[] { 20, 30, 31, 10 }, read.Select(r => Assert.IsType<short>(r.Value.Value)).ToArray());
        Assert.All(read, r => Assert.True(r.Value.Quality.IsGood, r.Value.Quality.ToString()));

        // 旧订阅未中断:替换后 y 与保留下来的 x 仍持续收到推送
        fx.Set("C.B.y", 11);
        fx.Set("C.A.x", 21);
        await EventuallyAsync(() => yValues.Contains((short)11) && xValues.Contains((short)21), timeout.Token);

        // 新点可以订阅
        var zValues = new ConcurrentQueue<short>();
        await using var zSub = await client.SubscribeAsync([Binding("C.A.z")], s => Record(s, zValues), timeout.Token);
        fx.Set("C.A.z", 32);
        await EventuallyAsync(() => zValues.Contains((short)32), timeout.Token);

        // 被删除的点读不到了
        var gone = Assert.Single(await client.ReadAsync([Binding("C.A.w")], timeout.Token));
        Assert.True(gone.Value.Quality.IsBad);
    }

    [Fact]
    public async Task Replacing_a_device_under_a_new_channel_makes_it_browsable()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var channel = new ChannelDef { Name = "C", Devices = [new DeviceDef { Name = "B", Tags = [Tag("y")] }] };
        await using var fx = await Fixture.StartAsync(new[] { channel });
        fx.Set("C.B.y", 3);
        await using var client = fx.CreateClient();
        await client.ConnectAsync(timeout.Token);

        var devD = new DeviceDef { Name = "D", Tags = [Tag("q")] };
        var channelN = new ChannelDef { Name = "N", Devices = [devD] };
        fx.Hub.Register("N.D.q", devD.Tags[0]);
        fx.Set("N.D.q", 44);
        fx.Server.ReplaceDevice(channelN, devD);

        var session = fx.Session(client);
        var roots = await BrowseNamesAsync(session, ObjectIds.ObjectsFolder, timeout.Token);
        Assert.Contains("N", roots);
        Assert.Contains("C", roots);
        Assert.Equal(new[] { "D" }, await BrowseNamesAsync(session, "N", timeout.Token));
        Assert.Equal(new[] { "q" }, await BrowseNamesAsync(session, "N.D", timeout.Token));
        var read = Assert.Single(await client.ReadAsync([Binding("N.D.q")], timeout.Token));
        Assert.Equal((short)44, Assert.IsType<short>(read.Value.Value));

        // 同一新通道再替换一次(幂等,不重复挂 Objects 引用)
        fx.Server.ReplaceDevice(channelN, devD);
        var again = await BrowseNamesAsync(session, ObjectIds.ObjectsFolder, timeout.Token);
        Assert.Single(again, name => name == "N");
    }

    [Fact]
    public async Task Recreated_hub_entries_with_permuted_handles_keep_pushing_without_demand_leak()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var devA = new DeviceDef { Name = "A", Tags = [Tag("x"), Tag("y")] };
        var channel = new ChannelDef { Name = "C", Devices = [devA] };
        await using var fx = await Fixture.StartAsync(new[] { channel });
        fx.Set("C.A.x", 1); fx.Set("C.A.y", 2);
        Assert.True(fx.Hub.TryGetHandleId("C.A.x", out int hx0));
        Assert.True(fx.Hub.TryGetHandleId("C.A.y", out int hy0));
        await using var client = fx.CreateClient();
        await client.ConnectAsync(timeout.Token);
        // x 两个监控项、y 一个监控项
        var x1 = new ConcurrentQueue<short>(); var x2 = new ConcurrentQueue<short>(); var y1 = new ConcurrentQueue<short>();
        var subs = new List<IAsyncDisposable>
        {
            await client.SubscribeAsync([Binding("C.A.x")], s => Record(s, x1), timeout.Token),
            await client.SubscribeAsync([Binding("C.A.x")], s => Record(s, x2), timeout.Token),
            await client.SubscribeAsync([Binding("C.A.y")], s => Record(s, y1), timeout.Token),
        };
        fx.Set("C.A.x", 5); fx.Set("C.A.y", 6);
        await EventuallyAsync(() => x1.Contains((short)5) && x2.Contains((short)5) && y1.Contains((short)6), timeout.Token);

        // 引擎停设备超时回退:先全部注销再重注册,LIFO 复用槽位 → 句柄互换
        fx.Hub.Unregister("C.A.x");
        fx.Hub.Unregister("C.A.y");
        fx.Hub.Register("C.A.x", devA.Tags[0]);
        fx.Hub.Register("C.A.y", devA.Tags[1]);
        Assert.True(fx.Hub.TryGetHandleId("C.A.x", out int hx1));
        Assert.True(fx.Hub.TryGetHandleId("C.A.y", out int hy1));
        Assert.Equal(hy0, hx1);
        Assert.Equal(hx0, hy1);
        fx.Server.ReplaceDevice(channel, devA, new[] { "C.A.x", "C.A.y" });
        fx.Set("C.A.x", 50); fx.Set("C.A.y", 60);
        await EventuallyAsync(() => x1.Contains((short)50) && x2.Contains((short)50) && y1.Contains((short)60), timeout.Token);

        foreach (var sub in subs) await sub.DisposeAsync();
        await EventuallyAsync(() => !fx.Hub.HasDeviceDemand("C.A", 1), timeout.Token);
    }

    [Fact]
    public async Task Removed_tag_slot_reused_by_new_tag_pushes_and_releases_demand()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var devA = new DeviceDef { Name = "A", Tags = [Tag("w")] };
        var channel = new ChannelDef { Name = "C", Devices = [devA] };
        await using var fx = await Fixture.StartAsync(new[] { channel });
        fx.Set("C.A.w", 1);
        Assert.True(fx.Hub.TryGetHandleId("C.A.w", out int hw));
        await using var client = fx.CreateClient();
        await client.ConnectAsync(timeout.Token);
        var wStatus = new ConcurrentQueue<bool>();
        var wSub = await client.SubscribeAsync([Binding("C.A.w")], samples =>
        {
            foreach (var sample in samples) wStatus.Enqueue(sample.Value.Quality.IsGood);
        }, timeout.Token);
        await EventuallyAsync(() => wStatus.Contains(true), timeout.Token);

        // 引擎:先注销被移除的 w,再注册新点 z(LIFO → z 复用 w 的槽位)
        var devA2 = new DeviceDef { Name = "A", Tags = [Tag("z")] };
        fx.Hub.Unregister("C.A.w");
        fx.Hub.Register("C.A.z", devA2.Tags[0]);
        Assert.True(fx.Hub.TryGetHandleId("C.A.z", out int hz));
        Assert.Equal(hw, hz);
        fx.Server.ReplaceDevice(channel, devA2);

        // 被删节点上的旧监控项收到 Bad 通知
        await EventuallyAsync(() => wStatus.Contains(false), timeout.Token);

        var zValues = new ConcurrentQueue<short>();
        var zSub = await client.SubscribeAsync([Binding("C.A.z")], s => Record(s, zValues), timeout.Token);
        fx.Set("C.A.z", 7);
        fx.Set("C.A.z", 8);
        await EventuallyAsync(() => zValues.Contains((short)8), timeout.Token);

        await zSub.DisposeAsync();
        await wSub.DisposeAsync();
        await EventuallyAsync(() => !fx.Hub.HasDeviceDemand("C.A", 1), timeout.Token);
    }

    [Fact]
    public async Task Surviving_tag_type_change_publishes_the_new_typed_value()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var devA = new DeviceDef { Name = "A", Tags = [Tag("x")] };
        var channel = new ChannelDef { Name = "C", Devices = [devA] };
        await using var fx = await Fixture.StartAsync(new[] { channel });
        fx.Set("C.A.x", 3);
        await using var client = fx.CreateClient();
        await client.ConnectAsync(timeout.Token);
        var seen = new ConcurrentQueue<object?>();
        await using var sub = await client.SubscribeAsync([FloatBinding("C.A.x")], samples =>
        {
            foreach (var sample in samples) if (sample.Value.Quality.IsGood) seen.Enqueue(sample.Value.Value);
        }, timeout.Token);
        await EventuallyAsync(() => seen.Count > 0, timeout.Token);

        var floatX = new TagDef { Name = "x", Address = "40001", DataType = IgwDataType.Float, Access = TagAccess.ReadWrite };
        var devA2 = new DeviceDef { Name = "A", Tags = [floatX] };
        fx.Hub.Register("C.A.x", floatX);
        fx.Hub.Update("C.A.x", new DataValue(2.5f, StatusCode.Good, DateTime.UtcNow, DateTime.UtcNow));
        await EventuallyAsync(() => fx.Hub.TryGet("C.A.x", out var cur) && cur.Value is float, timeout.Token);
        fx.Server.ReplaceDevice(channel, devA2, new[] { "C.A.x" });

        var read = Assert.Single(await client.ReadAsync([FloatBinding("C.A.x")], timeout.Token));
        Assert.True(read.Value.Quality.IsGood, read.Value.Quality.ToString());
        Assert.Equal(2.5f, Assert.IsType<float>(read.Value.Value));
        await EventuallyAsync(() => seen.Any(v => v is float f && f == 2.5f), timeout.Token);
    }

    [Fact]
    public async Task Reused_entry_type_change_never_serves_old_typed_value_as_good()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var devA = new DeviceDef { Name = "A", Tags = [Tag("x")] };
        var channel = new ChannelDef { Name = "C", Devices = [devA] };
        await using var fx = await Fixture.StartAsync(new[] { channel });
        fx.Set("C.A.x", 3);
        await using var client = fx.CreateClient();
        await client.ConnectAsync(timeout.Token);
        var samples = new ConcurrentQueue<(bool Good, object? Value)>();
        await using var sub = await client.SubscribeAsync([FloatBinding("C.A.x")], batch =>
        {
            foreach (var s in batch) samples.Enqueue((s.Value.Quality.IsGood, s.Value.Value));
        }, timeout.Token);
        await EventuallyAsync(() => samples.Any(s => s.Good && s.Value is short), timeout.Token);
        int mark = samples.Count;

        // 引擎正常路径:句柄复用,就地替换定义(Int16 → Float)
        var floatX = new TagDef { Name = "x", Address = "40001", DataType = IgwDataType.Float, Access = TagAccess.ReadWrite };
        Assert.True(fx.Hub.ReplaceDef("C.A.x", floatX));
        Assert.True(fx.Hub.TryGet("C.A.x", out var reset));
        Assert.Equal(StatusCode.BadWaitingForInitialData, reset.Quality);
        Assert.Null(reset.Value);
        fx.Server.ReplaceDevice(channel, new DeviceDef { Name = "A", Tags = [floatX] });

        var read = Assert.Single(await client.ReadAsync([FloatBinding("C.A.x")], timeout.Token));
        Assert.True(read.Value.Quality.IsBad, read.Value.Quality.ToString());

        fx.Hub.Update("C.A.x", new DataValue(2.5f, StatusCode.Good, DateTime.UtcNow, DateTime.UtcNow));
        await EventuallyAsync(() => samples.Skip(mark).Any(s => s.Good && s.Value is float f && f == 2.5f), timeout.Token);
        Assert.DoesNotContain(samples.Skip(mark), s => s.Good && s.Value is not float);
        var after = Assert.Single(await client.ReadAsync([FloatBinding("C.A.x")], timeout.Token));
        Assert.Equal(2.5f, Assert.IsType<float>(after.Value.Value));
    }

    [Fact]
    public void ReplaceDevice_is_a_noop_when_server_not_running()
    {
        var host = new OpcUaServerHost();
        var dev = new DeviceDef { Name = "A", Tags = [Tag("x")] };
        host.ReplaceDevice(new ChannelDef { Name = "C", Devices = [dev] }, dev);
    }

    private static TagDef Tag(string name) => new()
    {
        Name = name, Address = "40001", DataType = IgwDataType.Int16, Access = TagAccess.ReadWrite,
    };

    private static TagBinding Binding(string id) => new()
    {
        TagId = id,
        Def = new TagDef { Name = id, Address = $"ns=2;s={id}", DataType = IgwDataType.Int16, Access = TagAccess.ReadWrite },
        Address = new TagAddress(0, 0, 2),
    };

    private static TagBinding FloatBinding(string id) => new()
    {
        TagId = id,
        Def = new TagDef { Name = id, Address = $"ns=2;s={id}", DataType = IgwDataType.Float, Access = TagAccess.ReadWrite },
        Address = new TagAddress(0, 0, 2),
    };

    private static void Record(IReadOnlyList<TagSample> samples, ConcurrentQueue<short> target)
    {
        foreach (var sample in samples)
            if (sample.Value.Quality.IsGood && sample.Value.Value is short value) target.Enqueue(value);
    }

    private static Task<string[]> BrowseNamesAsync(Opc.Ua.Client.Session session, string id, CancellationToken ct) =>
        BrowseNamesAsync(session, new NodeId(id, 2), ct);

    private static async Task<string[]> BrowseNamesAsync(Opc.Ua.Client.Session session, NodeId node, CancellationToken ct)
    {
        var response = await session.BrowseAsync(null, null, 0, new BrowseDescriptionCollection
        {
            new BrowseDescription
            {
                NodeId = node,
                BrowseDirection = BrowseDirection.Forward,
                ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
                IncludeSubtypes = true,
                NodeClassMask = 0,
                ResultMask = (uint)BrowseResultMask.All,
            },
        }, ct);
        var result = Assert.Single(response.Results);
        Assert.True(Opc.Ua.StatusCode.IsGood(result.StatusCode), result.StatusCode.ToString());
        return result.References.Select(r => r.BrowseName.Name).Order(StringComparer.Ordinal).ToArray();
    }

    private static async Task EventuallyAsync(Func<bool> predicate, CancellationToken ct)
    {
        while (!predicate()) await Task.Delay(20, ct);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _temp = Path.Combine(Path.GetTempPath(), "igw-opcua-subtree-" + Guid.NewGuid().ToString("N"));
        private string _endpoint = "";
        public DataHub Hub { get; } = new();
        public OpcUaServerHost Server { get; } = new();

        public static async Task<Fixture> StartAsync(IEnumerable<ChannelDef> channels)
        {
            var fx = new Fixture();
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                fx._endpoint = $"opc.tcp://127.0.0.1:{port}";
                var project = new IgwProject
                {
                    Name = "Device subtree replacement",
                    OpcUa = new OpcUaSettings { Enabled = true, Port = port, AllowAnonymous = true, AllowEncryption = false },
                    Channels = channels.ToList(),
                };
                foreach (var ch in project.Channels)
                    foreach (var dev in ch.Devices)
                        foreach (var t in dev.Tags) fx.Hub.Register($"{ch.Name}.{dev.Name}.{t.Name}", t);
                await fx.Server.StartAsync(project, fx.Hub, (id, value) =>
                {
                    fx.Set(id, Convert.ToInt16(value));
                    return Task.FromResult(true);
                }, (_, _, _) => Task.FromResult(ArrayWriteResult.Failed(StatusCode.Bad, "unused")),
                    NullLogger.Instance, Path.Combine(fx._temp, "pki"));
                return fx;
            }
            catch { await fx.DisposeAsync(); throw; }
        }

        public void Set(string id, short value) => Hub.Update(id, new DataValue(value, StatusCode.Good, DateTime.UtcNow, DateTime.UtcNow));

        public OpcUaClientDeviceDriver CreateClient() => new(_endpoint, 5_000, true, "", "", "None");

        public Opc.Ua.Client.Session Session(OpcUaClientDeviceDriver client) => (Opc.Ua.Client.Session)typeof(OpcUaClientDeviceDriver)
            .GetField("_session", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(client)!;

        public async ValueTask DisposeAsync()
        {
            try { await Server.DisposeAsync(); }
            finally
            {
                await Hub.DisposeAsync();
                try { if (Directory.Exists(_temp)) Directory.Delete(_temp, recursive: true); } catch { }
            }
        }
    }
}
