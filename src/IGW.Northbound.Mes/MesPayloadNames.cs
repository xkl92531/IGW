using System.Text.Json;
using IGW.Abstractions;

namespace IGW.Northbound.Mes;

/// <summary>
/// 数据范围报文的键改名和数组成员去掉。在编译好的结构上按「结构路径」处理，拼报文、响应回写、结构预览看到的是同一套名字。
///
/// 结构路径按原名、相对数据范围来源书写：普通点 Counter，结构体成员 UDT1.A，数组元素成员 Cells[].Temp
/// (同一数组的所有元素共用一个 []，多维数组也只写一个 [])。没有改名和去掉成员时结构原样返回，报文与旧版完全一致。
/// </summary>
public static class MesPayloadNames
{
    public const string ElementMarker = "[]";

    public static CompiledMesSourceValue Apply(
        CompiledMesSourceValue value,
        IReadOnlyDictionary<string, string>? names,
        IReadOnlyCollection<string>? omit,
        string sourcePath)
    {
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, name) in names ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidDataException($"MES 数据范围 {sourcePath} 的字段 {key} 改名不能为空。");
            lookup[key.Trim()] = name.Trim();
        }
        var omitted = new HashSet<string>((omit ?? []).Where(key => !string.IsNullOrWhiteSpace(key)).Select(key => key.Trim()),
            StringComparer.OrdinalIgnoreCase);
        return lookup.Count == 0 && omitted.Count == 0 ? value : Rewrite(value, "", lookup, omitted, sourcePath);
    }

    /// <summary>结构路径：对象成员接在后面，数组元素统一记为 []。</summary>
    public static string Child(string parent, string name) =>
        parent.Length == 0 ? name : parent + "." + name;

    public static string Element(string parent) =>
        parent.EndsWith(ElementMarker, StringComparison.Ordinal) ? parent : parent + ElementMarker;

    private static CompiledMesSourceValue Rewrite(
        CompiledMesSourceValue value,
        string path,
        IReadOnlyDictionary<string, string> names,
        IReadOnlySet<string> omitted,
        string sourcePath)
    {
        switch (value)
        {
            case CompiledMesSourceObject obj:
            {
                var properties = new List<CompiledMesSourceProperty>(obj.Properties.Count);
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in obj.Properties)
                {
                    string childPath = Child(path, property.Name);
                    if (omitted.Contains(childPath)) continue;
                    string name = names.TryGetValue(childPath, out string? renamed) ? renamed : property.Name;
                    if (!used.Add(name))
                        throw new InvalidDataException($"MES 数据范围 {sourcePath} 改名后出现重复字段:{name}。");
                    properties.Add(new CompiledMesSourceProperty(name, Rewrite(property.Value, childPath, names, omitted, sourcePath)));
                }
                return new CompiledMesSourceObject(properties);
            }
            case CompiledMesSourceArray array:
            {
                string elementPath = Element(path);
                return new CompiledMesSourceArray(array.Elements
                    .Select(element => Rewrite(element, elementPath, names, omitted, sourcePath)).ToArray());
            }
            case CompiledMesSourceLogicalArray native when native.Definition.Struct is not null:
            {
                // 原生结构体数组：读回来的值自带结构定义，改名和去掉成员交给输出时按成员路径处理
                string prefix = Element(path) + ".";
                var memberNames = names.Where(item => item.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(item => item.Key[prefix.Length..], item => item.Value, StringComparer.OrdinalIgnoreCase);
                var memberOmits = omitted.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .Select(key => key[prefix.Length..]).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (memberNames.Count == 0 && memberOmits.Count == 0) return native;
                // 编译时的定义同步改名，响应回写按新名字找回成员
                var definition = Clone(native.Definition);
                RenameStruct(definition.Struct!, "", memberNames, memberOmits, sourcePath);
                return native with { Definition = definition, MemberNames = memberNames, OmittedMembers = memberOmits };
            }
            default:
                return value;
        }
    }

    private static void RenameStruct(
        StructDef definition, string path, IReadOnlyDictionary<string, string> names, IReadOnlySet<string> omitted, string sourcePath)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in definition.Fields)
        {
            string fieldPath = Child(path, field.Name);
            if (omitted.Contains(fieldPath)) continue;
            if (names.TryGetValue(fieldPath, out string? renamed)) field.JsonName = renamed;
            string wire = field.JsonName ?? field.Name;
            if (!used.Add(wire))
                throw new InvalidDataException($"MES 数据范围 {sourcePath} 改名后出现重复字段:{wire}。");
            if (field.Struct is not null) RenameStruct(field.Struct, fieldPath, names, omitted, sourcePath);
            if (field.Array?.Struct is not null) RenameStruct(field.Array.Struct, Element(fieldPath), names, omitted, sourcePath);
        }
    }

    // 定义在全工程共享，改名只作用于这一份编译结果
    private static ArrayDef Clone(ArrayDef definition) =>
        JsonSerializer.Deserialize<ArrayDef>(JsonSerializer.SerializeToUtf8Bytes(definition))!;
}
