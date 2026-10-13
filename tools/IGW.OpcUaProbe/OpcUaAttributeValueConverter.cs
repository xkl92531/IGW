using System.Collections;
using System.Xml;
using Opc.Ua;

namespace IGW.OpcUaProbe;

internal static class OpcUaAttributeValueConverter
{
    public static object? ConvertValue(object? value) => value switch
    {
        null => null,
        NodeId nodeId => nodeId.ToString(),
        ExpandedNodeId expanded => expanded.ToString(),
        QualifiedName name => new Dictionary<string, object?>
        {
            ["name"] = name.Name,
            ["namespaceIndex"] = name.NamespaceIndex,
        },
        LocalizedText text => new Dictionary<string, object?>
        {
            ["text"] = text.Text,
            ["locale"] = text.Locale,
        },
        StructureDefinition definition => Structure(definition),
        EnumDefinition definition => Enum(definition),
        ExtensionObject extension => new Dictionary<string, object?>
        {
            ["typeId"] = extension.TypeId?.ToString(),
            ["encoding"] = extension.Encoding.ToString(),
            ["bodyType"] = extension.Body?.GetType().FullName,
            ["body"] = ConvertValue(extension.Body),
        },
        byte[] bytes => System.Convert.ToBase64String(bytes),
        XmlElement element => element.OuterXml,
        Enum item => item.ToString(),
        DateTime date => date.ToUniversalTime().ToString("O"),
        DateTimeOffset date => date.ToUniversalTime().ToString("O"),
        Guid guid => guid.ToString("D"),
        string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => value,
        Array array => array.Cast<object?>().Select(ConvertValue).ToArray(),
        IEnumerable sequence => sequence.Cast<object?>().Select(ConvertValue).ToArray(),
        _ => value.ToString(),
    };

    public static IReadOnlyList<string>? DecodeFlags(uint attributeId, object? value)
    {
        ulong bits;
        try { bits = System.Convert.ToUInt64(value, System.Globalization.CultureInfo.InvariantCulture); }
        catch { return null; }
        if (attributeId is Attributes.AccessLevel or Attributes.UserAccessLevel or Attributes.AccessLevelEx)
        {
            var flags = new List<string>();
            Add(flags, bits, 0x01, "CurrentRead");
            Add(flags, bits, 0x02, "CurrentWrite");
            Add(flags, bits, 0x04, "HistoryRead");
            Add(flags, bits, 0x08, "HistoryWrite");
            Add(flags, bits, 0x10, "SemanticChange");
            Add(flags, bits, 0x20, "StatusWrite");
            Add(flags, bits, 0x40, "TimestampWrite");
            if (attributeId == Attributes.AccessLevelEx)
            {
                Add(flags, bits, 0x100, "NonatomicRead");
                Add(flags, bits, 0x200, "NonatomicWrite");
                Add(flags, bits, 0x400, "WriteFullArrayOnly");
            }
            return flags;
        }
        return null;
    }

    private static Dictionary<string, object?> Structure(StructureDefinition definition) => new()
    {
        ["defaultEncodingId"] = definition.DefaultEncodingId?.ToString(),
        ["baseDataType"] = definition.BaseDataType?.ToString(),
        ["structureType"] = definition.StructureType.ToString(),
        ["fields"] = definition.Fields?.Select(field => new Dictionary<string, object?>
        {
            ["name"] = field.Name,
            ["description"] = ConvertValue(field.Description),
            ["dataType"] = field.DataType?.ToString(),
            ["valueRank"] = field.ValueRank,
            ["arrayDimensions"] = field.ArrayDimensions?.ToArray(),
            ["maxStringLength"] = field.MaxStringLength,
            ["isOptional"] = field.IsOptional,
        }).ToArray() ?? [],
    };

    private static Dictionary<string, object?> Enum(EnumDefinition definition) => new()
    {
        ["fields"] = definition.Fields?.Select(field => new Dictionary<string, object?>
        {
            ["name"] = field.Name,
            ["value"] = field.Value,
            ["displayName"] = ConvertValue(field.DisplayName),
            ["description"] = ConvertValue(field.Description),
        }).ToArray() ?? [],
    };

    private static void Add(List<string> output, ulong bits, ulong mask, string name)
    {
        if ((bits & mask) != 0) output.Add(name);
    }
}
