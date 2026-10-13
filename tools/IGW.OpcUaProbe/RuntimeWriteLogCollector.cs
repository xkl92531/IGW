using System.Text;
using System.Text.RegularExpressions;

namespace IGW.OpcUaProbe;

public static class RuntimeWriteLogCollector
{
    private static readonly Regex Header = new(
        @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [A-Z?]{3} \[[^\]]*\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Secret = new(
        """(?i)\b(password|pwd|token|authorization)\s*[:=]\s*(?:"[^"]*"|'[^']*'|[^\s;,]+)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly string[] EvidenceTerms =
    [
        "写入失败", "写验证", "写响应", "服务器拒绝", "WriteAsync", "ServiceResultException",
        "BadNotWritable", "BadUserAccessDenied", "BadTypeMismatch", "BadOutOfRange",
        "BadRequestTimeout", "BadTimeout", "BadNotConnected", "BadNotVerified",
        "BadTooManyOperations", "BadEncodingLimitsExceeded", "BadInvalidArgument",
        "BadNodeIdUnknown", "BadAttributeIdInvalid", "BadSecurity", "OPC UA",
    ];

    public static string DefaultLogDirectory()
    {
        string? configured = Environment.GetEnvironmentVariable("IGW_DATA_DIR");
        string data = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "IGW")
            : configured;
        return Path.Combine(data, "logs");
    }

    public static string Collect(string logDirectory, int maxFiles = 3, int maxLines = 2_000)
    {
        if (!Directory.Exists(logDirectory))
            return $"未找到 IGW 日志目录：{logDirectory}";

        string[] files;
        try
        {
            files = Directory.EnumerateFiles(logDirectory, "igw*.log", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(Math.Clamp(maxFiles, 1, 20))
                .ToArray();
        }
        catch (Exception ex)
        {
            return $"读取 IGW 日志目录失败：{ex.GetType().Name}: {ex.Message}";
        }
        if (files.Length == 0) return $"IGW 日志目录中没有 igw*.log：{logDirectory}";

        var selected = new List<string>();
        foreach (string file in files)
        {
            foreach (string entry in ReadEntries(file).Reverse())
            {
                if (!EvidenceTerms.Any(term => entry.Contains(term, StringComparison.OrdinalIgnoreCase))) continue;
                selected.Add($"===== {Path.GetFileName(file)} ====={Environment.NewLine}{Redact(entry)}");
                if (selected.Sum(item => item.Count(character => character == '\n') + 1) >= maxLines)
                    return JoinAndTrim(selected, maxLines);
            }
        }
        return selected.Count == 0
            ? $"已检查 {files.Length} 个最新 IGW 日志文件，未发现 OPC UA/写入失败片段。"
            : JoinAndTrim(selected, maxLines);
    }

    private static IEnumerable<string> ReadEntries(string file)
    {
        var current = new StringBuilder();
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (reader.ReadLine() is { } line)
        {
            if (Header.IsMatch(line) && current.Length > 0)
            {
                yield return current.ToString().TrimEnd();
                current.Clear();
            }
            current.AppendLine(line);
        }
        if (current.Length > 0) yield return current.ToString().TrimEnd();
    }

    private static string Redact(string value) => Secret.Replace(value, match =>
        $"{match.Groups[1].Value}=***");

    private static string JoinAndTrim(IReadOnlyList<string> entries, int maxLines)
    {
        int limit = Math.Clamp(maxLines, 1, 20_000);
        return string.Join(Environment.NewLine + Environment.NewLine, entries)
            .Split(["\r\n", "\n"], StringSplitOptions.None)
            .Take(limit)
            .Aggregate(new StringBuilder(), (builder, line) => builder.AppendLine(line))
            .ToString()
            .TrimEnd();
    }
}
