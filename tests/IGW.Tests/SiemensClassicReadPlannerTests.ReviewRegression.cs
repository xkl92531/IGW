using System.Buffers.Binary;
using IGW.Abstractions;
using IGW.Drivers.IoTClient;
using IoTClient;
using IoTClient.Clients.PLC;
using IoTClient.Common.Enums;
using Xunit;

namespace IGW.Tests;

public sealed partial class SiemensClassicReadPlannerTests
{
    [Theory]
    [InlineData(6)]
    [InlineData(254)]
    public async Task Real_classic_block_auto_GB18030_read_teaches_scalar_write_bytes(int capacity)
    {
        var client = new PacketSiemensClient();
        client.Image[20] = (byte)capacity;
        client.Image[21] = 2;
        client.Image[22] = 0xc4; client.Image[23] = 0xe3;
        await using var driver = new IoTPlcDeviceDriver(IoTSiemensDriverFactory.Id, () => client);
        var tag = Binding("DB1.DBB20", IgwDataType.String, capacity);
        var read = Assert.Single(await driver.ReadAsync([tag], default));
        Assert.True(read.Value.IsGood, driver.LastReadError);
        Assert.Equal("你", read.Value.Value);
        var written = Assert.Single(await driver.WriteAsync([Write(tag, "你")], default));
        Assert.True(written.Status.IsGood);
        Assert.Equal(new byte[] { (byte)capacity, 2, 0xc4, 0xe3 }, Assert.Single(client.Writes).AsSpan(35).ToArray());
    }

    [Fact]
    public async Task Real_classic_auto_encoding_learning_is_isolated_between_driver_instances()
    {
        var gb = new PacketSiemensClient();
        var utf8 = new PacketSiemensClient();
        new byte[] { 6, 2, 0xc4, 0xe3 }.CopyTo(gb.Image, 20);
        new byte[] { 6, 3, 0xe4, 0xbd, 0xa0 }.CopyTo(utf8.Image, 20);
        await using var first = new IoTPlcDeviceDriver(IoTSiemensDriverFactory.Id, () => gb);
        await using var second = new IoTPlcDeviceDriver(IoTSiemensDriverFactory.Id, () => utf8);
        var tag = Binding("DB1.DBB20", IgwDataType.String, 6);
        Assert.Equal("你", Assert.Single(await first.ReadAsync([tag], default)).Value.Value);
        Assert.Equal("你", Assert.Single(await second.ReadAsync([tag], default)).Value.Value);
        await first.WriteAsync([Write(tag, "你")], default);
        await second.WriteAsync([Write(tag, "你")], default);
        Assert.Equal(new byte[] { 6, 2, 0xc4, 0xe3 }, Assert.Single(gb.Writes).AsSpan(35).ToArray());
        Assert.Equal(new byte[] { 6, 3, 0xe4, 0xbd, 0xa0 }, Assert.Single(utf8.Writes).AsSpan(35).ToArray());
    }

    [Theory]
    [InlineData(3)]
    [InlineData(200)]
    public async Task Real_classic_array_read_write_keeps_PLC_address_order_exactly_once(int payload)
    {
        var client = new PacketSiemensClient();
        new byte[] { 0, 7, 0, 8, 0, 9, 0, 10 }.CopyTo(client.Image, 20);
        await using var driver = new IoTPlcDeviceDriver(IoTSiemensDriverFactory.Id, () => client, maxPayloadBytes: payload);
        var tag = Binding("DB1.DBW20", IgwDataType.UInt16, array: new ArrayDef
            { ElementType = IgwDataType.UInt16, Dimensions = [4], LowerBounds = [0], ElementStrideBytes = 2 });
        var sample = Assert.Single(await driver.ReadAsync([tag], default));
        var values = Assert.IsType<PrimitiveArrayValue<ushort>>(sample.Value.Value);
        Assert.Equal(new ushort[] { 7, 8, 9, 10 }, values.Values.ToArray());
        var writeValues = PrimitiveArrayValue<ushort>.Create(ArrayShape.Create([4], [0]), [11, 12, 13, 14]);
        var result = await driver.WriteArrayAsync(new(tag, new ArraySlice([0], [4]), writeValues), default);
        Assert.True(result.Status.IsGood);
        Assert.Equal(new byte[] { 0, 11, 0, 12, 0, 13, 0, 14 }, client.Writes.SelectMany(p => p.Skip(35)).ToArray());
        Assert.Equal(payload == 3 ? 3 : 1, client.Writes.Count);
    }

    [Fact]
    public async Task Real_raw_write_does_not_mutate_the_callers_buffer()
    {
        var client = new PacketSiemensClient();
        byte[] bytes = [1, 2, 3, 4];
        await new IoTSiemensByteTransport(client).WriteAsync(1, 20, bytes, default);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, bytes);
        Assert.Equal(bytes, Assert.Single(client.Writes).AsSpan(35).ToArray());
    }

    /// <summary>
    /// 让路要发生在两次物理请求之间。过程映像区现在走块读，连续地址一次就读完，
    /// 所以这里用两段相隔很远的 M 区地址，逼出两个读取范围。
    /// </summary>
    [Fact]
    public async Task Real_non_DB_batch_yields_before_the_second_physical_request()
    {
        var client = new PacketSiemensClient();
        await using var driver = new IoTPlcDeviceDriver(IoTSiemensDriverFactory.Id, () => client, batchSize: 19);
        driver.SetPriorityWorkProbe(() => client.Reads.Count > 0);
        var tags = Enumerable.Range(0, 20).Select(i => Binding($"MW{i * 2}", IgwDataType.UInt16))
            .Concat(Enumerable.Range(0, 20).Select(i => Binding($"MW{400 + i * 2}", IgwDataType.UInt16)))
            .ToArray();
        await Assert.ThrowsAsync<DriverReadYieldException>(() => driver.ReadAsync(tags, default));
        // 块读的一次请求就是一个连续范围(一个 item),不再是 19 个逐点 item
        Assert.Equal(1, Assert.Single(client.Reads)[18]);
    }

    /// <summary>
    /// 40 个连续的 M 区 word 现在合并成一次块读(原先按 batchSize 切成 19/19/2 三次逐点请求)，
    /// 结果仍要按调用方顺序返回、不重复读。
    /// </summary>
    [Fact]
    public async Task Real_non_DB_batches_return_one_ordered_logical_result_without_duplicate_reads()
    {
        var client = new PacketSiemensClient();
        for (int i = 0; i < 40; i++) client.Image[i * 2 + 1] = (byte)(i + 1);
        await using var driver = new IoTPlcDeviceDriver(IoTSiemensDriverFactory.Id, () => client, batchSize: 19);
        var tags = Enumerable.Range(0, 40).Select(i => Binding($"MW{i * 2}", IgwDataType.UInt16)).ToArray();
        using var timing = ProtocolOperationTiming.Begin();
        var values = await driver.ReadAsync(tags, default);
        Assert.Equal(1, timing.Snapshot().PhysicalRequests);
        Assert.Equal(tags.Select(t => t.TagId), values.Select(s => s.TagId));
        Assert.Equal(Enumerable.Range(1, 40).Select(i => (ushort)i), values.Select(s => Assert.IsType<ushort>(s.Value.Value)));
        Assert.Equal(new byte[] { 1 }, client.Reads.Select(p => p[18]).ToArray());
    }

    [Fact]
    public async Task Real_non_DB_batch_observes_cancellation_before_second_physical_request()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new PacketSiemensClient { AfterRead = cancellation.Cancel };
        await using var driver = new IoTPlcDeviceDriver(IoTSiemensDriverFactory.Id, () => client, batchSize: 19);
        var tags = Enumerable.Range(0, 40).Select(i => Binding($"MW{i * 2}", IgwDataType.UInt16)).ToArray();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => driver.ReadAsync(tags, cancellation.Token));
        Assert.Single(client.Reads);
    }

    private static WriteRequest Write(TagBinding tag, object value) => new()
        { TagId = tag.TagId, Def = tag.Def, Address = tag.Address, Value = value };

    // Use IoTClient's actual Read/BatchRead/Write packet generation and result conversion.
    // Only the final I/O boundary is replaced; returned payloads are in normal PLC address order.
    private sealed class PacketSiemensClient() : SiemensClient(SiemensVersion.S7_300, "127.0.0.1", 102, 2, 0, 100), IPhysicalRequestClient
    {
        public byte[] Image { get; } = new byte[512];
        public List<byte[]> Reads { get; } = [];
        public List<byte[]> Writes { get; } = [];
        public Action? AfterRead { get; init; }
        protected override Result Connect() => new() { IsSucceed = true };
        public override Result<byte[]> SendPackageSingle(byte[] command) =>
            IoTProtocolTiming.Exchange(() => Respond(command), "S7 Classic", command);

        private Result<byte[]> Respond(byte[] command)
        {
            if (command[17] == 5)
            {
                Writes.Add(command.ToArray());
                byte[] ack = new byte[22];
                ack[19] = 5; ack[20] = 1; ack[21] = 0xff;
                return new() { IsSucceed = true, Value = ack };
            }
            Reads.Add(command.ToArray());
            AfterRead?.Invoke();
            int count = command[18];
            var response = new List<byte>(new byte[21]);
            response[19] = 4; response[20] = (byte)count;
            for (int i = 0; i < count; i++)
            {
                int item = 19 + i * 12;
                int length = BinaryPrimitives.ReadUInt16BigEndian(command.AsSpan(item + 4, 2));
                int offset = (command[item + 9] << 16 | command[item + 10] << 8 | command[item + 11]) / 8;
                // 过程映像区(I/Q/M,区域码 0x81/0x82/0x83)和 DB 区一样按请求长度返回整段字节。
                // 原先固定回 2 字节，是「M 区只会逐点读一个 word」的旧假设；这些区现在和 DB 区
                // 走同一条块读路径，一次就要取回整个连续范围。
                byte[] data = Image.AsSpan(offset, length).ToArray();
                response.AddRange([0xff, 4, (byte)(data.Length * 8 >> 8), (byte)(data.Length * 8)]);
                response.AddRange(data);
            }
            return new() { IsSucceed = true, Value = response.ToArray() };
        }
    }
}
