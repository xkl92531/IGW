using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using IGW.ProjectExchange.Igw;
using IGW.ProjectExchange.Kep;
using IGW.ProjectExchange.Security;

namespace IGW.ProjectExchange;

internal sealed record ProjectJsonReadResult(
    IgwProject Project,
    long Channels,
    long Devices,
    long Groups,
    long Tags,
    long MesTasks,
    bool HasPortableSecrets,
    KepCompatibilityReport Compatibility);

internal sealed class ProjectJsonDocumentReader
{
    private readonly KepProjectReader _kepReader;

    public ProjectJsonDocumentReader(IEnumerable<IKepDriverCodec> codecs) =>
        _kepReader = new KepProjectReader(codecs);

    public async Task<ProjectJsonReadResult> ReadAsync(
        string path,
        string? migrationPassword,
        bool restoreSecrets,
        CancellationToken cancellationToken)
    {
        await using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using JsonDocument document = await JsonDocument.ParseAsync(input, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 128,
        }, cancellationToken).ConfigureAwait(false);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new ProjectImportException("project_json_object_required", "工程 JSON 根节点必须是对象。");

        KepProjectReadResult kep = _kepReader.Read(root);
        bool hasExtension = root.TryGetProperty("igw", out JsonElement extension);
        bool hasSecrets = hasExtension && extension.TryGetProperty("secrets", out _);
        IgwProject project = hasExtension
            ? ReadIgwProject(root, extension, kep.Project, migrationPassword, restoreSecrets)
            : kep.Project;

        ValidateReferences(project);
        ProjectStore.Validate(project);
        return new ProjectJsonReadResult(
            project,
            project.Channels.Count,
            project.Channels.Sum(channel => (long)channel.Devices.Count),
            CountGroups(project),
            CountTags(project),
            project.MesUpload.Tasks.Count,
            hasSecrets,
            kep.Compatibility);
    }

    private static IgwProject ReadIgwProject(
        JsonElement root,
        JsonElement extension,
        IgwProject kepProject,
        string? migrationPassword,
        bool restoreSecrets)
    {
        ValidateHeader(extension);
        if (!extension.TryGetProperty("project", out JsonElement metadata))
            throw new ProjectImportException("igw_project_missing", "IGW 扩展缺少 project 元数据。");

        // 兼容早期 schema-v4：igw.project 直接保存完整工程快照。
        if (metadata.TryGetProperty("channels", out _))
            return new IgwExtensionReader().Merge(root, kepProject,
                restoreSecrets ? migrationPassword : null);

        var project = new IgwProject
        {
            SchemaVersion = OptionalInt(metadata, "schemaVersion", 1),
            Name = RequiredString(metadata, "name", "igw_project_name_required"),
            Description = OptionalString(metadata, "description"),
        };
        ReadDocuments(extension, project);
        ReadEntities(extension, project);
        IgwExtensionReader.Validate(project, kepProject);
        if (restoreSecrets && extension.TryGetProperty("secrets", out JsonElement secrets))
            RestoreSecrets(project, secrets, migrationPassword);
        return project;
    }

    private static void ValidateHeader(JsonElement extension)
    {
        if (!extension.TryGetProperty("format", out JsonElement format)
            || !string.Equals(format.GetString(), IgwExtensionWriter.Format, StringComparison.Ordinal))
            throw new ProjectImportException("igw_format_invalid", "IGW 扩展格式无效。");
        if (!extension.TryGetProperty("schemaVersion", out JsonElement schema)
            || !schema.TryGetInt32(out int version)
            || version != IgwExtensionWriter.SchemaVersion)
            throw new ProjectImportException("igw_schema_unsupported", "IGW 扩展版本不受支持。");
    }

    private static void ReadDocuments(JsonElement extension, IgwProject project)
    {
        if (!extension.TryGetProperty("documents", out JsonElement documents)
            || documents.ValueKind != JsonValueKind.Object)
            throw new ProjectImportException("igw_documents_missing", "IGW 扩展缺少 documents。");

        if (documents.TryGetProperty(nameof(ProjectDocumentKind.ProjectSection), out JsonElement sections))
        {
            foreach (JsonElement item in RequireArray(sections, "igw.documents.ProjectSection"))
            {
                string key = RequiredString(item, "key", "igw_document_key_required");
                JsonElement value = RequiredProperty(item, "document", "igw_document_body_required");
                switch (key)
                {
                    case "settings": project.Settings = Deserialize<ProjectSettings>(value, key); break;
                    case "opcUa": project.OpcUa = Deserialize<OpcUaSettings>(value, key); break;
                    case "history": project.History = Deserialize<HistorySettings>(value, key); break;
                    case "mqttPublish": project.MqttPublish = Deserialize<MqttPublishSettings>(value, key); break;
                    case "computedTags": project.ComputedTags = Deserialize<List<ComputedTagDef>>(value, key); break;
                    case "schedules": project.Schedules = Deserialize<List<ScheduleJobDef>>(value, key); break;
                    case "security": project.Security = Deserialize<SecuritySettings>(value, key); break;
                    case "kepProperties":
                        project.KepProperties = Deserialize<Dictionary<string, JsonElement>>(value, key);
                        break;
                    case "mesUpload": project.MesUpload = Deserialize<MesUploadSettings>(value, key); break;
                }
            }
        }

        project.Templates = ReadDocumentArray<DeviceTemplate>(documents, ProjectDocumentKind.DeviceTemplate);
        project.MesUpload.FlowTemplates = ReadDocumentArray<MesFlowTemplate>(documents, ProjectDocumentKind.FlowTemplate);
        project.MesUpload.Tasks = ReadDocumentArray<MesTask>(documents, ProjectDocumentKind.MesTask);
    }

    private static List<T> ReadDocumentArray<T>(JsonElement documents, ProjectDocumentKind kind)
    {
        if (!documents.TryGetProperty(kind.ToString(), out JsonElement array)) return [];
        var result = new List<T>();
        foreach (JsonElement item in RequireArray(array, $"igw.documents.{kind}"))
            result.Add(Deserialize<T>(RequiredProperty(item, "document", "igw_document_body_required"), kind.ToString()));
        return result;
    }

    private static void ReadEntities(JsonElement extension, IgwProject project)
    {
        if (!extension.TryGetProperty("entities", out JsonElement entities)
            || entities.ValueKind != JsonValueKind.Object)
            throw new ProjectImportException("igw_entities_missing", "IGW 扩展缺少 entities。");

        List<EntityEnvelope<ChannelDef>> channels = ReadEntityArray<ChannelDef>(entities, "channels");
        List<EntityEnvelope<DeviceDef>> devices = ReadEntityArray<DeviceDef>(entities, "devices");
        List<EntityEnvelope<TagGroupDef>> groups = ReadEntityArray<TagGroupDef>(entities, "groups");
        List<EntityEnvelope<TagDef>> tags = ReadEntityArray<TagDef>(entities, "tags");

        var channelMap = Unique(channels, "channel");
        var deviceMap = Unique(devices, "device");
        var groupMap = Unique(groups, "group");
        Unique(tags, "tag");

        project.Channels = channels.OrderBy(item => item.SortOrder).Select(item => item.Document).ToList();
        foreach (EntityEnvelope<DeviceDef> item in devices.OrderBy(item => item.SortOrder))
        {
            if (item.ParentId is null || !channelMap.TryGetValue(item.ParentId, out EntityEnvelope<ChannelDef>? parent))
                throw Dangling("device", item.Id, item.ParentId);
            parent.Document.Devices.Add(item.Document);
        }
        foreach (EntityEnvelope<TagGroupDef> item in groups.OrderBy(item => item.SortOrder))
        {
            if (item.ParentId is not null && groupMap.TryGetValue(item.ParentId, out EntityEnvelope<TagGroupDef>? parentGroup))
                parentGroup.Document.SubGroups.Add(item.Document);
            else if (item.ParentId is not null && deviceMap.TryGetValue(item.ParentId, out EntityEnvelope<DeviceDef>? parentDevice))
                parentDevice.Document.Groups.Add(item.Document);
            else throw Dangling("group", item.Id, item.ParentId);
        }
        foreach (EntityEnvelope<TagDef> item in tags.OrderBy(item => item.SortOrder))
        {
            if (item.ParentId is not null && groupMap.TryGetValue(item.ParentId, out EntityEnvelope<TagGroupDef>? parentGroup))
                parentGroup.Document.Tags.Add(item.Document);
            else if (item.ParentId is not null && deviceMap.TryGetValue(item.ParentId, out EntityEnvelope<DeviceDef>? parentDevice))
                parentDevice.Document.Tags.Add(item.Document);
            else throw Dangling("tag", item.Id, item.ParentId);
        }
    }

    private static List<EntityEnvelope<T>> ReadEntityArray<T>(JsonElement entities, string name)
    {
        if (!entities.TryGetProperty(name, out JsonElement array))
            throw new ProjectImportException("igw_entity_array_missing", $"IGW 扩展缺少 entities.{name}。");
        var result = new List<EntityEnvelope<T>>();
        foreach (JsonElement item in RequireArray(array, $"igw.entities.{name}"))
        {
            string id = RequiredString(item, "id", "igw_entity_id_required");
            string? parentId = OptionalString(item, "parentId");
            int sortOrder = OptionalInt(item, "sortOrder", result.Count);
            T entity = Deserialize<T>(RequiredProperty(item, "document", "igw_entity_document_required"), name);
            switch (entity)
            {
                case ChannelDef channel: EnsureEnvelopeId(id, channel.Id); channel.Id = id; channel.Devices = []; break;
                case DeviceDef device: EnsureEnvelopeId(id, device.Id); device.Id = id; device.Groups = []; device.Tags = []; break;
                case TagGroupDef group: EnsureEnvelopeId(id, group.Id); group.Id = id; group.SubGroups = []; group.Tags = []; break;
                case TagDef tag: EnsureEnvelopeId(id, tag.Id); tag.Id = id; break;
            }
            result.Add(new EntityEnvelope<T>(id, parentId, sortOrder, entity));
        }
        return result;
    }

    private static Dictionary<string, EntityEnvelope<T>> Unique<T>(
        IEnumerable<EntityEnvelope<T>> source,
        string kind)
    {
        var result = new Dictionary<string, EntityEnvelope<T>>(StringComparer.Ordinal);
        foreach (EntityEnvelope<T> item in source)
            if (!result.TryAdd(item.Id, item))
                throw new ProjectImportException("igw_entity_id_duplicate", $"重复的 {kind} ID：{item.Id}。");
        return result;
    }

    private static void EnsureEnvelopeId(string envelopeId, string documentId)
    {
        if (!string.IsNullOrWhiteSpace(documentId)
            && !string.Equals(envelopeId, documentId, StringComparison.Ordinal))
            throw new ProjectImportException("igw_entity_id_conflict",
                $"实体 ID 与 document.id 不一致：{envelopeId}/{documentId}。");
    }

    private static void RestoreSecrets(IgwProject project, JsonElement secrets, string? password)
    {
        if (string.IsNullOrWhiteSpace(password))
            throw new CryptographicException("migration_password_required");
        byte[] encoded = Encoding.UTF8.GetBytes(secrets.GetRawText());
        try
        {
            IReadOnlyDictionary<string, string> restored = PortableSecretEnvelope.Decrypt(encoded, password);
            PortableSecretCollector.Restore(project, restored);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
        }
    }

    private static void ValidateReferences(IgwProject project)
    {
        var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ChannelDef channel in project.Channels)
        foreach (DeviceDef device in channel.Devices)
            CollectGroupPaths(device.Groups, $"{channel.Name}.{device.Name}", groups);
        foreach (MesTask task in project.MesUpload.Tasks)
            if (!string.IsNullOrWhiteSpace(task.GroupRef) && !groups.Contains(task.GroupRef.Trim()))
                throw new ProjectImportException("mes_group_reference_dangling",
                    $"MES 任务 {task.Name} 引用了不存在的标记组：{task.GroupRef}。");
    }

    private static void CollectGroupPaths(
        IEnumerable<TagGroupDef> source,
        string prefix,
        ISet<string> target)
    {
        foreach (TagGroupDef group in source)
        {
            string path = $"{prefix}.{group.Name}";
            target.Add(path);
            CollectGroupPaths(group.SubGroups, path, target);
        }
    }

    private static long CountGroups(IgwProject project) => project.Channels.Sum(channel =>
        channel.Devices.Sum(device => CountGroups(device.Groups)));
    private static long CountGroups(IEnumerable<TagGroupDef> groups) => groups.Sum(group =>
        1L + CountGroups(group.SubGroups));
    private static long CountTags(IgwProject project) => project.Channels.Sum(channel =>
        channel.Devices.Sum(device => device.Tags.Count + CountTags(device.Groups)));
    private static long CountTags(IEnumerable<TagGroupDef> groups) => groups.Sum(group =>
        group.Tags.Count + CountTags(group.SubGroups));

    private static JsonElement.ArrayEnumerator RequireArray(JsonElement source, string path)
    {
        if (source.ValueKind != JsonValueKind.Array)
            throw new ProjectImportException("project_json_array_required", $"{path} 必须是数组。");
        return source.EnumerateArray();
    }

    private static JsonElement RequiredProperty(JsonElement source, string name, string code)
    {
        if (!source.TryGetProperty(name, out JsonElement value))
            throw new ProjectImportException(code, $"缺少属性 {name}。");
        return value;
    }

    private static string RequiredString(JsonElement source, string name, string code)
    {
        string? value = OptionalString(source, name);
        if (string.IsNullOrWhiteSpace(value))
            throw new ProjectImportException(code, $"属性 {name} 不能为空。");
        return value;
    }

    private static string? OptionalString(JsonElement source, string name) =>
        source.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static int OptionalInt(JsonElement source, string name, int fallback) =>
        source.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result)
            ? result : fallback;

    private static T Deserialize<T>(JsonElement source, string path) =>
        source.Deserialize<T>(ProjectStore.Options)
        ?? throw new ProjectImportException("igw_document_invalid", $"无法解析 IGW 文档：{path}。");

    private static ProjectImportException Dangling(string kind, string id, string? parentId) => new(
        "igw_parent_reference_dangling",
        $"{kind} {id} 引用了不存在的父节点 {parentId ?? "(null)"}。");

    private sealed record EntityEnvelope<T>(string Id, string? ParentId, int SortOrder, T Document);
}

internal sealed class ProjectImportException : Exception
{
    public string Code { get; }

    public ProjectImportException(string code, string message, Exception? inner = null)
        : base(message, inner) => Code = code;
}
