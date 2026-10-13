using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IGW.Abstractions;

namespace IGW.ProjectExchange.Kep;

public sealed record KepProjectReadResult(IgwProject Project, KepCompatibilityReport Compatibility);

public sealed class KepProjectReader
{
    private static readonly string[] ChannelCommon =
    [
        "common.ALLTYPES_NAME", "common.ALLTYPES_DESCRIPTION",
        "servermain.MULTIPLE_TYPES_DEVICE_DRIVER",
    ];
    private static readonly string[] DeviceCommon =
    [
        "common.ALLTYPES_NAME", "common.ALLTYPES_DESCRIPTION",
        "servermain.DEVICE_ID_STRING",
    ];
    private static readonly string[] GroupCommon =
    [
        "common.ALLTYPES_NAME", "common.ALLTYPES_DESCRIPTION",
    ];
    private static readonly string[] TagCommon =
    [
        "common.ALLTYPES_NAME", "common.ALLTYPES_DESCRIPTION", "servermain.TAG_ADDRESS",
        "servermain.TAG_READ_WRITE_ACCESS", "servermain.TAG_SCAN_RATE_MILLISECONDS",
    ];

    private readonly Dictionary<string, IKepDriverCodec> _codecs;
    private readonly int _maxDepth;
    private readonly long _maxEntities;

    public KepProjectReader(IEnumerable<IKepDriverCodec> codecs, int maxDepth = 128, long maxEntities = 2_000_000)
    {
        _codecs = new Dictionary<string, IKepDriverCodec>(StringComparer.OrdinalIgnoreCase);
        foreach (IKepDriverCodec codec in codecs)
            foreach (string name in codec.KepDriverNames)
                if (!_codecs.TryAdd(name, codec))
                    throw new InvalidOperationException($"duplicate_kep_driver_codec:{name}");
        _maxDepth = maxDepth;
        _maxEntities = maxEntities;
    }

    public KepProjectReadResult Read(JsonElement source)
    {
        JsonElement projectElement = source.TryGetProperty("project", out JsonElement nested) ? nested : source;
        if (projectElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("kep_project_object_required");

        var issues = new List<KepCompatibilityIssue>();
        var project = new IgwProject
        {
            Name = String(projectElement, "common.ALLTYPES_NAME", "KEP导入"),
            Description = NullableString(projectElement, "common.ALLTYPES_DESCRIPTION"),
            KepProperties = KepRawPropertyBag.Capture(projectElement,
                ["common.ALLTYPES_NAME", "common.ALLTYPES_DESCRIPTION"]),
        };
        long count = 0;
        if (projectElement.TryGetProperty("channels", out JsonElement channels))
        {
            RequireArray(channels, "project.channels");
            foreach (JsonElement channelElement in channels.EnumerateArray())
            {
                Count(ref count, "channel");
                project.Channels.Add(ReadChannel(channelElement, issues, ref count));
            }
        }
        return new KepProjectReadResult(project, new KepCompatibilityReport(issues));
    }

    private ChannelDef ReadChannel(JsonElement source, List<KepCompatibilityIssue> issues, ref long count)
    {
        string name = String(source, "common.ALLTYPES_NAME", "");
        string driverName = String(source, "servermain.MULTIPLE_TYPES_DEVICE_DRIVER", "");
        var channel = new ChannelDef
        {
            Id = StableId("channel", name),
            Name = name,
            Description = NullableString(source, "common.ALLTYPES_DESCRIPTION"),
        };
        Dictionary<string, JsonElement> unknown = KepRawPropertyBag.Capture(source, ChannelCommon);
        IKepDriverCodec? codec = null;
        if (_codecs.TryGetValue(driverName, out codec))
        {
            codec.ReadChannelProperties(source, channel, unknown);
            issues.AddRange(codec.Inspect(channel).Issues);
        }
        else
        {
            channel.DriverId = driverName;
            issues.Add(new KepCompatibilityIssue(name, "driver_not_supported_by_igw", KepIssueSeverity.Error,
                $"KEP driver '{driverName}' has no IGW codec."));
        }
        channel.KepProperties = NullIfEmpty(unknown);

        if (source.TryGetProperty("devices", out JsonElement devices))
        {
            RequireArray(devices, $"{name}.devices");
            foreach (JsonElement deviceElement in devices.EnumerateArray())
            {
                Count(ref count, "device");
                channel.Devices.Add(ReadDevice(deviceElement, channel, codec, name, issues, ref count));
            }
        }
        return channel;
    }

    private DeviceDef ReadDevice(
        JsonElement source,
        ChannelDef channel,
        IKepDriverCodec? codec,
        string channelPath,
        List<KepCompatibilityIssue> issues,
        ref long count)
    {
        string name = String(source, "common.ALLTYPES_NAME", "");
        string path = $"{channelPath}.{name}";
        var device = new DeviceDef
        {
            Id = StableId("device", path),
            Name = name,
            Description = NullableString(source, "common.ALLTYPES_DESCRIPTION"),
        };
        Dictionary<string, JsonElement> unknown = KepRawPropertyBag.Capture(source, DeviceCommon);
        codec?.ReadDeviceProperties(source, device, unknown);
        device.KepProperties = NullIfEmpty(unknown);
        ReadTags(source, device.Tags, path, issues, ref count);
        ReadGroups(source, device.Groups, path, 1, issues, ref count);
        return device;
    }

    private void ReadGroups(
        JsonElement source,
        List<TagGroupDef> target,
        string parentPath,
        int depth,
        List<KepCompatibilityIssue> issues,
        ref long count)
    {
        if (depth > _maxDepth) throw new InvalidDataException("kep_maximum_group_depth_exceeded");
        if (!source.TryGetProperty("tag_groups", out JsonElement groups)) return;
        RequireArray(groups, $"{parentPath}.tag_groups");
        foreach (JsonElement groupElement in groups.EnumerateArray())
        {
            Count(ref count, "group");
            string name = String(groupElement, "common.ALLTYPES_NAME", "");
            string path = $"{parentPath}.{name}";
            var group = new TagGroupDef
            {
                Id = StableId("group", path),
                Name = name,
                Description = NullableString(groupElement, "common.ALLTYPES_DESCRIPTION"),
                KepProperties = NullIfEmpty(KepRawPropertyBag.Capture(groupElement, GroupCommon)),
            };
            ReadTags(groupElement, group.Tags, path, issues, ref count);
            ReadGroups(groupElement, group.SubGroups, path, depth + 1, issues, ref count);
            target.Add(group);
        }
    }

    private void ReadTags(
        JsonElement source,
        List<TagDef> target,
        string parentPath,
        List<KepCompatibilityIssue> issues,
        ref long count)
    {
        if (!source.TryGetProperty("tags", out JsonElement tags)) return;
        RequireArray(tags, $"{parentPath}.tags");
        foreach (JsonElement tagElement in tags.EnumerateArray())
        {
            Count(ref count, "tag");
            string name = String(tagElement, "common.ALLTYPES_NAME", "");
            string path = $"{parentPath}.{name}";
            int code = Integer(tagElement, "servermain.TAG_DATA_TYPE", 0);
            (IgwDataType type, bool isArray, bool known) = KepTypeMap.FromCode(code);
            Dictionary<string, JsonElement> properties = KepRawPropertyBag.Capture(tagElement, TagCommon);
            properties["servermain.TAG_DATA_TYPE"] = JsonSerializer.SerializeToElement(code);
            if (!known)
                issues.Add(new KepCompatibilityIssue(path, "kep_data_type_unknown", KepIssueSeverity.Warning,
                    $"KEP data type code {code} is preserved but mapped to Int16 for IGW runtime."));
            target.Add(new TagDef
            {
                Id = StableId("tag", path),
                Name = name,
                Description = NullableString(tagElement, "common.ALLTYPES_DESCRIPTION"),
                Address = String(tagElement, "servermain.TAG_ADDRESS", ""),
                DataType = type,
                IsArray = isArray,
                Access = Integer(tagElement, "servermain.TAG_READ_WRITE_ACCESS", 0) == 0
                    ? TagAccess.Read : TagAccess.ReadWrite,
                ScanRateMs = Integer(tagElement, "servermain.TAG_SCAN_RATE_MILLISECONDS", 100),
                KepProperties = properties,
            });
        }
    }

    private void Count(ref long count, string kind)
    {
        if (++count > _maxEntities) throw new InvalidDataException($"kep_maximum_{kind}_count_exceeded");
    }

    private static void RequireArray(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"kep_array_required:{path}");
    }

    private static string String(JsonElement source, string name, string fallback) =>
        source.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback : fallback;
    private static string? NullableString(JsonElement source, string name) =>
        source.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
    private static int Integer(JsonElement source, string name, int fallback) =>
        source.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int parsed)
            ? parsed : fallback;
    private static Dictionary<string, JsonElement>? NullIfEmpty(Dictionary<string, JsonElement> values) =>
        values.Count == 0 ? null : values;

    private static string StableId(string kind, string path)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"KEP\n{kind}\n{path}"));
        return new Guid(hash.AsSpan(0, 16)).ToString("N");
    }
}
