#nullable enable
using System.IO;
using System.Text;
using PlcSimulator.Core;

namespace PlcSimulator.Protocols;

internal static class OpcUaStructureCodec
{
    public static bool Supports(SimulationDataType type) => type is >= SimulationDataType.Boolean and <= SimulationDataType.String or SimulationDataType.Bytes;

    public static void Write(BinaryWriter writer, SimulationTag tag, object? value)
    {
        if (!tag.IsArray) { WriteScalar(writer, tag.DataType, value); return; }
        var items = (Array)value!;
        writer.Write(items.Length);
        foreach (var item in items) WriteScalar(writer, tag.DataType, item);
    }

    public static object Read(BinaryReader reader, SimulationTag tag)
    {
        if (!tag.IsArray) return ReadScalar(reader, tag.DataType);
        int count = reader.ReadInt32();
        if (count != tag.Dimensions.Aggregate(1, (a, b) => checked(a * b))) throw new InvalidDataException("结构体数组成员长度不匹配");
        var values = new object[count];
        for (int i = 0; i < count; i++) values[i] = ReadScalar(reader, tag.DataType);
        return values;
    }

    private static void WriteScalar(BinaryWriter writer, SimulationDataType type, object? value)
    {
        switch (type)
        {
            case SimulationDataType.Boolean: writer.Write(Convert.ToBoolean(value)); break;
            case SimulationDataType.Byte: writer.Write(Convert.ToByte(value)); break;
            case SimulationDataType.SByte: writer.Write(Convert.ToSByte(value)); break;
            case SimulationDataType.Int16: writer.Write(Convert.ToInt16(value)); break;
            case SimulationDataType.UInt16: writer.Write(Convert.ToUInt16(value)); break;
            case SimulationDataType.Int32: writer.Write(Convert.ToInt32(value)); break;
            case SimulationDataType.UInt32: writer.Write(Convert.ToUInt32(value)); break;
            case SimulationDataType.Int64: writer.Write(Convert.ToInt64(value)); break;
            case SimulationDataType.UInt64: writer.Write(Convert.ToUInt64(value)); break;
            case SimulationDataType.Float32: writer.Write(Convert.ToSingle(value)); break;
            case SimulationDataType.Float64: writer.Write(Convert.ToDouble(value)); break;
            case SimulationDataType.String:
                var text = Encoding.UTF8.GetBytes(Convert.ToString(value) ?? ""); writer.Write(text.Length); writer.Write(text); break;
            case SimulationDataType.Bytes:
                var bytes = (byte[])value!; writer.Write(bytes.Length); writer.Write(bytes); break;
            default: throw new InvalidDataException("不支持的结构体成员类型");
        }
    }

    private static object ReadScalar(BinaryReader reader, SimulationDataType type) => type switch
    {
        SimulationDataType.Boolean => reader.ReadBoolean(),
        SimulationDataType.Byte => reader.ReadByte(), SimulationDataType.SByte => reader.ReadSByte(),
        SimulationDataType.Int16 => reader.ReadInt16(), SimulationDataType.UInt16 => reader.ReadUInt16(),
        SimulationDataType.Int32 => reader.ReadInt32(), SimulationDataType.UInt32 => reader.ReadUInt32(),
        SimulationDataType.Int64 => reader.ReadInt64(), SimulationDataType.UInt64 => reader.ReadUInt64(),
        SimulationDataType.Float32 => reader.ReadSingle(), SimulationDataType.Float64 => reader.ReadDouble(),
        SimulationDataType.String => Encoding.UTF8.GetString(ReadByteString(reader)),
        SimulationDataType.Bytes => ReadByteString(reader),
        _ => throw new InvalidDataException("不支持的结构体成员类型"),
    };

    private static byte[] ReadByteString(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length == -1) return Array.Empty<byte>();
        if (length < 0 || length > reader.BaseStream.Length - reader.BaseStream.Position) throw new InvalidDataException("结构体字符串长度无效");
        return reader.ReadBytes(length);
    }
}
