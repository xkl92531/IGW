using System.Text.Json;
using System.Security.Cryptography;
using IGW.Abstractions;
using IGW.Core;
using IGW.ProjectExchange.Kep;
using IGW.ProjectExchange.Security;

namespace IGW.ProjectExchange.Igw;

public sealed class IgwExtensionReader
{
    public IgwProject Merge(JsonElement source, IgwProject kepProject)
        => Merge(source, kepProject, null);

    public IgwProject Merge(JsonElement source, IgwProject kepProject, string? migrationPassword)
    {
        JsonElement extension = source.TryGetProperty("igw", out JsonElement nested) ? nested : source;
        if (!extension.TryGetProperty("format", out JsonElement format)
            || !string.Equals(format.GetString(), IgwExtensionWriter.Format, StringComparison.Ordinal))
            throw new IgwExtensionMergeException("igw_format_invalid", "IGW extension format is invalid.");
        if (!extension.TryGetProperty("schemaVersion", out JsonElement schema)
            || !schema.TryGetInt32(out int version)
            || version != IgwExtensionWriter.SchemaVersion)
            throw new IgwExtensionMergeException("igw_schema_unsupported", "IGW extension schema is unsupported.");
        if (!extension.TryGetProperty("project", out JsonElement projectElement))
            throw new IgwExtensionMergeException("igw_project_missing", "IGW extension project is missing.");

        IgwProject saved = JsonSerializer.Deserialize<IgwProject>(projectElement.GetRawText(), ProjectStore.Options)
            ?? throw new IgwExtensionMergeException("igw_project_invalid", "IGW extension project is empty.");
        if (extension.TryGetProperty("secrets", out JsonElement secrets))
        {
            if (string.IsNullOrWhiteSpace(migrationPassword))
                throw new CryptographicException("migration_password_required");
            byte[] encoded = System.Text.Encoding.UTF8.GetBytes(secrets.GetRawText());
            try
            {
                IReadOnlyDictionary<string, string> restored =
                    PortableSecretEnvelope.Decrypt(encoded, migrationPassword);
                PortableSecretCollector.Restore(saved, restored);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(encoded);
            }
        }
        Validate(saved, kepProject);
        return saved;
    }

    public static void Validate(IgwProject saved, IgwProject kep)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(kep);
        var channels = kep.Channels.Select(channel => new IgwMatchCandidate<ChannelDef>(
            channel.Id, channel.Name, ChannelFingerprint(channel), channel)).ToArray();
        var channelIndex = new IgwEntityMatchIndex<ChannelDef>(channels);
        foreach (ChannelDef savedChannel in saved.Channels)
        {
            IgwMatchCandidate<ChannelDef> channel = channelIndex.Match(
                savedChannel.Id, savedChannel.Name, ChannelFingerprint(savedChannel));
            ValidateDevices(savedChannel, channel.Value);
        }
    }

    private static void ValidateDevices(ChannelDef savedChannel, ChannelDef kepChannel)
    {
        string savedPrefix = savedChannel.Name;
        string kepPrefix = kepChannel.Name;
        var devices = kepChannel.Devices.Select(device => new IgwMatchCandidate<DeviceDef>(
            device.Id, $"{kepPrefix}.{device.Name}", DeviceFingerprint(device), device)).ToArray();
        var deviceIndex = new IgwEntityMatchIndex<DeviceDef>(devices);
        foreach (DeviceDef savedDevice in savedChannel.Devices)
        {
            string savedPath = $"{savedPrefix}.{savedDevice.Name}";
            DeviceDef kepDevice = deviceIndex.Match(savedDevice.Id, savedPath,
                DeviceFingerprint(savedDevice)).Value;
            ValidateTags(savedDevice.Tags, kepDevice.Tags, savedPath, $"{kepPrefix}.{kepDevice.Name}");
            ValidateGroups(savedDevice.Groups, kepDevice.Groups, savedPath, $"{kepPrefix}.{kepDevice.Name}");
        }
    }

    private static void ValidateGroups(
        IReadOnlyList<TagGroupDef> savedGroups,
        IReadOnlyList<TagGroupDef> kepGroups,
        string savedPrefix,
        string kepPrefix)
    {
        var candidates = kepGroups.Select(group => new IgwMatchCandidate<TagGroupDef>(
            group.Id, $"{kepPrefix}.{group.Name}", GroupFingerprint(group), group)).ToArray();
        var index = new IgwEntityMatchIndex<TagGroupDef>(candidates);
        foreach (TagGroupDef savedGroup in savedGroups)
        {
            string savedPath = $"{savedPrefix}.{savedGroup.Name}";
            IgwMatchCandidate<TagGroupDef> match = index.Match(
                savedGroup.Id, savedPath, GroupFingerprint(savedGroup));
            string kepPath = match.Path;
            ValidateTags(savedGroup.Tags, match.Value.Tags, savedPath, kepPath);
            ValidateGroups(savedGroup.SubGroups, match.Value.SubGroups, savedPath, kepPath);
        }
    }

    private static void ValidateTags(
        IReadOnlyList<TagDef> savedTags,
        IReadOnlyList<TagDef> kepTags,
        string savedPrefix,
        string kepPrefix)
    {
        var candidates = kepTags.Select(tag => new IgwMatchCandidate<TagDef>(
            tag.Id, $"{kepPrefix}.{tag.Name}", TagFingerprint(tag), tag)).ToArray();
        var index = new IgwEntityMatchIndex<TagDef>(candidates);
        foreach (TagDef savedTag in savedTags)
        {
            string savedPath = $"{savedPrefix}.{savedTag.Name}";
            TagDef kepTag = index.Match(savedTag.Id, savedPath,
                TagFingerprint(savedTag)).Value;
            if (!string.Equals(savedTag.Address, kepTag.Address, StringComparison.Ordinal)
                || KepTypeMap.ToCode(savedTag) != KepTypeMap.ToCode(kepTag)
                || savedTag.IsArray != kepTag.IsArray
                || savedTag.Access != kepTag.Access)
            {
                throw new IgwExtensionMergeException(
                    "kep_igw_conflict",
                    $"KEP and IGW values conflict at '{savedPath}' (address, type, array, or access).",
                    [$"{kepPrefix}.{kepTag.Name}"]);
            }
        }
    }

    private static string ChannelFingerprint(ChannelDef channel) => channel.DriverId;
    private static string DeviceFingerprint(DeviceDef device) => device.Endpoint;
    private static string GroupFingerprint(TagGroupDef group) => $"{group.Tags.Count}:{group.SubGroups.Count}";
    // 类型按 KEP 能表达的编码比较：DTL 等西门子类型在 KEP 层只能记为"默认"(0)，
    // 读回时是 Int16 占位；用原始 IGW 类型比较会让网关自己导出的工程无法再导入。
    private static string TagFingerprint(TagDef tag) =>
        $"{tag.Address}|{KepTypeMap.ToCode(tag)}|{tag.IsArray}|{tag.Access}";
}
