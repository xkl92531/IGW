using System.Globalization;
using System.Text;
using IGW.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;
using IgwStatusCode = IGW.Abstractions.StatusCode;

namespace IGW.Drivers.OpcUaClient;

internal sealed record OpcUaStringWriteOutcome(
    IgwStatusCode Status,
    string AppliedValue,
    int Attempts);

/// <summary>
/// OPC UA 服务器未公开 PLC STRING/WSTRING 容量时，对 BadOutOfRange 做有界降级。
/// 只尝试原字符串的 Unicode 安全前缀，不修改非字符串值，也不吞掉其它拒写状态。
/// </summary>
internal static class OpcUaStringWriteFallback
{
    private const uint BadOutOfRange = 0x803C0000;
    private static readonly Encoding Gb18030;

    static OpcUaStringWriteFallback()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Gb18030 = Encoding.GetEncoding(54936);
    }

    internal static async Task<OpcUaStringWriteOutcome> WriteAsync(
        string original,
        IgwDataType dataType,
        PlcTextEncoding? textEncoding,
        int? knownCapacity,
        int maxAttempts,
        Func<string, CancellationToken, Task<IgwStatusCode>> write,
        CancellationToken ct)
    {
        if (maxAttempts <= 0) throw new ArgumentOutOfRangeException(nameof(maxAttempts));

        int attempts = 1;
        IgwStatusCode status = await write(original, ct).ConfigureAwait(false);
        if (status.Code != BadOutOfRange || dataType is not (IgwDataType.String or IgwDataType.WString))
            return new OpcUaStringWriteOutcome(status, original, attempts);

        int[] prefixOffsets = UnicodePrefixOffsets(original);
        int high = prefixOffsets.Length - 2; // 原值已经被服务器拒绝。
        string? best = null;
        IgwStatusCode lastStatus = status;

        if (knownCapacity is > 0)
        {
            string limited = TruncateToCapacity(original, knownCapacity.Value, dataType, textEncoding);
            int limitedIndex = Array.BinarySearch(prefixOffsets, limited.Length);
            if (limitedIndex >= 0 && limitedIndex < prefixOffsets.Length - 1 && attempts < maxAttempts)
            {
                attempts++;
                lastStatus = await write(limited, ct).ConfigureAwait(false);
                if (lastStatus.IsGood)
                    return new OpcUaStringWriteOutcome(lastStatus, limited, attempts);
                if (lastStatus.Code != BadOutOfRange)
                    return new OpcUaStringWriteOutcome(lastStatus, limited, attempts);
                high = limitedIndex - 1;
            }
        }

        int bestIndex = -1;
        int low = 0;
        bool foundAdaptiveUpper = false;

        if (high >= 1 && attempts < maxAttempts)
        {
            attempts++;
            string first = original[..prefixOffsets[1]];
            lastStatus = await write(first, ct).ConfigureAwait(false);
            if (lastStatus.IsGood)
            {
                best = first;
                bestIndex = 1;
                low = 2;
            }
            else if (lastStatus.Code == BadOutOfRange)
            {
                high = 0;
            }
            else
            {
                return new OpcUaStringWriteOutcome(lastStatus, first, attempts);
            }
        }

        if (best is null && low <= high && attempts < maxAttempts)
        {
            attempts++;
            string empty = original[..prefixOffsets[0]];
            lastStatus = await write(empty, ct).ConfigureAwait(false);
            if (!lastStatus.IsGood)
                return new OpcUaStringWriteOutcome(lastStatus, empty, attempts);
            best = empty;
            bestIndex = 0;
            low = 1;
        }

        while (low <= high && attempts < maxAttempts)
        {
            int remainingAttempts = maxAttempts - attempts;
            int remainingCandidates = high - low + 1;
            int binaryAttemptsNeeded = remainingCandidates <= 1
                ? remainingCandidates
                : (int)Math.Ceiling(Math.Log2(remainingCandidates + 1d));
            int middle;
            if (foundAdaptiveUpper || binaryAttemptsNeeded <= remainingAttempts || bestIndex <= 0)
            {
                middle = low + ((high - low) / 2);
            }
            else
            {
                double growth = Math.Pow((double)high / bestIndex, 1d / remainingAttempts);
                middle = Math.Clamp((int)Math.Floor(bestIndex * growth), low, high);
            }
            string candidate = original[..prefixOffsets[middle]];
            attempts++;
            lastStatus = await write(candidate, ct).ConfigureAwait(false);
            if (lastStatus.IsGood)
            {
                best = candidate;
                bestIndex = middle;
                low = middle + 1;
            }
            else if (lastStatus.Code == BadOutOfRange)
            {
                high = middle - 1;
                foundAdaptiveUpper = true;
            }
            else
            {
                return new OpcUaStringWriteOutcome(lastStatus, candidate, attempts);
            }
        }

        return best is not null
            ? new OpcUaStringWriteOutcome(IgwStatusCode.Good, best, attempts)
            : new OpcUaStringWriteOutcome(lastStatus, original, attempts);
    }

    internal static string TruncateToCapacity(
        string value,
        int capacity,
        IgwDataType dataType,
        PlcTextEncoding? textEncoding)
    {
        if (capacity <= 0 || string.IsNullOrEmpty(value)) return capacity <= 0 ? "" : value;
        int[] prefixOffsets = UnicodePrefixOffsets(value);
        if (dataType == IgwDataType.WString)
            return value[..prefixOffsets[Math.Min(capacity, prefixOffsets.Length - 1)]];

        Encoding encoding = textEncoding == PlcTextEncoding.Gb18030 ? Gb18030 : Encoding.UTF8;
        int low = 0;
        int high = prefixOffsets.Length - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            if (encoding.GetByteCount(value.AsSpan(0, prefixOffsets[middle])) <= capacity) low = middle + 1;
            else high = middle - 1;
        }
        return value[..prefixOffsets[Math.Max(0, high)]];
    }

    internal static int EncodedLength(string value, IgwDataType dataType, PlcTextEncoding? textEncoding)
    {
        if (dataType == IgwDataType.WString) return value.EnumerateRunes().Count();
        Encoding encoding = textEncoding == PlcTextEncoding.Gb18030 ? Gb18030 : Encoding.UTF8;
        return encoding.GetByteCount(value);
    }

    internal static int? EffectiveCapacity(int? declared, int? verified)
    {
        int? declaredValue = declared is > 0 ? declared : null;
        int? verifiedValue = verified is > 0 ? verified : null;
        if (declaredValue is null) return verifiedValue;
        if (verifiedValue is null) return declaredValue;
        return Math.Min(declaredValue.Value, verifiedValue.Value);
    }

    private static int[] UnicodePrefixOffsets(string value)
    {
        var offsets = new List<int> { 0 };
        int offset = 0;
        foreach (Rune rune in value.EnumerateRunes())
        {
            offset += rune.Utf16SequenceLength;
            offsets.Add(offset);
        }
        return offsets.ToArray();
    }
}

/// <summary>从 Siemens 符号 NodeId 的父结构 DataTypeDefinition 获取字段字符串容量。</summary>
internal static class OpcUaStringLengthResolver
{
    internal static bool TrySplitParentNode(NodeId child, out NodeId parent, out string fieldName)
    {
        parent = NodeId.Null;
        fieldName = "";
        if (child.Identifier is not string identifier || string.IsNullOrWhiteSpace(identifier)) return false;

        int separator = identifier.LastIndexOf(".\"", StringComparison.Ordinal);
        int fieldStart;
        if (separator >= 0)
        {
            fieldStart = separator + 2;
        }
        else
        {
            separator = identifier.LastIndexOf('.');
            if (separator < 1 || separator >= identifier.Length - 1) return false;
            fieldStart = separator + 1;
        }

        string parentIdentifier = identifier[..separator];
        string field = identifier[fieldStart..].Trim();
        if (field.Length >= 2 && field[0] == '"' && field[^1] == '"')
            field = field[1..^1];
        else
            field = field.Trim('"');
        if (string.IsNullOrWhiteSpace(parentIdentifier) || string.IsNullOrWhiteSpace(field)) return false;

        parent = new NodeId(parentIdentifier, child.NamespaceIndex);
        fieldName = field;
        return true;
    }

    internal static async Task<int?> ResolveAsync(Session session, NodeId child, CancellationToken ct)
    {
        var result = await ResolveManyAsync(session, [child], ct).ConfigureAwait(false);
        return result.GetValueOrDefault(child.ToString());
    }

    internal static async Task<IReadOnlyDictionary<string, int>> ResolveManyAsync(
        Session session,
        IReadOnlyList<NodeId> children,
        CancellationToken ct)
    {
        var links = children.Select(child =>
            {
                bool parsed = TrySplitParentNode(child, out NodeId parent, out string field);
                return (Child: child, Parsed: parsed, Parent: parent, Field: field);
            })
            .Where(item => item.Parsed)
            .ToArray();
        if (links.Length == 0) return new Dictionary<string, int>(StringComparer.Ordinal);

        NodeId[] parents = links.Select(item => item.Parent)
            .Distinct()
            .ToArray();
        var parentRead = new ReadValueIdCollection(parents.Select(parent =>
            new ReadValueId { NodeId = parent, AttributeId = Attributes.DataType }));
        var parentResponse = await ProtocolOperationTiming.MeasureAsync(() => session.ReadAsync(
            null, 0, TimestampsToReturn.Neither, parentRead, ct)).ConfigureAwait(false);
        OpcUaClientDeviceDriver.ThrowIfBrowseTransportFailure(parentResponse.Results);
        if (parentResponse.Results is not { } parentValues || parentValues.Count != parents.Length)
            return new Dictionary<string, int>(StringComparer.Ordinal);

        var parentTypes = new Dictionary<NodeId, NodeId>();
        for (int i = 0; i < parents.Length; i++)
        {
            if (!Opc.Ua.StatusCode.IsBad(parentValues[i].StatusCode) &&
                TryDataTypeNode(parentValues[i].Value, session.NamespaceUris, out NodeId dataTypeNode))
                parentTypes[parents[i]] = dataTypeNode;
        }
        NodeId[] dataTypes = parentTypes.Values.Distinct().ToArray();
        if (dataTypes.Length == 0) return new Dictionary<string, int>(StringComparer.Ordinal);

        var definitionRead = new ReadValueIdCollection(dataTypes.Select(dataType =>
            new ReadValueId { NodeId = dataType, AttributeId = Attributes.DataTypeDefinition }));
        var definitionResponse = await ProtocolOperationTiming.MeasureAsync(() => session.ReadAsync(
            null, 0, TimestampsToReturn.Neither, definitionRead, ct)).ConfigureAwait(false);
        OpcUaClientDeviceDriver.ThrowIfBrowseTransportFailure(definitionResponse.Results);
        if (definitionResponse.Results is not { } definitionValues || definitionValues.Count != dataTypes.Length)
            return new Dictionary<string, int>(StringComparer.Ordinal);

        var definitions = new Dictionary<NodeId, StructureDefinition>();
        for (int i = 0; i < dataTypes.Length; i++)
        {
            if (Opc.Ua.StatusCode.IsBad(definitionValues[i].StatusCode)) continue;
            StructureDefinition? definition = definitionValues[i].Value switch
            {
                StructureDefinition direct => direct,
                ExtensionObject { Body: StructureDefinition body } => body,
                _ => null,
            };
            if (definition is not null) definitions[dataTypes[i]] = definition;
        }

        var output = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var link in links)
        {
            if (!parentTypes.TryGetValue(link.Parent, out NodeId? dataType) ||
                !definitions.TryGetValue(dataType, out StructureDefinition? definition)) continue;
            StructureField? field = definition.Fields?.FirstOrDefault(item =>
                string.Equals(item.Name, link.Field, StringComparison.Ordinal) ||
                string.Equals(item.Name, link.Field, StringComparison.OrdinalIgnoreCase));
            if (field?.MaxStringLength is not > 0) continue;
            output[link.Child.ToString()] = field.MaxStringLength > int.MaxValue
                ? int.MaxValue
                : (int)field.MaxStringLength;
        }
        return output;
    }

    private static bool TryDataTypeNode(object? value, NamespaceTable namespaceUris, out NodeId node)
    {
        node = NodeId.Null;
        switch (value)
        {
            case NodeId direct:
                node = direct;
                return true;
            case ExpandedNodeId expanded:
                node = ExpandedNodeId.ToNodeId(expanded, namespaceUris) ?? NodeId.Null;
                return !NodeId.IsNull(node);
            case string text:
                return NodeId.TryParse(text, out node);
            default:
                return false;
        }
    }
}
