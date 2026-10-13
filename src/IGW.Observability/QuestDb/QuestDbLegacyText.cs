using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace IGW.Observability;

/// <summary>Old igw-yyyyMMdd[.N].log records; continuation lines remain part of the original error.</summary>
public static partial class QuestDbLegacyText
{
    public static IEnumerable<LogRecord> Read(TextReader reader, CancellationToken cancellationToken = default)
    {
        LogRecord? current = null;
        bool nonempty = false;
        var message = new StringBuilder();
        while (reader.ReadLine() is string line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            nonempty |= !string.IsNullOrWhiteSpace(line);
            Match match = Header().Match(line.TrimStart('\uFEFF'));
            if (match.Success && DateTime.TryParseExact(match.Groups["time"].Value, "yyyy-MM-dd HH:mm:ss.fff",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime time))
            {
                if (current is not null) yield return current with { Message = message.ToString() };
                message.Clear().Append(match.Groups["message"].Value);
                int level = match.Groups["level"].Value switch { "TRC" => 0, "DBG" => 1, "WRN" => 3, "ERR" => 4, "CRT" => 5, _ => 2 };
                current = new LogRecord(LogRecordCodec.ToMicros(new DateTimeOffset(time)), level, "system", "",
                    new Dictionary<string, object?> { ["igw_logger"] = match.Groups["logger"].Value });
            }
            else if (current is not null)
            {
                if (message.Length + line.Length > 4 * 1024 * 1024) throw new InvalidDataException("旧文本日志的单条记录超过 4M 字符，原文件已保留。");
                message.Append('\n').Append(line);
            }
        }
        if (current is not null) yield return current with { Message = message.ToString() };
        else if (nonempty) throw new InvalidDataException("旧文本日志格式无法识别，原文件已保留。");
    }

    [GeneratedRegex(@"^(?<time>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) (?<level>[A-Z?]{3}) \[(?<logger>[^\]]*)\] ?(?<message>.*)$")]
    private static partial Regex Header();
}
