using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IGW.Observability;

public static class QuestDbLogCodec
{
    public static readonly string[] Fields = ["igw_protocol", "igw_channel", "igw_device", "igw_db", "igw_task_id",
        "igw_module", "igw_feature", "igw_trace_id", "igw_task_name", "igw_task_ids", "igw_task_names",
        "igw_interface_name", "igw_operation", "igw_route", "igw_target", "igw_logger", "igw_event_kind", "igw_tag_values"];
    public static readonly HashSet<string> IndexedFields = ["igw_protocol", "igw_channel", "igw_device", "igw_db", "igw_module", "igw_feature"];

    public static string Raw(LogRecord record)
    {
        var buffer = new ArrayBufferWriter<byte>();
        LogRecordCodec.Write(buffer, record);
        return Encoding.UTF8.GetString(buffer.WrittenSpan.TrimEnd((byte)'\n'));
    }

    public static LogRecord Decode(string raw) => LogRecordCodec.Parse(Encoding.UTF8.GetBytes(raw))
        ?? throw new InvalidDataException("QuestDB 日志正文无效。");

    public static string Encode(LogRecord record, Guid id)
    {
        var line = new StringBuilder("IGW_logs");
        // SYMBOL values are fields, not ILP tags: an empty value is valid and keeps the schema fixed.
        line.Append(" id=").Append(Quote(id.ToString())).Append(",level=").Append(record.Level).Append("i,category=")
            .Append(Quote(record.Category)).Append(",raw=").Append(Quote(Raw(record)));
        foreach (string field in Fields)
            line.Append(',').Append(field).Append('=').Append(Quote(Flat(LogFilter.Text(record, field))));
        return line.Append(' ').Append(record.TimestampMicros).Append('\n').ToString();
    }

    // ILP does not allow actual CR/LF in string fields. Complete values remain in raw JSON;
    // queries that can observe CR/LF use json_extract rather than the flattened hot columns.
    internal static string Flat(string value) => value.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n");
    internal static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    public static Guid HistoricalId(string relativePath, long row) => new(SHA256.HashData(Encoding.UTF8.GetBytes(relativePath.Replace('\\', '/') + "\n" + row))[..16]);

    internal static string Payload(Guid id, long timestamp, string contentType, string content) =>
        $"IGW_payloads id={Quote(id.ToString())},content_type={Quote(JsonSerializer.Serialize(contentType))},body={Quote(JsonSerializer.Serialize(content))} {timestamp}\n";
}
