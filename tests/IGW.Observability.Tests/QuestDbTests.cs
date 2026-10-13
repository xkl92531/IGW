using System.Net;
using System.Text;
using System.Text.Json;
using IGW.Observability;
using Xunit;

namespace IGW.Observability.Tests;

public sealed class QuestDbTests : IDisposable
{
    private readonly List<string> _directories = [];
    private string NewDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "igw-questdb-tests", Guid.NewGuid().ToString("N"));
        _directories.Add(path); return path;
    }
    public void Dispose()
    {
        foreach (string path in _directories) if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
    [Fact]
    public void Codec_keeps_multiline_unicode_and_attribute_types_without_extra_ilp_lines()
    {
        var record = new LogRecord(1791500000000000, 4, "system", "PLC6 失败\r\n第二行 \"x\" \\n",
            new Dictionary<string, object?> { ["igw_device"] = "产线1.PLC6", ["n"] = 42, ["ok"] = true });
        Guid id = Guid.NewGuid();
        string line = QuestDbLogCodec.Encode(record, id);
        Assert.Equal(1, line.Count(c => c == '\n'));
        Assert.Contains(id.ToString(), line);
        LogRecord restored = QuestDbLogCodec.Decode(QuestDbLogCodec.Raw(record));
        Assert.Equal(record.Message, restored.Message);
        Assert.Equal(42L, restored.Fields["n"]);
        Assert.Equal(true, restored.Fields["ok"]);
    }

    [Fact]
    public void Sql_filters_escape_literals_and_keep_path_segment_boundaries()
    {
        string where = QuestDbLogQuery.Where(new LogCenterRequest { Device = "PLC6'; drop table igw_logs;--", Path = "PLC6" });
        Assert.Contains("PLC6''; drop table igw_logs;--", where);
        Assert.Contains("igw_device", where);
        Assert.Contains("\\p{L}", where);
        Assert.Contains("strpos", QuestDbLogQuery.Where(new LogCenterRequest { Search = "%_读取" }));
        Assert.DoesNotContain(" like ", QuestDbLogQuery.Where(new LogCenterRequest { Search = "%_读取" }));
        Assert.Throws<ArgumentException>(() => QuestDbLogQuery.Where(new LogCenterRequest { Category = "injected" }));
    }

    [Fact]
    public async Task Query_and_export_send_both_independent_association_filters_to_the_database()
    {
        var statements = new List<string>();
        using var client = new QuestDbClient(new Uri("http://127.0.0.1:28922"), "test", "secret", new Handler(request =>
        {
            statements.Add(Uri.UnescapeDataString(request.RequestUri!.Query[7..]));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"dataset\":[]}") };
        }));
        var query = new QuestDbLogQuery(client);
        var request = new LogCenterRequest
        {
            Path = "PLC6.D1.Group", PathTaskIds = ["path-task-1"],
            TaskOrInterface = "api/report", TaskOrInterfaceTaskIds = ["term-task-2"]
        };
        await query.QueryAsync(request, default);
        await foreach (var _ in query.StreamAsync(request, default)) { }

        // These are the SQL requests really sent for the page, counts/trend and export (no network is used).
        Assert.Equal(3, statements.Count);
        foreach (string sql in statements)
        {
            Assert.Contains("from IGW_logs where", sql);
            Assert.Contains("api/report", sql);
            Assert.Contains("path-task-1", sql);
            Assert.Contains("term-task-2", sql);
            Assert.Contains("$.f.igw_scope", sql);
            Assert.DoesNotContain("IGW_payloads", sql);
        }
        string Predicate(string sql) => sql[(sql.IndexOf(" where ", StringComparison.Ordinal) + 7)..]
            .Split([" order by ", " sample by "], StringSplitOptions.None)[0];
        Assert.Single(statements.Select(Predicate).Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public void Association_changes_produce_distinct_sql_and_do_not_reuse_a_previous_filter()
    {
        DateTimeOffset to = DateTimeOffset.UtcNow;
        string Where(string pathId, string termId) => QuestDbLogQuery.Where(new LogCenterRequest
        {
            From = to.AddHours(-1), To = to, Path = "PLC6.D1.Group", PathTaskIds = [pathId],
            TaskOrInterface = "StationEntrance", TaskOrInterfaceTaskIds = [termId]
        });
        Assert.NotEqual(Where("task-a", "task-b"), Where("task-c", "task-b"));
        Assert.NotEqual(Where("task-a", "task-b"), Where("task-a", "task-c"));
    }

    [Fact]
    public async Task Client_authenticates_and_reports_http_and_sql_errors()
    {
        var handler = new Handler(request =>
        {
            Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
            Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("test:secret")), request.Headers.Authorization?.Parameter);
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"bad SQL\"}") };
        });
        using var client = new QuestDbClient(new Uri("http://127.0.0.1:28922"), "test", "secret", handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.QueryAsync("select 1", default));
        Assert.Throws<ArgumentException>(() => new QuestDbClient(new Uri("http://0.0.0.0:9000"), "x", "y"));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    [Fact]
    public async Task Unavailable_database_uses_bounded_durable_batches_without_blocking_append()
    {
        string root = NewDirectory();
        using var client = new QuestDbClient(new Uri("http://127.0.0.1:28922"), "test", "secret",
            new Handler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        await using (var store = new QuestDbLogStore(client, root, queueCapacity: 4, diskLimitBytes: 1024 * 1024))
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 10_000; i++) store.Append(new LogRecord(1791500000000000, 2, "system", "db down", new Dictionary<string, object?>()));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1));
            Assert.True(store.Dropped > 0);
            await store.FlushAsync(default);
            Assert.NotEmpty(Directory.GetFiles(root, "*.ilp"));
            Assert.True(store.Pending > 0);
        }
        Assert.NotEmpty(Directory.GetFiles(root, "*.ilp"));
    }

    [NativeQuestDbFact]
    public async Task Real_database_deduplicates_and_preserves_filters_payload_and_histogram()
    {
        using var client = new QuestDbClient(new Uri(Environment.GetEnvironmentVariable("IGW_QUESTDB_TEST_URL")!), "igw", "probe-only-local");
        await client.InitializeAsync(30, default);
        string scope = Guid.NewGuid().ToString("N");
        long ts = LogRecordCodec.ToMicros(DateTimeOffset.UtcNow.AddSeconds(-5));
        var records = new[] { "PLC6 读取失败\n第二行", "PLC66 正常", "X.PLC6 正常", "PLC6.D1 正常" }
            .Select((message, index) => new LogRecord(ts + index, index == 0 ? 4 : 2, "system", message,
                new Dictionary<string, object?> { ["igw_task_id"] = scope, ["n"] = long.MaxValue })).ToArray();
        byte[] batch = Encoding.UTF8.GetBytes(string.Concat(records.Select((record, index) => QuestDbLogCodec.Encode(record, QuestDbLogCodec.HistoricalId(scope, index)))));
        await client.WriteAsync(batch, default);
        await client.WriteAsync(batch, default);
        var query = new QuestDbLogQuery(client);
        var request = new LogCenterRequest { TaskId = scope, From = DateTimeOffset.UtcNow.AddMinutes(-1), To = DateTimeOffset.UtcNow.AddMinutes(1) };
        LogCenterResponse result = await WaitQuery(query, request, 4);
        Assert.False(result.Partial);
        Assert.Equal(4, result.Total);
        Assert.Equal(4, result.Histogram.Sum(b => b.Total));
        Assert.Equal(long.MaxValue, result.Items[0].Attributes["n"].GetInt64());
        result = await query.QueryAsync(new LogCenterRequest { TaskId = scope, Path = "PLC6", From = request.From, To = request.To }, default);
        Assert.False(result.Partial);
        Assert.Equal(2, result.Total);
        result = await query.QueryAsync(new LogCenterRequest { TaskId = scope, Search = "失败\n第二行", From = request.From, To = request.To }, default);
        Assert.False(result.Partial);
        Assert.Single(result.Items);
        var exported = new List<LogCenterEntry>();
        await foreach (var entry in query.StreamAsync(request, default)) exported.Add(entry);
        Assert.Equal(4, exported.Count);
    }

    [Fact]
    public async Task A_poison_log_does_not_discard_good_logs_in_the_same_batch()
    {
        string root = NewDirectory();
        var delivered = new List<string>();
        using var client = new QuestDbClient(new Uri("http://127.0.0.1:28922"), "test", "secret", new AsyncHandler(async request =>
        {
            string body = await request.Content!.ReadAsStringAsync();
            if (body.Contains("poison")) return new HttpResponseMessage(HttpStatusCode.BadRequest);
            lock (delivered) delivered.Add(body);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }));
        await using var store = new QuestDbLogStore(client, root);
        store.Append(new LogRecord(1791500000000000, 2, "system", "good", new Dictionary<string, object?>()));
        store.Append(new LogRecord(1791500000000001, 2, "system", "poison", new Dictionary<string, object?>()));
        await store.FlushAsync(default);
        for (int attempt = 0; attempt < 100 && store.Pending > 0; attempt++) await Task.Delay(100);
        Assert.Equal(0, store.Pending);
        Assert.Equal(1, store.Dropped);
        lock (delivered) Assert.Contains(delivered, body => body.Contains("good"));
        Assert.Single(Directory.GetFiles(root, "*.rejected"));
    }

    private sealed class AsyncHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }

    [Fact]
    public async Task A_failing_history_request_must_not_quarantine_a_new_good_batch()
    {
        string root = NewDirectory();
        var inFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new List<string>();
        using var client = new QuestDbClient(new Uri("http://127.0.0.1:28922"), "test", "secret", new AsyncHandler(async request =>
        {
            string body = await request.Content!.ReadAsStringAsync();
            if (body.Contains("history-bad")) { inFlight.TrySetResult(); await finish.Task; return new HttpResponseMessage(HttpStatusCode.BadRequest); }
            lock (delivered) delivered.Add(body);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }));
        await using var store = new QuestDbLogStore(client, root);
        await store.PersistHistoryAsync(QuestDbLogCodec.Encode(new LogRecord(1791500000000000, 2, "system", "history-bad", new Dictionary<string, object?>()), Guid.NewGuid()), default);
        await inFlight.Task.WaitAsync(TimeSpan.FromSeconds(5));
        store.Append(new LogRecord(1791500000000001, 2, "system", "new-good", new Dictionary<string, object?>()));
        await store.FlushAsync(default);
        finish.SetResult();
        for (int attempt = 0; attempt < 100 && store.Pending > 0; attempt++) await Task.Delay(100);
        lock (delivered) Assert.Contains(delivered, body => body.Contains("new-good"));
        Assert.Equal(1, store.HistoryRejected);
        Assert.Equal(1, store.Dropped);
    }

    [Fact]
    public async Task History_backpressure_is_not_a_write_failure_and_does_not_block_live_logs()
    {
        string root = NewDirectory();
        var sent = new List<string>();
        using var client = new QuestDbClient(new Uri("http://127.0.0.1:28922"), "test", "secret", new AsyncHandler(async request =>
        {
            string body = await request.Content!.ReadAsStringAsync(); lock (sent) sent.Add(body);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }));
        bool paused = true;
        await using var store = new QuestDbLogStore(client, root,
            prepareHistory: _ => paused ? Task.FromException(new QuestDbBackpressureException()) : Task.CompletedTask);
        await store.PersistHistoryAsync(QuestDbLogCodec.Encode(new LogRecord(1791500000000000, 2, "system", "old-history", new Dictionary<string, object?>()), Guid.NewGuid()), default);
        await Task.Delay(300);
        store.Append(new LogRecord(1791500000000001, 2, "system", "new-live", new Dictionary<string, object?>()));
        await store.FlushAsync(default);
        for (int attempt = 0; attempt < 40; attempt++)
        {
            lock (sent) { if (sent.Any(body => body.Contains("new-live"))) break; }
            await Task.Delay(50);
        }
        lock (sent) Assert.Contains(sent, body => body.Contains("new-live"));
        Assert.Equal(0, store.WriteFailures);
        Assert.Null(store.LastError);
        Assert.True(store.HistoryPending > 0);
        paused = false;
        for (int attempt = 0; attempt < 60 && store.Pending > 0; attempt++) await Task.Delay(50);
        Assert.Equal(0, store.Pending);
    }

    [Fact]
    public async Task Payload_save_reports_rejected_queue_instead_of_a_nonexistent_reference()
    {
        string root = NewDirectory();
        using var client = new QuestDbClient(new Uri("http://127.0.0.1:28922"), "test", "secret", new Handler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var store = new QuestDbLogStore(client, root);
        await store.DisposeAsync();
        var payloads = new QuestDbPayloadStore(client, store);
        Assert.Throws<IOException>(() => payloads.Save("text/plain", "not saved"));
    }

    [Fact]
    public async Task Retention_reverts_both_tables_when_second_alter_fails()
    {
        var queries = new List<string>();
        using var client = new QuestDbClient(new Uri("http://127.0.0.1:28922"), "test", "secret", new Handler(request =>
        {
            string sql = Uri.UnescapeDataString(request.RequestUri!.Query[7..]); queries.Add(sql);
            return new HttpResponseMessage(sql.Contains("IGW_payloads set ttl 7") ? HttpStatusCode.BadRequest : HttpStatusCode.OK)
                { Content = new StringContent("{\"ddl\":\"OK\"}") };
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SetRetentionAsync(7, default, previousDays: 30));
        Assert.Contains("alter table IGW_logs set ttl 30 DAYS", queries);
        Assert.Contains("alter table IGW_payloads set ttl 30 DAYS", queries);
    }

    [Fact]
    public async Task A_broken_spool_path_does_not_throw_on_creation_or_block_logging()
    {
        string root = NewDirectory();
        Directory.CreateDirectory(root);
        string spool = Path.Combine(root, "spool"); File.WriteAllText(spool, "blocking file");
        using var client = new QuestDbClient(new Uri("http://127.0.0.1:28922"), "test", "secret", new Handler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        await using var store = new QuestDbLogStore(client, spool);
        store.Append(new LogRecord(1791500000000000, 2, "system", "live PLC", new Dictionary<string, object?>()));
        await store.FlushAsync(default);
        Assert.True(store.WriteFailures > 0);
        Assert.True(store.Dropped > 0);
        Assert.NotNull(store.LastError);
    }

    [Fact]
    public async Task Complete_atomic_temp_batches_recover_and_incomplete_batches_count_against_cap()
    {
        string root = NewDirectory();
        Directory.CreateDirectory(root);
        string name = $"0000-{Guid.NewGuid():N}-1.ilp";
        File.WriteAllText(Path.Combine(root, name + ".123.tmp"), QuestDbLogCodec.Encode(new LogRecord(1791500000000000, 2, "system", "recover", new Dictionary<string, object?>()), Guid.NewGuid()));
        File.WriteAllText(Path.Combine(root, $"0001-{Guid.NewGuid():N}-1.ilp.123.tmp"), "partial no line end");
        using var client = new QuestDbClient(new Uri("http://127.0.0.1:28922"), "test", "secret", new Handler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        await using var store = new QuestDbLogStore(client, root);
        await Task.Delay(150);
        Assert.True(File.Exists(Path.Combine(root, name)));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        Assert.Single(Directory.GetFiles(root, "*.rejected"));
        Assert.Equal(1, store.Dropped);
        Assert.Equal(1, store.Pending);
    }

    [Fact]
    public async Task Cancelling_export_cancels_in_flight_database_payload_reads()
    {
        string root = NewDirectory();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int cancelled = 0;
        using var client = new QuestDbClient(new Uri("http://127.0.0.1:28922"), "test", "secret", new CancellationHandler(async token =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { Interlocked.Increment(ref cancelled); throw; }
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        await using var store = new QuestDbLogStore(client, Path.Combine(root, "spool"));
        var payloads = new LogPayloadStore(Path.Combine(root, "payloads")); payloads.UseDatabase(new QuestDbPayloadStore(client, store));
        using var cancellation = new CancellationTokenSource();
        var export = LogCenterExport.CreateAsync(new LogCenterRequest(), Entries(), payloads, new LogExportOptions(), cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => export.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.InRange(cancelled, 1, 3);
    }

    private static async IAsyncEnumerable<LogCenterEntry> Entries()
    {
        for (int i = 0; i < 32; i++) yield return new LogCenterEntry(DateTimeOffset.UtcNow, "INFO", "interface", "test", null, Guid.CreateVersion7().ToString("N"), new Dictionary<string, JsonElement>());
        await Task.CompletedTask;
    }
    private sealed class CancellationHandler(Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(cancellationToken);
    }

    [NativeQuestDbFact]
    public async Task Boolean_metadata_search_keeps_legacy_case_sensitive_text()
    {
        using var client = new QuestDbClient(new Uri(Environment.GetEnvironmentVariable("IGW_QUESTDB_TEST_URL")!), "igw", "probe-only-local");
        string scope = Guid.NewGuid().ToString("N");
        await client.WriteAsync(Encoding.UTF8.GetBytes(QuestDbLogCodec.Encode(new LogRecord(LogRecordCodec.ToMicros(DateTimeOffset.UtcNow), 2, "system", "flag", new Dictionary<string, object?> { ["igw_task_id"] = scope, ["igw_target"] = true }), Guid.NewGuid())), default);
        var query = new QuestDbLogQuery(client);
        var range = new LogCenterRequest { TaskId = scope, From = DateTimeOffset.UtcNow.AddMinutes(-1), To = DateTimeOffset.UtcNow.AddMinutes(1) };
        await WaitQuery(query, range, 1);
        Assert.Equal(1, (await query.QueryAsync(new LogCenterRequest { TaskId = scope, Search = "True", From = range.From, To = range.To }, default)).Total);
        Assert.Equal(0, (await query.QueryAsync(new LogCenterRequest { TaskId = scope, Search = "true", From = range.From, To = range.To }, default)).Total);
    }

    [NativeQuestDbFact]
    public async Task Payload_capacity_removes_old_hours_and_keeps_the_latest_partition()
    {
        using var client = new QuestDbClient(new Uri(Environment.GetEnvironmentVariable("IGW_QUESTDB_TEST_URL")!), "igw", "probe-only-local");
        await using var spool = new QuestDbLogStore(client, NewDirectory());
        var payloads = new QuestDbPayloadStore(client, spool);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid latest = Guid.CreateVersion7(now);
        Guid first = Guid.CreateVersion7(now.AddHours(-3)), second = Guid.CreateVersion7(now.AddHours(-2));
        string data = string.Concat(new[] { (Hour: -3, Id: first), (Hour: -2, Id: second), (Hour: 0, Id: latest) }
            .Select(item => QuestDbLogCodec.Payload(item.Id, LogRecordCodec.ToMicros(now.AddHours(item.Hour)), "text/plain", "capacity")));
        await client.WriteAsync(Encoding.UTF8.GetBytes(data), default);
        for (int attempt = 0; attempt < 100; attempt++)
        {
            using var parts = await client.QueryAsync("select count() from table_partitions('IGW_payloads')", default);
            if (parts.RootElement.GetProperty("dataset")[0][0].GetInt64() >= 3) break;
            await Task.Delay(50);
        }
        await payloads.CleanupCapacityAsync(1, default);
        for (int attempt = 0; attempt < 100; attempt++)
        {
            using var parts = await client.QueryAsync("select count() from table_partitions('IGW_payloads')", default);
            if (parts.RootElement.GetProperty("dataset")[0][0].GetInt64() == 1) break;
            await Task.Delay(50);
        }
        using var result = await client.QueryAsync("select id from IGW_payloads", default);
        string?[] ids = result.RootElement.GetProperty("dataset").EnumerateArray().Select(row => row[0].GetString()).ToArray();
        Assert.Contains(latest.ToString(), ids);
        Assert.DoesNotContain(first.ToString(), ids);
        Assert.DoesNotContain(second.ToString(), ids);
    }

    private static async Task<LogCenterResponse> WaitQuery(QuestDbLogQuery query, LogCenterRequest request, int expected)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            LogCenterResponse response = await query.QueryAsync(request, default);
            if (response.Total == expected && !response.Partial) return response;
            await Task.Delay(50);
        }
        throw new TimeoutException("WAL 日志没有及时变为可查询状态。");
    }
}

public sealed class NativeQuestDbFactAttribute : FactAttribute
{
    public NativeQuestDbFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("IGW_QUESTDB_TEST_URL") is null)
            Skip = "Set IGW_QUESTDB_TEST_URL to a disposable authenticated QuestDB instance.";
    }
}
