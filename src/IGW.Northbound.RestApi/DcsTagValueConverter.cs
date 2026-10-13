using System.Globalization;
using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;

namespace IGW.Northbound.RestApi;

internal static class DcsTagValueConverter
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static bool TryMapDefinition(DcsTagDataType source, out TagDef definition, out string error)
    {
        var mapped = source switch
        {
            DcsTagDataType.String => (IgwDataType.String, false),
            DcsTagDataType.Boolean => (IgwDataType.Bool, false),
            DcsTagDataType.Char => (IgwDataType.Char, false),
            DcsTagDataType.Byte => (IgwDataType.Byte, false),
            DcsTagDataType.Short => (IgwDataType.Int16, false),
            DcsTagDataType.Word => (IgwDataType.UInt16, false),
            DcsTagDataType.Long => (IgwDataType.Int32, false),
            DcsTagDataType.DWord => (IgwDataType.UInt32, false),
            DcsTagDataType.Float => (IgwDataType.Float, false),
            DcsTagDataType.Double => (IgwDataType.Double, false),
            DcsTagDataType.Date => (IgwDataType.String, false),
            DcsTagDataType.StringArray => (IgwDataType.String, true),
            DcsTagDataType.BooleanArray => (IgwDataType.Bool, true),
            DcsTagDataType.ByteArray => (IgwDataType.ByteArray, false),
            DcsTagDataType.ShortArray => (IgwDataType.Int16, true),
            DcsTagDataType.WordArray => (IgwDataType.UInt16, true),
            DcsTagDataType.LongArray => (IgwDataType.Int32, true),
            DcsTagDataType.DWordArray => (IgwDataType.UInt32, true),
            DcsTagDataType.FloatArray => (IgwDataType.Float, true),
            _ => ((IgwDataType)(-1), false),
        };

        if ((int)mapped.Item1 < 0)
        {
            definition = new TagDef();
            error = $"暂不支持的 DCS 数据类型:{source}";
            return false;
        }

        definition = new TagDef
        {
            DataType = mapped.Item1,
            IsArray = mapped.Item2,
            Access = TagAccess.ReadWrite,
            ScanRateMs = 100,
            Metadata = new Dictionary<string, string>
            {
                [DcsTagMetadata.DataType] = source.ToString(),
            },
        };
        error = "";
        return true;
    }

    public static string GetDcsTypeName(TagDef definition)
    {
        if (definition.Metadata?.TryGetValue(DcsTagMetadata.DataType, out string? stored) == true
            && Enum.TryParse<DcsTagDataType>(stored, true, out var parsed)
            && parsed != DcsTagDataType.None)
            return parsed.ToString();

        if (definition.DataType == IgwDataType.ByteArray) return DcsTagDataType.ByteArray.ToString();
        if (definition.IsArray)
        {
            return definition.DataType switch
            {
                IgwDataType.String or IgwDataType.WString => DcsTagDataType.StringArray.ToString(),
                IgwDataType.Bool => DcsTagDataType.BooleanArray.ToString(),
                IgwDataType.Int16 or IgwDataType.Char => DcsTagDataType.ShortArray.ToString(),
                IgwDataType.UInt16 or IgwDataType.Bcd => DcsTagDataType.WordArray.ToString(),
                IgwDataType.Int32 => DcsTagDataType.LongArray.ToString(),
                IgwDataType.UInt32 or IgwDataType.LBcd => DcsTagDataType.DWordArray.ToString(),
                IgwDataType.Float or IgwDataType.Double => DcsTagDataType.FloatArray.ToString(),
                _ => DcsTagDataType.None.ToString(),
            };
        }

        return definition.DataType switch
        {
            IgwDataType.String or IgwDataType.WString => DcsTagDataType.String.ToString(),
            IgwDataType.Bool => DcsTagDataType.Boolean.ToString(),
            IgwDataType.Char => DcsTagDataType.Char.ToString(),
            IgwDataType.Byte => DcsTagDataType.Byte.ToString(),
            IgwDataType.Int16 => DcsTagDataType.Short.ToString(),
            IgwDataType.UInt16 or IgwDataType.Bcd => DcsTagDataType.Word.ToString(),
            IgwDataType.Int32 => DcsTagDataType.Long.ToString(),
            IgwDataType.UInt32 or IgwDataType.LBcd => DcsTagDataType.DWord.ToString(),
            IgwDataType.Float => DcsTagDataType.Float.ToString(),
            IgwDataType.Double => DcsTagDataType.Double.ToString(),
            _ => DcsTagDataType.None.ToString(),
        };
    }

    public static bool TryConvertWriteValue(JsonElement source, TagDef target, out object? value, out string error)
    {
        try
        {
            if (target.DataType == IgwDataType.ByteArray)
            {
                value = source.ValueKind == JsonValueKind.String
                    ? Convert.FromBase64String(source.GetString() ?? "")
                    : ConvertArray<byte>(source, target.DataType);
            }
            else if (target.IsArray)
            {
                value = target.Array is not null
                    ? ArrayJsonValueConverter.FromJson(target.Array, target.Array.Shape, source)
                    : target.DataType switch
                {
                    IgwDataType.Bool => ConvertArray<bool>(source, target.DataType),
                    IgwDataType.Char => ConvertArray<sbyte>(source, target.DataType),
                    IgwDataType.Byte => ConvertArray<byte>(source, target.DataType),
                    IgwDataType.Int16 => ConvertArray<short>(source, target.DataType),
                    IgwDataType.UInt16 or IgwDataType.Bcd => ConvertArray<ushort>(source, target.DataType),
                    IgwDataType.Int32 => ConvertArray<int>(source, target.DataType),
                    IgwDataType.UInt32 or IgwDataType.LBcd => ConvertArray<uint>(source, target.DataType),
                    IgwDataType.Int64 => ConvertArray<long>(source, target.DataType),
                    IgwDataType.UInt64 => ConvertArray<ulong>(source, target.DataType),
                    IgwDataType.Float => ConvertArray<float>(source, target.DataType),
                    IgwDataType.Double => ConvertArray<double>(source, target.DataType),
                    IgwDataType.String or IgwDataType.WString => ConvertArray<string>(source, target.DataType),
                    _ => throw new FormatException($"不支持数组类型 {target.DataType}"),
                };
            }
            else if (IsDate(target))
            {
                string text = source.ValueKind == JsonValueKind.String ? source.GetString() ?? "" : "";
                if (!DateTimeOffset.TryParse(text, Invariant, DateTimeStyles.AllowWhiteSpaces, out var date))
                    throw new FormatException("日期格式无效");
                value = date.ToString("O", Invariant);
            }
            else
            {
                value = ConvertScalar(source, target.DataType);
            }

            error = "";
            return true;
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or OverflowException)
        {
            value = null;
            error = $"value 无法转换为 {GetDcsTypeName(target)}:{ex.Message}";
            return false;
        }
    }

    public static object? ToWireValue(DataValue value) => value.Value is ArrayRuntimeValue array
        ? ArrayValueCodec.ToWireValues(array)
        : value.Value;

    private static bool IsDate(TagDef target) =>
        target.Metadata?.TryGetValue(DcsTagMetadata.DataType, out string? type) == true
        && type.Equals(DcsTagDataType.Date.ToString(), StringComparison.OrdinalIgnoreCase);

    private static T[] ConvertArray<T>(JsonElement source, IgwDataType type)
    {
        if (source.ValueKind != JsonValueKind.Array) throw new FormatException("值必须是数组");
        var result = new T[source.GetArrayLength()];
        int index = 0;
        foreach (JsonElement item in source.EnumerateArray())
        {
            try
            {
                result[index] = (T)ConvertScalar(item, type);
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException or OverflowException)
            {
                throw new FormatException($"索引 {index}:{ex.Message}", ex);
            }
            index++;
        }
        return result;
    }

    private static object ConvertScalar(JsonElement source, IgwDataType type)
    {
        if (source.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            or JsonValueKind.Object or JsonValueKind.Array)
            throw new FormatException("值必须是非空标量");

        string text = ScalarText(source);
        if (SiemensValueCodec.IsSemantic(type)) return SiemensValueCodec.Normalize(type, source);
        return type switch
        {
            IgwDataType.Bool => ParseBool(source, text),
            IgwDataType.Char => sbyte.TryParse(text, NumberStyles.Integer, Invariant, out sbyte i8)
                ? i8 : throw new OverflowException("Char 超出范围"),
            IgwDataType.Byte => byte.TryParse(text, NumberStyles.Integer, Invariant, out byte u8)
                ? u8 : throw new OverflowException("Byte 超出范围"),
            IgwDataType.Int16 => short.TryParse(text, NumberStyles.Integer, Invariant, out short i16)
                ? i16 : throw new OverflowException("Short 超出范围"),
            IgwDataType.UInt16 or IgwDataType.Bcd => ushort.TryParse(text, NumberStyles.Integer, Invariant, out ushort u16)
                ? u16 : throw new OverflowException("Word 超出范围"),
            IgwDataType.Int32 => int.TryParse(text, NumberStyles.Integer, Invariant, out int i32)
                ? i32 : throw new OverflowException("Long 超出范围"),
            IgwDataType.UInt32 or IgwDataType.LBcd => uint.TryParse(text, NumberStyles.Integer, Invariant, out uint u32)
                ? u32 : throw new OverflowException("DWord 超出范围"),
            IgwDataType.Int64 => long.TryParse(text, NumberStyles.Integer, Invariant, out long i64)
                ? i64 : throw new OverflowException("Int64 超出范围"),
            IgwDataType.UInt64 => ulong.TryParse(text, NumberStyles.Integer, Invariant, out ulong u64)
                ? u64 : throw new OverflowException("UInt64 超出范围"),
            IgwDataType.Float => float.TryParse(text, NumberStyles.Float, Invariant, out float f) && float.IsFinite(f)
                ? f : throw new FormatException("Float 格式无效"),
            IgwDataType.Double => double.TryParse(text, NumberStyles.Float, Invariant, out double d) && double.IsFinite(d)
                ? d : throw new FormatException("Double 格式无效"),
            IgwDataType.String or IgwDataType.WString => text,
            _ => throw new FormatException($"不支持类型 {type}"),
        };
    }

    private static bool ParseBool(JsonElement source, string text)
    {
        if (source.ValueKind is JsonValueKind.True or JsonValueKind.False) return source.GetBoolean();
        if (bool.TryParse(text, out bool boolean)) return boolean;
        if (double.TryParse(text, NumberStyles.Float, Invariant, out double number) && double.IsFinite(number))
            return number != 0;
        throw new FormatException("Boolean 格式无效");
    }

    private static string ScalarText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => throw new FormatException("值必须是标量"),
    };
}
