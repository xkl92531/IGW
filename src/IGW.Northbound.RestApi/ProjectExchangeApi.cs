using IGW.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace IGW.Northbound.RestApi;

public static class ProjectExchangeApi
{
    /// <summary>工程 JSON 上传上限；Program.cs 同步放开 Kestrel/multipart 限制，两者必须一致。</summary>
    public const long MaximumUploadBytes = 4L * 1024 * 1024 * 1024;

    public static void MapProjectExchangeApi(
        this IEndpointRouteBuilder app,
        IProjectExchangeService exchange,
        Func<string> projectName)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        ArgumentNullException.ThrowIfNull(projectName);

        app.MapPost("/api/v1/project/import/inspect", async (HttpRequest request, CancellationToken ct) =>
        {
            try
            {
                LiftRequestBodyLimit(request);
                IFormFile file = await ReadFileAsync(request, ct).ConfigureAwait(false);
                await using Stream input = file.OpenReadStream();
                ProjectImportPreview preview = await exchange.InspectAsync(input, ct).ConfigureAwait(false);
                return Results.Ok(preview);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) { return ImportError(error); }
        });

        app.MapPost("/api/v1/project/import/commit", async (HttpRequest request, CancellationToken ct) =>
        {
            try
            {
                LiftRequestBodyLimit(request);
                IFormCollection form = await ReadFormAsync(request, ct).ConfigureAwait(false);
                IFormFile file = RequireFile(form);
                string token = RequireField(form, "token");
                string fileHash = RequireField(form, "fileHash");
                string? password = OptionalField(form, "migrationPassword");
                await using Stream input = file.OpenReadStream();
                ProjectImportResult result = await exchange.ImportAsync(
                    input, new ProjectImportOptions(token, fileHash, password), ct).ConfigureAwait(false);
                return result.Applied
                    ? Results.Ok(result)
                    : Results.Json(result, statusCode: FailureStatus(result.ErrorCode));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) { return ImportError(error); }
        });

        app.MapGet("/api/v1/project/export", async (HttpContext context, bool? includePortableSecrets) =>
        {
            string? password = context.Request.Headers["X-Igw-Migration-Password"].FirstOrDefault();
            await StreamExportAsync(context, exchange, projectName(), includePortableSecrets == true, password)
                .ConfigureAwait(false);
        }).ExcludeFromDescription();

        // 浏览器原生 form 下载允许携带迁移密码，响应仍由服务端直写，前端不会把大型 JSON 缓冲成 Blob。
        app.MapPost("/api/v1/project/export", async (HttpContext context) =>
        {
            IFormCollection form;
            try { form = await ReadFormAsync(context.Request, context.RequestAborted).ConfigureAwait(false); }
            catch (Exception error)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(
                    new { error = ErrorCode(error), detail = error.Message }, context.RequestAborted).ConfigureAwait(false);
                return;
            }
            bool includeSecrets = bool.TryParse(OptionalField(form, "includePortableSecrets"), out bool parsed) && parsed;
            await StreamExportAsync(
                context, exchange, projectName(), includeSecrets, OptionalField(form, "migrationPassword"))
                .ConfigureAwait(false);
        }).ExcludeFromDescription();
    }

    private static async Task StreamExportAsync(
        HttpContext context,
        IProjectExchangeService exchange,
        string projectName,
        bool includePortableSecrets,
        string? password)
    {
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.ContentDisposition =
            $"attachment; filename*=UTF-8''{Uri.EscapeDataString(SafeFileName(projectName) + ".json")}";
        try
        {
            await using var output = new AsyncCompatibleWriteStream(context.Response.Body);
            await exchange.ExportAsync(
                output,
                new ProjectExportOptions(includePortableSecrets, password),
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
        catch (ArgumentException error) when (!context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = ErrorCode(error) }, context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (!context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(
                new { error = "project_export_failed", detail = error.Message }, context.RequestAborted)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Kestrel 默认请求体上限 30MB，会先于本接口的 4GB 校验拒绝大工程；必须在读取 body 前抬高。
    /// (.NET 10 移除了 DisableRequestBodySizeLimit 扩展，改用特性接口直设。)
    /// 不是每个宿主都提供这个特性(如测试用的 TestServer)，开始读取 body 之后它也会变成只读；
    /// 两种情况都不能让导入接口直接抛异常。
    /// </summary>
    private static void LiftRequestBodyLimit(HttpRequest request)
    {
        if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = MaximumUploadBytes;
    }

    private static async Task<IFormFile> ReadFileAsync(HttpRequest request, CancellationToken ct) =>
        RequireFile(await ReadFormAsync(request, ct).ConfigureAwait(false));

    private static async Task<IFormCollection> ReadFormAsync(HttpRequest request, CancellationToken ct)
    {
        if (!request.HasFormContentType)
            throw new ProjectExchangeRequestException("multipart_form_required", "请求必须使用 multipart/form-data。");
        if (request.ContentLength > MaximumUploadBytes)
            throw new ProjectExchangeRequestException("project_import_file_too_large", "工程 JSON 超过上传上限。");
        return await request.ReadFormAsync(ct).ConfigureAwait(false);
    }

    private static IFormFile RequireFile(IFormCollection form)
    {
        IFormFile? file = form.Files.GetFile("file");
        if (file is null || file.Length == 0)
            throw new ProjectExchangeRequestException("project_file_required", "缺少工程 JSON 文件。");
        if (file.Length > MaximumUploadBytes)
            throw new ProjectExchangeRequestException("project_import_file_too_large", "工程 JSON 超过上传上限。");
        return file;
    }

    private static string RequireField(IFormCollection form, string name)
    {
        string? value = OptionalField(form, name);
        if (string.IsNullOrWhiteSpace(value))
            throw new ProjectExchangeRequestException($"{name}_required", $"缺少 {name}。");
        return value;
    }

    private static string? OptionalField(IFormCollection form, string name) =>
        form.TryGetValue(name, out Microsoft.Extensions.Primitives.StringValues value)
            ? value.FirstOrDefault() : null;

    private static IResult ImportError(Exception error) => error switch
    {
        ProjectExchangeRequestException request => Results.BadRequest(new { error = request.Code, detail = request.Message }),
        InvalidDataException invalid => Results.BadRequest(new { error = ErrorCode(invalid), detail = invalid.Message }),
        System.Text.Json.JsonException json => Results.BadRequest(new { error = "project_json_invalid", detail = json.Message }),
        _ => Results.Json(new { error = "project_import_failed", detail = error.Message }, statusCode: 500),
    };

    private static int FailureStatus(string? code) => code switch
    {
        "revision_conflict" => StatusCodes.Status409Conflict,
        "import_session_not_found" => StatusCodes.Status410Gone,
        "import_file_changed" => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status400BadRequest,
    };

    private static string ErrorCode(Exception error)
    {
        if (error.Message.Contains("migration_password_required", StringComparison.Ordinal))
            return "migration_password_required";
        int separator = error.Message.IndexOfAny([':', ' ', '\r', '\n']);
        return separator > 0 ? error.Message[..separator] : error.Message;
    }

    private static string SafeFileName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\']).Distinct().ToArray();
        string safe = new(value.Trim().Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "IGW-project" : safe;
    }

    private sealed class ProjectExchangeRequestException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }

    /// <summary>Utf8JsonWriter 在 Dispose 时会同步 Flush；把同步写转换为底层异步写，兼容 Kestrel 禁用同步 I/O。</summary>
    private sealed class AsyncCompatibleWriteStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.FlushAsync().GetAwaiter().GetResult();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) =>
            inner.WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            byte[] copy = buffer.ToArray();
            inner.WriteAsync(copy).AsTask().GetAwaiter().GetResult();
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(buffer, cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { }
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
