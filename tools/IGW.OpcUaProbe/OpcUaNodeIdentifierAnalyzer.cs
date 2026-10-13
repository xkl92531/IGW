using System.Text.RegularExpressions;
using Opc.Ua;

namespace IGW.OpcUaProbe;

public sealed record OpcUaNodeIdentifierAnalysis(
    ushort NamespaceIndex,
    string IdentifierType,
    string RawIdentifier,
    string? SiemensSymbolCandidate,
    string MappingConfidence,
    string Explanation);

public static class OpcUaNodeIdentifierAnalyzer
{
    private static readonly Regex SiemensNativeSymbol = new(
        "^\"[^\"]+\"(?:\\.(?:\"[^\"]+\"|[^\\s.]+))+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static OpcUaNodeIdentifierAnalysis Analyze(string nodeIdText)
    {
        try
        {
            var nodeId = NodeId.Parse(nodeIdText);
            string raw = nodeId.Identifier?.ToString() ?? string.Empty;
            string? candidate = nodeId.IdType == IdType.String && SiemensNativeSymbol.IsMatch(raw)
                ? raw
                : null;
            return new OpcUaNodeIdentifierAnalysis(
                nodeId.NamespaceIndex,
                nodeId.IdType.ToString(),
                raw,
                candidate,
                candidate is null ? "Unavailable" : "Unverified",
                candidate is null
                    ? "NodeId 本身不能证明或推导出 Siemens 原生地址。"
                    : "字符串标识符具有 SIMATIC 符号路径形态，但仍需与 PLC 原生符号表验证。仅凭 OPC UA 信息不能标记为精确匹配。");
        }
        catch (Exception ex) when (ex is ServiceResultException or FormatException or ArgumentException)
        {
            return new OpcUaNodeIdentifierAnalysis(
                0,
                "Invalid",
                nodeIdText ?? string.Empty,
                null,
                "Unavailable",
                $"NodeId 无法解析：{ex.Message}");
        }
    }
}
