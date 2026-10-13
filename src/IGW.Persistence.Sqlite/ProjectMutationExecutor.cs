using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using Microsoft.Data.Sqlite;

namespace IGW.Persistence.Sqlite;

/// <summary>在一个事务内执行局部工程变更，并只递增一次全局修订号。</summary>
public sealed class ProjectMutationExecutor
{
    private readonly ProjectDatabase _database;
    private readonly ProjectSecretVault _vault;

    public ProjectMutationExecutor(ProjectDatabase database, ProjectSecretVault vault)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
    }

    public async Task<ProjectMutationResult> ExecuteAsync(
        ProjectMutation mutation,
        ProjectRevision expected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        await using SqliteConnection connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        long current = await SqliteProjectRepository.ReadRevisionAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        if (current != expected.Value)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new ProjectMutationResult(false, true, new ProjectRevision(current), "工程已被其他操作修改，请刷新后重试。");
        }

        try
        {
            int changed = 0;
            if (mutation.Kind == ProjectMutationKind.Batch)
            {
                // 一组修改在同一事务里依次执行：任何一项失败，整批回滚，工程保持原样。
                List<ProjectMutation> items = JsonSerializer.Deserialize<List<ProjectMutation>>(
                    mutation.DocumentJson ?? "[]", ProjectStore.Options)
                    ?? throw new InvalidDataException("批量修改为空。");
                if (items.Count == 0) throw new InvalidDataException("批量修改为空。");
                foreach (ProjectMutation item in items)
                {
                    if (item.Kind == ProjectMutationKind.Batch) throw new InvalidDataException("批量修改不能嵌套。");
                    changed += await ExecuteOneAsync(connection, transaction, item, cancellationToken).ConfigureAwait(false);
                }
            }
            else changed = await ExecuteOneAsync(connection, transaction, mutation, cancellationToken).ConfigureAwait(false);

            long next = checked(current + 1);
            await using (var revision = connection.CreateCommand())
            {
                revision.Transaction = transaction;
                revision.CommandText = """
                    UPDATE IGW_project_meta
                    SET revision=$revision, runtime_generation=$revision, updated_at=$updatedAt
                    WHERE singleton_id=1;
                    """;
                revision.Parameters.AddWithValue("$revision", next);
                revision.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
                await revision.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ProjectMutationResult(true, false, new ProjectRevision(next), null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or JsonException or SqliteException)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return new ProjectMutationResult(false, false, new ProjectRevision(current), FriendlyError(ex));
        }
    }

    private static async Task<int> MoveAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProjectMutation mutation,
        CancellationToken cancellationToken)
    {
        string targetId = mutation.TargetId
            ?? throw new InvalidOperationException("移动操作缺少目标标记组。");
        await PopulateIdsAsync(connection, transaction, mutation.EntityIds, cancellationToken).ConfigureAwait(false);
        return mutation.EntityKind switch
        {
            ProjectNodeKind.Tag => await MoveTagsAsync(connection, transaction, targetId, cancellationToken)
                .ConfigureAwait(false),
            ProjectNodeKind.Group => await MoveGroupsAsync(connection, transaction, targetId, cancellationToken)
                .ConfigureAwait(false),
            _ => throw new InvalidOperationException("目前仅标记和标记组支持移动。"),
        };
    }

    private static async Task<int> CopyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProjectMutation mutation,
        CancellationToken cancellationToken)
    {
        if (mutation.EntityKind != ProjectNodeKind.Tag)
            throw new InvalidOperationException("目前仅点位支持批量复制。");
        string targetGroupId = mutation.TargetId
            ?? throw new InvalidOperationException("复制操作缺少目标标记组。");
        await PopulateIdsAsync(connection, transaction, mutation.EntityIds, cancellationToken).ConfigureAwait(false);
        await EnsureAllIdsExistAsync(connection, transaction, "IGW_tags", cancellationToken).ConfigureAwait(false);
        (string deviceId, string targetPath) = await ReadGroupLocationAsync(
            connection, transaction, targetGroupId, cancellationToken).ConfigureAwait(false);

        int copied = 0;
        foreach (string sourceId in mutation.EntityIds.Distinct(StringComparer.Ordinal))
        {
            string sourceName = await ReadOptionalScalarAsync(
                connection, transaction, "SELECT name FROM IGW_tags WHERE id=$id;", sourceId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException($"找不到待复制点位 {sourceId}。");
            string name = await UniqueTagNameAsync(
                connection, transaction, targetGroupId, sourceName, cancellationToken).ConfigureAwait(false);
            string newId = Guid.NewGuid().ToString("N");
            await using (var tag = connection.CreateCommand())
            {
                tag.Transaction = transaction;
                tag.CommandText = """
                    INSERT INTO IGW_tags(id,device_id,group_id,name,description,canonical_path,address,data_type,access,
                        scan_rate_ms,string_length,text_encoding,eng_unit,sort_order,row_version)
                    SELECT $newId,$deviceId,$groupId,$name,description,$path || '.' || $name,address,data_type,access,
                        scan_rate_ms,string_length,text_encoding,eng_unit,
                        COALESCE((SELECT MAX(sort_order)+1 FROM IGW_tags WHERE device_id=$deviceId AND group_id=$groupId),0),1
                    FROM IGW_tags WHERE id=$sourceId;
                    """;
                tag.Parameters.AddWithValue("$newId", newId);
                tag.Parameters.AddWithValue("$deviceId", deviceId);
                tag.Parameters.AddWithValue("$groupId", targetGroupId);
                tag.Parameters.AddWithValue("$name", name);
                tag.Parameters.AddWithValue("$path", targetPath);
                tag.Parameters.AddWithValue("$sourceId", sourceId);
                copied += await tag.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await using var options = connection.CreateCommand();
            options.Transaction = transaction;
            options.CommandText = """
                INSERT INTO IGW_tag_options(tag_id,is_array,array_json,scaling_json,deadband,deadband_type,
                    alarm_json,write_confirmation_json,metadata_json)
                SELECT $newId,is_array,array_json,scaling_json,deadband,deadband_type,
                    alarm_json,write_confirmation_json,metadata_json
                FROM IGW_tag_options WHERE tag_id=$sourceId;
                """;
            options.Parameters.AddWithValue("$newId", newId);
            options.Parameters.AddWithValue("$sourceId", sourceId);
            await options.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        return copied;
    }

    private static async Task<string> UniqueTagNameAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string targetGroupId,
        string sourceName,
        CancellationToken cancellationToken)
    {
        for (int copy = 0; ; copy++)
        {
            string candidate = copy switch
            {
                0 => sourceName,
                1 => sourceName + " Copy",
                _ => sourceName + $" Copy {copy}",
            };
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT COUNT(*) FROM IGW_tags WHERE group_id=$groupId AND name=$name COLLATE NOCASE;";
            command.Parameters.AddWithValue("$groupId", targetGroupId);
            command.Parameters.AddWithValue("$name", candidate);
            if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 0)
                return candidate;
        }
    }

    private static async Task<int> MoveTagsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string targetGroupId,
        CancellationToken cancellationToken)
    {
        (string deviceId, string path) = await ReadGroupLocationAsync(
            connection, transaction, targetGroupId, cancellationToken).ConfigureAwait(false);
        await EnsureAllIdsExistAsync(connection, transaction, "IGW_tags", cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE IGW_tags
            SET group_id=$groupId, device_id=$deviceId,
                canonical_path=$path || '.' || name, row_version=row_version+1
            WHERE id IN (SELECT id FROM IGW_mutation_ids);
            """;
        command.Parameters.AddWithValue("$groupId", targetGroupId);
        command.Parameters.AddWithValue("$deviceId", deviceId);
        command.Parameters.AddWithValue("$path", path);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> MoveGroupsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string targetId,
        CancellationToken cancellationToken)
    {
        GroupParent target = await ResolveGroupParentAsync(
            connection, transaction, targetId, string.Empty, cancellationToken).ConfigureAwait(false);
        await EnsureAllIdsExistAsync(connection, transaction, "IGW_tag_groups", cancellationToken).ConfigureAwait(false);

        var roots = new List<(string Id, string Name, string OldPath)>();
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT id,name,canonical_path FROM IGW_tag_groups
                WHERE id IN (SELECT id FROM IGW_mutation_ids) ORDER BY canonical_path;
                """;
            await using SqliteDataReader reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                roots.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        foreach ((string id, _, string oldPath) in roots)
        {
            if (string.Equals(id, target.ParentGroupId, StringComparison.Ordinal)
                || target.Path.StartsWith(oldPath + ".", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("不能把标记组移动到自身或自身的下级组。");
        }

        foreach ((string id, string name, string oldPath) in roots)
        {
            string newPath = target.Path + "." + name;
            await using (var root = connection.CreateCommand())
            {
                root.Transaction = transaction;
                root.CommandText = """
                    UPDATE IGW_tag_groups
                    SET parent_group_id=$parentId, device_id=$deviceId,
                        canonical_path=$newPath, row_version=row_version+1
                    WHERE id=$id;
                    """;
                root.Parameters.AddWithValue("$parentId", DbValue(target.ParentGroupId));
                root.Parameters.AddWithValue("$deviceId", target.DeviceId);
                root.Parameters.AddWithValue("$newPath", newPath);
                root.Parameters.AddWithValue("$id", id);
                await root.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await ReplacePathPrefixAsync(connection, transaction, "IGW_tag_groups", target.DeviceId, oldPath, newPath, id, cancellationToken)
                .ConfigureAwait(false);
            await ReplacePathPrefixAsync(connection, transaction, "IGW_tags", target.DeviceId, oldPath, newPath, null, cancellationToken)
                .ConfigureAwait(false);
            await MigrateMesTaskReferencesAsync(connection, transaction, oldPath, newPath, cancellationToken)
                .ConfigureAwait(false);
        }
        return roots.Count;
    }

    private static async Task MigrateMesTaskReferencesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string oldPath,
        string newPath,
        CancellationToken cancellationToken)
    {
        var tasks = new List<MesTask>();
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT flow_document_json FROM IGW_mes_tasks;";
            await using SqliteDataReader reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                tasks.Add(ProjectEntityMapper.Deserialize<MesTask>(reader.GetString(0)));
        }

        foreach (MesTask task in tasks)
        {
            if (MesReferenceMigration.Rewrite(task, oldPath, newPath) == 0) continue;
            MesFlowCommandValidation.Validate(task);
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE IGW_mes_tasks
                SET group_ref=$groupRef, device_ref=$deviceRef,
                    flow_document_json=$document, row_version=row_version+1
                WHERE id=$taskId;
                UPDATE IGW_mes_task_index
                SET referenced_path=$newPath || substr(referenced_path, length($oldPath)+1)
                WHERE task_id=$taskId
                  AND (referenced_path=$oldPath OR referenced_path LIKE $escapedPrefix ESCAPE '\');
                """;
            update.Parameters.AddWithValue("$groupRef", DbValue(task.GroupRef));
            update.Parameters.AddWithValue("$deviceRef", DbValue(task.DeviceRef));
            update.Parameters.AddWithValue("$document", ProjectEntityMapper.Serialize(task));
            update.Parameters.AddWithValue("$taskId", task.Id);
            update.Parameters.AddWithValue("$oldPath", oldPath);
            update.Parameters.AddWithValue("$newPath", newPath);
            update.Parameters.AddWithValue("$escapedPrefix", EscapeLike(oldPath) + ".%");
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ReplacePathPrefixAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string deviceId,
        string oldPath,
        string newPath,
        string? excludedId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            UPDATE {table}
            SET device_id=$deviceId,
                canonical_path=$newPath || substr(canonical_path, length($oldPath)+1),
                row_version=row_version+1
            WHERE canonical_path LIKE $escapedPrefix ESCAPE '\'
              AND ($excludedId IS NULL OR id<>$excludedId);
            """;
        command.Parameters.AddWithValue("$deviceId", deviceId);
        command.Parameters.AddWithValue("$newPath", newPath);
        command.Parameters.AddWithValue("$oldPath", oldPath);
        command.Parameters.AddWithValue("$escapedPrefix", EscapeLike(oldPath) + ".%");
        command.Parameters.AddWithValue("$excludedId", (object?)excludedId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> DeleteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProjectMutation mutation,
        CancellationToken cancellationToken)
    {
        string table = mutation.EntityKind switch
        {
            ProjectNodeKind.Channel => "IGW_channels",
            ProjectNodeKind.Device => "IGW_devices",
            ProjectNodeKind.Group => "IGW_tag_groups",
            ProjectNodeKind.Tag => "IGW_tags",
            _ => throw new InvalidOperationException("不能删除工程根节点。"),
        };
        await PopulateIdsAsync(connection, transaction, mutation.EntityIds, cancellationToken).ConfigureAwait(false);
        await EnsureAllIdsExistAsync(connection, transaction, table, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"DELETE FROM {table} WHERE id IN (SELECT id FROM IGW_mutation_ids);";
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> SetEnabledAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProjectMutation mutation,
        CancellationToken cancellationToken)
    {
        bool enabled = ParseBoolean(mutation.DocumentJson);
        (string table, string column) = mutation.EntityKind switch
        {
            ProjectNodeKind.Channel => ("IGW_channels", "enabled"),
            ProjectNodeKind.Device => ("IGW_devices", "enabled"),
            ProjectNodeKind.Group => ("IGW_tag_groups", "flow_enabled"),
            _ => throw new InvalidOperationException("该节点不支持启停。"),
        };
        await PopulateIdsAsync(connection, transaction, mutation.EntityIds, cancellationToken).ConfigureAwait(false);
        await EnsureAllIdsExistAsync(connection, transaction, table, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"UPDATE {table} SET {column}=$enabled,row_version=row_version+1 WHERE id IN (SELECT id FROM IGW_mutation_ids);";
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> ExecuteOneAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProjectMutation mutation,
        CancellationToken cancellationToken)
    {
        int changed = mutation.Kind switch
        {
            ProjectMutationKind.MoveEntities => await MoveAsync(connection, transaction, mutation, cancellationToken)
                .ConfigureAwait(false),
            ProjectMutationKind.CopyEntities => await CopyAsync(connection, transaction, mutation, cancellationToken)
                .ConfigureAwait(false),
            ProjectMutationKind.DeleteEntities => await DeleteAsync(connection, transaction, mutation, cancellationToken)
                .ConfigureAwait(false),
            ProjectMutationKind.SetEnabled => await SetEnabledAsync(connection, transaction, mutation, cancellationToken)
                .ConfigureAwait(false),
            ProjectMutationKind.UpsertEntity => await UpsertAsync(connection, transaction, mutation, cancellationToken)
                .ConfigureAwait(false),
            ProjectMutationKind.ReplaceMesTask => await ReplaceMesTaskAsync(connection, transaction, mutation, cancellationToken)
                .ConfigureAwait(false),
            ProjectMutationKind.SaveMesTasks => await SaveMesTasksAsync(connection, transaction, mutation, cancellationToken)
                .ConfigureAwait(false),
            ProjectMutationKind.ReplaceProjectSections => await ReplaceProjectSectionsAsync(connection, transaction, mutation, cancellationToken)
                .ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        // Saving MES drafts can legitimately touch no enabled rows (for example when
        // a group edit is batched with a disabled flow). Other operations still need
        // to match an entity so a stale or invalid target is not accepted silently.
        if (changed == 0 && mutation.Kind != ProjectMutationKind.SaveMesTasks)
            throw new InvalidOperationException("本次操作没有匹配到任何工程节点。");
        return changed;
    }

    private async Task<int> UpsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProjectMutation mutation,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(mutation.DocumentJson))
            throw new InvalidDataException("新增或更新节点缺少文档内容。");
        return mutation.EntityKind switch
        {
            ProjectNodeKind.Channel => await UpsertChannelAsync(connection, transaction,
                ProjectEntityMapper.Deserialize<ChannelDef>(mutation.DocumentJson), cancellationToken).ConfigureAwait(false),
            ProjectNodeKind.Device => await UpsertDeviceAsync(connection, transaction, mutation.TargetId,
                ProjectEntityMapper.Deserialize<DeviceDef>(mutation.DocumentJson), cancellationToken).ConfigureAwait(false),
            ProjectNodeKind.Group => await UpsertGroupAsync(connection, transaction, mutation.TargetId,
                ProjectEntityMapper.Deserialize<TagGroupDef>(mutation.DocumentJson), cancellationToken).ConfigureAwait(false),
            ProjectNodeKind.Tag => await UpsertTagAsync(connection, transaction, mutation.TargetId,
                ProjectEntityMapper.Deserialize<TagDef>(mutation.DocumentJson), cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException("不能局部替换工程根节点。"),
        };
    }

    internal async Task<int> UpsertChannelAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ChannelDef channel,
        CancellationToken cancellationToken)
    {
        channel.Id = ProjectEntityMapper.EnsureId(channel.Id);
        EnsureNoChildren(channel.Devices.Count, "通道");
        IReadOnlyList<StoredProjectSecret> secrets = _vault.Protect(new IgwProject { Channels = [channel] });
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO IGW_channels(id,name,description,driver_id,enabled,settings_json,kep_properties_json,sort_order,row_version)
            VALUES($id,$name,$description,$driverId,1,$settings,$kep,
                COALESCE((SELECT MAX(sort_order)+1 FROM IGW_channels),0),1)
            ON CONFLICT(id) DO UPDATE SET name=excluded.name,description=excluded.description,
                driver_id=excluded.driver_id,settings_json=excluded.settings_json,
                kep_properties_json=excluded.kep_properties_json,row_version=IGW_channels.row_version+1;
            """;
        command.Parameters.AddWithValue("$id", channel.Id);
        command.Parameters.AddWithValue("$name", channel.Name);
        command.Parameters.AddWithValue("$description", DbValue(channel.Description));
        command.Parameters.AddWithValue("$driverId", channel.DriverId);
        command.Parameters.AddWithValue("$settings", ProjectEntityMapper.Serialize(channel.Settings));
        command.Parameters.AddWithValue("$kep", KepJsonValue(channel.KepProperties));
        int changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await PersistEntitySecretsAsync(
            connection, transaction, "channel", channel.Id, channel.Settings, secrets, cancellationToken)
            .ConfigureAwait(false);
        return changed;
    }

    internal async Task<int> UpsertDeviceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? targetChannelId,
        DeviceDef device,
        CancellationToken cancellationToken)
    {
        device.Id = ProjectEntityMapper.EnsureId(device.Id);
        EnsureNoChildren(device.Groups.Count + device.Tags.Count, "设备");
        IReadOnlyList<StoredProjectSecret> secrets = _vault.Protect(new IgwProject
        {
            Channels = [new ChannelDef { Id = "mutation", Name = "mutation", Devices = [device] }],
        });
        string channelId = targetChannelId ?? await ReadExistingParentAsync(
            connection, transaction, "IGW_devices", "channel_id", device.Id, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO IGW_devices(id,channel_id,name,description,endpoint,enabled,template_ref,delta_json,settings_json,kep_properties_json,sort_order,row_version)
            VALUES($id,$parent,$name,$description,$endpoint,1,$templateRef,$delta,$settings,$kep,
                COALESCE((SELECT MAX(sort_order)+1 FROM IGW_devices WHERE channel_id=$parent),0),1)
            ON CONFLICT(id) DO UPDATE SET channel_id=excluded.channel_id,name=excluded.name,
                description=excluded.description,endpoint=excluded.endpoint,template_ref=excluded.template_ref,
                delta_json=excluded.delta_json,settings_json=excluded.settings_json,
                kep_properties_json=excluded.kep_properties_json,row_version=IGW_devices.row_version+1;
            """;
        command.Parameters.AddWithValue("$id", device.Id);
        command.Parameters.AddWithValue("$parent", channelId);
        command.Parameters.AddWithValue("$name", device.Name);
        command.Parameters.AddWithValue("$description", DbValue(device.Description));
        command.Parameters.AddWithValue("$endpoint", device.Endpoint);
        command.Parameters.AddWithValue("$templateRef", DbValue(device.TemplateRef));
        command.Parameters.AddWithValue("$delta", device.Delta is null ? DBNull.Value : ProjectEntityMapper.Serialize(device.Delta));
        command.Parameters.AddWithValue("$settings", ProjectEntityMapper.Serialize(device.Settings));
        command.Parameters.AddWithValue("$kep", KepJsonValue(device.KepProperties));
        int changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await PersistEntitySecretsAsync(
            connection, transaction, "device", device.Id, device.Settings, secrets, cancellationToken)
            .ConfigureAwait(false);
        return changed;
    }

    private static async Task<int> UpsertGroupAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? targetId,
        TagGroupDef group,
        CancellationToken cancellationToken)
    {
        group.Id = ProjectEntityMapper.EnsureId(group.Id);
        EnsureNoChildren(group.SubGroups.Count + group.Tags.Count, "标记组");
        GroupParent parent = await ResolveGroupParentAsync(
            connection, transaction, targetId, group.Id, cancellationToken).ConfigureAwait(false);
        string newPath = parent.Path + "." + group.Name;
        string? oldPath = await ReadOptionalScalarAsync(connection, transaction,
            "SELECT canonical_path FROM IGW_tag_groups WHERE id=$id;", group.Id, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO IGW_tag_groups(id,device_id,parent_group_id,name,description,canonical_path,flow_enabled,kep_properties_json,sort_order,row_version)
            VALUES($id,$deviceId,$parentId,$name,$description,$path,0,$kep,
                COALESCE((SELECT MAX(sort_order)+1 FROM IGW_tag_groups
                    WHERE device_id=$deviceId AND parent_group_id IS $parentId),0),1)
            ON CONFLICT(id) DO UPDATE SET device_id=excluded.device_id,parent_group_id=excluded.parent_group_id,
                name=excluded.name,description=excluded.description,canonical_path=excluded.canonical_path,
                kep_properties_json=excluded.kep_properties_json,row_version=IGW_tag_groups.row_version+1;
            """;
        command.Parameters.AddWithValue("$id", group.Id);
        command.Parameters.AddWithValue("$deviceId", parent.DeviceId);
        command.Parameters.AddWithValue("$parentId", DbValue(parent.ParentGroupId));
        command.Parameters.AddWithValue("$name", group.Name);
        command.Parameters.AddWithValue("$description", DbValue(group.Description));
        command.Parameters.AddWithValue("$path", newPath);
        command.Parameters.AddWithValue("$kep", KepJsonValue(group.KepProperties));
        int changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (oldPath is not null && !string.Equals(oldPath, newPath, StringComparison.Ordinal))
        {
            await ReplacePathPrefixAsync(connection, transaction, "IGW_tag_groups", parent.DeviceId, oldPath, newPath, group.Id, cancellationToken)
                .ConfigureAwait(false);
            await ReplacePathPrefixAsync(connection, transaction, "IGW_tags", parent.DeviceId, oldPath, newPath, null, cancellationToken)
                .ConfigureAwait(false);
        }
        return changed;
    }

    private static async Task<int> UpsertTagAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? targetId,
        TagDef tag,
        CancellationToken cancellationToken)
    {
        tag.Id = ProjectEntityMapper.EnsureId(tag.Id);
        TagParent parent = await ResolveTagParentAsync(connection, transaction, targetId, tag.Id, cancellationToken)
            .ConfigureAwait(false);
        // tags/tag_options 列清单在 ProjectBulkTagWriter 中有镜像，表结构变更时两处须同步修改。
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO IGW_tags(id,device_id,group_id,name,description,canonical_path,address,data_type,access,
                    scan_rate_ms,string_length,text_encoding,eng_unit,sort_order,row_version)
                VALUES($id,$deviceId,$groupId,$name,$description,$path,$address,$dataType,$access,
                    $scanRate,$stringLength,$textEncoding,$engUnit,
                    COALESCE((SELECT MAX(sort_order)+1 FROM IGW_tags WHERE device_id=$deviceId AND group_id IS $groupId),0),1)
                ON CONFLICT(id) DO UPDATE SET device_id=excluded.device_id,group_id=excluded.group_id,
                    name=excluded.name,description=excluded.description,canonical_path=excluded.canonical_path,
                    address=excluded.address,data_type=excluded.data_type,access=excluded.access,
                    scan_rate_ms=excluded.scan_rate_ms,string_length=excluded.string_length,
                    text_encoding=excluded.text_encoding,eng_unit=excluded.eng_unit,row_version=IGW_tags.row_version+1;
                """;
            command.Parameters.AddWithValue("$id", tag.Id);
            command.Parameters.AddWithValue("$deviceId", parent.DeviceId);
            command.Parameters.AddWithValue("$groupId", DbValue(parent.GroupId));
            command.Parameters.AddWithValue("$name", tag.Name);
            command.Parameters.AddWithValue("$description", DbValue(tag.Description));
            command.Parameters.AddWithValue("$path", parent.Path + "." + tag.Name);
            command.Parameters.AddWithValue("$address", tag.Address);
            command.Parameters.AddWithValue("$dataType", (int)tag.DataType);
            command.Parameters.AddWithValue("$access", (int)tag.Access);
            command.Parameters.AddWithValue("$scanRate", tag.ScanRateMs);
            command.Parameters.AddWithValue("$stringLength", DbValue(tag.StringLength));
            command.Parameters.AddWithValue("$textEncoding", tag.TextEncoding.HasValue ? (int)tag.TextEncoding.Value : DBNull.Value);
            command.Parameters.AddWithValue("$engUnit", DbValue(tag.EngUnit));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using var options = connection.CreateCommand();
        options.Transaction = transaction;
        options.CommandText = """
            INSERT INTO IGW_tag_options(tag_id,is_array,array_json,scaling_json,deadband,deadband_type,
                alarm_json,write_confirmation_json,metadata_json,kep_properties_json)
            VALUES($id,$isArray,$array,$scaling,$deadband,$deadbandType,$alarm,$confirmation,$metadata,$kep)
            ON CONFLICT(tag_id) DO UPDATE SET is_array=excluded.is_array,array_json=excluded.array_json,
                scaling_json=excluded.scaling_json,deadband=excluded.deadband,deadband_type=excluded.deadband_type,
                alarm_json=excluded.alarm_json,write_confirmation_json=excluded.write_confirmation_json,
                metadata_json=excluded.metadata_json,kep_properties_json=excluded.kep_properties_json;
            """;
        options.Parameters.AddWithValue("$id", tag.Id);
        options.Parameters.AddWithValue("$isArray", tag.IsArray ? 1 : 0);
        options.Parameters.AddWithValue("$array", JsonValue(tag.Array));
        options.Parameters.AddWithValue("$scaling", JsonValue(tag.Scaling));
        options.Parameters.AddWithValue("$deadband", tag.Deadband);
        options.Parameters.AddWithValue("$deadbandType", (int)tag.DeadbandType);
        options.Parameters.AddWithValue("$alarm", JsonValue(tag.Alarm));
        options.Parameters.AddWithValue("$confirmation", JsonValue(tag.WriteConfirmation));
        options.Parameters.AddWithValue("$metadata", JsonValue(tag.Metadata));
        options.Parameters.AddWithValue("$kep", KepJsonValue(tag.KepProperties));
        return await options.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ReplaceMesTaskAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProjectMutation mutation,
        CancellationToken cancellationToken,
        Dictionary<string, string>? sharedTagIds = null)
    {
        if (string.IsNullOrWhiteSpace(mutation.DocumentJson))
            throw new InvalidDataException("替换 MES 任务缺少任务文档。");
        MesTask task = ProjectEntityMapper.Deserialize<MesTask>(mutation.DocumentJson);
        MesFlowCommandValidation.Validate(task);
        task.Id = ProjectEntityMapper.EnsureId(task.Id);
        string? groupId = null;
        if (!string.IsNullOrWhiteSpace(task.GroupRef))
            groupId = await ReadOptionalScalarAsync(connection, transaction,
                "SELECT id FROM IGW_tag_groups WHERE canonical_path=$id;", task.GroupRef, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO IGW_mes_tasks(id,group_id,group_ref,device_ref,enabled,flow_available,name,template_id,flow_document_json,row_version)
            VALUES($id,$groupId,$groupRef,$deviceRef,$enabled,$flowAvailable,$name,NULL,$document,1)
            ON CONFLICT(id) DO UPDATE SET group_id=excluded.group_id,group_ref=excluded.group_ref,
                device_ref=excluded.device_ref,enabled=excluded.enabled,flow_available=excluded.flow_available,
                name=excluded.name,flow_document_json=excluded.flow_document_json,row_version=IGW_mes_tasks.row_version+1;
            DELETE FROM IGW_mes_task_index WHERE task_id=$id;
            """;
        command.Parameters.AddWithValue("$id", task.Id);
        command.Parameters.AddWithValue("$groupId", DbValue(groupId));
        command.Parameters.AddWithValue("$groupRef", DbValue(task.GroupRef));
        command.Parameters.AddWithValue("$deviceRef", DbValue(task.DeviceRef));
        command.Parameters.AddWithValue("$enabled", task.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$flowAvailable", task.FlowAvailable ? 1 : 0);
        command.Parameters.AddWithValue("$name", task.Name);
        command.Parameters.AddWithValue("$document", ProjectEntityMapper.Serialize(task));
        int changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<string, string> tags = sharedTagIds ?? await ProjectSnapshotWriter.ReadTagIdsForTasksAsync(
            connection, transaction, [task], cancellationToken).ConfigureAwait(false);
        await ProjectSnapshotWriter.InsertMesTaskIndexAsync(connection, transaction, task, tags, cancellationToken)
            .ConfigureAwait(false);
        return changed;
    }

    private static async Task<int> SaveMesTasksAsync(
        SqliteConnection connection, SqliteTransaction transaction, ProjectMutation mutation, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(mutation.DocumentJson))
            throw new InvalidDataException("批量保存缺少流程配置。");
        var batch = ProjectEntityMapper.Deserialize<MesTaskBatchSave>(mutation.DocumentJson);
        if (batch.Tasks is null || batch.PeriodTaskIds is null ||
            batch.Tasks.Any(task => task is null || string.IsNullOrWhiteSpace(task.Id)))
            throw new InvalidDataException("批量保存包含无效流程。");
        if (batch.Tasks.Select(task => task.Id).Distinct(StringComparer.Ordinal).Count() != batch.Tasks.Count)
            throw new InvalidDataException("批量保存包含重复流程 ID。");
        if (batch.CollectIntervalMs is int interval && (interval < 50 || interval > 3_600_000) ||
            batch.PeriodTaskIds.Count > 0 && batch.CollectIntervalMs is null)
            throw new InvalidDataException("批量采集周期必须为 50–3600000 ms。");

        var existing = new Dictionary<string, (string Json, bool Available)>(StringComparer.Ordinal);
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT id,flow_document_json,flow_available FROM IGW_mes_tasks;";
            await using var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                existing.Add(reader.GetString(0), (reader.GetString(1), reader.GetInt64(2) != 0));
        }
        var availableIds = existing.Where(item => item.Value.Available).Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var draft in batch.Tasks)
        {
            if (draft.FlowAvailable) availableIds.Add(draft.Id);
            else availableIds.Remove(draft.Id);
        }
        if (batch.PeriodTaskIds.Any(id => string.IsNullOrWhiteSpace(id) || !availableIds.Contains(id)))
            throw new InvalidDataException("选中的流程不存在或未开启采集流程功能，请重新载入后重试。");

        var changedDrafts = batch.Tasks.Where(draft => !existing.TryGetValue(draft.Id, out var prior) ||
            ProjectEntityMapper.Serialize(ProjectEntityMapper.Deserialize<MesTask>(prior.Json)) != ProjectEntityMapper.Serialize(draft)).ToList();
        // 只按改动任务引用到的路径查点位 ID(走路径索引)，百万点工程不再整表读取。
        Dictionary<string, string>? tagIds = changedDrafts.Count == 0 ? null
            : await ProjectSnapshotWriter.ReadTagIdsForTasksAsync(connection, transaction, changedDrafts, ct).ConfigureAwait(false);
        foreach (var draft in changedDrafts)
        {
            string json = ProjectEntityMapper.Serialize(draft);
            await ReplaceMesTaskAsync(connection, transaction,
                new ProjectMutation(ProjectMutationKind.ReplaceMesTask, ProjectNodeKind.Group, [draft.Id], null, json), ct, tagIds)
                .ConfigureAwait(false);
        }
        if (batch.CollectIntervalMs is int period)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE IGW_mes_tasks SET flow_document_json=json_set(flow_document_json,'$.collectIntervalMs',$period) WHERE id=$id;";
            update.Parameters.AddWithValue("$period", period);
            var idParameter = update.Parameters.Add("$id", SqliteType.Text);
            foreach (string id in batch.PeriodTaskIds.Distinct(StringComparer.Ordinal))
            {
                idParameter.Value = id;
                await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
        await using var saved = connection.CreateCommand();
        saved.Transaction = transaction;
        saved.CommandText = "UPDATE IGW_mes_tasks SET row_version=row_version+1 WHERE flow_available=1;";
        return changedDrafts.Count + await saved.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<int> ReplaceProjectSectionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProjectMutation mutation,
        CancellationToken cancellationToken)
    {
        if (mutation.EntityKind != ProjectNodeKind.Project || string.IsNullOrWhiteSpace(mutation.DocumentJson))
            throw new InvalidDataException("替换工程设置缺少设置文档。");
        Dictionary<string, JsonElement> sections = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            mutation.DocumentJson, ProjectStore.Options)
            ?? throw new InvalidDataException("工程设置文档为空。");
        string[] requested = mutation.EntityIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (requested.Length == 0 || requested.Any(key => key is not ("settings" or "opcUa" or "history")))
            throw new InvalidDataException("当前只允许局部替换 settings、opcUa 和 history 设置段。");

        int changed = 0;
        foreach (string key in requested)
        {
            if (!sections.TryGetValue(key, out JsonElement document))
                throw new InvalidDataException($"工程设置文档缺少 {key}。");
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE IGW_project_sections SET document_json=$document,row_version=row_version+1 WHERE section_key=$key;";
            command.Parameters.AddWithValue("$document", document.GetRawText());
            command.Parameters.AddWithValue("$key", key);
            changed += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        return changed;
    }

    private static async Task PopulateIdsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken)
    {
        string[] distinct = ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
        if (distinct.Length == 0) throw new InvalidOperationException("没有选择任何工程节点。");
        await using (var setup = connection.CreateCommand())
        {
            setup.Transaction = transaction;
            setup.CommandText = """
                CREATE TEMP TABLE IF NOT EXISTS IGW_mutation_ids(id TEXT PRIMARY KEY) WITHOUT ROWID;
                DELETE FROM IGW_mutation_ids;
                """;
            await setup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO IGW_mutation_ids(id) VALUES($id);";
        SqliteParameter parameter = insert.Parameters.Add("$id", SqliteType.Text);
        foreach (string id in distinct)
        {
            parameter.Value = id;
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task PersistEntitySecretsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string ownerType,
        string ownerId,
        IReadOnlyDictionary<string, string> settings,
        IReadOnlyList<StoredProjectSecret> secrets,
        CancellationToken cancellationToken)
    {
        string[] retainedNames = settings
            .Where(pair => ProjectSecrets.IsSecretSettingKey(pair.Key) && !string.IsNullOrEmpty(pair.Value))
            .Select(pair => pair.Key)
            .ToArray();

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = retainedNames.Length == 0
                ? "DELETE FROM IGW_secret_entries WHERE owner_type=$ownerType AND owner_id=$ownerId;"
                : $"""
                    DELETE FROM IGW_secret_entries
                    WHERE owner_type=$ownerType AND owner_id=$ownerId
                      AND secret_name NOT IN ({string.Join(',', retainedNames.Select((_, index) => "$keep" + index))});
                    """;
            delete.Parameters.AddWithValue("$ownerType", ownerType);
            delete.Parameters.AddWithValue("$ownerId", ownerId);
            for (int index = 0; index < retainedNames.Length; index++)
                delete.Parameters.AddWithValue("$keep" + index, retainedNames[index]);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (string emptyName in settings
                     .Where(pair => ProjectSecrets.IsSecretSettingKey(pair.Key) && string.IsNullOrEmpty(pair.Value))
                     .Select(pair => pair.Key))
        {
            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM IGW_secret_entries
                WHERE owner_type=$ownerType AND owner_id=$ownerId AND secret_name=$secretName;
                """;
            delete.Parameters.AddWithValue("$ownerType", ownerType);
            delete.Parameters.AddWithValue("$ownerId", ownerId);
            delete.Parameters.AddWithValue("$secretName", emptyName);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (StoredProjectSecret secret in secrets.Where(item =>
                     string.Equals(item.OwnerType, ownerType, StringComparison.Ordinal)
                     && string.Equals(item.OwnerId, ownerId, StringComparison.Ordinal)))
        {
            await using var upsert = connection.CreateCommand();
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO IGW_secret_entries(owner_type,owner_id,secret_name,ciphertext,key_version)
                VALUES($ownerType,$ownerId,$secretName,$ciphertext,$keyVersion)
                ON CONFLICT(owner_type,owner_id,secret_name) DO UPDATE SET
                    ciphertext=excluded.ciphertext,key_version=excluded.key_version;
                """;
            upsert.Parameters.AddWithValue("$ownerType", secret.OwnerType);
            upsert.Parameters.AddWithValue("$ownerId", secret.OwnerId);
            upsert.Parameters.AddWithValue("$secretName", secret.SecretName);
            upsert.Parameters.AddWithValue("$ciphertext", secret.Ciphertext);
            upsert.Parameters.AddWithValue("$keyVersion", secret.KeyVersion);
            await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task EnsureAllIdsExistAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT (SELECT COUNT(*) FROM IGW_mutation_ids),
                   (SELECT COUNT(*) FROM {table} WHERE id IN (SELECT id FROM IGW_mutation_ids));
            """;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (reader.GetInt64(0) != reader.GetInt64(1))
            throw new InvalidOperationException("选择中包含已删除或不存在的工程节点，请刷新后重试。");
    }

    private static async Task<(string DeviceId, string Path)> ReadGroupLocationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string groupId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT device_id,canonical_path FROM IGW_tag_groups WHERE id=$id;";
        command.Parameters.AddWithValue("$id", groupId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException($"目标标记组 {groupId} 不存在。");
        return (reader.GetString(0), reader.GetString(1));
    }

    private static async Task<GroupParent> ResolveGroupParentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? targetId,
        string entityId,
        CancellationToken cancellationToken)
    {
        if (targetId is not null)
        {
            string? groupPath = await ReadOptionalScalarAsync(connection, transaction,
                "SELECT canonical_path FROM IGW_tag_groups WHERE id=$id;", targetId, cancellationToken).ConfigureAwait(false);
            if (groupPath is not null)
            {
                (string deviceId, _) = await ReadGroupLocationAsync(connection, transaction, targetId, cancellationToken)
                    .ConfigureAwait(false);
                return new GroupParent(deviceId, targetId, groupPath);
            }
            string? devicePath = await ReadOptionalScalarAsync(connection, transaction,
                "SELECT c.name || '.' || d.name FROM IGW_devices d JOIN IGW_channels c ON c.id=d.channel_id WHERE d.id=$id;",
                targetId, cancellationToken).ConfigureAwait(false);
            if (devicePath is not null) return new GroupParent(targetId, null, devicePath);
            throw new InvalidOperationException($"目标父节点 {targetId} 不存在。");
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT g.device_id,g.parent_group_id,
                   CASE WHEN g.parent_group_id IS NULL THEN c.name || '.' || d.name ELSE p.canonical_path END
            FROM IGW_tag_groups g JOIN IGW_devices d ON d.id=g.device_id JOIN IGW_channels c ON c.id=d.channel_id
            LEFT JOIN IGW_tag_groups p ON p.id=g.parent_group_id WHERE g.id=$id;
            """;
        command.Parameters.AddWithValue("$id", entityId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("新增标记组必须指定父设备或父标记组。");
        return new GroupParent(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2));
    }

    private static async Task<TagParent> ResolveTagParentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? targetId,
        string entityId,
        CancellationToken cancellationToken)
    {
        if (targetId is not null)
        {
            string? groupPath = await ReadOptionalScalarAsync(connection, transaction,
                "SELECT canonical_path FROM IGW_tag_groups WHERE id=$id;", targetId, cancellationToken).ConfigureAwait(false);
            if (groupPath is not null)
            {
                (string deviceId, _) = await ReadGroupLocationAsync(connection, transaction, targetId, cancellationToken)
                    .ConfigureAwait(false);
                return new TagParent(deviceId, targetId, groupPath);
            }
            string? devicePath = await ReadOptionalScalarAsync(connection, transaction,
                "SELECT c.name || '.' || d.name FROM IGW_devices d JOIN IGW_channels c ON c.id=d.channel_id WHERE d.id=$id;",
                targetId, cancellationToken).ConfigureAwait(false);
            if (devicePath is not null) return new TagParent(targetId, null, devicePath);
            throw new InvalidOperationException($"目标父节点 {targetId} 不存在。");
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT t.device_id,t.group_id,
                   CASE WHEN t.group_id IS NULL THEN c.name || '.' || d.name ELSE g.canonical_path END
            FROM IGW_tags t JOIN IGW_devices d ON d.id=t.device_id JOIN IGW_channels c ON c.id=d.channel_id
            LEFT JOIN IGW_tag_groups g ON g.id=t.group_id WHERE t.id=$id;
            """;
        command.Parameters.AddWithValue("$id", entityId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("新增标记必须指定父设备或父标记组。");
        return new TagParent(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2));
    }

    private static async Task<string> ReadExistingParentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        string id,
        CancellationToken cancellationToken) =>
        await ReadOptionalScalarAsync(connection, transaction,
            $"SELECT {column} FROM {table} WHERE id=$id;", id, cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException("新增节点必须指定父节点。");

    private static async Task<string?> ReadOptionalScalarAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        string id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToString(value);
    }

    private static bool ParseBoolean(string? document)
    {
        if (bool.TryParse(document, out bool value)) return value;
        if (string.IsNullOrWhiteSpace(document)) throw new InvalidDataException("启停操作缺少布尔值。");
        using JsonDocument json = JsonDocument.Parse(document);
        if (json.RootElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return json.RootElement.GetBoolean();
        if (json.RootElement.TryGetProperty("enabled", out JsonElement enabled)
            && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return enabled.GetBoolean();
        throw new InvalidDataException("启停操作需要 true/false 或包含 enabled 的 JSON。");
    }

    private static void EnsureNoChildren(int count, string kind)
    {
        if (count != 0) throw new InvalidOperationException($"局部更新{kind}时不能内嵌下级节点。");
    }

    private static string FriendlyError(Exception exception) => exception switch
    {
        SqliteException { SqliteErrorCode: 19 } => "同级节点名称重复，操作已全部回滚。",
        _ => exception.Message,
    };

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static object DbValue(object? value) => value ?? DBNull.Value;
    private static object JsonValue<T>(T? value) where T : class =>
        value is null ? DBNull.Value : ProjectEntityMapper.Serialize(value);
    private static string KepJsonValue<T>(T? value) where T : class =>
        value is null ? "{}" : ProjectEntityMapper.Serialize(value);

    private sealed record GroupParent(string DeviceId, string? ParentGroupId, string Path);
    private sealed record TagParent(string DeviceId, string? GroupId, string Path);
}
