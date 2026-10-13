using IGW.Abstractions;
using IGW.Core;
using Microsoft.Data.Sqlite;

namespace IGW.Persistence.Sqlite;

/// <summary>把一个已验证的完整工程快照写入关系化 SQLite 模式。</summary>
public sealed partial class ProjectSnapshotWriter
{
    private readonly ProjectDatabase _database;
    private readonly ProjectSecretVault _vault;

    public ProjectSnapshotWriter(ProjectDatabase database, ProjectSecretVault vault)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
    }

    public async Task<ProjectRevision> WriteAsync(
        IgwProject project,
        ProjectRevision expected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        IgwProject persisted = ProjectStore.Clone(project);
        EnsureStableIds(persisted);
        ProjectSecrets.HashPendingApiPasswords(persisted);

        await using SqliteConnection connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        IgwProject? previous = null;
        if (expected.Value > 0)
        {
            // Check before reading the baseline, then recheck inside the write transaction.
            // A concurrent commit can never turn a stale comparison into a successful write.
            long baselineRevision = await ReadCurrentRevisionAsync(connection, null, cancellationToken).ConfigureAwait(false);
            if (baselineRevision != expected.Value)
                throw new ProjectRevisionConflictException(new ProjectRevision(baselineRevision));
            previous = await new ProjectSnapshotReader(_database, _vault).ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        IReadOnlyList<StoredProjectSecret> secrets = _vault.Protect(persisted);
        if (previous is not null) _vault.Protect(previous);
        try
        {
            return await WriteCoreAsync(connection, persisted, previous, secrets, expected, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException error) when (previous is not null && error.SqliteErrorCode == 19)
        {
            // 增量按行写入时，同一位置两个点互换名字这类改动会短暂撞上名称唯一约束；
            // 整笔已回滚，改走全量重写一定能得到正确结果。
            return await WriteCoreAsync(connection, persisted, null, secrets, expected, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// previous 不为空时只写改动：拓扑按 id 比对(新增/修改写入、删除的删掉)，其余分区内容不变就不写。
    /// previous 为空(首次写入或增量失败)时清空后全量写入。
    /// </summary>
    private async Task<ProjectRevision> WriteCoreAsync(
        SqliteConnection connection,
        IgwProject persisted,
        IgwProject? previous,
        IReadOnlyList<StoredProjectSecret> secrets,
        ProjectRevision expected,
        CancellationToken cancellationToken)
    {
        Dictionary<string, string>? baseline = previous is null ? null : TopologySignatures(previous);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long current = await ReadCurrentRevisionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            if (current != expected.Value) throw new ProjectRevisionConflictException(new ProjectRevision(current));
            long next = checked(current + 1);

            if (previous is null) await ClearSnapshotAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await InsertMetaAsync(connection, transaction, persisted, next, cancellationToken, previous is not null).ConfigureAwait(false);
            await InsertSectionsAsync(connection, transaction, persisted, cancellationToken, previous).ConfigureAwait(false);
            bool structureChanged = await WriteTopologyDeltaAsync(
                connection, transaction, persisted, baseline, cancellationToken).ConfigureAwait(false);
            if (previous is null || !Same(previous.Templates, persisted.Templates) ||
                !Same(previous.MesUpload.FlowTemplates, persisted.MesUpload.FlowTemplates))
            {
                if (previous is not null) await ClearTablesAsync(connection, transaction,
                    "DELETE FROM IGW_flow_templates; DELETE FROM IGW_device_templates;", cancellationToken).ConfigureAwait(false);
                await InsertTemplatesAsync(connection, transaction, persisted, cancellationToken).ConfigureAwait(false);
            }
            if (previous is null || !Same(previous.MesUpload.Tasks, persisted.MesUpload.Tasks))
            {
                if (previous is not null) await ClearTablesAsync(connection, transaction,
                    "DELETE FROM IGW_mes_task_index; DELETE FROM IGW_mes_tasks;", cancellationToken).ConfigureAwait(false);
                await InsertMesAsync(connection, transaction, persisted, cancellationToken).ConfigureAwait(false);
            }
            else if (structureChanged)
                await RelinkMesReferencesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            if (previous is not null) await ClearTablesAsync(connection, transaction,
                "DELETE FROM IGW_secret_entries;", cancellationToken).ConfigureAwait(false);
            await InsertSecretsAsync(connection, transaction, secrets, cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ProjectRevision(next);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<long> ReadCurrentRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT revision FROM IGW_project_meta WHERE singleton_id=1;";
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? 0L : Convert.ToInt64(value);
    }

    private static async Task ClearSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM IGW_mes_task_index;
            DELETE FROM IGW_mes_tasks;
            DELETE FROM IGW_flow_templates;
            DELETE FROM IGW_device_templates;
            DELETE FROM IGW_tag_options;
            DELETE FROM IGW_tags;
            DELETE FROM IGW_tag_groups;
            DELETE FROM IGW_devices;
            DELETE FROM IGW_channels;
            DELETE FROM IGW_project_sections;
            DELETE FROM IGW_secret_entries;
            DELETE FROM IGW_project_meta;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertMetaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IgwProject project,
        long revision,
        CancellationToken cancellationToken, bool update = false)
    {
        string now = DateTimeOffset.UtcNow.ToString("O");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO IGW_project_meta(
                singleton_id, project_id, name, description, schema_version,
                revision, runtime_generation, created_at, updated_at)
            VALUES(1, 'project', $name, $description, $schemaVersion,
                $revision, $revision, $now, $now);
            """;
        command.Parameters.AddWithValue("$name", project.Name);
        command.Parameters.AddWithValue("$description", DbValue(project.Description));
        command.Parameters.AddWithValue("$schemaVersion", project.SchemaVersion);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$now", now);
        await ExecuteInsertAsync(command, update, cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertSectionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IgwProject project,
        CancellationToken cancellationToken, IgwProject? previous = null)
    {
        if (previous is null || !Same(previous.Settings, project.Settings))
            await InsertSectionAsync(connection, transaction, "settings", project.Settings, cancellationToken, previous is not null).ConfigureAwait(false);
        if (previous is null || !Same(previous.OpcUa, project.OpcUa))
            await InsertSectionAsync(connection, transaction, "opcUa", project.OpcUa, cancellationToken, previous is not null).ConfigureAwait(false);
        if (previous is null || !Same(previous.History, project.History))
            await InsertSectionAsync(connection, transaction, "history", project.History, cancellationToken, previous is not null).ConfigureAwait(false);
        if (previous is null || !Same(previous.MqttPublish, project.MqttPublish))
            await InsertSectionAsync(connection, transaction, "mqttPublish", project.MqttPublish, cancellationToken, previous is not null).ConfigureAwait(false);
        if (previous is null || !Same(previous.ComputedTags, project.ComputedTags))
            await InsertSectionAsync(connection, transaction, "computedTags", project.ComputedTags, cancellationToken, previous is not null).ConfigureAwait(false);
        if (previous is null || !Same(previous.Schedules, project.Schedules))
            await InsertSectionAsync(connection, transaction, "schedules", project.Schedules, cancellationToken, previous is not null).ConfigureAwait(false);
        if (previous is null || !Same(previous.Security, project.Security))
            await InsertSectionAsync(connection, transaction, "security", project.Security, cancellationToken, previous is not null).ConfigureAwait(false);
        if (previous is null || !Same(previous.KepProperties, project.KepProperties))
            await InsertSectionAsync(connection, transaction, "kepProperties", project.KepProperties ?? new(), cancellationToken, previous is not null).ConfigureAwait(false);

        MesUploadSettings mesSettings = ProjectEntityMapper.Deserialize<MesUploadSettings>(
            ProjectEntityMapper.Serialize(project.MesUpload));
        mesSettings.Tasks = [];
        mesSettings.FlowTemplates = [];
        MesUploadSettings? previousSettings = previous is null ? null :
            ProjectEntityMapper.Deserialize<MesUploadSettings>(ProjectEntityMapper.Serialize(previous.MesUpload));
        if (previousSettings is not null) { previousSettings.Tasks = []; previousSettings.FlowTemplates = []; }
        if (previousSettings is null || !Same(previousSettings, mesSettings))
            await InsertSectionAsync(connection, transaction, "mesUpload", mesSettings, cancellationToken, previous is not null).ConfigureAwait(false);
    }

    private static async Task InsertSectionAsync<T>(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        T document,
        CancellationToken cancellationToken, bool update = false)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO IGW_project_sections(section_key, schema_version, document_json, row_version)
            VALUES($key, 1, $document, 1);
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$document", ProjectEntityMapper.Serialize(document));
        await ExecuteInsertAsync(command, update, cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertChannelAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ChannelDef channel,
        int sortOrder,
        CancellationToken cancellationToken, bool update = false)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO IGW_channels(
                id, name, description, driver_id, enabled, settings_json,
                igw_properties_json, kep_properties_json, sort_order, row_version)
            VALUES($id, $name, $description, $driverId, 1, $settings, '{}', $kepProperties, $sortOrder, 1);
            """;
        command.Parameters.AddWithValue("$id", channel.Id);
        command.Parameters.AddWithValue("$name", channel.Name);
        command.Parameters.AddWithValue("$description", DbValue(channel.Description));
        command.Parameters.AddWithValue("$driverId", channel.DriverId);
        command.Parameters.AddWithValue("$settings", ProjectEntityMapper.Serialize(channel.Settings));
        command.Parameters.AddWithValue("$kepProperties", KepJsonValue(channel.KepProperties));
        command.Parameters.AddWithValue("$sortOrder", sortOrder);
        await ExecuteInsertAsync(command, update, cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertDeviceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string channelId,
        DeviceDef device,
        int sortOrder,
        CancellationToken cancellationToken, bool update = false)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO IGW_devices(
                id, channel_id, name, description, endpoint, enabled, template_ref,
                delta_json, settings_json, igw_properties_json, kep_properties_json, sort_order, row_version)
            VALUES($id, $channelId, $name, $description, $endpoint, 1, $templateRef,
                $delta, $settings, '{}', $kepProperties, $sortOrder, 1);
            """;
        command.Parameters.AddWithValue("$id", device.Id);
        command.Parameters.AddWithValue("$channelId", channelId);
        command.Parameters.AddWithValue("$name", device.Name);
        command.Parameters.AddWithValue("$description", DbValue(device.Description));
        command.Parameters.AddWithValue("$endpoint", device.Endpoint);
        command.Parameters.AddWithValue("$templateRef", DbValue(device.TemplateRef));
        command.Parameters.AddWithValue("$delta", device.Delta is null
            ? DBNull.Value : ProjectEntityMapper.Serialize(device.Delta));
        command.Parameters.AddWithValue("$settings", ProjectEntityMapper.Serialize(device.Settings));
        command.Parameters.AddWithValue("$kepProperties", KepJsonValue(device.KepProperties));
        command.Parameters.AddWithValue("$sortOrder", sortOrder);
        await ExecuteInsertAsync(command, update, cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertGroupHeaderAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string deviceId,
        string? parentGroupId,
        TagGroupDef group,
        string path,
        int sortOrder,
        CancellationToken cancellationToken, bool update = false)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO IGW_tag_groups(
                id, device_id, parent_group_id, name, description, canonical_path,
                flow_enabled, igw_properties_json, kep_properties_json, sort_order, row_version)
            VALUES($id, $deviceId, $parentId, $name, $description, $path,
                0, '{}', $kepProperties, $sortOrder, 1);
            """;
        command.Parameters.AddWithValue("$id", group.Id);
        command.Parameters.AddWithValue("$deviceId", deviceId);
        command.Parameters.AddWithValue("$parentId", DbValue(parentGroupId));
        command.Parameters.AddWithValue("$name", group.Name);
        command.Parameters.AddWithValue("$description", DbValue(group.Description));
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$kepProperties", KepJsonValue(group.KepProperties));
        command.Parameters.AddWithValue("$sortOrder", sortOrder);
        await ExecuteInsertAsync(command, update, cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertTagAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string deviceId,
        string? groupId,
        TagDef tag,
        string path,
        int sortOrder,
        CancellationToken cancellationToken, bool update = false)
    {
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO IGW_tags(
                    id, device_id, group_id, name, description, canonical_path, address,
                    data_type, access, scan_rate_ms, string_length, text_encoding, eng_unit,
                    sort_order, row_version)
                VALUES($id, $deviceId, $groupId, $name, $description, $path, $address,
                    $dataType, $access, $scanRate, $stringLength, $textEncoding, $engUnit,
                    $sortOrder, 1);
                """;
            command.Parameters.AddWithValue("$id", tag.Id);
            command.Parameters.AddWithValue("$deviceId", deviceId);
            command.Parameters.AddWithValue("$groupId", DbValue(groupId));
            command.Parameters.AddWithValue("$name", tag.Name);
            command.Parameters.AddWithValue("$description", DbValue(tag.Description));
            command.Parameters.AddWithValue("$path", path);
            command.Parameters.AddWithValue("$address", tag.Address);
            command.Parameters.AddWithValue("$dataType", (int)tag.DataType);
            command.Parameters.AddWithValue("$access", (int)tag.Access);
            command.Parameters.AddWithValue("$scanRate", tag.ScanRateMs);
            command.Parameters.AddWithValue("$stringLength", DbValue(tag.StringLength));
            command.Parameters.AddWithValue("$textEncoding", tag.TextEncoding.HasValue
                ? (int)tag.TextEncoding.Value : DBNull.Value);
            command.Parameters.AddWithValue("$engUnit", DbValue(tag.EngUnit));
            command.Parameters.AddWithValue("$sortOrder", sortOrder);
            await ExecuteInsertAsync(command, update, cancellationToken).ConfigureAwait(false);
        }

        await using (var options = connection.CreateCommand())
        {
            options.Transaction = transaction;
            options.CommandText = """
                INSERT INTO IGW_tag_options(
                    tag_id, is_array, array_json, scaling_json, deadband, deadband_type,
                    alarm_json, write_confirmation_json, metadata_json,
                    igw_properties_json, kep_properties_json)
                VALUES($tagId, $isArray, $array, $scaling, $deadband, $deadbandType,
                    $alarm, $writeConfirmation, $metadata, '{}', $kepProperties);
                """;
            options.Parameters.AddWithValue("$tagId", tag.Id);
            options.Parameters.AddWithValue("$isArray", tag.IsArray ? 1 : 0);
            options.Parameters.AddWithValue("$array", JsonValue(tag.Array));
            options.Parameters.AddWithValue("$scaling", JsonValue(tag.Scaling));
            options.Parameters.AddWithValue("$deadband", tag.Deadband);
            options.Parameters.AddWithValue("$deadbandType", (int)tag.DeadbandType);
            options.Parameters.AddWithValue("$alarm", JsonValue(tag.Alarm));
            options.Parameters.AddWithValue("$writeConfirmation", JsonValue(tag.WriteConfirmation));
            options.Parameters.AddWithValue("$metadata", JsonValue(tag.Metadata));
            options.Parameters.AddWithValue("$kepProperties", KepJsonValue(tag.KepProperties));
            await ExecuteInsertAsync(options, update, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task InsertTemplatesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IgwProject project,
        CancellationToken cancellationToken)
    {
        foreach (DeviceTemplate template in project.Templates)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO IGW_device_templates(id, driver_id, name, description, template_document_json, row_version)
                VALUES($id, $driverId, $name, $description, $document, 1);
                """;
            command.Parameters.AddWithValue("$id", template.Id);
            command.Parameters.AddWithValue("$driverId", template.DriverId);
            command.Parameters.AddWithValue("$name", template.Name);
            command.Parameters.AddWithValue("$description", DbValue(template.Description));
            command.Parameters.AddWithValue("$document", ProjectEntityMapper.Serialize(template));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (MesFlowTemplate template in project.MesUpload.FlowTemplates)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO IGW_flow_templates(id, name, description, template_document_json, row_version)
                VALUES($id, $name, $description, $document, 1);
                """;
            command.Parameters.AddWithValue("$id", template.Id);
            command.Parameters.AddWithValue("$name", template.Name);
            command.Parameters.AddWithValue("$description", DbValue(template.Description));
            command.Parameters.AddWithValue("$document", ProjectEntityMapper.Serialize(template));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task InsertMesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IgwProject project,
        CancellationToken cancellationToken)
    {
        var groups = await ReadGroupIdsByPathAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var tags = await ReadTagIdsByPathAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        foreach (MesTask task in project.MesUpload.Tasks)
        {
            string? groupId = !string.IsNullOrWhiteSpace(task.GroupRef)
                && groups.TryGetValue(task.GroupRef, out string? found) ? found : null;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO IGW_mes_tasks(
                    id, group_id, group_ref, device_ref, enabled, flow_available,
                    name, template_id, flow_document_json, row_version)
                VALUES($id, $groupId, $groupRef, $deviceRef, $enabled, $flowAvailable,
                    $name, NULL, $document, 1);
                """;
            command.Parameters.AddWithValue("$id", task.Id);
            command.Parameters.AddWithValue("$groupId", DbValue(groupId));
            command.Parameters.AddWithValue("$groupRef", DbValue(task.GroupRef));
            command.Parameters.AddWithValue("$deviceRef", DbValue(task.DeviceRef));
            command.Parameters.AddWithValue("$enabled", task.Enabled ? 1 : 0);
            command.Parameters.AddWithValue("$flowAvailable", task.FlowAvailable ? 1 : 0);
            command.Parameters.AddWithValue("$name", task.Name);
            command.Parameters.AddWithValue("$document", ProjectEntityMapper.Serialize(task));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await InsertMesTaskIndexAsync(
                connection, transaction, task, tags, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<Dictionary<string, string>> ReadGroupIdsByPathAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT canonical_path, id FROM IGW_tag_groups;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }

    internal static async Task<Dictionary<string, string>> ReadTagIdsByPathAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT canonical_path, id FROM IGW_tags;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }

    /// <summary>只查询这些任务引用到的点位路径(精确匹配走 ix_tags_path 索引)。</summary>
    internal static async Task<Dictionary<string, string>> ReadTagIdsForTasksAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IEnumerable<MesTask> tasks,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string[] paths = tasks.SelectMany(CollectMesReferences).Select(reference => reference.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0) return result;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id FROM IGW_tags WHERE canonical_path=$path LIMIT 1;";
        SqliteParameter path = command.Parameters.Add("$path", SqliteType.Text);
        foreach (string item in paths)
        {
            path.Value = item;
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string id) result[item] = id;
        }
        return result;
    }

    internal static async Task InsertMesTaskIndexAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MesTask task,
        IReadOnlyDictionary<string, string> tags,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<MesReference> references = CollectMesReferences(task);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO IGW_mes_task_index(task_id, node_type, referenced_tag_id, referenced_path)
            VALUES($taskId, $nodeType, $tagId, $path);
            """;
        command.Parameters.AddWithValue("$taskId", task.Id);
        SqliteParameter nodeType = command.Parameters.Add("$nodeType", SqliteType.Text);
        SqliteParameter tagId = command.Parameters.Add("$tagId", SqliteType.Text);
        SqliteParameter path = command.Parameters.Add("$path", SqliteType.Text);

        foreach (MesReference reference in references)
        {
            nodeType.Value = reference.NodeType;
            path.Value = reference.Path;
            tagId.Value = tags.TryGetValue(reference.Path, out string? resolvedId)
                ? resolvedId
                : DBNull.Value;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static IReadOnlyList<MesReference> CollectMesReferences(MesTask task)
    {
        var references = new HashSet<MesReference>();
        AddReference(references, "Trigger", task.Trigger.TagId);
        AddPayloadReferences(references, task.Payload, "Payload");
        AddReference(references, "Heartbeat", task.Heartbeat.TagId);
        AddReference(references, "Handshake", task.Handshake.MesFlagTagId);
        AddReference(references, "Handshake", task.Handshake.PlcFlagTagId);

        foreach (MesStep step in task.Steps)
        {
            foreach (MesCondition condition in step.Conditions)
                if (condition.Source == "Tag") AddReference(references, "StepCondition", condition.TagId);
            AddWritebackReferences(references, step.WriteBack, "StepWriteback");
        }

        if (task.Flow is not null)
        {
            foreach (MesNode node in task.Flow.Nodes)
            {
                foreach (MesCondition condition in node.Conditions)
                    if (condition.Source == "Tag") AddReference(references, "FlowCondition", condition.TagId);
                foreach (MesCondBranch branch in node.Branches)
                    foreach (MesCondition condition in branch.Conditions)
                        if (condition.Source == "Tag") AddReference(references, "FlowCondition", condition.TagId);
                AddPayloadReferences(references, node.Payload, "FlowPayload");
                AddReference(references, "FlowSwitch", node.SwitchTag);
                AddWritebackReferences(references, node.WriteBack, "FlowWriteback");
            }
        }

        return references.OrderBy(item => item.NodeType, StringComparer.Ordinal)
            .ThenBy(item => item.Path, StringComparer.Ordinal)
            .ToArray();
    }

    private static void AddPayloadReferences(
        ISet<MesReference> references,
        MesPayload payload,
        string prefix)
    {
        foreach (MesDataRange range in payload.DataRanges)
            AddReference(references, prefix == "Payload" ? "DataRange" : prefix + "DataRange", range.SourcePath);
        foreach (MesField field in payload.Fields)
            AddReference(references, prefix + "Field", field.TagId);
        foreach (var field in payload.ExtraFields.Where(field => field.Source == "Tag"))
            AddReference(references, prefix + "ExtraField", field.Path);
        foreach (MesStruct structure in payload.Structs)
        {
            AddReference(references, prefix + "Struct", structure.BaseGroup);
            AddReference(references, prefix + "StructCount", structure.CountTagId);
            foreach (MesField member in structure.Members)
                AddReference(references, prefix + "StructMember", member.TagId);
        }
    }

    private static void AddWritebackReferences(
        ISet<MesReference> references,
        MesWriteBack writeback,
        string nodeType)
    {
        foreach (MesResponseWrite item in writeback.FromResponse)
            AddReference(references, nodeType, item.TagId);
        foreach (MesManualWrite item in writeback.Manual)
        {
            AddReference(references, nodeType, item.TagId);
            AddReference(references, nodeType + "CopySource", item.CopyFromTagId);
        }
    }

    private static void AddReference(ISet<MesReference> references, string nodeType, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            references.Add(new MesReference(nodeType, path.Trim()));
    }

    private static async Task InsertSecretsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<StoredProjectSecret> secrets,
        CancellationToken cancellationToken)
    {
        foreach (StoredProjectSecret secret in secrets)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO IGW_secret_entries(
                    owner_type, owner_id, secret_name, ciphertext, nonce, authentication_tag, key_version)
                VALUES($ownerType, $ownerId, $secretName, $ciphertext, NULL, NULL, $keyVersion);
                """;
            command.Parameters.AddWithValue("$ownerType", secret.OwnerType);
            command.Parameters.AddWithValue("$ownerId", secret.OwnerId);
            command.Parameters.AddWithValue("$secretName", secret.SecretName);
            command.Parameters.AddWithValue("$ciphertext", secret.Ciphertext);
            command.Parameters.AddWithValue("$keyVersion", secret.KeyVersion);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static void EnsureStableIds(IgwProject project)
    {
        // 拓扑按 id 差异写入：同一张表里 id 重复(如复制粘贴带出原 id)时，后一份会覆盖前一份而静默丢失。
        // 这里给重复出现的那一份换新 id，先出现的保留原 id(对应库里已有的行)。
        var channelIds = new HashSet<string>(StringComparer.Ordinal);
        var deviceIds = new HashSet<string>(StringComparer.Ordinal);
        var groupIds = new HashSet<string>(StringComparer.Ordinal);
        var tagIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (ChannelDef channel in project.Channels)
        {
            channel.Id = UniqueId(channel.Id, channelIds);
            foreach (DeviceDef device in channel.Devices)
            {
                device.Id = UniqueId(device.Id, deviceIds);
                EnsureTagIds(device.Tags, tagIds);
                EnsureGroupIds(device.Groups, groupIds, tagIds);
            }
        }

        foreach (DeviceTemplate template in project.Templates)
        {
            template.Id = ProjectEntityMapper.EnsureId(template.Id);
            EnsureGroupIds(template.Groups, null, null);
            EnsureTagIds(template.Tags, null);
        }
        foreach (MesTask task in project.MesUpload.Tasks)
            task.Id = ProjectEntityMapper.EnsureId(task.Id);
        foreach (MesFlowTemplate template in project.MesUpload.FlowTemplates)
            template.Id = ProjectEntityMapper.EnsureId(template.Id);
    }

    private static string UniqueId(string? id, HashSet<string>? seen)
    {
        string value = ProjectEntityMapper.EnsureId(id);
        if (seen is null) return value;
        while (!seen.Add(value)) value = ProjectEntityMapper.EnsureId(null);
        return value;
    }

    private static void EnsureGroupIds(IEnumerable<TagGroupDef> groups, HashSet<string>? groupIds, HashSet<string>? tagIds)
    {
        foreach (TagGroupDef group in groups)
        {
            group.Id = UniqueId(group.Id, groupIds);
            EnsureTagIds(group.Tags, tagIds);
            EnsureGroupIds(group.SubGroups, groupIds, tagIds);
        }
    }

    private static void EnsureTagIds(IEnumerable<TagDef> tags, HashSet<string>? tagIds)
    {
        foreach (TagDef tag in tags)
            tag.Id = UniqueId(tag.Id, tagIds);
    }

    private static object DbValue(object? value) => value ?? DBNull.Value;
    private static object JsonValue<T>(T? value) where T : class =>
        value is null ? DBNull.Value : ProjectEntityMapper.Serialize(value);
    private static string KepJsonValue<T>(T? value) where T : class =>
        value is null ? "{}" : ProjectEntityMapper.Serialize(value);

    private sealed record MesReference(string NodeType, string Path);
}
