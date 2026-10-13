using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace IGW.Observability;

public sealed record LogExportOptions(
    bool IncludePayloads = true,
    int MaxEntries = LogCenterExport.DefaultMaxEntries,
    long MaxPayloadBytes = LogCenterExport.DefaultMaxPayloadBytes,
    string Format = "text",
    string StorageEngine = "segments");

public sealed record LogExportSummary(
    long Entries,
    bool Truncated,
    int Payloads,
    long PayloadBytes,
    bool PayloadsTruncated);

/// <summary>
/// 把日志中心的查询结果打包成 zip：可直接阅读的文本日志、相关接口报文和一份清单。
/// 现场把整个日志存储目录拷走既大又打不开；按条件导出的文本压缩后通常只有几 MB，方便发送。
/// </summary>
public static class LogCenterExport
{
    public const int DefaultMaxEntries = 500_000;
    public const long DefaultMaxPayloadBytes = 200L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly JsonSerializerOptions JsonLine = new(JsonSerializerDefaults.Web);
    public static string ValidateFormat(string format) => format.ToLowerInvariant() switch
    {
        "text" or "jsonl" or "csv" => format.ToLowerInvariant(),
        _ => throw new ArgumentException("日志导出格式必须为 jsonl、csv 或 text。", nameof(format))
    };
    private static string LogFileName(string format) => format == "text" ? "logs.txt" : "logs." + format;
    private static readonly string[] ContextFields =
    [
        "igw_channel", "igw_device", "igw_db", "igw_task_name", "igw_task_id", "igw_module",
        "igw_feature", "igw_operation", "igw_target", "igw_trace_id"
    ];

    /// <summary>生成导出包到临时文件；返回的流位于开头，关闭即删除。</summary>
    public static async Task<(Stream Archive, LogExportSummary Summary)> CreateAsync(
        LogCenterRequest request,
        IAsyncEnumerable<LogCenterEntry> source,
        LogPayloadStore? payloads,
        LogExportOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(source);
        LogFilter.Create(request);   // 与查询相同的参数校验(时间范围不超过 31 天等)
        var file = new FileStream(
            Path.Combine(Path.GetTempPath(), "igw-logs-" + Guid.NewGuid().ToString("N") + ".zip"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        try
        {
            LogExportSummary summary = await WriteAsync(file, request, source, payloads, options, cancellationToken)
                .ConfigureAwait(false);
            file.Position = 0;
            return (file, summary);
        }
        catch
        {
            await file.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static async Task<LogExportSummary> WriteAsync(
        Stream destination,
        LogCenterRequest request,
        IAsyncEnumerable<LogCenterEntry> source,
        LogPayloadStore? payloads,
        LogExportOptions options,
        CancellationToken cancellationToken)
    {
        string format = ValidateFormat(options.Format);
        string logFile = LogFileName(format);
        var payloadIds = new List<string>();
        var seenPayloads = new HashSet<string>(StringComparer.Ordinal);
        long entries = 0;
        bool truncated = false;
        int payloadCount = 0;
        long payloadBytes = 0;
        bool payloadsTruncated = false;
        DateTimeOffset exportedAt = DateTimeOffset.Now;

        using (var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
        {
            // 最快压缩：几十万条日志用最高压缩要多花好几倍时间，包只大 10%~20%
            ZipArchiveEntry logEntry = archive.CreateEntry(logFile, CompressionLevel.Fastest);
            await using (Stream logStream = logEntry.Open())
            await using (var writer = new StreamWriter(logStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: format != "jsonl"), 1 << 16))
            {
                if (format == "csv") await writer.WriteLineAsync("timestamp,level,category,message,traceId,payloadId,attributes");
                await foreach (LogCenterEntry item in source.WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    if (entries >= options.MaxEntries) { truncated = true; break; }
                    string line = format switch
                    {
                        "jsonl" => JsonSerializer.Serialize(item, JsonLine) + "\n",
                        "csv" => Csv(item),
                        _ => Format(item)
                    };
                    await writer.WriteAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
                    entries++;
                    if (options.IncludePayloads && item.PayloadId is { Length: 32 } id && seenPayloads.Add(id))
                        payloadIds.Add(id);
                }
                if (entries == 0 && format == "text") await writer.WriteLineAsync("(所选时间段和条件下没有日志)").ConfigureAwait(false);
            }

            if (options.IncludePayloads && payloads is not null)
            {
                // 压缩包只能顺序写，但读报文可以并行：几十万个小文件逐个打开是导出最慢的一步。
                // 最多提前读 32 份，按原顺序写入；已按保留期或容量清理掉的报文跳过，日志行里的编号仍保留。
                var reads = new Queue<(string Id, Task<byte[]?> Content)>();
                using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                int next = 0;
                try
                {
                    while (true)
                    {
                        while (reads.Count < (payloads.DatabaseBacked ? 4 : 32) && next < payloadIds.Count)
                        {
                            string id = payloadIds[next++];
                            reads.Enqueue((id, payloads.TryReadRawAsync(id, readCancellation.Token)));
                        }
                        if (reads.Count == 0) break;
                        cancellationToken.ThrowIfCancellationRequested();
                        var (payloadId, read) = reads.Dequeue();
                        if (payloadBytes >= options.MaxPayloadBytes) { payloadsTruncated = true; break; }
                        byte[]? content = await read.ConfigureAwait(false);
                        if (content is null) continue;
                        ZipArchiveEntry entry = archive.CreateEntry($"payloads/{payloadId}.txt", CompressionLevel.Fastest);
                        await using (Stream target = entry.Open())
                            await target.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                        payloadBytes += content.Length;
                        payloadCount++;
                    }
                }
                finally
                {
                    readCancellation.Cancel();
                    try { await Task.WhenAll(reads.Select(item => item.Content)).ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                }
            }

            var summary = new LogExportSummary(entries, truncated, payloadCount, payloadBytes, payloadsTruncated);
            ZipArchiveEntry manifest = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
            await using (Stream manifestStream = manifest.Open())
            {
                await JsonSerializer.SerializeAsync(manifestStream, new
                {
                    exportedAt,
                    exportSchemaVersion = 2,
                    storageEngine = options.StorageEngine,
                    format,
                    logFile,
                    machine = Environment.MachineName,
                    gatewayVersion = GatewayVersion(),
                    timeZone = TimeZoneInfo.Local.Id,
                    range = new { from = request.From.ToLocalTime(), to = request.To.ToLocalTime() },
                    filters = new
                    {
                        request.Category,
                        request.Level,
                        request.Search,
                        request.Protocol,
                        request.Channel,
                        request.Device,
                        request.Database,
                        request.TaskId,
                        request.Task,
                        request.Module,
                        request.Feature,
                        request.Interface,
                        request.TaskOrInterface,
                        request.TraceId,
                        request.Path
                    },
                    entries,
                    truncated,
                    maxEntries = options.MaxEntries,
                    payloads = payloadCount,
                    payloadBytes,
                    payloadsTruncated,
                    payloadsRequested = options.IncludePayloads
                }, Json, cancellationToken).ConfigureAwait(false);
            }
            ZipArchiveEntry readme = archive.CreateEntry("说明.txt", CompressionLevel.Optimal);
            await using (var readmeWriter = new StreamWriter(readme.Open(), new UTF8Encoding(true)))
                await readmeWriter.WriteAsync(Readme(summary, options)).ConfigureAwait(false);
            return summary;
        }
    }

    internal static string Format(LogCenterEntry item)
    {
        var line = new StringBuilder(256);
        line.Append(item.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(' ').Append(item.Level.PadRight(5))
            .Append(" [").Append(item.Category).Append("] ")
            .Append(item.Message.Replace("\r\n", "\n").Replace("\n", "\n    "));
        var context = new List<string>();
        foreach (string field in ContextFields)
            if (Text(item.Attributes, field) is { Length: > 0 } value && !item.Message.Contains(value, StringComparison.Ordinal))
                context.Add($"{field["igw_".Length..]}={value}");
        // 接口/上报的耗时和状态码原先不导出，现场排查「哪个请求慢」只能拿完成时间倒推。
        if (Text(item.Attributes, "igw_http_status_code") is { Length: > 0 } httpStatus)
            context.Add($"HTTP={httpStatus}");
        if (item.Attributes.TryGetValue("igw_duration_ms", out JsonElement duration)
            && duration.ValueKind == JsonValueKind.Number && duration.TryGetDouble(out double durationMs))
            context.Add($"耗时={Math.Round(durationMs).ToString(CultureInfo.InvariantCulture)}ms");
        if (item.PayloadId is { Length: > 0 } payload) context.Add($"报文=payloads/{payload}.txt");
        if (context.Count > 0) line.Append("\n    ").Append(string.Join("  ", context));
        foreach (var (name, value) in item.Attributes)
            if (name.StartsWith("exception", StringComparison.OrdinalIgnoreCase) && TextOf(value) is { Length: > 0 } text)
                line.Append("\n    ").Append(name).Append(": ").Append(text.Replace("\r\n", "\n").Replace("\n", "\n    "));
        return line.Append('\n').ToString();
    }

    private static string? Text(IReadOnlyDictionary<string, JsonElement> attributes, string name) =>
        attributes.TryGetValue(name, out JsonElement value) ? TextOf(value) : null;

    private static string Csv(LogCenterEntry item)
    {
        static string Quote(string? value) => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
        return string.Join(',', new[] { item.Timestamp.ToString("O"), item.Level, item.Category, item.Message,
            item.TraceId, item.PayloadId, JsonSerializer.Serialize(item.Attributes, JsonLine) }.Select(Quote)) + "\r\n";
    }

    private static string? TextOf(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
        _ => null
    };

    private static string GatewayVersion() =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "unknown";

    private static string Readme(LogExportSummary summary, LogExportOptions options) =>
        $"""
        IGW 日志导出包

        数据来源：{(options.StorageEngine == "questdb" ? "QuestDB 日志数据库" : "网关日志存储")}。
        {LogFileName(ValidateFormat(options.Format))} 按时间先后排列的日志记录。
        JSONL 每行一条完整 JSON 日志，保留全部属性和数据类型；CSV 可用表格软件打开，attributes 列保留完整属性；文本可直接阅读。
        payloads/      日志里引用的接口请求/响应报文(日志行中“报文=payloads/xxx.txt”)。
        manifest.json  导出时间、网关版本、时间范围和筛选条件。

        本次共导出 {summary.Entries} 条日志{(summary.Truncated ? $"，已达到单次上限 {options.MaxEntries} 条，请缩小时间范围分批导出" : "")}。
        附带报文 {summary.Payloads} 份{(summary.PayloadsTruncated ? "，报文总量已达上限，其余未附带" : "")}。
        """;
}
