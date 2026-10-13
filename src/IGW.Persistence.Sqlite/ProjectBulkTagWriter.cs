using System.Text.Json;
using IGW.Abstractions;
using Microsoft.Data.Sqlite;

namespace IGW.Persistence.Sqlite;

/// <summary>
/// 批量注册点位：一个 IMMEDIATE 事务内确保通道/设备/嵌套标记组存在，逐行 upsert 点位，只递增一次修订号。
/// 只读取目标设备的现有组与点建内存索引，行级写入全部走预编译命令，不做逐行 SELECT。
/// </summary>
public sealed class ProjectBulkTagWriter
{
    private const string DriverMismatchCode = "channel_driver_mismatch";
    private const string PersistenceFailedCode = "persistence_failed";
    private const string TemplateInstanceCode = "template_instance_not_supported";
    private const string NameConflictCode = "name_conflict";
    /// <summary>请求内容导致、重试无用的拒绝（接口映射为 400）；其余失败按服务端存储故障处理（503）。</summary>
    private static readonly string[] RequestErrorCodes = [DriverMismatchCode, TemplateInstanceCode];

    private readonly ProjectDatabase _database;
    private readonly ProjectMutationExecutor _executor;

    public ProjectBulkTagWriter(ProjectDatabase database, ProjectMutationExecutor executor)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public async Task<ProjectBulkTagResult> WriteAsync(
        ProjectBulkTagRequest request,
        ProjectRevision? expected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return await WriteCoreAsync(request, expected, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            // 打开连接 / BEGIN IMMEDIATE / 读修订号阶段的失败（典型为 SQLITE_BUSY）：事务未开始，数据库不变
            return new ProjectBulkTagResult(false, false, expected ?? default, null, null, 0, 0, 0,
                FailureCode(ex), FriendlyError(ex), IsRetryable(ex));
        }
    }

    private async Task<ProjectBulkTagResult> WriteCoreAsync(
        ProjectBulkTagRequest request,
        ProjectRevision? expected,
        CancellationToken cancellationToken)
    {
        // 私有缓存连接：十万行级写事务期间不持有共享缓存表锁，其它连接（工程树/点位分页/导出）可按 WAL 快照并发读。
        await using SqliteConnection connection = await _database.OpenAsync(cancellationToken, privateCache: true)
            .ConfigureAwait(false);
        // deferred:false => BEGIN IMMEDIATE，开头即拿写锁，避免读后升级写锁时的 SQLITE_BUSY。
        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        long current = await SqliteProjectRepository.ReadRevisionAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        if (expected is { } wanted && wanted.Value != current)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new ProjectBulkTagResult(false, true, new ProjectRevision(current), null, null, 0, 0, 0,
                "revision_conflict", "工程已被其他操作修改，请刷新后重试。");
        }

        try
        {
            // 新 id 为时间有序 GUID（v7），主键插入集中在索引尾部；但路径/地址等二级索引的插入仍分散在各处，
            // 十万行级会频繁换页。本连接临时放大页缓存（按需分配，连接关闭即释放）。
            await ExecAsync(connection, transaction, "PRAGMA defer_foreign_keys=ON; PRAGMA cache_size=-65536;",
                cancellationToken).ConfigureAwait(false);
            (string channelId, string channelName) = await EnsureChannelAsync(
                connection, transaction, request.Channel, cancellationToken).ConfigureAwait(false);
            (string deviceId, string deviceName) = await EnsureDeviceAsync(
                connection, transaction, channelId, request.Device, cancellationToken).ConfigureAwait(false);
            string devicePath = channelName + "." + deviceName;

            GroupIndex groups = await LoadGroupsAsync(connection, transaction, deviceId, cancellationToken)
                .ConfigureAwait(false);
            int deleted = await DeleteReplacedGroupsAsync(
                connection, transaction, groups, request.ReplaceGroups ?? [], cancellationToken).ConfigureAwait(false);
            TagIndex tags = await LoadTagsAsync(connection, transaction, deviceId, groups, request.Items ?? [], cancellationToken)
                .ConfigureAwait(false);
            // 外部数据库模式：点和组的写入不走逐行触发器，改为提交前一次性记入待同步队列(见 ReplicationOutbox)。
            // 通道/设备/替换删除/修订号仍由触发器逐行记录(行数少，且删除必须按键记下)。
            bool capture = await ReplicationOutbox.IsInstalledAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            IReadOnlyList<string> suspended = capture
                ? await ReplicationOutbox.BeginAsync(connection, transaction, cancellationToken).ConfigureAwait(false)
                : [];
            (int created, int updated) = await WriteItemsAsync(
                connection, transaction, deviceId, devicePath, groups, tags, request.Items ?? [], capture, cancellationToken)
                .ConfigureAwait(false);
            if (capture)
                await ReplicationOutbox.EndAsync(connection, transaction, suspended, cancellationToken).ConfigureAwait(false);

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
            return new ProjectBulkTagResult(true, false, new ProjectRevision(next), channelId, deviceId,
                created, updated, deleted, null, null);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidDataException or InvalidOperationException or JsonException)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return new ProjectBulkTagResult(false, false, new ProjectRevision(current), null, null, 0, 0, 0,
                FailureCode(ex), FriendlyError(ex), IsRetryable(ex));
        }
    }

    /// <summary>
    /// 批量注册方说了算的设置键：它们决定这台设备的采集规模与调度方式，被手工改坏会直接压垮 PLC，
    /// 而且用「只补缺失键」的合并方式永远纠正不回来。其余设置仍以现场配置为准。
    /// </summary>
    private static readonly HashSet<string> BulkAuthoritativeSettings =
        new(StringComparer.OrdinalIgnoreCase) { "scanMode", "activeWindowSec", "demote" };

    /// <summary>
    /// 合并设备设置：默认只补缺失键，保留现场在配置器里调好的值；
    /// 但对已被批量注册接管的设备，<see cref="BulkAuthoritativeSettings"/> 里那几个键以批量请求为准。
    /// 返回是否有改动。
    /// </summary>
    internal static bool MergeDeviceSettings(DeviceDef device, IReadOnlyDictionary<string, string>? settings)
    {
        if (settings is null || settings.Count == 0) return false;
        bool bulkManaged = ProjectBulkTagMetadata.IsBulkManaged(device);
        bool changed = false;
        foreach ((string key, string value) in settings)
        {
            if (bulkManaged && BulkAuthoritativeSettings.Contains(key))
            {
                if (device.Settings.TryGetValue(key, out string? current) && string.Equals(current, value, StringComparison.Ordinal))
                    continue;
                device.Settings[key] = value;
                changed = true;
                continue;
            }
            changed |= device.Settings.TryAdd(key, value);
        }
        return changed;
    }

    private async Task<(string Id, string Name)> EnsureChannelAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProjectBulkChannelSpec spec,
        CancellationToken cancellationToken)
    {
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT id, driver_id, name FROM IGW_channels WHERE name=$name COLLATE NOCASE;";
            select.Parameters.AddWithValue("$name", spec.Name);
            await using SqliteDataReader reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                string driverId = reader.GetString(1);
                if (!string.Equals(driverId, spec.DriverId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"{DriverMismatchCode}: 通道 {reader.GetString(2)} 已存在且驱动为 {driverId}，与请求的 {spec.DriverId} 不一致。");
                return (reader.GetString(0), reader.GetString(2));
            }
        }

        var channel = new ChannelDef
        {
            Id = "", Name = spec.Name, DriverId = spec.DriverId,
            Settings = spec.Settings is null ? new() : new Dictionary<string, string>(spec.Settings),
        };
        await _executor.UpsertChannelAsync(connection, transaction, channel, cancellationToken).ConfigureAwait(false);
        return (channel.Id, channel.Name);
    }

    private async Task<(string Id, string Name)> EnsureDeviceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string channelId,
        ProjectBulkDeviceSpec spec,
        CancellationToken cancellationToken)
    {
        string? existingId = null;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT id FROM IGW_devices WHERE channel_id=$channel AND name=$name COLLATE NOCASE;";
            select.Parameters.AddWithValue("$channel", channelId);
            select.Parameters.AddWithValue("$name", spec.Name);
            object? value = await select.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is not null and not DBNull) existingId = Convert.ToString(value);
        }

        if (existingId is null)
        {
            var created = new DeviceDef
            {
                Id = "", Name = spec.Name, Endpoint = spec.Endpoint ?? "",
                Settings = spec.Settings is null ? new() : new Dictionary<string, string>(spec.Settings),
            };
            // 归属标记：让 DCS /Tag/AddTags 不把点位落到批量注册管理的通道/设备上
            created.Settings[ProjectBulkTagMetadata.ManagedByKey] = ProjectBulkTagMetadata.ManagedByBulk;
            await _executor.UpsertDeviceAsync(connection, transaction, channelId, created, cancellationToken)
                .ConfigureAwait(false);
            return (created.Id, created.Name);
        }

        // 已有设备：endpoint 以请求为准；settings 只补缺失键，不覆盖现场已调好的值。
        DeviceDef device = await SqliteProjectRepository.ReadDeviceDocumentAsync(
            connection, existingId, cancellationToken, transaction).ConfigureAwait(false);
        // 纵深防御（宿主已预检）：模板实例的点位来自模板展开，批量写入的点会与之冲突
        if (!string.IsNullOrWhiteSpace(device.TemplateRef))
            throw new InvalidDataException(
                $"{TemplateInstanceCode}: 设备 {device.Name} 是模板 {device.TemplateRef} 的实例，不支持批量注册点位。");
        bool changed = false;
        if (!string.IsNullOrEmpty(spec.Endpoint) && !string.Equals(device.Endpoint, spec.Endpoint, StringComparison.Ordinal))
        {
            device.Endpoint = spec.Endpoint;
            changed = true;
        }
        // settings 默认只补缺失键，保留现场在配置器里调好的值；
        // 但决定这台设备采集规模的那几个键必须以批量请求为准——它们一旦被手工改坏，
        // 批量方永远改不回来：例如扫描模式被改成「请求所有数据」后，几十万点会按各自扫描率
        // 全量轮询，把整台 PLC 压满，而下一次批量注册用 TryAdd 是纠正不了的。
        changed |= MergeDeviceSettings(device, spec.Settings);
        // 归属标记只改这一个键（其它 settings 不覆盖）：被批量注册接管的设备此后不再由 AddTags 管理
        if (!ProjectBulkTagMetadata.IsBulkManaged(device))
        {
            device.Settings[ProjectBulkTagMetadata.ManagedByKey] = ProjectBulkTagMetadata.ManagedByBulk;
            changed = true;
        }
        if (changed)
            await _executor.UpsertDeviceAsync(connection, transaction, channelId, device, cancellationToken)
                .ConfigureAwait(false);
        return (device.Id, device.Name);
    }

    private static async Task<GroupIndex> LoadGroupsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string deviceId,
        CancellationToken cancellationToken)
    {
        var index = new GroupIndex();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, parent_group_id, name, canonical_path, sort_order
            FROM IGW_tag_groups WHERE device_id=$device;
            """;
        command.Parameters.AddWithValue("$device", deviceId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = new GroupRow(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2), reader.GetString(3));
            index.Add(row);
            index.ObserveSort(row.ParentId, reader.GetInt64(4));
        }
        return index;
    }

    private static async Task<int> DeleteReplacedGroupsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GroupIndex groups,
        List<List<string>> replaceGroups,
        CancellationToken cancellationToken)
    {
        // 先在内存里收集所有被替换子树（前序），删除时反序即自底向上。
        var ordered = new List<GroupRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (List<string> path in replaceGroups)
        {
            GroupRow? root = groups.Find(path);
            if (root is null || seen.Contains(root.Id)) continue;
            var stack = new Stack<GroupRow>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                GroupRow group = stack.Pop();
                if (!seen.Add(group.Id)) continue;
                ordered.Add(group);
                foreach (GroupRow child in groups.Children(group.Id)) stack.Push(child);
            }
        }
        if (ordered.Count == 0) return 0;

        await ExecAsync(connection, transaction, """
            CREATE TEMP TABLE IF NOT EXISTS IGW_bulk_ids(id TEXT PRIMARY KEY) WITHOUT ROWID;
            DELETE FROM IGW_bulk_ids;
            """, cancellationToken).ConfigureAwait(false);
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO IGW_bulk_ids(id) VALUES($id);";
            SqliteParameter id = insert.Parameters.Add("$id", SqliteType.Text);
            insert.Prepare();
            foreach (GroupRow group in ordered)
            {
                id.Value = group.Id;
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        int deleted;
        await using (var deleteTags = connection.CreateCommand())
        {
            deleteTags.Transaction = transaction;
            deleteTags.CommandText = "DELETE FROM IGW_tags WHERE group_id IN (SELECT id FROM IGW_bulk_ids);";
            deleted = await deleteTags.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var deleteGroup = connection.CreateCommand())
        {
            deleteGroup.Transaction = transaction;
            deleteGroup.CommandText = "DELETE FROM IGW_tag_groups WHERE id=$id;";
            SqliteParameter id = deleteGroup.Parameters.Add("$id", SqliteType.Text);
            deleteGroup.Prepare();
            for (int i = ordered.Count - 1; i >= 0; i--)
            {
                id.Value = ordered[i].Id;
                await deleteGroup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                groups.Remove(ordered[i]);
            }
        }
        await ExecAsync(connection, transaction, "DELETE FROM IGW_bulk_ids;", cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    /// <summary>
    /// 只加载本次请求会写到的点：每个请求项所在的叶子组(已存在的)里的点，以及设备根下的点(有不带组的项时)。
    /// 大设备上的小增量注册(如 32 万点里改几千点)不再把整台设备的点读进内存建索引；
    /// 新点只会落到这些组里，所以排序号也只需要这些组的最大值。组本身数量少，仍整台设备加载(路径解析、替换删除要用)。
    /// </summary>
    private static async Task<TagIndex> LoadTagsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string deviceId,
        GroupIndex groups,
        List<ProjectBulkTagItem> items,
        CancellationToken cancellationToken)
    {
        var index = new TagIndex();
        var leafGroups = new HashSet<string>(StringComparer.Ordinal);
        bool includeRoot = false;
        foreach (ProjectBulkTagItem item in items)
        {
            string? groupId = null;
            bool exists = true;
            foreach (string segment in item.GroupPath ?? [])
            {
                GroupRow? group = groups.Get(groupId, segment);
                if (group is null) { exists = false; break; }   // 新组：里面还没有点
                groupId = group.Id;
            }
            if (!exists) continue;
            if (groupId is null) includeRoot = true;
            else leafGroups.Add(groupId);
        }
        if (leafGroups.Count == 0 && !includeRoot) return index;

        await ExecAsync(connection, transaction, """
            CREATE TEMP TABLE IF NOT EXISTS IGW_bulk_scope_groups(id TEXT PRIMARY KEY) WITHOUT ROWID;
            DELETE FROM IGW_bulk_scope_groups;
            """, cancellationToken).ConfigureAwait(false);
        if (leafGroups.Count > 0)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO IGW_bulk_scope_groups(id) VALUES($id);";
            SqliteParameter id = insert.Parameters.Add("$id", SqliteType.Text);
            insert.Prepare();
            foreach (string groupId in leafGroups)
            {
                id.Value = groupId;
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        // 走 ix_tags_group_order(device_id, group_id, sort_order)：按组逐个取，不扫整台设备
        string scope = includeRoot
            ? "(group_id IN (SELECT id FROM IGW_bulk_scope_groups) OR group_id IS NULL)"
            : "group_id IN (SELECT id FROM IGW_bulk_scope_groups)";
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"SELECT id, group_id, name FROM IGW_tags WHERE device_id=$device AND {scope};";
            command.Parameters.AddWithValue("$device", deviceId);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                index.Ids[(reader.IsDBNull(1) ? null : reader.GetString(1), NoCase.Fold(reader.GetString(2)))] =
                    reader.GetString(0);
        }
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"SELECT group_id, MAX(sort_order) FROM IGW_tags WHERE device_id=$device AND {scope} GROUP BY group_id;";
            command.Parameters.AddWithValue("$device", deviceId);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                index.NextSort[reader.IsDBNull(0) ? "" : reader.GetString(0)] = reader.GetInt64(1) + 1;
        }
        await ExecAsync(connection, transaction, "DELETE FROM IGW_bulk_scope_groups;", cancellationToken).ConfigureAwait(false);
        return index;
    }

    private static async Task<(int Created, int Updated)> WriteItemsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string deviceId,
        string devicePath,
        GroupIndex groups,
        TagIndex tags,
        List<ProjectBulkTagItem> items,
        bool capture,
        CancellationToken cancellationToken)
    {
        // capture：记下写过的点和组的主键(临时表)，由 ReplicationOutbox.EndAsync 一次性记入待同步队列
        await using var touchedTag = connection.CreateCommand();
        touchedTag.Transaction = transaction;
        touchedTag.CommandText = "INSERT OR IGNORE INTO IGW_bulk_touched_tags(id) VALUES($id);";
        SqliteParameter touchedTagId = touchedTag.Parameters.Add("$id", SqliteType.Text);
        await using var touchedGroup = connection.CreateCommand();
        touchedGroup.Transaction = transaction;
        touchedGroup.CommandText = "INSERT OR IGNORE INTO IGW_bulk_touched_groups(id) VALUES($id);";
        SqliteParameter touchedGroupId = touchedGroup.Parameters.Add("$id", SqliteType.Text);
        if (capture)
        {
            touchedTag.Prepare();
            touchedGroup.Prepare();
        }

        await using var insertGroup = connection.CreateCommand();
        insertGroup.Transaction = transaction;
        insertGroup.CommandText = """
            INSERT INTO IGW_tag_groups(id,device_id,parent_group_id,name,description,canonical_path,flow_enabled,sort_order,row_version)
            VALUES($id,$deviceId,$parentId,$name,NULL,$path,0,$sort,1);
            """;
        SqliteParameter gId = insertGroup.Parameters.Add("$id", SqliteType.Text);
        insertGroup.Parameters.AddWithValue("$deviceId", deviceId);
        SqliteParameter gParent = insertGroup.Parameters.Add("$parentId", SqliteType.Text);
        SqliteParameter gName = insertGroup.Parameters.Add("$name", SqliteType.Text);
        SqliteParameter gPath = insertGroup.Parameters.Add("$path", SqliteType.Text);
        SqliteParameter gSort = insertGroup.Parameters.Add("$sort", SqliteType.Integer);
        insertGroup.Prepare();

        // 列清单镜像 ProjectMutationExecutor.UpsertTagAsync（tags/tag_options 表结构变更时两处须同步修改）。
        // 拆成「新点 INSERT / 已有点 UPDATE」两条预编译语句：实测 INSERT … ON CONFLICT DO UPDATE 在十万行级比纯 INSERT 慢约 4 倍。
        // 新点 sort_order 由内存计数给出；已有点不改 sort_order、不动请求契约表达不了的 text_encoding，
        // 请求里留空(null)的可选字段（描述/单位/字符串长度/元数据）保留库内原值。
        await using var insertTag = connection.CreateCommand();
        insertTag.Transaction = transaction;
        insertTag.CommandText = """
            INSERT INTO IGW_tags(id,device_id,group_id,name,description,canonical_path,address,data_type,access,
                scan_rate_ms,string_length,text_encoding,eng_unit,sort_order,row_version)
            VALUES($id,$deviceId,$groupId,$name,$description,$path,$address,$dataType,$access,
                $scanRate,$stringLength,NULL,$engUnit,$sort,1);
            """;
        await using var updateTag = connection.CreateCommand();
        updateTag.Transaction = transaction;
        updateTag.CommandText = """
            UPDATE IGW_tags SET group_id=$groupId,name=$name,description=COALESCE($description,description),
                canonical_path=$path,address=$address,data_type=$dataType,access=$access,scan_rate_ms=$scanRate,
                string_length=COALESCE($stringLength,string_length),eng_unit=COALESCE($engUnit,eng_unit),
                row_version=row_version+1
            WHERE id=$id;
            """;
        // 参数对象建一次（两条语句各持一份实例），每行只改 Value。
        var tagParams = new TagParameters(insertTag, updateTag, deviceId);
        insertTag.Prepare();
        updateTag.Prepare();

        // 已有点只更新请求能表达的选项：数组随请求（类型变化须生效），元数据为 null 时保留；
        // 缩放、死区、报警、写确认等现场配置保留。
        await using var insertOptions = connection.CreateCommand();
        insertOptions.Transaction = transaction;
        insertOptions.CommandText = """
            INSERT INTO IGW_tag_options(tag_id,is_array,array_json,scaling_json,deadband,deadband_type,
                alarm_json,write_confirmation_json,metadata_json)
            VALUES($id,$isArray,$array,NULL,0,0,NULL,NULL,$metadata);
            """;
        await using var updateOptions = connection.CreateCommand();
        updateOptions.Transaction = transaction;
        updateOptions.CommandText = """
            INSERT INTO IGW_tag_options(tag_id,is_array,array_json,metadata_json)
            VALUES($id,$isArray,$array,$metadata)
            ON CONFLICT(tag_id) DO UPDATE SET is_array=excluded.is_array,array_json=excluded.array_json,
                metadata_json=COALESCE(excluded.metadata_json,IGW_tag_options.metadata_json);
            """;
        var optionParams = new OptionParameters(insertOptions, updateOptions);
        insertOptions.Prepare();
        updateOptions.Prepare();

        int created = 0, updated = 0;
        foreach (ProjectBulkTagItem item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // 逐段确保组存在。
            string? groupId = null;
            string groupPath = devicePath;
            foreach (string segment in item.GroupPath ?? [])
            {
                GroupRow? group = groups.Get(groupId, segment);
                if (group is null)
                {
                    group = new GroupRow(NewId(), groupId, segment, groupPath + "." + segment);
                    gId.Value = group.Id;
                    gParent.Value = (object?)groupId ?? DBNull.Value;
                    gName.Value = segment;
                    gPath.Value = group.Path;
                    gSort.Value = groups.TakeSort(groupId);
                    await insertGroup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    if (capture)
                    {
                        touchedGroupId.Value = group.Id;
                        await touchedGroup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }
                    groups.Add(group);
                }
                groupId = group.Id;
                groupPath = group.Path;
            }

            var key = (groupId, NoCase.Fold(item.Name));
            bool exists = tags.Ids.TryGetValue(key, out string? tagId);
            if (!exists)
            {
                tagId = NewId();
                tags.Ids[key] = tagId;
            }
            tagParams.Set(tagId!, groupId, groupPath + "." + item.Name, item, exists ? 0L : tags.TakeSort(groupId));
            optionParams.Set(tagId!, item);
            if (exists)
            {
                await updateTag.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await updateOptions.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                updated++;
            }
            else
            {
                await insertTag.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await insertOptions.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                created++;
            }
            if (capture)
            {
                touchedTagId.Value = tagId;
                await touchedTag.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        return (created, updated);
    }

    /// <summary>
    /// 新建组/点的 id：按时间有序的 GUID（v7）。随机 GUID 在百万行库上会把每次插入打散到主键 B 树各处、
    /// 撑爆页缓存；有序 id 让本批插入集中在索引尾部（百万点库上写库约快一倍、单设备读回约快三倍）。
    /// 已有行的 id 不变；格式仍是 32 位十六进制，与其它路径生成的 id 兼容。
    /// </summary>
    internal static string NewId() => Guid.CreateVersion7().ToString("N");

    private static async Task ExecAsync(
        SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 失败分类：驱动不一致/模板实例/同级重名（仅 UNIQUE / PRIMARY KEY 约束）是请求内容问题；
    /// 其余（其它约束、SQLite 忙/锁/IO/磁盘满、库内文档损坏等）一律 persistence_failed，是否可重试见 <see cref="IsRetryable"/>。
    /// </summary>
    internal static string FailureCode(Exception exception) =>
        RequestErrorCode(exception) ?? (IsNameConflict(exception) ? NameConflictCode : PersistenceFailedCode);

    /// <summary>可重试的存储故障（接口 503）：SQLITE_BUSY/LOCKED/IOERR/FULL/CANTOPEN。文档损坏（JSON/数据格式）等不可重试（500）。</summary>
    internal static bool IsRetryable(Exception exception) =>
        exception is SqliteException { SqliteErrorCode: 5 or 6 or 10 or 13 or 14 };

    // SQLITE_CONSTRAINT_UNIQUE = 2067，SQLITE_CONSTRAINT_PRIMARYKEY = 1555
    private static bool IsNameConflict(Exception exception) =>
        exception is SqliteException { SqliteExtendedErrorCode: 2067 or 1555 };

    private static string FriendlyError(Exception exception) => exception switch
    {
        _ when RequestErrorCode(exception) is { } code => exception.Message[(code.Length + 2)..],
        _ when IsNameConflict(exception) => "同级节点名称重复，批量注册已全部回滚。",
        _ => exception.Message,
    };

    private static string? RequestErrorCode(Exception exception) =>
        exception is InvalidDataException
            ? RequestErrorCodes.FirstOrDefault(code => exception.Message.StartsWith(code + ": ", StringComparison.Ordinal))
            : null;

    /// <summary>INSERT 与 UPDATE 两条点位语句各自的参数实例；每行只改 Value。</summary>
    private sealed class TagParameters
    {
        private readonly SqliteParameter[] _id, _group, _name, _description, _path, _address, _dataType, _access,
            _scanRate, _stringLength, _engUnit;
        private readonly SqliteParameter _sort;

        public TagParameters(SqliteCommand insert, SqliteCommand update, string deviceId)
        {
            insert.Parameters.AddWithValue("$deviceId", deviceId);
            _id = Both(insert, update, "$id", SqliteType.Text);
            _group = Both(insert, update, "$groupId", SqliteType.Text);
            _name = Both(insert, update, "$name", SqliteType.Text);
            _description = Both(insert, update, "$description", SqliteType.Text);
            _path = Both(insert, update, "$path", SqliteType.Text);
            _address = Both(insert, update, "$address", SqliteType.Text);
            _dataType = Both(insert, update, "$dataType", SqliteType.Integer);
            _access = Both(insert, update, "$access", SqliteType.Integer);
            _scanRate = Both(insert, update, "$scanRate", SqliteType.Integer);
            _stringLength = Both(insert, update, "$stringLength", SqliteType.Integer);
            _engUnit = Both(insert, update, "$engUnit", SqliteType.Text);
            _sort = insert.Parameters.Add("$sort", SqliteType.Integer);
        }

        public void Set(string id, string? groupId, string path, ProjectBulkTagItem item, long sort)
        {
            SetAll(_id, id);
            SetAll(_group, (object?)groupId ?? DBNull.Value);
            SetAll(_name, item.Name);
            SetAll(_description, (object?)item.Description ?? DBNull.Value);
            SetAll(_path, path);
            SetAll(_address, item.Address);
            SetAll(_dataType, (int)item.DataType);
            SetAll(_access, (int)item.Access);
            SetAll(_scanRate, item.ScanRateMs);
            SetAll(_stringLength, (object?)item.StringLength ?? DBNull.Value);
            SetAll(_engUnit, (object?)item.EngUnit ?? DBNull.Value);
            _sort.Value = sort;
        }
    }

    private sealed class OptionParameters(SqliteCommand insert, SqliteCommand update)
    {
        private readonly SqliteParameter[] _id = Both(insert, update, "$id", SqliteType.Text);
        private readonly SqliteParameter[] _isArray = Both(insert, update, "$isArray", SqliteType.Integer);
        private readonly SqliteParameter[] _array = Both(insert, update, "$array", SqliteType.Text);
        private readonly SqliteParameter[] _metadata = Both(insert, update, "$metadata", SqliteType.Text);

        public void Set(string id, ProjectBulkTagItem item)
        {
            SetAll(_id, id);
            SetAll(_isArray, item.Array is null ? 0 : 1);
            SetAll(_array, item.Array is null ? DBNull.Value : ProjectEntityMapper.Serialize(item.Array));
            SetAll(_metadata, item.Metadata is null ? DBNull.Value : ProjectEntityMapper.Serialize(item.Metadata));
        }
    }

    private static SqliteParameter[] Both(SqliteCommand insert, SqliteCommand update, string name, SqliteType type) =>
        [insert.Parameters.Add(name, type), update.Parameters.Add(name, type)];

    private static void SetAll(SqliteParameter[] parameters, object value)
    {
        foreach (SqliteParameter parameter in parameters) parameter.Value = value;
    }

    private sealed record GroupRow(string Id, string? ParentId, string Name, string Path);

    /// <summary>目标设备的组索引：(父组, 折叠名) → 组，外加父 → 子列表与每个父节点下一个 sort_order。</summary>
    private sealed class GroupIndex
    {
        private readonly Dictionary<(string? ParentId, string Name), GroupRow> _byName = new();
        private readonly Dictionary<string, List<GroupRow>> _children = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _nextSort = new(StringComparer.Ordinal);

        public void Add(GroupRow row)
        {
            _byName[(row.ParentId, NoCase.Fold(row.Name))] = row;
            string parent = row.ParentId ?? "";
            if (!_children.TryGetValue(parent, out List<GroupRow>? list)) _children[parent] = list = [];
            list.Add(row);
        }

        public void Remove(GroupRow row)
        {
            _byName.Remove((row.ParentId, NoCase.Fold(row.Name)));
            if (_children.TryGetValue(row.ParentId ?? "", out List<GroupRow>? list)) list.Remove(row);
            _children.Remove(row.Id);
        }

        public GroupRow? Get(string? parentId, string name) =>
            _byName.TryGetValue((parentId, NoCase.Fold(name)), out GroupRow? row) ? row : null;

        public GroupRow? Find(IReadOnlyList<string> path)
        {
            GroupRow? current = null;
            foreach (string segment in path)
            {
                current = Get(current?.Id, segment);
                if (current is null) return null;
            }
            return current;
        }

        public IEnumerable<GroupRow> Children(string id) =>
            _children.TryGetValue(id, out List<GroupRow>? list) ? list : [];

        public void ObserveSort(string? parentId, long sort)
        {
            string key = parentId ?? "";
            if (!_nextSort.TryGetValue(key, out long next) || sort + 1 > next) _nextSort[key] = sort + 1;
        }

        public long TakeSort(string? parentId)
        {
            string key = parentId ?? "";
            _nextSort.TryGetValue(key, out long next);
            _nextSort[key] = next + 1;
            return next;
        }
    }

    private sealed class TagIndex
    {
        public Dictionary<(string? GroupId, string Name), string> Ids { get; } = new();
        public Dictionary<string, long> NextSort { get; } = new(StringComparer.Ordinal);

        public long TakeSort(string? groupId)
        {
            string key = groupId ?? "";
            NextSort.TryGetValue(key, out long next);
            NextSort[key] = next + 1;
            return next;
        }
    }

    /// <summary>与 SQLite COLLATE NOCASE 一致：只折叠 ASCII 字母，避免与数据库唯一索引判定不一致。</summary>
    private static class NoCase
    {
        public static string Fold(string value)
        {
            int i = 0;
            while (i < value.Length && (value[i] < 'a' || value[i] > 'z')) i++;
            if (i == value.Length) return value;
            return string.Create(value.Length, value, static (span, source) =>
            {
                for (int j = 0; j < source.Length; j++)
                {
                    char c = source[j];
                    span[j] = c is >= 'a' and <= 'z' ? (char)(c - 32) : c;
                }
            });
        }
    }
}
