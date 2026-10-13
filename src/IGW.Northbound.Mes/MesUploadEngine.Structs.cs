using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using IGW.Abstractions;
using IGW.Core;

namespace IGW.Northbound.Mes;

/// <summary>
/// 字段/结构映射里的结构体按层拼报文：结构体里可以再套子结构体(对象或对象数组)，
/// 子结构体的 BaseGroup 写相对上一层元素的路径。UDT 数组(一个点位里带完整结构)的成员
/// 按 数组点[下标].成员[下标].成员 这样的路径从读回来的整块数组值里取。
/// </summary>
public sealed partial class MesUploadEngine
{
    /// <summary>写出结构体的值：对象 {…}、值数组 […] 或对象数组 [{…}]。basePath 是它在工程里的绝对路径。</summary>
    private void AppendStructValue(StringBuilder sb, MesStruct st, string basePath, Func<string, double, object?> at, bool automatic = true)
    {
        if (st.Kind == "Object") { AppendStructElement(sb, st, basePath, at, automatic); return; }
        if (st.Kind == "List")   // 同级标量成员 → 值数组 [v1,v2,…]
        {
            sb.Append('[');
            bool firstValue = true;
            foreach (var member in st.Members)
            {
                if (automatic && !string.IsNullOrWhiteSpace(member.ParameterCode)) continue;
                if (!firstValue) sb.Append(','); firstValue = false;
                sb.Append(ToJson(at($"{basePath}.{member.TagId}", member.Scale)));
            }
            sb.Append(']');
            return;
        }
        sb.Append('[');
        bool first = true;
        foreach (string element in StructElementPaths(st, basePath))
        {
            if (!first) sb.Append(','); first = false;
            AppendStructElement(sb, st, element, at, automatic);
        }
        sb.Append(']');
    }

    /// <summary>对象数组每个元素的路径：枚举子组按标记组扫描，其余按 起始下标..起始下标+数量-1。</summary>
    private IEnumerable<string> StructElementPaths(MesStruct st, string basePath)
    {
        if (IsEnumStruct(st))
            return EnumStructElements(new MesStruct
            {
                Kind = st.Kind, CountMode = st.CountMode, BaseGroup = basePath,
                StartIndex = st.StartIndex, FixedCount = st.FixedCount,
            }, _hub.ListTagIds());
        int count = st.FixedCount;
        if (st.CountMode == "Tag" && !string.IsNullOrWhiteSpace(st.CountTagId) &&
            _hub.TryGet(st.CountTagId, out var countValue) && countValue.Value is not null &&
            int.TryParse(Convert.ToString(countValue.Value, CultureInfo.InvariantCulture), out int fromTag)) count = fromTag;
        return Enumerable.Range(st.StartIndex, Math.Max(0, count)).Select(index => $"{basePath}[{index}]");
    }

    /// <summary>一个元素写成对象：成员，再加上子结构体(按别名作为键，套在里面)。</summary>
    private void AppendStructElement(StringBuilder sb, MesStruct st, string elementPath, Func<string, double, object?> at, bool automatic = true)
    {
        var members = automatic ? st.Members.Where(member => string.IsNullOrWhiteSpace(member.ParameterCode)).ToList() : st.Members;
        if (st.Structs is not { Count: > 0 })
        {
            AppendMemberObject(sb, members, member => at($"{elementPath}.{member.TagId}", member.Scale));
            return;
        }
        var own = new StringBuilder();
        AppendMemberObject(own, members, member => at($"{elementPath}.{member.TagId}", member.Scale));
        var element = JsonNode.Parse(own.ToString()) as JsonObject ?? new JsonObject();
        foreach (var child in st.Structs)
        {
            if (automatic && child.AsDictionary) continue;
            if (string.IsNullOrWhiteSpace(child.AliasName) || child.Members.Count == 0 && child.Structs.Count == 0) continue;
            var part = new StringBuilder();
            AppendStructValue(part, child, $"{elementPath}.{child.BaseGroup}", at, automatic);
            element[child.AliasName] = JsonNode.Parse(part.ToString());
        }
        sb.Append(element.ToJsonString(RelaxedJson));
    }

    /// <summary>
    /// 取报文里一个路径的值。工程里真有这个点位就直接读；否则看它是不是 UDT 数组里的成员
    /// (如 DB.CellModeGlueDataArray[0].GlueData.CellGlueData[1].GlueLength_L)，从整块数组值里按层取。
    /// wires 缓存本次拼报文已经展开过的数组，同一个数组的几十个成员只展开一次。
    /// </summary>
    private object? ReadPayloadPath(string path, double scale, ISet<string> badTags, Dictionary<string, object?> wires)
    {
        if (string.IsNullOrWhiteSpace(path) || _hub.TryGetDef(path, out _)) return CurAt(path, scale, badTags);
        if (!TryReadArrayMember(path, badTags, wires, out object? value)) return CurAt(path, scale, badTags);
        if (scale != 1.0 && scale != 0 && value is not (null or string or bool or IList or IDictionary) &&
            double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out double number))
            return number * scale;
        return value;
    }

    private bool TryReadArrayMember(string path, ISet<string> badTags, Dictionary<string, object?> wires, out object? value)
    {
        value = null;
        var parts = SplitIndexedPath(path);
        if (parts is null) return false;
        string tagId = "";
        for (int i = 0; i < parts.Count; i++)
        {
            tagId = i == 0 ? parts[0].Name : tagId + "." + parts[i].Name;
            if (!_hub.TryGetDef(tagId, out var definition) || definition.Array is null)
            {
                if (parts[i].Indices.Length > 0) return false;   // 下标只能跟在数组后面
                continue;
            }
            if (!wires.TryGetValue(tagId, out object? wire))
            {
                object? raw = MesPayloadValuePolicy.Read(_hub, tagId, out bool bad);
                if (bad) badTags.Add(tagId);
                wires[tagId] = wire = raw is ArrayRuntimeValue array ? MesArrayProjection.ToWireValue(array) : null;
            }
            object? current = Index(wire, parts[i].Indices, definition.Array.LowerBounds);
            StructDef? schema = definition.Array.Struct;
            for (int j = i + 1; j < parts.Count; j++)
            {
                if (current is not IDictionary<string, object?> map || schema is null) { value = null; return true; }
                var part = parts[j];
                var field = schema.Fields.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, part.Name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(candidate.JsonName, part.Name, StringComparison.OrdinalIgnoreCase));
                if (field is null) { value = null; return true; }
                current = map.TryGetValue(field.JsonName ?? field.Name, out object? member) ? member : null;
                if (field.Array is not null)
                {
                    current = Index(current, part.Indices, field.Array.LowerBounds);
                    schema = field.Array.Struct;
                }
                else
                {
                    if (part.Indices.Length > 0) { value = null; return true; }
                    schema = field.Struct;
                }
            }
            value = current;
            return true;
        }
        return false;
    }

    /// <summary>按 PLC 下标取数组元素(下标减去下界)；多维数组的值是一层套一层的列表。</summary>
    private static object? Index(object? value, int[] indices, IReadOnlyList<int>? lowerBounds)
    {
        for (int dimension = 0; dimension < indices.Length; dimension++)
        {
            if (value is not IList list) return null;
            int lower = lowerBounds is { Count: > 0 } && dimension < lowerBounds.Count ? lowerBounds[dimension] : 0;
            int offset = indices[dimension] - lower;
            if (offset < 0 || offset >= list.Count) return null;
            value = list[offset];
        }
        return value;
    }

    private readonly record struct PathPart(string Name, int[] Indices);

    /// <summary>A.B[1].C[2,3][4] → (A)(B,[1])(C,[2,3,4])；格式不对返回 null。</summary>
    private static List<PathPart>? SplitIndexedPath(string path)
    {
        var parts = new List<PathPart>();
        foreach (string segment in path.Split('.'))
        {
            int bracket = segment.IndexOf('[');
            if (bracket == 0 || segment.Length == 0) return null;
            if (bracket < 0) { parts.Add(new PathPart(segment, [])); continue; }
            var indices = new List<int>();
            string rest = segment[bracket..];
            while (rest.Length > 0)
            {
                int close = rest.IndexOf(']');
                if (rest[0] != '[' || close < 0) return null;
                foreach (string item in rest[1..close].Split(',', StringSplitOptions.TrimEntries))
                {
                    if (!int.TryParse(item, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)) return null;
                    indices.Add(index);
                }
                rest = rest[(close + 1)..];
            }
            parts.Add(new PathPart(segment[..bracket], [.. indices]));
        }
        return parts;
    }
}
