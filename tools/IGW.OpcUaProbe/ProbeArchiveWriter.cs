using System.IO.Compression;
using System.Text;

namespace IGW.OpcUaProbe;

public static class ProbeArchiveWriter
{
    private const string Readme = """
        IGW OPC UA 只读信息采集报告

        本工具只浏览节点、读取节点属性和 OPC UA Server 标准能力值，不读取业务变量值、不订阅变量、不调用方法，也不向 PLC 写入任何内容。
        opcua-nodes.json：机器可读的完整报告。
        write-diagnostics.json：写权限、类型、范围/长度证据和服务器写入能力摘要。
        igw-runtime-write-errors.log：本机 IGW 最近日志中与 OPC UA/写入失败有关的脱敏片段。
        summary.txt：采集范围和可映射性摘要。
        opcua-probe.log：连接、浏览和异常日志，不包含密码。

        注意：报告中的 SiemensSymbolCandidate 仅表示 NodeId 具有西门子符号路径形态，
        不能单独证明该地址可被 S7/S7 Plus 驱动直接读取。
        """;

    public static void Create(string zipPath, string reportJson, string summary, string log)
        => Create(zipPath, reportJson, "{}", summary, log);

    public static void Create(
        string zipPath,
        string reportJson,
        string writeDiagnosticsJson,
        string summary,
        string log)
        => Create(zipPath, reportJson, writeDiagnosticsJson, summary, log, string.Empty);

    public static void Create(
        string zipPath,
        string reportJson,
        string writeDiagnosticsJson,
        string summary,
        string log,
        string runtimeWriteErrors)
    {
        string fullPath = Path.GetFullPath(zipPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        if (File.Exists(fullPath)) File.Delete(fullPath);
        using var archive = ZipFile.Open(fullPath, ZipArchiveMode.Create);
        Write(archive, "README.txt", Readme);
        Write(archive, "opcua-nodes.json", reportJson);
        Write(archive, "write-diagnostics.json", writeDiagnosticsJson);
        Write(archive, "summary.txt", summary);
        Write(archive, "opcua-probe.log", log);
        Write(archive, "igw-runtime-write-errors.log", runtimeWriteErrors);
    }

    private static void Write(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content ?? string.Empty);
    }
}
