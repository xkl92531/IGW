using System.Buffers.Binary;
using IGW.Abstractions;
using IGW.Drivers.IoTClient;
using Xunit;

namespace IGW.Tests;

public sealed class SiemensClassicArrayTests
{
    [Fact]
    public void Definition_span_counts_bit_offset_and_confirmed_struct_stride()
    {
        var bits = SiemensClassicReadPlannerTests.Binding("DB1.DBX10.7", IgwDataType.Bool, array: new ArrayDef
            { ElementType = IgwDataType.Bool, Dimensions = [2], LowerBounds = [0], ElementStrideBits = 1 });
        Assert.Equal(2, bits.Address.Length);
        var structs = SiemensClassicReadPlannerTests.Binding("DB1.DBB20", IgwDataType.Struct, array: new ArrayDef
            { ElementType = IgwDataType.Struct, Dimensions = [3], LowerBounds = [0], Struct = new StructDef { SizeBytes = 12 } });
        Assert.Equal(36, structs.Address.Length);
    }

    [Fact]
    public void Known_array_and_adjacent_scalar_decode_from_the_same_bounded_response()
    {
        var array = SiemensClassicReadPlannerTests.Binding("DB1.DBW20", IgwDataType.Int16, array: new ArrayDef
            { ElementType = IgwDataType.Int16, Dimensions = [2], LowerBounds = [0], ElementStrideBytes = 2 });
        var scalar = SiemensClassicReadPlannerTests.Binding("DB1.DBW24", IgwDataType.UInt16);
        var range = Assert.Single(SiemensClassicReadPlanner.Plan([array, scalar], 200));
        var values = SiemensClassicBlockCodec.Decode(range, new byte[] { 0, 7, 0, 8, 0, 9 }, DateTime.UtcNow);
        var decoded = Assert.IsType<PrimitiveArrayValue<short>>(values[0].Value.Value);
        Assert.Equal(new short[] { 7, 8 }, decoded.Values.ToArray());
        Assert.Equal((ushort)9, values[1].Value.Value);
        Assert.All(values, value => Assert.True(value.Value.IsGood));
    }

    [Fact]
    public async Task Reads_primitive_array_in_pdu_sized_blocks()
    {
        var bytes = new byte[8];
        for (short i = 0; i < 4; i++)
            BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(i * 2, 2), (short)(100 + i));
        var transport = new FakeS7Bytes(bytes, maxPayload: 3);
        var binding = Binding(new ArrayDef
        {
            ElementType = IgwDataType.Int16,
            Dimensions = [4], LowerBounds = [1], ElementStrideBytes = 2,
        }, IgwDataType.Int16, "DB2100.DBW0");

        var value = await SiemensArrayCodec.ReadAsync(transport, binding, CancellationToken.None);

        var typed = Assert.IsType<PrimitiveArrayValue<short>>(value);
        Assert.Equal([100, 101, 102, 103], typed.Values.ToArray());
        Assert.All(transport.Reads, request => Assert.InRange(request.Length, 1, 3));
        Assert.Equal(StatusCode.Good, value.OverallQuality);
    }

    [Fact]
    public async Task Reads_struct_array_as_one_logical_value_and_marks_only_overlapping_element_bad()
    {
        var schema = new StructDef
        {
            Id = "cell", Name = "Cell", SizeBytes = 10,
            Fields =
            [
                new StructFieldDef { Name = "Code", DataType = IgwDataType.String, ByteOffset = 0, StringLength = 3 },
                new StructFieldDef { Name = "Value", DataType = IgwDataType.Int16, ByteOffset = 6 },
                new StructFieldDef { Name = "Ok", DataType = IgwDataType.Bool, ByteOffset = 8, BitOffset = 0 },
            ],
        };
        var bytes = new byte[20];
        PutString(bytes, 0, 3, "A1");
        BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(6, 2), 7);
        bytes[8] = 1;
        PutString(bytes, 10, 3, "B2");
        BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(16, 2), 9);
        bytes[18] = 1;
        var transport = new FakeS7Bytes(bytes, maxPayload: 10) { FailAtOffset = 10 };
        var binding = Binding(new ArrayDef
        {
            ElementType = IgwDataType.Struct,
            Dimensions = [2], LowerBounds = [0], ElementStrideBytes = 10, Struct = schema,
        }, IgwDataType.Struct, "DB2100.DBB0");

        var value = Assert.IsType<StructArrayValue>(
            await SiemensArrayCodec.ReadAsync(transport, binding, CancellationToken.None));

        Assert.Equal("A1", value.Members["Code"].GetBoxedValue(0));
        Assert.Equal((short)7, value.Members["Value"].GetBoxedValue(0));
        Assert.Equal(true, value.Members["Ok"].GetBoxedValue(0));
        Assert.True(value.ElementQuality.Span[0].IsGood);
        Assert.False(value.ElementQuality.Span[1].IsGood);
        Assert.Equal(StatusCode.Uncertain, value.OverallQuality);
    }

    [Fact]
    public async Task Writes_only_requested_multidimensional_slice()
    {
        var transport = new FakeS7Bytes(new byte[12], maxPayload: 3);
        var binding = Binding(new ArrayDef
        {
            ElementType = IgwDataType.Int16,
            Dimensions = [2, 3], LowerBounds = [1, -1], ElementStrideBytes = 2,
        }, IgwDataType.Int16, "DB7.DBW4");
        var values = PrimitiveArrayValue<short>.Create(
            ArrayShape.Create([1, 2], [2, -1]), [44, 55]);

        var result = await SiemensArrayCodec.WriteAsync(transport,
            new ArrayWriteRequest(binding, new ArraySlice([2, -1], [1, 2]), values), CancellationToken.None);

        Assert.Equal(StatusCode.Good, result.Status);
        Assert.Equal(2, result.Succeeded);
        Assert.Equal([44, 55], new[]
        {
            BinaryPrimitives.ReadInt16BigEndian(transport.Image.AsSpan(10, 2)),
            BinaryPrimitives.ReadInt16BigEndian(transport.Image.AsSpan(12, 2)),
        });
        Assert.All(transport.Writes, request => Assert.InRange(request.Data.Length, 1, 3));
    }

    private static TagBinding Binding(ArrayDef array, IgwDataType type, string address) => new()
    {
        TagId = "S7.PLC.DB.Values",
        Address = new TagAddress(0, 0, 1),
        Def = new TagDef { Name = "Values", Address = address, DataType = type, Array = array },
    };

    private static void PutString(byte[] target, int offset, int capacity, string text)
    {
        target[offset] = (byte)capacity;
        target[offset + 1] = (byte)text.Length;
        System.Text.Encoding.ASCII.GetBytes(text, target.AsSpan(offset + 2));
    }

    private sealed class FakeS7Bytes(byte[] image, int maxPayload) : ISiemensByteTransport
    {
        public byte[] Image { get; } = new byte[Math.Max(image.Length + 16, 64)];
        public int MaxPayloadBytes => maxPayload;
        public int? FailAtOffset { get; init; }
        public List<(int Offset, int Length)> Reads { get; } = [];
        public List<(int Offset, byte[] Data)> Writes { get; } = [];

        public ValueTask<SiemensByteReadResult> ReadAsync(int dbNumber, int byteOffset, int length,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Reads.Count == 0) image.CopyTo(Image, 0);
            Reads.Add((byteOffset, length));
            if (FailAtOffset is int failed && byteOffset <= failed && failed < byteOffset + length)
                return ValueTask.FromResult(SiemensByteReadResult.Failed(StatusCode.BadCommFailure, "模拟分块失败"));
            return ValueTask.FromResult(SiemensByteReadResult.Good(Image.AsSpan(byteOffset, length).ToArray()));
        }

        public ValueTask<StatusCode> WriteAsync(int dbNumber, int byteOffset, ReadOnlyMemory<byte> data,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            data.Span.CopyTo(Image.AsSpan(byteOffset));
            Writes.Add((byteOffset, data.ToArray()));
            return ValueTask.FromResult(StatusCode.Good);
        }
    }
}
