using IGW.Observability;
using Microsoft.Extensions.Logging;
using Xunit;

namespace IGW.Tests;

public sealed class LogSegmentStoreTests
{
    private static readonly DateTimeOffset Base = DateTimeOffset.UtcNow.AddHours(-3);

    [Fact]
    public async Task Records_survive_sealing_compression_and_restart_and_query_newest_first()
    {
        string root = NewDirectory();
        try
        {
            await using (var store = new LogSegmentStore(root, 30))
            {
                for (int i = 0; i < 1000; i++) store.Append(Record(Base.AddSeconds(i), i % 10 == 0 ? 4 : 2, $"消息 {i}", "PLC5.OP050"));
                await store.FlushAsync();
                store.SealNow();
                await store.WaitForCompressionAsync();
                Assert.NotEmpty(Directory.GetFiles(LogSegmentStore.RootFor(root), "*.seg", SearchOption.AllDirectories));
                Assert.Empty(Directory.GetFiles(LogSegmentStore.RootFor(root), "*.jsonl", SearchOption.AllDirectories));
            }

            await using var reopened = new LogSegmentStore(root, 30);
            LogCenterResponse page = LogQueryEngine.Query(reopened, Request(pageSize: 5));
            Assert.Equal(1000, page.Total);
            Assert.Equal(["消息 999", "消息 998", "消息 997", "消息 996", "消息 995"], page.Items.Select(item => item.Message));

            LogCenterResponse second = LogQueryEngine.Query(reopened, Request(page: 2, pageSize: 5));
            Assert.Equal("消息 994", second.Items[0].Message);

            LogCenterResponse errors = LogQueryEngine.Query(reopened, Request(level: "error"));
            Assert.Equal(100, errors.Total);
            Assert.All(errors.Items, item => Assert.Equal("ERROR", item.Level));
            Assert.Equal(1000, page.Histogram.Sum(bucket => bucket.Total));
            Assert.Equal(100, page.Histogram.Sum(bucket => bucket.Errors));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Crash_leftover_segment_is_recovered_and_a_torn_last_line_is_ignored()
    {
        string root = NewDirectory();
        try
        {
            string day = Path.Combine(LogSegmentStore.RootFor(root), Base.ToString("yyyyMMdd"));
            Directory.CreateDirectory(day);
            var buffer = new System.Buffers.ArrayBufferWriter<byte>();
            for (int i = 0; i < 3; i++) LogRecordCodec.Write(buffer, Record(Base.AddSeconds(i), 2, $"断电前 {i}", "PLC7"));
            byte[] content = [.. buffer.WrittenSpan, .. "{\"t\":123,\"l\":2,\"c\":\"sys"u8];   // 最后一行只写了一半
            File.WriteAllBytes(Path.Combine(day, "120000-abcdef.jsonl"), content);

            await using var store = new LogSegmentStore(root, 30);
            await store.WaitForCompressionAsync();
            LogCenterResponse page = LogQueryEngine.Query(store, Request());
            Assert.Equal(3, page.Total);
            Assert.Equal("断电前 2", page.Items[0].Message);
            Assert.NotEmpty(Directory.GetFiles(day, "*.seg"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Filters_match_exact_fields_task_text_and_keywords()
    {
        string root = NewDirectory();
        try
        {
            await using var store = new LogSegmentStore(root, 30);
            store.Append(Record(Base, 2, "MES 回写完成", "PLC5.OP050", ("igw_task_name", "StationEXIT-21"), ("igw_trace_id", "abc123")));
            store.Append(Record(Base.AddSeconds(1), 3, "MES 请求超时 \"quoted\"", "PLC7.OP090"));
            store.Append(Record(Base.AddSeconds(2), 2, "设备上线", "PLC7.OP090", ("igw_category", "state")));
            await store.FlushAsync();

            Assert.Equal(1, LogQueryEngine.Query(store, Request(device: "PLC5.OP050")).Total);
            Assert.Equal(0, LogQueryEngine.Query(store, Request(device: "PLC5")).Total);            // 设备是精确匹配
            Assert.Equal(1, LogQueryEngine.Query(store, Request(task: "EXIT-21")).Total);           // 任务是包含匹配
            Assert.Equal(1, LogQueryEngine.Query(store, Request(traceId: "abc123")).Total);
            Assert.Equal(1, LogQueryEngine.Query(store, Request(search: "超时")).Total);
            Assert.Equal(1, LogQueryEngine.Query(store, Request(search: "\"quoted\"")).Total);      // 需要转义的关键词走逐条判断
            Assert.Equal(1, LogQueryEngine.Query(store, Request(search: "StationEXIT")).Total);     // 关键词也查任务名
            Assert.Equal(1, LogQueryEngine.Query(store, Request(category: "state")).Total);
            Assert.Equal(1, LogQueryEngine.Query(store, Request(level: "warning")).Total);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Sealed_segments_are_skipped_by_index_when_they_cannot_match()
    {
        string root = NewDirectory();
        try
        {
            await using var store = new LogSegmentStore(root, 30);
            store.Append(Record(Base, 2, "只有信息", "PLC5"));
            await store.FlushAsync();
            store.SealNow();
            LogSegmentView segment = Assert.Single(store.Snapshot(0, long.MaxValue));
            Assert.True(LogFilter.Create(Request(level: "error")).CanSkip(segment));
            Assert.True(LogFilter.Create(Request(device: "PLC9")).CanSkip(segment));
            Assert.False(LogFilter.Create(Request(device: "PLC5")).CanSkip(segment));
            Assert.False(LogFilter.Create(Request(search: "信息")).CanSkip(segment));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Expired_segments_are_deleted_by_retention()
    {
        string root = NewDirectory();
        try
        {
            await using var store = new LogSegmentStore(root, 1);
            store.Append(Record(DateTimeOffset.UtcNow.AddDays(-3), 2, "三天前", "PLC5"));
            await store.FlushAsync();
            store.SealNow();
            store.Append(Record(DateTimeOffset.UtcNow.AddMinutes(-1), 2, "刚才", "PLC5"));
            await store.FlushAsync();
            await store.WaitForCompressionAsync();

            store.CleanupExpired();

            var request = new LogCenterRequest { From = DateTimeOffset.UtcNow.AddDays(-5), To = DateTimeOffset.UtcNow.AddMinutes(1) };
            LogCenterResponse page = LogQueryEngine.Query(store, request);
            Assert.Equal("刚才", Assert.Single(page.Items).Message);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Runtime_logger_writes_context_scopes_and_exceptions_into_the_store()
    {
        string root = NewDirectory();
        try
        {
            ObservabilitySettings settings = ObservabilitySettings.Initialize(root, Path.Combine(root, "store"), 5088);
            await using (var telemetry = RuntimeTelemetry.Create(settings, root, useQuestDb: false))
            {
                using ILoggerProvider provider = telemetry.CreateLoggerProvider();
                ILogger logger = provider.CreateLogger("MesUpload");
                using (logger.BeginScope(new Dictionary<string, object?> { ["igw_task_id"] = "task-7", ["igw_task_name"] = "OP050 出站" }))
                    logger.LogError(new InvalidOperationException("PLC 拒绝写入"), "回写失败：{tag}", "PLC5.OP050.Result");
                await telemetry.Store.FlushAsync();

                LogCenterEntry entry = Assert.Single(LogQueryEngine.Query(telemetry.Store,
                    new LogCenterRequest { From = DateTimeOffset.UtcNow.AddMinutes(-1), To = DateTimeOffset.UtcNow.AddMinutes(1) }).Items);
                Assert.Equal("回写失败：PLC5.OP050.Result", entry.Message);
                Assert.Equal("ERROR", entry.Level);
                Assert.Equal("task-7", entry.Attributes["igw_task_id"].GetString());
                Assert.Equal("mes", entry.Attributes["igw_module"].GetString());
                Assert.Equal("PLC5.OP050.Result", entry.Attributes["tag"].GetString());
                Assert.Contains("PLC 拒绝写入", entry.Attributes["exception_stacktrace"].GetString());
            }
            Assert.False(Directory.Exists(Path.Combine(root, "telemetry-spool")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Obsolete_OpenObserve_data_is_removed_without_touching_other_files()
    {
        string root = NewDirectory();
        try
        {
            void Write(string relative, int bytes)
            {
                string path = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, new byte[bytes]);
            }
            Write(@"db\metadata.sqlite", 100);
            Write(@"wal\logs\0\default\logs\1.wal", 1000);
            Write(@"stream\files\default\logs\igw_runtime\2026\09\18\a.parquet", 500);
            Write(@"mmdb\GeoLite2-City.mmdb", 300);
            Write(@"stream\files\default\bloom\igw_runtime_logs\2026\09\13\09\1.bf", 20);
            Write(@"stream\files\default\index\igw_runtime_logs\2026\09\13\09\1.ttv", 30);
            Write(@"cache\metrics_results\default\2026\09\13\13\a.pb", 40);
            Write(@"cache\results\default\q.json", 10);
            Directory.CreateDirectory(Path.Combine(root, "database"));
            Write(@"tmp\用户自己的文件.txt", 10);
            Write(@"notes.txt", 10);
            Write(@"igw-logs\20260919\000000-abc.seg", 10);

            long freed = RuntimeTelemetry.RemoveObsoleteOpenObserveData(root);

            Assert.Equal(2000, freed);
            foreach (string gone in new[] { "db", "wal", "stream", "mmdb", "database", "cache" })
                Assert.False(Directory.Exists(Path.Combine(root, gone)), gone);
            Assert.True(File.Exists(Path.Combine(root, @"tmp\用户自己的文件.txt")));
            Assert.True(File.Exists(Path.Combine(root, "notes.txt")));
            Assert.True(File.Exists(Path.Combine(root, @"igw-logs\20260919\000000-abc.seg")));
            Assert.Equal(0, RuntimeTelemetry.RemoveObsoleteOpenObserveData(root));   // 没有 OpenObserve 特征时什么都不删

            // 上一版只清理了一部分(元数据库已删、索引文件还在)：再次启动时凭 stream/files 继续清理。
            Write(@"stream\files\default\index\igw_runtime_logs\2026\09\13\10\2.ttv", 50);
            Assert.Equal(50, RuntimeTelemetry.RemoveObsoleteOpenObserveData(root));
            Assert.False(Directory.Exists(Path.Combine(root, "stream")));
            Write(@"wal\files\default\metrics\igw_events\0\a.parquet", 70);   // 指标暂存也是 OpenObserve 的
            Assert.Equal(70, RuntimeTelemetry.RemoveObsoleteOpenObserveData(root));
            Assert.False(Directory.Exists(Path.Combine(root, "wal")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ---------- 按日志分类拆文件夹：igw-logs\{分类}\yyyyMMdd\ ----------

    [Fact]
    public async Task Each_category_is_written_to_its_own_folder_and_unknown_categories_go_to_system()
    {
        string root = NewDirectory();
        try
        {
            await using var store = new LogSegmentStore(root, 30);
            string[] categories = ["collection", "interface", "state", "performance", "system"];
            for (int i = 0; i < categories.Length; i++)
                store.Append(Record(Base.AddSeconds(i), 2, categories[i] + " 日志", "PLC1", ("igw_category", categories[i])));
            store.Append(Record(Base.AddSeconds(9), 2, "未知分类", "PLC1", ("igw_category", "custom")));
            await store.FlushAsync();
            store.SealNow();
            await store.WaitForCompressionAsync();

            string logs = LogSegmentStore.RootFor(root);
            string day = Base.ToLocalTime().ToString("yyyyMMdd");
            foreach (string category in categories)
                Assert.NotEmpty(Directory.GetFiles(Path.Combine(logs, category, day), "*.seg"));
            Assert.False(Directory.Exists(Path.Combine(logs, day)));   // 不再写到 igw-logs\日期\ 下
            Assert.Equal(categories.Length, store.Snapshot(0, long.MaxValue).Count);
            // 不认识的分类放在 system 文件夹里，记录本身的分类不变，「全部」照常查得到
            Assert.Contains(LogQueryEngine.Query(store, Request()).Items, item => item.Message == "未知分类");
            Assert.Equal(categories.Length + 1, LogQueryEngine.Query(store, Request()).Total);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task All_categories_query_and_export_stay_in_time_order_across_category_folders()
    {
        string root = NewDirectory();
        try
        {
            await using (var store = new LogSegmentStore(root, 30))
            {
                // 两类日志交替写入：两个分类的分段时间完全重叠
                for (int i = 0; i < 20; i++)
                    store.Append(Record(Base.AddSeconds(i), 2, $"消息 {i}", "PLC1", ("igw_category", i % 2 == 0 ? "collection" : "interface")));
                await store.FlushAsync();
                AssertNewestFirst(store);   // 正在写的分段
                store.SealNow();
                await store.WaitForCompressionAsync();
            }
            await using var reopened = new LogSegmentStore(root, 30);
            AssertNewestFirst(reopened);    // 压缩、重启之后
            var exported = new List<string>();
            await foreach (LogCenterEntry entry in LogQueryEngine.StreamAscending(reopened, Request()))
                exported.Add(entry.Message);
            Assert.Equal(Enumerable.Range(0, 20).Select(i => $"消息 {i}"), exported);
        }
        finally { Directory.Delete(root, recursive: true); }

        static void AssertNewestFirst(LogSegmentStore store)
        {
            LogCenterResponse first = LogQueryEngine.Query(store, Request(pageSize: 3));
            Assert.Equal(20, first.Total);
            Assert.Equal(["消息 19", "消息 18", "消息 17"], first.Items.Select(item => item.Message));
            LogCenterResponse second = LogQueryEngine.Query(store, Request(page: 2, pageSize: 3));
            Assert.Equal(["消息 16", "消息 15", "消息 14"], second.Items.Select(item => item.Message));
        }
    }

    [Fact]
    public async Task Category_query_skips_other_category_folders_even_while_they_are_being_written()
    {
        string root = NewDirectory();
        try
        {
            await using var store = new LogSegmentStore(root, 30);
            store.Append(Record(Base, 2, "采集", "PLC1", ("igw_category", "collection")));
            store.Append(Record(Base, 2, "接口", "PLC1", ("igw_category", "interface")));
            await store.FlushAsync();
            LogFilter filter = LogFilter.Create(Request(category: "interface"));
            var segments = store.Snapshot(0, long.MaxValue);
            Assert.All(segments, segment => Assert.True(segment.Active));
            Assert.Equal(["interface"], segments.Where(segment => !filter.CanSkip(segment)).Select(segment => segment.Category));
            Assert.Equal(["接口"], LogQueryEngine.Query(store, Request(category: "interface")).Items.Select(item => item.Message));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Old_mixed_layout_is_still_read_and_restart_keeps_the_category_of_each_folder()
    {
        string root = NewDirectory();
        try
        {
            // 旧版本：所有分类混在 igw-logs\日期\ 下
            string legacyDay = Path.Combine(LogSegmentStore.RootFor(root), Base.ToString("yyyyMMdd"));
            Directory.CreateDirectory(legacyDay);
            var buffer = new System.Buffers.ArrayBufferWriter<byte>();
            LogRecordCodec.Write(buffer, Record(Base, 2, "旧版采集", "PLC1", ("igw_category", "collection")));
            LogRecordCodec.Write(buffer, Record(Base.AddSeconds(1), 2, "旧版状态", "PLC1", ("igw_category", "state")));
            File.WriteAllBytes(Path.Combine(legacyDay, "120000-abcdef.jsonl"), buffer.WrittenSpan.ToArray());

            await using (var store = new LogSegmentStore(root, 30))
            {
                store.Append(Record(Base.AddSeconds(2), 2, "新版状态", "PLC1", ("igw_category", "state")));
                await store.FlushAsync();
                store.SealNow();
                await store.WaitForCompressionAsync();
            }
            await using var reopened = new LogSegmentStore(root, 30);
            Assert.Equal(["新版状态", "旧版状态"], LogQueryEngine.Query(reopened, Request(category: "state")).Items.Select(item => item.Message));
            Assert.Equal(3, LogQueryEngine.Query(reopened, Request()).Total);
            Assert.Equal([null, "state"], reopened.Snapshot(0, long.MaxValue).Select(segment => segment.Category).OrderBy(c => c));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Retention_cleanup_removes_empty_day_folders_inside_category_folders()
    {
        string root = NewDirectory();
        try
        {
            var time = new ManualTime(Base);
            await using var store = new LogSegmentStore(root, 1, time);
            store.Append(Record(Base, 2, "过期", "PLC1", ("igw_category", "collection")));
            await store.FlushAsync();
            store.SealNow();
            await store.WaitForCompressionAsync();
            string day = Path.Combine(LogSegmentStore.RootFor(root), "collection", Base.ToLocalTime().ToString("yyyyMMdd"));
            Assert.True(Directory.Exists(day));

            time.Now = Base.AddDays(3);
            store.CleanupExpired();
            Assert.False(Directory.Exists(day));
            Assert.Empty(store.Snapshot(0, long.MaxValue));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Day_folders_and_file_names_use_the_gateway_local_time()
    {
        string root = NewDirectory();
        try
        {
            // 北京时间 10 月 8 日早上 7 点 = UTC 10 月 7 日 23 点：文件夹要按现场看到的本地日期建
            var beijing = TimeZoneInfo.CreateCustomTimeZone("UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
            var at = new DateTimeOffset(2026, 10, 7, 23, 1, 0, TimeSpan.Zero);
            await using var store = new LogSegmentStore(root, 30, new ManualTime(at, beijing));
            store.Append(Record(at, 2, "早班开机", "PLC1", ("igw_category", "collection")));
            await store.FlushAsync();

            string day = Path.Combine(LogSegmentStore.RootFor(root), "collection", "20261008");
            string file = Path.GetFileName(Assert.Single(Directory.GetFiles(day, "*.jsonl")));
            Assert.StartsWith("070000-", file);   // 分段起点 UTC 23:00 = 本地 07:00
            Assert.False(Directory.Exists(Path.Combine(LogSegmentStore.RootFor(root), "collection", "20261007")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class ManualTime(DateTimeOffset now, TimeZoneInfo? zone = null) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => zone ?? TimeZoneInfo.Local;
    }

    private static LogRecord Record(DateTimeOffset at, int level, string message, string device, params (string Key, object? Value)[] extra)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal) { ["igw_device"] = device };
        string category = "system";
        foreach (var (key, value) in extra)
        {
            if (key == "igw_category") category = (string)value!;
            else fields[key] = value;
        }
        return new LogRecord(LogRecordCodec.ToMicros(at), level, category, message, fields);
    }

    private static LogCenterRequest Request(
        int page = 1, int pageSize = 50, string? level = null, string? device = null, string? task = null,
        string? search = null, string? traceId = null, string category = "all") => new()
    {
        From = Base.AddMinutes(-1),
        To = Base.AddHours(1),
        Page = page,
        PageSize = pageSize,
        Level = level,
        Device = device,
        Task = task,
        Search = search,
        TraceId = traceId,
        Category = category
    };

    private static string NewDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "igw-log-store", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
