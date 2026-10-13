using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IGW.Observability;

public static class LogCenterApi
{
    public static void MapLogCenterApi(this IEndpointRouteBuilder endpoints, LogCenterBackend backend,
        Func<IEnumerable<LogTaskBinding>>? projectTasks = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(backend);

        endpoints.MapGet("/api/v1/log-center/logs", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                LogCenterRequest request = ParseRequest(context.Request.Query, projectTasks);
                return Results.Ok(await backend.QueryAsync(request, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception error) { return QueryError(error); }
        });

        // 按当前筛选条件打包导出：文本日志 + 相关接口报文 + 清单，便于从现场发给其他人分析。
        endpoints.MapGet("/api/v1/log-center/export", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                LogCenterRequest request = ParseRequest(context.Request.Query, projectTasks);
                bool includePayloads = !string.Equals(context.Request.Query["payloads"], "false", StringComparison.OrdinalIgnoreCase);
                string format = context.Request.Query["format"].ToString();
                if (string.IsNullOrWhiteSpace(format)) format = backend.Database is null ? "text" : "jsonl";
                format = LogCenterExport.ValidateFormat(format);
                var (archive, summary) = await LogCenterExport.CreateAsync(
                    request,
                    backend.StreamAsync(request, cancellationToken),
                    backend.Payloads,
                    new LogExportOptions(includePayloads, Format: format, StorageEngine: backend.Database is null ? "segments" : "questdb"),
                    cancellationToken).ConfigureAwait(false);
                context.Response.Headers["X-Igw-Log-Entries"] = summary.Entries.ToString(CultureInfo.InvariantCulture);
                context.Response.Headers["X-Igw-Log-Truncated"] = summary.Truncated ? "true" : "false";
                context.Response.Headers.CacheControl = "no-store";
                string machine = new(Environment.MachineName.Where(char.IsAsciiLetterOrDigit).ToArray());
                string fileName = string.Create(CultureInfo.InvariantCulture,
                    $"igw-logs-{(machine.Length == 0 ? "gateway" : machine)}-{format}-{request.From.ToLocalTime():yyyyMMdd-HHmm}-{request.To.ToLocalTime():yyyyMMdd-HHmm}-{DateTimeOffset.UtcNow:HHmmssfff}.zip");
                return Results.Stream(archive, "application/zip", fileName);
            }
            catch (Exception error) { return QueryError(error); }
        });

        // 一键清理：GET 列出可清理的残留和进度；POST 开始清理，立即返回，删除在网关后台进行(不停服务)。
        // scan=true：打开清理窗口时在各本地硬盘上查找网关以前留下的日志(后台进行，结果随状态返回)
        endpoints.MapGet("/api/v1/log-center/cleanup", (bool? scan) =>
        {
            if (scan == true) backend.Cleanup.ScanOtherLocations();
            return Results.Ok(backend.Cleanup.Status);
        });
        endpoints.MapPost("/api/v1/log-center/cleanup", (LogCleanupRequest? request) => backend.Cleanup.Start(request?.Paths)
            ? Results.Accepted("/api/v1/log-center/cleanup", backend.Cleanup.Status)
            : Results.Conflict(backend.Cleanup.Status));

        endpoints.MapGet("/api/v1/log-center/status", async (CancellationToken cancellationToken) =>
            Results.Ok(await backend.StatusAsync(cancellationToken).ConfigureAwait(false)));

        endpoints.MapPut("/api/v1/log-center/settings/retention", async (
            RetentionRequest request,
            CancellationToken cancellationToken) =>
        {
            try
            {
                ObservabilitySettings saved = await backend.UpdateRetentionAsync(
                    request.RetentionDays, cancellationToken).ConfigureAwait(false);
                return Results.Ok(new { saved.RetentionDays, restartPending = false });
            }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
            {
                return Results.BadRequest(new { error = error.Message });
            }
        });

        endpoints.MapPost("/api/v1/log-center/settings/storage", async (
            StorageRequest request,
            CancellationToken cancellationToken) =>
        {
            try
            {
                LogStorageChange change = await backend.BeginStorageChangeAsync(
                    request.StorageDirectory, cancellationToken).ConfigureAwait(false);
                return Results.Ok(new
                {
                    status = change.Status,
                    change.SourceDirectory,
                    change.TargetDirectory,
                    change.TotalBytes,
                    change.CopiedBytes
                });
            }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = error.Message });
            }
        });

        endpoints.MapGet("/api/v1/log-center/payloads/{id}", async (
            string id,
            int? page,
            CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(await backend.Payloads.ReadPageAsync(id, page ?? 0, cancellationToken).ConfigureAwait(false)); }
            catch (FileNotFoundException) { return Results.NotFound(new { error = "日志载荷不存在或已过期。" }); }
            catch (Exception error) when (error is ArgumentException or InvalidDataException)
            {
                return Results.BadRequest(new { error = error.Message });
            }
        });

        // 只返回“发送内容”和“返回内容”，每部分最多 1M 字符；更长的请下载完整报文。
        endpoints.MapGet("/api/v1/log-center/payloads/{id}/exchange", async (
            string id,
            CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(await backend.Payloads.ReadExchangeAsync(id, 1_000_000, cancellationToken).ConfigureAwait(false)); }
            catch (FileNotFoundException) { return Results.NotFound(new { error = "日志载荷不存在或已过期。" }); }
            catch (Exception error) when (error is ArgumentException or InvalidDataException)
            {
                return Results.BadRequest(new { error = error.Message });
            }
        });

        endpoints.MapGet("/api/v1/log-center/payloads/{id}/download", async (
            string id,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var payload = await backend.Payloads.OpenReadAsync(id, cancellationToken).ConfigureAwait(false);
                return Results.Stream(payload.Stream, payload.ContentType, payload.FileName, enableRangeProcessing: true);
            }
            catch (FileNotFoundException) { return Results.NotFound(new { error = "日志载荷不存在或已过期。" }); }
            catch (Exception error) when (error is ArgumentException or InvalidDataException)
            {
                return Results.BadRequest(new { error = error.Message });
            }
        });

        MapCompatibilityEndpoints(endpoints, backend);
    }

    private static void MapCompatibilityEndpoints(IEndpointRouteBuilder endpoints, LogCenterBackend backend)
    {
        endpoints.MapGet("/api/v1/runtime-logs", async (
            string? level,
            string? search,
            DateOnly? date,
            int? limit,
            CancellationToken cancellationToken) =>
        {
            try
            {
                DateTimeOffset from;
                DateTimeOffset to;
                if (date is { } selected)
                {
                    DateTime local = selected.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
                    TimeSpan offset = TimeZoneInfo.Local.GetUtcOffset(local);
                    from = new DateTimeOffset(local, offset);
                    to = from.AddDays(1);
                }
                else
                {
                    to = DateTimeOffset.UtcNow;
                    from = to.AddHours(-24);
                }
                int size = Math.Clamp(limit ?? 300, 1, 200);
                LogCenterResponse response = await backend.QueryAsync(new LogCenterRequest
                {
                    From = from,
                    To = to,
                    Level = level,
                    Search = search,
                    PageSize = size
                }, cancellationToken).ConfigureAwait(false);
                ObservabilitySettings settings = backend.Settings;
                return Results.Ok(new
                {
                    settings.RetentionDays,
                    items = response.Items.Select(item => new
                    {
                        item.Timestamp,
                        level = LegacyLevel(item.Level),
                        category = item.Category,
                        message = item.Message
                    }),
                    warnings = Array.Empty<string>(),
                    settings.StorageDirectory,
                    defaultDirectory = backend.DefaultStorageDirectory
                });
            }
            catch (Exception error) { return QueryError(error); }
        });

        endpoints.MapPut("/api/v1/runtime-logs/settings", async (
            LegacySettingsRequest request,
            CancellationToken cancellationToken) =>
        {
            try
            {
                ObservabilitySettings settings = await backend.UpdateRetentionAsync(
                    request.RetentionDays, cancellationToken).ConfigureAwait(false);
                if (request.StorageDirectory is { } directory)
                {
                    string selected = string.IsNullOrWhiteSpace(directory)
                        ? backend.DefaultStorageDirectory
                        : directory;
                    if (!SamePath(selected, settings.StorageDirectory))
                        await backend.BeginStorageChangeAsync(selected, cancellationToken).ConfigureAwait(false);
                }
                return Results.Ok(new
                {
                    settings.RetentionDays,
                    settings.StorageDirectory,
                    defaultDirectory = backend.DefaultStorageDirectory
                });
            }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = error.Message });
            }
        });

        // 空路径 = 磁盘列表(前端“磁盘列表”按钮)。打开对话框时前端会显式传入当前存储目录。
        endpoints.MapGet("/api/v1/runtime-logs/directories", (string? path, CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(GatewayDirectoryBrowser.List(path, cancellationToken));
            }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return Results.BadRequest(new { error = $"无法浏览网关目录：{error.Message}" });
            }
        });
    }

    private static LogCenterRequest ParseRequest(IQueryCollection query, Func<IEnumerable<LogTaskBinding>>? projectTasks)
    {
        if (!DateTimeOffset.TryParse(query["from"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset from))
            throw new ArgumentException("from 必须是带时区的 ISO 8601 时间。");
        if (!DateTimeOffset.TryParse(query["to"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset to))
            throw new ArgumentException("to 必须是带时区的 ISO 8601 时间。");
        string? path = Text(query, "path"), term = Text(query, "taskOrInterface");
        // 两个条件共享同一份当前工程快照，但各自解析关联集合，随后仍独立 AND 过滤。
        LogTaskBinding[] bindings = (path is not null || term is not null) && projectTasks is not null
            ? projectTasks().ToArray() : [];
        return new LogCenterRequest
        {
            Category = Text(query, "category") ?? "all",
            From = from,
            To = to,
            Protocol = Text(query, "protocol"),
            Channel = Text(query, "channel"),
            Device = Text(query, "device"),
            Database = Text(query, "database"),
            TaskId = Text(query, "taskId"),
            Module = Text(query, "module"),
            Feature = Text(query, "feature"),
            Task = Text(query, "task"),
            Interface = Text(query, "interface"),
            TaskOrInterface = term,
            TaskOrInterfaceTaskIds = ResolveTasks(term, bindings, partial: true),
            Level = Text(query, "level"),
            Search = Text(query, "search"),
            TraceId = Text(query, "traceId"),
            Path = path,
            PathTaskIds = ResolveTasks(path, bindings, partial: false),
            Page = Integer(query, "page", 1),
            PageSize = Integer(query, "pageSize", 50)
        };
    }

    private static IReadOnlyList<string> ResolveTasks(string? text, IEnumerable<LogTaskBinding> bindings, bool partial)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        string selected = text.Trim();
        // 右键位置按完整路径段取自身及下级；自由输入按组名/组路径片段匹配。
        return bindings.Where(binding => !string.IsNullOrWhiteSpace(binding.Path)
                && (partial ? binding.Path.Contains(selected, StringComparison.Ordinal)
                    : LogFilter.IsPathOrDescendant(binding.Path.Trim(), selected)))
            .Select(binding => binding.TaskId).Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string? Text(IQueryCollection query, string name) =>
        query.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.ToString() : null;

    private static int Integer(IQueryCollection query, string name, int fallback) =>
        query.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                ? parsed
                : throw new ArgumentException($"{name} 必须是整数。")
            : fallback;

    private static IResult QueryError(Exception error) => error switch
    {
        ArgumentException => Results.BadRequest(new { error = error.Message }),
        // 浏览器放弃了请求(超时、换了查询条件)：正常情况，不当错误记录；客户端已断开，499 只是给访问日志看的
        OperationCanceledException => Results.Json(new { error = "日志查询已取消。" }, statusCode: 499),
        _ => Results.Json(new { error = "日志查询失败：" + error.Message }, statusCode: StatusCodes.Status500InternalServerError)
    };

    private static string LegacyLevel(string level) => level.ToUpperInvariant() switch
    {
        "TRACE" => "TRC", "DEBUG" => "DBG", "INFO" or "INFORMATION" => "INF",
        "WARN" or "WARNING" => "WRN", "ERROR" => "ERR", "FATAL" or "CRITICAL" => "CRT", _ => level
    };

    private static bool SamePath(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public sealed record RetentionRequest(int RetentionDays);
    public sealed record StorageRequest(string StorageDirectory);
    public sealed record LegacySettingsRequest(int RetentionDays, string? StorageDirectory = null);
}
