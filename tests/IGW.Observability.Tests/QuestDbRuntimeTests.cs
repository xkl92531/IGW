using System.Net;
using System.Net.Sockets;
using System.Text;
using IGW.Observability;
using Microsoft.Extensions.Logging;
using Xunit;

namespace IGW.Observability.Tests;

public sealed class QuestDbRuntimeTests : IDisposable
{
    private string? _data;
    public void Dispose()
    {
        if (_data is null) return;
        // On Windows, QuestDB's mapped partition files can remain locked briefly after its child exits.
        for (int attempt = 0; Directory.Exists(_data); attempt++)
        {
            try { Directory.Delete(_data, recursive: true); }
            catch (Exception error) when (attempt < 40 && error is IOException or UnauthorizedAccessException)
            { Thread.Sleep(50); }
        }
    }
    [NativeRuntimeFact]
    public async Task Legacy_QuestDB_tables_keep_table_identity_WAL_dedup_and_payload_references_after_namespace_migration()
    {
        string data = Path.Combine(Path.GetTempPath(), "igw-questdb-tests", Guid.NewGuid().ToString("N")); _data = data;
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var settings = ObservabilitySettings.Initialize(data, Path.Combine(data, "logs"), port);
        await using var process = new QuestDbProcess(settings, Environment.GetEnvironmentVariable("IGW_QUESTDB_TEST_RUNTIME"));
        using var client = new QuestDbClient(new Uri($"http://127.0.0.1:{port}"), settings.Username, settings.Password);
        process.Start();
        for (int attempt = 0; ; attempt++)
        {
            try { using var ready = await client.QueryAsync("select 1", default); break; }
            catch when (attempt < 100) { await Task.Delay(50); }
        }
        string fields = string.Join(',', QuestDbLogCodec.Fields.Select(field => field + (QuestDbLogCodec.IndexedFields.Contains(field) ? " SYMBOL NOCACHE INDEX" : " VARCHAR")));
        using (await client.QueryAsync($"create table igw_logs(ts TIMESTAMP,id UUID,level INT,category SYMBOL INDEX,raw VARCHAR,{fields}) timestamp(ts) partition by HOUR ttl 30 DAYS WAL DEDUP UPSERT KEYS(ts,id)", default)) { }
        using (await client.QueryAsync("create table igw_payloads(ts TIMESTAMP,id UUID,content_type VARCHAR,body VARCHAR) timestamp(ts) partition by HOUR ttl 30 DAYS WAL DEDUP UPSERT KEYS(ts,id)", default)) { }
        using var before = await client.QueryAsync("select id,directoryName from tables() where table_name in ('igw_logs','igw_payloads') order by id", default);
        DateTimeOffset at = DateTimeOffset.UtcNow;
        long timestamp = LogRecordCodec.ToMicros(at);
        Guid payloadId = Guid.CreateVersion7(at), logId = Guid.NewGuid();
        var record = new LogRecord(timestamp, 4, "interface", "legacy-migration", new Dictionary<string, object?> { ["igw_payload_id"] = payloadId.ToString("N"), ["igw_device"] = "PLC6" });
        byte[] batch = Encoding.UTF8.GetBytes(QuestDbLogCodec.Encode(record, logId) + QuestDbLogCodec.Payload(payloadId, timestamp, "text/plain", "原始报文"));
        await client.WriteAsync(batch, default);
        await client.InitializeAsync(30, default);
        await client.InitializeAsync(30, default);
        await client.WriteAsync(batch, default);
        using var after = await client.QueryAsync("select id,directoryName from tables() where table_name in ('IGW_logs','IGW_payloads') order by id", default);
        Assert.Equal(before.RootElement.GetProperty("dataset").GetRawText(), after.RootElement.GetProperty("dataset").GetRawText());
        using var catalog = await client.QueryAsync("select table_name from tables() where table_name in ('IGW_logs','IGW_payloads') order by table_name", default);
        Assert.Equal(new[] { "IGW_logs", "IGW_payloads" }, catalog.RootElement.GetProperty("dataset").EnumerateArray().Select(row => row[0].GetString()));
        var query = new QuestDbLogQuery(client);
        var request = new LogCenterRequest { From = at.AddMinutes(-1), To = at.AddMinutes(1), Path = "PLC6" };
        LogCenterResponse? result = null;
        for (int attempt = 0; attempt < 200; attempt++)
        {
            result = await query.QueryAsync(request, default);
            if (result.Total == 1 && result.Items.Count == 1 && !result.Partial) break;
            await Task.Delay(50);
        }
        Assert.Equal(1, result!.Total); Assert.Equal(1, result.Histogram.Sum(bucket => bucket.Total));
        Assert.Equal(payloadId.ToString("N"), Assert.Single(result.Items).PayloadId);
        await using var spool = new QuestDbLogStore(client, Path.Combine(data, "spool"));
        var payloads = new QuestDbPayloadStore(client, spool);
        Assert.Equal("原始报文", (await payloads.ReadAsync(payloadId.ToString("N"), default))!.Content);
        using var wal = await client.QueryAsync("select name,suspended from wal_tables() where name in ('IGW_logs','IGW_payloads')", default);
        Assert.Equal(2, wal.RootElement.GetProperty("dataset").GetArrayLength());
        Assert.All(wal.RootElement.GetProperty("dataset").EnumerateArray(), row => Assert.False(row[1].GetBoolean()));
    }

    [NativeRuntimeFact]
    public async Task Runtime_starts_database_reads_payloads_and_recovers_history_and_directory_move()
    {
        string data = Path.Combine(Path.GetTempPath(), "igw-questdb-tests", Guid.NewGuid().ToString("N"));
        _data = data;
        string storage = Path.Combine(data, "logs");
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var settings = ObservabilitySettings.Initialize(data, storage, port);
        await using (var legacy = new LogSegmentStore(storage, 30))
        {
            legacy.Append(new LogRecord(LogRecordCodec.ToMicros(DateTimeOffset.UtcNow.AddMinutes(-1)), 2, "system", "旧日志", new Dictionary<string, object?>()));
            await legacy.FlushAsync();
        }
        await using (var telemetry = RuntimeTelemetry.Create(settings, data, questDbRuntimeDirectory: Environment.GetEnvironmentVariable("IGW_QUESTDB_TEST_RUNTIME")))
        {
            telemetry.LoggerFactory.CreateLogger("PLC").LogInformation("新日志 {igw_device}", "PLC6.D1");
            var payload = await telemetry.Payloads.SaveTextAsync("text/plain", "第一行\n第二行 中文 token=secret", default);
            LogCenterResponse result = await WaitAsync(telemetry.Backend, 2);
            Assert.Contains(result.Items, entry => entry.Message == "旧日志");
            Assert.Contains(result.Items, entry => entry.Message.Contains("新日志"));
            LogPayloadPage page = await telemetry.Payloads.ReadPageAsync(payload.Id, 0, default);
            Assert.Equal("第一行\n第二行 中文 token=***", page.Content);
            var download = await telemetry.Payloads.OpenReadAsync(payload.Id, default);
            using var reader = new StreamReader(download.Stream);
            Assert.Equal(page.Content, await reader.ReadToEndAsync());
            if (Directory.Exists(LogPayloadStore.DirectoryFor(storage))) Assert.Empty(Directory.GetFiles(LogPayloadStore.DirectoryFor(storage), "*.payload"));
            LogCenterStatus status = await telemetry.Backend.StatusAsync(default);
            for (int attempt = 0; attempt < 20 && !status.Available; attempt++)
            { await Task.Delay(300); status = await telemetry.Backend.StatusAsync(default); }
            Assert.True(status.Available, System.Text.Json.JsonSerializer.Serialize(status));
            await telemetry.Backend.UpdateRetentionAsync(14, default);
            Assert.Equal(14, telemetry.Backend.Settings.RetentionDays);
            string target = Path.Combine(data, "moved");
            var moved = await telemetry.Backend.BeginStorageChangeAsync(target, default);
            for (int attempt = 0; attempt < 800 && moved.Status is not (LogStorageChangeStatus.Completed or LogStorageChangeStatus.Failed); attempt++) await Task.Delay(50);
            Assert.Equal(LogStorageChangeStatus.Completed, moved.Status);
            Assert.Equal(target, telemetry.Backend.Settings.StorageDirectory);
            Assert.Equal(2, (await WaitAsync(telemetry.Backend, 2)).Total);
            Assert.Equal(page.Content, (await telemetry.Payloads.ReadPageAsync(payload.Id, 0, default)).Content);
            telemetry.LoggerFactory.CreateLogger("PLC").LogWarning("搬迁后的日志");
            await WaitAsync(telemetry.Backend, 3);

            // Config is readable but atomic replacement is denied. A failed move must restart the original DB.
            using (var locked = new FileStream(Path.Combine(data, ObservabilitySettings.FileName), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await Assert.ThrowsAnyAsync<Exception>(() => telemetry.Backend.UpdateRetentionAsync(7, default));
                Assert.Equal(14, telemetry.Backend.Settings.RetentionDays);
                var failed = await telemetry.Backend.BeginStorageChangeAsync(Path.Combine(data, "failed-move"), default);
                for (int attempt = 0; attempt < 400 && failed.Status is not LogStorageChangeStatus.Failed; attempt++) await Task.Delay(50);
                Assert.Equal(LogStorageChangeStatus.Failed, failed.Status);
                Assert.Equal(target, telemetry.Backend.Settings.StorageDirectory);
                Assert.Equal(3, (await WaitAsync(telemetry.Backend, 3)).Total);
            }
        }
        settings = ObservabilitySettings.Load(data)!;
        await using (var restarted = RuntimeTelemetry.Create(settings, data, questDbRuntimeDirectory: Environment.GetEnvironmentVariable("IGW_QUESTDB_TEST_RUNTIME")))
        {
            Assert.Equal(3, (await WaitAsync(restarted.Backend, 3)).Total);
        }
    }

    [NativeRuntimeFact]
    public async Task Migrated_and_live_logs_apply_scope_and_associated_task_filters_to_list_trend_and_export()
    {
        // Own GUID data root and OS-selected loopback port; never connect to IGW_QUESTDB_TEST_URL.
        string data = Path.Combine(Path.GetTempPath(), "igw-questdb-tests", Guid.NewGuid().ToString("N")); _data = data;
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var settings = ObservabilitySettings.Initialize(data, Path.Combine(data, "logs"), port);
        DateTimeOffset at = DateTimeOffset.UtcNow.AddMinutes(-1);
        var records = new List<LogRecord>();
        void Add(string message, params (string Key, object? Value)[] fields) => records.Add(new LogRecord(
            LogRecordCodec.ToMicros(at) + records.Count, records.Count % 2 == 0 ? 3 : 4, "interface", message,
            fields.ToDictionary(field => field.Key, field => field.Value)));
        Add("single", ("igw_scope", "PLC6.D1.Group"), ("igw_task_id", "old-single"));
        Add("child", ("igw_scope", "PLC6.D1.Group.Sub"), ("igw_task_id", "old-child"));
        Add("shared", ("igw_scope", "PLC7.D1.Other, PLC6.D1.Group.Sub"), ("igw_task_ids", "old-other, old-shared"));
        Add("punctuation", ("igw_scope", "PLC6.D1.Group(2)"), ("igw_task_id", "old-punctuation"));
        Add("comma", ("igw_scope", "PLC6.D1.Group, 2"), ("igw_task_id", "old-comma"));
        Add("shared-punctuation", ("igw_scope", "PLC7.D1.Other, PLC6.D1.Group(2)"), ("igw_task_ids", "old-other, old-punctuation"));
        Add("shared-comma", ("igw_scope", "PLC7.D1.Other, PLC6.D1.Group, 2"), ("igw_task_ids", "old-other, old-comma"));
        Add("bound", ("igw_task_id", "task-1"), ("igw_target", "http://mes.example/api/report?token=***"));
        Add("bound-shared", ("igw_task_ids", " other,\ttask-1  , last"));
        Add("near", ("igw_task_id", "task-10"));
        Add("near-shared", ("igw_task_ids", "other, task-10"));
        Add("other-bound", ("igw_task_id", "task-2"), ("igw_target", "http://other.example/ping"));
        Add("named", ("igw_task_name", "StationEntrance"));
        Add("message-only /api/report private-body");
        Add("private", ("igw_payload_id", "private-body"), ("igw_error_type", "private-body"));
        Add("mention", ("igw_target", "PLC6.D1.Group.Result"));
        // Raw legacy rows have igw_scope but 1.7.20 has no scope hot column. No schema upgrade should be required.
        await using (var legacy = new LogSegmentStore(settings.StorageDirectory, 30))
        {
            foreach (var row in records.Take(records.Count - 1)) legacy.Append(row);
            await legacy.FlushAsync();
        }
        await using var telemetry = RuntimeTelemetry.Create(settings, data,
            questDbRuntimeDirectory: Environment.GetEnvironmentVariable("IGW_QUESTDB_TEST_RUNTIME"));
        telemetry.Database!.Store.Append(records[^1]);
        await WaitAsync(telemetry.Backend, records.Count);
        for (int attempt = 0; attempt < 200 && !telemetry.Database.History.Completed; attempt++) await Task.Delay(50);
        Assert.True(telemetry.Database.History.Completed);
        var cases = new (string? Path, string[] PathIds, string? Term, string[] TermIds, string[] Expected)[]
        {
            ("PLC6.D1.Group", [], null, [], ["single", "child", "shared", "mention"]),
            ("PLC6.D1.Group(2)", [], null, [], ["punctuation", "shared-punctuation"]),
            ("PLC6.D1.Group, 2", [], null, [], ["comma"]),
            ("PLC6.D1.Group", ["task-1"], null, [], ["single", "child", "shared", "bound", "bound-shared", "mention"]),
            ("PLC6.D1.Group", ["task-2"], null, [], ["single", "child", "shared", "other-bound", "mention"]),
            (null, [], "StationEntrance", [], ["named"]),
            (null, [], "Group", ["task-1"], ["single", "child", "shared", "punctuation", "comma", "shared-punctuation", "shared-comma", "bound", "bound-shared", "mention"]),
            (null, [], "StationEntrance", ["task-1"], ["named", "bound", "bound-shared"]),
            (null, [], "StationEntrance", ["task-2"], ["named", "other-bound"]),
            (null, [], "/api/report", [], ["bound"]),
            (null, [], "private-body", [], []),
            ("PLC6.D1.Group", ["task-1"], "/api/report", [], ["bound"]),
            ("PLC6.D1.Group", ["task-1"], "StationEntrance", ["task-2"], []),
            ("unmatched", ["task-1"], "StationEntrance", ["task-1"], ["bound", "bound-shared"]),
        };
        foreach (var item in cases)
        {
            var request = new LogCenterRequest
            {
                From = at.AddMinutes(-1), To = at.AddMinutes(1), Path = item.Path, PathTaskIds = item.PathIds,
                TaskOrInterface = item.Term, TaskOrInterfaceTaskIds = item.TermIds
            };
            var result = await telemetry.Backend.QueryAsync(request, default);
            Assert.False(result.Partial);
            Assert.Equal(item.Expected.Order(StringComparer.Ordinal), result.Items.Select(row => row.Message).Order(StringComparer.Ordinal));
            Assert.Equal(item.Expected.Length, result.Total);
            Assert.Equal(item.Expected.Length, result.Histogram.Sum(bucket => bucket.Total));
            Assert.Equal(result.Items.Count(row => row.Level == "WARN"), result.Histogram.Sum(bucket => bucket.Warnings));
            Assert.Equal(result.Items.Count(row => row.Level == "ERROR"), result.Histogram.Sum(bucket => bucket.Errors));
            foreach (string format in new[] { "jsonl", "csv", "text" })
            {
                var exported = await LogCenterExport.CreateAsync(request, telemetry.Backend.StreamAsync(request, default),
                    telemetry.Payloads, new LogExportOptions(IncludePayloads: false, Format: format, StorageEngine: "questdb"), default);
                await using (exported.Archive)
                {
                    Assert.Equal(item.Expected.Length, exported.Summary.Entries);
                    using var zip = new System.IO.Compression.ZipArchive(exported.Archive, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: true);
                    Assert.NotNull(zip.GetEntry(format == "text" ? "logs.txt" : "logs." + format));
                    using var manifest = await System.Text.Json.JsonDocument.ParseAsync(zip.GetEntry("manifest.json")!.Open());
                    Assert.Equal(item.Term, manifest.RootElement.GetProperty("filters").GetProperty("taskOrInterface").GetString());
                }
            }
        }
    }

    private static async Task<LogCenterResponse> WaitAsync(LogCenterBackend backend, int expected)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            var response = await backend.QueryAsync(new LogCenterRequest { From = DateTimeOffset.UtcNow.AddHours(-1), To = DateTimeOffset.UtcNow.AddMinutes(1), PageSize = expected }, default);
            // The page and aggregate queries can straddle a WAL commit during background import.
            if (response.Total == expected && response.Items.Count == expected && !response.Partial) return response;
            await Task.Delay(50);
        }
        throw new TimeoutException("Runtime 日志或历史导入没有变为可查询状态。");
    }

    [NativeRuntimeFact]
    public async Task Existing_storage_settings_move_the_database_and_keep_all_legacy_formats_and_payloads()
    {
        string data = Path.Combine(Path.GetTempPath(), "igw-questdb-tests", Guid.NewGuid().ToString("N")); _data = data;
        string old = Path.Combine(data, "old"), current = Path.Combine(data, "current");
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var settings = ObservabilitySettings.Initialize(data, old, port).WithStorageDirectory(current); settings.Persist(data);
        var oldPayloads = new LogPayloadStore(LogPayloadStore.DirectoryFor(old));
        var payload = await oldPayloads.SaveTextAsync("text/plain", "历史接口报文", default);
        await using (var writer = new LogSegmentStore(old, 30))
        {
            writer.Append(new LogRecord(LogRecordCodec.ToMicros(DateTimeOffset.UtcNow.AddMinutes(-3)), 4, "system", "compat-old-path",
                new Dictionary<string, object?> { ["igw_payload_id"] = payload.Id })); await writer.FlushAsync();
        }
        await using (var writer = new LogSegmentStore(current, 30))
        {
            writer.Append(new LogRecord(LogRecordCodec.ToMicros(DateTimeOffset.UtcNow.AddMinutes(-2)), 2, "system", "compat-old-current", new Dictionary<string, object?>())); await writer.FlushAsync();
        }
        string textDirectory = Path.Combine(data, "logs"); Directory.CreateDirectory(textDirectory);
        string textFile = Path.Combine(textDirectory, "igw-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
        await File.WriteAllTextAsync(textFile, DateTime.Now.AddMinutes(-1).ToString("yyyy-MM-dd HH:mm:ss.fff") + " ERR [Legacy.Plc] compat-old-text\n多行堆栈\n");
        await using (var telemetry = RuntimeTelemetry.Create(settings, data, questDbRuntimeDirectory: Environment.GetEnvironmentVariable("IGW_QUESTDB_TEST_RUNTIME")))
        {
            telemetry.LoggerFactory.CreateLogger("PLC").LogInformation("compat-new");
            var request = new LogCenterRequest { From = DateTimeOffset.UtcNow.AddHours(-1), To = DateTimeOffset.UtcNow.AddMinutes(1), Search = "compat-" };
            async Task<LogCenterResponse> QueryReady()
            {
                for (int attempt = 0; attempt < 200; attempt++)
                {
                    var result = await telemetry.Backend.QueryAsync(request, default);
                    if (result.Total == 4 && result.Items.Count == 4 && !result.Partial) return result;
                    await Task.Delay(100);
                }
                throw new TimeoutException(System.Text.Json.JsonSerializer.Serialize(await telemetry.Backend.StatusAsync(default)));
            }
            var imported = await QueryReady();
            Assert.Contains(imported.Items, row => row.Message == "compat-old-text\n多行堆栈");
            Assert.Equal("历史接口报文", (await telemetry.Payloads.ReadPageAsync(payload.Id, 0, default)).Content);
            var status = await telemetry.Backend.StatusAsync(default);
            Assert.True(status.HistoryImported); Assert.Equal(3, status.HistoryTotalFiles); Assert.Equal(3, status.HistoryImportedFiles);
            Assert.True(File.Exists(textFile));
            var exported = await LogCenterExport.CreateAsync(request, telemetry.Backend.StreamAsync(request, default), telemetry.Payloads,
                new LogExportOptions(Format: "jsonl", StorageEngine: "questdb"), default);
            await using (exported.Archive)
            {
                using var archive = new System.IO.Compression.ZipArchive(exported.Archive, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: true);
                using var reader = new StreamReader(archive.GetEntry("logs.jsonl")!.Open());
                string[] lines = (await reader.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                Assert.Equal(4, lines.Length);
                Assert.Contains(lines, line => System.Text.Json.JsonDocument.Parse(line).RootElement.GetProperty("message").GetString() == "compat-new");
                Assert.NotNull(archive.GetEntry($"payloads/{payload.Id}.txt"));
            }
            string moved = Path.Combine(data, "moved");
            var change = await telemetry.Backend.BeginStorageChangeAsync(moved, default);
            for (int attempt = 0; attempt < 800 && change.Status is not (LogStorageChangeStatus.Completed or LogStorageChangeStatus.Failed); attempt++) await Task.Delay(50);
            Assert.Equal(LogStorageChangeStatus.Completed, change.Status);
            Assert.Equal(moved, telemetry.Backend.Settings.StorageDirectory);
            Assert.Equal(4, (await QueryReady()).Total);
            Assert.Equal("历史接口报文", (await telemetry.Payloads.ReadPageAsync(payload.Id, 0, default)).Content);
            Assert.Empty(telemetry.Payloads.FallbackDirectories);
        }
    }
}

public sealed class NativeRuntimeFactAttribute : FactAttribute
{
    public NativeRuntimeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("IGW_QUESTDB_TEST_RUNTIME") is null) Skip = "Set IGW_QUESTDB_TEST_RUNTIME to the pinned native runtime directory.";
    }
}
