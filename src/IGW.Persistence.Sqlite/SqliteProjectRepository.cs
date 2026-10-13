using System.Runtime.CompilerServices;
using IGW.Abstractions;
using Microsoft.Data.Sqlite;

namespace IGW.Persistence.Sqlite;

/// <summary>面向大点表的 SQLite 工程仓储；读取分页，写入按修订号串行提交。</summary>
public sealed class SqliteProjectRepository : IProjectRepository
{
    private readonly ProjectDatabase _database;
    private readonly ProjectSnapshotReader _reader;
    private readonly ProjectSnapshotWriter _writer;
    private readonly ProjectMutationExecutor _mutations;
    private readonly ProjectBulkTagWriter _bulkTags;
    private readonly ProjectSecretVault _vault;

    public SqliteProjectRepository(ProjectDatabase database, ProjectSecretVault vault)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        ArgumentNullException.ThrowIfNull(vault);
        _vault = vault;
        _reader = new ProjectSnapshotReader(database, vault);
        _writer = new ProjectSnapshotWriter(database, vault);
        _mutations = new ProjectMutationExecutor(database, vault);
        _bulkTags = new ProjectBulkTagWriter(database, _mutations);
    }

    public Task InitializeAsync(CancellationToken cancellationToken) =>
        new ProjectSchemaMigrator(_database).MigrateAsync(cancellationToken);

    public async Task<ProjectRevision> GetRevisionAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        return new ProjectRevision(await ReadRevisionAsync(connection, null, cancellationToken).ConfigureAwait(false));
    }

    public async Task<ProjectTreeNode> GetRootAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT project_id, name, description, revision,
                   EXISTS(SELECT 1 FROM IGW_channels)
                   , schema_version
            FROM IGW_project_meta WHERE singleton_id=1;
            """;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("工程数据库中没有工程元数据。");
        return new ProjectTreeNode(
            reader.GetString(0), ProjectNodeKind.Project, reader.GetString(1), NullableString(reader, 2),
            reader.GetString(1), reader.GetInt64(4) != 0, 0, reader.GetInt64(3), reader.GetInt32(5));
    }

    public async Task<IReadOnlyList<ProjectTreeNode>> GetChildrenAsync(
        ProjectNodeRef parent,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        return parent.Kind switch
        {
            ProjectNodeKind.Project => await ReadChannelsAsync(connection, cancellationToken).ConfigureAwait(false),
            ProjectNodeKind.Channel => await ReadDevicesAsync(connection, parent.Id, cancellationToken).ConfigureAwait(false),
            ProjectNodeKind.Device => await ReadDeviceChildrenAsync(connection, parent.Id, cancellationToken).ConfigureAwait(false),
            ProjectNodeKind.Group => await ReadGroupChildrenAsync(connection, parent.Id, cancellationToken).ConfigureAwait(false),
            ProjectNodeKind.Tag => [],
            _ => throw new ArgumentOutOfRangeException(nameof(parent)),
        };
    }

    public async Task<ProjectPage<ProjectTagRow>> GetTagsAsync(
        ProjectTagQuery query,
        CancellationToken cancellationToken)
    {
        query.Validate();
        await using SqliteConnection connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        string? pattern = string.IsNullOrWhiteSpace(query.Search)
            ? null
            : $"%{EscapeLike(query.Search.Trim())}%";
        // 设备直挂点位(Kepware 导入常把点位全放在设备下,组为空)：device_id 匹配且 group_id 为空。
        string ownerPredicate = query.GroupId is not null
            ? "t.group_id=$groupId AND "
            : query.DeviceId is not null
                ? "t.device_id=$deviceId AND t.group_id IS NULL AND "
                : "";
        string predicate = ownerPredicate + """
            ($pattern IS NULL OR t.name LIKE $pattern ESCAPE '\' COLLATE NOCASE
                 OR t.canonical_path LIKE $pattern ESCAPE '\' COLLATE NOCASE
                 OR t.address LIKE $pattern ESCAPE '\' COLLATE NOCASE
                 OR t.description LIKE $pattern ESCAPE '\' COLLATE NOCASE)
            """;

        long total;
        await using (var count = connection.CreateCommand())
        {
            count.CommandText = $"SELECT COUNT(*) FROM IGW_tags t WHERE {predicate};";
            AddTagQueryParameters(count, query.GroupId, pattern, query.DeviceId);
            total = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        }

        var items = new List<ProjectTagRow>(Math.Min(query.Limit, total > int.MaxValue ? query.Limit : (int)total));
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = ownerPredicate is ""
                ? $"""
                    SELECT t.id, t.name, t.canonical_path, t.address, t.data_type, t.access,
                           t.scan_rate_ms, t.description, t.eng_unit, t.string_length,
                           o.array_json, o.metadata_json, t.row_version
                    FROM IGW_tags t LEFT JOIN IGW_tag_options o ON o.tag_id=t.id
                    WHERE {predicate}
                    ORDER BY t.canonical_path COLLATE NOCASE, t.rowid
                    LIMIT $limit OFFSET $offset;
                    """
                : $"""
                    SELECT page.id,page.name,page.canonical_path,page.address,page.data_type,page.access,
                           page.scan_rate_ms,page.description,page.eng_unit,page.string_length,
                           o.array_json,o.metadata_json,page.row_version
                    FROM (
                        SELECT t.id,t.name,t.canonical_path,t.address,t.data_type,t.access,
                               t.scan_rate_ms,t.description,t.eng_unit,t.string_length,t.row_version,
                               t.sort_order,t.rowid AS source_rowid
                        FROM IGW_tags t
                        WHERE {predicate}
                        ORDER BY t.sort_order,t.rowid
                        LIMIT $limit OFFSET $offset
                    ) page
                    LEFT JOIN IGW_tag_options o ON o.tag_id=page.id
                    ORDER BY page.sort_order,page.source_rowid;
                    """;
            AddTagQueryParameters(command, query.GroupId, pattern, query.DeviceId);
            command.Parameters.AddWithValue("$limit", query.Limit);
            command.Parameters.AddWithValue("$offset", query.Offset);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(new ProjectTagRow(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    (IgwDataType)reader.GetInt32(4), (TagAccess)reader.GetInt32(5), reader.GetInt32(6),
                    NullableString(reader, 7), NullableString(reader, 8), NullableInt(reader, 9),
                    NullableJson<ArrayDef>(reader, 10),
                    NullableJson<Dictionary<string, string>>(reader, 11), reader.GetInt64(12)));
            }
        }

        return new ProjectPage<ProjectTagRow>(items, total, query.Offset, query.Limit);
    }

    public async Task<long> GetTagCountAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM IGW_tags;";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task<ProjectPage<ProjectMesTaskRow>> GetMesTasksAsync(
        ProjectMesTaskQuery query,
        CancellationToken cancellationToken)
    {
        query.Validate();
        await using SqliteConnection connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        string? pattern = string.IsNullOrWhiteSpace(query.Search)
            ? null
            : $"%{EscapeLike(query.Search.Trim())}%";
        string? deviceId = query.Scope == ProjectMesTaskScope.Device ? query.DeviceId : null;
        string? channelId = query.Scope == ProjectMesTaskScope.Channel ? query.ChannelId : null;
        const string fromAndPredicate = """
            FROM IGW_mes_tasks m
            LEFT JOIN IGW_tag_groups g ON g.id=m.group_id
            LEFT JOIN IGW_devices d ON d.id=g.device_id
            LEFT JOIN IGW_channels c ON c.id=d.channel_id
            WHERE m.flow_available=1
              AND ($deviceId IS NULL OR d.id=$deviceId)
              AND ($channelId IS NULL OR c.id=$channelId)
              AND ($enabled IS NULL OR m.enabled=$enabled)
              AND ($pattern IS NULL OR m.name LIKE $pattern ESCAPE '\' COLLATE NOCASE
                   OR m.group_ref LIKE $pattern ESCAPE '\' COLLATE NOCASE
                   OR m.device_ref LIKE $pattern ESCAPE '\' COLLATE NOCASE)
            """;

        long total;
        await using (var count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) " + fromAndPredicate + ";";
            AddMesTaskQueryParameters(count, deviceId, channelId, pattern, query.Enabled);
            total = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        }

        var items = new List<ProjectMesTaskRow>(Math.Min(query.Limit, total > int.MaxValue ? query.Limit : (int)total));
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT m.id,m.name,m.group_id,m.group_ref,m.device_ref,d.id,c.id,g.description, "
                + "m.enabled,m.flow_available,m.row_version,COALESCE(json_extract(m.flow_document_json,'$.collectIntervalMs'),1000) " + fromAndPredicate + " " + """
                ORDER BY m.group_ref COLLATE NOCASE, m.name COLLATE NOCASE, m.rowid
                LIMIT $limit OFFSET $offset;
                """;
            AddMesTaskQueryParameters(command, deviceId, channelId, pattern, query.Enabled);
            command.Parameters.AddWithValue("$limit", query.Limit);
            command.Parameters.AddWithValue("$offset", query.Offset);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(new ProjectMesTaskRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    NullableString(reader, 2),
                    NullableString(reader, 3),
                    NullableString(reader, 4),
                    NullableString(reader, 5),
                    NullableString(reader, 6),
                    NullableString(reader, 7),
                    reader.GetInt64(8) != 0,
                    reader.GetInt64(9) != 0,
                    reader.GetInt64(10), reader.GetInt32(11)));
            }
        }

        return new ProjectPage<ProjectMesTaskRow>(items, total, query.Offset, query.Limit);
    }

    public async Task<ProjectPage<ProjectNodeSearchRow>> SearchNodesAsync(
        ProjectNodeSearchQuery query,
        CancellationToken cancellationToken)
    {
        query.Validate();
        await using SqliteConnection connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        return query.Kind == ProjectNodeKind.Tag
            ? await SearchTagsAsync(connection, query, cancellationToken).ConfigureAwait(false)
            : await SearchGroupsAsync(connection, query, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> GetEntityDocumentAsync(
        ProjectNodeRef entity,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        return entity.Kind switch
        {
            ProjectNodeKind.Channel => ProjectEntityMapper.Serialize(
                await ReadChannelDocumentAsync(connection, entity.Id, cancellationToken).ConfigureAwait(false)),
            ProjectNodeKind.Device => ProjectEntityMapper.Serialize(
                await ReadDeviceDocumentAsync(connection, entity.Id, cancellationToken).ConfigureAwait(false)),
            ProjectNodeKind.Group => ProjectEntityMapper.Serialize(
                await ReadGroupDocumentAsync(connection, entity.Id, cancellationToken).ConfigureAwait(false)),
            ProjectNodeKind.Tag => ProjectEntityMapper.Serialize(
                await ReadTagDocumentAsync(connection, entity.Id, cancellationToken).ConfigureAwait(false)),
            _ => throw new ArgumentException("只能读取通道、设备、标记组或标记文档。", nameof(entity)),
        };
    }

    public async Task<ProjectDocument> GetDocumentAsync(
        ProjectDocumentKind kind,
        string key,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        (string table, string keyColumn, string documentColumn) = kind switch
        {
            ProjectDocumentKind.ProjectSection => ("IGW_project_sections", "section_key", "document_json"),
            ProjectDocumentKind.MesTask => ("IGW_mes_tasks", "id", "flow_document_json"),
            ProjectDocumentKind.FlowTemplate => ("IGW_flow_templates", "id", "template_document_json"),
            ProjectDocumentKind.DeviceTemplate => ("IGW_device_templates", "id", "template_document_json"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {documentColumn},row_version FROM {table} WHERE {keyColumn}=$key;";
        command.Parameters.AddWithValue("$key", key);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new KeyNotFoundException($"找不到工程文档 {kind}:{key}。");
        return new ProjectDocument(kind, key, reader.GetString(0), reader.GetInt64(1));
    }

    public Task<ProjectMutationResult> ExecuteAsync(
        ProjectMutation mutation,
        ProjectRevision expected,
        CancellationToken cancellationToken) =>
        _mutations.ExecuteAsync(mutation, expected, cancellationToken);

    public Task<ProjectBulkTagResult> BulkUpsertTagsAsync(
        ProjectBulkTagRequest request,
        ProjectRevision? expected,
        CancellationToken cancellationToken) =>
        _bulkTags.WriteAsync(request, expected, cancellationToken);

    public Task<IgwProject> LoadRuntimeProjectAsync(CancellationToken cancellationToken) =>
        _reader.ReadAsync(cancellationToken);

    public Task<ProjectDeviceSnapshot> LoadDeviceAsync(string deviceId, CancellationToken cancellationToken) =>
        _reader.LoadDeviceAsync(deviceId, cancellationToken);

    public Task<ProjectDeviceSnapshot> LoadDeviceGroupsAsync(
        string deviceId, IReadOnlyCollection<string> rootGroupNames, bool includeRootTags, CancellationToken cancellationToken) =>
        _reader.LoadDeviceAsync(deviceId, cancellationToken, new DevicePartialScope(rootGroupNames, includeRootTags));

    public Task<ProjectRevision> ReplaceFromSnapshotAsync(
        IgwProject project,
        ProjectRevision expected,
        CancellationToken cancellationToken) =>
        _writer.WriteAsync(project, expected, cancellationToken);

    public async Task<IProjectReadSnapshot> OpenReadSnapshotAsync(CancellationToken cancellationToken)
    {
        SqliteConnection connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            long revision = await ReadRevisionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            return new SqliteProjectReadSnapshot(connection, transaction, new ProjectRevision(revision), _vault);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static void AddMesTaskQueryParameters(
        SqliteCommand command,
        string? deviceId,
        string? channelId,
        string? pattern,
        bool? enabled)
    {
        command.Parameters.AddWithValue("$deviceId", (object?)deviceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$channelId", (object?)channelId ?? DBNull.Value);
        command.Parameters.AddWithValue("$pattern", (object?)pattern ?? DBNull.Value);
        command.Parameters.AddWithValue("$enabled", enabled.HasValue ? (enabled.Value ? 1 : 0) : DBNull.Value);
    }

    private static async Task<ProjectPage<ProjectNodeSearchRow>> SearchTagsAsync(
        SqliteConnection connection,
        ProjectNodeSearchQuery query,
        CancellationToken cancellationToken)
    {
        string? scopePattern = string.IsNullOrWhiteSpace(query.ScopePath)
            ? null : EscapeLike(query.ScopePath.Trim().Trim('.')) + ".%";
        string? scope = string.IsNullOrWhiteSpace(query.ScopePath) ? null : query.ScopePath.Trim().Trim('.');
        string? searchPattern = string.IsNullOrWhiteSpace(query.Search)
            ? null : "%" + EscapeLike(query.Search.Trim()) + "%";
        const string source = """
            FROM IGW_tags t
            INNER JOIN IGW_devices d ON d.id=t.device_id
            INNER JOIN IGW_channels c ON c.id=d.channel_id
            LEFT JOIN IGW_tag_groups g ON g.id=t.group_id
            LEFT JOIN IGW_tag_options o ON o.tag_id=t.id
            WHERE ($scope IS NULL
                   OR ($directOnly=1 AND COALESCE(g.canonical_path,c.name||'.'||d.name)=$scope COLLATE NOCASE)
                   OR ($directOnly=0 AND (t.canonical_path=$scope COLLATE NOCASE
                       OR t.canonical_path LIKE $scopePattern ESCAPE '\' COLLATE NOCASE)))
              AND ($searchPattern IS NULL OR t.name LIKE $searchPattern ESCAPE '\' COLLATE NOCASE
                   OR t.canonical_path LIKE $searchPattern ESCAPE '\' COLLATE NOCASE
                   OR t.address LIKE $searchPattern ESCAPE '\' COLLATE NOCASE
                   OR t.description LIKE $searchPattern ESCAPE '\' COLLATE NOCASE)
              AND ($access IS NULL OR ($access=0 AND t.access IN (0,2)) OR ($access=1 AND t.access IN (1,2)))
            """;

        long total = 0;
        if (query.CountTotal)
        {
            await using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) " + source + ";";
            AddNodeSearchParameters(count, query, scope, scopePattern, searchPattern);
            total = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        }

        var items = new List<ProjectNodeSearchRow>(Math.Min(query.Limit, total > int.MaxValue ? query.Limit : (int)total));
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT t.id,t.name,t.canonical_path,"
                + "COALESCE(g.canonical_path,c.name||'.'||d.name),c.name||'.'||d.name,"
                + "t.description,t.address,t.data_type,t.access,t.eng_unit,t.string_length,"
                + "o.array_json,o.metadata_json,t.row_version " + source
                // 同一个组里按原始顺序(与工程树、TIA 里的声明顺序一致)，不按名字字母排：
                // 「配置数据」按它拼报文，字段顺序要和 DB 一致
                + " ORDER BY CASE WHEN $directOnly=1 THEN t.sort_order END,"
                + "COALESCE(g.canonical_path,c.name||'.'||d.name) COLLATE NOCASE,t.sort_order,"
                + "t.canonical_path COLLATE NOCASE,t.rowid LIMIT $limit OFFSET $offset;";
            AddNodeSearchParameters(command, query, scope, scopePattern, searchPattern);
            command.Parameters.AddWithValue("$limit", query.Limit);
            command.Parameters.AddWithValue("$offset", query.Offset);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(new ProjectNodeSearchRow(
                    reader.GetString(0), ProjectNodeKind.Tag, reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), NullableString(reader, 5), reader.GetString(6),
                    (IgwDataType)reader.GetInt32(7), (TagAccess)reader.GetInt32(8), NullableString(reader, 9),
                    NullableInt(reader, 10), NullableJson<ArrayDef>(reader, 11),
                    NullableJson<Dictionary<string, string>>(reader, 12), reader.GetInt64(13)));
            }
        }
        if (!query.CountTotal) total = query.Offset + items.Count;
        return new ProjectPage<ProjectNodeSearchRow>(items, total, query.Offset, query.Limit);
    }

    private static async Task<ProjectPage<ProjectNodeSearchRow>> SearchGroupsAsync(
        SqliteConnection connection,
        ProjectNodeSearchQuery query,
        CancellationToken cancellationToken)
    {
        string? scopePattern = string.IsNullOrWhiteSpace(query.ScopePath)
            ? null : EscapeLike(query.ScopePath.Trim().Trim('.')) + ".%";
        string? scope = string.IsNullOrWhiteSpace(query.ScopePath) ? null : query.ScopePath.Trim().Trim('.');
        string? searchPattern = string.IsNullOrWhiteSpace(query.Search)
            ? null : "%" + EscapeLike(query.Search.Trim()) + "%";
        const string source = """
            FROM IGW_tag_groups g
            INNER JOIN IGW_devices d ON d.id=g.device_id
            INNER JOIN IGW_channels c ON c.id=d.channel_id
            LEFT JOIN IGW_tag_groups parent ON parent.id=g.parent_group_id
            WHERE ($scope IS NULL
                   OR ($directOnly=1 AND COALESCE(parent.canonical_path,c.name||'.'||d.name)=$scope COLLATE NOCASE)
                   OR ($directOnly=0 AND (g.canonical_path=$scope COLLATE NOCASE
                       OR g.canonical_path LIKE $scopePattern ESCAPE '\' COLLATE NOCASE)))
              AND ($searchPattern IS NULL OR g.name LIKE $searchPattern ESCAPE '\' COLLATE NOCASE
                   OR g.canonical_path LIKE $searchPattern ESCAPE '\' COLLATE NOCASE)
            """;

        long total = 0;
        if (query.CountTotal)
        {
            await using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) " + source + ";";
            AddNodeSearchParameters(count, query, scope, scopePattern, searchPattern);
            total = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        }

        var items = new List<ProjectNodeSearchRow>(Math.Min(query.Limit, total > int.MaxValue ? query.Limit : (int)total));
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT g.id,g.name,g.canonical_path,"
                + "COALESCE(parent.canonical_path,c.name||'.'||d.name),c.name||'.'||d.name,"
                + "g.description,g.row_version " + source
                + " ORDER BY CASE WHEN $directOnly=1 THEN g.sort_order END,"
                + "COALESCE(parent.canonical_path,c.name||'.'||d.name) COLLATE NOCASE,g.sort_order,"
                + "g.canonical_path COLLATE NOCASE,g.rowid LIMIT $limit OFFSET $offset;";
            AddNodeSearchParameters(command, query, scope, scopePattern, searchPattern);
            command.Parameters.AddWithValue("$limit", query.Limit);
            command.Parameters.AddWithValue("$offset", query.Offset);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(new ProjectNodeSearchRow(
                    reader.GetString(0), ProjectNodeKind.Group, reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), NullableString(reader, 5), null, null, null,
                    null, null, null, null, reader.GetInt64(6)));
            }
        }
        if (!query.CountTotal) total = query.Offset + items.Count;
        return new ProjectPage<ProjectNodeSearchRow>(items, total, query.Offset, query.Limit);
    }

    private static void AddNodeSearchParameters(
        SqliteCommand command,
        ProjectNodeSearchQuery query,
        string? scope,
        string? scopePattern,
        string? searchPattern)
    {
        command.Parameters.AddWithValue("$scope", (object?)scope ?? DBNull.Value);
        command.Parameters.AddWithValue("$scopePattern", (object?)scopePattern ?? DBNull.Value);
        command.Parameters.AddWithValue("$searchPattern", (object?)searchPattern ?? DBNull.Value);
        command.Parameters.AddWithValue("$directOnly", query.DirectOnly ? 1 : 0);
        command.Parameters.AddWithValue("$access", query.RequiredAccess is null ? DBNull.Value : (object)(int)query.RequiredAccess.Value);
    }

    internal static async Task<long> ReadRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT revision FROM IGW_project_meta WHERE singleton_id=1;";
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null or DBNull) throw new InvalidDataException("工程数据库中没有修订号。");
        return Convert.ToInt64(value);
    }

    private static async Task<IReadOnlyList<ProjectTreeNode>> ReadChannelsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT c.id, c.name, c.description, c.name,
                   EXISTS(SELECT 1 FROM IGW_devices d WHERE d.channel_id=c.id),
                   0, c.row_version
            FROM IGW_channels c ORDER BY c.sort_order, c.rowid;
            """;
        return await ReadTreeNodesAsync(connection, sql, ProjectNodeKind.Channel, null, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<ProjectTreeNode>> ReadDevicesAsync(
        SqliteConnection connection,
        string channelId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT d.id, d.name, d.description, c.name || '.' || d.name,
                   EXISTS(SELECT 1 FROM IGW_tag_groups g WHERE g.device_id=d.id),
                   (SELECT COUNT(*) FROM IGW_tags t WHERE t.device_id=d.id AND t.group_id IS NULL),
                   d.row_version
            FROM IGW_devices d INNER JOIN IGW_channels c ON c.id=d.channel_id
            WHERE d.channel_id=$parentId ORDER BY d.sort_order, d.rowid;
            """;
        return await ReadTreeNodesAsync(connection, sql, ProjectNodeKind.Device, channelId, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<ProjectTreeNode>> ReadDeviceChildrenAsync(
        SqliteConnection connection,
        string deviceId,
        CancellationToken cancellationToken)
    {
        const string groupSql = """
            SELECT g.id, g.name, g.description, g.canonical_path,
                   EXISTS(SELECT 1 FROM IGW_tag_groups child WHERE child.parent_group_id=g.id),
                   (SELECT COUNT(*) FROM IGW_tags t WHERE t.group_id=g.id), g.row_version
            FROM IGW_tag_groups g
            WHERE g.device_id=$parentId AND g.parent_group_id IS NULL
            ORDER BY g.sort_order, g.rowid;
            """;
        return await ReadTreeNodesAsync(connection, groupSql, ProjectNodeKind.Group, deviceId, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<ProjectTreeNode>> ReadGroupChildrenAsync(
        SqliteConnection connection,
        string groupId,
        CancellationToken cancellationToken)
    {
        const string groupSql = """
            SELECT g.id, g.name, g.description, g.canonical_path,
                   EXISTS(SELECT 1 FROM IGW_tag_groups child WHERE child.parent_group_id=g.id),
                   (SELECT COUNT(*) FROM IGW_tags t WHERE t.group_id=g.id), g.row_version
            FROM IGW_tag_groups g WHERE g.parent_group_id=$parentId
            ORDER BY g.sort_order, g.rowid;
            """;
        return await ReadTreeNodesAsync(connection, groupSql, ProjectNodeKind.Group, groupId, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<ProjectTreeNode>> ReadTreeNodesAsync(
        SqliteConnection connection,
        string sql,
        ProjectNodeKind kind,
        string? parentId,
        CancellationToken cancellationToken)
    {
        var result = new List<ProjectTreeNode>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (parentId is not null) command.Parameters.AddWithValue("$parentId", parentId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new ProjectTreeNode(
                reader.GetString(0), kind, reader.GetString(1), NullableString(reader, 2), reader.GetString(3),
                reader.GetInt64(4) != 0, reader.GetInt32(5), reader.GetInt64(6)));
        }
        return result;
    }

    internal static async Task<ChannelDef> ReadChannelDocumentAsync(
        SqliteConnection connection, string id, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,name,driver_id,description,settings_json FROM IGW_channels WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw Missing(ProjectNodeKind.Channel, id);
        return new ChannelDef
        {
            Id = reader.GetString(0), Name = reader.GetString(1), DriverId = reader.GetString(2),
            Description = NullableString(reader, 3),
            Settings = ProjectEntityMapper.Deserialize<Dictionary<string, string>>(reader.GetString(4)),
        };
    }

    internal static async Task<DeviceDef> ReadDeviceDocumentAsync(
        SqliteConnection connection, string id, CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id,name,endpoint,description,settings_json,template_ref,delta_json,kep_properties_json
            FROM IGW_devices WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$id", id);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw Missing(ProjectNodeKind.Device, id);
        return new DeviceDef
        {
            Id = reader.GetString(0), Name = reader.GetString(1), Endpoint = reader.GetString(2),
            Description = NullableString(reader, 3),
            Settings = ProjectEntityMapper.Deserialize<Dictionary<string, string>>(reader.GetString(4)),
            TemplateRef = NullableString(reader, 5),
            Delta = reader.IsDBNull(6) ? null : ProjectEntityMapper.Deserialize<DeviceDelta>(reader.GetString(6)),
            // 设备 upsert(界面保存与批量注册补设置)会整列写回 kep_properties_json：读文档时必须带上，否则写回即清空
            KepProperties = NullableJson<Dictionary<string, System.Text.Json.JsonElement>>(reader, 7) is { Count: > 0 } kep
                ? kep : null,
        };
    }

    internal static async Task<TagGroupDef> ReadGroupDocumentAsync(
        SqliteConnection connection, string id, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,name,description FROM IGW_tag_groups WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw Missing(ProjectNodeKind.Group, id);
        return new TagGroupDef { Id = reader.GetString(0), Name = reader.GetString(1), Description = NullableString(reader, 2) };
    }

    internal static async Task<TagDef> ReadTagDocumentAsync(
        SqliteConnection connection, string id, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.id,t.name,t.address,t.data_type,t.access,t.scan_rate_ms,t.description,t.eng_unit,
                   t.string_length,t.text_encoding,o.is_array,o.array_json,o.scaling_json,o.deadband,
                   o.deadband_type,o.metadata_json,o.alarm_json,o.write_confirmation_json
            FROM IGW_tags t INNER JOIN IGW_tag_options o ON o.tag_id=t.id WHERE t.id=$id;
            """;
        command.Parameters.AddWithValue("$id", id);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw Missing(ProjectNodeKind.Tag, id);
        return new TagDef
        {
            Id = reader.GetString(0), Name = reader.GetString(1), Address = reader.GetString(2),
            DataType = (IgwDataType)reader.GetInt32(3), Access = (TagAccess)reader.GetInt32(4),
            ScanRateMs = reader.GetInt32(5), Description = NullableString(reader, 6), EngUnit = NullableString(reader, 7),
            StringLength = reader.IsDBNull(8) ? null : reader.GetInt32(8),
            TextEncoding = reader.IsDBNull(9) ? null : (PlcTextEncoding)reader.GetInt32(9),
            IsArray = reader.GetInt32(10) != 0,
            Array = NullableJson<ArrayDef>(reader, 11), Scaling = NullableJson<ScalingDef>(reader, 12),
            Deadband = reader.GetDouble(13), DeadbandType = (DeadbandType)reader.GetInt32(14),
            Metadata = NullableJson<Dictionary<string, string>>(reader, 15), Alarm = NullableJson<AlarmDef>(reader, 16),
            WriteConfirmation = NullableJson<WriteConfirmationDef>(reader, 17),
        };
    }

    private static void AddTagQueryParameters(SqliteCommand command, string? groupId, string? pattern, string? deviceId = null)
    {
        command.Parameters.AddWithValue("$groupId", (object?)groupId ?? DBNull.Value);
        command.Parameters.AddWithValue("$pattern", (object?)pattern ?? DBNull.Value);
        command.Parameters.AddWithValue("$deviceId", (object?)deviceId ?? DBNull.Value);
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    internal static string? NullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    internal static int? NullableInt(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    internal static T? NullableJson<T>(SqliteDataReader reader, int ordinal) where T : class =>
        reader.IsDBNull(ordinal) ? null : ProjectEntityMapper.Deserialize<T>(reader.GetString(ordinal));

    private static KeyNotFoundException Missing(ProjectNodeKind kind, string id) =>
        new($"找不到 {kind} 节点 {id}。");
}

internal sealed class SqliteProjectReadSnapshot : IProjectReadSnapshot
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;
    private bool _disposed;
    private readonly ProjectSecretVault _vault;

    public ProjectRevision Revision { get; }

    public SqliteProjectReadSnapshot(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProjectRevision revision,
        ProjectSecretVault vault)
    {
        _connection = connection;
        _transaction = transaction;
        Revision = revision;
        _vault = vault;
    }

    public async IAsyncEnumerable<ProjectEntityDocument> StreamEntitiesAsync(
        ProjectNodeKind kind,
        string? parentId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        IAsyncEnumerable<ProjectEntityDocument> stream = kind switch
        {
            ProjectNodeKind.Channel => StreamChannelsAsync(cancellationToken),
            ProjectNodeKind.Device => StreamDevicesAsync(parentId, cancellationToken),
            ProjectNodeKind.Group => StreamGroupsAsync(parentId, cancellationToken),
            ProjectNodeKind.Tag => StreamTagsAsync(parentId, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        await foreach (ProjectEntityDocument document in stream.ConfigureAwait(false))
            yield return document;
    }

    private async IAsyncEnumerable<ProjectEntityDocument> StreamChannelsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = """
            SELECT id,sort_order,name,driver_id,description,settings_json,kep_properties_json
            FROM IGW_channels ORDER BY sort_order,rowid;
            """;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var channel = new ChannelDef
            {
                Id = reader.GetString(0), Name = reader.GetString(2), DriverId = reader.GetString(3),
                Description = SqliteProjectRepository.NullableString(reader, 4),
                Settings = ProjectEntityMapper.Deserialize<Dictionary<string, string>>(reader.GetString(5)),
                KepProperties = ReadKepProperties(reader, 6),
            };
            yield return new ProjectEntityDocument(
                new ProjectNodeRef(ProjectNodeKind.Channel, channel.Id), null, reader.GetInt32(1),
                ProjectEntityMapper.Serialize(channel));
        }
    }

    private async IAsyncEnumerable<ProjectEntityDocument> StreamDevicesAsync(
        string? parentId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = """
            SELECT id,channel_id,sort_order,name,endpoint,description,settings_json,template_ref,delta_json,kep_properties_json
            FROM IGW_devices WHERE ($parent IS NULL OR channel_id=$parent)
            ORDER BY channel_id,sort_order,rowid;
            """;
        command.Parameters.AddWithValue("$parent", (object?)parentId ?? DBNull.Value);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var device = new DeviceDef
            {
                Id = reader.GetString(0), Name = reader.GetString(3), Endpoint = reader.GetString(4),
                Description = SqliteProjectRepository.NullableString(reader, 5),
                Settings = ProjectEntityMapper.Deserialize<Dictionary<string, string>>(reader.GetString(6)),
                TemplateRef = SqliteProjectRepository.NullableString(reader, 7),
                Delta = reader.IsDBNull(8) ? null : ProjectEntityMapper.Deserialize<DeviceDelta>(reader.GetString(8)),
                KepProperties = ReadKepProperties(reader, 9),
            };
            yield return new ProjectEntityDocument(
                new ProjectNodeRef(ProjectNodeKind.Device, device.Id), reader.GetString(1), reader.GetInt32(2),
                ProjectEntityMapper.Serialize(device));
        }
    }

    private async IAsyncEnumerable<ProjectEntityDocument> StreamGroupsAsync(
        string? parentId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = """
            SELECT id,COALESCE(parent_group_id,device_id),sort_order,name,description,kep_properties_json
            FROM IGW_tag_groups
            WHERE ($parent IS NULL OR parent_group_id=$parent OR (parent_group_id IS NULL AND device_id=$parent))
            ORDER BY device_id,parent_group_id,sort_order,rowid;
            """;
        command.Parameters.AddWithValue("$parent", (object?)parentId ?? DBNull.Value);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var group = new TagGroupDef
            {
                Id = reader.GetString(0), Name = reader.GetString(3),
                Description = SqliteProjectRepository.NullableString(reader, 4),
                KepProperties = ReadKepProperties(reader, 5),
            };
            yield return new ProjectEntityDocument(
                new ProjectNodeRef(ProjectNodeKind.Group, group.Id), reader.GetString(1), reader.GetInt32(2),
                ProjectEntityMapper.Serialize(group));
        }
    }

    private async IAsyncEnumerable<ProjectEntityDocument> StreamTagsAsync(
        string? parentId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = """
            SELECT t.id,COALESCE(t.group_id,t.device_id),t.sort_order,t.name,t.address,t.data_type,t.access,
                   t.scan_rate_ms,t.description,t.eng_unit,t.string_length,t.text_encoding,o.is_array,o.array_json,
                   o.scaling_json,o.deadband,o.deadband_type,o.metadata_json,o.alarm_json,o.write_confirmation_json,
                   o.kep_properties_json
            FROM IGW_tags t INNER JOIN IGW_tag_options o ON o.tag_id=t.id
            WHERE ($parent IS NULL OR t.group_id=$parent OR (t.group_id IS NULL AND t.device_id=$parent))
            ORDER BY t.device_id,t.group_id,t.sort_order,t.rowid;
            """;
        command.Parameters.AddWithValue("$parent", (object?)parentId ?? DBNull.Value);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var tag = new TagDef
            {
                Id = reader.GetString(0), Name = reader.GetString(3), Address = reader.GetString(4),
                DataType = (IgwDataType)reader.GetInt32(5), Access = (TagAccess)reader.GetInt32(6),
                ScanRateMs = reader.GetInt32(7), Description = SqliteProjectRepository.NullableString(reader, 8),
                EngUnit = SqliteProjectRepository.NullableString(reader, 9),
                StringLength = reader.IsDBNull(10) ? null : reader.GetInt32(10),
                TextEncoding = reader.IsDBNull(11) ? null : (PlcTextEncoding)reader.GetInt32(11),
                IsArray = reader.GetInt32(12) != 0,
                Array = SqliteProjectRepository.NullableJson<ArrayDef>(reader, 13),
                Scaling = SqliteProjectRepository.NullableJson<ScalingDef>(reader, 14),
                Deadband = reader.GetDouble(15), DeadbandType = (DeadbandType)reader.GetInt32(16),
                Metadata = SqliteProjectRepository.NullableJson<Dictionary<string, string>>(reader, 17),
                Alarm = SqliteProjectRepository.NullableJson<AlarmDef>(reader, 18),
                WriteConfirmation = SqliteProjectRepository.NullableJson<WriteConfirmationDef>(reader, 19),
                KepProperties = ReadKepProperties(reader, 20),
            };
            yield return new ProjectEntityDocument(
                new ProjectNodeRef(ProjectNodeKind.Tag, tag.Id), reader.GetString(1), reader.GetInt32(2),
                ProjectEntityMapper.Serialize(tag));
        }
    }

    public async IAsyncEnumerable<ProjectDocument> StreamDocumentsAsync(
        ProjectDocumentKind kind,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        (string sql, ProjectDocumentKind documentKind) = kind switch
        {
            ProjectDocumentKind.ProjectSection => ("SELECT section_key,document_json,row_version FROM IGW_project_sections ORDER BY section_key", kind),
            ProjectDocumentKind.MesTask => ("SELECT id,flow_document_json,row_version FROM IGW_mes_tasks ORDER BY rowid", kind),
            ProjectDocumentKind.FlowTemplate => ("SELECT id,template_document_json,row_version FROM IGW_flow_templates ORDER BY rowid", kind),
            ProjectDocumentKind.DeviceTemplate => ("SELECT id,template_document_json,row_version FROM IGW_device_templates ORDER BY rowid", kind),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        await using var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = sql;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            yield return new ProjectDocument(documentKind, reader.GetString(0), reader.GetString(1), reader.GetInt64(2));
    }

    public async IAsyncEnumerable<ProjectPortableSecret> StreamPortableSecretsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await using var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = """
            SELECT owner_type,owner_id,secret_name,ciphertext
            FROM IGW_secret_entries ORDER BY owner_type,owner_id,secret_name;
            """;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            yield return new ProjectPortableSecret(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), _vault.Reveal(reader.GetString(3)));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _transaction.DisposeAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static Dictionary<string, System.Text.Json.JsonElement>? ReadKepProperties(
        SqliteDataReader reader,
        int ordinal)
    {
        Dictionary<string, System.Text.Json.JsonElement>? properties =
            SqliteProjectRepository.NullableJson<Dictionary<string, System.Text.Json.JsonElement>>(reader, ordinal);
        return properties is { Count: > 0 } ? properties : null;
    }
}
