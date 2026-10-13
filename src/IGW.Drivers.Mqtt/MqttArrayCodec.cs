using System.Globalization;
using System.Text.Json;
using IGW.Abstractions;

namespace IGW.Drivers.Mqtt;

/// <summary>MQTT JSON 数组/对象数组与 IGW 逻辑数组之间的转换。</summary>
public static class MqttArrayCodec
{
    public static ArrayRuntimeValue ReplaceSlice(
        ArrayRuntimeValue whole, ArraySlice slice, ArrayRuntimeValue replacement)
    {
        var targetShape = slice.ValidateAndCreateShape(whole.Shape);
        if (whole.ElementType != replacement.ElementType || targetShape.Rank != replacement.Shape.Rank)
            throw new ArgumentException("替换数组的元素类型或维度不一致。", nameof(replacement));
        for (var i = 0; i < targetShape.Rank; i++)
            if (targetShape.Dimensions[i] != replacement.Shape.Dimensions[i])
                throw new ArgumentException("替换数组的形状与切片不一致。", nameof(replacement));

        return whole switch
        {
            PrimitiveArrayValue<bool> value => ReplacePrimitive(value, slice, Require<bool>(replacement)),
            PrimitiveArrayValue<byte> value => ReplacePrimitive(value, slice, Require<byte>(replacement)),
            PrimitiveArrayValue<sbyte> value => ReplacePrimitive(value, slice, Require<sbyte>(replacement)),
            PrimitiveArrayValue<short> value => ReplacePrimitive(value, slice, Require<short>(replacement)),
            PrimitiveArrayValue<ushort> value => ReplacePrimitive(value, slice, Require<ushort>(replacement)),
            PrimitiveArrayValue<int> value => ReplacePrimitive(value, slice, Require<int>(replacement)),
            PrimitiveArrayValue<uint> value => ReplacePrimitive(value, slice, Require<uint>(replacement)),
            PrimitiveArrayValue<long> value => ReplacePrimitive(value, slice, Require<long>(replacement)),
            PrimitiveArrayValue<ulong> value => ReplacePrimitive(value, slice, Require<ulong>(replacement)),
            PrimitiveArrayValue<float> value => ReplacePrimitive(value, slice, Require<float>(replacement)),
            PrimitiveArrayValue<double> value => ReplacePrimitive(value, slice, Require<double>(replacement)),
            PrimitiveArrayValue<string> value => ReplacePrimitive(value, slice, Require<string>(replacement)),
            PrimitiveArrayValue<byte[]> value => ReplacePrimitive(value, slice, Require<byte[]>(replacement)),
            StructArrayValue value => ReplaceStruct(value, slice,
                replacement as StructArrayValue ?? throw new ArgumentException("替换值不是结构体数组。", nameof(replacement))),
            _ => throw new NotSupportedException($"MQTT 不支持数组快照类型:{whole.GetType().Name}"),
        };
    }

    public static ArrayRuntimeValue Decode(string json, TagDef tag, StatusCode quality)
    {
        var definition = tag.Array ?? throw new ArgumentException("点位不是数组", nameof(tag));
        using var document = JsonDocument.Parse(json);
        var items = Flatten(document.RootElement, definition.ElementType == IgwDataType.Struct).ToArray();
        if (items.Length != definition.Shape.ElementCount)
            throw new InvalidDataException($"MQTT 数组元素数量不匹配(期望 {definition.Shape.ElementCount},实得 {items.Length})");
        var qualities = Enumerable.Repeat(quality, items.Length).ToArray();
        return Create(definition.ElementType, definition.Shape, items, definition.StringLength,
            definition.Struct, qualities);
    }

    public static string Encode(ArrayRuntimeValue value)
    {
        var dimensions = value.Shape.Dimensions.ToArray();
        var root = BuildNested(dimensions, 0, 0, offset => WireValue(value, offset)).Value;
        return JsonSerializer.Serialize(root);
    }

    private static (object? Value, int Next) BuildNested(IReadOnlyList<int> dimensions, int dimension,
        int offset, Func<int, object?> value)
    {
        if (dimension == dimensions.Count) return (value(offset), offset + 1);
        var result = new object?[dimensions[dimension]];
        for (var i = 0; i < result.Length; i++)
        {
            var child = BuildNested(dimensions, dimension + 1, offset, value);
            result[i] = child.Value;
            offset = child.Next;
        }
        return (result, offset);
    }

    private static object? WireValue(ArrayRuntimeValue value, int offset)
    {
        if (value is not StructArrayValue structured) return value.GetBoxedValue(offset);
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in structured.Schema.Fields)
        {
            var name = field.JsonName ?? field.Name;
            if (field.Array is { } memberArray)
            {
                var slice = structured.GetArrayMemberSlice(field, offset);
                row[name] = BuildNested(memberArray.Dimensions, 0, 0, i => WireValue(slice, i)).Value;
            }
            else row[name] = WireValue(structured.Members[field.Name], offset);
        }
        return row;
    }

    private static ArrayRuntimeValue Create(IgwDataType type, ArrayShape shape,
        IReadOnlyList<JsonElement> items, int? stringLength, StructDef? schema,
        IReadOnlyList<StatusCode> quality) => type switch
    {
        IgwDataType.Bool => PrimitiveArrayValue<bool>.Create(shape, items.Select(e => e.GetBoolean()).ToArray(), quality, type),
        IgwDataType.Byte => PrimitiveArrayValue<byte>.Create(shape, items.Select(e => e.GetByte()).ToArray(), quality, type),
        IgwDataType.Char => PrimitiveArrayValue<sbyte>.Create(shape, items.Select(e => e.GetSByte()).ToArray(), quality, type),
        IgwDataType.Int16 => PrimitiveArrayValue<short>.Create(shape, items.Select(e => e.GetInt16()).ToArray(), quality, type),
        IgwDataType.UInt16 or IgwDataType.Bcd => PrimitiveArrayValue<ushort>.Create(shape, items.Select(e => e.GetUInt16()).ToArray(), quality, type),
        IgwDataType.Int32 => PrimitiveArrayValue<int>.Create(shape, items.Select(e => e.GetInt32()).ToArray(), quality, type),
        IgwDataType.UInt32 or IgwDataType.LBcd => PrimitiveArrayValue<uint>.Create(shape, items.Select(e => e.GetUInt32()).ToArray(), quality, type),
        IgwDataType.Int64 => PrimitiveArrayValue<long>.Create(shape, items.Select(e => e.GetInt64()).ToArray(), quality, type),
        IgwDataType.UInt64 => PrimitiveArrayValue<ulong>.Create(shape, items.Select(e => e.GetUInt64()).ToArray(), quality, type),
        IgwDataType.Float => PrimitiveArrayValue<float>.Create(shape, items.Select(e => e.GetSingle()).ToArray(), quality, type),
        IgwDataType.Double => PrimitiveArrayValue<double>.Create(shape, items.Select(e => e.GetDouble()).ToArray(), quality, type),
        IgwDataType.String or IgwDataType.WString => PrimitiveArrayValue<string>.Create(shape,
            items.Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.ToString()).ToArray(), quality, type),
        IgwDataType.ByteArray => PrimitiveArrayValue<byte[]>.Create(shape,
            items.Select(e => e.ValueKind == JsonValueKind.String
                ? Convert.FromBase64String(e.GetString() ?? "") : e.EnumerateArray().Select(x => x.GetByte()).ToArray()).ToArray(), quality, type),
        IgwDataType.Struct => CreateStruct(shape, schema ?? throw new InvalidDataException("结构体数组缺少布局"), items, quality),
        _ => throw new NotSupportedException($"MQTT 数组元素类型尚不支持:{type}"),
    };

    private static StructArrayValue CreateStruct(ArrayShape outer, StructDef schema,
        IReadOnlyList<JsonElement> rows, IReadOnlyList<StatusCode> quality)
    {
        var members = new Dictionary<string, ArrayRuntimeValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in schema.Fields)
        {
            var values = rows.Select(row => Property(row, field.JsonName ?? field.Name, field.Name)).ToArray();
            if (field.Array is { } array)
            {
                var flattened = values.SelectMany(value => Flatten(value, array.ElementType == IgwDataType.Struct)).ToArray();
                var shape = ArrayShape.Create(outer.Dimensions.Concat(array.Dimensions).ToArray(),
                    outer.LowerBounds.Concat(array.LowerBounds).ToArray());
                members[field.Name] = Create(array.ElementType, shape, flattened, array.StringLength,
                    array.Struct, Repeat(quality, checked((int)array.Shape.ElementCount)));
            }
            else if (field.Struct is not null)
                members[field.Name] = Create(IgwDataType.Struct, outer, values, null, field.Struct, quality);
            else members[field.Name] = Create(field.DataType, outer, values, field.StringLength, null, quality);
        }
        return StructArrayValue.Create(outer, schema, members, quality);
    }

    private static JsonElement Property(JsonElement row, string preferred, string fallback)
    {
        if (row.TryGetProperty(preferred, out var value) || row.TryGetProperty(fallback, out value)) return value;
        foreach (var property in row.EnumerateObject())
            if (string.Equals(property.Name, preferred, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(property.Name, fallback, StringComparison.OrdinalIgnoreCase)) return property.Value;
        throw new InvalidDataException($"MQTT 结构体缺少字段:{preferred}");
    }

    private static IEnumerable<JsonElement> Flatten(JsonElement value, bool keepObjects)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                if (keepObjects && item.ValueKind == JsonValueKind.Object) yield return item.Clone();
                else foreach (var nested in Flatten(item, keepObjects)) yield return nested;
            }
        }
        else yield return value.Clone();
    }

    private static StatusCode[] Repeat(IReadOnlyList<StatusCode> quality, int count)
    {
        var result = new StatusCode[checked(quality.Count * count)];
        for (var i = 0; i < quality.Count; i++) Array.Fill(result, quality[i], i * count, count);
        return result;
    }

    private static PrimitiveArrayValue<T> ReplacePrimitive<T>(
        PrimitiveArrayValue<T> whole, ArraySlice slice, PrimitiveArrayValue<T> replacement) where T : notnull
    {
        var targetShape = slice.ValidateAndCreateShape(whole.Shape);
        var values = whole.Values.ToArray();
        var quality = whole.ElementQuality.ToArray();
        for (var sourceOffset = 0; sourceOffset < replacement.Count; sourceOffset++)
        {
            var targetOffset = checked((int)whole.Shape.GetLinearOffset(targetShape.GetIndices(sourceOffset)));
            values[targetOffset] = replacement.Values.Span[sourceOffset];
            quality[targetOffset] = replacement.ElementQuality.Span[sourceOffset];
        }
        return PrimitiveArrayValue<T>.Create(whole.Shape, values, quality, whole.ElementType);
    }

    private static StructArrayValue ReplaceStruct(
        StructArrayValue whole, ArraySlice slice, StructArrayValue replacement)
    {
        var members = new Dictionary<string, ArrayRuntimeValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in whole.Schema.Fields)
        {
            if (!replacement.Members.TryGetValue(field.Name, out var incoming))
                throw new ArgumentException($"替换结构体缺少成员:{field.Name}", nameof(replacement));
            var memberSlice = field.Array is null ? slice : StructArrayValue.ExpandMemberSlice(slice, field.Array);
            members[field.Name] = ReplaceSlice(whole.Members[field.Name], memberSlice, incoming);
        }

        var targetShape = slice.ValidateAndCreateShape(whole.Shape);
        var quality = whole.ElementQuality.ToArray();
        for (var sourceOffset = 0; sourceOffset < replacement.Count; sourceOffset++)
        {
            var targetOffset = checked((int)whole.Shape.GetLinearOffset(targetShape.GetIndices(sourceOffset)));
            quality[targetOffset] = replacement.ElementQuality.Span[sourceOffset];
        }
        return StructArrayValue.Create(whole.Shape, whole.Schema, members, quality);
    }

    private static PrimitiveArrayValue<T> Require<T>(ArrayRuntimeValue value) where T : notnull =>
        value as PrimitiveArrayValue<T>
        ?? throw new ArgumentException($"替换数组 CLR 元素类型应为 {typeof(T).Name}。", nameof(value));
}
