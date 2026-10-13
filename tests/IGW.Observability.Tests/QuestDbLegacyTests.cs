using System.Text;
using IGW.Observability;
using Xunit;

namespace IGW.Observability.Tests;

public sealed class QuestDbLegacyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "igw-questdb-legacy", Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public void Text_log_parser_preserves_multiline_errors_and_logger_and_ignores_unrecognised_preamble()
    {
        string text = "ignored header\n2026-10-09 08:01:02.003 ERR [IGW.Plc] 读取失败\n第二行堆栈\n2026-10-09 08:01:03.004 INF [IGW.Mes] 上报完成\n";
        var rows = QuestDbLegacyText.Read(new StringReader(text)).ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal("读取失败\n第二行堆栈", rows[0].Message);
        Assert.Equal(4, rows[0].Level);
        Assert.Equal("IGW.Plc", rows[0].Fields["igw_logger"]);
        Assert.Equal("system", rows[0].Category);
    }

    [Fact]
    public void Historical_read_must_not_treat_a_missing_segment_as_success()
    {
        Directory.CreateDirectory(_root);
        var missing = new LogSegmentView(Path.Combine(_root, "missing.seg"), true, 1, new LogSegmentIndex { Count = 1 }, false);
        Assert.Throws<FileNotFoundException>(() => LogSegmentStore.ReadRaw(missing, requireExists: true));
    }

    [Fact]
    public async Task Cleanup_rejects_import_in_progress_and_keeps_unadopted_payload_fallbacks()
    {
        string data = Path.Combine(_root, "data"), old = Path.Combine(_root, "old"), current = Path.Combine(_root, "current");
        var settings = ObservabilitySettings.Initialize(data, old, 28931).WithStorageDirectory(current);
        string oldRoot = LogSegmentStore.RootFor(old); Directory.CreateDirectory(oldRoot);
        File.WriteAllText(Path.Combine(oldRoot, "old.seg"), "source");
        string fallback = LogPayloadStore.DirectoryFor(old); Directory.CreateDirectory(fallback);
        File.WriteAllText(Path.Combine(fallback, "x.payload"), "old payload");
        var payloads = new LogPayloadStore(LogPayloadStore.DirectoryFor(current), [fallback]);
        bool importing = true;
        var cleaner = new LogResidueCleaner(data, () => settings, payloads, scanRoots: () => [],
            blockedReason: () => importing ? "旧日志尚未导入完成" : null);
        Assert.False(cleaner.Start([oldRoot]));
        Assert.NotNull(cleaner.Status.BlockedReason);
        Assert.True(File.Exists(Path.Combine(oldRoot, "old.seg")));
        importing = false;
        Assert.DoesNotContain(cleaner.Find(), item => item.Path == oldRoot);
        payloads.AbandonFallback(fallback);
        Assert.Contains(cleaner.Find(), item => item.Path == oldRoot);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Failed_payload_adoption_keeps_its_source_readable()
    {
        string old = Path.Combine(_root, "old-payloads");
        var source = new LogPayloadStore(old);
        var reference = await source.SaveTextAsync("text/plain", "原始报文", default);
        var target = new LogPayloadStore(Path.Combine(_root, "new-payloads"), [old]);
        using (var blocked = new FileStream(reference.Path, FileMode.Open, FileAccess.Read, FileShare.None))
            target.AdoptFrom(old, move: true);
        Assert.Contains(Path.GetFullPath(old), target.FallbackDirectories);
        Assert.Equal("原始报文", (await target.ReadPageAsync(reference.Id, 0, default)).Content);
    }

    [Fact]
    public async Task A_corrupt_legacy_segment_does_not_hide_other_readable_segments()
    {
        string storage = Path.Combine(_root, "logs");
        await using (var writer = new LogSegmentStore(storage, 30))
        {
            writer.Append(new LogRecord(LogRecordCodec.ToMicros(DateTimeOffset.UtcNow), 2, "system", "正常旧日志", new Dictionary<string, object?>()));
            await writer.FlushAsync();
        }
        File.WriteAllBytes(Path.Combine(LogSegmentStore.RootFor(storage), "broken.seg"), [0xff, 0xff, 0xff]);
        await using var reader = new LogSegmentStore(storage, 30, readOnly: true);
        await reader.Recovery;
        Assert.Single(reader.RecoveryErrors);
        Assert.NotEmpty(reader.Snapshot(long.MinValue, long.MaxValue));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Startup_leaves_old_openobserve_data_untouched(bool useQuestDb)
    {
        string data = Path.Combine(_root, "data"), old = Path.Combine(_root, "old"), current = Path.Combine(_root, "current");
        var settings = ObservabilitySettings.Initialize(data, old, 28931).WithStorageDirectory(current); settings.Persist(data);
        foreach (string storage in new[] { old, current })
        {
            Directory.CreateDirectory(Path.Combine(storage, "db"));
            File.WriteAllText(Path.Combine(storage, "db", "metadata.sqlite"), "old openobserve metadata");
            Directory.CreateDirectory(Path.Combine(storage, "stream", "files"));
            File.WriteAllText(Path.Combine(storage, "stream", "files", "old.parquet"), "old logs");
        }
        string questDbFile = Path.Combine(QuestDbProcess.RootFor(current), "db", "keep.parquet");
        Directory.CreateDirectory(Path.GetDirectoryName(questDbFile)!); File.WriteAllText(questDbFile, "new database");
        string projectFile = Path.Combine(data, "project.db"); File.WriteAllText(projectFile, "engineering configuration");
        string oldSpool = Path.Combine(data, "telemetry-spool", "pending.json");
        Directory.CreateDirectory(Path.GetDirectoryName(oldSpool)!); File.WriteAllText(oldSpool, "old telemetry");
        string userFile = Path.Combine(current, "stream", "files", "keep.txt"); File.WriteAllText(userFile, "user file");
        await using (var telemetry = RuntimeTelemetry.Create(settings, data, useQuestDb, questDbRuntimeDirectory: Path.Combine(_root, "missing-runtime")))
        {
            await Task.Delay(300);
            foreach (string storage in new[] { old, current })
            {
                Assert.True(File.Exists(Path.Combine(storage, "db", "metadata.sqlite")));
                Assert.True(File.Exists(Path.Combine(storage, "stream", "files", "old.parquet")));
            }
            Assert.Equal("new database", File.ReadAllText(questDbFile));
            Assert.Equal("engineering configuration", File.ReadAllText(projectFile));
            Assert.Equal("old telemetry", File.ReadAllText(oldSpool));
            Assert.Equal("user file", File.ReadAllText(userFile));
        }
    }

    [Fact]
    public async Task Small_old_files_share_batches_and_expired_rows_do_not_reach_the_database()
    {
        string data = Path.Combine(_root, "data"), storage = Path.Combine(_root, "logs"); Directory.CreateDirectory(data);
        string root = LogSegmentStore.RootFor(storage); Directory.CreateDirectory(root);
        long start = LogRecordCodec.ToMicros(DateTimeOffset.UtcNow.AddHours(-1));
        for (int file = 0; file < 20; file++)
        {
            var buffer = new System.Buffers.ArrayBufferWriter<byte>();
            for (int row = 0; row < 20; row++)
                LogRecordCodec.Write(buffer, new LogRecord(start + file * 60_000_000L + row, 2, "system", "valid-" + file, new Dictionary<string, object?>()));
            File.WriteAllBytes(Path.Combine(root, file + ".jsonl"), buffer.WrittenSpan.ToArray());
        }
        var expired = new System.Buffers.ArrayBufferWriter<byte>();
        LogRecordCodec.Write(expired, new LogRecord(LogRecordCodec.ToMicros(DateTimeOffset.UtcNow.AddDays(-40)), 2, "system", "expired-record", new Dictionary<string, object?>()));
        File.WriteAllBytes(Path.Combine(root, "expired.jsonl"), expired.WrittenSpan.ToArray());
        var sent = new List<string>();
        using var client = new QuestDbClient(new Uri("http://127.0.0.1:28931"), "test", "secret", new HistoryHandler(sent));
        await using var store = new QuestDbLogStore(client, Path.Combine(data, "spool"));
        await using var catalog = new LogSegmentStore(storage, 30, readOnly: true);
        await using var importer = new QuestDbHistoryImporter(catalog, store, data);
        for (int attempt = 0; attempt < 200 && !importer.Completed; attempt++) await Task.Delay(50);
        Assert.True(importer.Completed, importer.Error);
        lock (sent)
        {
            Assert.InRange(sent.Count, 1, 2);
            Assert.DoesNotContain(sent, batch => batch.Contains("expired-record"));
            Assert.Equal(400, sent.Sum(batch => batch.Count(ch => ch == '\n')));
        }
        Assert.True(File.Exists(Path.Combine(root, "expired.jsonl")));
    }
    private sealed class HistoryHandler(List<string> sent) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = await request.Content!.ReadAsStringAsync(cancellationToken);
            lock (sent) sent.Add(body);
            return new HttpResponseMessage(System.Net.HttpStatusCode.NoContent);
        }
    }

    [Fact]
    public async Task Low_capacity_history_cache_still_imports_large_files_and_later_small_files()
    {
        string data = Path.Combine(_root, "data"), storage = Path.Combine(_root, "logs"); Directory.CreateDirectory(data);
        string root = LogSegmentStore.RootFor(storage); Directory.CreateDirectory(root);
        for (int file = 0; file < 5; file++)
        {
            var buffer = new System.Buffers.ArrayBufferWriter<byte>();
            LogRecordCodec.Write(buffer, new LogRecord(LogRecordCodec.ToMicros(DateTimeOffset.UtcNow.AddMinutes(file - 10)), 2, "system",
                file == 4 ? "later-good" : new string('x', 350000), new Dictionary<string, object?>()));
            File.WriteAllBytes(Path.Combine(root, file + ".jsonl"), buffer.WrittenSpan.ToArray());
        }
        var sent = new List<string>();
        using var client = new QuestDbClient(new Uri("http://127.0.0.1:28931"), "test", "secret", new HistoryHandler(sent));
        await using var store = new QuestDbLogStore(client, Path.Combine(data, "spool"), diskLimitBytes: 4 * 1024 * 1024);
        await using var catalog = new LogSegmentStore(storage, 30, readOnly: true);
        await using var importer = new QuestDbHistoryImporter(catalog, store, data);
        for (int attempt = 0; attempt < 200 && !importer.Completed && importer.Error is null; attempt++) await Task.Delay(50);
        Assert.True(importer.Completed, importer.Error);
        lock (sent) Assert.Contains(sent, batch => batch.Contains("later-good"));
        Assert.Equal(5, importer.ImportedFiles);
    }
}
