using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace IGW.OpcUaProbe;

public static class ProbeReportFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string ToJson(OpcUaProbeReport report) =>
        JsonSerializer.Serialize(report, JsonOptions);

    public static string ToWriteDiagnosticsJson(ProbeWriteDiagnostics? diagnostics) =>
        JsonSerializer.Serialize(diagnostics ?? new ProbeWriteDiagnostics(
            0, 0, 0, 0,
            new Dictionary<string, ProbeAttributeReport>(),
            [],
            "未生成写入诊断。"), JsonOptions);

    public static string ToSummary(OpcUaProbeReport report)
    {
        var summary = report.Summary;
        var text = new StringBuilder();
        text.AppendLine("IGW OPC UA 只读信息采集摘要");
        text.AppendLine($"采集时间(UTC)：{report.CollectedAtUtc:O}");
        text.AppendLine($"配置端点：{report.Connection.RequestedEndpoint}");
        text.AppendLine($"会话端点：{report.Connection.SessionEndpoint}");
        text.AppendLine($"服务器：{report.Connection.ServerApplicationName}");
        text.AppendLine($"服务器 ApplicationUri：{report.Connection.ServerApplicationUri}");
        text.AppendLine($"安全模式：{report.Connection.SecurityMode}");
        text.AppendLine($"Namespace 数量：{report.Namespaces.Count}");
        text.AppendLine($"节点总数：{summary.TotalNodes}");
        text.AppendLine($"Object/Variable/Method：{summary.Objects}/{summary.Variables}/{summary.Methods}");
        text.AppendLine($"字符串/数值 NodeId：{summary.StringNodeIds}/{summary.NumericNodeIds}");
        text.AppendLine($"当前用户可读/可写变量：{summary.UserReadableVariables}/{summary.UserWritableVariables}");
        text.AppendLine($"SIMATIC 符号候选：{summary.SiemensSymbolCandidates}");
        text.AppendLine($"属性读取异常数：{summary.AttributeErrors}");
        if (report.WriteDiagnostics is { } write)
        {
            text.AppendLine($"当前用户可写/只读/权限未知：{write.WritableByCurrentUser}/{write.ReadOnlyForCurrentUser}/{write.UnknownWritePermission}");
            if (write.ServerCapabilities.TryGetValue("OperationLimits_MaxNodesPerWrite", out var limit))
                text.AppendLine($"服务器 MaxNodesPerWrite：{limit.Value ?? "未公开"} ({limit.Status})");
        }
        text.AppendLine($"是否因节点上限截断：{summary.Truncated}");
        text.AppendLine("业务变量值读取：否");
        text.AppendLine("服务器标准能力值读取：是");
        text.AppendLine("PLC 写入：否");
        text.AppendLine();
        text.AppendLine("结论：");
        text.AppendLine(summary.MappingConclusion);
        return text.ToString();
    }
}
