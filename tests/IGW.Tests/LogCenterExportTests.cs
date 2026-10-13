using System.IO.Compression;
using System.Text.Json;
using IGW.Observability;
using Xunit;

namespace IGW.Tests;

public sealed class LogCenterExportTests
{
    private static readonly DateTimeOffset From = new(2026, 9, 19, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Export_writes_readable_logs_in_time_order_with_referenced_payloads_and_manifest()
    {
        string root = Path.Combine(Path.GetTempPath(), "igw-log-export", Guid.NewGuid().ToString("N"));
        try
        {
            var payloads = new LogPayloadStore(root);
            LogPayloadReference payload = await payloads.SaveTextAsync("text/plain", "{\"code\":0}", CancellationToken.None);
            // 共 1,200 条；第 7 条带报文和设备上下文
            var all = Enumerable.Range(0, 1200).Select(i => Entry(From.AddSeconds(i * 6), $"消息 {i}",
                i == 7 ? payload.Id : null)).ToArray();

            await using var zip = new MemoryStream();
            LogExportSummary summary = await LogCenterExport.WriteAsync(zip, Request(From, From.AddHours(2)),
                all.ToAsyncEnumerable(), payloads, new LogExportOptions(), CancellationToken.None);

            Assert.Equal(1200, summary.Entries);
            Assert.False(summary.Truncated);
            Assert.Equal(1, summary.Payloads);

            zip.Position = 0;
            using var archive = new ZipArchive(zip, ZipArchiveMode.Read);
            string logs = await Read(archive, "logs.txt");
            Assert.True(logs.IndexOf("消息 0", StringComparison.Ordinal) < logs.IndexOf("消息 1199", StringComparison.Ordinal));
            Assert.Contains("device=PLC5.PLC5", logs);
            Assert.Contains($"报文=payloads/{payload.Id}.txt", logs);
            Assert.Equal("{\"code\":0}", await Read(archive, $"payloads/{payload.Id}.txt"));
            using JsonDocument manifest = JsonDocument.Parse(await Read(archive, "manifest.json"));
            Assert.Equal(1200, manifest.RootElement.GetProperty("entries").GetInt64());
            Assert.NotNull(archive.GetEntry("说明.txt"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Export_stops_at_the_entry_limit_and_says_so()
    {
        var all = Enumerable.Range(0, 50).Select(i => Entry(From.AddSeconds(i), $"消息 {i}", null)).ToArray();
        await using var zip = new MemoryStream();
        LogExportSummary summary = await LogCenterExport.WriteAsync(zip, Request(From, From.AddMinutes(5)),
            all.ToAsyncEnumerable(), null, new LogExportOptions(IncludePayloads: false, MaxEntries: 20), CancellationToken.None);

        Assert.Equal(20, summary.Entries);
        Assert.True(summary.Truncated);
        zip.Position = 0;
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read);
        Assert.Contains("缩小时间范围", await Read(archive, "说明.txt"));
    }

    [Fact]
    public void Interface_entries_carry_duration_and_http_status()
    {
        var attributes = new Dictionary<string, JsonElement>
        {
            ["igw_operation"] = JsonDocument.Parse("\"POST /Tag/ReadTagsAsList\"").RootElement.Clone(),
            ["igw_http_status_code"] = JsonDocument.Parse("200").RootElement.Clone(),
            ["igw_duration_ms"] = JsonDocument.Parse("1234.567").RootElement.Clone(),
        };
        string line = LogCenterExport.Format(
            new LogCenterEntry(From, "INFO", "interface", "HTTP 接口请求完成", null, null, attributes));

        Assert.Contains("HTTP=200", line);
        Assert.Contains("耗时=1235ms", line);
    }

    [Fact]
    public async Task QuestDb_jsonl_export_keeps_all_fields_and_identifies_its_source()
    {
        var row = new LogCenterEntry(From, "ERROR", "interface", "第一行\n第二行 \"原始\"", "trace-1", null,
            new Dictionary<string, JsonElement> { ["large"] = JsonSerializer.SerializeToElement(long.MaxValue), ["ok"] = JsonSerializer.SerializeToElement(false) });
        await using var file = new MemoryStream();
        await LogCenterExport.WriteAsync(file, Request(From, From.AddHours(1)), new[] { row }.ToAsyncEnumerable(), null,
            new LogExportOptions(Format: "jsonl", StorageEngine: "questdb"), default);
        file.Position = 0; using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        string raw = await Read(zip, "logs.jsonl");
        using var json = JsonDocument.Parse(raw);
        Assert.Equal(row.Message, json.RootElement.GetProperty("message").GetString());
        Assert.Equal(long.MaxValue, json.RootElement.GetProperty("attributes").GetProperty("large").GetInt64());
        Assert.False(json.RootElement.GetProperty("attributes").GetProperty("ok").GetBoolean());
        using var manifest = JsonDocument.Parse(await Read(zip, "manifest.json"));
        Assert.Equal("questdb", manifest.RootElement.GetProperty("storageEngine").GetString());
        Assert.Equal("jsonl", manifest.RootElement.GetProperty("format").GetString());
        Assert.Equal("logs.jsonl", manifest.RootElement.GetProperty("logFile").GetString());
    }

    [Fact]
    public async Task QuestDb_csv_export_quotes_multiline_fields_and_embeds_attributes()
    {
        var row = Entry(From, "含,逗号 \"引号\"\n第二行", null);
        await using var file = new MemoryStream();
        await LogCenterExport.WriteAsync(file, Request(From, From.AddHours(1)), new[] { row }.ToAsyncEnumerable(), null,
            new LogExportOptions(Format: "csv", StorageEngine: "questdb"), default);
        file.Position = 0; using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        string text = await Read(zip, "logs.csv");
        Assert.StartsWith("timestamp,level,category,message,traceId,payloadId,attributes", text);
        Assert.Contains("\"含,逗号 \"\"引号\"\"\n第二行\"", text);
        Assert.Contains("igw_device", text);
    }

    private static LogCenterRequest Request(DateTimeOffset from, DateTimeOffset to) => new() { From = from, To = to };

    private static LogCenterEntry Entry(DateTimeOffset at, string message, string? payloadId)
    {
        var attributes = new Dictionary<string, JsonElement>
        {
            ["igw_device"] = JsonDocument.Parse("\"PLC5.PLC5\"").RootElement.Clone()
        };
        return new LogCenterEntry(at, "INFO", "collection", message, null, payloadId, attributes);
    }

    private static async Task<string> Read(ZipArchive archive, string name)
    {
        using var reader = new StreamReader(archive.GetEntry(name)!.Open());
        return (await reader.ReadToEndAsync()).TrimStart('﻿');
    }
}
