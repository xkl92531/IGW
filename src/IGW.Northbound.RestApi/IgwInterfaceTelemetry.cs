using System.Diagnostics;
using System.Text;
using IGW.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace IGW.Northbound.RestApi;

public static class IgwInterfaceTelemetry
{
    internal const int MaxCapturedBodyBytes = 8 * 1024 * 1024;

    public static IApplicationBuilder UseIgwInterfaceTelemetry(
        this IApplicationBuilder app,
        ILogger logger,
        Func<string, CancellationToken, Task<string?>>? saveLogPayload = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(logger);
        return app.Use(async (context, next) =>
        {
            if (!ShouldRecord(context.Request.Path))
            {
                await next(context);
                return;
            }

            string path = BoundedPath(context.Request.Path.Value);
            string method = context.Request.Method.Length <= 16 ? context.Request.Method : context.Request.Method[..16];
            long started = Stopwatch.GetTimestamp();
            string status = "success";
            string transportStatus = "completed";
            string errorType = "";
            int? failureStatusCode = null;
            CapturedBody requestBody = CapturedBody.Empty;
            Stream originalResponseBody = context.Response.Body;
            using var responseCapture = new BoundedResponseCaptureStream(originalResponseBody, context.Response, MaxCapturedBodyBytes);
            if (saveLogPayload is not null) context.Response.Body = responseCapture;
            using var activity = GatewayTelemetry.StartActivity("igw.interface.http",
                new Dictionary<string, object?> { ["method"] = method, ["target"] = path }, ActivityKind.Server);
            try
            {
                if (saveLogPayload is not null)
                    requestBody = await CaptureRequestBodyAsync(context.Request, MaxCapturedBodyBytes, context.RequestAborted)
                        .ConfigureAwait(false);
                await next(context);
                if (context.Response.StatusCode >= 400)
                {
                    status = "http_error";
                    activity?.SetStatus(ActivityStatusCode.Error);
                }
            }
            catch (Exception error)
            {
                status = "exception";
                transportStatus = "failed";
                errorType = error.GetType().Name;
                failureStatusCode = StatusCodes.Status500InternalServerError;
                activity?.SetStatus(ActivityStatusCode.Error);
                throw;
            }
            finally
            {
                if (saveLogPayload is not null) context.Response.Body = originalResponseBody;
                double durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                int statusCode = failureStatusCode ?? context.Response.StatusCode;
                string route = context.GetEndpoint() is RouteEndpoint endpoint
                    ? BoundedPath(endpoint.RoutePattern.RawText)
                    : path;
                string? payloadId = null;
                if (saveLogPayload is not null)
                {
                    try
                    {
                        string exchange = BuildExchange(context.Request, requestBody, context.Response,
                            responseCapture, statusCode);
                        payloadId = await saveLogPayload(exchange, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception captureError)
                    {
                        logger.LogWarning("HTTP 接口关联报文保存失败，接口结果仍按实际响应处理：{errorType}",
                            captureError.GetType().Name);
                    }
                }
                activity?.SetTag("igw.http.status_code", statusCode);
                activity?.SetTag("igw.duration_ms", durationMs);
                GatewayTelemetry.Log(logger, status == "success" ? LogLevel.Information : LogLevel.Warning,
                    "interface", "HTTP 接口请求完成", new Dictionary<string, object?>
                    {
                        ["eventKind"] = "rest_request",
                        ["module"] = "northbound",
                        ["feature"] = "rest_api",
                        ["direction"] = "inbound",
                        ["protocol"] = "http",
                        ["method"] = method,
                        ["route"] = route,
                        ["operation"] = method + " " + route,
                        ["interfaceName"] = route,
                        ["target"] = path,
                        ["httpStatusCode"] = statusCode,
                        ["transportStatus"] = transportStatus,
                        ["status"] = status,
                        ["errorType"] = errorType,
                        ["durationMs"] = durationMs,
                        ["payloadId"] = payloadId,
                        ["requestBytes"] = requestBody.TotalBytes,
                        ["responseBytes"] = responseCapture.TotalBytes
                    });
            }
        });
    }

    /// <summary>
    /// 只记 IGW 自己页面调的管理接口。第三方系统调的数据接口(DCS 兼容 /Tag、KEP 兼容 /iotgateway、读写点位、
    /// 批量读值、实时订阅、历史/报警查询、设备在线状态)不记：现场外部系统每秒两百多次 /Tag/ReadTagsAsList，
    /// 一天两千万条日志，日志中心查询超时，任务的接口调用、采集和状态记录都被淹没。任务调用 MES 接口另有日志。
    /// </summary>
    private static bool ShouldRecord(PathString path)
    {
        if (!path.StartsWithSegments("/api")) return false;
        if (IsThirdPartyDataInterface(path)) return false;
        if (path.StartsWithSegments("/api/v1/log-center")) return false;
        if (path.StartsWithSegments("/api/v1/runtime-logs")) return false;
        if (path.Equals("/api/v1/health")) return false;
        return true;
    }

    internal static bool IsThirdPartyDataInterface(PathString path) =>
        path.StartsWithSegments("/Tag") || path.StartsWithSegments("/iotgateway")
        || path.StartsWithSegments("/api/v1/tags") || path.StartsWithSegments("/api/v1/values")
        || path.StartsWithSegments("/api/v1/streams") || path.StartsWithSegments("/api/v1/history")
        || path.StartsWithSegments("/api/v1/alarms") || path.Equals("/api/v1/devices", StringComparison.OrdinalIgnoreCase);

    private static string BoundedPath(string? path)
    {
        string value = string.IsNullOrWhiteSpace(path) ? "/" : path;
        return value.Length <= 512 ? value : value[..512];
    }

    private static async Task<CapturedBody> CaptureRequestBodyAsync(
        HttpRequest request,
        int limit,
        CancellationToken cancellationToken)
    {
        long declaredLength = request.ContentLength ?? 0;
        if (declaredLength == 0 && !request.Headers.ContainsKey("Transfer-Encoding")) return CapturedBody.Empty;
        if (!IsTextContentType(request.ContentType))
            return new CapturedBody("", declaredLength, false, $"（未记录非文本请求正文：{request.ContentType ?? "未知类型"}）");

        request.EnableBuffering();
        long originalPosition = request.Body.CanSeek ? request.Body.Position : 0;
        using var captured = new MemoryStream(Math.Min(limit, declaredLength is > 0 and <= int.MaxValue ? (int)declaredLength : 4096));
        byte[] buffer = new byte[81920];
        long observed = 0;
        bool truncated = declaredLength > limit;
        try
        {
            while (captured.Length < limit)
            {
                int wanted = (int)Math.Min(buffer.Length, limit - captured.Length);
                int read = await request.Body.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                await captured.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                observed += read;
            }
            if (!truncated && captured.Length == limit)
            {
                int extra = await request.Body.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
                if (extra > 0) { observed++; truncated = true; }
            }
        }
        finally
        {
            if (request.Body.CanSeek) request.Body.Position = originalPosition;
        }

        long total = declaredLength > 0 ? declaredLength : observed;
        return new CapturedBody(Encoding.UTF8.GetString(captured.GetBuffer(), 0, (int)captured.Length), total, truncated, null);
    }

    private static string BuildExchange(
        HttpRequest request,
        CapturedBody requestBody,
        HttpResponse response,
        BoundedResponseCaptureStream responseBody,
        int statusCode)
    {
        // 只保存发送内容和返回内容；方法、路径、状态码、字节数已在日志字段里。
        var content = new StringBuilder(requestBody.Text.Length + responseBody.CapturedLength + 256)
            .AppendLine("===== 发送内容 =====")
            .AppendLine(requestBody.Note ?? (requestBody.TotalBytes == 0 ? "（无请求正文）" : requestBody.Text));
        if (requestBody.Truncated)
            content.AppendLine().Append("（请求正文超过 ").Append(MaxCapturedBodyBytes).AppendLine(" 字节，仅保存开头部分）");

        content.AppendLine("===== 返回内容 =====");
        if (!responseBody.CaptureEnabled)
            content.Append("（未记录非文本或实时流响应正文：").Append(response.ContentType ?? "未知类型").AppendLine("）");
        else if (responseBody.TotalBytes == 0)
            content.AppendLine("（无响应正文）");
        else
            content.AppendLine(responseBody.Text);
        if (responseBody.Truncated)
            content.AppendLine().Append("（响应正文超过 ").Append(MaxCapturedBodyBytes).AppendLine(" 字节，仅保存开头部分；客户端仍收到完整响应）");
        return content.ToString();
    }

    private static bool IsTextContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return true;
        string mediaType = contentType.Split(';', 2)[0].Trim();
        return mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+xml", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/xml", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record CapturedBody(string Text, long TotalBytes, bool Truncated, string? Note)
    {
        public static readonly CapturedBody Empty = new("", 0, false, null);
    }

    private sealed class BoundedResponseCaptureStream(Stream inner, HttpResponse response, int limit) : Stream
    {
        private readonly MemoryStream _capture = new(Math.Min(limit, 4096));
        private bool? _captureEnabled;

        public long TotalBytes { get; private set; }
        public int CapturedLength => (int)_capture.Length;
        public bool CaptureEnabled => _captureEnabled ?? IsTextResponse(response.ContentType);
        public bool Truncated => CaptureEnabled && TotalBytes > _capture.Length;
        public string Text => Encoding.UTF8.GetString(_capture.GetBuffer(), 0, (int)_capture.Length);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            Capture(buffer.AsSpan(offset, count));
            inner.Write(buffer, offset, count);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Capture(buffer.Span);
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _capture.Dispose();
            base.Dispose(disposing);
        }

        private void Capture(ReadOnlySpan<byte> bytes)
        {
            TotalBytes += bytes.Length;
            _captureEnabled ??= IsTextResponse(response.ContentType);
            if (!_captureEnabled.Value || _capture.Length >= limit) return;
            int count = (int)Math.Min(bytes.Length, limit - _capture.Length);
            _capture.Write(bytes[..count]);
        }

        private static bool IsTextResponse(string? contentType) =>
            IsTextContentType(contentType)
            && !(contentType?.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase) ?? false);
    }
}
