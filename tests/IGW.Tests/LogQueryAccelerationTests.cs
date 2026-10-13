using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using IGW.Observability;
using Xunit;

namespace IGW.Tests;

/// <summary>
/// 日志查询加速：索引分钟计数(不解压已封存分段)、已封存分段计数缓存、取消和时间预算。
/// 每个查询都和直接按记录算出的期望值(总数、趋势图、整页明细)逐项比较，确认加速前后结果一致。
/// </summary>
public sealed class LogQueryAccelerationTests
{
    private const long SpanMicros = 600_000_000;   // 分段 10 分钟
    private static readonly string[] Categories = ["collection", "interface", "system", "custom"];

    [Fact]
    public async Task Unfiltered_level_and_category_counts_come_from_the_index_without_reading_sealed_segments()
    {
        using var data = await Dataset.CreateAsync();
        var reads = data.TrackReads();

        foreach (Query query in new[]
                 {
                     new Query(),
                     new Query(Level: "error"),
                     new Query(Level: "warning"),
                     new Query(Category: "interface"),
                     new Query(Category: "collection", Level: "error"),
                     new Query(FromOffset: TimeSpan.FromHours(-2)),          // 3 小时范围：15 分钟一个桶，分钟计数合并成大桶
                 })
        {
            reads.Clear();
            LogCenterResponse response = data.AssertMatchesExpected(query with { PageSize = 1 });
            // 第一页落在正在写入的那组分段里：计数全靠索引，已封存分段一个都没读
            Assert.All(reads, segment => Assert.True(segment.Active, $"{query} 读了已封存分段 {segment.Path}"));
            Assert.False(response.Partial);
        }

        // system 文件夹里还混着不认识的分类(custom)：按 system 查不能直接用索引计数，要扫描
        reads.Clear();
        data.AssertMatchesExpected(new Query(Category: "system", PageSize: 1));
        Assert.Contains(reads, segment => !segment.Active && segment.Category == "system");
    }

    [Fact]
    public async Task Every_page_matches_expected_order_across_overlapping_category_segments()
    {
        using var data = await Dataset.CreateAsync();
        foreach (Query query in new[] { new Query(), new Query(Device: "PLC1"), new Query(Level: "warning") })
        {
            Expected expected = data.Expected(query);
            var all = new List<string>();
            for (int page = 1; all.Count < expected.Messages.Count; page++)
            {
                LogCenterResponse response = LogQueryEngine.Query(data.Store, data.Request(query with { Page = page, PageSize = 37 }));
                Assert.Equal(expected.Total, response.Total);
                Assert.NotEmpty(response.Items);
                all.AddRange(response.Items.Select(item => item.Message));
            }
            Assert.Equal(expected.Messages, all);
        }
    }

    [Fact]
    public async Task Range_cutting_through_segments_scans_the_cut_segments_and_still_matches()
    {
        using var data = await Dataset.CreateAsync();
        var reads = data.TrackReads();
        var cut = new Query(FromOffset: TimeSpan.FromMinutes(5.5), ToOffset: TimeSpan.FromMinutes(33) + TimeSpan.FromSeconds(10));
        foreach (Query query in new[] { cut, cut with { Level = "error" }, cut with { Category = "interface" } })
        {
            reads.Clear();
            data.AssertMatchesExpected(query with { PageSize = 1 });   // 第一页在被切开的最新一组里，取页不读别的分段
            (long from, long to) = data.Range(query);
            // 整段在范围内的已封存分段不读；被范围切开的要读
            Assert.All(reads.Where(segment => !segment.Active), segment =>
                Assert.True(segment.Index.FromMicros < from || segment.Index.ToMicros >= to, segment.Path));
            Assert.Contains(reads, segment => !segment.Active && segment.Index.FromMicros < from);
            data.AssertMatchesExpected(query);
            data.AssertMatchesExpected(query with { Page = 3, PageSize = 20 });
        }
    }

    [Fact]
    public async Task Filtered_counts_of_sealed_segments_are_cached_but_new_active_records_are_always_seen()
    {
        using var data = await Dataset.CreateAsync();
        var reads = data.TrackReads();
        var query = new Query(Device: "PLC1", PageSize: 5);

        LogCenterResponse first = data.AssertMatchesExpected(query);
        Assert.Contains(reads, segment => !segment.Active);                 // 第一次要扫描已封存分段
        long misses = data.Store.CountCache.Misses;

        reads.Clear();
        LogCenterResponse second = data.AssertMatchesExpected(query);
        Assert.All(reads, segment => Assert.True(segment.Active, segment.Path));   // 再查只重扫正在写入的分段
        Assert.Equal(misses, data.Store.CountCache.Misses);
        Assert.True(data.Store.CountCache.Hits > 0);
        Assert.Equal(first.Total, second.Total);
        Assert.Equal(first.Histogram, second.Histogram);

        // 时间窗口往后滑(自动刷新)：整段在范围内的已封存分段仍然命中缓存
        reads.Clear();
        data.AssertMatchesExpected(query with { FromOffset = TimeSpan.FromSeconds(-30), ToOffset = TimeSpan.FromMinutes(61) });
        Assert.All(reads, segment => Assert.True(segment.Active, segment.Path));

        // 两次查询之间正在写入的分段又来了新日志：缓存不能把它们挡住
        data.Append(TimeSpan.FromMinutes(46), 4, "新来的错误", "collection", "PLC1");
        data.Append(TimeSpan.FromMinutes(46) + TimeSpan.FromSeconds(1), 2, "新来的信息", "interface", "PLC1");
        data.Append(TimeSpan.FromMinutes(46) + TimeSpan.FromSeconds(2), 2, "别的设备", "interface", "PLC2");
        await data.Store.FlushAsync();
        LogCenterResponse third = data.AssertMatchesExpected(query);
        Assert.Equal(first.Total + 2, third.Total);
        Assert.Equal(["新来的信息", "新来的错误"], third.Items.Take(2).Select(item => item.Message));
        data.AssertMatchesExpected(new Query());   // 不带条件的也看得到
    }

    [Fact]
    public async Task Retention_cleanup_evicts_cached_counts_of_deleted_segments()
    {
        string root = NewDirectory();
        try
        {
            var time = new ManualTime(DateTimeOffset.UtcNow);
            await using var store = new LogSegmentStore(root, 1, time);
            DateTimeOffset old = time.Now.AddDays(-3);
            store.Append(new LogRecord(LogRecordCodec.ToMicros(old), 2, "system", "三天前", Fields("PLC1")));
            await store.FlushAsync();
            store.SealNow();
            await store.WaitForCompressionAsync();
            var request = new LogCenterRequest { From = old.AddMinutes(-1), To = old.AddMinutes(1), Device = "PLC1" };
            Assert.Equal(1, LogQueryEngine.Query(store, request).Total);
            Assert.Equal(1, store.CountCache.Count);

            store.CleanupExpired();
            Assert.Equal(0, store.CountCache.Count);
            Assert.Equal(0, LogQueryEngine.Query(store, request).Total);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Minute_counts_survive_restart_through_the_index_file()
    {
        string root = NewDirectory();
        try
        {
            DateTimeOffset start;
            using (var data = await Dataset.CreateAsync(root, keepDirectory: true, sealEverything: true)) start = data.Start;
            using var reopened = await Dataset.OpenAsync(root, start);
            IReadOnlyList<LogSegmentView> segments = reopened.Store.Snapshot(0, long.MaxValue);
            Assert.All(segments, segment => Assert.True(segment.Index.MinutesConsistent()));
            var reads = reopened.TrackReads();
            reopened.AssertMatchesExpected(new Query(PageSize: 1));
            reopened.AssertMatchesExpected(new Query(Level: "error", PageSize: 1));
            // 计数全靠 .idx 里的分钟计数，只读了取第一页的最新一组(三个分类文件夹各一段)
            Assert.True(reads.Distinct().Count() <= 3, $"读了 {reads.Distinct().Count()} 个分段，共 {segments.Count} 个");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Old_index_files_without_minute_counts_still_load_and_fall_back_to_scanning()
    {
        string root = NewDirectory();
        try
        {
            DateTimeOffset start;
            using (var data = await Dataset.CreateAsync(root, keepDirectory: true, sealEverything: true)) start = data.Start;
            // 模拟旧版本写的 .idx：没有 minutes
            string[] indexes = Directory.GetFiles(LogSegmentStore.RootFor(root), "*.idx", SearchOption.AllDirectories);
            Assert.NotEmpty(indexes);
            foreach (string path in indexes)
            {
                JsonObject json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
                Assert.True(json.Remove("minutes"));
                File.WriteAllText(path, json.ToJsonString());
            }

            using var reopened = await Dataset.OpenAsync(root, start);
            Assert.All(reopened.Store.Snapshot(0, long.MaxValue), segment => Assert.Null(segment.Index.Minutes));
            var reads = reopened.TrackReads();
            reopened.AssertMatchesExpected(new Query(PageSize: 5));
            Assert.Equal(reopened.Store.Snapshot(0, long.MaxValue).Count, reads.Distinct().Count());   // 没有分钟计数：每段都扫描
            reopened.AssertMatchesExpected(new Query(Level: "error", Category: "collection"));

            // 扫描结果进了缓存，再查不用重读
            reads.Clear();
            reopened.AssertMatchesExpected(new Query(PageSize: 5));
            Assert.True(reads.Count <= 3, $"读了 {reads.Count} 次");   // 只剩取第一页的那组分段(三个分类文件夹各一段)
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Cancelling_the_request_stops_counting_and_page_collection_promptly()
    {
        using var data = await Dataset.CreateAsync();
        int segments = data.Store.Snapshot(0, long.MaxValue).Count;

        // 已取消的请求不读任何分段
        var reads = data.TrackReads();
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() =>
                LogQueryEngine.Query(data.Store, data.Request(new Query(Search: "消息")), cancellationToken: cancelled.Token));
            Assert.Empty(reads);
        }

        // 计数时浏览器放弃了请求：在读第一个分段时取消，其余分段不再读
        using (var abandon = new CancellationTokenSource())
        {
            int count = 0;
            data.Store.OnRead = _ => { Interlocked.Increment(ref count); abandon.Cancel(); };
            Assert.ThrowsAny<OperationCanceledException>(() =>
                LogQueryEngine.Query(data.Store, data.Request(new Query(Search: "消息")), cancellationToken: abandon.Token));
            Assert.InRange(count, 1, 4);   // 最多每个并行线程各开始读了一个
            Assert.True(count < segments);
        }

        // 取页时取消(计数全靠索引，只有回读已封存分段时才取消)：同样抛出取消，不当成部分结果返回
        using (var abandon = new CancellationTokenSource())
        {
            data.Store.OnRead = segment => { if (!segment.Active) abandon.Cancel(); };
            Assert.ThrowsAny<OperationCanceledException>(() =>
                LogQueryEngine.Query(data.Store, data.Request(new Query(Page: 3, PageSize: 100)), cancellationToken: abandon.Token));
        }
        data.Store.OnRead = null;
    }

    [Fact]
    public async Task Cancelled_backend_query_is_reported_as_cancelled_not_as_a_failure()
    {
        string root = NewDirectory();
        try
        {
            ObservabilitySettings settings = ObservabilitySettings.Initialize(root, Path.Combine(root, "store"), 5088);
            await using var store = new LogSegmentStore(settings.StorageDirectory, settings.RetentionDays);
            var backend = new LogCenterBackend(root, settings, store, new LogPayloadStore(Path.Combine(root, "payloads")));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backend.QueryAsync(new LogCenterRequest(), cancelled.Token));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Budget_covers_page_collection_and_marks_the_result_partial()
    {
        using var data = await Dataset.CreateAsync();

        // 预算为零：什么都来不及算，返回空的部分结果，不抛异常
        LogCenterResponse none = LogQueryEngine.Query(data.Store, data.Request(new Query()), TimeSpan.Zero);
        Assert.True(none.Partial);
        Assert.Equal(0, none.Total);

        // 慢盘：回读已封存分段很慢。计数靠索引很快算完，取页超出预算就停下，返回 Partial，不会等到读完
        data.Store.OnRead = segment => { if (!segment.Active) Thread.Sleep(3000); };
        Expected expected = data.Expected(new Query(Page: 3, PageSize: 100));
        LogCenterResponse slow = LogQueryEngine.Query(data.Store, data.Request(new Query(Page: 3, PageSize: 100)), TimeSpan.FromSeconds(2));
        data.Store.OnRead = null;
        Assert.True(slow.Partial);
        Assert.Equal(expected.Total, slow.Total);                         // 计数是完整的
        Assert.True(slow.Items.Count < 100);
        Assert.Equal(expected.Messages.Skip(200).Take(slow.Items.Count), slow.Items.Select(item => item.Message));
    }

    [Fact]
    public async Task Path_or_related_task_matches_survive_byte_prefilter_indexes_count_cache_and_export()
    {
        string root = NewDirectory();
        try
        {
            await using var store = new LogSegmentStore(root, 30);
            DateTimeOffset at = DateTimeOffset.UtcNow.AddMinutes(-1);
            void Append(string message, string field, string value) => store.Append(new LogRecord(LogRecordCodec.ToMicros(at),
                2, "interface", message, new Dictionary<string, object?> { [field] = value }));
            Append("工程路径", "igw_target", "PLC6.D1.Group.Result");
            Append("关联任务", "igw_task_id", "task-1");
            Append("共享任务", "igw_task_ids", "other, task-1");
            Append("相似 ID 不是关联任务", "igw_task_id", "task-10");
            Append("相似共享 ID 不是关联任务", "igw_task_ids", "other, task-10");
            Append("同名不是关联任务", "igw_task_name", "task-1");
            Append("另一个任务", "igw_task_id", "task-2");
            await store.FlushAsync();

            var first = new LogCenterRequest { From = at.AddMinutes(-1), To = at.AddMinutes(1), Path = "PLC6.D1", PathTaskIds = ["task-1"] };
            Assert.Equal(3, LogQueryEngine.Query(store, first).Total);   // 活跃分段：任务记录不含 path，不能被字节预筛排除
            store.SealNow();
            await store.WaitForCompressionAsync();
            foreach (int _ in Enumerable.Range(0, 2))
            {
                LogCenterResponse result = LogQueryEngine.Query(store, first);
                Assert.Equal(3, result.Total);
                Assert.Equal(["共享任务", "关联任务", "工程路径"], result.Items.Select(item => item.Message));
                Assert.Equal(3, result.Histogram.Sum(bucket => bucket.Total));
            }
            Assert.True(store.CountCache.Hits > 0);
            // 相同 path 的当前工程绑定发生变化：不能命中旧绑定的计数缓存。
            var changed = new LogCenterRequest { From = first.From, To = first.To, Path = first.Path, PathTaskIds = ["task-2"] };
            Assert.Equal(2, LogQueryEngine.Query(store, changed).Total);
            var exported = new List<string>();
            await foreach (LogCenterEntry item in LogQueryEngine.StreamAscending(store, first)) exported.Add(item.Message);
            Assert.Equal(["工程路径", "关联任务", "共享任务"], exported);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Related_task_ids_cannot_collide_with_other_filter_sections_in_the_count_cache()
    {
        string root = NewDirectory();
        try
        {
            await using var store = new LogSegmentStore(root, 30);
            DateTimeOffset at = DateTimeOffset.UtcNow.AddMinutes(-1);
            store.Append(new LogRecord(LogRecordCodec.ToMicros(at), 2, "interface", "task-2",
                new Dictionary<string, object?> { ["igw_device"] = "PLC6.D1", ["igw_task_id"] = "task-1" }));
            await store.FlushAsync();
            store.SealNow();
            await store.WaitForCompressionAsync();
            var misses = new LogCenterRequest { From = at.AddMinutes(-1), To = at.AddMinutes(1),
                Search = "=igw_device", Path = "PLC6.D1", PathTaskIds = ["task-1", "task-2"] };
            var matches = new LogCenterRequest { From = misses.From, To = misses.To,
                Device = "PLC6.D1", Search = "task-1", Path = "task-2" };

            Assert.Equal(0, LogQueryEngine.Query(store, misses).Total);
            LogCenterResponse result = LogQueryEngine.Query(store, matches);
            Assert.Equal(1, result.Total);
            Assert.Equal("task-2", Assert.Single(result.Items).Message);
            Assert.Equal(1, result.Histogram.Sum(bucket => bucket.Total));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ---------- 测试数据 ----------

    private sealed record Query(
        string? Level = null, string? Category = null, string? Device = null, string? Search = null,
        TimeSpan? FromOffset = null, TimeSpan? ToOffset = null, int Page = 1, int PageSize = 50);

    private sealed record Expected(long Total, IReadOnlyList<LogHistogramBucket> Histogram, IReadOnlyList<string> Messages);

    /// <summary>
    /// 从一个整 10 分钟开始，每 7 秒一条、共 45 分钟的日志，分布在 collection / interface / system 三个文件夹(custom 也写进 system)。
    /// 前 4 个 10 分钟的分段已封存压缩，最后 5 分钟的在写。
    /// </summary>
    private sealed class Dataset : IDisposable
    {
        private readonly string _root;
        private readonly bool _keepDirectory;
        private readonly List<LogRecord> _records;

        private Dataset(string root, bool keepDirectory, LogSegmentStore store, List<LogRecord> records, DateTimeOffset start)
        {
            _root = root;
            _keepDirectory = keepDirectory;
            Store = store;
            _records = records;
            Start = start;
        }

        public LogSegmentStore Store { get; }
        public DateTimeOffset Start { get; }

        private static DateTimeOffset AlignedStart()
        {
            long micros = LogRecordCodec.ToMicros(DateTimeOffset.UtcNow.AddHours(-3)) / SpanMicros * SpanMicros;
            return DateTimeOffset.UnixEpoch.AddTicks(micros * 10);
        }

        private static List<LogRecord> Generate(DateTimeOffset start)
        {
            var records = new List<LogRecord>();
            for (int i = 0; i * 7 < 45 * 60; i++)
            {
                int level = i % 11 == 0 ? 4 : i % 7 == 0 ? 3 : i % 13 == 0 ? 5 : 2;
                string category = Categories[i % 9 == 0 ? 3 : i % 5 == 0 ? 2 : i % 2];
                records.Add(new LogRecord(LogRecordCodec.ToMicros(start.AddSeconds(i * 7)), level, category, $"消息 {i}", Fields($"PLC{i % 4}")));
            }
            return records;
        }

        public static async Task<Dataset> CreateAsync(string? root = null, bool keepDirectory = false, bool sealEverything = false)
        {
            root ??= NewDirectory();
            DateTimeOffset start = AlignedStart();
            List<LogRecord> records = Generate(start);
            // 时钟停在最后一条附近：后台维护不会把正在写的分段提前封存
            var store = new LogSegmentStore(root, 30, new ManualTime(start.AddMinutes(45)));
            foreach (LogRecord record in records) store.Append(record);
            await store.FlushAsync();
            if (sealEverything) store.SealNow();
            await store.WaitForCompressionAsync();
            var segments = store.Snapshot(0, long.MaxValue);
            Assert.Contains(segments, segment => segment.Compressed);
            if (!sealEverything) Assert.Contains(segments, segment => segment.Active);
            return new Dataset(root, keepDirectory, store, records, start);
        }

        public static async Task<Dataset> OpenAsync(string root, DateTimeOffset start)
        {
            var store = new LogSegmentStore(root, 30, new ManualTime(start.AddMinutes(45)));
            await store.WaitForCompressionAsync();
            return new Dataset(root, keepDirectory: true, store, Generate(start), start);
        }

        public ConcurrentQueue<LogSegmentView> TrackReads()
        {
            var reads = new ConcurrentQueue<LogSegmentView>();
            Store.OnRead = reads.Enqueue;
            return reads;
        }

        public void Append(TimeSpan offset, int level, string message, string category, string device)
        {
            var record = new LogRecord(LogRecordCodec.ToMicros(Start + offset), level, category, message, Fields(device));
            _records.Add(record);
            Store.Append(record);
        }

        public (long From, long To) Range(Query query) =>
            (LogRecordCodec.ToMicros(Start + (query.FromOffset ?? TimeSpan.FromMinutes(-1))),
             LogRecordCodec.ToMicros(Start + (query.ToOffset ?? TimeSpan.FromMinutes(60))));

        public LogCenterRequest Request(Query query) => new()
        {
            From = Start + (query.FromOffset ?? TimeSpan.FromMinutes(-1)),
            To = Start + (query.ToOffset ?? TimeSpan.FromMinutes(60)),
            Level = query.Level,
            Category = query.Category ?? "all",
            Device = query.Device,
            Search = query.Search,
            Page = query.Page,
            PageSize = query.PageSize
        };

        /// <summary>不经过存储，直接按记录算出期望的总数、趋势图和从新到旧的全部消息。</summary>
        public Expected Expected(Query query)
        {
            (long from, long to) = Range(query);
            int? level = query.Level switch { null => null, "error" => 4, "warning" => 3, _ => throw new ArgumentException(query.Level) };
            TimeSpan range = TimeSpan.FromTicks((to - from) * 10);
            long bucketTicks = range <= TimeSpan.FromHours(2) ? TimeSpan.FromMinutes(1).Ticks
                : range <= TimeSpan.FromDays(2) ? TimeSpan.FromMinutes(15).Ticks : TimeSpan.FromHours(1).Ticks;
            LogRecord[] matching = _records
                .Where(record => record.TimestampMicros >= from && record.TimestampMicros < to)
                .Where(record => level is null || record.Level == level)
                .Where(record => query.Category is null || record.Category == query.Category)
                .Where(record => query.Device is null || (string?)record.Fields["igw_device"] == query.Device)
                .Where(record => query.Search is null || record.Message.Contains(query.Search, StringComparison.Ordinal))
                .OrderByDescending(record => record.TimestampMicros)
                .ToArray();
            LogHistogramBucket[] histogram = matching
                .GroupBy(record => (DateTimeOffset.UnixEpoch.Ticks + record.TimestampMicros * 10) / bucketTicks * bucketTicks)
                .OrderBy(group => group.Key)
                .Select(group => new LogHistogramBucket(new DateTimeOffset(group.Key, TimeSpan.Zero), group.Count(),
                    group.Count(record => record.Level == 3), group.Count(record => record.Level >= 4)))
                .ToArray();
            return new Expected(matching.Length, histogram, matching.Select(record => record.Message).ToArray());
        }

        public LogCenterResponse AssertMatchesExpected(Query query)
        {
            Expected expected = Expected(query);
            LogCenterResponse response = LogQueryEngine.Query(Store, Request(query));
            Assert.False(response.Partial, query.ToString());
            Assert.Equal(expected.Total, response.Total);
            Assert.Equal(expected.Histogram, response.Histogram);
            Assert.Equal(expected.Messages.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize),
                response.Items.Select(item => item.Message));
            return response;
        }

        public void Dispose()
        {
            Store.OnRead = null;
            Store.DisposeAsync().AsTask().GetAwaiter().GetResult();
            if (!_keepDirectory) try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }
    }

    private static Dictionary<string, object?> Fields(string device) =>
        new(StringComparer.Ordinal) { ["igw_device"] = device };

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static string NewDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "igw-log-query", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
