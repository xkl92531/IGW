using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace IGW.Observability;

/// <summary>日志中心的查询条件：参数校验与逐条匹配。语义与原 OpenObserve 查询一致(精确字段相等、关键词区分大小写的包含)。</summary>
public sealed class LogFilter
{
    private const int MaxTextLength = 500;
    private static readonly HashSet<string> Categories = new(StringComparer.OrdinalIgnoreCase)
    {
        "all", "collection", "interface", "state", "performance", "system"
    };
    private static readonly Dictionary<string, int> Levels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["trace"] = 0, ["trc"] = 0, ["debug"] = 1, ["dbg"] = 1,
        ["information"] = 2, ["info"] = 2, ["inf"] = 2,
        ["warning"] = 3, ["warn"] = 3, ["wrn"] = 3,
        ["error"] = 4, ["err"] = 4, ["critical"] = 5, ["fatal"] = 5, ["crt"] = 5
    };
    private static readonly string[] TaskFields = ["igw_task_id", "igw_task_name", "igw_task_ids", "igw_task_names"];
    private static readonly string[] InterfaceFields = ["igw_interface_name", "igw_operation", "igw_route", "igw_target"];
    private static readonly string[] TaskOrInterfaceFields = [.. TaskFields, .. InterfaceFields, "igw_scope"];
    private static readonly string[] SearchFields =
    [
        "igw_logger", "igw_module", "igw_feature", "igw_event_kind", "igw_device", "igw_task_id",
        "igw_task_name", "igw_task_ids", "igw_task_names", "igw_operation", "igw_target", "igw_trace_id", "igw_tag_values"
    ];

    private readonly List<(string Field, string Value)> _exact = [];
    private readonly List<(string[] Fields, string Value)> _contains = [];
    private readonly List<byte[]> _needles = [];
    private readonly HashSet<string> _pathTaskIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _termTaskIds = new(StringComparer.Ordinal);
    private string? _taskOrInterface;

    public long FromMicros { get; private init; }
    public long ToMicros { get; private init; }
    public int? Level { get; private set; }
    public string? Category { get; private set; }
    public string? Search { get; private set; }
    public string? ProjectPath { get; private set; }
    public int Page { get; private init; }
    public int PageSize { get; private init; }

    /// <summary>是否需要解析整行才能判断(只按时间/级别过滤时不用)。</summary>
    public bool NeedsParse => !CountableFromIndex || Category is not null;

    /// <summary>只按时间、分类、级别过滤(默认视图和自动刷新)：已封存分段可以直接用索引里的分钟计数。</summary>
    internal bool CountableFromIndex => _exact.Count == 0 && _contains.Count == 0 && Search is null && ProjectPath is null && _taskOrInterface is null;

    private string? _signature;

    /// <summary>
    /// 决定一条日志是否匹配的全部条件(不含时间范围和分页)，作分段计数缓存的键。
    /// 每个值带长度前缀，任意内容拼起来都不会混淆。
    /// </summary>
    internal string Signature => _signature ??= BuildSignature();

    private string BuildSignature()
    {
        var text = new StringBuilder();
        void Append(string? value) => text.Append(value is null ? "-" : $"{value.Length}:{value}").Append('|');
        Append(Level?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(Category);
        foreach (var (field, value) in _exact) { Append("=" + field); Append(value); }
        foreach (var (fields, value) in _contains) { Append("~" + string.Join(',', fields)); Append(value); }
        Append(Search);
        Append(ProjectPath);
        // 整个任务集合占一个带长度的区段，避免可变项数与前面的精确字段/搜索条件拼出同一个签名。
        Append(JsonSerializer.Serialize(_pathTaskIds.Order(StringComparer.Ordinal)));
        Append(_taskOrInterface);
        Append(JsonSerializer.Serialize(_termTaskIds.Order(StringComparer.Ordinal)));
        return text.ToString();
    }

    public static LogFilter Create(LogCenterRequest request, int maxPageSize = 200)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.To <= request.From) throw new ArgumentException("日志结束时间必须晚于开始时间。");
        if (request.To - request.From > TimeSpan.FromDays(31)) throw new ArgumentException("单次日志查询不能超过 31 天。");
        if (!Categories.Contains(request.Category)) throw new ArgumentException("日志分类无效。", nameof(request.Category));
        if (request.Page is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(request.Page));
        if (request.PageSize < 1 || request.PageSize > maxPageSize) throw new ArgumentOutOfRangeException(nameof(request.PageSize));

        var filter = new LogFilter
        {
            FromMicros = LogRecordCodec.ToMicros(request.From),
            ToMicros = LogRecordCodec.ToMicros(request.To),
            Page = request.Page,
            PageSize = request.PageSize
        };
        if (!request.Category.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            filter.Category = request.Category.ToLowerInvariant();
            filter.AddNeedle(filter.Category, quoted: true);
        }
        filter.AddExact("igw_protocol", request.Protocol, nameof(request.Protocol));
        filter.AddExact("igw_channel", request.Channel, nameof(request.Channel));
        filter.AddExact("igw_device", request.Device, nameof(request.Device));
        filter.AddExact("igw_db", request.Database, nameof(request.Database));
        filter.AddExact("igw_task_id", request.TaskId, nameof(request.TaskId));
        filter.AddExact("igw_module", request.Module, nameof(request.Module));
        filter.AddExact("igw_feature", request.Feature, nameof(request.Feature));
        filter.AddExact("igw_trace_id", request.TraceId, nameof(request.TraceId));
        filter.AddContains(TaskFields, request.Task, nameof(request.Task));
        filter.AddContains(InterfaceFields, request.Interface, nameof(request.Interface));
        if (!string.IsNullOrWhiteSpace(request.TaskOrInterface))
        {
            filter._taskOrInterface = Validate(request.TaskOrInterface, nameof(request.TaskOrInterface));
            filter._termTaskIds.UnionWith(request.TaskOrInterfaceTaskIds);
            // 组名可能只在当前工程绑定里，历史接口记录只有任务 ID，不能要求日志原文也出现输入片段。
            if (filter._termTaskIds.Count == 0) filter.AddNeedle(filter._taskOrInterface, quoted: false);
        }
        if (!string.IsNullOrWhiteSpace(request.Level))
        {
            string value = Validate(request.Level, nameof(request.Level));
            if (!Levels.TryGetValue(value, out int level)) throw new ArgumentException("日志级别无效。", nameof(request.Level));
            filter.Level = level;
        }
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            filter.Search = Validate(request.Search, nameof(request.Search));
            filter.AddNeedle(filter.Search, quoted: false);
        }
        if (!string.IsNullOrWhiteSpace(request.Path))
        {
            filter.ProjectPath = Validate(request.Path, nameof(request.Path));
            filter._pathTaskIds.UnionWith(request.PathTaskIds);
            // MES 接口/业务日志只记任务 Id，不一定含工程路径，不能用 path 字节把 OR 的另一支提前排除。
            if (filter._pathTaskIds.Count == 0) filter.AddNeedle(filter.ProjectPath, quoted: false);
        }
        return filter;
    }

    private void AddExact(string field, string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        string text = Validate(value, name);
        _exact.Add((field, text));
        AddNeedle(text, quoted: true);
    }

    private void AddContains(string[] fields, string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        string text = Validate(value, name);
        _contains.Add((fields, text));
        AddNeedle(text, quoted: false);
    }

    /// <summary>
    /// 预筛选：条件值必然以原文出现在行里(存储时不转义中文)。含引号、反斜杠或控制字符的值在 JSON 里会被转义，
    /// 这种值不做字节预筛，交给逐条解析判断。
    /// </summary>
    private void AddNeedle(string value, bool quoted)
    {
        if (value.Any(ch => ch is '"' or '\\' || char.IsControl(ch))) return;
        _needles.Add(Encoding.UTF8.GetBytes(quoted ? "\"" + value + "\"" : value));
    }

    private static string Validate(string value, string name)
    {
        string normalized = value.Trim();
        if (normalized.Length > MaxTextLength)
            throw new ArgumentException($"{name} 最长为 {MaxTextLength} 个字符。", name);
        return normalized;
    }

    /// <summary>根据分段索引判断能否整段跳过。正在写入的分段索引仍在变化，不据此跳过。</summary>
    public bool CanSkip(LogSegmentView segment)
    {
        // 按分类拆了文件夹的分段只含这一类日志(正在写的也是)；旧版本混写的分段(Category 为 null)要看索引
        if (Category is not null && segment.Category is not null && !string.Equals(segment.Category, Category, StringComparison.Ordinal))
            return true;
        if (segment.Active) return false;
        LogSegmentIndex index = segment.Index;
        if (Level is int level && index.Levels.Length > level && index.Levels[level] == 0) return true;
        if (Category is not null && !index.MayContain("igw_category", Category)) return true;
        foreach (var (field, value) in _exact)
            if (Array.IndexOf(LogSegmentIndex.IndexedFields, field) >= 0 && !index.MayContain(field, value)) return true;
        return false;
    }

    public bool HeaderMatches(long timestampMicros, int level) =>
        timestampMicros >= FromMicros && timestampMicros < ToMicros && (Level is null || Level == level);

    public bool LineMayMatch(ReadOnlySpan<byte> line)
    {
        foreach (byte[] needle in _needles)
            if (line.IndexOf(needle) < 0) return false;
        return true;
    }

    public bool Matches(LogRecord record)
    {
        if (!HeaderMatches(record.TimestampMicros, record.Level)) return false;
        if (Category is not null && !string.Equals(record.Category, Category, StringComparison.Ordinal)) return false;
        foreach (var (field, value) in _exact)
            if (!string.Equals(Text(record, field), value, StringComparison.Ordinal)) return false;
        foreach (var (fields, value) in _contains)
            if (!fields.Any(field => Text(record, field).Contains(value, StringComparison.Ordinal))) return false;
        if (Search is not null && !record.Message.Contains(Search, StringComparison.Ordinal)
            && !SearchFields.Any(field => Text(record, field).Contains(Search, StringComparison.Ordinal)))
            return false;
        if (_taskOrInterface is not null
            && !TaskOrInterfaceFields.Any(field => Text(record, field).Contains(_taskOrInterface, StringComparison.Ordinal))
            && !RelatedTaskMatches(record, _termTaskIds)) return false;
        if (ProjectPath is not null && !PathMatches(record, ProjectPath) && !RelatedTaskMatches(record, _pathTaskIds)) return false;
        return true;
    }

    private static bool RelatedTaskMatches(LogRecord record, HashSet<string> taskIds) => taskIds.Count > 0
        && (taskIds.Contains(Text(record, "igw_task_id"))
            || Text(record, "igw_task_ids").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Any(taskIds.Contains));

    /// <summary>
    /// 这条日志是否属于工程里的这一项(含下级)：
    /// 通道字段等于它；设备字段(「通道.设备」)等于它或在它下面；或者消息、对象等字段里完整提到了它
    /// (如点位名出现在写值、MES 上报、点位异常的消息里)。
    /// 「完整提到」按路径段判断：PLC6 不会误中 PLC66 或 X.PLC6，但会命中 PLC6.D1.T1。
    /// </summary>
    internal static bool PathMatches(LogRecord record, string path)
    {
        if (string.Equals(Text(record, "igw_channel"), path, StringComparison.Ordinal)) return true;
        string device = Text(record, "igw_device");
        if (device.Length > 0 && (device == path || device.StartsWith(path + ".", StringComparison.Ordinal))) return true;
        return ScopeMatches(record, path) || MentionsPath(record.Message, path)
            || SearchFields.Any(field => MentionsPath(Text(record, field), path));
    }

    private static bool ScopeMatches(LogRecord record, string path)
    {
        string scope = Text(record, "igw_scope");
        if (scope.Length == 0) return false;
        // MES 作用域是一个完整 GroupRef；单任务采集也只有一个范围，名称中的标点不是路径分隔符。
        if (Text(record, "igw_task_id").Length > 0 || !Text(record, "igw_task_ids").Contains(", ", StringComparison.Ordinal))
            return IsPathOrDescendant(scope, path);
        // Engine 对共享采集的不同范围使用精确的 ", " 分隔。逗号本身不是分隔符。
        string[] scopes = scope.Split(", ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        // 旧格式没有转义分隔串。若名称含 ", " 导致片段不是完整组路径，不能把截断名称误判为另一组；由任务 ID 关联兜底。
        if (scopes.Any(value => value.Count(ch => ch == '.') < 2)) return IsPathOrDescendant(scope, path);
        return scopes.Any(value => IsPathOrDescendant(value, path));
    }

    internal static bool IsPathOrDescendant(string value, string path) =>
        value.Equals(path, StringComparison.Ordinal) || value.StartsWith(path + ".", StringComparison.Ordinal);

    internal static bool MentionsPath(string text, string path)
    {
        for (int at = text.IndexOf(path, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(path, at + 1, StringComparison.Ordinal))
        {
            int end = at + path.Length;
            bool startsSegment = at == 0 || !IsPathChar(text[at - 1]) && text[at - 1] != '.';
            bool endsSegment = end == text.Length || !IsPathChar(text[end]);   // 后面是 . [ 空格 引号等都算完整
            if (startsSegment && endsSegment) return true;
        }
        return false;
    }

    private static bool IsPathChar(char ch) => char.IsLetterOrDigit(ch) || ch is '_' or '-';

    internal static string Text(LogRecord record, string field) =>
        record.Fields.TryGetValue(field, out object? value) ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "" : "";
}

/// <summary>
/// 在日志分段上执行日志中心查询：索引跳段 → 计数与趋势(能用索引分钟计数或缓存的不读文件，其余并行扫描)
/// → 只回读当前页所在的分段取明细。
/// </summary>
public static class LogQueryEngine
{
    /// <summary>
    /// 单次查询(计数 + 回读当前页)的总时间预算。计数最多用到“预算 - 留给取页的时间”，之后停止扫描更早的分段，
    /// 总数按已扫描部分返回并标记 Partial；取页也超时则返回已取到的(从最新开始连续的)部分，同样标记 Partial。
    /// 前端这个接口的 HTTP 超时是 60 秒，预算要远小于它：慢盘上也要在预算前后返回，不让自动刷新的查询堆起来。
    /// </summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(8);

    /// <summary>预算里留给回读当前页的时间：最多 2 秒，预算很小时取四分之一。</summary>
    private static TimeSpan PageReserve(TimeSpan budget) =>
        TimeSpan.FromTicks(Math.Min(TimeSpan.FromSeconds(2).Ticks, budget.Ticks / 4));

    /// <summary>cancellationToken 是请求的 RequestAborted：浏览器超时或页面关掉后立即停止扫描，抛出 OperationCanceledException。</summary>
    public static LogCenterResponse Query(LogSegmentStore store, LogCenterRequest request, TimeSpan? budget = null,
        CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        LogFilter filter = LogFilter.Create(request);
        cancellationToken.ThrowIfCancellationRequested();
        LogSegmentView[] segments = NewestFirst(store.Snapshot(filter.FromMicros, filter.ToMicros)
            .Where(segment => !filter.CanSkip(segment)));
        long bucketTicks = BucketTicks(request.To - request.From);
        TimeSpan limit = TimeSpan.FromTicks(Math.Clamp((budget ?? DefaultBudget).Ticks, 0, TimeSpan.TicksPerDay));   // CancelAfter 不接受负数和过大的值
        TimeSpan countLimit = limit - PageReserve(limit);
        // 超出预算只停止扫描、返回已有结果；请求被放弃则整个查询取消。正在解压/扫描的分段也按块检查，不必等它读完。
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overall.CancelAfter(limit);
        using var counting = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
        counting.CancelAfter(countLimit);

        var results = new SegmentCount?[segments.Length];
        // 从新到旧逐个领取分段(不预先切块)：预算用完时，算完的基本是连续的最新一段，能用上的结果最多。
        Parallel.ForEach(Partitioner.Create(Enumerable.Range(0, segments.Length), EnumerablePartitionerOptions.NoBuffering),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4) },
            (i, state) =>
            {
                if (counting.IsCancellationRequested || watch.Elapsed >= countLimit) { state.Stop(); return; }
                try { results[i] = CountSegment(store, segments[i], filter, bucketTicks, counting.Token); }
                catch (OperationCanceledException) when (counting.IsCancellationRequested) { state.Stop(); }
            });
        cancellationToken.ThrowIfCancellationRequested();
        // 从第一个没算完的分段起按“未扫描”处理，保证计数连续。
        int scanned = Array.FindIndex(results, result => result is null);
        bool partial = scanned >= 0;
        if (!partial) scanned = results.Length;

        long total = 0;
        var buckets = new SortedDictionary<long, (int Total, int Warn, int Error)>();
        for (int i = 0; i < scanned; i++)
        {
            total += results[i]!.Matches;
            foreach (var (key, value) in results[i]!.Buckets)
            {
                buckets.TryGetValue(key, out var existing);
                buckets[key] = (existing.Total + value.Total, existing.Warn + value.Warn, existing.Error + value.Error);
            }
        }

        var entries = new List<LogCenterEntry>(filter.PageSize);
        long skip = (long)(filter.Page - 1) * filter.PageSize;
        try
        {
            foreach (var (start, end) in OverlappingGroups(segments, scanned))
            {
                if (entries.Count >= filter.PageSize) break;
                long matches = 0;
                for (int i = start; i < end; i++) matches += results[i]!.Matches;
                if (skip >= matches) { skip -= matches; continue; }   // 前面的页：只用计数跳过，不读文件
                if (watch.Elapsed >= limit) { partial = true; break; }
                List<LogRecord> oldestFirst = CollectGroup(store, segments, start, end, filter, overall.Token);
                for (long j = oldestFirst.Count - 1 - skip; j >= 0 && entries.Count < filter.PageSize; j--)
                    entries.Add(ToEntry(oldestFirst[(int)j]));
                skip = 0;
            }
        }
        // 取页超出预算：已取到的是从最新开始连续的一段，照样返回，标记 Partial
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { partial = true; }
        cancellationToken.ThrowIfCancellationRequested();

        var histogram = buckets.Select(item => new LogHistogramBucket(
            new DateTimeOffset(item.Key, TimeSpan.Zero), item.Value.Total, item.Value.Warn, item.Value.Error)).ToArray();
        return new LogCenterResponse(entries, total, watch.ElapsedMilliseconds, histogram,
            LogCenterSummaries.Devices(entries), LogCenterSummaries.Outages(entries), partial);
    }

    /// <summary>按时间从旧到新逐条输出匹配的日志，供打包导出。</summary>
    public static async IAsyncEnumerable<LogCenterEntry> StreamAscending(
        LogSegmentStore store, LogCenterRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        LogFilter filter = LogFilter.Create(request, maxPageSize: int.MaxValue);
        LogSegmentView[] segments = NewestFirst(store.Snapshot(filter.FromMicros, filter.ToMicros)
            .Where(segment => !filter.CanSkip(segment)));
        var groups = OverlappingGroups(segments, segments.Length).Reverse().ToArray();   // 从旧到新
        // 调用方写出当前一组时，下一组已在后台解压、解析(导出时读和压缩写并行)
        Task<List<LogRecord>>? next = groups.Length > 0 ? CollectAsync(groups[0]) : null;
        for (int index = 0; index < groups.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<LogRecord> records = await next!.ConfigureAwait(false);
            next = index + 1 < groups.Length ? CollectAsync(groups[index + 1]) : null;
            foreach (LogRecord record in records)
                yield return ToEntry(record);
        }

        Task<List<LogRecord>> CollectAsync((int Start, int End) group) =>
            Task.Run(() => CollectGroup(store, segments, group.Start, group.End, filter, cancellationToken), cancellationToken);
    }

    /// <summary>按结束时间从新到旧排序(先取一次时间：正在写的分段索引还在变化)。</summary>
    private static LogSegmentView[] NewestFirst(IEnumerable<LogSegmentView> segments) =>
        segments.Select(segment => (Segment: segment, To: segment.Index.ToMicros, From: segment.Index.FromMicros))
            .OrderByDescending(item => item.To).ThenByDescending(item => item.From)
            .Select(item => item.Segment).ToArray();

    /// <summary>
    /// 把按结束时间从新到旧排好的前 count 个分段，分成时间上互相重叠的组(下标 [Start, End))，组按从新到旧给出。
    /// 日志按分类拆了文件夹后，同一时段每个分类各有一个分段：组内要按时间合并，翻页和导出才不会先列完一类再列另一类。
    /// </summary>
    internal static IEnumerable<(int Start, int End)> OverlappingGroups(IReadOnlyList<LogSegmentView> newestFirst, int count)
    {
        if (count <= 0) yield break;
        int start = 0;
        long earliest = newestFirst[0].Index.FromMicros;
        for (int i = 1; i < count; i++)
        {
            LogSegmentIndex index = newestFirst[i].Index;
            if (index.ToMicros >= earliest) { earliest = Math.Min(earliest, index.FromMicros); continue; }
            yield return (start, i);
            start = i;
            earliest = index.FromMicros;
        }
        yield return (start, count);
    }

    /// <summary>一组分段里所有匹配的记录，按时间从旧到新(同一时间保持文件中的先后)。</summary>
    private static List<LogRecord> CollectGroup(LogSegmentStore store, IReadOnlyList<LogSegmentView> segments, int start, int end,
        LogFilter filter, CancellationToken cancellationToken)
    {
        if (end - start == 1)
            return Collect(store.Read(segments[start], cancellationToken), filter, cancellationToken)
                .OrderBy(record => record.TimestampMicros).ToList();
        var records = new List<LogRecord>();
        for (int i = start; i < end; i++) records.AddRange(Collect(store.Read(segments[i], cancellationToken), filter, cancellationToken));
        return records.OrderBy(record => record.TimestampMicros).ToList();   // OrderBy 是稳定排序
    }

    /// <summary>
    /// 一个分段的计数。正在写入的分段内容还在增加，每次都重扫；已封存分段先看能否直接用索引的分钟计数，
    /// 再查缓存，都不行才解压扫描并记入缓存。所以自动刷新和翻页基本只重扫正在写入的分段。
    /// </summary>
    private static SegmentCount CountSegment(LogSegmentStore store, LogSegmentView segment, LogFilter filter, long bucketTicks,
        CancellationToken cancellationToken)
    {
        if (segment.Active) return Count(store.Read(segment, cancellationToken), filter, bucketTicks, cancellationToken);
        if (CountFromIndex(segment.Index, filter, bucketTicks) is { } indexed) return indexed;
        LogSegmentIndex index = segment.Index;
        // 分段里的记录都在索引的 [From, To] 内，把查询范围裁到分段上：整段都在范围内时，时间窗口怎么滑动都命中同一个键
        var key = new LogCountCache.Key(LogCountCache.SegmentId(segment.Path), index.Count, filter.Signature,
            Math.Max(filter.FromMicros, index.FromMicros), Math.Min(filter.ToMicros, index.ToMicros + 1), bucketTicks);
        if (store.CountCache.TryGet(key, out SegmentCount cached)) return cached;
        byte[] raw = store.Read(segment, cancellationToken);
        SegmentCount counted = Count(raw, filter, bucketTicks, cancellationToken);
        if (raw.Length > 0) store.CountCache.Add(key, counted);   // 读到空(刚被保留期清理)不缓存
        return counted;
    }

    /// <summary>
    /// 用索引里的分钟计数算一个已封存分段，不读文件。条件：只按时间/分类/级别过滤、分段整段落在查询范围内、索引带分钟计数；
    /// 按分类查询时还要求分段只含这一类(system 文件夹里也放着不认识的分类，旧版本分段各类混写)。不满足返回 null，走扫描。
    /// 趋势图的桶宽都是整分钟的倍数且按 Unix 纪元对齐，按分钟归桶和逐条归桶结果相同。
    /// </summary>
    internal static SegmentCount? CountFromIndex(LogSegmentIndex index, LogFilter filter, long bucketTicks)
    {
        if (!filter.CountableFromIndex || index.Minutes is not { } minutes) return null;
        if (index.FromMicros < filter.FromMicros || index.ToMicros >= filter.ToMicros) return null;
        if (filter.Category is { } category && !index.OnlyContains("igw_category", category)) return null;
        int matches = 0;
        var buckets = new Dictionary<long, (int Total, int Warn, int Error)>();
        foreach (var (minute, counts) in minutes)
        {
            int total, warn, error;
            if (filter.Level is int level) (total, warn, error) = (counts[level], level == 3 ? counts[level] : 0, level >= 4 ? counts[level] : 0);
            else (total, warn, error) = (counts.Sum(), counts[3], counts[4] + counts[5]);
            if (total == 0) continue;
            matches += total;
            long key = BucketKey(minute * LogSegmentIndex.MicrosPerMinute, bucketTicks);
            buckets.TryGetValue(key, out var bucket);
            buckets[key] = (bucket.Total + total, bucket.Warn + warn, bucket.Error + error);
        }
        return new SegmentCount(matches, buckets);
    }

    private static long BucketKey(long timestampMicros, long bucketTicks)
    {
        long ticks = DateTimeOffset.UnixEpoch.Ticks + timestampMicros * 10;
        return ticks / bucketTicks * bucketTicks;
    }

    private static SegmentCount Count(byte[] raw, LogFilter filter, long bucketTicks, CancellationToken cancellationToken)
    {
        int matches = 0, lines = 0;
        var buckets = new Dictionary<long, (int Total, int Warn, int Error)>();
        LogRecordCodec.ForEachLine(raw, (line, _) =>
        {
            if ((++lines & 0xFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (!LogRecordCodec.TryReadHeader(line, out long timestamp, out int level)) return true;
            if (!filter.HeaderMatches(timestamp, level) || !filter.LineMayMatch(line)) return true;
            if (filter.NeedsParse && (LogRecordCodec.Parse(line) is not { } record || !filter.Matches(record))) return true;
            matches++;
            long key = BucketKey(timestamp, bucketTicks);
            buckets.TryGetValue(key, out var bucket);
            buckets[key] = (bucket.Total + 1, bucket.Warn + (level == 3 ? 1 : 0), bucket.Error + (level >= 4 ? 1 : 0));
            return true;
        });
        return new SegmentCount(matches, buckets);
    }

    /// <summary>一个分段内所有匹配的记录，保持文件中的先后顺序。</summary>
    private static List<LogRecord> Collect(byte[] raw, LogFilter filter, CancellationToken cancellationToken)
    {
        var records = new List<LogRecord>();
        int lines = 0;
        LogRecordCodec.ForEachLine(raw, (line, _) =>
        {
            if ((++lines & 0xFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (!LogRecordCodec.TryReadHeader(line, out long timestamp, out int level)) return true;
            if (!filter.HeaderMatches(timestamp, level) || !filter.LineMayMatch(line)) return true;
            if (LogRecordCodec.Parse(line) is { } record && filter.Matches(record)) records.Add(record);
            return true;
        });
        return records;
    }

    private static long BucketTicks(TimeSpan range) =>
        range <= TimeSpan.FromHours(2) ? TimeSpan.FromMinutes(1).Ticks
        : range <= TimeSpan.FromDays(2) ? TimeSpan.FromMinutes(15).Ticks
        : range <= TimeSpan.FromDays(7) ? TimeSpan.FromHours(1).Ticks
        : TimeSpan.FromDays(1).Ticks;

    private static readonly JsonElement[] SeverityElements =
        LogRecordCodec.LevelNames.Select(name => JsonSerializer.SerializeToElement(name)).ToArray();

    public static LogCenterEntry ToEntry(LogRecord record)
    {
        var attributes = new Dictionary<string, JsonElement>(record.Fields.Count + 3, StringComparer.Ordinal);
        // 整条记录的字段一次序列化成一个 JSON 对象再拆开：原先每个字段各建一个 JsonDocument，
        // 导出几十万条日志时光这一步就是几百万次文档分配。
        foreach (JsonProperty field in JsonSerializer.SerializeToElement(record.Fields).EnumerateObject())
            attributes[field.Name] = field.Value;
        attributes["igw_category"] = JsonSerializer.SerializeToElement(record.Category);
        attributes["severity"] = SeverityElements[Math.Clamp(record.Level, 0, SeverityElements.Length - 1)];
        string? traceId = NonEmpty(LogFilter.Text(record, "igw_trace_id"));
        string? payloadId = NonEmpty(LogFilter.Text(record, "igw_payload_id"));
        return new LogCenterEntry(record.Timestamp, LogRecordCodec.LevelName(record.Level), record.Category,
            record.Message, traceId, payloadId, attributes);
    }

    private static string? NonEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>当前页里的设备最新状态与离线事件，供日志中心顶部摘要使用。</summary>
public static class LogCenterSummaries
{
    public static IReadOnlyList<LogDeviceSummary> Devices(IReadOnlyList<LogCenterEntry> entries) =>
        entries.Where(entry => Attribute(entry, "igw_device") is not null)
            .GroupBy(entry => Attribute(entry, "igw_device")!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(entry => entry.Timestamp).First())
            .Select(entry => new LogDeviceSummary(Attribute(entry, "igw_device")!, Attribute(entry, "igw_status") ?? "unknown", entry.Timestamp))
            .OrderBy(item => item.Device, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static IReadOnlyList<LogOutageSummary> Outages(IReadOnlyList<LogCenterEntry> entries) =>
        entries.Where(entry =>
                string.Equals(Attribute(entry, "igw_category"), "state", StringComparison.OrdinalIgnoreCase)
                && Attribute(entry, "igw_status") is "offline" or "disconnected" or "faulted")
            .Select(entry => new LogOutageSummary(Attribute(entry, "igw_device") ?? "unknown", entry.Timestamp, null))
            .ToArray();

    private static string? Attribute(LogCenterEntry entry, string name)
    {
        if (!entry.Attributes.TryGetValue(name, out JsonElement value)) return null;
        string? text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => value.GetRawText()
        };
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
