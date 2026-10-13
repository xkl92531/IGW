using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using IGW.Northbound.RestApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace IGW.Tests;

/// <summary>POST /api/v1/project/bulk/tags：伪造 IGatewayHost 记录收到的请求并返回预置结果，覆盖端点自身的校验/映射逻辑。</summary>
public sealed class ProjectBulkTagApiTests
{
    [Fact]
    public async Task Success_returns_200_with_etag_and_result_body()
    {
        var host = new FakeGatewayHost
        {
            NextResponse = new ProjectBulkTagResponse(
                Applied: true, Conflict: false, Revision: "\"project-3\"",
                ChannelId: "chan-1", DeviceId: "dev-1",
                Created: 5, Updated: 2, Deleted: 1,
                DeviceRestarted: true, ErrorCode: null, Error: null, Timing: "保存数据库 12ms;PLC 采集 8ms"),
        };
        await using var app = await StartApp(host);
        using var request = PostRequest(ValidRequest());

        HttpResponseMessage response = await app.GetTestClient().SendAsync(request);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"project-3\"", response.Headers.ETag?.Tag);
        Assert.True(body.GetProperty("applied").GetBoolean());
        Assert.Equal("\"project-3\"", body.GetProperty("revision").GetString());
        Assert.Equal("chan-1", body.GetProperty("channelId").GetString());
        Assert.Equal("dev-1", body.GetProperty("deviceId").GetString());
        Assert.Equal(5, body.GetProperty("created").GetInt32());
        Assert.Equal(2, body.GetProperty("updated").GetInt32());
        Assert.Equal(1, body.GetProperty("deleted").GetInt32());
        Assert.True(body.GetProperty("deviceRestarted").GetBoolean());
        Assert.Equal("保存数据库 12ms;PLC 采集 8ms", body.GetProperty("timing").GetString());
        Assert.NotNull(host.LastRequest);
        Assert.Equal("PLC1", host.LastRequest!.Device.Name);
        Assert.Equal("Sim", host.LastRequest.Channel.Name);
        Assert.Equal(2, host.LastRequest.Items.Count);
    }

    [Fact]
    public async Task Enum_string_values_deserialize_from_camel_case()
    {
        var host = new FakeGatewayHost();
        await using var app = await StartApp(host);
        const string json = """
            {
              "channel": { "name": "Sim", "driverId": "Simulator" },
              "device": { "name": "PLC1", "endpoint": "" },
              "items": [
                { "groupPath": ["G1"], "name": "T0", "address": "DB1.DBX0.0", "dataType": "bool", "access": "readWrite" }
              ]
            }
            """;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/project/bulk/tags")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        HttpResponseMessage response = await app.GetTestClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(host.LastRequest);
        var item = Assert.Single(host.LastRequest!.Items);
        Assert.Equal(IgwDataType.Bool, item.DataType);
        Assert.Equal(TagAccess.ReadWrite, item.Access);
    }

    [Fact]
    public async Task Invalid_items_from_api_validation_returns_400_with_indexed_details()
    {
        var host = new FakeGatewayHost();
        await using var app = await StartApp(host);
        var invalid = ValidRequest();
        invalid.Items[1].Name = "bad.name";
        using var request = PostRequest(invalid);

        HttpResponseMessage response = await app.GetTestClient().SendAsync(request);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_items", body.GetProperty("error").GetString());
        JsonElement detail = Assert.Single(body.GetProperty("details").EnumerateArray());
        Assert.Equal(1, detail.GetProperty("index").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(detail.GetProperty("reason").GetString()));
        Assert.Null(host.LastRequest); // 校验在到达 host 之前拦截
    }

    [Fact]
    public async Task Invalid_if_match_header_returns_400()
    {
        var host = new FakeGatewayHost();
        await using var app = await StartApp(host);
        using var request = PostRequest(ValidRequest());
        request.Headers.TryAddWithoutValidation("If-Match", "not-a-revision");

        HttpResponseMessage response = await app.GetTestClient().SendAsync(request);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_if_match", body.GetProperty("error").GetString());
        Assert.Null(host.LastRequest);
    }

    [Fact]
    public async Task Well_formed_if_match_is_forwarded_to_host_as_expected_revision()
    {
        var host = new FakeGatewayHost();
        await using var app = await StartApp(host);
        using var request = PostRequest(ValidRequest());
        request.Headers.TryAddWithoutValidation("If-Match", "\"project-7\"");

        HttpResponseMessage response = await app.GetTestClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"project-7\"", host.LastExpectedRevision);
    }

    [Fact]
    public async Task Revision_conflict_returns_409_with_current_revision()
    {
        var host = new FakeGatewayHost
        {
            NextResponse = new ProjectBulkTagResponse(
                Applied: false, Conflict: true, Revision: "\"project-9\"",
                ChannelId: null, DeviceId: null, Created: 0, Updated: 0, Deleted: 0,
                DeviceRestarted: false, ErrorCode: null, Error: null, Timing: ""),
        };
        await using var app = await StartApp(host);
        using var request = PostRequest(ValidRequest());

        HttpResponseMessage response = await app.GetTestClient().SendAsync(request);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("\"project-9\"", response.Headers.ETag?.Tag);
        Assert.Equal("revision_conflict", body.GetProperty("error").GetString());
        Assert.Equal("\"project-9\"", body.GetProperty("revision").GetString());
    }

    [Fact]
    public async Task Runtime_apply_failed_returns_500_with_detail_and_revision()
    {
        var host = new FakeGatewayHost
        {
            NextResponse = new ProjectBulkTagResponse(
                Applied: false, Conflict: false, Revision: "\"project-4\"",
                ChannelId: "chan-1", DeviceId: "dev-1", Created: 5, Updated: 0, Deleted: 0,
                DeviceRestarted: false, ErrorCode: "runtime_apply_failed", Error: "设备连接失败", Timing: "保存数据库 9ms"),
        };
        await using var app = await StartApp(host);
        using var request = PostRequest(ValidRequest());

        HttpResponseMessage response = await app.GetTestClient().SendAsync(request);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("\"project-4\"", response.Headers.ETag?.Tag);
        Assert.Equal("runtime_apply_failed", body.GetProperty("error").GetString());
        Assert.Equal("设备连接失败", body.GetProperty("detail").GetString());
        Assert.Equal("\"project-4\"", body.GetProperty("revision").GetString());
    }

    [Theory]
    [InlineData("mixed_siemens_address_mode")]
    [InlineData("channel_driver_mismatch")]
    [InlineData("invalid_items")]
    [InlineData("template_instance_not_supported")]
    [InlineData("name_conflict")]
    public async Task Host_side_rejection_codes_pass_through_as_400(string errorCode)
    {
        var host = new FakeGatewayHost
        {
            NextResponse = new ProjectBulkTagResponse(
                Applied: false, Conflict: false, Revision: "\"project-1\"",
                ChannelId: null, DeviceId: null, Created: 0, Updated: 0, Deleted: 0,
                DeviceRestarted: false, ErrorCode: errorCode, Error: "详情:" + errorCode, Timing: ""),
        };
        await using var app = await StartApp(host);
        using var request = PostRequest(ValidRequest());

        HttpResponseMessage response = await app.GetTestClient().SendAsync(request);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(errorCode, body.GetProperty("error").GetString());
        Assert.Equal("详情:" + errorCode, body.GetProperty("detail").GetString());
    }

    [Theory]
    [InlineData("persistence_failed", true, 503)]
    [InlineData("persistence_failed", false, 500)]
    [InlineData("too_many_items", false, 413)]
    public async Task Host_side_server_and_size_failures_map_to_their_status(string errorCode, bool retryable, int status)
    {
        var host = new FakeGatewayHost
        {
            NextResponse = new ProjectBulkTagResponse(
                Applied: false, Conflict: false, Revision: "\"project-1\"",
                ChannelId: null, DeviceId: null, Created: 0, Updated: 0, Deleted: 0,
                DeviceRestarted: false, ErrorCode: errorCode, Error: "详情:" + errorCode, Timing: "",
                Retryable: retryable),
        };
        await using var app = await StartApp(host);
        using var request = PostRequest(ValidRequest());

        HttpResponseMessage response = await app.GetTestClient().SendAsync(request);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(errorCode, body.GetProperty("error").GetString());
        Assert.Equal("详情:" + errorCode, body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Too_many_items_returns_413_before_reaching_the_host()
    {
        var host = new FakeGatewayHost();
        await using var app = await StartApp(host);
        var json = new StringBuilder("""{"channel":{"name":"Sim","driverId":"Simulator"},"device":{"name":"PLC1","endpoint":""},"items":[""");
        for (int i = 0; i <= ProjectBulkTagValidator.MaxItems; i++) json.Append(i == 0 ? "{}" : ",{}");
        json.Append("]}");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/project/bulk/tags")
        {
            Content = new StringContent(json.ToString(), Encoding.UTF8, "application/json"),
        };

        HttpResponseMessage response = await app.GetTestClient().SendAsync(request);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("too_many_items", body.GetProperty("error").GetString());
        Assert.Null(host.LastRequest);
    }

    [Fact]
    public async Task Too_many_replace_groups_returns_413_before_reaching_the_host()
    {
        var host = new FakeGatewayHost();
        await using var app = await StartApp(host);
        var body = ValidRequest();
        body.ReplaceGroups = [.. Enumerable.Range(0, ProjectBulkTagValidator.MaxReplaceGroups + 1).Select(i => new List<string> { "G" + i })];
        using var request = PostRequest(body);

        HttpResponseMessage response = await app.GetTestClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Null(host.LastRequest);
    }

    [Fact]
    public void Request_body_limit_is_consistent_with_the_item_cap() =>
        Assert.Equal(256L * 1024 * 1024, ProjectBulkTagApi.MaximumRequestBytes);

    [Fact]
    public async Task Empty_body_returns_400_invalid_request()
    {
        var host = new FakeGatewayHost();
        await using var app = await StartApp(host);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/project/bulk/tags")
        {
            Content = new StringContent("", Encoding.UTF8, "application/json"),
        };

        HttpResponseMessage response = await app.GetTestClient().SendAsync(request);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", body.GetProperty("error").GetString());
        Assert.Null(host.LastRequest);
    }

    /// <summary>
    /// 端点未单独写鉴权逻辑：/api/v1 前缀路径由 Program.cs 里全局注册的
    /// PublicPlcApiPolicy 中间件统一保护(IsProtectedPath 按路径前缀判断，与具体端点无关)。
    /// 这里直接对新路径验证该策略——未认证 401、只读用户写操作 403——
    /// 与 TagValueStreamAuthorizationTests 对既有端点的验证方式一致。
    /// </summary>
    [Fact]
    public void Unauthenticated_request_is_rejected_when_security_is_enabled()
    {
        var security = new SecuritySettings
        {
            Enabled = true,
            Users = [new UserDef { Username = "reader", Password = "secret", CanWrite = false }],
        };
        var anonymous = AuthorizationRequest(HttpMethods.Post, "/api/v1/project/bulk/tags");
        var readOnly = AuthorizationRequest(HttpMethods.Post, "/api/v1/project/bulk/tags", "reader", "secret");

        var anonymousResult = PublicPlcApiPolicy.Authorize(anonymous, security);
        var readOnlyResult = PublicPlcApiPolicy.Authorize(readOnly, security);

        Assert.False(anonymousResult.IsAllowed);
        Assert.Equal(StatusCodes.Status401Unauthorized, anonymousResult.StatusCode);
        Assert.Equal("authentication_required", anonymousResult.ErrorCode);
        Assert.False(readOnlyResult.IsAllowed);
        Assert.Equal(StatusCodes.Status403Forbidden, readOnlyResult.StatusCode);
        Assert.Equal("write_forbidden", readOnlyResult.ErrorCode);
    }

    private static DefaultHttpContext AuthorizationRequest(
        string method, string path, string? username = null, string? password = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        if (username is not null)
        {
            string raw = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
            context.Request.Headers.Authorization = new AuthenticationHeaderValue("Basic", raw).ToString();
        }
        return context;
    }

    private static HttpRequestMessage PostRequest(ProjectBulkTagRequest body) =>
        new(HttpMethod.Post, "/api/v1/project/bulk/tags")
        {
            Content = JsonContent.Create(body, options: ProjectStore.Options),
        };

    private static ProjectBulkTagRequest ValidRequest() => new()
    {
        Channel = new ProjectBulkChannelSpec("Sim", "Simulator", null),
        Device = new ProjectBulkDeviceSpec("PLC1", "", null),
        Items =
        [
            new ProjectBulkTagItem { GroupPath = ["G1"], Name = "T0", Address = "DB1.DBX0.0", DataType = IgwDataType.Bool, Access = TagAccess.ReadWrite },
            new ProjectBulkTagItem { GroupPath = ["G1"], Name = "T1", Address = "DB1.DBX0.1", DataType = IgwDataType.Bool, Access = TagAccess.Read },
        ],
    };

    private static async Task<WebApplication> StartApp(IGatewayHost host)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        app.MapProjectBulkTagApi(host);
        await app.StartAsync();
        return app;
    }

    /// <summary>记录收到的请求，返回预置结果；除 ApplyBulkTagsAsync 外其余成员不被端点使用，未实现者一律抛出。</summary>
    private sealed class FakeGatewayHost : IGatewayHost
    {
        public ProjectBulkTagRequest? LastRequest { get; private set; }
        public string? LastExpectedRevision { get; private set; }
        public ProjectBulkTagResponse NextResponse { get; set; } = new(
            Applied: true, Conflict: false, Revision: "\"project-1\"",
            ChannelId: null, DeviceId: null, Created: 0, Updated: 0, Deleted: 0,
            DeviceRestarted: false, ErrorCode: null, Error: null, Timing: "");

        public Task<ProjectBulkTagResponse> ApplyBulkTagsAsync(
            ProjectBulkTagRequest request, string? expectedRevision, CancellationToken ct)
        {
            LastRequest = request;
            LastExpectedRevision = expectedRevision;
            return Task.FromResult(NextResponse);
        }

        public DataHub Hub => throw new NotSupportedException();
        public RuntimeEngine Engine => throw new NotSupportedException();
        public AlarmEngine Alarms => throw new NotSupportedException();
        public DataLogger? Logger => null;
        public ComputedEngine Computed => throw new NotSupportedException();
        public ScheduleEngine Scheduler => throw new NotSupportedException();
        public IgwProject Project => throw new NotSupportedException();
        public IReadOnlyList<DriverDescriptor> Drivers => [];
        public INetworkAdapterCatalog NetworkAdapters => throw new NotSupportedException();
        public int OpcUaSessions => 0;
        public bool Reloading => false;

        public Task<BrowseResult> BrowsePreviewAsync(BrowsePreviewRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<OpcUaBrowseChildrenResult> BrowseOpcUaChildrenAsync(BrowsePreviewRequest connection, string nodeId, IReadOnlyList<string> pathSegments, CancellationToken ct) => throw new NotSupportedException();
        public Task<BrowseResult> BrowseOpcUaBranchAsync(BrowsePreviewRequest connection, OpcUaBrowseNode selectedRoot, int limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<object> SiemensAuthenticationStatusAsync(string deviceName, CancellationToken ct) => throw new NotSupportedException();
        public Task<object> VerifySiemensAuthenticationAsync(string deviceName, string username, string password, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteSiemensAuthenticationAsync(string deviceName, CancellationToken ct) => throw new NotSupportedException();
        public Task<object> ReconnectChannelAsync(string channelName, CancellationToken ct) => throw new NotSupportedException();
        public object ComponentsSnapshot() => new { };
        public object LicenseSnapshot() => new { blocked = false };
        public Task<object> ActivateLicenseAsync(string code) => throw new NotSupportedException();
        public Task ReloadAsync(IgwProject newProject) => throw new NotSupportedException();
        public object MesStatusSnapshot() => new { };
        public object MesLogSnapshot(int limit) => Array.Empty<object>();
        public IReadOnlyList<MesTaskStatus> MesTaskStatuses() => [];
        public IReadOnlyList<MesFlowEvent> MesTimeline(string taskName) => [];
        public Task<object> MesTestAsync(MesTask task, CancellationToken ct) => throw new NotSupportedException();
    }
}
