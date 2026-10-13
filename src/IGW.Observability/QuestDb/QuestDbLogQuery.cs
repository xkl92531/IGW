using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IGW.Observability;

public sealed class QuestDbLogQuery(QuestDbClient client)
{
    private static readonly string[] SearchFields = ["igw_logger", "igw_module", "igw_feature", "igw_event_kind", "igw_device",
        "igw_task_id", "igw_task_name", "igw_task_ids", "igw_task_names", "igw_operation", "igw_target", "igw_trace_id", "igw_tag_values"];
    internal static string Literal(string text) => "'" + text.Replace("'", "''") + "'";
    private static string JsonField(string field) => $"(case when {field}='True' or {field}='False' then cast({field} as varchar) else json_extract(raw,'$.f.{field}') end)";
    private const string Message = "json_extract(raw,'$.m')";
    // 1.7.20 的表没有 scope 热列；从原始属性读才能覆盖已迁移的历史日志，无需改表或重导入。
    private const string Scope = "json_extract(raw,'$.f.igw_scope')";

    private static string Contains(string value, IEnumerable<string> columns) => "(" + string.Join(" or ",
        columns.Select(column => $"strpos({column},{Literal(value.Trim())}) > 0")) + ")";

    private static string RelatedTasks(IReadOnlyList<string> taskIds)
    {
        string[] ids = taskIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) return "false";
        // 共享采集的任务列表按逗号分隔并去空白。匹配完整 ID，不能让 task-1 命中 task-10。
        string pattern = @"(^|,)\s*(?:" + string.Join('|', ids.Select(Regex.Escape)) + @")\s*(,|$)";
        return $"(igw_task_id in ({string.Join(',', ids.Select(id => Literal(QuestDbLogCodec.Flat(id))))}) or {JsonField("igw_task_ids")} ~ {Literal(pattern)})";
    }

    private static string ScopeMatches(string path)
    {
        string single = $"({Scope}={Literal(path)} or starts_with({Scope},{Literal(path + ".")}))";
        // 与 legacy ScopeMatches 相同：只有共享任务才拆精确的 ", "，名称里的逗号/括号不是路径分隔符。
        // 若任一片段不是完整组路径(至少两点)，旧格式的分隔符可能属于组名，保留整串并由任务关联兜底。
        const string scopeCharacter = @"(?:(?!, )[\s\S])";
        string fullScope = $"(?:{scopeCharacter}*\\.){{2}}{scopeCharacter}*";
        string unambiguous = "^" + fullScope + "(?:, " + fullScope + ")*$";
        string member = @"(^|, )\s*" + Regex.Escape(path) + @"(?:\.[\s\S]*?)?\s*($|, )";
        return $"(case when igw_task_id='' and strpos(igw_task_ids,', ') > 0 and {Scope} ~ {Literal(unambiguous)} then {Scope} ~ {Literal(member)} else {single} end)";
    }

    public static string Where(LogCenterRequest request)
    {
        LogFilter filter = LogFilter.Create(request, maxPageSize: int.MaxValue);
        var terms = new List<string> { $"ts >= cast({filter.FromMicros} as timestamp)", $"ts < cast({filter.ToMicros} as timestamp)" };
        if (filter.Category is not null) terms.Add("category=" + Literal(filter.Category));
        if (filter.Level is int level) terms.Add("level=" + level);
        void Exact(string field, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            string text = value.Trim();
            terms.Add(field + "=" + Literal(QuestDbLogCodec.Flat(text)));
        }
        Exact("igw_protocol", request.Protocol); Exact("igw_channel", request.Channel); Exact("igw_device", request.Device);
        Exact("igw_db", request.Database); Exact("igw_task_id", request.TaskId); Exact("igw_module", request.Module);
        Exact("igw_feature", request.Feature); Exact("igw_trace_id", request.TraceId);
        void AddContains(string? value, IEnumerable<string> columns)
        {
            if (!string.IsNullOrWhiteSpace(value)) terms.Add(Contains(value, columns));
        }
        string[] taskFields = ["igw_task_id", "igw_task_name", "igw_task_ids", "igw_task_names"];
        string[] interfaceFields = ["igw_interface_name", "igw_operation", "igw_route", "igw_target"];
        AddContains(request.Task, taskFields.Select(JsonField));
        AddContains(request.Interface, interfaceFields.Select(JsonField));
        AddContains(request.Search, new[] { Message }.Concat(SearchFields.Select(JsonField)));
        // 自由输入只查任务/脱敏接口属性和 scope，不查消息/报文。关联集合只扩展这一项，不绕过 path。
        if (!string.IsNullOrWhiteSpace(request.TaskOrInterface))
            terms.Add($"({Contains(request.TaskOrInterface, taskFields.Concat(interfaceFields).Select(JsonField).Append(Scope))} or {RelatedTasks(request.TaskOrInterfaceTaskIds)})");
        if (filter.ProjectPath is string path)
        {
            string pattern = "(^|[^\\p{L}\\p{Nd}_\\-.])" + Regex.Escape(path) + "($|[^\\p{L}\\p{Nd}_-])";
            string mention = string.Join(" or ", new[] { Message }.Concat(SearchFields.Select(JsonField))
                .Select(column => $"{column} ~ {Literal(pattern)}"));
            terms.Add($"(igw_channel={Literal(QuestDbLogCodec.Flat(path))} or igw_device={Literal(QuestDbLogCodec.Flat(path))} or starts_with(igw_device,{Literal(QuestDbLogCodec.Flat(path + "."))}) or {mention} or {ScopeMatches(path)} or {RelatedTasks(request.PathTaskIds)})");
        }
        return string.Join(" and ", terms);
    }

    public async Task<LogCenterResponse> QueryAsync(LogCenterRequest request, CancellationToken cancellationToken)
    {
        LogFilter filter = LogFilter.Create(request);
        string where = Where(request);
        var watch = Stopwatch.StartNew();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(LogQueryEngine.DefaultBudget);
        var entries = new List<LogCenterEntry>();
        var histogram = new List<LogHistogramBucket>();
        long total = 0;
        bool partial = false;
        try
        {
            int offset = checked((filter.Page - 1) * filter.PageSize);
            using JsonDocument page = await client.QueryAsync($"select raw from IGW_logs where {where} order by ts desc,id desc limit {offset},{offset + filter.PageSize}", budget.Token);
            foreach (JsonElement row in page.RootElement.GetProperty("dataset").EnumerateArray())
                entries.Add(LogQueryEngine.ToEntry(QuestDbLogCodec.Decode(row[0].GetString()!)));
            string period = request.To - request.From <= TimeSpan.FromHours(2) ? "1m"
                : request.To - request.From <= TimeSpan.FromDays(2) ? "15m"
                : request.To - request.From <= TimeSpan.FromDays(7) ? "1h" : "1d";
            using JsonDocument counts = await client.QueryAsync($"select ts,count(),sum(case when level=3 then 1 else 0 end),sum(case when level>=4 then 1 else 0 end) from IGW_logs where {where} sample by {period} align to calendar", budget.Token);
            foreach (JsonElement row in counts.RootElement.GetProperty("dataset").EnumerateArray())
            {
                long count = row[1].GetInt64(); total += count;
                histogram.Add(new LogHistogramBucket(row[0].GetDateTimeOffset(), checked((int)count), row[2].GetInt32(), row[3].GetInt32()));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { partial = true; }
        catch (HttpRequestException) { partial = true; }
        catch (InvalidOperationException) { partial = true; }
        if (partial) total = Math.Max(total, entries.Count);
        return new LogCenterResponse(entries, total, watch.ElapsedMilliseconds, histogram, LogCenterSummaries.Devices(entries), LogCenterSummaries.Outages(entries), partial);
    }

    public async IAsyncEnumerable<LogCenterEntry> StreamAsync(LogCenterRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string where = Where(request);
        string cursor = "";
        while (true)
        {
            using JsonDocument page = await client.QueryAsync($"select raw,ts,cast(id as varchar) from IGW_logs where {where}{cursor} order by ts,cast(id as varchar) limit 1000", cancellationToken);
            JsonElement rows = page.RootElement.GetProperty("dataset");
            foreach (JsonElement row in rows.EnumerateArray())
                yield return LogQueryEngine.ToEntry(QuestDbLogCodec.Decode(row[0].GetString()!));
            if (rows.GetArrayLength() < 1000) yield break;
            JsonElement last = rows[rows.GetArrayLength() - 1];
            string ts = Literal(last[1].GetString()!), id = Literal(last[2].GetString()!);
            cursor = $" and (ts > {ts} or (ts={ts} and cast(id as varchar) > {id}))";
        }
    }
}
