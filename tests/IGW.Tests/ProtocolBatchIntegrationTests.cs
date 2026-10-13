using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using IGW.Abstractions;
using IGW.Drivers.IoTClient;
using IGW.Drivers.Mitsubishi;
using IGW.Runtime;
using IoTClient.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class ProtocolBatchIntegrationTests
{
    [Fact]
    public async Task Modbus_contiguous_batch_can_be_written_and_read_back_without_disconnect()
    {
        int port = FreePort();
        using var simulator = new ModbusSimulator(NullLogger.Instance, port);
        simulator.Start();
        await using var driver = new IoTModbusDeviceDriver(
            "127.0.0.1", port, 1, EndianFormat.ABCD, "ABCD", 2_000, zeroBased: false);
        await driver.ConnectAsync(CancellationToken.None);
        var first = ModbusBinding("Line.Plc.DB.A", "40010");
        var second = ModbusBinding("Line.Plc.DB.B", "40011");

        var written = await driver.WriteAsync([
            Request(first, (short)123), Request(second, (short)456)], CancellationToken.None);
        using var timing = ProtocolOperationTiming.Begin();
        var read = await driver.ReadAsync([first, second], CancellationToken.None);
        Assert.Equal(1, timing.Snapshot().PhysicalRequests);
        Assert.Equal(2, Assert.Single(timing.Snapshot().Samples!).Descriptor!.TagCount);
        Assert.True(driver.Capabilities.PartitionsReadRequests);

        Assert.All(written, result => Assert.True(result.Status.IsGood));
        Assert.Equal(new short[] { 123, 456 }, read.Select(sample => Convert.ToInt16(sample.Value.Value)));
        Assert.Equal(ConnectionState.Connected, driver.State);
    }

    [Fact]
    public async Task Mc_contiguous_words_are_sent_in_one_native_frame()
    {
        await using var server = new McTestServer(closeWithoutResponse: false);
        await server.StartAsync();
        await using var driver = new McTcpDeviceDriver("127.0.0.1", server.Port, 2_000);
        await driver.ConnectAsync(CancellationToken.None);
        var first = McBinding("Line.Plc.DB.A", "D100", 100);
        var second = McBinding("Line.Plc.DB.B", "D101", 101);

        using var timing = ProtocolOperationTiming.Begin();
        var results = await driver.WriteAsync([
            Request(first, (short)12), Request(second, (short)34)], CancellationToken.None);

        Assert.All(results, result => Assert.True(result.Status.IsGood));
        Assert.Equal(1, server.Requests);
        Assert.Equal(1, timing.Snapshot().PhysicalRequests);
        Assert.Equal(ConnectionState.Connected, driver.State);
    }

    [Fact]
    public async Task Mc_transport_close_is_thrown_instead_of_being_reported_as_a_point_error()
    {
        await using var server = new McTestServer(closeWithoutResponse: true);
        await server.StartAsync();
        await using var driver = new McTcpDeviceDriver("127.0.0.1", server.Port, 2_000);
        await driver.ConnectAsync(CancellationToken.None);
        var binding = McBinding("Line.Plc.DB.A", "D100", 100);

        await Assert.ThrowsAnyAsync<IOException>(() =>
            driver.WriteAsync([Request(binding, (short)12)], CancellationToken.None));
    }

    private static TagBinding ModbusBinding(string id, string address)
    {
        var definition = new TagDef { Name = id.Split('.').Last(), Address = address,
            DataType = IgwDataType.Int16, Access = TagAccess.ReadWrite };
        Assert.True(new IoTModbusAddressParser().TryParse(address, definition.DataType, out var parsed, out _));
        return new TagBinding { TagId = id, Def = definition, Address = parsed };
    }

    private static TagBinding McBinding(string id, string address, int offset)
    {
        var definition = new TagDef { Name = id.Split('.').Last(), Address = address,
            DataType = IgwDataType.Int16, Access = TagAccess.ReadWrite };
        return new TagBinding { TagId = id, Def = definition, Address = new TagAddress(0, offset, 1) };
    }

    private static WriteRequest Request(TagBinding binding, object value) => new()
    { TagId = binding.TagId, Def = binding.Def, Address = binding.Address, Value = value };

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class McTestServer(bool closeWithoutResponse) : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private Task? _run;
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public int Requests { get; private set; }

        public Task StartAsync()
        {
            _listener.Start();
            _run = Task.Run(RunAsync);
            return Task.CompletedTask;
        }

        private async Task RunAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                using var stream = client.GetStream();
                byte[] header = await ReadExactAsync(stream, 9, _stop.Token);
                int length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(7));
                _ = await ReadExactAsync(stream, length, _stop.Token);
                Requests++;
                if (closeWithoutResponse) return;
                byte[] response = [0xD0, 0x00, 0x00, 0xFF, 0xFF, 0x03, 0x00, 0x02, 0x00, 0x00, 0x00];
                await stream.WriteAsync(response, _stop.Token);
            }
            catch (OperationCanceledException) { }
        }

        private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int length, CancellationToken ct)
        {
            var buffer = new byte[length];
            int read = 0;
            while (read < length)
            {
                int current = await stream.ReadAsync(buffer.AsMemory(read, length - read), ct);
                if (current == 0) throw new IOException("client closed");
                read += current;
            }
            return buffer;
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            if (_run is not null) try { await _run; } catch { }
            _stop.Dispose();
        }
    }
}
