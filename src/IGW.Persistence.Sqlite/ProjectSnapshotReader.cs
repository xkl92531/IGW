using IGW.Abstractions;
using Microsoft.Data.Sqlite;

namespace IGW.Persistence.Sqlite;

/// <summary>从关系化 SQLite 模式还原一个完整、可运行的工程快照。</summary>
public sealed class ProjectSnapshotReader
{
    private readonly ProjectDatabase _database;
    private readonly ProjectSecretVault _vault;

    public ProjectSnapshotReader(ProjectDatabase database, ProjectSecretVault vault)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
    }

    public async Task<IgwProject> ReadAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        // 百万点工程要按索引顺序在 tags/tag_options 之间随机读 1GB 数据：默认 2MB 页缓存下需 20 多秒，
        // 本次读取临时放大缓存并启用内存映射(约 5 秒)，读完恢复默认，连接回到连接池后不常驻内存。
        Pragma(connection, "cache_size=-262144");
        Pragma(connection, "mmap_size=4294967296");
        try
        {
            return await ReadCoreAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Pragma(connection, "cache_size=-2000");
            Pragma(connection, "mmap_size=0");
        }
    }

    private static void Pragma(SqliteConnection connection, string pragma)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA " + pragma + ";";
        command.ExecuteNonQuery();
    }

    private async Task<IgwProject> ReadCoreAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        IgwProject project = await ReadProjectAsync(connection, cancellationToken).ConfigureAwait(false);
        await ReadSectionsAsync(connection, project, cancellationToken).ConfigureAwait(false);
        await ReadTopologyAsync(connection, project, cancellationToken).ConfigureAwait(false);
        await ReadTemplatesAsync(connection, project, cancellationToken).ConfigureAwait(false);
        await ReadMesAsync(connection, project, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<StoredProjectSecret> secrets = await ReadSecretsAsync(connection, cancellationToken).ConfigureAwait(false);
        _vault.Restore(project, secrets);
        return project;
    }

    /// <summary>只读一个设备子树（含它所属通道的自身字段，不含 Devices；组/点/已还原密钥齐全），
    /// 供批量写入后只重载被改动的那一个设备，避免整个工程的大点表反序列化开销。</summary>
    public async Task<ProjectDeviceSnapshot> LoadDeviceAsync(
        string deviceId, CancellationToken cancellationToken, DevicePartialScope? partial = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        await using SqliteConnection connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (partial is not null)
            await SelectPartialGroupsAsync(connection, deviceId, partial.RootGroupNames, cancellationToken).ConfigureAwait(false);

        // 临时工程只用来承载这一个通道+设备,好复用 ProjectSecretVault.Restore 的既有遍历逻辑。
        var project = new IgwProject();
        Dictionary<string, DeviceDef> devices = await ReadTopologyAsync(connection, project, cancellationToken, deviceId, partial)
            .ConfigureAwait(false);
        if (!devices.TryGetValue(deviceId, out DeviceDef? device))
            throw new KeyNotFoundException($"找不到设备 {deviceId}。");
        ChannelDef channel = project.Channels.Single();

        IReadOnlyList<StoredProjectSecret> secrets = await ReadSecretsAsync(connection, cancellationToken, channel.Id, deviceId)
            .ConfigureAwait(false);
        _vault.Restore(project, secrets);

        channel.Devices.Clear();
        return new ProjectDeviceSnapshot(channel, device);
    }

    /// <summary>把部分读回范围内的组(指定根组及其整棵子树)记进本连接的临时表 IGW_partial_groups。</summary>
    private static async Task SelectPartialGroupsAsync(
        SqliteConnection connection, string deviceId, IReadOnlyCollection<string> rootGroupNames, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TEMP TABLE IF NOT EXISTS IGW_partial_groups(id TEXT PRIMARY KEY) WITHOUT ROWID;
            DELETE FROM IGW_partial_groups;
            WITH RECURSIVE scope(id) AS (
                SELECT id FROM IGW_tag_groups
                WHERE device_id=$deviceId AND parent_group_id IS NULL
                  AND name COLLATE NOCASE IN (SELECT value FROM json_each($roots))
                UNION ALL
                SELECT g.id FROM IGW_tag_groups g JOIN scope s ON g.parent_group_id=s.id)
            INSERT INTO IGW_partial_groups(id) SELECT id FROM scope;
            """;
        command.Parameters.AddWithValue("$deviceId", deviceId);
        command.Parameters.AddWithValue("$roots", System.Text.Json.JsonSerializer.Serialize(rootGroupNames));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IgwProject> ReadProjectAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, description, schema_version FROM IGW_project_meta WHERE singleton_id=1;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("工程数据库中没有工程元数据。");

        return new IgwProject
        {
            Name = reader.GetString(0),
            Description = GetNullableString(reader, 1),
            SchemaVersion = reader.GetInt32(2),
        };
    }

    private static async Task ReadSectionsAsync(
        SqliteConnection connection,
        IgwProject project,
        CancellationToken cancellationToken)
    {
        var sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT section_key, document_json FROM IGW_project_sections;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            sections[reader.GetString(0)] = reader.GetString(1);

        project.Settings = ReadSection<ProjectSettings>(sections, "settings");
        project.OpcUa = ReadSection<OpcUaSettings>(sections, "opcUa");
        project.History = ReadSection<HistorySettings>(sections, "history");
        project.MqttPublish = ReadSection<MqttPublishSettings>(sections, "mqttPublish");
        project.ComputedTags = ReadSection<List<ComputedTagDef>>(sections, "computedTags");
        project.Schedules = ReadSection<List<ScheduleJobDef>>(sections, "schedules");
        project.Security = ReadSection<SecuritySettings>(sections, "security");
        project.KepProperties = ReadOptionalKepProperties(sections, "kepProperties");
        project.MesUpload = ReadSection<MesUploadSettings>(sections, "mesUpload");
    }

    private static T ReadSection<T>(IReadOnlyDictionary<string, string> sections, string key)
    {
        if (!sections.TryGetValue(key, out string? document))
            throw new InvalidDataException($"工程数据库缺少 {key} 配置段。");
        return ProjectEntityMapper.Deserialize<T>(document);
    }

    private static async Task<Dictionary<string, DeviceDef>> ReadTopologyAsync(
        SqliteConnection connection,
        IgwProject project,
        CancellationToken cancellationToken,
        string? deviceId = null,
        DevicePartialScope? partial = null)
    {
        Dictionary<string, ChannelDef> channels = await ReadChannelsAsync(connection, project, cancellationToken, deviceId)
            .ConfigureAwait(false);
        Dictionary<string, DeviceDef> devices = await ReadDevicesAsync(connection, channels, cancellationToken, deviceId)
            .ConfigureAwait(false);
        Dictionary<string, GroupRow> groups = await ReadGroupsAsync(connection, cancellationToken, deviceId, partial is not null)
            .ConfigureAwait(false);

        foreach (GroupRow row in groups.Values)
        {
            if (row.ParentGroupId is null)
            {
                if (!devices.TryGetValue(row.DeviceId, out DeviceDef? device))
                    throw new InvalidDataException($"标记组 {row.Group.Id} 引用了不存在的设备 {row.DeviceId}。");
                device.Groups.Add(row.Group);
            }
            else
            {
                if (!groups.TryGetValue(row.ParentGroupId, out GroupRow? parent))
                    throw new InvalidDataException($"标记组 {row.Group.Id} 引用了不存在的父组 {row.ParentGroupId}。");
                parent.Group.SubGroups.Add(row.Group);
            }
        }

        await ReadTagsAsync(connection, devices, groups, cancellationToken, deviceId, partial).ConfigureAwait(false);
        return devices;
    }

    private static async Task<Dictionary<string, ChannelDef>> ReadChannelsAsync(
        SqliteConnection connection,
        IgwProject project,
        CancellationToken cancellationToken,
        string? deviceId = null)
    {
        var result = new Dictionary<string, ChannelDef>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        // deviceId 非空时只读该设备所属的那一条通道行,供 LoadDeviceAsync 单设备加载使用。
        string whereClause = deviceId is null ? "" : "WHERE id IN (SELECT channel_id FROM IGW_devices WHERE id=$deviceId)";
        command.CommandText = $"""
            SELECT id, name, description, driver_id, settings_json, kep_properties_json
            FROM IGW_channels
            {whereClause}
            ORDER BY sort_order, rowid;
            """;
        if (deviceId is not null) command.Parameters.AddWithValue("$deviceId", deviceId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var channel = new ChannelDef
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Description = GetNullableString(reader, 2),
                DriverId = reader.GetString(3),
                Settings = ProjectEntityMapper.Deserialize<Dictionary<string, string>>(reader.GetString(4)),
                KepProperties = ReadKepProperties(reader, 5),
            };
            project.Channels.Add(channel);
            result.Add(channel.Id, channel);
        }
        return result;
    }

    private static async Task<Dictionary<string, DeviceDef>> ReadDevicesAsync(
        SqliteConnection connection,
        IReadOnlyDictionary<string, ChannelDef> channels,
        CancellationToken cancellationToken,
        string? deviceId = null)
    {
        var result = new Dictionary<string, DeviceDef>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        string whereClause = deviceId is null ? "" : "WHERE id=$deviceId";
        command.CommandText = $"""
            SELECT id, channel_id, name, description, endpoint, template_ref, delta_json, settings_json, kep_properties_json
            FROM IGW_devices
            {whereClause}
            ORDER BY channel_id, sort_order, rowid;
            """;
        if (deviceId is not null) command.Parameters.AddWithValue("$deviceId", deviceId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string channelId = reader.GetString(1);
            if (!channels.TryGetValue(channelId, out ChannelDef? channel))
                throw new InvalidDataException($"设备 {reader.GetString(0)} 引用了不存在的通道 {channelId}。");
            var device = new DeviceDef
            {
                Id = reader.GetString(0),
                Name = reader.GetString(2),
                Description = GetNullableString(reader, 3),
                Endpoint = reader.GetString(4),
                TemplateRef = GetNullableString(reader, 5),
                Delta = reader.IsDBNull(6) ? null : ProjectEntityMapper.Deserialize<DeviceDelta>(reader.GetString(6)),
                Settings = ProjectEntityMapper.Deserialize<Dictionary<string, string>>(reader.GetString(7)),
                KepProperties = ReadKepProperties(reader, 8),
            };
            channel.Devices.Add(device);
            result.Add(device.Id, device);
        }
        return result;
    }

    private static async Task<Dictionary<string, GroupRow>> ReadGroupsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken,
        string? deviceId = null,
        bool partial = false)
    {
        var result = new Dictionary<string, GroupRow>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        string whereClause = deviceId is null ? "" : "WHERE device_id=$deviceId";
        if (partial) whereClause += " AND id IN (SELECT id FROM IGW_partial_groups)";
        command.CommandText = $"""
            SELECT id, device_id, parent_group_id, name, description, kep_properties_json
            FROM IGW_tag_groups
            {whereClause}
            ORDER BY device_id, parent_group_id, sort_order, rowid;
            """;
        if (deviceId is not null) command.Parameters.AddWithValue("$deviceId", deviceId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string id = reader.GetString(0);
            result.Add(id, new GroupRow(
                reader.GetString(1),
                GetNullableString(reader, 2),
                new TagGroupDef
                {
                    Id = id,
                    Name = reader.GetString(3),
                    Description = GetNullableString(reader, 4),
                    KepProperties = ReadKepProperties(reader, 5),
                }));
        }

        return result;
    }

    private static async Task ReadTagsAsync(
        SqliteConnection connection,
        IReadOnlyDictionary<string, DeviceDef> devices,
        IReadOnlyDictionary<string, GroupRow> groups,
        CancellationToken cancellationToken,
        string? ownerDeviceId = null,
        DevicePartialScope? partial = null)
    {
        await using var command = connection.CreateCommand();
        string whereClause = ownerDeviceId is null ? "" : "WHERE t.device_id=$deviceId";
        if (partial is not null)
            whereClause += partial.IncludeRootTags
                ? " AND (t.group_id IN (SELECT id FROM IGW_partial_groups) OR t.group_id IS NULL)"
                : " AND t.group_id IN (SELECT id FROM IGW_partial_groups)";
        command.CommandText = $"""
            SELECT t.id, t.device_id, t.group_id, t.name, t.description, t.address,
                   t.data_type, t.access, t.scan_rate_ms, t.string_length, t.text_encoding, t.eng_unit,
                   o.is_array, o.array_json, o.scaling_json, o.deadband, o.deadband_type,
                   o.alarm_json, o.write_confirmation_json, o.metadata_json, o.kep_properties_json
            FROM IGW_tags t
            INNER JOIN IGW_tag_options o ON o.tag_id=t.id
            {whereClause}
            ORDER BY t.device_id, t.group_id, t.sort_order, t.rowid;
            """;
        if (ownerDeviceId is not null) command.Parameters.AddWithValue("$deviceId", ownerDeviceId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        // 百万行逐行同步读取(Microsoft.Data.Sqlite 的异步读取本身也是同步执行，只多出每行的 Task 开销)。
        int rows = 0;
        while (reader.Read())
        {
            if ((++rows & 0xFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            var tag = new TagDef
            {
                Id = reader.GetString(0),
                Name = reader.GetString(3),
                Description = GetNullableString(reader, 4),
                Address = reader.GetString(5),
                DataType = (IgwDataType)reader.GetInt32(6),
                Access = (TagAccess)reader.GetInt32(7),
                ScanRateMs = reader.GetInt32(8),
                StringLength = GetNullableInt32(reader, 9),
                TextEncoding = reader.IsDBNull(10) ? null : (PlcTextEncoding)reader.GetInt32(10),
                EngUnit = GetNullableString(reader, 11),
                IsArray = reader.GetInt32(12) != 0,
                Array = ReadNullableJson<ArrayDef>(reader, 13),
                Scaling = ReadNullableJson<ScalingDef>(reader, 14),
                Deadband = reader.GetDouble(15),
                DeadbandType = (DeadbandType)reader.GetInt32(16),
                Alarm = ReadNullableJson<AlarmDef>(reader, 17),
                WriteConfirmation = ReadNullableJson<WriteConfirmationDef>(reader, 18),
                Metadata = ReadNullableJson<Dictionary<string, string>>(reader, 19),
                KepProperties = ReadKepPropertiesFast(reader, 20),
            };

            string? groupId = GetNullableString(reader, 2);
            if (groupId is not null)
            {
                if (!groups.TryGetValue(groupId, out GroupRow? group))
                    throw new InvalidDataException($"标记 {tag.Id} 引用了不存在的标记组 {groupId}。");
                group.Group.Tags.Add(tag);
            }
            else
            {
                string deviceId = reader.GetString(1);
                if (!devices.TryGetValue(deviceId, out DeviceDef? device))
                    throw new InvalidDataException($"标记 {tag.Id} 引用了不存在的设备 {deviceId}。");
                device.Tags.Add(tag);
            }
        }
    }

    private static async Task ReadTemplatesAsync(
        SqliteConnection connection,
        IgwProject project,
        CancellationToken cancellationToken)
    {
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT template_document_json FROM IGW_device_templates ORDER BY rowid;";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                project.Templates.Add(ProjectEntityMapper.Deserialize<DeviceTemplate>(reader.GetString(0)));
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT template_document_json FROM IGW_flow_templates ORDER BY rowid;";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                project.MesUpload.FlowTemplates.Add(ProjectEntityMapper.Deserialize<MesFlowTemplate>(reader.GetString(0)));
        }
    }

    private static async Task ReadMesAsync(
        SqliteConnection connection,
        IgwProject project,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT flow_document_json FROM IGW_mes_tasks ORDER BY rowid;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            project.MesUpload.Tasks.Add(ProjectEntityMapper.Deserialize<MesTask>(reader.GetString(0)));
    }

    private static async Task<IReadOnlyList<StoredProjectSecret>> ReadSecretsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken,
        string? channelId = null,
        string? deviceId = null)
    {
        var result = new List<StoredProjectSecret>();
        await using var command = connection.CreateCommand();
        // channelId/deviceId 都给出时,只还原这一个通道 + 这一个设备名下的密钥(LoadDeviceAsync 用),
        // 其余(如项目级 mqtt/mes 密钥)全量加载路径用不到,单设备路径也不需要。
        bool scoped = channelId is not null || deviceId is not null;
        string whereClause = scoped
            ? "WHERE (owner_type='channel' AND owner_id=$channelId) OR (owner_type='device' AND owner_id=$deviceId)"
            : "";
        command.CommandText = $"""
            SELECT owner_type, owner_id, secret_name, ciphertext, key_version
            FROM IGW_secret_entries
            {whereClause}
            ORDER BY owner_type, owner_id, secret_name;
            """;
        if (scoped)
        {
            command.Parameters.AddWithValue("$channelId", (object?)channelId ?? DBNull.Value);
            command.Parameters.AddWithValue("$deviceId", (object?)deviceId ?? DBNull.Value);
        }
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new StoredProjectSecret(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4)));
        return result;
    }

    private static T? ReadNullableJson<T>(SqliteDataReader reader, int ordinal) where T : class =>
        reader.IsDBNull(ordinal) ? null : ProjectEntityMapper.Deserialize<T>(reader.GetString(ordinal));

    private static Dictionary<string, System.Text.Json.JsonElement>? ReadKepPropertiesFast(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        string json = reader.GetString(ordinal);
        // 绝大多数点位没有 KEP 属性(存的是 "{}")，跳过反序列化。
        if (json.Length <= 2) return null;
        Dictionary<string, System.Text.Json.JsonElement> properties =
            ProjectEntityMapper.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(json);
        return properties.Count > 0 ? properties : null;
    }

    private static Dictionary<string, System.Text.Json.JsonElement>? ReadKepProperties(
        SqliteDataReader reader,
        int ordinal)
    {
        Dictionary<string, System.Text.Json.JsonElement>? properties =
            ReadNullableJson<Dictionary<string, System.Text.Json.JsonElement>>(reader, ordinal);
        return properties is { Count: > 0 } ? properties : null;
    }

    private static Dictionary<string, System.Text.Json.JsonElement>? ReadOptionalKepProperties(
        IReadOnlyDictionary<string, string> sections,
        string key)
    {
        if (!sections.TryGetValue(key, out string? document)) return null;
        Dictionary<string, System.Text.Json.JsonElement> properties =
            ProjectEntityMapper.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(document);
        return properties.Count > 0 ? properties : null;
    }

    private static string? GetNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? GetNullableInt32(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private sealed record GroupRow(string DeviceId, string? ParentGroupId, TagGroupDef Group);
}

/// <summary>单设备部分读回的范围：这些根组(整棵子树)，以及是否包含设备根下的点。</summary>
public sealed record DevicePartialScope(IReadOnlyCollection<string> RootGroupNames, bool IncludeRootTags);
