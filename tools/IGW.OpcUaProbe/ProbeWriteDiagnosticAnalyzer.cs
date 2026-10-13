using System.Collections;

namespace IGW.OpcUaProbe;

public static class ProbeWriteDiagnosticAnalyzer
{
    public static ProbeWriteDiagnosticItem Analyze(ProbeNodeReport node)
        => Analyze(node, new Dictionary<string, ProbeNodeReport>(StringComparer.Ordinal));

    public static ProbeWriteDiagnosticItem Analyze(
        ProbeNodeReport node,
        IReadOnlyDictionary<string, ProbeNodeReport> dataTypes)
    {
        var declaredFlags = Flags(node, "AccessLevel");
        var userFlags = Flags(node, "UserAccessLevel");
        bool declaredWritable = declaredFlags.Contains("CurrentWrite", StringComparer.Ordinal);
        bool userWritable = userFlags.Contains("CurrentWrite", StringComparer.Ordinal);
        string? dataType = ValueText(node, "DataType");
        int? valueRank = Number(node, "ValueRank");
        int[]? dimensions = Numbers(node, "ArrayDimensions");
        ProbeAttributeReport? definition = node.Attributes.TryGetValue("DataTypeDefinition", out var inlineDefinition)
            ? inlineDefinition
            : null;
        if ((definition is null || !Good(definition) || definition.Value is null)
            && dataType is not null && dataTypes.TryGetValue(dataType, out var dataTypeNode)
            && dataTypeNode.Attributes.TryGetValue("DataTypeDefinition", out var referencedDefinition))
            definition = referencedDefinition;
        bool definitionPublished = definition is not null && Good(definition) && definition.Value is not null;
        uint[] maxStringLengths = FindNumbers(definition?.Value,
                "maxStringLength")
            .Where(value => value > 0)
            .Distinct()
            .Order()
            .ToArray();
        var reasons = new List<string>();
        var limitations = new List<string>();

        if (!declaredWritable)
            reasons.Add("服务器声明 AccessLevel 不允许 CurrentWrite，该节点在协议层为只读。 ");
        else if (!userWritable)
            reasons.Add("服务器允许写，但当前登录用户的 UserAccessLevel 没有 CurrentWrite；需检查 OPC UA 用户、角色和 PLC 安全策略。");

        if (node.Attributes.TryGetValue("AccessRestrictions", out var restrictions)
            && Good(restrictions) && ToUInt64(restrictions.Value) is > 0)
            reasons.Add("节点声明了 AccessRestrictions；当前安全模式、签名或加密级别可能不满足写入要求。");

        if (IsString(dataType) && maxStringLengths.Length == 0)
        {
            reasons.Add("服务器未公开字符串长度上限；BadOutOfRange 可能由 PLC STRING/WSTRING 容量或编码后的字节长度超限引起，但只读元数据无法确认具体长度。");
            limitations.Add("要确认字符串容量，需要 TIA/S7 原生符号信息，或在明确授权后以受控写入获得服务器返回状态。");
        }
        if (valueRank is >= 1 && (dimensions is null || dimensions.Length == 0))
            reasons.Add("节点是数组，但服务器未公开 ArrayDimensions；写入数组形状不一致时可能返回 BadTypeMismatch 或 BadOutOfRange。");
        if (dataType is null)
            reasons.Add("DataType 属性读取失败，无法验证写入 CLR/OPC UA 类型是否匹配。");
        if (reasons.Count == 0)
            reasons.Add("只读元数据未发现明确阻断项；仍需结合实际 Write 返回的 StatusCode 判断类型、范围、长度或 PLC 程序覆盖问题。");
        limitations.Add("本探针不执行 Write，因此不能主动复现 BadOutOfRange、BadTypeMismatch 或 BadNotWritable。");

        return new ProbeWriteDiagnosticItem(
            node.NodeId,
            node.Paths.FirstOrDefault() ?? string.Empty,
            declaredWritable,
            userWritable,
            dataType,
            valueRank,
            dimensions,
            definitionPublished,
            maxStringLengths,
            reasons,
            limitations);
    }

    private static IReadOnlyList<string> Flags(ProbeNodeReport node, string name) =>
        node.Attributes.TryGetValue(name, out var value) && value.DecodedFlags is not null
            ? value.DecodedFlags
            : [];

    private static bool Good(ProbeNodeReport node, string name) =>
        node.Attributes.TryGetValue(name, out var value) && Good(value);

    private static bool Good(ProbeAttributeReport value) => value.StatusCode == "0x00000000";

    private static string? ValueText(ProbeNodeReport node, string name) =>
        node.Attributes.TryGetValue(name, out var value) && Good(value)
            ? value.Value?.ToString()
            : null;

    private static int? Number(ProbeNodeReport node, string name)
    {
        if (!node.Attributes.TryGetValue(name, out var value) || !Good(value)) return null;
        try { return System.Convert.ToInt32(value.Value, System.Globalization.CultureInfo.InvariantCulture); }
        catch { return null; }
    }

    private static int[]? Numbers(ProbeNodeReport node, string name)
    {
        if (!node.Attributes.TryGetValue(name, out var value) || !Good(value) || value.Value is null) return null;
        if (value.Value is IEnumerable sequence)
        {
            var result = new List<int>();
            foreach (object? item in sequence)
                try { result.Add(System.Convert.ToInt32(item, System.Globalization.CultureInfo.InvariantCulture)); }
                catch { }
            return result.ToArray();
        }
        return null;
    }

    private static bool IsString(string? dataType) => dataType is not null
        && (dataType.Equals("i=12", StringComparison.OrdinalIgnoreCase)
            || dataType.Equals("ns=0;i=12", StringComparison.OrdinalIgnoreCase)
            || dataType.Contains("String", StringComparison.OrdinalIgnoreCase));

    private static ulong? ToUInt64(object? value)
    {
        try { return System.Convert.ToUInt64(value, System.Globalization.CultureInfo.InvariantCulture); }
        catch { return null; }
    }

    private static IEnumerable<uint> FindNumbers(object? value, string key)
    {
        if (value is IReadOnlyDictionary<string, object?> readOnly)
        {
            foreach (var item in readOnly)
            {
                if (item.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    if (TryUInt32(item.Value, out uint number)) yield return number;
                }
                foreach (uint found in FindNumbers(item.Value, key)) yield return found;
            }
        }
        else if (value is IDictionary dictionary)
        {
            foreach (DictionaryEntry item in dictionary)
            {
                if (string.Equals(item.Key?.ToString(), key, StringComparison.OrdinalIgnoreCase))
                {
                    if (TryUInt32(item.Value, out uint number)) yield return number;
                }
                foreach (uint found in FindNumbers(item.Value, key)) yield return found;
            }
        }
        else if (value is IEnumerable sequence and not string)
        {
            foreach (object? item in sequence)
                foreach (uint found in FindNumbers(item, key)) yield return found;
        }
    }

    private static bool TryUInt32(object? value, out uint result)
    {
        try
        {
            result = System.Convert.ToUInt32(value, System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            result = 0;
            return false;
        }
    }
}
