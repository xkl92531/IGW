using System.Text;
using System.Globalization;
using IGW.Abstractions;

namespace IGW.Core;

/// <summary>在所有写 PLC 的统一入口按点位声明容量收敛字符串，避免越界覆盖相邻地址。</summary>
public static class PlcWriteValueNormalizer
{
    private static readonly Encoding Gb18030;

    static PlcWriteValueNormalizer()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Gb18030 = Encoding.GetEncoding(54936);
    }

    public static object Normalize(TagDef definition, object value)
    {
        if (SiemensValueCodec.IsSemantic(definition.DataType))
            return SiemensValueCodec.Normalize(definition.DataType, value);
        if (definition.DataType is IgwDataType.String or IgwDataType.WString)
        {
            string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            if (definition.StringLength is not > 0) return text;

            int capacity = definition.StringLength.Value;
            if (definition.DataType == IgwDataType.WString)
                return TruncateRunes(text, capacity);
            var encoding = definition.TextEncoding == PlcTextEncoding.Gb18030 ? Gb18030 : Encoding.UTF8;
            if (definition.DataType != IgwDataType.String || encoding.GetByteCount(text) <= capacity)
                return text;

            var buffer = new byte[capacity];
            encoding.GetEncoder().Convert(text.AsSpan(), buffer.AsSpan(), true,
                out int charsUsed, out _, out _);
            return text[..charsUsed];
        }

        // REST/JSON/MES 字段可能以 Double 或字符串进入运行时；OPC UA 等强类型协议要求 Variant
        // 与目标点声明类型完全一致，否则服务器会返回 BadTypeMismatch 或等待到超时。
        return definition.DataType switch
        {
            IgwDataType.Bool => Convert.ToBoolean(value, CultureInfo.InvariantCulture),
            IgwDataType.Byte => Convert.ToByte(value, CultureInfo.InvariantCulture),
            IgwDataType.Char => Convert.ToSByte(value, CultureInfo.InvariantCulture),
            IgwDataType.Int16 => Convert.ToInt16(value, CultureInfo.InvariantCulture),
            IgwDataType.UInt16 or IgwDataType.Bcd => Convert.ToUInt16(value, CultureInfo.InvariantCulture),
            IgwDataType.Int32 => Convert.ToInt32(value, CultureInfo.InvariantCulture),
            IgwDataType.UInt32 or IgwDataType.LBcd => Convert.ToUInt32(value, CultureInfo.InvariantCulture),
            IgwDataType.Int64 => Convert.ToInt64(value, CultureInfo.InvariantCulture),
            IgwDataType.UInt64 => Convert.ToUInt64(value, CultureInfo.InvariantCulture),
            IgwDataType.Float => Convert.ToSingle(value, CultureInfo.InvariantCulture),
            IgwDataType.Double => Convert.ToDouble(value, CultureInfo.InvariantCulture),
            _ => value,
        };
    }

    private static string TruncateRunes(string value, int capacity)
    {
        if (capacity <= 0) return "";
        int offset = 0;
        int count = 0;
        foreach (Rune rune in value.EnumerateRunes())
        {
            if (count++ >= capacity) break;
            offset += rune.Utf16SequenceLength;
        }
        return offset >= value.Length ? value : value[..offset];
    }
}
