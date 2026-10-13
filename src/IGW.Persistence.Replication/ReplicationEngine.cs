using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace IGW.Persistence.Replication;

/// <summary>一个本机 SQLite 文件。Authoritative=配置类(主库为准，不一致时从主库拉回)；否则为运行数据(只往主库推)。</summary>
public sealed record ReplicationSource(string Name, string DatabasePath, bool Authoritative);

public sealed record ReplicationSourceStatus(string Name, string State, long Pending, string? Message);

public sealed record ReplicationStatus(
    string DatabaseKind,
    string Target,
    /// <summary>sqlite / syncing / online / offline</summary>
    string State,
    string? Message,
    long Pending,
    DateTimeOffset? LastSyncUtc,
    IReadOnlyList<ReplicationSourceStatus> Sources)
{
    public static ReplicationStatus LocalOnly { get; } =
        new("sqlite", "SQLite(本机)", "sqlite", null, 0, null, []);
}

/// <summary>
/// 本机 SQLite 为工作库和断线缓存，用户选择的数据库为主库：
/// 启动时握手(主库为空→整表推送；主库数据与本机不同代→配置类从主库拉回)，
/// 运行中后台按待同步队列把增删改推到主库；主库断开时继续本机运行，恢复后自动补传。
/// </summary>
public sealed class ReplicationEngine : IAsyncDisposable, IGW.Core.IDatabaseSync
{
    private const int BatchSize = 5000;
    /// <summary>每轮从待同步队列取出的条数：批量注册一次记下十万级主键，取大些减少轮次。</summary>
    private const int DrainBatchSize = 20000;
    /// <summary>
    /// 某张表积压的不同主键既超过此数、又超过表行数的一半时改为整表同步(导入/替换整个工程)；
    /// 只改了大表一部分(如百万点工程里批量注册一台设备)时按主键批量替换，不重传整表。
    /// </summary>
    private const int FullCopyThreshold = 20000;

    private readonly DatabaseSettings _settings;
    private readonly IReadOnlyList<ReplicationSource> _sources;
    private ILogger _log;
    private readonly Dictionary<string, SourceState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private volatile string _state = "syncing";
    private volatile string? _message;
    private DateTimeOffset? _lastSync;

    public ReplicationEngine(DatabaseSettings settings, IReadOnlyList<ReplicationSource> sources, ILogger log)
    {
        _settings = settings;
        _sources = sources;
        _log = log;
        foreach (var source in sources) _states[source.Name] = new SourceState(source);
    }

    /// <summary>主库断开或配置类数据尚未与主库对齐时，禁止修改配置(避免本机与主库分叉)。</summary>
    public string? ConfigurationWriteBlockedReason =>
        _state == "offline"
            ? $"主数据库({_settings})当前无法连接，网关正在使用本机缓存运行，暂不能修改配置。连接恢复后会自动同步并解除限制。"
            : _states.Values.FirstOrDefault(state => state.Source.Authoritative && state.NeedsRestart) is { } stale
                ? $"主数据库中的{stale.Source.Name}数据已被其他地方修改，请重启网关服务后再修改配置。"
                : null;

    public ReplicationStatus Status
    {
        get
        {
            var sources = _states.Values.Select(state => new ReplicationSourceStatus(
                state.Source.Name, state.State, state.Pending, state.Message)).ToList();
            return new ReplicationStatus(DatabaseSettings.DisplayName(_settings.Kind), _settings.ToString(), _state,
                _message, sources.Sum(source => source.Pending), _lastSync, sources);
        }
    }

    /// <summary>
    /// 启动握手：必须在网关读取工程之前完成(配置类可能要从主库拉回)。主库连不上时返回 false，网关用本机缓存运行。
    /// </summary>
    public async Task<bool> InitializeAsync(CancellationToken ct)
    {
        try
        {
            // 数据库被人删掉(或安装后才换了库名)时自动重建；账号没有建库权限则交给后面的连接报错。
            try { await DatabaseConnector.EnsureDatabaseAsync(_settings, ct).ConfigureAwait(false); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { _log.LogDebug(ex, "确认主数据库存在时出错"); }
            await Task.Run(() =>
            {
                using var mirror = new ServerMirror(_settings);
                mirror.Ping();
                mirror.EnsureMetaTable();
                foreach (var state in _states.Values)
                    if (File.Exists(state.Source.DatabasePath)) Handshake(mirror, state, startup: true, deferPush: true);
            }, ct).ConfigureAwait(false);
            _state = _states.Values.Any(state => state.FullPushPending || !state.HandshakeDone && File.Exists(state.Source.DatabasePath)) ? "syncing" : "online";
            _message = null;
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _state = "offline";
            _message = ex.Message;
            _log.LogWarning("无法连接主数据库 {target}，网关使用本机缓存运行：{message}", _settings, ex.Message);
            return false;
        }
    }

    public void Start() => _loop ??= Task.Run(() => LoopAsync(_stop.Token));

    /// <summary>网关启动早期还没有日志系统，建好后换成正式日志。</summary>
    public void UseLogger(ILogger logger) => _log = logger;

    string IGW.Core.IDatabaseSync.State => _state;

    object IGW.Core.IDatabaseSync.Snapshot() => Status;

    /// <summary>有新的修改(例如工程保存)时立即同步，不等下一个周期。</summary>
    public void Nudge() => _signal.Release();

    private async Task LoopAsync(CancellationToken ct)
    {
        int backoffMs = 2000;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                bool more = RunOnce(ct);
                backoffMs = 2000;
                if (more) continue;
                await _signal.WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (_state != "offline")
                    _log.LogWarning("主数据库同步中断，本机继续运行并排队待同步数据：{message}", ex.Message);
                _state = "offline";
                _message = ex.Message;
                try { await Task.Delay(backoffMs, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                backoffMs = Math.Min(30_000, backoffMs * 2);
            }
        }
    }

    /// <summary>执行一轮：对每个本机文件完成握手(首次出现的文件/表)并排空一批待同步队列。返回是否还有积压。</summary>
    public bool RunOnce(CancellationToken ct)
    {
        using var mirror = new ServerMirror(_settings);
        mirror.Ping();
        if (_state == "offline")
        {
            mirror.EnsureMetaTable();
            _log.LogInformation("主数据库 {target} 已恢复连接，开始补传本机积压的数据", _settings);
        }
        bool more = false;
        foreach (var state in _states.Values)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(state.Source.DatabasePath)) continue;
            if (!state.HandshakeDone || _state == "offline") Handshake(mirror, state, startup: false, deferPush: false);
            if (!state.HandshakeDone) continue; // A lazy local store has not enrolled its owned tables yet.
            if (state.NeedsRestart) continue;
            more |= Drain(mirror, state, ct);
        }
        _state = more || _states.Values.Any(state => state.FullPushPending || !state.HandshakeDone && File.Exists(state.Source.DatabasePath)) ? "syncing" : "online";
        _message = null;
        _lastSync = DateTimeOffset.UtcNow;
        return more;
    }

    // ---------------------------------------------------------------- 握手

    /// <param name="deferPush">启动时为 true：主库为空需要整表推送时只挂好触发器并标记待推送，由后台完成，不拖慢网关启动。</param>
    private void Handshake(ServerMirror mirror, SourceState state, bool startup, bool deferPush)
    {
        using var connection = Open(state.Source.DatabasePath);
        SqliteChangeCapture.EnsureInfrastructure(connection);
        var tables = SqliteChangeCapture.ReadTables(connection);
        if (tables.Count == 0)
        {
            // Auxiliary stores initialize later or lazily. Their unrenamed local tables are not
            // replication input yet; keep generation and pending deletes until the owner migrates them.
            state.HandshakeDone = false;
            state.Pending = PendingCount(connection);
            state.State = "syncing";
            return;
        }
        var newlyCaptured = SqliteChangeCapture.EnsureTriggers(connection, tables);
        state.Tables = tables.ToDictionary(table => table.Name, StringComparer.OrdinalIgnoreCase);

        string? local = SqliteChangeCapture.GetState(connection, "generation");
        string? server = mirror.PrepareSource(state.Source.Name, tables);
        foreach (var table in tables) mirror.EnsureTable(ServerMirror.MirrorName(state.Source.Name, table.Name), table);

        if (server is null)
        {
            if (deferPush)
            {
                state.FullPushPending = true;
                state.State = "syncing";
                return;
            }
            // 主库里还没有这份数据：整表推送一次，建立同步代次。
            FullPush(mirror, connection, state, tables);
            string generation = Guid.NewGuid().ToString("N");
            mirror.SetGeneration(state.Source.Name, generation);
            SqliteChangeCapture.SetState(connection, "generation", generation);
        }
        else if (!string.Equals(local, server, StringComparison.Ordinal))
        {
            if (state.Source.Authoritative)
            {
                if (!startup)
                {
                    // 运行中发现主库被别处改过：配置已载入内存，不能在运行中替换，提示重启。
                    state.NeedsRestart = true;
                    state.State = "needsRestart";
                    return;
                }
                // 保护：主库有代次记录但表里一行数据都没有(被人清空或建表失败)，不能拿空数据覆盖本机配置。
                bool serverHasData = tables.Any(table =>
                {
                    string name = ServerMirror.MirrorName(state.Source.Name, table.Name);
                    return mirror.TableExists(name) && mirror.Count(name) > 0;
                });
                bool localHasData = tables.Any(table => CountRows(connection, table.Name) > 0);
                if (!serverHasData && localHasData)
                {
                    _log.LogWarning("主数据库中的 {source} 为空，拒绝用空数据覆盖本机配置，改为把本机数据推送到主数据库", state.Source.Name);
                    FullPush(mirror, connection, state, tables);
                    string generation = Guid.NewGuid().ToString("N");
                    mirror.SetGeneration(state.Source.Name, generation);
                    SqliteChangeCapture.SetState(connection, "generation", generation);
                }
                else
                {
                    FullPull(mirror, connection, state, tables);
                    SqliteChangeCapture.SetState(connection, "generation", server);
                    state.Pulled = true;
                }
            }
            else
            {
                // 运行数据：本机是新的一代(重装/换电脑)，把本机数据合并进主库，不删除主库原有数据。
                MergePush(mirror, connection, state, tables);
                SqliteChangeCapture.SetState(connection, "generation", server);
            }
        }
        else
        {
            // 同一代：新挂上触发器的表(新版本新增的表)补一次整表同步，其余靠队列增量同步。
            foreach (string name in newlyCaptured)
                if (state.Tables.TryGetValue(name, out var table))
                {
                    if (state.Source.Authoritative) CopyTable(mirror, connection, state, table);
                    else MergeTable(mirror, connection, state, table);
                }
        }
        state.HandshakeDone = true;
        state.FullPushPending = false;
        state.State = "online";
    }

    private void FullPush(ServerMirror mirror, SqliteConnection connection, SourceState state, IReadOnlyList<LocalTable> tables)
    {
        _log.LogInformation("首次同步 {source} 到主数据库：整表推送 {count} 张表", state.Source.Name, tables.Count);
        state.FullPushPending = true;
        state.State = "syncing";
        foreach (var table in tables) CopyTable(mirror, connection, state, table);
    }

    private void MergePush(ServerMirror mirror, SqliteConnection connection, SourceState state, IReadOnlyList<LocalTable> tables)
    {
        foreach (var table in tables)
            MergeTable(mirror, connection, state, table);
        // Keep queued deletes: a row absent locally cannot be represented by an upsert-only merge.
    }

    private void MergeTable(ServerMirror mirror, SqliteConnection connection, SourceState state, LocalTable table)
    {
        string name = ServerMirror.MirrorName(state.Source.Name, table.Name);
        foreach (var chunk in ReadRows(connection, table, null).Chunk(BatchSize))
        {
            var rows = chunk.ToList();
            var keys = rows.Select(row => KeyOf(mirror, table, row)).ToList();
            mirror.Apply(name, table, keys, rows);
        }
    }

    /// <summary>整表复制到主库；复制前记下队列位置，复制期间新产生的修改留在队列里之后再同步。</summary>
    private void CopyTable(ServerMirror mirror, SqliteConnection connection, SourceState state, LocalTable table)
    {
        long watermark = MaxSeq(connection);
        mirror.ReplaceAll(ServerMirror.MirrorName(state.Source.Name, table.Name), table, ReadRows(connection, table, null));
        ClearOutbox(connection, watermark, table.Name);
    }

    /// <summary>主库为准：清空本机表后写入主库数据，写入期间暂停记录修改，避免回传。</summary>
    private void FullPull(ServerMirror mirror, SqliteConnection connection, SourceState state, IReadOnlyList<LocalTable> tables)
    {
        _log.LogInformation("本机的 {source} 与主数据库不一致，以主数据库为准回灌到本机", state.Source.Name);
        using var transaction = connection.BeginTransaction();
        SqliteChangeCapture.SetState(connection, "suppress", "1", transaction);
        SqliteChangeCapture.Execute(connection, "PRAGMA defer_foreign_keys=ON;", transaction);
        foreach (var table in tables)
        {
            string mirrorName = ServerMirror.MirrorName(state.Source.Name, table.Name);
            SqliteChangeCapture.Execute(connection, $"DELETE FROM {SqliteChangeCapture.Quote(table.Name)};", transaction);
            if (!mirror.TableExists(mirrorName)) continue;
            var columns = mirror.ColumnNames(table).ToList();
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            var localColumns = columns.Select(column => column == ServerMirror.RowIdColumn ? "rowid" : SqliteChangeCapture.Quote(column));
            insert.CommandText = $"INSERT INTO {SqliteChangeCapture.Quote(table.Name)} ({string.Join(",", localColumns)}) " +
                $"VALUES ({string.Join(",", columns.Select((_, index) => "$p" + index))});";
            var parameters = columns.Select((_, index) => insert.Parameters.Add("$p" + index, SqliteType.Blob)).ToArray();
            foreach (var row in mirror.ReadAll(mirrorName, table))
            {
                for (int index = 0; index < row.Length; index++)
                {
                    parameters[index].SqliteType = SqliteTypeOf(row[index]);
                    parameters[index].Value = row[index] ?? DBNull.Value;
                }
                insert.ExecuteNonQuery();
            }
        }
        SqliteChangeCapture.Execute(connection, $"DELETE FROM {SqliteChangeCapture.OutboxTable};", transaction);
        SqliteChangeCapture.SetState(connection, "suppress", null, transaction);
        transaction.Commit();
    }

    // ---------------------------------------------------------------- 增量

    private bool Drain(ServerMirror mirror, SourceState state, CancellationToken ct)
    {
        using var connection = Open(state.Source.DatabasePath);
        // 新版本或其他组件新建的表：补挂触发器并整表同步一次。
        var tables = SqliteChangeCapture.ReadTables(connection);
        var enrolled = tables.Where(table => !state.Tables.ContainsKey(table.Name)).ToList();
        // Validate/migrate every newly discovered legacy mirror before creating or writing any target.
        if (enrolled.Count > 0) mirror.PrepareSource(state.Source.Name, enrolled);
        foreach (var table in tables.Where(table => !state.Tables.ContainsKey(table.Name)
            || state.Tables[table.Name].Columns.Count != table.Columns.Count))
        {
            mirror.EnsureTable(ServerMirror.MirrorName(state.Source.Name, table.Name), table);
            if (!state.Tables.ContainsKey(table.Name))
            {
                SqliteChangeCapture.EnsureTriggers(connection, [table]);
                if (state.Source.Authoritative) CopyTable(mirror, connection, state, table);
                else MergeTable(mirror, connection, state, table);
            }
            state.Tables[table.Name] = table;
        }

        var batch = new List<(long Seq, string Table, char Op, string Key)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT seq,tbl,op,pk FROM {SqliteChangeCapture.OutboxTable} ORDER BY seq LIMIT {DrainBatchSize};";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                string table = reader.GetString(1);
                // A writer already in flight during an upgrade can still emit a legacy tbl literal.
                // Resolve aliases only against known IGW business tables; no foreign table is enrolled.
                if (!state.Tables.ContainsKey(table))
                {
                    string physical = IGW.Core.IgwSqliteTableNamespace.PhysicalName(table);
                    if (state.Tables.ContainsKey(physical)) table = physical;
                }
                batch.Add((reader.GetInt64(0), table, reader.GetString(2)[0], reader.GetString(3)));
            }
        }
        if (batch.Count == 0)
        {
            state.Pending = 0;
            return false;
        }
        long maxSeq = batch[^1].Seq;
        foreach (var group in batch.GroupBy(change => change.Table, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (!state.Tables.TryGetValue(group.Key, out var table)) continue;   // 表已被删除
            string mirrorName = ServerMirror.MirrorName(state.Source.Name, table.Name);
            var last = new Dictionary<string, char>(StringComparer.Ordinal);
            foreach (var change in group) last[change.Key] = change.Op;
            if (state.Source.Authoritative && last.Count >= BatchSize && PendingDistinctKeys(connection, table.Name) is var pending
                && pending > FullCopyThreshold && pending * 2 > CountRows(connection, table.Name))
            {
                CopyTable(mirror, connection, state, table);
                continue;
            }
            var deleteKeys = last.Keys.Select(key => ParseKey(key)).ToList();
            var upsertKeys = last.Where(pair => pair.Value == 'u').Select(pair => pair.Key).ToList();
            var rows = ReadRows(connection, table, upsertKeys).ToList();
            mirror.Apply(mirrorName, table, deleteKeys, rows);
        }
        ClearOutbox(connection, maxSeq, null);
        state.Pending = PendingCount(connection);
        return state.Pending > 0;
    }

    private static long PendingDistinctKeys(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(DISTINCT pk) FROM {SqliteChangeCapture.OutboxTable} WHERE tbl=$t;";
        command.Parameters.AddWithValue("$t", table);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static long CountRows(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {SqliteChangeCapture.Quote(table)};";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static long PendingCount(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {SqliteChangeCapture.OutboxTable};";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static long MaxSeq(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COALESCE(MAX(seq),0) FROM {SqliteChangeCapture.OutboxTable};";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void ClearOutbox(SqliteConnection connection, long upToSeq, string? table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {SqliteChangeCapture.OutboxTable} WHERE seq <= $seq" + (table is null ? ";" : " AND tbl=$t;");
        command.Parameters.AddWithValue("$seq", upToSeq);
        if (table is not null) command.Parameters.AddWithValue("$t", table);
        command.ExecuteNonQuery();
    }

    // ---------------------------------------------------------------- 本机读取

    /// <summary>读本机行(列顺序与主库一致；rowid 表首列为 rowid)。keys 为 null 时读整表。</summary>
    private static IEnumerable<object?[]> ReadRows(SqliteConnection connection, LocalTable table, IReadOnlyList<string>? keys)
    {
        var columns = table.Columns.Select(column => SqliteChangeCapture.Quote(column.Name)).ToList();
        if (table.UsesRowId) columns.Insert(0, "rowid");
        string sql = $"SELECT {string.Join(",", columns)} FROM {SqliteChangeCapture.Quote(table.Name)}";
        if (keys is not null)
        {
            if (keys.Count == 0) yield break;
            if (table.UsesRowId)
                sql += " WHERE rowid IN (SELECT value FROM json_each($keys))";
            else if (table.KeyColumns.Count == 1)
                sql += $" WHERE {SqliteChangeCapture.Quote(table.KeyColumns[0].Name)} IN (SELECT value FROM json_each($keys))";
            else
                sql += $" WHERE {SqliteChangeCapture.KeyExpression(table, SqliteChangeCapture.Quote(table.Name))} IN (SELECT value FROM json_each($keys))";
        }
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (keys is not null)
        {
            // 单列主键和 rowid 用标量数组；复合主键用“数组的数组”，与 json_array(...) 的文本形式逐字相同。
            bool scalar = table.UsesRowId || table.KeyColumns.Count == 1;
            string list = scalar
                ? "[" + string.Join(",", keys.Select(key => key[1..^1])) + "]"
                : "[" + string.Join(",", keys.Select(key => JsonSerializer.Serialize(key))) + "]";
            command.Parameters.AddWithValue("$keys", list);
        }
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (int index = 0; index < row.Length; index++) row[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            yield return row;
        }
    }

    private static object?[] ParseKey(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray().Select(element => (object?)(element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt64(out long integer) => integer,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Null => null,
            _ => element.GetRawText(),
        })).ToArray();
    }

    private static object?[] KeyOf(ServerMirror mirror, LocalTable table, object?[] row)
    {
        var columns = mirror.ColumnNames(table).ToList();
        return mirror.KeyNames(table).Select(key => row[columns.IndexOf(key)]).ToArray();
    }

    private static SqliteType SqliteTypeOf(object? value) => value switch
    {
        null or DBNull => SqliteType.Text,
        long or int or short or byte or bool => SqliteType.Integer,
        double or float or decimal => SqliteType.Real,
        byte[] => SqliteType.Blob,
        _ => SqliteType.Text,
    };

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        SqliteChangeCapture.Execute(connection, "PRAGMA busy_timeout=10000;");
        return connection;
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); } catch { }
        }
        _stop.Dispose();
    }

    private sealed class SourceState(ReplicationSource source)
    {
        public ReplicationSource Source { get; } = source;
        public Dictionary<string, LocalTable> Tables { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public bool HandshakeDone { get; set; }
        public bool FullPushPending { get; set; }
        public bool NeedsRestart { get; set; }
        public bool Pulled { get; set; }
        public string State { get; set; } = "pending";
        public long Pending { get; set; }
        public string? Message { get; set; }
    }

    /// <summary>启动握手时本次是否从主库回灌了该来源(网关据此决定是否应用首次安装的端口设置)。</summary>
    public bool WasPulled(string source) => _states.TryGetValue(source, out var state) && state.Pulled;
}
