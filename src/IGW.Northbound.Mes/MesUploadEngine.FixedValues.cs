using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using IGW.Abstractions;

namespace IGW.Northbound.Mes;

/// <summary>
/// 固定值：上报节点里的有类型固定值和点位字段放在 JSON 同一层；
/// 「固定值」节点的值合并进后面上报的 JSON 顶层(同名时以固定值为准)，不包进结构体。
/// </summary>
public sealed partial class MesUploadEngine
{
    internal static JsonObject FixedValuesJson(IReadOnlyList<MesFixedValue> items)
    {
        MesFixedValueTypes.Validate(items);
        var json = new JsonObject();
        foreach (var item in items) json[item.Name.Trim()] = MesFixedValueTypes.ToJson(item);
        return json;
    }

    internal static string MergeFixedValues(string body, IReadOnlyList<MesFixedValue> items)
    {
        JsonObject target;
        try { target = JsonNode.Parse(body) as JsonObject ?? throw new InvalidDataException("固定值只能合并进 JSON 对象报文。"); }
        catch (JsonException) { throw new InvalidDataException("固定值只能合并进 JSON 对象报文，当前报文不是合法 JSON。"); }
        foreach (var pair in FixedValuesJson(items))
            target[pair.Key] = pair.Value?.DeepClone();
        return target.ToJsonString();
    }

    /// <summary>平铺报文里逐个写出上报节点自己的固定值。</summary>
    private static void AppendFixedValues(StringBuilder sb, ref bool first, IReadOnlyList<MesFixedValue>? items)
    {
        if (items is not { Count: > 0 }) return;
        foreach (var pair in FixedValuesJson(items))
        {
            if (!first) sb.Append(','); first = false;
            sb.Append('"').Append(JsonEsc(pair.Key)).Append("\":").Append(pair.Value?.ToJsonString() ?? "null");
        }
    }

    /// <summary>报文模板里的 {字段名}：文本按转义后的原文替换(模板里自带引号)，其余按 JSON 字面量替换。</summary>
    private static string ReplaceFixedValues(string template, IReadOnlyList<MesFixedValue>? items)
    {
        if (items is not { Count: > 0 }) return template;
        MesFixedValueTypes.Validate(items);
        foreach (var item in items)
        {
            string raw = item.DataType is MesFixedValueTypes.Int or MesFixedValueTypes.Decimal or MesFixedValueTypes.Bool
                ? MesFixedValueTypes.ToJson(item)!.ToJsonString()
                : JsonEsc(item.Value ?? "");
            template = template.Replace("{" + item.Name.Trim() + "}", raw);
        }
        return template;
    }

    private static readonly JsonSerializerOptions RelaxedJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// 结构体成员写成 JSON 对象。成员路径带点(结构体里套结构体，如 Inner.A)时按层级嵌套成 {"Inner":{"A":…}}；
    /// 改过字段名的成员按字段名原样作为一个键。成员路径都不带点时和原来逐个写出完全一样。
    /// </summary>
    private static void AppendMemberObject(StringBuilder sb, IReadOnlyList<MesField> members, Func<MesField, object?> value)
    {
        static string KeyOf(MesField member) => string.IsNullOrWhiteSpace(member.FieldName) ? member.TagId : member.FieldName;
        if (!members.Any(member => string.IsNullOrWhiteSpace(member.FieldName) && member.TagId.Contains('.')))
        {
            sb.Append('{');
            bool first = true;
            foreach (var member in members)
            {
                if (!first) sb.Append(','); first = false;
                sb.Append('"').Append(JsonEsc(KeyOf(member))).Append("\":").Append(ToJson(value(member)));
            }
            sb.Append('}');
            return;
        }
        var root = new JsonObject();
        foreach (var member in members)
        {
            string[] path = string.IsNullOrWhiteSpace(member.FieldName) ? member.TagId.Split('.') : [member.FieldName];
            JsonNode? leaf = JsonNode.Parse(ToJson(value(member)));
            JsonObject parent = root;
            bool placed = true;
            for (int i = 0; i < path.Length - 1 && placed; i++)
            {
                if (parent[path[i]] is JsonObject child) parent = child;
                else if (parent.ContainsKey(path[i])) placed = false;   // 同名已经是值：退回按原路径作为一个键
                else { var created = new JsonObject(); parent[path[i]] = created; parent = created; }
            }
            if (placed && !parent.ContainsKey(path[^1])) parent[path[^1]] = leaf;
            else root[KeyOf(member)] = leaf;
        }
        sb.Append(root.ToJsonString(RelaxedJson));
    }
}
