using System.Net;
using System.Text;
using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using IGW.Northbound.Mes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class MesBadQualityPayloadTests
{
    [Fact]
    public async Task Field_mapping_keeps_good_values_and_marks_bad_strings_without_faking_non_strings()
    {
        await using var hub = new DataHub();
        Register(hub, "Line.Plc.DB.Good", IgwDataType.Int16, DataValue.GoodAt((short)7, DateTime.UtcNow));
        Register(hub, "Line.Plc.DB.BadText", IgwDataType.String, DataValue.BadAt(StatusCode.Bad, DateTime.UtcNow));
        Register(hub, "Line.Plc.DB.BadNumber", IgwDataType.Int16, DataValue.BadAt(StatusCode.Bad, DateTime.UtcNow));
        Register(hub, "Line.Plc.DB.BadBool", IgwDataType.Bool, DataValue.BadAt(StatusCode.Bad, DateTime.UtcNow));

        var handler = new CapturingHandler();
        await using var engine = new MesUploadEngine(hub, new MesUploadSettings(), (_, _, _) => Task.FromResult(true),
            NullLogger.Instance, httpMessageHandler: handler);
        var task = new MesTask
        {
            Payload = new MesPayload
            {
                Mode = MesPayloadModes.FieldMapping,
                Fields =
                [
                    new MesField { TagId = "Line.Plc.DB.Good", FieldName = "good" },
                    new MesField { TagId = "Line.Plc.DB.BadText", FieldName = "badText" },
                    new MesField { TagId = "Line.Plc.DB.BadNumber", FieldName = "badNumber" },
                    new MesField { TagId = "Line.Plc.DB.BadBool", FieldName = "badBool" },
                ],
            },
            Steps = [new MesStep { Http = new MesHttp { Url = "http://localhost/test" } }],
        };

        await engine.TestRunAsync(task, default);

        using var json = JsonDocument.Parse(handler.LastRequestBody);
        Assert.Equal(7, json.RootElement.GetProperty("good").GetInt32());
        Assert.Equal("Bad", json.RootElement.GetProperty("badText").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("badNumber").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("badBool").ValueKind);
    }

    [Fact]
    public async Task Data_range_uses_the_same_bad_value_policy()
    {
        await using var hub = new DataHub();
        Register(hub, "Line.Plc.DB.BadText", IgwDataType.String, DataValue.BadAt(StatusCode.Bad, DateTime.UtcNow));
        Register(hub, "Line.Plc.DB.BadNumber", IgwDataType.Int16, DataValue.BadAt(StatusCode.Bad, DateTime.UtcNow));
        var source = new CompiledMesSourceObject
        ([
            new CompiledMesSourceProperty("badText", new CompiledMesSourceField("Line.Plc.DB.BadText")),
            new CompiledMesSourceProperty("badNumber", new CompiledMesSourceField("Line.Plc.DB.BadNumber")),
        ]);
        var range = new CompiledMesRange("Line.Plc.DB", "Line.Plc", ["payload"], source, 2);
        var payload = new CompiledMesPayload(
            new CompiledMesOutputObject([new CompiledMesOutputProperty("payload", new CompiledMesOutputRange(range))]),
            [range], 2);

        var result = MesRangeJsonWriter.Build(payload, new Dictionary<string, string>(), hub, _ => ConnectionState.Connected);

        using var json = JsonDocument.Parse(result.Json);
        Assert.Equal("Bad", json.RootElement.GetProperty("payload").GetProperty("badText").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("payload").GetProperty("badNumber").ValueKind);
        Assert.Equal(2, result.BadTags.Count);
    }

    private static void Register(DataHub hub, string id, IgwDataType type, DataValue value)
    {
        hub.Register(id, new TagDef { Name = id.Split('.').Last(), DataType = type, Access = TagAccess.ReadWrite });
        hub.Update(id, value);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string LastRequestBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"httpStatusCode\":200}", Encoding.UTF8, "application/json"),
            };
        }
    }
}
