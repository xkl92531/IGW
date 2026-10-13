using System.Buffers;
using System.Buffers.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace IGW.Observability;

/// <summary>
/// 一条运行日志。Fields 只保存非空的上下文字段(设备、任务、排障编号等)，值为字符串、数字或布尔。
/// </summary>
public sealed record LogRecord(
    long TimestampMicros,
    int Level,
    string Category,
    string Message,
    IReadOnlyDictionary<string, object?> Fields)
{
    public DateTimeOffset Timestamp => DateTimeOffset.UnixEpoch.AddTicks(TimestampMicros * 10);
}

/// <summary>
/// 日志行编码：一条一行紧凑 JSON，固定以 {"t":时间,"l":级别, 开头，扫描时不解析整行就能按时间和级别过滤。
/// 中文等字符不转义，关键词可以直接在 UTF-8 字节上查找。
/// </summary>
public static class LogRecordCodec
{
    public static readonly string[] LevelNames = ["TRACE", "DEBUG", "INFO", "WARN", "ERROR", "CRITICAL"];
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        SkipValidation = true
    };
    private static ReadOnlySpan<byte> TimePrefix => "{\"t\":"u8;
    private static ReadOnlySpan<byte> LevelPrefix => ",\"l\":"u8;

    public static int FromLogLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => 0,
        LogLevel.Debug => 1,
        LogLevel.Information => 2,
        LogLevel.Warning => 3,
        LogLevel.Error => 4,
        _ => 5
    };

    public static string LevelName(int level) => LevelNames[Math.Clamp(level, 0, LevelNames.Length - 1)];

    public static long ToMicros(DateTimeOffset value) =>
        (value.UtcTicks - DateTimeOffset.UnixEpoch.Ticks) / 10;

    /// <summary>把一条记录写成一行(含结尾换行)。</summary>
    public static void Write(IBufferWriter<byte> output, LogRecord record)
    {
        using (var writer = new Utf8JsonWriter(output, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("t", record.TimestampMicros);
            writer.WriteNumber("l", record.Level);
            writer.WriteString("c", record.Category);
            writer.WriteString("m", record.Message);
            if (record.Fields.Count > 0)
            {
                writer.WriteStartObject("f");
                foreach (var (key, value) in record.Fields)
                {
                    switch (value)
                    {
                        case null: continue;
                        case string text: writer.WriteString(key, text); break;
                        case bool flag: writer.WriteBoolean(key, flag); break;
                        case int or long or short or byte or uint or ushort or sbyte:
                            writer.WriteNumber(key, Convert.ToInt64(value)); break;
                        case ulong big: writer.WriteNumber(key, big); break;
                        case double or float or decimal:
                            double number = Convert.ToDouble(value);
                            if (double.IsFinite(number)) writer.WriteNumber(key, number);
                            else writer.WriteString(key, number.ToString(System.Globalization.CultureInfo.InvariantCulture));
                            break;
                        default: writer.WriteString(key, Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)); break;
                    }
                }
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        output.Write("\n"u8);
    }

    /// <summary>不解析整行，只读取行首的时间和级别。格式不对时返回 false。</summary>
    public static bool TryReadHeader(ReadOnlySpan<byte> line, out long timestampMicros, out int level)
    {
        timestampMicros = 0;
        level = 0;
        if (!line.StartsWith(TimePrefix)) return false;
        ReadOnlySpan<byte> rest = line[TimePrefix.Length..];
        if (!Utf8Parser.TryParse(rest, out timestampMicros, out int consumed)) return false;
        rest = rest[consumed..];
        if (!rest.StartsWith(LevelPrefix)) return false;
        rest = rest[LevelPrefix.Length..];
        return Utf8Parser.TryParse(rest, out level, out _);
    }

    /// <summary>解析一整行；损坏的行(如断电时写了一半)返回 null。</summary>
    public static LogRecord? Parse(ReadOnlySpan<byte> line)
    {
        try
        {
            var reader = new Utf8JsonReader(line);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
            long timestamp = 0;
            int level = 2;
            string category = "system", message = "";
            Dictionary<string, object?>? fields = null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("t")) { reader.Read(); timestamp = reader.GetInt64(); }
                else if (reader.ValueTextEquals("l")) { reader.Read(); level = reader.GetInt32(); }
                else if (reader.ValueTextEquals("c")) { reader.Read(); category = reader.GetString() ?? "system"; }
                else if (reader.ValueTextEquals("m")) { reader.Read(); message = reader.GetString() ?? ""; }
                else if (reader.ValueTextEquals("f"))
                {
                    reader.Read();
                    fields = new Dictionary<string, object?>(StringComparer.Ordinal);
                    while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                    {
                        string key = reader.GetString()!;
                        reader.Read();
                        fields[key] = reader.TokenType switch
                        {
                            JsonTokenType.String => reader.GetString(),
                            JsonTokenType.True => true,
                            JsonTokenType.False => false,
                            JsonTokenType.Number => reader.TryGetInt64(out long whole) ? (object)whole : reader.GetDouble(),
                            _ => null
                        };
                        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) reader.Skip();
                    }
                }
                else { reader.Read(); reader.Skip(); }
            }
            return new LogRecord(timestamp, level, category, message,
                (IReadOnlyDictionary<string, object?>?)fields ?? EmptyFields);
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (FormatException) { return null; }
    }

    private static readonly IReadOnlyDictionary<string, object?> EmptyFields =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>逐行遍历缓冲区(以 \n 分隔)，末尾不完整的行也会交给回调，由解析决定是否丢弃。</summary>
    public static void ForEachLine(ReadOnlySpan<byte> buffer, LineVisitor visitor)
    {
        int start = 0;
        while (start < buffer.Length)
        {
            int length = buffer[start..].IndexOf((byte)'\n');
            if (length < 0) length = buffer.Length - start;
            if (length > 0 && !visitor(buffer.Slice(start, length), start)) return;
            start += length + 1;
        }
    }

    public delegate bool LineVisitor(ReadOnlySpan<byte> line, int offset);
}
