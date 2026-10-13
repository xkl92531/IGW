using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using IGW.Abstractions;
using IGW.Northbound.RestApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IGW.Tests;

public sealed class DcsTagCompatibilityApiTests
{
    [Fact]
    public async Task Registers_exactly_the_twelve_dcs_routes_and_methods()
    {
        await using var app = await StartAppAsync(new DcsTagTestRuntime());
        var expected = new HashSet<(string Method, string Route)>
        {
            ("GET", "/Tag/ReadConditionCache"),
            ("POST", "/Tag/ReadTag"),
            ("POST", "/Tag/ReadTag1"),
            ("POST", "/Tag/ReadTags"),
            ("POST", "/Tag/ReadTagsAsList"),
            ("POST", "/Tag/ReadTagsAsDictionary"),
            ("POST", "/Tag/ReadTagsWithQuality"),
            ("POST", "/Tag/WriteTag"),
            ("POST", "/Tag/WriteTags"),
            ("POST", "/Tag/GetTagValueTypeByTagName"),
            ("POST", "/Tag/CheckTagValueValidity"),
            ("POST", "/Tag/AddTags"),
        };

        var actual = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/Tag", StringComparison.OrdinalIgnoreCase) == true)
            .SelectMany(endpoint => endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods
                .Select(method => (method, endpoint.RoutePattern.RawText!)))
            .ToHashSet();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task ReadTag1_uses_dcs_request_and_response_shape()
    {
        await using var app = await StartAppAsync(DcsTagTestRuntime.WithGoodTag("A", 7));

        var response = await app.GetTestClient().PostAsJsonAsync("/Tag/ReadTag1", new { tag = "A" });
        string json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "{\"httpStatusCode\":200,\"isSuccess\":true,\"backResult\":7,\"errorMessage\":null,\"exceptionMessage\":null}",
            json);
    }

    [Fact]
    public async Task ReadTag_accepts_original_query_parameter()
    {
        await using var app = await StartAppAsync(DcsTagTestRuntime.WithGoodTag("A", 8));

        var response = await app.GetTestClient().PostAsync("/Tag/ReadTag?tag=A", null);
        var envelope = await response.Content.ReadFromJsonAsync<DcsResponse<JsonElement>>(DcsJson.Options);

        Assert.Equal(8, envelope!.BackResult.GetInt32());
    }

    [Fact]
    public async Task ReadTagsWithQuality_returns_every_point_with_its_quality_in_camel_case()
    {
        var def = new TagDef { DataType = IgwDataType.Int32 };
        var runtime = DcsTagTestRuntime.WithGoodTags(("A", 1))
            .AddTag("Bad", def, DataValue.BadAt(StatusCode.BadNodeIdUnknown, new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc)));
        await using var app = await StartAppAsync(runtime);

        var response = await app.GetTestClient().PostAsJsonAsync("/Tag/ReadTagsWithQuality", new[] { "A", "Bad", "Nope" });
        string json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.GetProperty("isSuccess").GetBoolean());
        var rows = document.RootElement.GetProperty("backResult").EnumerateArray().ToList();
        Assert.Equal(["A", "Bad", "Nope"], rows.Select(row => row.GetProperty("key").GetString()));
        Assert.True(rows[0].GetProperty("isGood").GetBoolean());
        Assert.Equal(1, rows[0].GetProperty("value").GetInt32());
        Assert.Equal(0u, rows[0].GetProperty("statusCode").GetUInt32());
        Assert.Equal("Good", rows[0].GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, rows[0].GetProperty("reason").ValueKind);
        Assert.Equal(0x80340000u, rows[1].GetProperty("statusCode").GetUInt32());
        Assert.Equal("BadNodeIdUnknown", rows[1].GetProperty("status").GetString());
        Assert.Equal("SymbolNotFound", rows[1].GetProperty("reason").GetString());
        Assert.Equal("2026-09-19T12:00:00Z", rows[1].GetProperty("timestampUtc").GetString());
        Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("value").ValueKind);
        Assert.Equal("NotRegistered", rows[2].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task ReadTags_returns_list_of_json_strings()
    {
        await using var app = await StartAppAsync(DcsTagTestRuntime.WithGoodTags(("A", 1), ("B", "x")));

        var response = await app.GetTestClient().PostAsJsonAsync("/Tag/ReadTags", new[] { "A", "B" });
        var envelope = await response.Content.ReadFromJsonAsync<DcsResponse<List<string>>>(DcsJson.Options);

        Assert.Equal(["1", "\"x\""], envelope!.BackResult);
    }

    [Fact]
    public async Task Type_lookup_returns_direct_dictionary_without_envelope()
    {
        await using var app = await StartAppAsync(DcsTagTestRuntime.WithGoodTag("A", (short)1));

        var response = await app.GetTestClient().PostAsJsonAsync("/Tag/GetTagValueTypeByTagName", new[] { "A" });
        string json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"A\":\"Short\"}", json);
        Assert.DoesNotContain("isSuccess", json);
    }

    [Fact]
    public async Task Empty_array_returns_http_400_dcs_envelope()
    {
        await using var app = await StartAppAsync(new DcsTagTestRuntime());

        var response = await app.GetTestClient().PostAsJsonAsync("/Tag/ReadTags", Array.Empty<string>());
        var envelope = await response.Content.ReadFromJsonAsync<DcsResponse<JsonElement>>(DcsJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(envelope!.IsSuccess);
        Assert.Equal(400, envelope.HttpStatusCode);
    }

    [Fact]
    public async Task Malformed_json_returns_machine_readable_dcs_envelope()
    {
        await using var app = await StartAppAsync(new DcsTagTestRuntime());
        using var content = new StringContent("{bad", Encoding.UTF8, "application/json");

        var response = await app.GetTestClient().PostAsync("/Tag/ReadTag1", content);
        string json = await response.Content.ReadAsStringAsync();
        var envelope = JsonSerializer.Deserialize<DcsResponse<JsonElement>>(json, DcsJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(envelope!.IsSuccess);
        Assert.StartsWith("application/json", response.Content.Headers.ContentType!.ToString());
    }

    [Fact]
    public async Task Operational_write_failure_remains_http_200_envelope()
    {
        var runtime = DcsTagTestRuntime.WithWritableTag("C.D.A", IgwDataType.Int16);
        runtime.WriteHandler = (_, _, _) => StatusCode.BadNotConnected;
        await using var app = await StartAppAsync(runtime);

        var response = await app.GetTestClient().PostAsJsonAsync(
            "/Tag/WriteTag", new { tag = new { key = "C.D.A", value = 1 } });
        var envelope = await response.Content.ReadFromJsonAsync<DcsResponse<bool>>(DcsJson.Options);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(envelope!.IsSuccess);
    }

    [Fact]
    public async Task Route_metadata_has_public_plc_tag_and_unique_names()
    {
        await using var app = await StartAppAsync(new DcsTagTestRuntime());
        var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/Tag", StringComparison.OrdinalIgnoreCase) == true)
            .ToArray();

        Assert.All(endpoints, endpoint => Assert.Contains("PLC 操作", endpoint.Metadata.GetMetadata<ITagsMetadata>()!.Tags));
        Assert.Equal(endpoints.Length, endpoints.Select(endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()!.EndpointName).Distinct().Count());
    }

    [Fact]
    public async Task Public_tag_endpoint_ignores_authenticated_user_permissions()
    {
        await using var app = await StartAppAsync(
            DcsTagTestRuntime.WithGoodTag("A", 7),
            beforeMap: app => app.Use(async (context, next) =>
            {
                context.Items["IGW.AuthenticatedUser"] = new UserDef
                {
                    CanWrite = false,
                    ReadBranches = ["Denied.Branch"],
                };
                await next();
            }));

        var response = await app.GetTestClient().PostAsJsonAsync("/Tag/ReadTag1", new { tag = "A" });
        var body = await response.Content.ReadFromJsonAsync<DcsResponse<JsonElement>>(DcsJson.Options);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(body!.IsSuccess, body.ErrorMessage);
        Assert.Equal(7, body.BackResult.GetInt32());
    }

    private static async Task<WebApplication> StartAppAsync(
        IDcsTagRuntime runtime,
        Action<WebApplication>? beforeMap = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        beforeMap?.Invoke(app);
        app.MapDcsTagCompatibilityApi(runtime);
        await app.StartAsync();
        return app;
    }
}
