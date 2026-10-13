using System.Net;
using System.Text;
using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using IGW.Northbound.Mes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

/// <summary>
/// 后台接口会用 [Range] 等校验拦下 NaN、无穷大(报「The field Angle must be between 0 and 1.79E+308」)：
/// 报文里无穷大写成 99999999、无穷小写成 -99999999、NaN 写成 null；小数按上报节点设置的位数和取法截取。
/// </summary>
public sealed class MesNumberPolicyTests
{
    [Theory]
    [InlineData(float.PositiveInfinity, "99999999")]
    [InlineData(float.NegativeInfinity, "-99999999")]
    [InlineData(float.NaN, "null")]
    [InlineData(1.5f, "1.5")]
    public void Infinity_and_nan_never_reach_the_payload(float value, string expected)
    {
        Assert.Equal(expected, JsonSerializer.Serialize(MesNumberPolicy.Apply(value, null)));
        Assert.Equal(expected, JsonSerializer.Serialize(MesNumberPolicy.Apply((double)value, new MesNumberFormat())));
    }

    [Theory]
    [InlineData(MesNumberRounding.Round, 12.345, "12.35")]
    [InlineData(MesNumberRounding.Round, -12.345, "-12.35")]
    [InlineData(MesNumberRounding.Truncate, 12.349, "12.34")]
    [InlineData(MesNumberRounding.Truncate, -12.349, "-12.34")]
    [InlineData(MesNumberRounding.Ceiling, 12.341, "12.35")]
    [InlineData(MesNumberRounding.Ceiling, -12.341, "-12.35")]
    [InlineData(MesNumberRounding.Round, 0.00001, "0.00")]
    public void Decimals_are_cut_by_the_configured_places_and_rounding(string rounding, double value, string expected)
    {
        var format = new MesNumberFormat { Decimals = 2, Rounding = rounding };
        Assert.Equal(expected, JsonSerializer.Serialize(MesNumberPolicy.Apply(value, format)));
    }

    [Fact]
    public void Float_is_cut_from_its_shown_value_and_integers_or_text_are_untouched()
    {
        var format = new MesNumberFormat { Decimals = 3 };
        Assert.Equal("0.1", JsonSerializer.Serialize(MesNumberPolicy.Apply(0.1f, format)));      // 不会变成 0.100000001
        Assert.Equal("123456789", JsonSerializer.Serialize(MesNumberPolicy.Apply(123456789, format)));
        Assert.Equal("\"1.23456\"", JsonSerializer.Serialize(MesNumberPolicy.Apply("1.23456", format)));
        Assert.Equal("99999999", JsonSerializer.Serialize(MesNumberPolicy.Apply(double.PositiveInfinity, format)));
        // 数组/结构体投影出来的值逐个处理
        var nested = MesNumberPolicy.Apply(new object?[] { 1.23456, float.NaN, new Dictionary<string, object?> { ["Angle"] = double.NegativeInfinity } }, format);
        Assert.Equal("[1.235,null,{\"Angle\":-99999999}]", JsonSerializer.Serialize(nested));
    }

    [Fact]
    public async Task Field_mapping_payload_is_valid_json_with_limits_and_decimals()
    {
        await using var hub = new DataHub();
        Register(hub, "Line.Plc.DB.Angle", IgwDataType.Float, DataValue.GoodAt(float.PositiveInfinity, DateTime.UtcNow));
        Register(hub, "Line.Plc.DB.Offset", IgwDataType.Float, DataValue.GoodAt(float.NegativeInfinity, DateTime.UtcNow));
        Register(hub, "Line.Plc.DB.Temp", IgwDataType.Float, DataValue.GoodAt(float.NaN, DateTime.UtcNow));
        Register(hub, "Line.Plc.DB.Volt", IgwDataType.Double, DataValue.GoodAt(3.14159, DateTime.UtcNow));

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
                    new MesField { TagId = "Line.Plc.DB.Angle" }, new MesField { TagId = "Line.Plc.DB.Offset" },
                    new MesField { TagId = "Line.Plc.DB.Temp" }, new MesField { TagId = "Line.Plc.DB.Volt" },
                ],
                Number = new MesNumberFormat { Decimals = 2, Rounding = MesNumberRounding.Truncate },
            },
            Steps = [new MesStep { Http = new MesHttp { Url = "http://localhost/test" } }],
        };

        await engine.TestRunAsync(task, default);

        Assert.Equal("{\"Angle\":99999999,\"Offset\":-99999999,\"Temp\":null,\"Volt\":3.14}", handler.LastRequestBody);
    }

    [Fact]
    public async Task Data_range_payload_no_longer_fails_on_infinity()
    {
        await using var hub = new DataHub();
        Register(hub, "Line.Plc.DB.Angle", IgwDataType.Float, DataValue.GoodAt(float.PositiveInfinity, DateTime.UtcNow));
        Register(hub, "Line.Plc.DB.Volt", IgwDataType.Double, DataValue.GoodAt(3.14159, DateTime.UtcNow));
        var source = new CompiledMesSourceObject
        ([
            new CompiledMesSourceProperty("Angle", new CompiledMesSourceField("Line.Plc.DB.Angle")),
            new CompiledMesSourceProperty("Volt", new CompiledMesSourceField("Line.Plc.DB.Volt")),
        ]);
        var range = new CompiledMesRange("Line.Plc.DB", "Line.Plc", ["DB"], source, 2);
        var payload = new CompiledMesPayload(
            new CompiledMesOutputObject([new CompiledMesOutputProperty("DB", new CompiledMesOutputRange(range))]), [range], 2);

        var result = MesRangeJsonWriter.Build(payload, new Dictionary<string, string>(), hub, _ => ConnectionState.Connected,
            new MesNumberFormat { Decimals = 1 });

        Assert.Equal("{\"DB\":{\"Angle\":99999999,\"Volt\":3.1}}", result.Json);
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
