using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using IGW.Abstractions;
using IGW.Drivers.IoTClient;
using Xunit;

namespace IGW.Tests;

public sealed class ModbusProtocolWireTests
{
    [Theory]
    [InlineData("auto", 1, 5)]
    [InlineData("multiple", 1, 15)]
    [InlineData("multiple", 9, 15)]
    [InlineData("single", 9, 5)]
    public async Task Coil_writes_have_exact_function_quantity_and_packed_bits(string mode, int count, int fc)
    {
        await using var server = new Server();
        await using var driver = Driver(server, new() { ["writeMode"] = mode, ["unitId"] = "255" });
        await driver.ConnectAsync(default);
        var writes = Enumerable.Range(0, count).Select(i => Request(Binding("C:" + (10 + i), IgwDataType.Bool), i % 2 == 0)).ToArray();
        Assert.All(await driver.WriteAsync(writes, default), result => Assert.True(result.Status.IsGood));
        var frames = server.Frames.ToArray();
        Assert.Equal(mode == "single" ? count : 1, frames.Length);
        Assert.All(frames, frame => { Assert.Equal(255, frame[6]); Assert.Equal(fc, frame[7]); });
        Assert.Equal(10, U16(frames[0], 8));
        if (fc == 15)
        {
            Assert.Equal(count, U16(frames[0], 10));
            Assert.Equal((count + 7) / 8, frames[0][12]);
            Assert.Equal(count == 1 ? new byte[] { 1 } : new byte[] { 0x55, 1 }, frames[0][13..]);
        }
        else Assert.Equal(0xff00, U16(frames[0], 10));
    }

    [Theory]
    [InlineData("auto", "ABCD", 1, 6)]
    [InlineData("multiple", "ABCD", 1, 16)]
    [InlineData("single", "ABCD", 2, 6)]
    [InlineData("auto", "CDAB", 2, 16)]
    [InlineData("auto", "BADC", 2, 16)]
    [InlineData("auto", "DCBA", 2, 16)]
    public async Task Register_write_mode_and_word_order_are_visible_on_wire(string mode, string order, int registers, int fc)
    {
        await using var server = new Server();
        await using var driver = Driver(server, new() { ["writeMode"] = mode, ["wordOrder"] = order });
        await driver.ConnectAsync(default);
        var binding = Binding("HR:4", registers == 1 ? IgwDataType.UInt16 : IgwDataType.UInt32);
        Assert.True(Assert.Single(await driver.WriteAsync([Request(binding, registers == 1 ? 0x1122u : 0x11223344u)], default)).Status.IsGood);
        var frames = server.Frames.ToArray();
        Assert.Equal(mode == "single" ? registers : 1, frames.Length);
        Assert.All(frames, f => Assert.Equal(fc, f[7]));
        var payload = fc == 6 ? frames.SelectMany(f => f[10..12]).ToArray() : frames[0][13..];
        var expected = registers == 1 ? new byte[] { 0x11, 0x22 } : order switch
        {
            "CDAB" => [0x33, 0x44, 0x11, 0x22], "BADC" => [0x22, 0x11, 0x44, 0x33],
            "DCBA" => [0x44, 0x33, 0x22, 0x11], _ => new byte[] { 0x11, 0x22, 0x33, 0x44 }
        };
        Assert.Equal(expected, payload);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Device_read_limits_govern_scalars_and_arrays(bool arrays)
    {
        await using var server = new Server();
        await using var driver = Driver(server, new() { ["regBlock"] = "2", ["coilBlock"] = "3" });
        await driver.ConnectAsync(default);
        var tags = arrays ? new[] { Binding("HR:0", IgwDataType.UInt16, 5), Binding("C:0", IgwDataType.Bool, 7) }
            : Enumerable.Range(0, 5).Select(i => Binding("HR:" + i, IgwDataType.UInt16))
                .Concat(Enumerable.Range(0, 7).Select(i => Binding("C:" + i, IgwDataType.Bool))).ToArray();
        Assert.All(await driver.ReadAsync(tags, default), value => Assert.True(value.Value.Quality.IsGood));
        Assert.Equal(new[] { 2, 2, 1 }, server.Frames.Where(f => f[7] == 3).Select(f => U16(f, 10)));
        Assert.Equal(new[] { 3, 3, 1 }, server.Frames.Where(f => f[7] == 1).Select(f => U16(f, 10)));
    }

    [Fact]
    public async Task Input_areas_never_emit_write_frames()
    {
        await using var server = new Server();
        await using var driver = Driver(server, new());
        await driver.ConnectAsync(default);
        var writes = new[] { Request(Binding("DI:0", IgwDataType.Bool), true), Request(Binding("IR:0", IgwDataType.UInt16), 2) };
        Assert.All(await driver.WriteAsync(writes, default), r => Assert.Equal(StatusCode.BadNotWritable, r.Status));
        Assert.Empty(server.Frames);
    }

    internal static IoTModbusDeviceDriver Driver(Server server, Dictionary<string, string> settings) =>
        ModbusProtocolSettingsTests.Create($"127.0.0.1:{server.Port}", settings);
    [Fact]
    public async Task Coil_array_write_preserves_non_byte_aligned_count()
    {
        await using var server = new Server();
        await using var driver = Driver(server, new());
        await driver.ConnectAsync(default);
        var binding = Binding("C:12", IgwDataType.Bool, 9);
        var value = PrimitiveArrayValue<bool>.Create(binding.Def.Array!.Shape, [true, false, true, false, true, false, true, false, true]);
        var result = await driver.WriteArrayAsync(new ArrayWriteRequest(binding, new ArraySlice([0], [9]), value), default);
        Assert.Equal(9, result.Succeeded);
        var frame = Assert.Single(server.Frames);
        Assert.Equal(15, frame[7]);
        Assert.Equal(9, U16(frame, 10));
        Assert.Equal(new byte[] { 0x55, 1 }, frame[13..]);
    }

    [Theory]
    [InlineData("C:65535", IgwDataType.Bool)]
    [InlineData("HR:65535", IgwDataType.UInt16)]
    public async Task Out_of_range_arrays_never_touch_wire(string address, IgwDataType type)
    {
        await using var server = new Server();
        await using var driver = Driver(server, new());
        await driver.ConnectAsync(default);
        var binding = Binding(address, type, 2);
        Assert.False(Assert.Single(await driver.ReadAsync([binding], default)).Value.Quality.IsGood);
        ArrayRuntimeValue value = type == IgwDataType.Bool
            ? PrimitiveArrayValue<bool>.Create(binding.Def.Array!.Shape, [true, false])
            : PrimitiveArrayValue<ushort>.Create(binding.Def.Array!.Shape, [1, 2]);
        var result = await driver.WriteArrayAsync(new ArrayWriteRequest(binding, new ArraySlice([0], [2]), value), default);
        Assert.False(result.Status.IsGood);
        Assert.Empty(server.Frames);
    }

    [Theory]
    [InlineData("ABCD", 0x00, 0x10, 0x00, 0x18)]
    [InlineData("BADC", 0x10, 0x00, 0x18, 0x00)]
    public async Task Register_bit_auto_write_preserves_other_bits_and_byte_order(string order, byte hi, byte lo, byte outHi, byte outLo)
    {
        await using var server = new Server();
        server.Response = reply => { if (reply[7] == 3) { reply[9] = hi; reply[10] = lo; } return reply; };
        await using var driver = Driver(server, new() { ["wordOrder"] = order });
        await driver.ConnectAsync(default);
        Assert.True(Assert.Single(await driver.WriteAsync([Request(Binding("HR:0.3", IgwDataType.Bool), true)], default)).Status.IsGood);
        var frames = server.Frames.ToArray();
        Assert.Equal(new byte[] { 3, 6 }, frames.Select(f => f[7]));
        Assert.Equal(new byte[] { outHi, outLo }, frames[1][10..12]);
    }

    [Theory]
    [InlineData("ABCD")]
    [InlineData("BADC")]
    public async Task Register_bool_array_crosses_register_boundary_and_preserves_unselected_bits(string order)
    {
        await using var server = new Server();
        server.Response = reply =>
        {
            if (reply[7] == 3)
            {
                // Each register starts 0x4002: bit14=true, bit15=false, bit0=false, bit1=true.
                for (int i = 9; i < reply.Length; i += 2)
                { reply[i] = order == "ABCD" ? (byte)0x40 : (byte)2; reply[i + 1] = order == "ABCD" ? (byte)2 : (byte)0x40; }
            }
            return reply;
        };
        await using var driver = Driver(server, new() { ["wordOrder"] = order, ["regBlock"] = "1" });
        await driver.ConnectAsync(default);
        var binding = Binding("HR:0.14", IgwDataType.Bool, 4);
        var sample = Assert.Single(await driver.ReadAsync([binding], default));
        var read = Assert.IsType<PrimitiveArrayValue<bool>>(sample.Value.Value);
        Assert.Equal(new[] { true, false, false, true }, read.Values.ToArray());
        Assert.True(read.OverallQuality.IsGood);
        var value = PrimitiveArrayValue<bool>.Create(binding.Def.Array!.Shape, [false, true, true, false]);
        Assert.Equal(4, (await driver.WriteArrayAsync(new(binding, new([0], [4]), value), default)).Succeeded);
        var writes = server.Frames.Where(f => f[7] == 6).ToArray();
        Assert.Equal(new[] { 0, 1 }, writes.Select(f => U16(f, 8)));
        Assert.Equal(order == "ABCD" ? new byte[] { 0x80, 2, 0x40, 1 } : new byte[] { 2, 0x80, 1, 0x40 },
            writes.SelectMany(f => f[10..12]).ToArray());
    }

    [Fact]
    public async Task Modbus_exception_response_is_a_point_failure_without_retry_or_disconnect()
    {
        await using var server = new Server();
        server.Response = reply => { var error = reply[..9]; error[5] = 3; error[7] |= 0x80; error[8] = 1; return error; };
        await using var driver = Driver(server, new());
        await driver.ConnectAsync(default);
        var result = Assert.Single(await driver.WriteAsync([Request(Binding("HR:0", IgwDataType.UInt16), 1)], default));
        Assert.Equal(StatusCode.BadDeviceFailure, result.Status);
        Assert.Equal(ConnectionState.Connected, driver.State);
        Assert.Single(server.Frames);
    }

    [Fact]
    public async Task Register_limit_one_reassembles_multi_register_scalar_and_array_elements()
    {
        await using var server = new Server();
        await using var driver = Driver(server, new() { ["regBlock"] = "1" });
        await driver.ConnectAsync(default);
        var scalar = Binding("HR:0", IgwDataType.UInt32);
        var array = Binding("HR:10", IgwDataType.UInt32, 2);
        array.Def.Array!.ElementStrideBytes = 4;
        Assert.All(await driver.ReadAsync([scalar, array], default), value => Assert.True(value.Value.Quality.IsGood));
        Assert.Equal(6, server.Frames.Count);
        Assert.All(server.Frames, f => Assert.Equal(1, U16(f, 10)));
    }
    internal static TagBinding Binding(string address, IgwDataType type, int count = 0)
    {
        Assert.True(new IoTModbusAddressParser().TryParse(address, type, out var parsed, out var error), error);
        return new TagBinding { TagId = address, Address = parsed, Def = new TagDef
        {
            Name = address, Address = address, DataType = type, Access = TagAccess.ReadWrite,
            Array = count == 0 ? null : new ArrayDef { ElementType = type, Dimensions = [count], LowerBounds = [0],
                ElementStrideBytes = type == IgwDataType.Bool ? 0 : 2, ElementStrideBits = type == IgwDataType.Bool ? 1 : 0 }
        } };
    }

    [Theory]
    [InlineData("C:5", IgwDataType.Bool, 1)]
    [InlineData("DI:5", IgwDataType.Bool, 2)]
    [InlineData("HR:5", IgwDataType.UInt16, 3)]
    [InlineData("IR:5", IgwDataType.UInt16, 4)]
    public async Task Reads_map_all_four_areas_and_unit_zero_to_wire(string address, IgwDataType type, int fc)
    {
        await using var server = new Server();
        await using var driver = Driver(server, new() { ["unitId"] = "0" });
        await driver.ConnectAsync(default);
        Assert.True(Assert.Single(await driver.ReadAsync([Binding(address, type)], default)).Value.Quality.IsGood);
        var frame = Assert.Single(server.Frames);
        Assert.Equal(0, frame[6]); Assert.Equal(fc, frame[7]); Assert.Equal(5, U16(frame, 8)); Assert.Equal(1, U16(frame, 10));
    }

    [Fact]
    public async Task Device_timeout_overrides_channel_timeout_and_uncertain_write_is_not_retried()
    {
        await using var server = new Server { ResponseDelayMs = 1500 };
        await using var driver = ModbusProtocolSettingsTests.Create($"127.0.0.1:{server.Port}",
            new() { ["timeoutMs"] = "100" }, new() { ["timeoutMs"] = "3000" });
        await driver.ConnectAsync(default);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<IOException>(() => driver.WriteAsync([Request(Binding("HR:0", IgwDataType.UInt16), 1)], default));
        Assert.True(timer.ElapsedMilliseconds < 1200, $"Device timeout ignored: {timer.ElapsedMilliseconds} ms");
        Assert.Single(server.Frames);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(10)]
    public async Task Write_response_must_echo_transaction_unit_address_and_value(int corruptOffset)
    {
        await using var server = new Server();
        server.Response = reply => { reply[corruptOffset] ^= 1; return reply; };
        await using var driver = Driver(server, new());
        await driver.ConnectAsync(default);
        Assert.False(Assert.Single(await driver.WriteAsync([Request(Binding("HR:0", IgwDataType.UInt16), 1)], default)).Status.IsGood);
        Assert.Single(server.Frames);
    }

    [Fact]
    public async Task Full_coil_protocol_quantity_is_bounded_without_dropping_tail()
    {
        await using var server = new Server();
        await using var driver = Driver(server, new() { ["writeMode"] = "multiple" });
        await driver.ConnectAsync(default);
        var binding = Binding("C:0", IgwDataType.Bool, 1969);
        var values = PrimitiveArrayValue<bool>.Create(binding.Def.Array!.Shape, Enumerable.Repeat(true, 1969).ToArray());
        Assert.Equal(1969, (await driver.WriteArrayAsync(new(binding, new([0], [1969]), values), default)).Succeeded);
        Assert.Equal(new[] { 1968, 1 }, server.Frames.Select(f => U16(f, 10)));
        Assert.Equal(new[] { 0, 1968 }, server.Frames.Select(f => U16(f, 8)));
        Assert.Equal(new byte[] { 1 }, server.Frames.Last()[13..]);
    }
    internal static WriteRequest Request(TagBinding binding, object value) => new()
        { TagId = binding.TagId, Def = binding.Def, Address = binding.Address, Value = value };
    internal static int U16(byte[] frame, int offset) => BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(offset));

    internal sealed class Server : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _task;
        internal ConcurrentQueue<byte[]> Frames { get; } = new();
        internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        internal Func<byte[], byte[]>? Response { get; set; }
        internal int ResponseDelayMs { get; init; }
        internal Server() { _listener.Start(); _task = Task.Run(Run); }
        private async Task Run()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                using var stream = client.GetStream();
                while (!_stop.IsCancellationRequested)
                {
                    var header = new byte[7];
                    await stream.ReadExactlyAsync(header, _stop.Token);
                    var body = new byte[U16(header, 4) - 1];
                    await stream.ReadExactlyAsync(body, _stop.Token);
                    var frame = header.Concat(body).ToArray();
                    Frames.Enqueue(frame);
                    byte[] reply;
                    if (body[0] is >= 1 and <= 4)
                    {
                        int bytes = body[0] <= 2 ? (U16(frame, 10) + 7) / 8 : U16(frame, 10) * 2;
                        reply = new byte[9 + bytes]; frame.AsSpan(0, 8).CopyTo(reply);
                        reply[8] = (byte)bytes;
                    }
                    else { reply = frame[..12]; }
                    BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(4), (ushort)(reply.Length - 6));
                    if (ResponseDelayMs > 0) await Task.Delay(ResponseDelayMs, _stop.Token);
                    await stream.WriteAsync(Response?.Invoke(reply) ?? reply, _stop.Token);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or SocketException) { }
        }
        public async ValueTask DisposeAsync() { _stop.Cancel(); _listener.Stop(); await _task; _stop.Dispose(); }
    }
}
