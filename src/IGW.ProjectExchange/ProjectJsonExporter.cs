using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using IGW.ProjectExchange.Kep;
using IGW.ProjectExchange.Security;

namespace IGW.ProjectExchange;

/// <summary>从 SQLite 只读快照流式生成同一份 KEP project + IGW 无损扩展 JSON。</summary>
public sealed class ProjectJsonExporter
{
    private readonly IProjectRepository _repository;
    private readonly IReadOnlyDictionary<string, IKepDriverCodec> _codecs;

    public ProjectJsonExporter(IProjectRepository repository, IEnumerable<IKepDriverCodec> codecs)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        var registry = new KepDriverCodecRegistry(codecs);
        _codecs = registry.Codecs.ToDictionary(codec => codec.IgwDriverId, StringComparer.OrdinalIgnoreCase);
    }

    public async Task ExportAsync(
        Stream output,
        ProjectExportOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        ProjectTreeNode root = await _repository.GetRootAsync(cancellationToken).ConfigureAwait(false);
        await using IProjectReadSnapshot snapshot = await _repository.OpenReadSnapshotAsync(cancellationToken)
            .ConfigureAwait(false);
        if (root.RowVersion != snapshot.Revision.Value)
            throw new InvalidOperationException("project_changed_before_export_snapshot");

        var portableSecrets = new Dictionary<string, string>(StringComparer.Ordinal);
        await foreach (ProjectPortableSecret secret in snapshot.StreamPortableSecretsAsync(cancellationToken)
                           .ConfigureAwait(false))
        {
            portableSecrets.Add(
                PortableSecretCollector.CreatePortablePath(secret.OwnerType, secret.OwnerId, secret.SecretName),
                secret.Value);
        }
        options.Validate(portableSecrets.Count > 0);

        var issues = new List<KepCompatibilityIssue>();
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WritePropertyName("project");
        await WriteKepProjectAsync(writer, snapshot, root, issues, cancellationToken).ConfigureAwait(false);
        writer.WritePropertyName("igw");
        await WriteIgwExtensionAsync(
            writer, snapshot, root, issues, portableSecrets, options, cancellationToken).ConfigureAwait(false);
        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteKepProjectAsync(
        Utf8JsonWriter writer,
        IProjectReadSnapshot snapshot,
        ProjectTreeNode root,
        List<KepCompatibilityIssue> issues,
        CancellationToken cancellationToken)
    {
        writer.WriteStartObject();
        writer.WriteString("common.ALLTYPES_NAME", root.Name);
        writer.WriteString("servermain.PROJECT_TITLE", root.Name);
        WriteOptional(writer, "common.ALLTYPES_DESCRIPTION", root.Description);
        writer.WritePropertyName("channels");
        writer.WriteStartArray();
        IReadOnlyList<ProjectEntityDocument> channels = await ReadAsync(
            snapshot.StreamEntitiesAsync(ProjectNodeKind.Channel, null, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        foreach (ProjectEntityDocument document in channels)
        {
            ChannelDef channel = Deserialize<ChannelDef>(document.DocumentJson);
            _codecs.TryGetValue(channel.DriverId, out IKepDriverCodec? codec);
            if (codec is null)
                issues.Add(new KepCompatibilityIssue(channel.Name, "driver_not_supported_by_kep",
                    KepIssueSeverity.Error, $"No KEP codec is registered for {channel.DriverId}."));
            else issues.AddRange(codec.Inspect(channel).Issues);
            await WriteChannelAsync(writer, snapshot, channel, codec, cancellationToken).ConfigureAwait(false);
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private async Task WriteChannelAsync(
        Utf8JsonWriter writer,
        IProjectReadSnapshot snapshot,
        ChannelDef channel,
        IKepDriverCodec? codec,
        CancellationToken cancellationToken)
    {
        writer.WriteStartObject();
        writer.WriteString("common.ALLTYPES_NAME", channel.Name);
        WriteOptional(writer, "common.ALLTYPES_DESCRIPTION", channel.Description);
        if (codec is null) writer.WriteString("servermain.MULTIPLE_TYPES_DEVICE_DRIVER", channel.DriverId);
        else codec.WriteChannelProperties(writer, channel);
        KepRawPropertyBag.WriteUnknown(writer, channel.KepProperties,
            ["common.ALLTYPES_NAME", "common.ALLTYPES_DESCRIPTION", "servermain.MULTIPLE_TYPES_DEVICE_DRIVER", "devices"]);
        writer.WritePropertyName("devices");
        writer.WriteStartArray();
        IReadOnlyList<ProjectEntityDocument> devices = await ReadAsync(
            snapshot.StreamEntitiesAsync(ProjectNodeKind.Device, channel.Id, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        foreach (ProjectEntityDocument document in devices)
            await WriteDeviceAsync(writer, snapshot, channel, Deserialize<DeviceDef>(document.DocumentJson), codec, cancellationToken)
                .ConfigureAwait(false);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private async Task WriteDeviceAsync(
        Utf8JsonWriter writer,
        IProjectReadSnapshot snapshot,
        ChannelDef channel,
        DeviceDef device,
        IKepDriverCodec? codec,
        CancellationToken cancellationToken)
    {
        writer.WriteStartObject();
        writer.WriteString("common.ALLTYPES_NAME", device.Name);
        WriteOptional(writer, "common.ALLTYPES_DESCRIPTION", device.Description);
        if (codec is null) writer.WriteString("servermain.DEVICE_ID_STRING", device.Endpoint);
        else codec.WriteDeviceProperties(writer, channel, device);
        KepRawPropertyBag.WriteUnknown(writer, device.KepProperties,
            ["common.ALLTYPES_NAME", "common.ALLTYPES_DESCRIPTION", "servermain.DEVICE_ID_STRING", "tags", "tag_groups"]);
        await WriteTagsAsync(writer, snapshot, device.Id, cancellationToken).ConfigureAwait(false);
        writer.WritePropertyName("tag_groups");
        writer.WriteStartArray();
        await WriteGroupsAsync(writer, snapshot, device.Id, cancellationToken).ConfigureAwait(false);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private async Task WriteGroupsAsync(
        Utf8JsonWriter writer,
        IProjectReadSnapshot snapshot,
        string parentId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ProjectEntityDocument> groups = await ReadAsync(
            snapshot.StreamEntitiesAsync(ProjectNodeKind.Group, parentId, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        foreach (ProjectEntityDocument document in groups)
        {
            TagGroupDef group = Deserialize<TagGroupDef>(document.DocumentJson);
            writer.WriteStartObject();
            writer.WriteString("common.ALLTYPES_NAME", group.Name);
            WriteOptional(writer, "common.ALLTYPES_DESCRIPTION", group.Description);
            writer.WriteBoolean("servermain.TAGGROUP_AUTOGENERATED", false);
            KepRawPropertyBag.WriteUnknown(writer, group.KepProperties,
                ["common.ALLTYPES_NAME", "common.ALLTYPES_DESCRIPTION", "servermain.TAGGROUP_AUTOGENERATED", "tags", "tag_groups"]);
            await WriteTagsAsync(writer, snapshot, group.Id, cancellationToken).ConfigureAwait(false);
            writer.WritePropertyName("tag_groups");
            writer.WriteStartArray();
            await WriteGroupsAsync(writer, snapshot, group.Id, cancellationToken).ConfigureAwait(false);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
    }

    private static async Task WriteTagsAsync(
        Utf8JsonWriter writer,
        IProjectReadSnapshot snapshot,
        string parentId,
        CancellationToken cancellationToken)
    {
        writer.WritePropertyName("tags");
        writer.WriteStartArray();
        int pending = 0;
        await foreach (ProjectEntityDocument document in snapshot.StreamEntitiesAsync(
                           ProjectNodeKind.Tag, parentId, cancellationToken).ConfigureAwait(false))
        {
            TagDef tag = Deserialize<TagDef>(document.DocumentJson);
            writer.WriteStartObject();
            writer.WriteString("common.ALLTYPES_NAME", tag.Name);
            WriteOptional(writer, "common.ALLTYPES_DESCRIPTION", tag.Description);
            writer.WriteString("servermain.TAG_ADDRESS", tag.Address);
            writer.WriteNumber("servermain.TAG_DATA_TYPE", KepTypeMap.ToCode(tag));
            writer.WriteNumber("servermain.TAG_READ_WRITE_ACCESS", tag.Access == TagAccess.Read ? 0 : 1);
            writer.WriteNumber("servermain.TAG_SCAN_RATE_MILLISECONDS", KepScanRate.Normalize(tag.ScanRateMs));
            KepRawPropertyBag.WriteUnknown(writer, tag.KepProperties,
                ["common.ALLTYPES_NAME", "common.ALLTYPES_DESCRIPTION", "servermain.TAG_ADDRESS",
                    "servermain.TAG_DATA_TYPE", "servermain.TAG_READ_WRITE_ACCESS", "servermain.TAG_SCAN_RATE_MILLISECONDS"]);
            writer.WriteEndObject();
            if (++pending == 128)
            {
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                pending = 0;
            }
        }
        writer.WriteEndArray();
    }

    private static async Task WriteIgwExtensionAsync(
        Utf8JsonWriter writer,
        IProjectReadSnapshot snapshot,
        ProjectTreeNode root,
        IReadOnlyList<KepCompatibilityIssue> issues,
        IReadOnlyDictionary<string, string> portableSecrets,
        ProjectExportOptions options,
        CancellationToken cancellationToken)
    {
        writer.WriteStartObject();
        writer.WriteString("format", "IGW.JSON.Configuration");
        writer.WriteNumber("schemaVersion", 4);
        writer.WriteString("exportedAt", DateTimeOffset.UtcNow);
        writer.WriteNumber("revision", snapshot.Revision.Value);
        writer.WritePropertyName("project");
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", root.SchemaVersion);
        writer.WriteString("name", root.Name);
        WriteOptional(writer, "description", root.Description);
        writer.WriteEndObject();

        writer.WritePropertyName("entities");
        writer.WriteStartObject();
        foreach (ProjectNodeKind kind in new[] { ProjectNodeKind.Channel, ProjectNodeKind.Device, ProjectNodeKind.Group, ProjectNodeKind.Tag })
        {
            writer.WritePropertyName(kind.ToString().ToLowerInvariant() + "s");
            writer.WriteStartArray();
            int pending = 0;
            await foreach (ProjectEntityDocument document in snapshot.StreamEntitiesAsync(kind, null, cancellationToken)
                               .ConfigureAwait(false))
            {
                writer.WriteStartObject();
                writer.WriteString("id", document.Entity.Id);
                if (document.ParentId is null) writer.WriteNull("parentId");
                else writer.WriteString("parentId", document.ParentId);
                writer.WriteNumber("sortOrder", document.SortOrder);
                writer.WritePropertyName("document");
                using (JsonDocument parsed = JsonDocument.Parse(document.DocumentJson)) parsed.RootElement.WriteTo(writer);
                writer.WriteEndObject();
                if (++pending == 128)
                {
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                    pending = 0;
                }
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();

        writer.WritePropertyName("documents");
        writer.WriteStartObject();
        foreach (ProjectDocumentKind kind in Enum.GetValues<ProjectDocumentKind>())
        {
            writer.WritePropertyName(kind.ToString());
            writer.WriteStartArray();
            await foreach (ProjectDocument document in snapshot.StreamDocumentsAsync(kind, cancellationToken)
                               .ConfigureAwait(false))
            {
                writer.WriteStartObject();
                writer.WriteString("key", document.Key);
                writer.WriteNumber("rowVersion", document.RowVersion);
                writer.WritePropertyName("document");
                using (JsonDocument parsed = JsonDocument.Parse(document.DocumentJson)) parsed.RootElement.WriteTo(writer);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();

        if (options.IncludePortableSecrets && portableSecrets.Count > 0)
        {
            byte[] envelope = PortableSecretEnvelope.Encrypt(portableSecrets, options.MigrationPassword!);
            try
            {
                using JsonDocument parsed = JsonDocument.Parse(envelope);
                writer.WritePropertyName("secrets");
                parsed.RootElement.WriteTo(writer);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(envelope);
            }
        }
        writer.WritePropertyName("compatibility");
        JsonSerializer.Serialize(writer, new KepCompatibilityReport(issues), ProjectStore.Options);
        writer.WriteEndObject();
    }

    private static async Task<IReadOnlyList<ProjectEntityDocument>> ReadAsync(
        IAsyncEnumerable<ProjectEntityDocument> source,
        CancellationToken cancellationToken)
    {
        var result = new List<ProjectEntityDocument>();
        await foreach (ProjectEntityDocument item in source.WithCancellation(cancellationToken).ConfigureAwait(false))
            result.Add(item);
        return result;
    }

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, ProjectStore.Options)
        ?? throw new InvalidDataException($"project_entity_invalid:{typeof(T).Name}");

    private static void WriteOptional(Utf8JsonWriter writer, string property, string? value)
    {
        if (!string.IsNullOrEmpty(value)) writer.WriteString(property, value);
    }
}
