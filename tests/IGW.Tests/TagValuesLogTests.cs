using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using IGW.Observability;
using Microsoft.Extensions.Logging;
using Xunit;

namespace IGW.Tests;

/// <summary>
/// 采集记录、质量变化记录带上那一刻的点位值(igw_tag_values)：现场查日志时能直接看到 PLC 里是什么值，
/// 体积有上限，且右键点位「查看日志」、按点位名搜关键词都能找到这些记录。
/// </summary>
public sealed class TagValuesLogTests
{
    private static readonly DateTime At = new(2026, 10, 8, 8, 0, 0, DateTimeKind.Utc);

    // ---------- 字段本身：格式、质量、上限、截断、数组 ----------

    [Fact]
    public void Values_are_written_per_point_with_invariant_numbers_and_quality_only_when_not_good()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");          // 逗号小数的现场系统也写成 12.5
        try
        {
            TagBinding[] tags = [Tag("PLC6.D1.G.Speed"), Tag("PLC6.D1.G.Run"), Tag("PLC6.D1.G.Code"), Tag("PLC6.D1.G.Broken"), Tag("PLC6.D1.G.Missing")];
            TagSample[] samples =
            [
                new("PLC6.D1.G.Speed", DataValue.GoodAt(12.5f, At)),
                new("PLC6.D1.G.Run", DataValue.GoodAt(true, At)),
                new("PLC6.D1.G.Code", DataValue.GoodAt("SN-001", At)),
                new("PLC6.D1.G.Broken", DataValue.BadAt(StatusCode.BadDeviceFailure, At)),
            ];

            JsonElement root = Parse(new TagValuesLogField(tags, samples, StatusCode.BadCommFailure));

            Assert.Equal(5, root.GetProperty("total").GetInt32());
            Assert.False(root.TryGetProperty("omitted", out _));
            JsonElement[] points = root.GetProperty("points").EnumerateArray().ToArray();
            Assert.Equal("PLC6.D1.G.Speed", points[0][0].GetString());
            Assert.Equal("12.5", points[0][1].GetRawText());
            Assert.Equal(2, points[0].GetArrayLength());                                  // Good 不写质量
            Assert.True(points[1][1].GetBoolean());
            Assert.Equal("SN-001", points[2][1].GetString());
            Assert.Equal(JsonValueKind.Null, points[3][1].ValueKind);
            Assert.Equal("BadDeviceFailure", points[3][2].GetString());
            Assert.Equal("PLC6.D1.G.Missing", points[4][0].GetString());                  // 没返回的点按缺失状态记
            Assert.Equal("BadCommFailure", points[4][2].GetString());
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void Large_blocks_are_capped_with_bad_points_kept_and_the_rest_counted_as_omitted()
    {
        TagBinding[] tags = Enumerable.Range(0, 250).Select(index => Tag($"PLC6.D1.G.T{index:D3}")).ToArray();
        TagSample[] samples = tags.Select((tag, index) => new TagSample(tag.TagId, index >= 240
            ? DataValue.BadAt(StatusCode.BadTimeout, At)
            : DataValue.GoodAt(index, At))).ToArray();

        JsonElement root = Parse(new TagValuesLogField(tags, samples, StatusCode.BadCommFailure));

        Assert.Equal(250, root.GetProperty("total").GetInt32());
        Assert.Equal(50, root.GetProperty("omitted").GetInt32());
        string[] ids = root.GetProperty("points").EnumerateArray().Select(point => point[0].GetString()!).ToArray();
        Assert.Equal(TagValuesLogField.MaxPoints, ids.Length);
        Assert.Contains("PLC6.D1.G.T249", ids);                                       // 坏点优先保留
        Assert.Contains("PLC6.D1.G.T000", ids);
        Assert.DoesNotContain("PLC6.D1.G.T239", ids);
        Assert.Equal(ids.OrderBy(id => id, StringComparer.Ordinal), ids);              // 仍按原顺序
    }

    [Fact]
    public void Long_strings_are_truncated_and_arrays_are_summarized()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            var array = PrimitiveArrayValue<double>.Create(
                ArrayShape.Create([100], [0]), Enumerable.Range(0, 100).Select(index => index + 0.5).ToArray());
            TagBinding[] tags = [Tag("PLC6.D1.Text"), Tag("PLC6.D1.Curve"), Tag("PLC6.D1.Secret")];
            TagSample[] samples =
            [
                new("PLC6.D1.Text", DataValue.GoodAt(new string('x', 500), At)),
                new("PLC6.D1.Curve", DataValue.GoodAt(array, At)),
                new("PLC6.D1.Secret", DataValue.GoodAt("password=abc123", At)),
            ];

            JsonElement[] points = Parse(new TagValuesLogField(tags, samples, StatusCode.BadCommFailure))
                .GetProperty("points").EnumerateArray().ToArray();

            string text = points[0][1].GetString()!;
            Assert.Equal(TagValuesLogField.MaxTextLength + 1, text.Length);
            Assert.EndsWith("…", text);
            Assert.Equal("数组[100]: 0.5, 1.5, 2.5, 3.5, 4.5, …", points[1][1].GetString());
            Assert.Equal("password=***", points[2][1].GetString());                    // 和其他日志字段一样脱敏
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void Bad_only_lists_just_the_bad_points_and_nothing_bad_means_no_field()
    {
        TagBinding[] tags = [Tag("PLC6.D1.A"), Tag("PLC6.D1.B")];
        TagSample[] samples = [new("PLC6.D1.A", DataValue.GoodAt(1, At)), new("PLC6.D1.B", DataValue.BadAt(StatusCode.BadTimeout, At))];

        JsonElement root = Parse(new TagValuesLogField(tags, samples, StatusCode.BadCommFailure, badOnly: true));
        Assert.True(root.GetProperty("badOnly").GetBoolean());
        Assert.Equal(1, root.GetProperty("total").GetInt32());
        Assert.Equal("PLC6.D1.B", root.GetProperty("points")[0][0].GetString());

        Assert.Equal("", new TagValuesLogField(tags, [samples[0], new("PLC6.D1.B", DataValue.GoodAt(2, At))],
            StatusCode.BadCommFailure, badOnly: true).ToString());
    }

    [Fact]
    public void A_broken_sample_list_drops_the_field_instead_of_throwing()
    {
        var field = new TagValuesLogField([Tag("PLC6.D1.A")], new ThrowingSamples(), StatusCode.BadCommFailure);
        Assert.Equal("", field.ToString());
    }

    // ---------- 采集记录汇总：首条、异常、汇总都带值；只序列化真正写出的那批 ----------

    [Fact]
    public void First_success_failure_and_summary_records_carry_values_and_only_written_batches_are_serialized()
    {
        var logger = new TelemetryTestLogger();
        var summarizer = new CollectionLogSummarizer(logger, TimeSpan.FromSeconds(30));
        var batches = new List<CountingSamples>();
        Dictionary<string, object?> Batch(int value, bool good = true)
        {
            var samples = new CountingSamples([
                new("PLC8.OP140.DB.Value0", DataValue.GoodAt(value, At)),
                new("PLC8.OP140.DB.Value1", good ? DataValue.GoodAt(value + 1000, At) : DataValue.BadAt(StatusCode.BadTimeout, At)),
            ]);
            batches.Add(samples);
            return new Dictionary<string, object?>
            {
                ["channel"] = "PLC8", ["device"] = "PLC8.OP140", ["blockId"] = 1, ["status"] = good ? "good" : "partial",
                ["durationMs"] = 10, ["sampleCount"] = 2, ["badCount"] = good ? 0 : 1,
                [TagValuesLogField.FieldName] = new TagValuesLogField(
                    [Tag("PLC8.OP140.DB.Value0"), Tag("PLC8.OP140.DB.Value1")], samples, StatusCode.BadCommFailure, badOnly: !good),
            };
        }

        summarizer.Completed(1, good: true, Batch(1), nowMs: 0);                         // 首条立即记
        for (int i = 1; i < 300; i++) summarizer.Completed(1, good: true, Batch(1 + i), nowMs: i * 100);
        summarizer.Completed(1, good: true, Batch(301), nowMs: 30_000);                  // 窗口到了：汇总
        summarizer.Completed(1, good: false, Batch(302, good: false), nowMs: 30_100);    // 异常立即记
        summarizer.Completed(1, good: false, Batch(303, good: false), nowMs: 30_200);    // 持续异常：30 秒内不再重复点位清单

        IReadOnlyList<IReadOnlyDictionary<string, object?>> records = logger.ByCategory("collection");
        Assert.Equal(["采集批次完成", "采集批次汇总", "采集批次完成", "采集批次完成"],
            records.Select(record => record["{OriginalFormat}"]?.ToString()));

        JsonElement first = Parse(records[0]["igw_tag_values"]);
        Assert.Equal(1, first.GetProperty("points")[0][1].GetInt32());
        JsonElement summary = Parse(records[1]["igw_tag_values"]);
        Assert.Equal(301, summary.GetProperty("points")[0][1].GetInt32());              // 窗口里最后一批的值
        Assert.Equal(1301, summary.GetProperty("points")[1][1].GetInt32());
        JsonElement failure = Parse(records[2]["igw_tag_values"]);
        Assert.Equal("PLC8.OP140.DB.Value1", Assert.Single(failure.GetProperty("points").EnumerateArray().ToArray())[0].GetString());
        Assert.Equal("BadTimeout", failure.GetProperty("points")[0][2].GetString());
        Assert.False(records[3].ContainsKey("igw_tag_values"));

        // 惰性：只有写出去的 3 批读过样本；被汇总吞掉的 299 批和被节流的异常批一次都没序列化
        Assert.Equal(3, batches.Count(batch => batch.Reads > 0));
        Assert.True(batches[0].Reads > 0 && batches[300].Reads > 0 && batches[301].Reads > 0);

        summarizer.Completed(1, good: false, Batch(304, good: false), nowMs: 60_100);    // 下一个窗口又带
        Assert.True(logger.ByCategory("collection")[^1].ContainsKey("igw_tag_values"));
    }

    // ---------- 设备循环：采集记录和质量变化记录 ----------

    [Fact]
    public async Task Device_loop_quality_changes_list_bad_points_and_then_the_recovered_values()
    {
        await using var hub = new DataHub();
        TagBinding[] bindings = Enumerable.Range(0, 2).Select(index =>
        {
            string id = $"PLC8.OP140.DB.Value{index}";
            var definition = new TagDef { Name = $"Value{index}", Address = $"DB1.DBW{index * 2}", DataType = IgwDataType.Int16, ScanRateMs = 20 };
            hub.Register(id, definition);
            return new TagBinding { TagId = id, Def = definition, Address = new TagAddress(1, index * 2, 2) };
        }).ToArray();
        var demands = new ScanDemandRegistry(hub);                                       // 有消费者才会采集
        using var demand = demands.Acquire("mes:task-1", bindings.Select(item => item.TagId), 20, ownerName: "OP140 出站");
        var logger = new TelemetryTestLogger();
        var loop = new DeviceLoop(new FlakyDriver(badReads: 3), bindings, hub, logger, "PLC8.OP140",
            scanDemands: demands, channelName: "PLC8", protocol: "siemens.s7");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        loop.Start(stop.Token);
        await WaitUntil(() => logger.ByCategory("state").Any(record => Equals(record["igw_event_kind"], "quality_change")
            && Equals(record["igw_status"], "good")), TimeSpan.FromSeconds(4), () => string.Join(" || ",
            logger.ByCategory("state").Concat(logger.ByCategory("collection")).Select(r => $"{r["{OriginalFormat}"]} {r.GetValueOrDefault("igw_status")} {r.GetValueOrDefault("igw_tag_values")}")));
        stop.Cancel();
        await loop.Completion;

        IReadOnlyDictionary<string, object?>[] quality = logger.ByCategory("state")
            .Where(record => Equals(record["igw_event_kind"], "quality_change")).ToArray();
        JsonElement bad = Parse(quality[0]["igw_tag_values"]);
        Assert.Equal("bad", quality[0]["igw_status"]);
        JsonElement badPoint = Assert.Single(bad.GetProperty("points").EnumerateArray().ToArray());   // 只列坏点和原因
        Assert.Equal("PLC8.OP140.DB.Value1", badPoint[0].GetString());
        Assert.Equal("BadDeviceFailure", badPoint[2].GetString());
        JsonElement good = Parse(quality[1]["igw_tag_values"]);
        Assert.Equal("good", quality[1]["igw_status"]);
        Assert.Equal(new[] { 7, 42 }, good.GetProperty("points").EnumerateArray().Select(point => point[1].GetInt32()));

        IReadOnlyList<IReadOnlyDictionary<string, object?>> collection = logger.ByCategory("collection");
        JsonElement partial = Parse(collection[0]["igw_tag_values"]);                   // 首个异常批次带坏点
        Assert.Equal("PLC8.OP140.DB.Value1", partial.GetProperty("points")[0][0].GetString());
        IReadOnlyDictionary<string, object?> recovered = collection.First(record => Equals(record["igw_status"], "good"));
        Assert.Contains("[\"PLC8.OP140.DB.Value1\",42]", recovered["igw_tag_values"]?.ToString());
    }

    // ---------- 存储与查找：落盘为字符串；工程路径和关键词能按点位名找到 ----------

    [Fact]
    public async Task Stored_collection_records_are_found_by_point_path_and_keyword()
    {
        string root = Path.Combine(Path.GetTempPath(), "igw-log-store", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using (var store = new LogSegmentStore(root, 30))
            {
                using var provider = new LogStoreLoggerProvider(store);
                ILogger logger = provider.CreateLogger("Device:PLC6.D1");
                TagBinding[] tags = [Tag("PLC6.D1.OP051.Barcode"), Tag("PLC6.D1.OP051.条码长度")];
                TagSample[] samples = [new("PLC6.D1.OP051.Barcode", DataValue.GoodAt("SN-778", At)), new("PLC6.D1.OP051.条码长度", DataValue.GoodAt(12, At))];
                GatewayTelemetry.Log(logger, LogLevel.Information, "collection", "采集批次汇总", new Dictionary<string, object?>
                {
                    ["channel"] = "PLC6", ["device"] = "PLC6.D1", ["blockId"] = 3,
                    [TagValuesLogField.FieldName] = new TagValuesLogField(tags, samples, StatusCode.BadCommFailure),
                });
                GatewayTelemetry.Log(logger, LogLevel.Information, "collection", "采集批次汇总", new Dictionary<string, object?>
                {
                    ["channel"] = "PLC6", ["device"] = "PLC6.D1", ["blockId"] = 4,
                });
                await store.FlushAsync();

                LogCenterResponse byPath = LogQueryEngine.Query(store, Request(path: "PLC6.D1.OP051.Barcode"));
                LogCenterEntry entry = Assert.Single(byPath.Items);
                string values = entry.Attributes["igw_tag_values"].GetString()!;
                Assert.Contains("[\"PLC6.D1.OP051.Barcode\",\"SN-778\"]", values);
                Assert.Equal(1, LogQueryEngine.Query(store, Request(path: "PLC6.D1.OP051")).Total);       // 组路径
                Assert.Equal(0, LogQueryEngine.Query(store, Request(path: "PLC6.D1.OP05")).Total);        // 不半截命中
                Assert.Equal(1, LogQueryEngine.Query(store, Request(search: "条码长度")).Total);            // 中文点位名关键词
                Assert.Equal(1, LogQueryEngine.Query(store, Request(search: "SN-778")).Total);             // 按值搜
            }
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void Path_filter_and_keyword_match_a_point_named_only_inside_the_values()
    {
        string values = "{\"total\":2,\"points\":[[\"PLC6.D1.OP051.Barcode\",\"SN-1\"],[\"PLC6.D1.Arr\",\"数组[10]\"]]}";
        var record = new LogRecord(LogRecordCodec.ToMicros(new DateTimeOffset(At)), 2, "state", "采集质量正常",
            new Dictionary<string, object?> { ["igw_device"] = "PLC6.D1", ["igw_tag_values"] = values });
        bool Matches(string? path = null, string? search = null) => LogFilter.Create(new LogCenterRequest
        {
            From = new DateTimeOffset(At).AddMinutes(-1), To = new DateTimeOffset(At).AddMinutes(1), Path = path, Search = search
        }).Matches(record);

        Assert.True(Matches(path: "PLC6.D1.OP051.Barcode"));
        Assert.True(Matches(path: "PLC6.D1.Arr"));
        Assert.False(Matches(path: "PLC6.D1.OP051.Bar"));
        Assert.True(Matches(search: "OP051.Barcode"));

        // 落盘后的整行里引号被转义，按字节预筛仍能命中点位路径
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        LogRecordCodec.Write(buffer, record);
        Assert.True(LogFilter.Create(new LogCenterRequest
        {
            From = new DateTimeOffset(At).AddMinutes(-1), To = new DateTimeOffset(At).AddMinutes(1), Path = "PLC6.D1.OP051.Barcode"
        }).LineMayMatch(buffer.WrittenSpan));
    }

    // ---------- helpers ----------

    private static LogCenterRequest Request(string? path = null, string? search = null) => new()
    {
        From = DateTimeOffset.UtcNow.AddMinutes(-5), To = DateTimeOffset.UtcNow.AddMinutes(5), Path = path, Search = search
    };

    private static TagBinding Tag(string id) => new()
    {
        TagId = id,
        Def = new TagDef { Name = id[(id.LastIndexOf('.') + 1)..], Address = "DB1.DBW0", DataType = IgwDataType.Int16 },
        Address = new TagAddress(1, 0, 2)
    };

    private static JsonElement Parse(object? field)
    {
        string text = Assert.IsType<string>(field?.ToString());
        Assert.NotEqual("", text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout, Func<string>? describe = null)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("condition not met " + describe?.Invoke());
            await Task.Delay(10);
        }
    }

    /// <summary>记录样本被读了几次，用来验证没写出的批次不序列化。</summary>
    private sealed class CountingSamples(TagSample[] samples) : IReadOnlyList<TagSample>
    {
        public int Reads;
        public TagSample this[int index] { get { Reads++; return samples[index]; } }
        public int Count { get { Reads++; return samples.Length; } }
        public IEnumerator<TagSample> GetEnumerator() { Reads++; return ((IEnumerable<TagSample>)samples).GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ThrowingSamples : IReadOnlyList<TagSample>
    {
        public TagSample this[int index] => throw new InvalidOperationException("driver buffer reused");
        public int Count => 1;
        public IEnumerator<TagSample> GetEnumerator() => throw new InvalidOperationException("driver buffer reused");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>前几次读取 Value1 坏(BadDeviceFailure)，之后全部正常。</summary>
    private sealed class FlakyDriver(int badReads) : IDeviceDriver
    {
        private int _reads;
        public string DriverId => "siemens.s7";
        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser { get; } = new Parser();
        public DriverLimits Limits { get; } = new() { MaxTagsPerRead = 100, MaxReadUnits = 100, MaxGap = 16 };

        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            State = ConnectionState.Connected;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            State = ConnectionState.Disconnected;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken cancellationToken)
        {
            bool bad = Interlocked.Increment(ref _reads) <= badReads;
            DateTime now = DateTime.UtcNow;
            return Task.FromResult<IReadOnlyList<TagSample>>(tags.Select(tag => new TagSample(tag.TagId,
                tag.TagId.EndsWith("Value1", StringComparison.Ordinal)
                    ? bad ? DataValue.BadAt(StatusCode.BadDeviceFailure, now) : DataValue.GoodAt((short)42, now)
                    : DataValue.GoodAt((short)7, now))).ToArray());
        }

        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WriteResult>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Parser : IAddressParser
    {
        public bool TryParse(string address, IgwDataType type, out TagAddress result, out string? error)
        {
            result = new TagAddress(1, 0, 2);
            error = null;
            return true;
        }
    }
}
