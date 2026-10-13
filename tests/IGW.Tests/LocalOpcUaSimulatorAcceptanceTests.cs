using System.Net;
using System.Net.Sockets;
using IGW.Abstractions;
using IGW.Core;
using IGW.Drivers.IoTClient;
using IGW.Drivers.OpcUaClient;
using IGW.Northbound.OpcUaServer;
using IGW.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class LocalOpcUaSimulatorAcceptanceTests
{
    [Fact]
    public async Task Direct_opcua_write_roundtrips_through_the_local_plc_simulator()
    {
        int modbusPort = FreePort();
        int opcUaPort = FreePort();
        string tagId = "SIM.ModbusPLC.DB.SetPoint";
        string temp = Path.Combine(Path.GetTempPath(), "igw-local-opcua-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        using var simulator = new ModbusSimulator(NullLogger.Instance, modbusPort);
        simulator.Start();
        var project = new IgwProject
        {
            Name = "Local OPC UA acceptance",
            OpcUa = new OpcUaSettings
            {
                Enabled = true,
                Port = opcUaPort,
                AllowAnonymous = true,
                AllowEncryption = false,
            },
            Channels =
            {
                new ChannelDef
                {
                    Name = "SIM",
                    DriverId = IoTModbusDriverFactory.Id,
                    Settings = { ["timeoutMs"] = "1000" },
                    Devices =
                    {
                        new DeviceDef
                        {
                            Name = "ModbusPLC",
                            Endpoint = $"127.0.0.1:{modbusPort}",
                            Settings =
                            {
                                ["unitId"] = "1",
                                ["zeroBased"] = "禁用",
                                ["scanMode"] = "固定扫描全部标记",
                            },
                            Groups =
                            {
                                new TagGroupDef
                                {
                                    Name = "DB",
                                    Tags =
                                    {
                                        new TagDef
                                        {
                                            Name = "SetPoint",
                                            Address = "40010",
                                            DataType = IgwDataType.Int16,
                                            Access = TagAccess.ReadWrite,
                                            ScanRateMs = 20,
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            },
        };
        var registry = new DriverRegistry();
        registry.Register(new IoTModbusDriverFactory());
        using var loggerFactory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Debug)
            .AddSimpleConsole(options => options.SingleLine = true));
        await using var hub = new DataHub();
        await using var engine = new RuntimeEngine(project, registry, hub, loggerFactory);
        await using var server = new OpcUaServerHost();
        try
        {
            await engine.StartAsync();
            engine.Touch(tagId);
            using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while ((!hub.TryGet(tagId, out var current) || current.Quality.IsBad) && !ready.IsCancellationRequested)
                await Task.Delay(20, ready.Token);

            await server.StartAsync(
                project,
                hub,
                async (id, value) => (await engine.WriteAsync(id, value, CancellationToken.None)).IsGood,
                (id, slice, value) => engine.WriteArrayAsync(id, slice, value, CancellationToken.None),
                NullLogger.Instance,
                Path.Combine(temp, "pki"));

            await using var client = new OpcUaClientDeviceDriver(
                $"opc.tcp://127.0.0.1:{opcUaPort}", 5_000, true, "", "", "None");
            await client.ConnectAsync(ready.Token);
            var definition = new TagDef
            {
                Name = "SetPoint",
                Address = $"ns=2;s={tagId}",
                DataType = IgwDataType.Int16,
                Access = TagAccess.ReadWrite,
            };
            var binding = new TagBinding
            {
                TagId = tagId,
                Def = definition,
                Address = new TagAddress(0, 0, 2),
            };

            using var protocolTiming = ProtocolOperationTiming.Begin();
            var write = Assert.Single(await client.WriteAsync([
                new WriteRequest { TagId = tagId, Def = definition, Address = binding.Address, Value = (short)321 },
            ], ready.Token));
            var read = Assert.Single(await client.ReadAsync([binding], ready.Token));

            Assert.Equal(StatusCode.Good, write.Status);
            Assert.Equal(StatusCode.Good, read.Value.Quality);
            Assert.Equal((short)321, Assert.IsType<short>(read.Value.Value));
            Assert.Equal(ConnectionState.Connected, client.State);
            var protocol = protocolTiming.Snapshot();
            // RegisterNodes 一次 + Write + Read；读取复用写入时注册的句柄，不再重复注册。
            Assert.Equal(3, protocol.Requests);
            Assert.Equal(0, protocol.FailedRequests);
            Assert.Equal(0, protocol.CancelledRequests);
            Assert.True(protocol.RequestTotalMs >= protocol.RequestMaxMs);
            Assert.True(protocol.RequestMaxMs > 0);

            // 后续读取只剩读请求本身，并且通过句柄仍然读到正确的值。
            using var again = ProtocolOperationTiming.Begin();
            var reread = Assert.Single(await client.ReadAsync([binding], ready.Token));
            Assert.Equal((short)321, Assert.IsType<short>(reread.Value.Value));
            Assert.Equal(1, again.Snapshot().Requests);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
