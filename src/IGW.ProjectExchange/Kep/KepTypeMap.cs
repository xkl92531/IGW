using System.Text.Json;
using IGW.Abstractions;

namespace IGW.ProjectExchange.Kep;

public static class KepTypeMap
{
    public static (IgwDataType Type, bool IsArray, bool IsKnown) FromCode(int code)
    {
        bool isArray = code is >= 20 and <= 34;
        int scalar = isArray ? code - 20 : code;
        IgwDataType type = scalar switch
        {
            1 => IgwDataType.Bool,
            2 => IgwDataType.Char,
            3 => IgwDataType.Byte,
            4 => IgwDataType.Int16,
            5 => IgwDataType.UInt16,
            6 => IgwDataType.Int32,
            7 => IgwDataType.UInt32,
            8 => IgwDataType.Float,
            9 => IgwDataType.Double,
            10 => IgwDataType.String,
            11 => IgwDataType.Bcd,
            12 => IgwDataType.LBcd,
            13 => IgwDataType.Int64,
            14 => IgwDataType.UInt64,
            _ => IgwDataType.Int16,
        };
        return (type, isArray, scalar is >= 0 and <= 14);
    }

    public static int ToCode(TagDef tag)
    {
        if (tag.KepProperties is not null
            && tag.KepProperties.TryGetValue("servermain.TAG_DATA_TYPE", out JsonElement preserved)
            && preserved.TryGetInt32(out int code)
            && code is >= 0 and <= 34)
        {
            (IgwDataType type, bool isArray, _) = FromCode(code);
            if (type == tag.DataType && isArray == tag.IsArray) return code;
        }

        int scalar = tag.DataType switch
        {
            IgwDataType.Bool => 1,
            IgwDataType.Char => 2,
            IgwDataType.Byte or IgwDataType.ByteArray => 3,
            IgwDataType.Int16 => 4,
            IgwDataType.UInt16 => 5,
            IgwDataType.Int32 => 6,
            IgwDataType.UInt32 => 7,
            IgwDataType.Float => 8,
            IgwDataType.Double => 9,
            IgwDataType.String or IgwDataType.WString => 10,
            IgwDataType.Bcd => 11,
            IgwDataType.LBcd => 12,
            IgwDataType.Int64 => 13,
            IgwDataType.UInt64 => 14,
            _ => 0,
        };
        return tag.IsArray ? scalar + 20 : scalar;
    }
}

public static class KepScanRate
{
    public static int Normalize(int milliseconds) => Math.Clamp(milliseconds, 10, 86_400_000);
}
