using IGW.Abstractions;
using IGW.Drivers.OpcUaClient;
using Opc.Ua;
using Xunit;

namespace IGW.Tests;

public class SiemensOpcUaCodecTests
{
    [Fact]
    public void Decoded_Dtl_IEncodeable_uses_binary_encoding_identity_when_written_as_opaque_body()
    {
        const string date = "2024-02-29 12:34:56.123456789";
        var body = new DtlEncodeable();
        var template = new ExtensionObject(body);
        Assert.Equal(body.BinaryEncodingId, WireRoundTrip(template).TypeId);
        Assert.Equal(date, SiemensOpcUaCodec.Decode(IgwDataType.Dtl, template));
        var write = Assert.IsType<ExtensionObject>(SiemensOpcUaCodec.Encode(IgwDataType.Dtl, date, template));
        var readback = WireRoundTrip(write);
        Assert.Equal(body.BinaryEncodingId, readback.TypeId);
        Assert.Equal(date, SiemensOpcUaCodec.Decode(IgwDataType.Dtl, readback));
        var rewritten = Assert.IsType<ExtensionObject>(SiemensOpcUaCodec.Encode(IgwDataType.Dtl, date, readback));
        Assert.Equal(readback.TypeId, WireRoundTrip(rewritten).TypeId);
    }

    [Fact]
    public void Dt_Byte_array_variant_retains_Byte_rank_through_SDK_binary_wire()
    {
        const string date = "2024-02-29 12:34:56.123";
        var context = new ServiceMessageContext(DefaultTelemetry.Create(_ => { }));
        using var encoder = new BinaryEncoder(context);
        encoder.WriteVariant("Value", SiemensOpcUaCodec.WireVariant(IgwDataType.Dt, SiemensOpcUaCodec.Encode(IgwDataType.Dt, date)));
        using var decoder = new BinaryDecoder(encoder.CloseAndReturnBuffer(), context);
        var readback = decoder.ReadVariant("Value");
        Assert.Equal(BuiltInType.Byte, readback.TypeInfo.BuiltInType);
        Assert.Equal(ValueRanks.OneDimension, readback.TypeInfo.ValueRank);
        Assert.Equal(date, SiemensOpcUaCodec.Decode(IgwDataType.Dt, readback.Value));
    }

    private static ExtensionObject WireRoundTrip(ExtensionObject value)
    {
        var context = new ServiceMessageContext(DefaultTelemetry.Create(_ => { }));
        context.NamespaceUris.Append("urn:igw:test:dtl");
        using var encoder = new BinaryEncoder(context);
        encoder.WriteExtensionObject("Value", value);
        using var decoder = new BinaryDecoder(encoder.CloseAndReturnBuffer(), context);
        return decoder.ReadExtensionObject("Value");
    }

    private sealed class DtlEncodeable : IEncodeable
    {
        public ExpandedNodeId TypeId => new(50000u, "urn:igw:test:dtl");
        public ExpandedNodeId BinaryEncodingId => new(50001u, "urn:igw:test:dtl");
        public ExpandedNodeId XmlEncodingId => new(50002u, "urn:igw:test:dtl");
        public void Encode(IEncoder encoder)
        {
            encoder.WriteUInt16("YEAR", 2024);
            encoder.WriteByte("MONTH", 2);
            encoder.WriteByte("DAY", 29);
            encoder.WriteByte("WEEKDAY", 5);
            encoder.WriteByte("HOUR", 12);
            encoder.WriteByte("MINUTE", 34);
            encoder.WriteByte("SECOND", 56);
            encoder.WriteUInt32("NANOSECOND", 123456789);
        }
        public void Decode(IDecoder decoder) => throw new NotSupportedException();
        public bool IsEqual(IEncodeable value) => value is DtlEncodeable;
        public object Clone() => new DtlEncodeable();
    }

    [Fact]
    public void Successful_Dtl_write_expected_value_matches_canonical_readback()
    {
        const string date = "2024-02-29 12:34:56.123456789";
        var wire = new ExtensionObject(new ExpandedNodeId(100u, 3), SiemensValueCodec.EncodeDtl(date, true));
        var expected = SiemensOpcUaCodec.WrittenValue(IgwDataType.Dtl, wire);
        var tag = new TagDef { Name = "timestamp", Address = "ns=3;s=timestamp", DataType = IgwDataType.Dtl };
        var actual = new IGW.Abstractions.DataValue(date, IGW.Abstractions.StatusCode.Good, DateTime.UtcNow, DateTime.UtcNow);
        Assert.True(IGW.Core.WriteVerificationComparer.IsMatch(tag, expected, actual, out var reason), reason);
    }

    [Fact]
    public void Unknown_binary_structures_do_not_invent_byte_array_reflection_fields()
    {
        Assert.Null(OpcUaArrayCodec.TryInferStructure(new[] { new ExtensionObject(new ExpandedNodeId(100u, 3), new byte[12]) }, "UDT"));
    }

    [Fact]
    public void Dtl_array_slices_preserve_each_original_encoding()
    {
        var shape = ArrayShape.Create([2], [0]);
        var dates = new[] { "2024-02-29 12:34:56.123456789", "2024-03-01 00:00:00.000000001" };
        var templates = dates.Select(date => (object?)new ExtensionObject(new ExpandedNodeId(100u, 3), SiemensValueCodec.EncodeDtl(date, true))).ToArray();
        var value = PrimitiveArrayValue<string>.Create(shape, dates, [IGW.Abstractions.StatusCode.Good, IGW.Abstractions.StatusCode.Good], IgwDataType.Dtl);
        var wire = Assert.IsType<ExtensionObject[]>(OpcUaArrayCodec.Encode(value, templates));
        Assert.Equal(dates[1], SiemensOpcUaCodec.Decode(IgwDataType.Dtl, wire[1]));
        Assert.Equal(((ExtensionObject)templates[1]!).TypeId, wire[1].TypeId);
    }

    [Fact]
    public void Dt_scalar_is_a_UA_Byte_array_not_ByteString()
    {
        var wire = SiemensOpcUaCodec.Encode(IgwDataType.Dt, "2024-02-29 12:34:56.123");
        var variant = SiemensOpcUaCodec.WireVariant(IgwDataType.Dt, wire);
        Assert.Equal(BuiltInType.Byte, variant.TypeInfo.BuiltInType);
        Assert.Equal(ValueRanks.OneDimension, variant.TypeInfo.ValueRank);
    }

    [Fact]
    public void Dtl_binary_extension_retains_encoding_identity_and_nanoseconds()
    {
        const string date = "2024-02-29 12:34:56.123456789";
        var id = new ExpandedNodeId("DTL.Binary", 4);
        var template = new ExtensionObject(id, SiemensValueCodec.EncodeDtl(date, littleEndian: true));
        Assert.Equal(date, SiemensOpcUaCodec.Decode(IgwDataType.Dtl, template));
        var encoded = Assert.IsType<ExtensionObject>(SiemensOpcUaCodec.Encode(IgwDataType.Dtl, date, template));
        Assert.Equal(id, encoded.TypeId);
        Assert.Equal(template.Body, encoded.Body);
        Assert.Throws<InvalidOperationException>(() => SiemensOpcUaCodec.Encode(IgwDataType.Dtl, date));
    }

    [Fact]
    public void Explicit_raw_Dtl_is_big_endian_and_bytes_do_not_infer_Dtl()
    {
        const string date = "2024-02-29 12:34:56.123456789";
        byte[] raw = SiemensValueCodec.EncodeDtl(date);
        Assert.Equal(date, SiemensOpcUaCodec.Decode(IgwDataType.Dtl, raw));
        Assert.Equal(raw, SiemensOpcUaCodec.Encode(IgwDataType.Dtl, date, raw));
    }

    [Fact]
    public void Dt_multidimensional_physical_arrays_collapse_only_final_byte_dimension()
    {
        var shape = ArrayShape.Create([2, 3], [0, 0]);
        var dates = Enumerable.Repeat("2024-02-29 12:34:56.123", 6).ToArray();
        var value = PrimitiveArrayValue<string>.Create(shape, dates, Enumerable.Repeat(IGW.Abstractions.StatusCode.Good, 6).ToArray(), IgwDataType.Dt);
        var wire = Assert.IsAssignableFrom<Array>(OpcUaArrayCodec.Encode(value));
        Assert.Equal(3, wire.Rank);
        Assert.Equal(8, wire.GetLength(2));
        Assert.Equal(6, SiemensOpcUaCodec.Elements(wire, IgwDataType.Dt).Length);
        var tag = new TagDef { Name = "dates", Address = "ns=3;s=dates", DataType = IgwDataType.Dt,
            Array = new ArrayDef { ElementType = IgwDataType.Dt, Dimensions = [2, 3], LowerBounds = [0, 0] } };
        var decoded = OpcUaArrayCodec.Decode(wire, tag, IGW.Abstractions.StatusCode.Good);
        Assert.Equal(dates[5], decoded.GetBoxedValue(5));
    }
}
