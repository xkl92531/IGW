using IGW.Abstractions;
using Opc.Ua;

namespace IGW.Drivers.OpcUaClient;

/// <summary>Conversion requires the configured/source semantic type; sizes never infer types.</summary>
public static class SiemensOpcUaCodec
{
    public static object WrittenValue(IgwDataType type, object wire) => SiemensValueCodec.IsSemantic(type)
        ? Decode(type, wire) : wire;

    public static Variant WireVariant(IgwDataType type, object wire) => type == IgwDataType.Dt
        ? new Variant(wire, new TypeInfo(BuiltInType.Byte, wire is Array array ? array.Rank : 1))
        : new Variant(wire);

    public static object Decode(IgwDataType type, object source)
    {
        if (type != IgwDataType.Dtl) return SiemensValueCodec.Normalize(type, source);
        if (source is ExtensionObject extension)
        {
            if (extension.Body is byte[] binary) return SiemensValueCodec.DecodeDtl(binary, littleEndian: true);
            if (extension.Body is IEncodeable body)
            {
                using var encoder = new BinaryEncoder(new ServiceMessageContext(DefaultTelemetry.Create(_ => { })));
                body.Encode(encoder);
                return SiemensValueCodec.DecodeDtl(encoder.CloseAndReturnBuffer(), littleEndian: true);
            }
            throw new InvalidDataException("DTL ExtensionObject 缺少二进制结构体编码");
        }
        return SiemensValueCodec.Normalize(type, source);
    }

    public static object Encode(IgwDataType type, object value, object? template = null)
    {
        if (type != IgwDataType.Dtl) return SiemensValueCodec.ToWire(type, value);
        if (template is ExtensionObject extension)
        {
            var binaryId = extension.Body is IEncodeable body ? body.BinaryEncodingId : extension.TypeId;
            if (binaryId.IsNull) throw new InvalidDataException("DTL 缺少原始 BinaryEncoding TypeId");
            return new ExtensionObject(binaryId, SiemensValueCodec.EncodeDtl(value, littleEndian: true));
        }
        if (template is byte[]) return SiemensValueCodec.EncodeDtl(value);
        throw new InvalidOperationException("DTL 写入必须先读取服务器原值以保留完整结构体编码");
    }

    public static object?[] Elements(object? source, IgwDataType type)
    {
        if (source is Matrix matrix) source = matrix.ToArray();
        if (type == IgwDataType.Dt && source is Array physical && physical.GetType().GetElementType() == typeof(byte))
        {
            if (physical.GetLength(physical.Rank - 1) != 8) throw new InvalidDataException("DT 物理数组最后一维必须为 8 字节");
            return physical.Cast<byte>().Chunk(8).Cast<object?>().ToArray();
        }
        return source is Array array ? array.Cast<object?>().ToArray() : [source];
    }
}
