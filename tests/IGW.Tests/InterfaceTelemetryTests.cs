using IGW.Northbound.RestApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace IGW.Tests;

public sealed class InterfaceTelemetryTests
{
    [Fact]
    public async Task Rest_middleware_records_one_bounded_summary_and_excludes_log_center_queries()
    {
        var logger = new TelemetryTestLogger();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();
        app.UseIgwInterfaceTelemetry(logger);
        app.MapGet("/api/v1/devices/{id}", (string id) => Results.Ok(new { id }));
        app.MapGet("/api/v1/log-center/status", () => Results.Ok(new { ok = true }));
        await app.StartAsync();

        using HttpClient client = app.GetTestClient();
        using HttpResponseMessage response = await client.GetAsync("/api/v1/devices/PLC8?token=must-not-be-logged");
        using HttpResponseMessage excluded = await client.GetAsync("/api/v1/log-center/status");

        Assert.True(response.IsSuccessStatusCode);
        Assert.True(excluded.IsSuccessStatusCode);
        IReadOnlyDictionary<string, object?> item = Assert.Single(logger.ByCategory("interface"));
        Assert.Equal("northbound", item["igw_module"]);
        Assert.Equal("rest_api", item["igw_feature"]);
        Assert.Equal("rest_request", item["igw_event_kind"]);
        Assert.Equal("inbound", item["igw_direction"]);
        Assert.Equal("http", item["igw_protocol"]);
        Assert.Equal("GET", item["igw_method"]);
        Assert.Equal("GET /api/v1/devices/{id}", item["igw_operation"]);
        Assert.Equal("/api/v1/devices/{id}", item["igw_route"]);
        Assert.Equal("/api/v1/devices/PLC8", item["igw_target"]);
        Assert.Equal(200, item["igw_http_status_code"]);
        Assert.Equal("success", item["igw_status"]);
        Assert.True(Convert.ToDouble(item["igw_duration_ms"]) >= 0);
        Assert.DoesNotContain("must-not-be-logged", string.Join('|', item.Values));
    }

    [Fact]
    public async Task Rest_middleware_records_thrown_endpoints_as_failed_500_transport()
    {
        var logger = new TelemetryTestLogger();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();
        app.UseIgwInterfaceTelemetry(logger);
        app.MapGet("/api/v1/boom", (HttpContext _) => throw new InvalidOperationException("boom"));
        await app.StartAsync();

        using HttpClient client = app.GetTestClient();
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("/api/v1/boom"));

        IReadOnlyDictionary<string, object?> item = Assert.Single(logger.ByCategory("interface"));
        Assert.Equal("exception", item["igw_status"]);
        Assert.Equal("failed", item["igw_transport_status"]);
        Assert.Equal(500, item["igw_http_status_code"]);
        Assert.Equal(nameof(InvalidOperationException), item["igw_error_type"]);
    }

    [Fact]
    public async Task Rest_middleware_links_the_actual_request_and_response_exchange()
    {
        var logger = new TelemetryTestLogger();
        string? savedExchange = null;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();
        app.UseIgwInterfaceTelemetry(logger, (content, _) =>
        {
            savedExchange = content;
            return Task.FromResult<string?>("payload-1");
        });
        app.MapGet("/api/v1/project/tag-scope", () => Results.Json(new
        {
            items = new[] { new { id = "PLC8.OP140.Result", value = 2 } }
        }));
        await app.StartAsync();

        using HttpClient client = app.GetTestClient();
        using HttpResponseMessage response = await client.GetAsync("/api/v1/project/tag-scope?scope=PLC8.OP140");

        Assert.True(response.IsSuccessStatusCode);
        Assert.NotNull(savedExchange);
        // 报文只保存发送内容和返回内容；方法、路径、状态码在日志字段里。
        Assert.Contains("===== 发送内容 =====", savedExchange);
        Assert.Contains("（无请求正文）", savedExchange);
        Assert.Contains("===== 返回内容 =====", savedExchange);
        Assert.Contains("PLC8.OP140.Result", savedExchange);
        Assert.DoesNotContain("Content-Type", savedExchange);
        Assert.DoesNotContain("HTTP 200", savedExchange);

        IReadOnlyDictionary<string, object?> item = Assert.Single(logger.ByCategory("interface"));
        Assert.Equal("payload-1", item["igw_payload_id"]);
        Assert.Equal(0L, Convert.ToInt64(item["igw_request_bytes"]));
        Assert.True(Convert.ToInt64(item["igw_response_bytes"]) > 0);
    }

    [Fact]
    public async Task Rest_middleware_preserves_and_records_a_json_request_body()
    {
        var logger = new TelemetryTestLogger();
        string? savedExchange = null;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();
        app.UseIgwInterfaceTelemetry(logger, (content, _) =>
        {
            savedExchange = content;
            return Task.FromResult<string?>("payload-json");
        });
        app.MapPost("/api/v1/echo", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            string body = await reader.ReadToEndAsync();
            return Results.Text(body, "application/json");
        });
        await app.StartAsync();

        const string json = "{\"station\":\"OP140\",\"result\":2}";
        using HttpClient client = app.GetTestClient();
        using HttpResponseMessage response = await client.PostAsync("/api/v1/echo",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(json, await response.Content.ReadAsStringAsync());
        Assert.NotNull(savedExchange);
        Assert.Contains(json, savedExchange);
        IReadOnlyDictionary<string, object?> item = Assert.Single(logger.ByCategory("interface"));
        Assert.Equal((long)System.Text.Encoding.UTF8.GetByteCount(json), Convert.ToInt64(item["igw_request_bytes"]));
        Assert.Equal((long)System.Text.Encoding.UTF8.GetByteCount(json), Convert.ToInt64(item["igw_response_bytes"]));
    }
    /// <summary>
    /// 第三方系统调的数据接口不记日志、不存报文：现场外部系统每秒两百多次 /Tag/ReadTagsAsList，一天两千万条，
    /// 日志中心查询超时、任务的接口调用和采集/状态记录被淹没。IGW 自己页面调的管理接口照常记。
    /// </summary>
    [Fact]
    public async Task Third_party_data_interface_calls_are_not_logged_but_gateway_management_calls_are()
    {
        var logger = new TelemetryTestLogger();
        var saved = new List<string>();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();
        app.UseIgwInterfaceTelemetry(logger, (content, _) =>
        {
            lock (saved) saved.Add(content);
            return Task.FromResult<string?>("payload-" + saved.Count);
        });
        app.MapPost("/Tag/ReadTagsAsList", () => Results.Json(new { httpStatusCode = 200, isSuccess = false, errorMessage = "点位质量异常" }));
        app.MapPost("/Tag/WriteTags", () => Results.Json(new { httpStatusCode = 200, isSuccess = true }));
        app.MapGet("/iotgateway/read", () => Results.Ok());
        app.MapGet("/api/v1/values", () => Results.Problem("boom"));
        app.MapPost("/api/v1/values/read", () => Results.Ok());
        app.MapPost("/api/v1/tags/{id}/value", (string id) => Results.Ok());
        app.MapGet("/api/v1/devices", () => Results.Ok());
        app.MapPut("/api/v1/project", () => Results.Ok(new { ok = true }));
        await app.StartAsync();
        using HttpClient client = app.GetTestClient();

        using (await client.PostAsync("/Tag/ReadTagsAsList", Json("[\"A\"]"))) { }
        using (await client.PostAsync("/Tag/WriteTags", Json("[{\"key\":\"A\",\"value\":1}]"))) { }
        using (await client.GetAsync("/iotgateway/read?ids=A")) { }
        using (await client.GetAsync("/api/v1/values?ids=A")) { }
        using (await client.PostAsync("/api/v1/values/read", Json("{\"ids\":[\"A\"]}"))) { }
        using (await client.PostAsync("/api/v1/tags/A/value", Json("{\"value\":1}"))) { }
        using (await client.GetAsync("/api/v1/devices")) { }
        Assert.Empty(logger.ByCategory("interface"));   // 第三方调用：成功、失败都不记
        Assert.Empty(saved);

        using (await client.PutAsync("/api/v1/project", Json("{}"))) { }
        IReadOnlyDictionary<string, object?> item = Assert.Single(logger.ByCategory("interface"));
        Assert.Equal("PUT /api/v1/project", item["igw_operation"]);
        Assert.Single(saved);

        static StringContent Json(string json) => new(json, System.Text.Encoding.UTF8, "application/json");
    }
}
