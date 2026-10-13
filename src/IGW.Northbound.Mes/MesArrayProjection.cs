using IGW.Abstractions;
using IGW.Core;

namespace IGW.Northbound.Mes;

public static class MesArrayProjection
{
    public static ArrayRuntimeValue Select(
        ArrayRuntimeValue value,
        ArraySlice slice,
        IReadOnlyList<string> memberPath)
    {
        ArgumentNullException.ThrowIfNull(value);
        var selected = value.Slice(slice);
        foreach (var memberName in memberPath)
        {
            if (selected is not StructArrayValue structure)
                throw new InvalidDataException($"MES 数组成员路径不是结构体:{memberName}");
            var field = structure.Schema.Fields.FirstOrDefault(field =>
                string.Equals(field.Name, memberName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(field.JsonName, memberName, StringComparison.OrdinalIgnoreCase));
            if (field is null || !structure.Members.TryGetValue(field.Name, out var next))
                throw new InvalidDataException($"MES 数组成员不存在:{memberName}");
            selected = next;
        }
        return selected;
    }

    public static object? ToWireValue(ArrayRuntimeValue value) =>
        BuildNested(value, value.Shape.Dimensions, 0, 0).Value;

    /// <summary>
    /// 带成员改名、去掉成员的输出。成员路径按点位定义里的原名写(相对元素，如 Ok、Sub.X、Items[].Y)。
    /// 读回来的值自带结构定义，所以改名和去掉成员要在这里按路径处理，不能只改编译时的定义。
    /// </summary>
    public static object? ToWireValue(
        ArrayRuntimeValue value,
        IReadOnlyDictionary<string, string>? names,
        IReadOnlySet<string>? omitted)
    {
        if ((names is null || names.Count == 0) && (omitted is null || omitted.Count == 0)) return ToWireValue(value);
        return BuildNested(value, value.Shape.Dimensions, 0, 0,
            (array, offset) => Element(array, offset, "", names ?? new Dictionary<string, string>(), omitted ?? new HashSet<string>())).Value;
    }

    private static object? Element(
        ArrayRuntimeValue value, int offset, string path,
        IReadOnlyDictionary<string, string> names, IReadOnlySet<string> omitted)
    {
        if (value is not StructArrayValue structure || value.ElementQuality.Span[offset].IsBad)
            return ArrayValueCodec.ToWireValue(value, offset);
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in structure.Schema.Fields)
        {
            string fieldPath = path.Length == 0 ? field.Name : path + "." + field.Name;
            if (omitted.Contains(fieldPath)) continue;
            string key = names.TryGetValue(fieldPath, out string? renamed) ? renamed : field.JsonName ?? field.Name;
            if (field.Array is null)
            {
                result[key] = Element(structure.Members[field.Name], offset, fieldPath, names, omitted);
                continue;
            }
            var slice = structure.GetArrayMemberSlice(field, offset);
            var items = new object?[slice.Count];
            for (int i = 0; i < items.Length; i++) items[i] = Element(slice, i, fieldPath + "[]", names, omitted);
            result[key] = items;
        }
        return result;
    }

    public static IReadOnlyList<JsonArrayElement> FlattenResponseElements(
        System.Text.Json.JsonElement value,
        ArrayShape shape)
    {
        var elements = new List<System.Text.Json.JsonElement>();
        Flatten(value, elements);
        var count = Math.Min(elements.Count, checked((int)shape.ElementCount));
        var result = new List<JsonArrayElement>(count);
        for (var i = 0; i < count; i++)
            result.Add(new JsonArrayElement(shape.GetIndices(i), elements[i]));
        return result;
    }

    public static string Alias(string tagId, IReadOnlyList<int> indices, IEnumerable<string>? memberPath = null)
    {
        var suffix = string.Join(',', indices);
        var members = memberPath?.ToArray() ?? [];
        return members.Length == 0 ? $"{tagId}[{suffix}]" : $"{tagId}[{suffix}].{string.Join('.', members)}";
    }

    private static (object? Value, int Next) BuildNested(
        ArrayRuntimeValue value,
        IReadOnlyList<int> dimensions,
        int dimension,
        int offset,
        Func<ArrayRuntimeValue, int, object?>? element = null)
    {
        if (dimension == dimensions.Count)
            return ((element ?? ArrayValueCodec.ToWireValue)(value, offset), offset + 1);
        var result = new object?[dimensions[dimension]];
        for (var i = 0; i < result.Length; i++)
        {
            var child = BuildNested(value, dimensions, dimension + 1, offset, element);
            result[i] = child.Value;
            offset = child.Next;
        }
        return (result, offset);
    }

    private static void Flatten(System.Text.Json.JsonElement value, List<System.Text.Json.JsonElement> result)
    {
        if (value.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            result.Add(value);
            return;
        }
        foreach (var child in value.EnumerateArray()) Flatten(child, result);
    }
}

public sealed record JsonArrayElement(IReadOnlyList<int> Indices, System.Text.Json.JsonElement Value);
