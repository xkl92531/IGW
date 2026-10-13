using IGW.Abstractions;
using Microsoft.Data.Sqlite;

namespace IGW.Persistence.Sqlite;

public sealed partial class ProjectSnapshotWriter
{
    /// <summary>
    /// 拓扑里的一行(通道/设备/组/点位)：它在库里的位置和内容指纹。
    /// 指纹一致就不用再写这一行——大工程保存时绝大多数行都不会变。
    /// </summary>
    private sealed record TopologyRow(
        char Kind, string Id, string Signature, string ChannelId, string DeviceId,
        string? GroupId, string Path, int Order, object Entity);

    /// <summary>按写入顺序遍历工程拓扑：父在子之前，顺序与全量写入完全一致。</summary>
    private static IEnumerable<TopologyRow> TopologyRows(IgwProject project)
    {
        for (int channelIndex = 0; channelIndex < project.Channels.Count; channelIndex++)
        {
            ChannelDef channel = project.Channels[channelIndex];
            yield return new('c', channel.Id, Signature('c', "", channelIndex, Header(channel)),
                channel.Id, "", null, channel.Name, channelIndex, channel);
            for (int deviceIndex = 0; deviceIndex < channel.Devices.Count; deviceIndex++)
            {
                DeviceDef device = channel.Devices[deviceIndex];
                string devicePath = $"{channel.Name}.{device.Name}";
                yield return new('d', device.Id, Signature('d', channel.Id, deviceIndex, Header(device)),
                    channel.Id, device.Id, null, devicePath, deviceIndex, device);
                for (int tagIndex = 0; tagIndex < device.Tags.Count; tagIndex++)
                    yield return TagRow(channel.Id, device.Id, null, devicePath, device.Tags[tagIndex], tagIndex);
                for (int groupIndex = 0; groupIndex < device.Groups.Count; groupIndex++)
                    foreach (var row in GroupRows(channel.Id, device.Id, null, devicePath, device.Groups[groupIndex], groupIndex))
                        yield return row;
            }
        }
    }

    private static IEnumerable<TopologyRow> GroupRows(
        string channelId, string deviceId, string? parentGroupId, string parentPath, TagGroupDef group, int order)
    {
        string path = $"{parentPath}.{group.Name}";
        yield return new('g', group.Id, Signature('g', deviceId + "|" + parentGroupId + "|" + path, order, Header(group)),
            channelId, deviceId, parentGroupId, path, order, group);
        for (int tagIndex = 0; tagIndex < group.Tags.Count; tagIndex++)
            yield return TagRow(channelId, deviceId, group.Id, path, group.Tags[tagIndex], tagIndex);
        for (int index = 0; index < group.SubGroups.Count; index++)
            foreach (var row in GroupRows(channelId, deviceId, group.Id, path, group.SubGroups[index], index))
                yield return row;
    }

    private static TopologyRow TagRow(
        string channelId, string deviceId, string? groupId, string parentPath, TagDef tag, int order) =>
        new('t', tag.Id, Signature('t', deviceId + "|" + groupId + "|" + parentPath, order, tag),
            channelId, deviceId, groupId, $"{parentPath}.{tag.Name}", order, tag);

    private static string Signature(char kind, string placement, int order, object content) =>
        $"{kind}|{placement}|{order}|{ProjectEntityMapper.Serialize(content)}";

    private static Dictionary<string, string> TopologySignatures(IgwProject project)
    {
        var signatures = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in TopologyRows(project)) signatures[row.Id] = row.Signature;
        return signatures;
    }

    /// <summary>
    /// 按 id 比对上一版与新版：写入新增和改动的行，删除已经不在工程里的行。
    /// 顺序：先删被移除的点位(腾出名称，支持“删掉 A 再新增 A”)；再写新增/改动的行，
    /// 被挪出的子组和点位此时已挂到新父级下；最后删被移除的组、设备、通道——
    /// 外键级联只会带走真正被移除的子项，不会误删刚挪走、内容没变而跳过写入的行。
    /// 同位置新建同名组等撞上唯一约束的情况由调用方回退全量重写。
    /// </summary>
    /// <returns>是否有组或点位被新增、删除或改写(路径/归属可能变化)。</returns>
    private static async Task<bool> WriteTopologyDeltaAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        IgwProject project, Dictionary<string, string>? baseline, CancellationToken ct)
    {
        bool structureChanged = false;
        var rows = TopologyRows(project).ToList();
        Dictionary<char, string[]> removed = [];
        if (baseline is not null)
        {
            var alive = new HashSet<string>(rows.Select(row => row.Id), StringComparer.Ordinal);
            removed = baseline.Where(entry => !alive.Contains(entry.Key))
                .GroupBy(entry => entry.Value.Length > 0 ? entry.Value[0] : 't')
                .ToDictionary(group => group.Key, group => group.Select(entry => entry.Key).ToArray());
            await DeleteRemovedAsync(connection, transaction, removed, "t", ct).ConfigureAwait(false);
            structureChanged = removed.Count > 0;
        }

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            if (baseline is not null && baseline.TryGetValue(row.Id, out string? previous) && previous == row.Signature) continue;
            bool update = baseline is not null;
            if (row.Kind is 'g' or 't') structureChanged = true;
            switch (row.Kind)
            {
                case 'c':
                    await InsertChannelAsync(connection, transaction, (ChannelDef)row.Entity, row.Order, ct, update).ConfigureAwait(false);
                    break;
                case 'd':
                    await InsertDeviceAsync(connection, transaction, row.ChannelId, (DeviceDef)row.Entity, row.Order, ct, update).ConfigureAwait(false);
                    break;
                case 'g':
                    await InsertGroupHeaderAsync(connection, transaction, row.DeviceId, row.GroupId,
                        (TagGroupDef)row.Entity, row.Path, row.Order, ct, update).ConfigureAwait(false);
                    break;
                default:
                    await InsertTagAsync(connection, transaction, row.DeviceId, row.GroupId,
                        (TagDef)row.Entity, row.Path, row.Order, ct, update).ConfigureAwait(false);
                    break;
            }
        }
        await DeleteRemovedAsync(connection, transaction, removed, "gdc", ct).ConfigureAwait(false);
        return structureChanged;
    }

    /// <summary>
    /// MES 任务没有重写时，按路径重新关联它引用的组和点位 id。
    /// 这两个字段由路径推导：组/点位被删掉重建、或任务引用的组后来才加上时，
    /// 不重新关联就会一直停在旧值/空值，任务目录按设备、通道筛选时会漏掉任务。
    /// </summary>
    private static async Task RelinkMesReferencesAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        var groups = await ReadGroupIdsByPathAsync(connection, transaction, ct).ConfigureAwait(false);
        var taskLinks = new List<(string Id, string? GroupId)>();
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT id, group_ref, group_id FROM IGW_mes_tasks;";
            await using var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                string? groupRef = reader.IsDBNull(1) ? null : reader.GetString(1);
                string? current = reader.IsDBNull(2) ? null : reader.GetString(2);
                string? expected = !string.IsNullOrWhiteSpace(groupRef) && groups.TryGetValue(groupRef, out var id) ? id : null;
                if (!string.Equals(current, expected, StringComparison.Ordinal)) taskLinks.Add((reader.GetString(0), expected));
            }
        }
        foreach (var (id, groupId) in taskLinks)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE IGW_mes_tasks SET group_id=$groupId WHERE id=$id;";
            update.Parameters.AddWithValue("$groupId", DbValue(groupId));
            update.Parameters.AddWithValue("$id", id);
            await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var tags = await ReadTagIdsByPathAsync(connection, transaction, ct).ConfigureAwait(false);
        var refLinks = new List<(long RowId, string? TagId)>();
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT rowid, referenced_path, referenced_tag_id FROM IGW_mes_task_index WHERE referenced_path IS NOT NULL;";
            await using var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                string? current = reader.IsDBNull(2) ? null : reader.GetString(2);
                string? expected = tags.TryGetValue(reader.GetString(1), out var id) ? id : null;
                if (!string.Equals(current, expected, StringComparison.Ordinal)) refLinks.Add((reader.GetInt64(0), expected));
            }
        }
        foreach (var (rowId, tagId) in refLinks)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE IGW_mes_task_index SET referenced_tag_id=$tagId WHERE rowid=$rowId;";
            update.Parameters.AddWithValue("$tagId", DbValue(tagId));
            update.Parameters.AddWithValue("$rowId", rowId);
            await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task DeleteRemovedAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        Dictionary<char, string[]> removed, string kinds, CancellationToken ct)
    {
        foreach (char kind in kinds)
        {
            if (!removed.TryGetValue(kind, out string[]? ids) || ids.Length == 0) continue;
            string table = kind switch { 't' => "IGW_tags", 'g' => "IGW_tag_groups", 'd' => "IGW_devices", _ => "IGW_channels" };
            await DeleteByIdAsync(connection, transaction, table, ids, ct).ConfigureAwait(false);
        }
    }

    private static async Task DeleteByIdAsync(
        SqliteConnection connection, SqliteTransaction transaction, string table,
        IReadOnlyList<string> ids, CancellationToken ct)
    {
        const int chunk = 400;   // SQLite 默认最多 999 个参数
        for (int offset = 0; offset < ids.Count; offset += chunk)
        {
            ct.ThrowIfCancellationRequested();
            string[] slice = ids.Skip(offset).Take(chunk).ToArray();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            string parameters = string.Join(',', slice.Select((_, index) => "$p" + index));
            // 表名来自上面的固定映射，不是外部输入。
            command.CommandText = $"DELETE FROM {table} WHERE id IN ({parameters});";
            for (int index = 0; index < slice.Length; index++)
                command.Parameters.AddWithValue("$p" + index, slice[index]);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static bool Same<T>(T left, T right) =>
        ProjectEntityMapper.Serialize(left) == ProjectEntityMapper.Serialize(right);
    private static object Header(ChannelDef value) => new
    { value.Id, value.Name, value.Description, value.DriverId, value.Settings, value.KepProperties };
    private static object Header(DeviceDef value) => new
    { value.Id, value.Name, value.Description, value.Endpoint, value.TemplateRef, value.Delta, value.Settings, value.KepProperties };
    private static object Header(TagGroupDef value) => new
    { value.Id, value.Name, value.Description, value.KepProperties };

    private static async Task ClearTablesAsync(SqliteConnection connection, SqliteTransaction transaction,
        string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // These commands are fixed INSERT statements built by this writer; only values are parameters.
    // UPSERT preserves row identities, foreign keys and unchanged rows (unlike INSERT OR REPLACE).
    private static Task<int> ExecuteInsertAsync(SqliteCommand command, bool update, CancellationToken ct)
    {
        if (update)
        {
            string sql = command.CommandText.Trim().TrimEnd(';');
            int start = sql.IndexOf('(');
            int end = sql.IndexOf(')', start);
            string[] columns = sql[(start + 1)..end].Split(',').Select(c => c.Trim()).ToArray();
            string key = columns[0];
            var assignments = columns.Skip(1).Where(c => c is not ("created_at" or "enabled" or "flow_enabled" or "igw_properties_json")).Select(c =>
                c == "row_version" ? "row_version=row_version+1" : $"{c}=excluded.{c}");
            command.CommandText = sql + $" ON CONFLICT({key}) DO UPDATE SET " + string.Join(',', assignments) + ";";
        }
        return command.ExecuteNonQueryAsync(ct);
    }
}
