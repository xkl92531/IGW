using IGW.Abstractions;

namespace IGW.Core;

/// <summary>把不可变数组快照按北向 JSON/MES 质量语义投影到边界对象。</summary>
public static class ArrayValueCodec
{
    public static object?[] ToWireValues(ArrayRuntimeValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var result = new object?[value.Count];
        for (var i = 0; i < result.Length; i++) result[i] = ToWireValue(value, i);
        return result;
    }

    public static object? ToWireValue(ArrayRuntimeValue value, int linearOffset)
    {
        if ((uint)linearOffset >= (uint)value.Count)
            throw new IndexOutOfRangeException($"数组元素 {linearOffset} 超出范围。");

        var quality = value.ElementQuality.Span[linearOffset];
        if (quality.IsBad)
            return value.ElementType is IgwDataType.String or IgwDataType.WString ? "Bad" : null;

        if (value is not StructArrayValue structure)
            return value.GetBoxedValue(linearOffset);

        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in structure.Schema.Fields)
            result[field.JsonName ?? field.Name] = field.Array is null
                ? ToWireValue(structure.Members[field.Name], linearOffset)
                : ToWireValues(structure.GetArrayMemberSlice(field, linearOffset));
        return result;
    }
}
