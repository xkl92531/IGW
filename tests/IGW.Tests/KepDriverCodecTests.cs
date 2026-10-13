using System.Text.Json;
using IGW.Abstractions;
using IGW.Drivers.EtherNetIP.Kep;
using IGW.Drivers.IoTClient.Kep;
using IGW.Drivers.Mqtt.Kep;
using IGW.Drivers.OpcUaClient.Kep;
using IGW.Drivers.Simulator.Kep;
using IGW.ProjectExchange.Kep;
using Xunit;

namespace IGW.Tests;

public sealed class KepDriverCodecTests
{
    public static IEnumerable<object[]> DriverCodecs()
    {
        yield return [new SimulatorKepDriverCodec(), "R0", new Dictionary<string, string>(), new Dictionary<string, string>()];
        yield return [new SiemensS7KepDriverCodec(), "10.0.0.1:102", new Dictionary<string, string>(),
            new Dictionary<string, string> { ["model"] = "S7-1500", ["rack"] = "0", ["slot"] = "1" }];
        yield return [new ModbusTcpKepDriverCodec(), "10.0.0.2:502", new Dictionary<string, string> { ["wordOrder"] = "CDAB" },
            new Dictionary<string, string> { ["unitId"] = "7", ["zeroBased"] = "启用", ["wordOrder"] = "CDAB" }];
        yield return [new OmronFinsKepDriverCodec(), "10.0.0.3:9600", new Dictionary<string, string>(),
            new Dictionary<string, string> { ["wordOrder"] = "BADC" }];
        yield return [new AllenBradleyKepDriverCodec(), "10.0.0.4:44818", new Dictionary<string, string>(),
            new Dictionary<string, string> { ["slot"] = "2", ["path"] = "1,2" }];
        yield return [new MitsubishiKepDriverCodec(), "10.0.0.5:6000", new Dictionary<string, string>(),
            new Dictionary<string, string> { ["mcVersion"] = "Qna_3E" }];
        yield return [new OmronCipKepDriverCodec(), "10.0.0.6:44818", new Dictionary<string, string>(),
            new Dictionary<string, string> { ["path"] = "1,0" }];
        yield return [new OpcUaClientKepDriverCodec(), "opc.tcp://10.0.0.7:4840/UA", new Dictionary<string, string>
            { ["securityPolicy"] = "Basic256Sha256", ["anonymous"] = "false", ["username"] = "operator", ["timeoutMs"] = "9000" },
            new Dictionary<string, string>()];
    }

    [Theory]
    [MemberData(nameof(DriverCodecs))]
    public void Driver_properties_round_trip_without_claimed_setting_loss(
        IKepDriverCodec codec,
        string endpoint,
        Dictionary<string, string> channelSettings,
        Dictionary<string, string> deviceSettings)
    {
        channelSettings["networkAdapter"] = "192.168.1.20";
        var channel = new ChannelDef { Name = "C", DriverId = codec.IgwDriverId, Settings = channelSettings };
        var device = new DeviceDef { Name = "D", Endpoint = endpoint, Settings = deviceSettings };
        using JsonDocument fixture = WriteProperties(codec, channel, device);
        var importedChannel = new ChannelDef();
        var importedDevice = new DeviceDef();
        var channelUnknown = KepRawPropertyBag.Capture(fixture.RootElement.GetProperty("channel"));
        var deviceUnknown = KepRawPropertyBag.Capture(fixture.RootElement.GetProperty("device"));

        codec.ReadChannelProperties(fixture.RootElement.GetProperty("channel"), importedChannel, channelUnknown);
        codec.ReadDeviceProperties(fixture.RootElement.GetProperty("device"), importedDevice, deviceUnknown);

        Assert.Equal(codec.IgwDriverId, importedChannel.DriverId);
        Assert.Equal(endpoint, importedDevice.Endpoint);
        Assert.Equal("192.168.1.20", importedChannel.Settings["networkAdapter"]);
        foreach ((string key, string value) in deviceSettings)
            Assert.Equal(value, importedDevice.Settings[key]);
        Assert.DoesNotContain(codec.Inspect(channel).Issues, issue => issue.Severity == KepIssueSeverity.Error);
    }

    [Fact]
    public void Mqtt_is_explicitly_not_importable_into_kep()
    {
        KepCompatibilityReport report = new MqttKepDriverCodec().Inspect(
            new ChannelDef { Name = "MQTT", DriverId = "Mqtt.Client" });

        Assert.False(report.CanImportIntoKep);
        Assert.Contains(report.Issues, issue => issue.Code == "driver_not_supported_by_kep");
    }

    [Fact]
    public void Registry_rejects_duplicate_igw_driver_ids()
    {
        Assert.Throws<InvalidOperationException>(() => new KepDriverCodecRegistry([
            new SiemensS7KepDriverCodec(), new SiemensS7KepDriverCodec(),
        ]));
    }

    private static JsonDocument WriteProperties(IKepDriverCodec codec, ChannelDef channel, DeviceDef device)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("channel");
            writer.WriteStartObject();
            codec.WriteChannelProperties(writer, channel);
            writer.WriteEndObject();
            writer.WritePropertyName("device");
            writer.WriteStartObject();
            codec.WriteDeviceProperties(writer, channel, device);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(stream.ToArray());
    }
}
