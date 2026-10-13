using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace IGW.Northbound.RestApi;

/// <summary>点位批量注册：一次请求写一台设备，行级批量落库，只重建该设备。</summary>
public static class ProjectBulkTagApi
{
    /// <summary>
    /// 请求体上限 256 MB：与 <see cref="ProjectBulkTagValidator.MaxItems"/>（20 万点）匹配——
    /// 一个带组路径/描述/元数据的典型点位 JSON 约 0.3～1 KB，20 万点约 60～200 MB。
    /// </summary>
    public const long MaximumRequestBytes = 256L * 1024 * 1024;

    public static void MapProjectBulkTagApi(this IEndpointRouteBuilder app, IGatewayHost host)
    {
        app.MapPost("/api/v1/project/bulk/tags", async (HttpRequest request, HttpResponse response, CancellationToken ct) =>
        {
            if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = MaximumRequestBytes;

            string? expected = request.Headers.IfMatch.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(expected) && !ProjectRevision.TryParse(expected, out _))
                return Results.BadRequest(new { error = "invalid_if_match" });

            ProjectBulkTagRequest? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<ProjectBulkTagRequest>(request.Body, ProjectStore.Options, ct)
                    .ConfigureAwait(false);
            }
            catch (JsonException ex) { return Results.BadRequest(new { error = "invalid_request", detail = ex.Message }); }
            if (body is null) return Results.BadRequest(new { error = "invalid_request", detail = "请求体为空" });

            string? tooMany = ProjectBulkTagValidator.CheckLimits(body);
            if (tooMany is not null)
                return Results.Json(new { error = "too_many_items", detail = tooMany }, statusCode: StatusCodes.Status413PayloadTooLarge);

            var errors = ProjectBulkTagValidator.Validate(body);
            if (errors.Count > 0)
                return Results.BadRequest(new { error = "invalid_items", details = errors });

            ProjectBulkTagResponse result;
            try
            {
                result = await host.ApplyBulkTagsAsync(body, string.IsNullOrWhiteSpace(expected) ? null : expected, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { return Results.Json(new { error = "bulk_failed", detail = ex.Message }, statusCode: 500); }

            response.Headers.ETag = result.Revision;
            if (result.Conflict)
                return Results.Json(new { error = "revision_conflict", revision = result.Revision }, statusCode: 409);
            if (result.ErrorCode == "runtime_apply_failed")
                return Results.Json(new { error = result.ErrorCode, detail = result.Error, revision = result.Revision }, statusCode: 500);
            if (!result.Applied && result.ErrorCode == "too_many_items")
                return Results.Json(new { error = result.ErrorCode, detail = result.Error }, statusCode: StatusCodes.Status413PayloadTooLarge);
            // 外部主数据库断开：与其它配置保存一样暂停写入，恢复连接后可重试
            if (!result.Applied && result.ErrorCode == "database_unavailable")
                return Results.Json(new { error = result.ErrorCode, detail = result.Error },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            // 服务端存储故障：SQLite 忙/锁/IO/磁盘满等可重试 → 503；库内文档损坏等 → 500。都不是请求内容的问题
            if (!result.Applied && result.ErrorCode == "persistence_failed")
                return Results.Json(new { error = result.ErrorCode, detail = result.Error },
                    statusCode: result.Retryable ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status500InternalServerError);
            if (!result.Applied)
                return Results.BadRequest(new { error = result.ErrorCode ?? "bulk_rejected", detail = result.Error });
            return Results.Ok(new
            {
                applied = true, revision = result.Revision, channelId = result.ChannelId, deviceId = result.DeviceId,
                created = result.Created, updated = result.Updated, deleted = result.Deleted,
                deviceRestarted = result.DeviceRestarted, timing = result.Timing,
            });
        });
    }
}
