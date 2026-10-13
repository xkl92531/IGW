using System.Net;
using System.Net.Sockets;
using IGW.Abstractions;
using IGW.Core;
using IGW.Drivers.OpcUaClient;
using IGW.Northbound.OpcUaServer;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

/// <summary>工程局部修改后只同步受影响的 OPC UA 节点：新增通道/点位可读，改名/删除后的旧节点消失。</summary>
public sealed class OpcUaAddressSpaceSyncTests
{
    [Fact]
    public async Task New_and_renamed_channels_are_synced_into_the_running_address_space()
    {
        int port = FreePort();
        string temp = Path.Combine(Path.GetTempPath(), "igw-opcua-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var project = Project(port, ("LineA", "PLC1", "Speed"));
        await using var hub = new DataHub();
        Register(hub, "LineA.PLC1.Speed", 11.0);
        await using var server = new OpcUaServerHost();
        try
        {
            await server.StartAsync(project, hub, (_, _) => Task.FromResult(true),
                (_, _, _) => Task.FromResult(new ArrayWriteResult(StatusCode.Good, 0, [])), NullLogger.Instance, Path.Combine(temp, "pki"));

            // 新增通道 LineB，并把 LineA 改名为 LineA2(旧通道文件夹和点位应消失)。
            var next = Project(port, ("LineA2", "PLC1", "Speed"), ("LineB", "PLC9", "Pressure"));
            Register(hub, "LineA2.PLC1.Speed", 12.0);
            Register(hub, "LineB.PLC9.Pressure", 3.5);
            bool synced = server.SyncDevices(next,
                new HashSet<string>(["LineA2.PLC1", "LineB.PLC9"], StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(["LineA.PLC1"], StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(["LineA"], StringComparer.Ordinal));
            Assert.True(synced);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using var client = new OpcUaClientDeviceDriver($"opc.tcp://127.0.0.1:{port}", 5_000, true, "", "", "None");
            await client.ConnectAsync(timeout.Token);
            var results = await client.ReadAsync(
                [Binding("LineB.PLC9.Pressure"), Binding("LineA2.PLC1.Speed"), Binding("LineA.PLC1.Speed")], timeout.Token);

            Assert.Equal(3.5, Assert.IsType<double>(results[0].Value.Value));
            Assert.Equal(12.0, Assert.IsType<double>(results[1].Value.Value));
            Assert.True(results[2].Value.Quality.IsBad);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// 删除一个点很多的设备要在几秒内完成。SDK 的 DeleteNode 逐个把子节点从父节点子列表头部摘掉，
    /// 大文件夹是 O(n²)：现场删一个百万点通道，保存接口几十分钟不返回，刷新后通道还在。
    /// </summary>
    [Fact]
    public async Task Removing_a_device_with_a_huge_folder_is_fast_and_leaves_other_channels_readable()
    {
        const int points = 400_000;
        int port = FreePort();
        string temp = Path.Combine(Path.GetTempPath(), "igw-opcua-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        await using var hub = new DataHub();
        var big = new DeviceDef
        {
            Name = "PLC",
            Groups = [new TagGroupDef { Name = "Points", Tags = Enumerable.Range(0, points)
                .Select(i => new TagDef { Name = $"V{i}", Address = "R0", DataType = IgwDataType.Double }).ToList() }],
        };
        for (int i = 0; i < points; i++) Register(hub, $"Big.PLC.Points.V{i}", i);
        Register(hub, "Keep.PLC1.Speed", 7.0);
        var project = Project(port, ("Keep", "PLC1", "Speed"));
        project.Channels.Add(new ChannelDef { Name = "Big", DriverId = "Simulator", Devices = [big] });
        await using var server = new OpcUaServerHost();
        try
        {
            await server.StartAsync(project, hub, (_, _) => Task.FromResult(true),
                (_, _, _) => Task.FromResult(new ArrayWriteResult(StatusCode.Good, 0, [])), NullLogger.Instance, Path.Combine(temp, "pki"));

            var watch = System.Diagnostics.Stopwatch.StartNew();
            bool synced = server.SyncDevices(Project(port, ("Keep", "PLC1", "Speed")),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(["Big.PLC"], StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(["Big"], StringComparer.Ordinal));
            watch.Stop();

            Assert.True(synced);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"删除 {points} 点设备耗时 {watch.Elapsed.TotalSeconds:F1}s");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using var client = new OpcUaClientDeviceDriver($"opc.tcp://127.0.0.1:{port}", 5_000, true, "", "", "None");
            await client.ConnectAsync(timeout.Token);
            var results = await client.ReadAsync([Binding("Keep.PLC1.Speed"), Binding("Big.PLC.Points.V123")], timeout.Token);
            Assert.Equal(7.0, Assert.IsType<double>(results[0].Value.Value));
            Assert.True(results[1].Value.Quality.IsBad);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    private static void Register(DataHub hub, string tagId, double value)
    {
        hub.Register(tagId, new TagDef { Name = tagId[(tagId.LastIndexOf('.') + 1)..], DataType = IgwDataType.Double });
        hub.Update(tagId, new DataValue(value, StatusCode.Good, DateTime.UtcNow, DateTime.UtcNow));
    }

    private static TagBinding Binding(string tagId) => new()
    {
        TagId = tagId,
        Def = new TagDef { Name = tagId, Address = $"ns=2;s={tagId}", DataType = IgwDataType.Double },
        Address = new TagAddress(0, 0, 8),
    };

    private static IgwProject Project(int port, params (string Channel, string Device, string Tag)[] rows) => new()
    {
        OpcUa = new OpcUaSettings { Enabled = true, Port = port, AllowAnonymous = true, AllowEncryption = false },
        Channels = rows.Select(row => new ChannelDef
        {
            Name = row.Channel,
            DriverId = "Simulator",
            Devices = [new DeviceDef { Name = row.Device, Tags = [new TagDef { Name = row.Tag, Address = "R0", DataType = IgwDataType.Double }] }],
        }).ToList(),
    };

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
