using System.Net;
using System.Text;
using IGW.Abstractions;
using IGW.Core;
using IGW.Northbound.Mes;
using Xunit;

namespace IGW.Tests;

public sealed class MesTelemetryTests
{
    [Fact]
    public async Task Test_upload_separates_http_transport_from_business_result_without_bodies()
    {
        var logger = new TelemetryTestLogger();
        string? capturedExchange = null;
        await using var hub = new DataHub();
        using var handler = new JsonHandler("{\"isSuccess\":false,\"secret\":\"response-body-must-not-be-logged\"}");
        await using var engine = new MesUploadEngine(hub, new MesUploadSettings { LogDbPath = "" },
            (_, _, _) => Task.FromResult(true), logger, httpMessageHandler: handler,
            saveLogPayload: (content, _) =>
            {
                capturedExchange = content;
                return Task.FromResult<string?>("payload-reference");
            });
        var task = new MesTask
        {
            Id = "task-stable-id",
            Name = "display-name",
            Steps = [new MesStep
            {
                Name = "upload-step",
                Http = new MesHttp
                {
                    Url = "http://user:password@mes.local/report?token=hidden",
                    SuccessKey = "isSuccess",
                    SuccessValue = "true",
                    JsonTemplate = "{\"secret\":\"request-body-must-not-be-logged\"}"
                }
            }]
        };

        await engine.TestRunAsync(task, CancellationToken.None);

        IReadOnlyList<IReadOnlyDictionary<string, object?>> records = logger.ByCategory("interface");
        IReadOnlyDictionary<string, object?> transport = Assert.Single(records,
            record => Equals(record.GetValueOrDefault("igw_event_kind"), "mes_transport"));
        IReadOnlyDictionary<string, object?> business = Assert.Single(records,
            record => Equals(record.GetValueOrDefault("igw_event_kind"), "mes_business"));
        Assert.Equal("task-stable-id", transport["igw_task_id"]);
        Assert.Equal("display-name", transport["igw_task_name"]);
        Assert.Equal("step:0", transport["igw_node_id"]);
        Assert.Equal("upload-step", transport["igw_node_name"]);
        Assert.Equal("mes", transport["igw_module"]);
        Assert.Equal("http_upload", transport["igw_feature"]);
        Assert.Equal("outbound", transport["igw_direction"]);
        Assert.Equal("http", transport["igw_protocol"]);
        Assert.Equal("POST", transport["igw_operation"]);
        Assert.Equal("upload-step", transport["igw_interface_name"]);
        Assert.Equal("http://mes.local/report", transport["igw_target"]);
        Assert.Equal(200, transport["igw_http_status_code"]);
        Assert.Equal("completed", transport["igw_transport_status"]);
        Assert.Equal("success", transport["igw_status"]);
        Assert.Equal("payload-reference", transport["igw_payload_id"]);
        Assert.Equal(44, transport["igw_request_bytes"]);
        Assert.Equal(63, transport["igw_response_bytes"]);
        Assert.Equal("failure", business["igw_status"]);
        Assert.Equal("false", business["igw_business_status"]);
        Assert.Contains("===== 发送内容 =====", capturedExchange);
        Assert.Contains("request-body-must-not-be-logged", capturedExchange);
        Assert.Contains("===== 返回内容 =====", capturedExchange);
        Assert.DoesNotContain("Content-Type", capturedExchange);
        Assert.Contains("response-body-must-not-be-logged", capturedExchange);
        string serialized = string.Join('|', records.SelectMany(record => record.Values).Select(value => value?.ToString()));
        Assert.DoesNotContain("request-body-must-not-be-logged", serialized);
        Assert.DoesNotContain("response-body-must-not-be-logged", serialized);
        Assert.DoesNotContain("password", serialized);
        Assert.DoesNotContain("hidden", serialized);
    }

    [Fact]
    public async Task Payload_storage_failure_does_not_change_the_mes_result()
    {
        var logger = new TelemetryTestLogger();
        await using var hub = new DataHub();
        using var handler = new JsonHandler("{\"isSuccess\":true}");
        await using var engine = new MesUploadEngine(hub, new MesUploadSettings { LogDbPath = "" },
            (_, _, _) => Task.FromResult(true), logger, httpMessageHandler: handler,
            saveLogPayload: (_, _) => throw new IOException("disk unavailable"));
        var task = new MesTask
        {
            Id = "task-1",
            Name = "task",
            Steps = [new MesStep
            {
                Name = "upload",
                Http = new MesHttp
                {
                    Url = "http://mes.local/report",
                    SuccessKey = "isSuccess",
                    SuccessValue = "true",
                    JsonTemplate = "{\"value\":1}"
                }
            }]
        };

        await engine.TestRunAsync(task, CancellationToken.None);

        IReadOnlyDictionary<string, object?> transport = Assert.Single(logger.ByCategory("interface"),
            record => Equals(record.GetValueOrDefault("igw_event_kind"), "mes_transport"));
        Assert.Equal("success", transport["igw_status"]);
        Assert.Null(transport["igw_payload_id"]);
    }

    private sealed class JsonHandler(string responseBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            });
    }
}
