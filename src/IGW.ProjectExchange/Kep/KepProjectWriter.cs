using System.Text.Json;
using IGW.Abstractions;

namespace IGW.ProjectExchange.Kep;

public sealed class KepProjectWriter
{
    private static readonly string[] ProjectReserved =
        ["common.ALLTYPES_NAME", "common.ALLTYPES_DESCRIPTION", "channels"];
    private static readonly string[] ChannelReserved =
        ["common.ALLTYPES_NAME", "common.ALLTYPES_DESCRIPTION", "servermain.MULTIPLE_TYPES_DEVICE_DRIVER", "devices"];
    private static readonly string[] DeviceReserved =
        ["common.ALLTYPES_NAME", "common.ALLTYPES_DESCRIPTION", "servermain.DEVICE_ID_STRING", "tags", "tag_groups"];
    private static readonly string[] GroupReserved =
        ["common.ALLTYPES_NAME", "common.ALLTYPES_DESCRIPTION", "tags", "tag_groups"];
    private static readonly string[] TagReserved =
    [
        "common.ALLTYPES_NAME", "common.ALLTYPES_DESCRIPTION", "servermain.TAG_ADDRESS",
        "servermain.TAG_DATA_TYPE", "servermain.TAG_READ_WRITE_ACCESS",
        "servermain.TAG_SCAN_RATE_MILLISECONDS",
    ];

    private readonly IReadOnlyDictionary<string, IKepDriverCodec> _codecs;

    public KepProjectWriter(IEnumerable<IKepDriverCodec> codecs)
    {
        var registry = new Dictionary<string, IKepDriverCodec>(StringComparer.OrdinalIgnoreCase);
        foreach (IKepDriverCodec codec in codecs)
            if (!registry.TryAdd(codec.IgwDriverId, codec))
                throw new InvalidOperationException($"duplicate_igw_driver_codec:{codec.IgwDriverId}");
        _codecs = registry;
    }

    public void Write(Utf8JsonWriter writer, IgwProject project)
    {
        writer.WriteStartObject();
        writer.WriteString("common.ALLTYPES_NAME", project.Name);
        WriteOptional(writer, "common.ALLTYPES_DESCRIPTION", project.Description);
        KepRawPropertyBag.WriteUnknown(writer, project.KepProperties, ProjectReserved);
        writer.WritePropertyName("channels");
        writer.WriteStartArray();
        foreach (ChannelDef channel in project.Channels) WriteChannel(writer, channel);
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
    }

    private void WriteChannel(Utf8JsonWriter writer, ChannelDef channel)
    {
        writer.WriteStartObject();
        writer.WriteString("common.ALLTYPES_NAME", channel.Name);
        WriteOptional(writer, "common.ALLTYPES_DESCRIPTION", channel.Description);
        if (_codecs.TryGetValue(channel.DriverId, out IKepDriverCodec? codec))
            codec.WriteChannelProperties(writer, channel);
        else
            writer.WriteString("servermain.MULTIPLE_TYPES_DEVICE_DRIVER", channel.DriverId);
        KepRawPropertyBag.WriteUnknown(writer, channel.KepProperties, ChannelReserved);
        writer.WritePropertyName("devices");
        writer.WriteStartArray();
        foreach (DeviceDef device in channel.Devices) WriteDevice(writer, channel, device, codec);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteDevice(
        Utf8JsonWriter writer,
        ChannelDef channel,
        DeviceDef device,
        IKepDriverCodec? codec)
    {
        writer.WriteStartObject();
        writer.WriteString("common.ALLTYPES_NAME", device.Name);
        WriteOptional(writer, "common.ALLTYPES_DESCRIPTION", device.Description);
        if (codec is not null) codec.WriteDeviceProperties(writer, channel, device);
        else writer.WriteString("servermain.DEVICE_ID_STRING", device.Endpoint);
        KepRawPropertyBag.WriteUnknown(writer, device.KepProperties, DeviceReserved);
        WriteTags(writer, device.Tags);
        writer.WritePropertyName("tag_groups");
        writer.WriteStartArray();
        foreach (TagGroupDef group in device.Groups) WriteGroup(writer, group);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteGroup(Utf8JsonWriter writer, TagGroupDef group)
    {
        writer.WriteStartObject();
        writer.WriteString("common.ALLTYPES_NAME", group.Name);
        WriteOptional(writer, "common.ALLTYPES_DESCRIPTION", group.Description);
        KepRawPropertyBag.WriteUnknown(writer, group.KepProperties, GroupReserved);
        WriteTags(writer, group.Tags);
        writer.WritePropertyName("tag_groups");
        writer.WriteStartArray();
        foreach (TagGroupDef child in group.SubGroups) WriteGroup(writer, child);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteTags(Utf8JsonWriter writer, IEnumerable<TagDef> tags)
    {
        writer.WritePropertyName("tags");
        writer.WriteStartArray();
        foreach (TagDef tag in tags)
        {
            writer.WriteStartObject();
            writer.WriteString("common.ALLTYPES_NAME", tag.Name);
            WriteOptional(writer, "common.ALLTYPES_DESCRIPTION", tag.Description);
            writer.WriteString("servermain.TAG_ADDRESS", tag.Address);
            writer.WriteNumber("servermain.TAG_DATA_TYPE", KepTypeMap.ToCode(tag));
            writer.WriteNumber("servermain.TAG_READ_WRITE_ACCESS", tag.Access == TagAccess.Read ? 0 : 1);
            writer.WriteNumber("servermain.TAG_SCAN_RATE_MILLISECONDS", KepScanRate.Normalize(tag.ScanRateMs));
            KepRawPropertyBag.WriteUnknown(writer, tag.KepProperties, TagReserved);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteOptional(Utf8JsonWriter writer, string property, string? value)
    {
        if (!string.IsNullOrEmpty(value)) writer.WriteString(property, value);
    }
}
