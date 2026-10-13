using System.Data;
using System.Text;
using SqlSugar;

namespace IGW.Persistence.Replication;

internal sealed record MirrorColumn(string Name, string Type, bool Nullable, bool PrimaryKey);

/// <summary>
/// 主库(SQL Server / PostgreSQL / MySQL)一侧：按本机 SQLite 表结构建同构表、补列，按主键删除/写入行。
/// 所有 SQL 自己拼(标识符按方言加引号、值全部参数化)，SqlSugar 负责连接、事务和批量导入。
/// </summary>
internal sealed class ServerMirror : IDisposable
{
    public const string MetaTable = "IGW_sync_meta";
    public const string RowIdColumn = "igw_rowid";

    private readonly DatabaseSettings _settings;
    private readonly SqlSugarClient _client;
    private string _schema = "dbo";
    private int _mySqlCaseMode;
    private bool _sqlServerCaseInsensitive = true;
    private bool _namingRulesLoaded;
    private IReadOnlyList<string>? _tableNames;

    public ServerMirror(DatabaseSettings settings)
    {
        _settings = settings;
        _client = DatabaseConnector.CreateClient(settings);
        _client.Ado.CommandTimeOut = 300;
    }

    public DatabaseKind Kind => _settings.Kind;

    public static string MirrorName(string source, string table)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        string logical = LogicalName(table).ToLowerInvariant();
        source = source.ToLowerInvariant();
        string raw = source + "_" + logical;
        string name = "IGW_" + new string(raw.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray());
        // Hash unsanitized components and their boundary. Otherwise a-b/a_b and a/b_tags/a_b/tags collide.
        bool needsHash = name.Length > 63 || source.Any(c => !char.IsAsciiLetterOrDigit(c))
            || logical.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_');
        if (!needsHash) return name;
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(source + "\0" + logical)))[..12].ToLowerInvariant();
        return name[..Math.Min(name.Length, 50)] + "_" + hash;
    }

    internal static string LogicalName(string table) => table.StartsWith("_igw_", StringComparison.OrdinalIgnoreCase)
        ? table[5..] : table.StartsWith("igw_", StringComparison.OrdinalIgnoreCase) ? table[4..] : table;

    internal string? PlanLegacyMigration(string source, LocalTable table, string? generation,
        IReadOnlyList<MirrorColumn> legacyColumns, bool targetExists)
    {
        if (string.IsNullOrWhiteSpace(generation) || !SchemaMatches(table, legacyColumns)) return null;
        if (targetExists)
            throw new InvalidDataException($"Both legacy and namespaced IGW mirrors exist for '{source}/{table.Name}'; refusing to merge or overwrite either table.");
        return RenameSql(LegacyMirrorName(source, LogicalName(table.Name)), MirrorName(source, table.Name));
    }

    // Kept solely for finding previously deployed mirrors. New table creation never uses this algorithm.
    internal static string LegacyMirrorName(string source, string table)
    {
        string name = new((source + "_" + table).ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray());
        return name.Length <= 60 ? name : name[..48] + "_" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..11].ToLowerInvariant();
    }

    private string RenameSql(string oldName, string newName)
    {
        if (Kind == DatabaseKind.MySql && _mySqlCaseMode == 2 && oldName.Equals(newName, StringComparison.OrdinalIgnoreCase))
        {
            string temporary = "IGW_namespace_" + Guid.NewGuid().ToString("N");
            return $"RENAME TABLE {Q(oldName)} TO {Q(temporary)}, {Q(temporary)} TO {Q(newName)};";
        }
        return Kind switch
        {
            DatabaseKind.SqlServer => $"EXEC sp_rename N'{(Q(_schema) + "." + Q(oldName)).Replace("'", "''")}', N'{newName.Replace("'", "''")}', N'OBJECT';",
            DatabaseKind.MySql => $"RENAME TABLE {Q(oldName)} TO {Q(newName)};",
            _ => $"ALTER TABLE {Q(oldName)} RENAME TO {Q(newName)};",
        };
    }

    private bool SchemaMatches(LocalTable table, IReadOnlyList<MirrorColumn> actual)
    {
        var keys = KeyNames(table);
        var expected = table.Columns.Select(column =>
        {
            bool key = keys.Contains(column.Name, StringComparer.OrdinalIgnoreCase);
            return new MirrorColumn(column.Name, ColumnType(column, key, keys.Count), !(key && !table.HasNullableKey), key && !table.HasNullableKey);
        }).ToList();
        if (table.UsesRowId) expected.Insert(0, new MirrorColumn(RowIdColumn, "BIGINT", false, true));
        return SchemaMatches(expected, actual);
    }

    private static bool SchemaMatches(IReadOnlyList<MirrorColumn> expected, IReadOnlyList<MirrorColumn> actual) =>
        expected.Count == actual.Count && expected.All(column => actual.Any(candidate =>
            candidate.Name.Equals(column.Name, StringComparison.OrdinalIgnoreCase)
            && NormalizeType(candidate.Type) == NormalizeType(column.Type)
            && candidate.Nullable == column.Nullable && candidate.PrimaryKey == column.PrimaryKey));

    internal bool IsOwnedMetadata(IReadOnlyList<MirrorColumn> actual) => SchemaMatches(
        [new MirrorColumn("source", KeyText(1), false, true), new MirrorColumn("generation", KeyText(1), true, false),
            new MirrorColumn("updated_utc", KeyText(1), true, false)], actual);

    internal bool TableIdentifiersEqual(string left, string right, int mySqlCaseMode) =>
        left.Equals(right, Kind == DatabaseKind.MySql && mySqlCaseMode is 1 or 2
            || Kind == DatabaseKind.SqlServer && _sqlServerCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    internal string? ResolveCatalogTableName(string name, IReadOnlyList<string> names, int mySqlCaseMode) =>
        names.FirstOrDefault(candidate => candidate.Equals(name, StringComparison.Ordinal))
            ?? names.FirstOrDefault(candidate => TableIdentifiersEqual(candidate, name, mySqlCaseMode));

    private static string NormalizeType(string type)
    {
        string value = type.ToUpperInvariant().Replace(" ", "");
        // Older MySQL reports a BIGINT display width, which does not change the BIGINT storage type.
        return value is "BIGINT(20)" ? "BIGINT" : value;
    }

    public string Q(string identifier) => Kind switch
    {
        DatabaseKind.SqlServer => "[" + identifier.Replace("]", "]]") + "]",
        DatabaseKind.MySql => "`" + identifier.Replace("`", "``") + "`",
        _ => "\"" + identifier.Replace("\"", "\"\"") + "\"",
    };

    /// <summary>单条语句的参数上限(SQL Server 为 2100)。</summary>
    private int MaxParameters => Kind == DatabaseKind.SqlServer ? 2000 : 20000;

    public void Ping()
    {
        _client.Ado.GetScalar("SELECT 1");
        EnsureNamingRules();
    }

    private void EnsureNamingRules()
    {
        if (_namingRulesLoaded) return;
        if (Kind == DatabaseKind.MySql) _mySqlCaseMode = Convert.ToInt32(_client.Ado.GetScalar("SELECT @@lower_case_table_names"));
        if (Kind == DatabaseKind.SqlServer)
        {
            _schema = Convert.ToString(_client.Ado.GetScalar("SELECT SCHEMA_NAME()")) ?? "dbo";
            _sqlServerCaseInsensitive = Convert.ToInt32(_client.Ado.GetScalar("SELECT CASE WHEN 'a' COLLATE DATABASE_DEFAULT = 'A' COLLATE DATABASE_DEFAULT THEN 1 ELSE 0 END")) == 1;
        }
        _namingRulesLoaded = true;
    }

    // ---------------------------------------------------------------- 元数据

    public void EnsureMetaTable()
    {
        EnsureNamingRules();
        string? current = FindTable(MetaTable);
        string? legacy = FindTable("igw_sync_meta");
        if (current is not null && !IsOwnedMetadata(ReadMirrorColumns(current)))
            throw new InvalidDataException($"Table '{current}' does not have the IGW synchronization metadata schema.");
        // Mode 1 stores names in lowercase. SQL still uses IGW_ consistently, and the server resolves it case-insensitively.
        if (Kind == DatabaseKind.MySql && _mySqlCaseMode == 1 && current is not null) return;
        if (legacy is not null && !legacy.Equals(MetaTable, StringComparison.Ordinal)
            && IsOwnedMetadata(ReadMirrorColumns(legacy)))
        {
            if (current is not null && !current.Equals(legacy, StringComparison.Ordinal))
                throw new InvalidDataException("Both legacy and current IGW synchronization metadata tables exist.");
            _client.Ado.ExecuteCommand(RenameSql(legacy, MetaTable));
            _tableNames = null;
            return;
        }
        if (current is not null) return;
        _client.Ado.ExecuteCommand(
            $"CREATE TABLE {Q(MetaTable)} ({Q("source")} {KeyText(1)} NOT NULL PRIMARY KEY, {Q("generation")} {KeyText(1)} NULL, {Q("updated_utc")} {KeyText(1)} NULL)");
        _tableNames = null;
    }

    /// <summary>主库记录的同步代次；没有记录时返回 null(注意 Convert.ToString(null) 会得到空串，必须显式处理)。</summary>
    public string? GetGeneration(string source)
    {
        object? value = _client.Ado.GetScalar(
            $"SELECT {Q("generation")} FROM {Q(MetaTable)} WHERE {Q("source")} = @s", new SugarParameter("@s", source));
        string? text = value is null or DBNull ? null : Convert.ToString(value);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    public void SetGeneration(string source, string generation)
    {
        _client.Ado.ExecuteCommand($"DELETE FROM {Q(MetaTable)} WHERE {Q("source")} = @s", new SugarParameter("@s", source));
        _client.Ado.ExecuteCommand(
            $"INSERT INTO {Q(MetaTable)} ({Q("source")},{Q("generation")},{Q("updated_utc")}) VALUES (@s,@g,@u)",
            new SugarParameter("@s", source), new SugarParameter("@g", generation),
            new SugarParameter("@u", DateTime.UtcNow.ToString("O")));
    }

    // ---------------------------------------------------------------- 表结构

    public bool TableExists(string name) => FindTable(name) is not null;

    // Direct catalog SQL avoids ORM identifier folding and limits discovery to the active schema/database.
    private string? FindTable(string name)
    {
        string sql = Kind switch
        {
            DatabaseKind.SqlServer => "SELECT name FROM sys.tables WHERE schema_id=SCHEMA_ID()",
            DatabaseKind.MySql => "SELECT table_name FROM information_schema.tables WHERE table_schema=DATABASE() AND table_type='BASE TABLE'",
            _ => "SELECT tablename FROM pg_tables WHERE schemaname=current_schema()",
        };
        var names = _tableNames ??= _client.Ado.SqlQuery<string>(sql);
        return ResolveCatalogTableName(name, names, _mySqlCaseMode);
    }

    private IReadOnlyList<MirrorColumn> ReadMirrorColumns(string table)
    {
        // MySQL mode 1 stores uppercase SQL names in lowercase in information_schema.
        // Bind the actual catalog spelling when comparing it with BINARY for ownership checks.
        table = FindTable(table) ?? table;
        string sql = Kind switch
        {
            DatabaseKind.SqlServer => """
                SELECT c.name,
                    CASE WHEN ty.name IN ('nvarchar','varchar','varbinary') THEN UPPER(ty.name)+'('+CASE WHEN c.max_length=-1 THEN 'MAX' ELSE CAST(CASE WHEN ty.name='nvarchar' THEN c.max_length/2 ELSE c.max_length END AS varchar(10)) END+')'
                         ELSE UPPER(ty.name) END,
                    c.is_nullable,
                    CAST(CASE WHEN EXISTS(SELECT 1 FROM sys.index_columns ic JOIN sys.indexes i ON i.object_id=ic.object_id AND i.index_id=ic.index_id WHERE i.object_id=t.object_id AND i.is_primary_key=1 AND ic.column_id=c.column_id) THEN 1 ELSE 0 END AS bit)
                FROM sys.tables t JOIN sys.columns c ON c.object_id=t.object_id JOIN sys.types ty ON ty.user_type_id=c.user_type_id
                WHERE t.schema_id=SCHEMA_ID() AND t.name=@n ORDER BY c.column_id
                """,
            DatabaseKind.MySql => """
                SELECT column_name,column_type,is_nullable='YES',column_key='PRI'
                FROM information_schema.columns WHERE table_schema=DATABASE() AND BINARY table_name=BINARY @n ORDER BY ordinal_position
                """,
            _ => """
                SELECT a.attname,format_type(a.atttypid,a.atttypmod),NOT a.attnotnull,
                    EXISTS(SELECT 1 FROM pg_index i WHERE i.indrelid=c.oid AND i.indisprimary AND a.attnum=ANY(i.indkey))
                FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace JOIN pg_attribute a ON a.attrelid=c.oid
                WHERE n.nspname=current_schema() AND c.relname=@n AND a.attnum>0 AND NOT a.attisdropped ORDER BY a.attnum
                """,
        };
        using var reader = _client.Ado.GetDataReader(sql, new SugarParameter("@n", table));
        var columns = new List<MirrorColumn>();
        while (reader.Read()) columns.Add(new MirrorColumn(reader.GetString(0), reader.GetString(1),
            Convert.ToBoolean(reader.GetValue(2)), Convert.ToBoolean(reader.GetValue(3))));
        return columns;
    }

    /// <summary>Migrate only legacy mirrors proven by this source's generation and their exact expected schema.</summary>
    public string? PrepareSource(string source, IReadOnlyList<LocalTable> tables)
    {
        string? generation = GetGeneration(source);
        if (generation is null) return null;
        var renames = new List<(string Old, string New, string Sql)>();
        bool hasUnownedLegacy = false;
        bool hasCurrentMirror = false;
        foreach (var table in tables)
        {
            string target = MirrorName(source, table.Name);
            bool targetExists = TableExists(target);
            hasCurrentMirror |= targetExists;
            string legacy = LegacyMirrorName(source, LogicalName(table.Name));
            string? actual = FindTable(legacy);
            if (actual is null) continue;
            string? sql = PlanLegacyMigration(source, table, generation, ReadMirrorColumns(actual), targetExists);
            if (sql is null) { hasUnownedLegacy = true; continue; }
            renames.Add((actual, target, RenameSql(actual, target)));
        }
        if (hasUnownedLegacy && renames.Count > 0)
            throw new InvalidDataException($"Legacy mirrors for '{source}' have mixed ownership/schema evidence. No legacy tables were renamed; resolve the conflicting schema before synchronization.");
        // Preflight every schema/collision before DDL. MySQL multi-table RENAME is atomic; other providers use a DDL transaction.
        if (renames.Count > 0)
        {
            if (Kind == DatabaseKind.MySql)
                _client.Ado.ExecuteCommand("RENAME TABLE " + string.Join(",", renames.Select(rename => $"{Q(rename.Old)} TO {Q(rename.New)}")));
            else
            {
                try
                {
                    _client.Ado.BeginTran();
                    foreach (var rename in renames) _client.Ado.ExecuteCommand(rename.Sql);
                    _client.Ado.CommitTran();
                }
                catch { try { _client.Ado.RollbackTran(); } catch { } throw; }
            }
            _tableNames = null;
        }
        // An unrelated legacy table must never cause an authoritative local database to be replaced by empty new mirrors.
        return hasUnownedLegacy && !hasCurrentMirror ? null : generation;
    }

    /// <summary>建表或补齐本机新增的列(本机升级后多出来的列在主库自动补上，全部允许为空)。</summary>
    public void EnsureTable(string mirror, LocalTable table)
    {
        if (!TableExists(mirror))
        {
            var parts = new List<string>();
            var keys = table.UsesRowId ? [RowIdColumn] : table.KeyColumns.Select(column => column.Name).ToList();
            if (table.UsesRowId) parts.Add($"{Q(RowIdColumn)} BIGINT NOT NULL");
            bool nullableKey = table.HasNullableKey;
            foreach (LocalColumn column in table.Columns)
            {
                bool key = keys.Contains(column.Name, StringComparer.OrdinalIgnoreCase);
                bool notNull = key && !nullableKey;
                parts.Add($"{Q(column.Name)} {ColumnType(column, key, keys.Count)} {(notNull ? "NOT NULL" : "NULL")}");
            }
            if (!nullableKey) parts.Add($"PRIMARY KEY ({string.Join(",", keys.Select(Q))})");
            _client.Ado.ExecuteCommand($"CREATE TABLE {Q(mirror)} ({string.Join(", ", parts)})");
            _tableNames = null;
            if (nullableKey)
                _client.Ado.ExecuteCommand($"CREATE INDEX {Q(MirrorName("index", mirror))} ON {Q(mirror)} ({string.Join(",", keys.Select(Q))})");
            return;
        }
        var existing = ReadMirrorColumns(mirror).Select(column => column.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (LocalColumn column in table.Columns.Where(column => !existing.Contains(column.Name)))
        {
            string add = Kind == DatabaseKind.SqlServer ? "ADD" : "ADD COLUMN";
            _client.Ado.ExecuteCommand($"ALTER TABLE {Q(mirror)} {add} {Q(column.Name)} {ColumnType(column, false, 0)} NULL");
        }
    }

    private static string Affinity(string declared)
    {
        string type = declared.ToUpperInvariant();
        if (type.Contains("INT")) return "INTEGER";
        if (type.Contains("REAL") || type.Contains("FLOA") || type.Contains("DOUB")) return "REAL";
        if (type.Contains("BLOB")) return "BLOB";
        return "TEXT";
    }

    private string KeyText(int keyCount) => Kind switch
    {
        DatabaseKind.SqlServer => keyCount > 1 ? "NVARCHAR(200)" : "NVARCHAR(450)",
        DatabaseKind.MySql => keyCount > 1 ? "VARCHAR(190)" : "VARCHAR(255)",
        _ => "TEXT",
    };

    private string ColumnType(LocalColumn column, bool key, int keyCount) => Affinity(column.DeclaredType) switch
    {
        "INTEGER" => "BIGINT",
        "REAL" => Kind switch { DatabaseKind.SqlServer => "FLOAT", DatabaseKind.MySql => "DOUBLE", _ => "DOUBLE PRECISION" },
        "BLOB" => Kind switch { DatabaseKind.SqlServer => "VARBINARY(MAX)", DatabaseKind.MySql => "LONGBLOB", _ => "BYTEA" },
        _ when key => KeyText(keyCount),
        _ => Kind switch { DatabaseKind.SqlServer => "NVARCHAR(MAX)", DatabaseKind.MySql => "LONGTEXT", _ => "TEXT" },
    };

    // ---------------------------------------------------------------- 行同步

    public IReadOnlyList<string> KeyNames(LocalTable table) =>
        table.UsesRowId ? [RowIdColumn] : table.KeyColumns.Select(column => column.Name).ToList();

    public IReadOnlyList<string> ColumnNames(LocalTable table)
    {
        var names = table.Columns.Select(column => column.Name).ToList();
        if (table.UsesRowId) names.Insert(0, RowIdColumn);
        return names;
    }

    /// <summary>行数达到此值时改用 SqlBulkCopy / 二进制 COPY 写入(SQL Server 每条语句只能带约 2000 个参数，多行 INSERT 太慢)。</summary>
    private const int BulkInsertRows = 500;

    /// <summary>
    /// 先按主键删除，再写入最新行(删除+插入 = 与方言无关的“合并”)。
    /// 一般在一个事务里完成；行数多且主库支持批量导入时，删除提交后再批量写入：
    /// 中途失败时待同步队列不清，下一轮重做(删除+插入可重复执行)，主库短暂缺少这些行。
    /// </summary>
    public void Apply(string mirror, LocalTable table, IReadOnlyList<object?[]> deleteKeys, IReadOnlyList<object?[]> rows)
    {
        if (deleteKeys.Count == 0 && rows.Count == 0) return;
        if (rows.Count >= BulkInsertRows && Kind is DatabaseKind.SqlServer or DatabaseKind.PostgreSql)
        {
            if (Kind == DatabaseKind.SqlServer && deleteKeys.Count >= BulkInsertRows && deleteKeys.All(key => key.All(part => part is not null)))
                DeleteKeysSqlServer(mirror, table, deleteKeys);
            else
                Apply(mirror, table, deleteKeys, []);
            var columns = ColumnNames(table);
            var types = ParameterTypes(table);
            foreach (var chunk in rows.Chunk(5000))
            {
                if (Kind == DatabaseKind.SqlServer) BulkCopySqlServer(mirror, columns, types, chunk);
                else CopyPostgres(mirror, columns, types, chunk);
            }
            return;
        }
        try
        {
            _client.Ado.BeginTran();
            DeleteKeys(mirror, table, deleteKeys);
            InsertRows(mirror, table, rows);
            _client.Ado.CommitTran();
        }
        catch
        {
            try { _client.Ado.RollbackTran(); } catch { }
            throw;
        }
    }

    /// <summary>
    /// SQL Server 大批量按主键删除：每条语句只能带约 2000 个参数，拆成几十条 IN(...) 很慢；
    /// 改为把主键批量导入会话临时表，再一条 DELETE … JOIN 删除(临时表列用库默认排序规则，避免与 tempdb 冲突)。
    /// </summary>
    private void DeleteKeysSqlServer(string mirror, LocalTable table, IReadOnlyList<object?[]> keys)
    {
        var keyNames = KeyNames(table);
        var keyTypes = table.UsesRowId
            ? ["BIGINT"]
            : table.KeyColumns.Select(column => ColumnType(column, key: true, keyNames.Count)).ToList();
        using var connection = new Microsoft.Data.SqlClient.SqlConnection(_settings.ConnectionString());
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE #IGW_keys (" + string.Join(",", keyTypes.Select((type, index) =>
                $"k{index} {type}" + (type.StartsWith("NVARCHAR", StringComparison.Ordinal) ? " COLLATE DATABASE_DEFAULT" : "") + " NOT NULL")) + ")";
            create.ExecuteNonQuery();
        }
        var data = new DataTable("#IGW_keys");
        for (int index = 0; index < keyTypes.Count; index++)
            data.Columns.Add("k" + index, keyTypes[index] == "BIGINT" ? typeof(long)
                : keyTypes[index] == "FLOAT" ? typeof(double)
                : keyTypes[index].StartsWith("VARBINARY", StringComparison.Ordinal) ? typeof(byte[]) : typeof(string));
        foreach (var key in keys)
            data.Rows.Add(key.Select((value, index) => Coerce(value!, data.Columns[index].DataType)).ToArray());
        using (var bulk = new Microsoft.Data.SqlClient.SqlBulkCopy(connection) { DestinationTableName = "#IGW_keys", BulkCopyTimeout = 300 })
            bulk.WriteToServer(data);
        using var delete = connection.CreateCommand();
        delete.CommandTimeout = 300;
        delete.CommandText = $"DELETE t FROM {Q(mirror)} t JOIN #IGW_keys k ON " +
            string.Join(" AND ", keyNames.Select((name, index) => $"t.{Q(name)} = k.k{index}"));
        delete.ExecuteNonQuery();
    }

    private void DeleteKeys(string mirror, LocalTable table, IReadOnlyList<object?[]> keys)
    {
        if (keys.Count == 0) return;
        var keyNames = KeyNames(table);
        int perStatement = Math.Max(1, MaxParameters / keyNames.Count);
        foreach (var chunk in keys.Chunk(perStatement))
        {
            var parameters = new List<SugarParameter>();
            string where;
            if (keyNames.Count == 1 && chunk.All(key => key[0] is not null))
            {
                var names = chunk.Select((key, index) =>
                {
                    string name = "@k" + index;
                    parameters.Add(new SugarParameter(name, key[0] ?? DBNull.Value));
                    return name;
                });
                where = $"{Q(keyNames[0])} IN ({string.Join(",", names)})";
            }
            else
            {
                var clauses = chunk.Select((key, row) => "(" + string.Join(" AND ", keyNames.Select((column, index) =>
                {
                    if (key[index] is null) return $"{Q(column)} IS NULL";   // 可空主键列
                    string name = $"@k{row}_{index}";
                    parameters.Add(new SugarParameter(name, key[index]));
                    return $"{Q(column)} = {name}";
                })) + ")");
                where = string.Join(" OR ", clauses);
            }
            _client.Ado.ExecuteCommand($"DELETE FROM {Q(mirror)} WHERE {where}", parameters);
        }
    }

    /// <summary>每列的参数类型：空值也要带类型，否则 SQL Server 会把 NULL 当字符串写进二进制列而报错。</summary>
    private static System.Data.DbType[] ParameterTypes(LocalTable table)
    {
        var types = table.Columns.Select(column => Affinity(column.DeclaredType) switch
        {
            "INTEGER" => System.Data.DbType.Int64,
            "REAL" => System.Data.DbType.Double,
            "BLOB" => System.Data.DbType.Binary,
            _ => System.Data.DbType.String,
        }).ToList();
        if (table.UsesRowId) types.Insert(0, System.Data.DbType.Int64);
        return types.ToArray();
    }

    private void InsertRows(string mirror, LocalTable table, IReadOnlyList<object?[]> rows)
    {
        if (rows.Count == 0) return;
        var columns = ColumnNames(table);
        var types = ParameterTypes(table);
        int perStatement = Math.Max(1, Math.Min(1000, MaxParameters / columns.Count));
        string head = $"INSERT INTO {Q(mirror)} ({string.Join(",", columns.Select(Q))}) VALUES ";
        foreach (var chunk in rows.Chunk(perStatement))
        {
            var parameters = new List<SugarParameter>(chunk.Length * columns.Count);
            var values = new StringBuilder();
            for (int row = 0; row < chunk.Length; row++)
            {
                if (row > 0) values.Append(',');
                values.Append('(');
                for (int column = 0; column < columns.Count; column++)
                {
                    if (column > 0) values.Append(',');
                    string name = $"@v{row}_{column}";
                    values.Append(name);
                    object? value = chunk[row][column];
                    parameters.Add(value is null or DBNull
                        ? new SugarParameter(name, DBNull.Value) { DbType = types[column] }
                        : new SugarParameter(name, value));
                }
                values.Append(')');
            }
            _client.Ado.ExecuteCommand(head + values, parameters);
        }
    }

    /// <summary>整表替换：清空主库表后分批写入。SQL Server 用 SqlBulkCopy，PostgreSQL 用二进制 COPY，MySQL 用多行 INSERT。</summary>
    public void ReplaceAll(string mirror, LocalTable table, IEnumerable<object?[]> rows, int batchSize = 5000)
    {
        _client.Ado.ExecuteCommand($"DELETE FROM {Q(mirror)}");
        var columns = ColumnNames(table);
        var types = ParameterTypes(table);
        foreach (var chunk in rows.Chunk(batchSize))
        {
            if (Kind == DatabaseKind.SqlServer) BulkCopySqlServer(mirror, columns, types, chunk);
            else if (Kind == DatabaseKind.PostgreSql) CopyPostgres(mirror, columns, types, chunk);
            else InsertRows(mirror, table, chunk);
        }
    }

    private void BulkCopySqlServer(string mirror, IReadOnlyList<string> columns, System.Data.DbType[] types, IReadOnlyList<object?[]> rows)
    {
        var data = new DataTable(mirror);
        for (int column = 0; column < columns.Count; column++)
            data.Columns.Add(columns[column], types[column] switch
            {
                System.Data.DbType.Int64 => typeof(long),
                System.Data.DbType.Double => typeof(double),
                System.Data.DbType.Binary => typeof(byte[]),
                _ => typeof(string),
            });
        foreach (var row in rows)
            data.Rows.Add(row.Select((value, column) => value is null ? DBNull.Value : Coerce(value, data.Columns[column].DataType)).ToArray());
        using var bulk = new Microsoft.Data.SqlClient.SqlBulkCopy(_settings.ConnectionString())
        {
            DestinationTableName = Q(mirror),
            BulkCopyTimeout = 300,
            BatchSize = rows.Count,
        };
        foreach (DataColumn column in data.Columns) bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        bulk.WriteToServer(data);
    }

    private void CopyPostgres(string mirror, IReadOnlyList<string> columns, System.Data.DbType[] types, IReadOnlyList<object?[]> rows)
    {
        using var connection = new Npgsql.NpgsqlConnection(_settings.ConnectionString());
        connection.Open();
        using var writer = connection.BeginBinaryImport(
            $"COPY {Q(mirror)} ({string.Join(",", columns.Select(Q))}) FROM STDIN (FORMAT BINARY)");
        foreach (var row in rows)
        {
            writer.StartRow();
            for (int column = 0; column < columns.Count; column++)
            {
                object? value = row[column];
                if (value is null or DBNull) { writer.WriteNull(); continue; }
                switch (types[column])
                {
                    case System.Data.DbType.Int64: writer.Write((long)Coerce(value, typeof(long)), NpgsqlTypes.NpgsqlDbType.Bigint); break;
                    case System.Data.DbType.Double: writer.Write((double)Coerce(value, typeof(double)), NpgsqlTypes.NpgsqlDbType.Double); break;
                    case System.Data.DbType.Binary: writer.Write((byte[])value, NpgsqlTypes.NpgsqlDbType.Bytea); break;
                    default: writer.Write((string)Coerce(value, typeof(string)), NpgsqlTypes.NpgsqlDbType.Text); break;
                }
            }
        }
        writer.Complete();
    }

    /// <summary>SQLite 列是动态类型(同一列可能存了整数或文本)，写入强类型的主库列前统一转换。</summary>
    private static object Coerce(object value, Type target) => target switch
    {
        _ when value.GetType() == target => value,
        _ when target == typeof(string) => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "",
        _ when target == typeof(long) => Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture),
        _ when target == typeof(double) => Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture),
        _ => value,
    };

    /// <summary>读出主库整表(回灌本机用)，按本机列顺序返回。</summary>
    public IEnumerable<object?[]> ReadAll(string mirror, LocalTable table)
    {
        var columns = ColumnNames(table);
        using var reader = _client.Ado.GetDataReader(
            $"SELECT {string.Join(",", columns.Select(Q))} FROM {Q(mirror)}");
        while (reader.Read())
        {
            var row = new object?[columns.Count];
            for (int index = 0; index < columns.Count; index++)
                row[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            yield return row;
        }
    }

    public long Count(string mirror) => Convert.ToInt64(_client.Ado.GetScalar($"SELECT COUNT(*) FROM {Q(mirror)}"));

    public void Dispose() => _client.Dispose();
}
