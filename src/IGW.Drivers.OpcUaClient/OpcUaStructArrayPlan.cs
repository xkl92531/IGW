using System.Globalization;
using System.Text;
using IGW.Abstractions;
using Opc.Ua;

namespace IGW.Drivers.OpcUaClient;

/// <summary>
/// 结构体数组的整读计划。西门子把 DB 里的 UDT 数组展开成
/// <c>ns=3;s="DB"."Block"."Array"[3]."Member"</c> 这样的逐个叶子节点，一个 208×11 的数组就是 2288 个节点；
/// S7-1500 逐点读取约 1.6–2 ms/点，一次上报就要占住 PLC 好几秒。
/// 数组本身在服务器上是一个 UDT 数组节点，读一次就能拿到全部元素，再按结构定义拆回各点位。
/// </summary>
internal sealed record OpcUaStructArrayGroup(
    string ArrayAddress,
    int FirstIndex,
    int LastIndex,
    IReadOnlyList<(TagBinding Binding, int Index, string Member)> Members)
{
    public int ElementCount => LastIndex - FirstIndex + 1;
}

internal static class OpcUaStructArrayPlan
{
    /// <summary>少于该点数的数组不值得整读：读回整个数组的代价可能超过逐点读。</summary>
    internal const int MinimumGroupTags = 16;
    /// <summary>一次整读最多覆盖的元素数；更大的数组按索引分段，避免单个响应过大。</summary>
    internal const int MaxElementsPerRead = 512;

    /// <summary>
    /// 把一批点位按“同一个数组 + 连续索引段”分组。地址形如 <c>前缀[索引]."成员"</c> 才会成组；
    /// 其余点位原样返回，仍走逐点读取。
    /// </summary>
    internal static (IReadOnlyList<OpcUaStructArrayGroup> Groups, List<TagBinding> Others) Plan(
        IReadOnlyList<TagBinding> tags)
    {
        var byArray = new Dictionary<string, List<(TagBinding Binding, int Index, string Member)>>(StringComparer.Ordinal);
        var rest = new List<TagBinding>();
        foreach (var tag in tags)
        {
            if (TryParseElement(tag.Def.Address, out string arrayAddress, out int index, out string member))
            {
                if (!byArray.TryGetValue(arrayAddress, out var list)) byArray[arrayAddress] = list = [];
                list.Add((tag, index, member));
            }
            else rest.Add(tag);
        }

        var groups = new List<OpcUaStructArrayGroup>();
        foreach (var pair in byArray)
        {
            if (pair.Value.Count < MinimumGroupTags) { rest.AddRange(pair.Value.Select(item => item.Binding)); continue; }
            var ordered = pair.Value.OrderBy(item => item.Index).ToArray();
            int start = 0;
            while (start < ordered.Length)
            {
                int first = ordered[start].Index;
                int end = start;
                // 一段最多覆盖 MaxElementsPerRead 个元素；索引不连续也没关系，段内的空洞一起读回来即可。
                while (end + 1 < ordered.Length && ordered[end + 1].Index - first < MaxElementsPerRead) end++;
                var slice = ordered[start..(end + 1)];
                if (slice.Length >= MinimumGroupTags)
                    groups.Add(new OpcUaStructArrayGroup(pair.Key, first, slice[^1].Index, slice));
                else rest.AddRange(slice.Select(item => item.Binding));
                start = end + 1;
            }
        }
        return (groups, rest);
    }

    /// <summary>解析 <c>ns=3;s="DB"."Block"."Array"[3]."Member"</c>：返回数组节点地址、元素索引和成员名。</summary>
    internal static bool TryParseElement(string? address, out string arrayAddress, out int index, out string member)
    {
        arrayAddress = ""; index = 0; member = "";
        if (string.IsNullOrEmpty(address)) return false;
        int lastDot = address.LastIndexOf('.');
        if (lastDot <= 0 || lastDot == address.Length - 1) return false;
        member = address[(lastDot + 1)..].Trim('"');
        if (member.Length == 0 || member.Contains('[')) return false;
        string head = address[..lastDot];
        if (head.Length == 0 || head[^1] != ']') return false;
        int open = head.LastIndexOf('[');
        if (open <= 0) return false;
        if (!int.TryParse(head.AsSpan(open + 1, head.Length - open - 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out index)
            || index < 0) return false;
        arrayAddress = head[..open];
        return arrayAddress.Length > 0;
    }

    /// <summary>整读请求：一个数组节点 + 元素索引范围。</summary>
    internal static ReadValueId ReadRequest(OpcUaStructArrayGroup group, NodeId node) => new()
    {
        NodeId = node,
        AttributeId = Attributes.Value,
        IndexRange = group.FirstIndex == group.LastIndex
            ? group.FirstIndex.ToString(CultureInfo.InvariantCulture)
            : $"{group.FirstIndex}:{group.LastIndex}",
    };
}

/// <summary>按服务器给出的结构定义解码 OPC UA 二进制结构体。</summary>
internal sealed class OpcUaStructDecoder
{
    private readonly StructureDefinition _definition;
    private readonly IReadOnlyDictionary<NodeId, StructureDefinition> _nested;

    public OpcUaStructDecoder(StructureDefinition definition, IReadOnlyDictionary<NodeId, StructureDefinition> nested)
    {
        _definition = definition;
        _nested = nested;
    }

    public IReadOnlyCollection<string> FieldNames =>
        _definition.Fields?.Select(item => item.Name).Where(name => !string.IsNullOrEmpty(name)).ToArray() ?? [];

    /// <summary>解出一个元素的全部成员值；结构体带可选字段(Union/Optional)时返回 null，由调用方退回逐点读取。</summary>
    public Dictionary<string, object?>? TryDecode(byte[] body)
    {
        if (_definition.StructureType is not (StructureType.Structure or StructureType.StructureWithSubtypedValues)) return null;
        try
        {
            var reader = new BinaryReader(new MemoryStream(body, writable: false), Encoding.UTF8, leaveOpen: false);
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var field in _definition.Fields ?? [])
            {
                if (field.IsOptional) return null;
                object? value = ReadField(reader, field);
                if (value is MissingValue) return null;
                if (!string.IsNullOrEmpty(field.Name)) values[field.Name] = value;
            }
            return values;
        }
        catch (Exception exception) when (exception is EndOfStreamException or IOException or ArgumentException)
        {
            return null;
        }
    }

    private sealed class MissingValue { public static readonly MissingValue Instance = new(); }

    private object? ReadField(BinaryReader reader, StructureField field)
    {
        if (field.ValueRank > 0)
        {
            int count = reader.ReadInt32();
            if (count < 0) return null;
            var items = new object?[count];
            for (int i = 0; i < count; i++)
            {
                object? item = ReadScalar(reader, field);
                if (item is MissingValue) return MissingValue.Instance;
                items[i] = item;
            }
            return items;
        }
        return ReadScalar(reader, field);
    }

    private object? ReadScalar(BinaryReader reader, StructureField field)
    {
        if (field.DataType.NamespaceIndex == 0 && field.DataType.IdType == IdType.Numeric)
        {
            switch ((uint)field.DataType.Identifier)
            {
                case DataTypes.Boolean: return reader.ReadByte() != 0;
                case DataTypes.SByte: return reader.ReadSByte();
                case DataTypes.Byte: return reader.ReadByte();
                case DataTypes.Int16: return reader.ReadInt16();
                case DataTypes.UInt16: return reader.ReadUInt16();
                case DataTypes.Int32: return reader.ReadInt32();
                case DataTypes.UInt32: return reader.ReadUInt32();
                case DataTypes.Int64: return reader.ReadInt64();
                case DataTypes.UInt64: return reader.ReadUInt64();
                case DataTypes.Float: return reader.ReadSingle();
                case DataTypes.Double: return reader.ReadDouble();
                case DataTypes.DateTime: return DateTime.FromFileTimeUtc(reader.ReadInt64());
                case DataTypes.String: return ReadString(reader);
                case DataTypes.ByteString: return ReadByteString(reader);
            }
        }
        // 嵌套 UDT：服务器给出定义时继续解码，否则整组退回逐点读取。
        if (_nested.TryGetValue(field.DataType, out var nested))
        {
            var decoder = new OpcUaStructDecoder(nested, _nested);
            var members = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var child in nested.Fields ?? [])
            {
                if (child.IsOptional) return MissingValue.Instance;
                object? value = decoder.ReadField(reader, child);
                if (value is MissingValue) return MissingValue.Instance;
                if (!string.IsNullOrEmpty(child.Name)) members[child.Name] = value;
            }
            return members;
        }
        return MissingValue.Instance;
    }

    private static string? ReadString(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length < 0) return null;
        return Encoding.UTF8.GetString(reader.ReadBytes(length));
    }

    private static byte[]? ReadByteString(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        return length < 0 ? null : reader.ReadBytes(length);
    }
}
