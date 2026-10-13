using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IGW.Northbound.RestApi;

public static class DcsTagCompatibilityApi
{
    public static void MapDcsTagCompatibilityApi(this IEndpointRouteBuilder app, IGatewayHost host) =>
        app.MapDcsTagCompatibilityApi(new GatewayDcsTagRuntime(host));

    internal static void MapDcsTagCompatibilityApi(this IEndpointRouteBuilder app, IDcsTagRuntime runtime)
    {
        // 后台别名解析器重建失败需要能被日志观测到：从 DI 里取 ILoggerFactory 建一个专属 category 的 logger；
        // 取不到（如某些测试宿主未注册日志服务）时传 null，DcsTagOperations 内部会退回 Console.Error 兜底。
        var logger = app.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger<DcsTagOperations>();
        var operations = new DcsTagOperations(runtime, logger);
        var group = app.MapGroup("/Tag").WithTags(PublicPlcApiPolicy.OperationTag);

        group.MapGet("/ReadConditionCache", (string? groupName) =>
                ToResult(operations.ReadConditionCache(groupName)))
            .WithName("DcsReadConditionCache")
            .WithSummary("读取 DCS 条件缓存")
            .Produces<DcsResponse<object?>>(StatusCodes.Status200OK);

        group.MapPost("/ReadTag", async (string? tag, HttpRequest request, CancellationToken ct) =>
            {
                await operations.PrepareReadAsync([tag], null, ct, Activate(request));
                return ToResult(operations.ReadOne(tag, null, Activate(request)));
            })
            .WithName("DcsReadTag")
            .WithSummary("按查询参数读取一个 PLC 点位")
            .Produces<DcsResponse<object?>>(StatusCodes.Status200OK)
            .Produces<DcsResponse<object?>>(StatusCodes.Status400BadRequest);

        group.MapPost("/ReadTag1", async (HttpRequest request, CancellationToken ct) =>
            {
                var body = await ReadBodyAsync<DcsReadTagRequest>(request, ct);
                if (body.Error is not null) return body.Error;
                await operations.PrepareReadAsync([body.Value!.Tag], null, ct, Activate(request));
                return ToResult(operations.ReadOne(body.Value!.Tag, null, Activate(request)));
            })
            .WithName("DcsReadTag1")
            .WithSummary("按 JSON 请求读取一个 PLC 点位")
            .Accepts<DcsReadTagRequest>("application/json")
            .Produces<DcsResponse<object?>>(StatusCodes.Status200OK)
            .Produces<DcsResponse<object?>>(StatusCodes.Status400BadRequest);

        group.MapPost("/ReadTags", async (HttpRequest request, CancellationToken ct) =>
            {
                var body = await ReadBodyAsync<List<string>>(request, ct);
                if (body.Error is not null) return body.Error;
                await operations.PrepareReadAsync(body.Value, null, ct, Activate(request));
                return ToResult(operations.ReadAsJsonStrings(body.Value, null, Activate(request)));
            })
            .WithName("DcsReadTags")
            .WithSummary("批量读取并返回 JSON 字符串列表")
            .Accepts<List<string>>("application/json")
            .Produces<DcsResponse<List<string>>>(StatusCodes.Status200OK)
            .Produces<DcsResponse<List<string>>>(StatusCodes.Status400BadRequest);

        group.MapPost("/ReadTagsAsList", async (HttpRequest request, CancellationToken ct) =>
            {
                var body = await ReadBodyAsync<List<string>>(request, ct);
                if (body.Error is not null) return body.Error;
                await operations.PrepareReadAsync(body.Value, null, ct, Activate(request));
                return ToResult(operations.ReadAsList(body.Value, null, Activate(request)));
            })
            .WithName("DcsReadTagsAsList")
            .WithSummary("批量读取并返回键值列表")
            .Accepts<List<string>>("application/json")
            .Produces<DcsResponse<List<DcsKeyValue<object?>>>>(StatusCodes.Status200OK)
            .Produces<DcsResponse<List<DcsKeyValue<object?>>>>(StatusCodes.Status400BadRequest);

        group.MapPost("/ReadTagsWithQuality", async (HttpRequest request, CancellationToken ct) =>
            {
                var body = await ReadBodyAsync<List<string>>(request, ct);
                if (body.Error is not null) return body.Error;
                await operations.PrepareReadAsync(body.Value, null, ct, Activate(request));
                return ToResult(operations.ReadWithQuality(body.Value, null, Activate(request)));
            })
            .WithName("DcsReadTagsWithQuality")
            .WithSummary("批量读取并逐点返回值、质量与原因(单个坏点不影响其它点)")
            .Accepts<List<string>>("application/json")
            .Produces<DcsResponse<List<DcsTagQuality>>>(StatusCodes.Status200OK)
            .Produces<DcsResponse<List<DcsTagQuality>>>(StatusCodes.Status400BadRequest)
            .Produces<DcsResponse<List<DcsTagQuality>>>(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/ReadTagsAsDictionary", async (HttpRequest request, CancellationToken ct) =>
            {
                var body = await ReadBodyAsync<List<string>>(request, ct);
                if (body.Error is not null) return body.Error;
                await operations.PrepareReadAsync(body.Value, null, ct, Activate(request));
                return ToResult(operations.ReadAsDictionary(body.Value, null, Activate(request)));
            })
            .WithName("DcsReadTagsAsDictionary")
            .WithSummary("批量读取并返回字典")
            .Accepts<List<string>>("application/json")
            .Produces<DcsResponse<Dictionary<string, object?>>>(StatusCodes.Status200OK)
            .Produces<DcsResponse<Dictionary<string, object?>>>(StatusCodes.Status400BadRequest);

        group.MapPost("/WriteTag", async (HttpRequest request, CancellationToken ct) =>
            {
                if (!string.IsNullOrWhiteSpace(request.Headers[PublicPlcApiPolicy.ProjectRevisionHeader].FirstOrDefault())
                    && PublicPlcApiPolicy.RejectStaleProjectWrite(request, JsonHash.Of(runtime.Project)) is { } stale)
                    return stale;
                var body = await ReadBodyAsync<DcsWriteTagRequest>(request, ct);
                return body.Error ?? ToResult(await operations.WriteOneAsync(body.Value!.Tag, null, ct));
            })
            .WithName("DcsWriteTag")
            .WithSummary("写入一个 PLC 点位")
            .Accepts<DcsWriteTagRequest>("application/json")
            .Produces<DcsResponse<bool>>(StatusCodes.Status200OK)
            .Produces<DcsResponse<bool>>(StatusCodes.Status400BadRequest);

        group.MapPost("/WriteTags", async (HttpRequest request, CancellationToken ct) =>
            {
                if (!string.IsNullOrWhiteSpace(request.Headers[PublicPlcApiPolicy.ProjectRevisionHeader].FirstOrDefault())
                    && PublicPlcApiPolicy.RejectStaleProjectWrite(request, JsonHash.Of(runtime.Project)) is { } stale)
                    return stale;
                var body = await ReadBodyAsync<DcsWriteTagsRequest>(request, ct);
                return body.Error ?? ToResult(await operations.WriteManyAsync(body.Value!.Tags, null, ct));
            })
            .WithName("DcsWriteTags")
            .WithSummary("批量写入 PLC 点位")
            .Accepts<DcsWriteTagsRequest>("application/json")
            .Produces<DcsResponse<bool>>(StatusCodes.Status200OK)
            .Produces<DcsResponse<bool>>(StatusCodes.Status400BadRequest);

        group.MapPost("/GetTagValueTypeByTagName", async (HttpRequest request, CancellationToken ct) =>
            {
                var body = await ReadBodyAsync<List<string>>(request, ct);
                if (body.Error is not null) return body.Error;
                DcsApiOutcome<Dictionary<string, string>> outcome = operations.GetTypes(body.Value, null);
                return outcome.Body.IsSuccess
                    ? Results.Json(outcome.Body.BackResult, DcsJson.Options, statusCode: outcome.StatusCode)
                    : ToResult(outcome);
            })
            .WithName("DcsGetTagValueTypeByTagName")
            .WithSummary("获取 PLC 点位的数据类型")
            .Accepts<List<string>>("application/json")
            .Produces<Dictionary<string, string>>(StatusCodes.Status200OK)
            .Produces<DcsResponse<Dictionary<string, string>>>(StatusCodes.Status400BadRequest);

        group.MapPost("/CheckTagValueValidity", async (HttpRequest request, CancellationToken ct) =>
            {
                var body = await ReadBodyAsync<DcsWriteTagsRequest>(request, ct);
                return body.Error ?? ToResult(operations.CheckValidity(body.Value!.Tags, null));
            })
            .WithName("DcsCheckTagValueValidity")
            .WithSummary("校验 PLC 点位写入值但不执行写入")
            .Accepts<DcsWriteTagsRequest>("application/json")
            .Produces<DcsResponse<bool>>(StatusCodes.Status200OK)
            .Produces<DcsResponse<bool>>(StatusCodes.Status400BadRequest);

        group.MapPost("/AddTags", async (HttpRequest request, CancellationToken ct) =>
            {
                var body = await ReadBodyAsync<List<DcsTagUpdateModel>>(request, ct);
                return body.Error ?? ToResult(await operations.AddTagsAsync(body.Value, null, ct));
            })
            .WithName("DcsAddTags")
            .WithSummary("按 DCS 模型添加或更新 PLC 点位")
            .Accepts<List<DcsTagUpdateModel>>("application/json")
            .Produces<DcsResponse<string>>(StatusCodes.Status200OK)
            .Produces<DcsResponse<string>>(StatusCodes.Status400BadRequest);
    }

    private static IResult ToResult<T>(DcsApiOutcome<T> outcome) =>
        Results.Json(outcome.Body, DcsJson.Options, statusCode: outcome.StatusCode);

    private static async Task<(T? Value, IResult? Error)> ReadBodyAsync<T>(
        HttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            T? value = await JsonSerializer.DeserializeAsync<T>(request.Body, DcsJson.Options, cancellationToken);
            return value is null
                ? (default, BadBody("请求 JSON 不能为空"))
                : (value, null);
        }
        catch (JsonException)
        {
            return (default, BadBody("请求 JSON 无效"));
        }
        catch (NotSupportedException)
        {
            return (default, BadBody("请求 JSON 类型不受支持"));
        }
    }

    private static IResult BadBody(string message) =>
        Results.Json(
            DcsResponse<object?>.Failure(message, StatusCodes.Status400BadRequest),
            DcsJson.Options,
            statusCode: StatusCodes.Status400BadRequest);

    /// <summary>
    /// 读取是否顺带把点位拉进活动窗口。默认不拉：诊断类调用一次读上千个点、
    /// 读完就走，没必要让它们被轮询 activeWindowSec。持续盯屏的调用方传 ?activate=true。
    /// </summary>
    private static bool Activate(HttpRequest request) =>
        request.Query.TryGetValue("activate", out var raw) && bool.TryParse(raw, out bool on) && on;
}
