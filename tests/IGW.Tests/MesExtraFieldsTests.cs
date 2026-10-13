using System.Net;
using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using IGW.Northbound.Mes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class MesExtraFieldsTests
{
    [Fact]
    public async Task Fixed_values_keep_types_and_optional_unavailable_values_are_null()
    {
        var payload = new MesPayload { ExtraFields = [
            new() { Name = "product.code", Value = "A\"B" },
            new() { Name = "quantity", DataType = "int32", Value = "2", Required = true },
            new() { Name = "passed", DataType = "bool", Value = "false" },
            new() { Name = "metadata", DataType = "json", Value = "{\"a\":[1,true]}" },
            new() { Name = "missing", DataType = "int32", Source = "Input", Path = "quantity" },
        ] };
        var handler = await Run(payload);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("A\"B", body.RootElement.GetProperty("product").GetProperty("code").GetString());
        Assert.Equal(2, body.RootElement.GetProperty("quantity").GetInt32());
        Assert.False(body.RootElement.GetProperty("passed").GetBoolean());
        Assert.Equal(JsonValueKind.Object, body.RootElement.GetProperty("metadata").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("missing").ValueKind);
    }

    [Theory]
    [InlineData("Tag", "missing")]
    [InlineData("Input", "product.code")]
    public async Task Required_missing_source_prevents_http(string source, string path)
    {
        var handler = await Run(new MesPayload { ExtraFields = [new() { Name = "code", Source = source, Path = path, Required = true }] }, expectFailure: true);
        Assert.Null(handler.Body);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Offline_device_good_cache_is_missing_for_business_tag(bool required)
    {
        var handler = await Run(new MesPayload { ExtraFields = [new() { Name = "torque", DataType = "int32", Source = "Tag", Path = "Line.Plc.Torque", Required = required }] },
            expectFailure: required, setup: hub => { hub.Register("Line.Plc.Torque", new() { DataType = IgwDataType.Int32 }); hub.Update("Line.Plc.Torque", DataValue.GoodAt(45, DateTime.UtcNow)); },
            deviceState: _ => ConnectionState.Disconnected);
        if (required) Assert.Null(handler.Body);
        else { using var body = JsonDocument.Parse(handler.Body!); Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("torque").ValueKind); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Bad_quality_tag_never_exposes_stale_numeric_value(bool required)
    {
        var handler = await Run(new MesPayload { ExtraFields = [new() { Name = "torque", DataType = "int32", Source = "Tag", Path = "Line.Plc.Torque", Required = required }] },
            expectFailure: required, setup: hub => { hub.Register("Line.Plc.Torque", new() { DataType = IgwDataType.Int32 }); hub.Update("Line.Plc.Torque", DataValue.BadAt(StatusCode.Bad, DateTime.UtcNow)); });
        if (required) Assert.Null(handler.Body);
        else { using var body = JsonDocument.Parse(handler.Body!); Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("torque").ValueKind); }
    }

    [Fact]
    public async Task Wrong_dynamic_type_is_rejected_even_when_optional()
    {
        var handler = await Run(new MesPayload { ExtraFields = [new() { Name = "torque", DataType = "int32", Source = "Tag", Path = "Line.Plc.Torque" }] },
            expectFailure: true, setup: hub => { hub.Register("Line.Plc.Torque", new() { DataType = IgwDataType.String }); hub.Update("Line.Plc.Torque", DataValue.GoodAt("12", DateTime.UtcNow)); });
        Assert.Null(handler.Body);
    }

    [Fact]
    public async Task Ordinary_field_conflict_prevents_http()
    {
        var handler = await Run(new MesPayload { Append = new() { ["code"] = "original" }, ExtraFields = [new() { Name = "code", Value = "override" }] }, expectFailure: true);
        Assert.Null(handler.Body);
    }

    [Theory]
    [InlineData("oldAlias")]
    [InlineData("Line")]
    [InlineData("Line.Plc.DB.Value")]
    [InlineData("Line.Plc.DB.Value.detail")]
    public async Task ItemId_dictionary_key_does_not_conflict_with_old_alias_or_nested_business_paths(string extraPath)
    {
        var payload = JsonSerializer.Deserialize<MesPayload>("""
            {"mode":"FieldMapping","fieldKeyMode":"ItemId","fields":[{"tagId":"Line.Plc.DB.Value","fieldName":"oldAlias","parameterCode":"Line.Plc.DB.Value"}]}
            """, ProjectStore.Options)!;
        payload.ExtraFields = [new() { Name = extraPath, Value = "business" }];
        var handler = await Run(payload, setup: hub =>
        {
            hub.Register("Line.Plc.DB.Value", new() { DataType = IgwDataType.Int32 });
            hub.Update("Line.Plc.DB.Value", DataValue.GoodAt(12, DateTime.UtcNow));
        });

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(12, body.RootElement.GetProperty("parameterDic").GetProperty("Line.Plc.DB.Value").GetInt32());
        var extra = body.RootElement;
        foreach (string part in extraPath.Split('.')) extra = extra.GetProperty(part);
        Assert.Equal("business", extra.GetString());
    }

    [Theory]
    [InlineData("Line")]
    [InlineData("Line.Plc")]
    [InlineData("Line.Plc.DB.Value")]
    [InlineData("Line.Plc.DB.Value.detail")]
    public void ItemId_with_dotted_legacy_alias_keeps_business_paths_available(string extraPath)
    {
        var payload = JsonSerializer.Deserialize<MesPayload>("""
            {"fieldKeyMode":"ItemId","fields":[{"tagId":"Line.Plc.DB.Value","fieldName":"Line.Plc.DB.Value","parameterCode":"Line.Plc.DB.Value"}]}
            """, ProjectStore.Options)!;
        payload.ExtraFields = [new() { Name = extraPath, Value = "business" }];

        MesExtraFieldValidation.Validate(payload);
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("plain.child")]
    public async Task ItemId_ordinary_fields_no_longer_conflict_with_outer_business_fields(string extraPath)
    {
        var payload = JsonSerializer.Deserialize<MesPayload>("""
            {"fieldKeyMode":"ItemId","fields":[{"tagId":"plain","fieldName":"oldAlias","parameterCode":"plain"}]}
            """, ProjectStore.Options)!;
        payload.ExtraFields = [new() { Name = extraPath, Value = "business" }];

        MesExtraFieldValidation.Validate(payload);
        var handler = await Run(payload, setup: hub =>
        {
            hub.Register("plain", new() { DataType = IgwDataType.Int32 });
            hub.Update("plain", DataValue.GoodAt(12, DateTime.UtcNow));
        });
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(12, body.RootElement.GetProperty("parameterDic").GetProperty("plain").GetInt32());
        var extra = body.RootElement;
        foreach (string part in extraPath.Split('.')) extra = extra.GetProperty(part);
        Assert.Equal("business", extra.GetString());
    }

    [Fact]
    public async Task Legacy_template_without_extras_remains_byte_identical()
    {
        const string template = "{ \"legacy\": \"value\", \"number\" : 2 }";
        Assert.Equal(template, (await Run(new(), new MesHttp { BodyTemplate = template })).Body);
    }

    [Fact]
    public async Task Data_ranges_merge_business_fields_without_changing_collected_values()
    {
        await using var hub = new DataHub();
        var tag = new TagDef { Name = "Torque", DataType = IgwDataType.Int32 };
        hub.Register("Line.Plc.DB.Torque", tag);
        hub.Update("Line.Plc.DB.Torque", DataValue.GoodAt(12, DateTime.UtcNow));
        var project = new IgwProject { Channels = [new() { Name = "Line", Devices = [new() { Name = "Plc", Groups = [new() { Name = "DB", Tags = [tag] }] }] }] };
        var payload = new MesPayload { Mode = "DataRanges", DataRanges = [new() { SourcePath = "Line.Plc.DB", OutputPath = "data", OutputPathMode = "Custom" }], ExtraFields = [new() { Name = "product.code", Value = "ABC" }] };
        var handler = new Handler();
        await using var engine = new MesUploadEngine(hub, new MesUploadSettings(), (_, _, _) => Task.FromResult(true), NullLogger.Instance,
            catalog: MesDataRangeCatalog.Build(project), deviceState: _ => ConnectionState.Connected, httpMessageHandler: handler);
        await engine.TestRunAsync(new MesTask { Payload = payload, Steps = [new() { Http = new() { Url = "http://localhost/test" } }] }, default);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(12, body.RootElement.GetProperty("data").GetProperty("Torque").GetInt32());
        Assert.Equal("ABC", body.RootElement.GetProperty("product").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Extras_are_available_in_typed_template()
    {
        var handler = await Run(new MesPayload { ExtraFields = [new() { Name = "quantity", DataType = "int32", Value = "3" }] },
            new MesHttp { JsonTemplate = "{\"value\":\"${fields.quantity}\"}" });
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(3, body.RootElement.GetProperty("value").GetInt32());
    }

    [Theory]
    [InlineData("quantity", "quantity")]
    [InlineData("product", "product.code")]
    [InlineData("product.code", "product")]
    public void Duplicate_or_parent_paths_are_rejected(string first, string second)
    {
        Assert.Throws<InvalidDataException>(() => MesExtraFieldValidation.Validate(new MesPayload { ExtraFields = [new() { Name = first }, new() { Name = second }] }));
    }

    [Theory]
    [InlineData("int32", "1.5")]
    [InlineData("int32", "2147483648")]
    [InlineData("double", "NaN")]
    [InlineData("bool", "1")]
    [InlineData("json", "{")]
    [InlineData("string", "")]
    public void Invalid_required_fixed_configuration_is_rejected(string type, string value)
    {
        Assert.Throws<InvalidDataException>(() => MesExtraFieldValidation.Validate(new MesPayload { ExtraFields = [new() { Name = "value", DataType = type, Value = value, Required = true }] }));
    }

    private static async Task<Handler> Run(MesPayload payload, MesHttp? http = null, bool expectFailure = false, Action<DataHub>? setup = null, Func<string, ConnectionState?>? deviceState = null)
    {
        await using var hub = new DataHub();
        setup?.Invoke(hub);
        var handler = new Handler();
        await using var engine = new MesUploadEngine(hub, new MesUploadSettings(), (_, _, _) => Task.FromResult(true), NullLogger.Instance, httpMessageHandler: handler, deviceState: deviceState);
        http ??= new(); http.Url = "http://localhost/test";
        var task = new MesTask { Payload = payload, Steps = [new() { Http = http }] };
        if (expectFailure) await Assert.ThrowsAsync<InvalidDataException>(() => engine.TestRunAsync(task, default));
        else await engine.TestRunAsync(task, default);
        return handler;
    }
    private sealed class Handler : HttpMessageHandler
    {
        public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Body = await request.Content!.ReadAsStringAsync(token);
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"httpStatusCode\":200}") };
        }
    }
}
