using System.Globalization;
using IGW.Abstractions;

namespace IGW.Core;

/// <summary>为虚拟数组元素路径构造一个单元素切片写值，不修改 DataHub 中的不可变快照。</summary>
public static class ArrayValueEditor
{
    public static ArrayRuntimeValue ReplaceSingle(
        ArrayRuntimeValue current,
        IReadOnlyList<int> indices,
        IReadOnlyList<string> memberPath,
        object rawValue,
        PlcTextEncoding textEncoding = PlcTextEncoding.Utf8)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (indices.Count != current.Shape.Rank)
            throw new IndexOutOfRangeException("数组索引数量与维度不一致。");

        var selected = current.Slice(new ArraySlice(indices, Enumerable.Repeat(1, indices.Count).ToArray()));
        return memberPath.Count == 0
            ? ReplacePrimitive(selected, rawValue, null, textEncoding)
            : ReplaceStructMember(selected, memberPath, 0, rawValue, textEncoding);
    }

    private static ArrayRuntimeValue ReplaceStructMember(
        ArrayRuntimeValue value,
        IReadOnlyList<string> memberPath,
        int depth,
        object rawValue,
        PlcTextEncoding textEncoding)
    {
        if (value is not StructArrayValue structure)
            throw new InvalidDataException($"成员路径 {string.Join('.', memberPath)} 的父级不是结构体。");

        string requested = memberPath[depth];
        var field = structure.Schema.Fields.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, requested, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate.JsonName, requested, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"结构体成员 {requested} 不存在。");
        if (field.Array is not null)
            throw new NotSupportedException($"结构体成员数组 {field.Name} 需要使用数组切片写入。");

        var members = new Dictionary<string, ArrayRuntimeValue>(structure.Members, StringComparer.OrdinalIgnoreCase);
        var existing = members[field.Name];
        if (depth + 1 < memberPath.Count)
            members[field.Name] = ReplaceStructMember(existing, memberPath, depth + 1, rawValue, textEncoding);
        else
            members[field.Name] = ReplacePrimitive(existing, rawValue, field, textEncoding);

        return StructArrayValue.Create(structure.Shape, structure.Schema, members,
            structure.ElementQuality.Span.ToArray());
    }

    private static ArrayRuntimeValue ReplacePrimitive(
        ArrayRuntimeValue current,
        object rawValue,
        StructFieldDef? field,
        PlcTextEncoding textEncoding)
    {
        if (current.Count != 1)
            throw new InvalidDataException("虚拟数组元素写入必须是单元素切片。");

        object normalized = rawValue;
        if (SiemensValueCodec.IsSemantic(current.ElementType))
            normalized = SiemensValueCodec.Normalize(current.ElementType, rawValue);
        else if (current.ElementType is IgwDataType.String or IgwDataType.WString)
        {
            normalized = PlcWriteValueNormalizer.Normalize(new TagDef
            {
                DataType = current.ElementType,
                StringLength = field?.StringLength,
                TextEncoding = textEncoding,
            }, Convert.ToString(rawValue, CultureInfo.InvariantCulture) ?? "");
        }

        var quality = current.ElementQuality.Span.ToArray();
        return current switch
        {
            PrimitiveArrayValue<bool> => Create<bool>(current, normalized, quality),
            PrimitiveArrayValue<byte> => Create<byte>(current, normalized, quality),
            PrimitiveArrayValue<sbyte> => Create<sbyte>(current, normalized, quality),
            PrimitiveArrayValue<char> => Create<char>(current, normalized, quality),
            PrimitiveArrayValue<short> => Create<short>(current, normalized, quality),
            PrimitiveArrayValue<ushort> => Create<ushort>(current, normalized, quality),
            PrimitiveArrayValue<int> => Create<int>(current, normalized, quality),
            PrimitiveArrayValue<uint> => Create<uint>(current, normalized, quality),
            PrimitiveArrayValue<long> => Create<long>(current, normalized, quality),
            PrimitiveArrayValue<ulong> => Create<ulong>(current, normalized, quality),
            PrimitiveArrayValue<float> => Create<float>(current, normalized, quality),
            PrimitiveArrayValue<double> => Create<double>(current, normalized, quality),
            PrimitiveArrayValue<string> => PrimitiveArrayValue<string>.Create(
                current.Shape, [Convert.ToString(normalized, CultureInfo.InvariantCulture) ?? ""], quality,
                current.ElementType),
            _ => throw new NotSupportedException($"不支持写入数组元素类型:{current.ElementType}"),
        };
    }

    private static PrimitiveArrayValue<T> Create<T>(
        ArrayRuntimeValue current, object rawValue, IReadOnlyList<StatusCode> quality) where T : notnull
    {
        object converted = typeof(T) == typeof(bool)
            ? ParseBoolean(rawValue)
            : typeof(T) == typeof(char)
                ? ParseCharacter(rawValue)
                : Convert.ChangeType(rawValue, typeof(T), CultureInfo.InvariantCulture);
        return PrimitiveArrayValue<T>.Create(current.Shape, [(T)converted], quality, current.ElementType);
    }

    private static bool ParseBoolean(object value)
    {
        if (value is bool boolean) return boolean;
        string text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? "";
        if (bool.TryParse(text, out boolean)) return boolean;
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long number))
            return number != 0;
        throw new FormatException($"{text} 不能转换为 Bool。");
    }

    private static char ParseCharacter(object value)
    {
        if (value is char character) return character;
        string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        if (text.Length == 1) return text[0];
        if (byte.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte number))
            return (char)number;
        throw new FormatException($"{text} 不能转换为 Char。");
    }
}
