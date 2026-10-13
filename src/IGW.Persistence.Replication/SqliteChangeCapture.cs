using Microsoft.Data.Sqlite;

namespace IGW.Persistence.Replication;

/// <summary>本机 SQLite 表结构(用于在主库建同构表和同步行)。</summary>
public sealed record LocalColumn(string Name, string DeclaredType, bool NotNull, int PrimaryKeyOrdinal);

public sealed record LocalTable(string Name, IReadOnlyList<LocalColumn> Columns)
{
    /// <summary>没有显式主键的表用 rowid 作为同步键(主库里对应 igw_rowid 列)。</summary>
    public bool UsesRowId => Columns.All(column => column.PrimaryKeyOrdinal == 0);

    /// <summary>SQLite 允许非整数主键列存 NULL；这类表在主库不能建主键约束，改用普通索引并按 IS NULL 匹配。</summary>
    public bool HasNullableKey => !UsesRowId && KeyColumns.Any(column =>
        !column.NotNull && !(KeyColumns.Count == 1 && column.DeclaredType.Equals("INTEGER", StringComparison.OrdinalIgnoreCase)));

    public IReadOnlyList<LocalColumn> KeyColumns =>
        Columns.Where(column => column.PrimaryKeyOrdinal > 0).OrderBy(column => column.PrimaryKeyOrdinal).ToList();
}

/// <summary>
/// 在本机 SQLite 上记录每一次增删改：每张业务表挂三个触发器，把(表名, 操作, 主键)写进待同步队列 IGW_outbox。
/// 远端数据库断开时队列自然积压，恢复后按顺序补传；从主库回灌数据时用 suppress 标志暂停记录，避免回环。
/// </summary>
public static class SqliteChangeCapture
{
    public const string OutboxTable = "IGW_outbox";
    public const string StateTable = "IGW_sync";

    public static bool IsReplicated(string table) =>
        table.StartsWith("IGW_", StringComparison.OrdinalIgnoreCase)
        && !table.Equals(OutboxTable, StringComparison.OrdinalIgnoreCase)
        && !table.Equals(StateTable, StringComparison.OrdinalIgnoreCase)
        && !table.Equals(ServerMirror.MetaTable, StringComparison.OrdinalIgnoreCase);

    public static void EnsureInfrastructure(SqliteConnection connection)
    {
        IGW.Core.IgwSqliteTableNamespace.Ensure(connection, ["_igw_outbox", "_igw_sync"]);
        Execute(connection, $"""
            CREATE TABLE IF NOT EXISTS {OutboxTable}(
                seq INTEGER PRIMARY KEY AUTOINCREMENT,
                tbl TEXT NOT NULL,
                op TEXT NOT NULL,
                pk TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS {StateTable}(key TEXT PRIMARY KEY, value TEXT);
            """);
        NormalizePendingTables(connection, ReadTables(connection));
    }

    /// <summary>ALTER TABLE updates trigger table references, but SQLite deliberately leaves quoted tbl payloads unchanged.</summary>
    private static void NormalizePendingTables(SqliteConnection connection, IReadOnlyList<LocalTable> tables)
    {
        using var transaction = connection.BeginTransaction();
        foreach (var table in tables.Where(table => table.Name.StartsWith("IGW_", StringComparison.OrdinalIgnoreCase)))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"UPDATE {OutboxTable} SET tbl=$physical WHERE tbl=$logical COLLATE NOCASE OR tbl=$oldPrefix COLLATE NOCASE OR tbl=$prefixed COLLATE NOCASE;";
            command.Parameters.AddWithValue("$physical", table.Name);
            command.Parameters.AddWithValue("$logical", ServerMirror.LogicalName(table.Name));
            command.Parameters.AddWithValue("$oldPrefix", "_igw_" + ServerMirror.LogicalName(table.Name));
            command.Parameters.AddWithValue("$prefixed", "igw_" + ServerMirror.LogicalName(table.Name));
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public static IReadOnlyList<LocalTable> ReadTables(SqliteConnection connection)
    {
        var names = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                if (IsReplicated(reader.GetString(0))) names.Add(reader.GetString(0));
        }
        var tables = new List<LocalTable>();
        foreach (string name in names)
        {
            var columns = new List<LocalColumn>();
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({Quote(name)});";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                columns.Add(new LocalColumn(reader.GetString(1), reader.IsDBNull(2) ? "" : reader.GetString(2),
                    reader.GetInt32(3) != 0, reader.GetInt32(5)));
            tables.Add(new LocalTable(name, columns));
        }
        return tables;
    }

    /// <summary>为每张业务表补齐三个触发器(已存在则跳过)。返回新挂上触发器的表(它们的历史数据需要整表同步一次)。</summary>
    public static IReadOnlyList<string> EnsureTriggers(SqliteConnection connection, IReadOnlyList<LocalTable> tables)
    {
        var existing = new Dictionary<string, (string Table, string Sql)>(StringComparer.OrdinalIgnoreCase);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name,tbl_name,sql FROM sqlite_master WHERE type='trigger' AND name LIKE '\\_igw\\_%' ESCAPE '\\';";
            using var reader = command.ExecuteReader();
            while (reader.Read()) existing.Add(reader.GetString(0), (reader.GetString(1), reader.GetString(2)));
        }
        var added = new List<string>();
        foreach (LocalTable table in tables)
        {
            string suffix = TriggerSuffix(table.Name);
            string[] expected = ["_igw_ai_" + suffix, "_igw_au_" + suffix, "_igw_ad_" + suffix];
            if (expected.All(name => existing.TryGetValue(name, out var trigger)
                && trigger.Table.Equals(table.Name, StringComparison.OrdinalIgnoreCase)
                && trigger.Sql.Contains(Literal(table.Name), StringComparison.Ordinal)
                && trigger.Sql.Contains(OutboxTable, StringComparison.OrdinalIgnoreCase)))
                continue;
            foreach (string legacy in existing.Where(pair => pair.Value.Table.Equals(table.Name, StringComparison.OrdinalIgnoreCase)
                && (pair.Key.StartsWith("_igw_ai_", StringComparison.OrdinalIgnoreCase)
                    || pair.Key.StartsWith("_igw_au_", StringComparison.OrdinalIgnoreCase)
                    || pair.Key.StartsWith("_igw_ad_", StringComparison.OrdinalIgnoreCase))).Select(pair => pair.Key))
                Execute(connection, $"DROP TRIGGER {Quote(legacy)};");
            string q = Quote(table.Name);
            string literal = Literal(table.Name);
            string newKey = KeyExpression(table, "NEW");
            string oldKey = KeyExpression(table, "OLD");
            const string active = $"(SELECT value FROM {StateTable} WHERE key='suppress') IS NOT '1'";
            Execute(connection, $"""
                DROP TRIGGER IF EXISTS _igw_ai_{suffix};
                DROP TRIGGER IF EXISTS _igw_au_{suffix};
                DROP TRIGGER IF EXISTS _igw_ad_{suffix};
                CREATE TRIGGER _igw_ai_{suffix} AFTER INSERT ON {q} WHEN {active} BEGIN
                    INSERT INTO {OutboxTable}(tbl,op,pk) VALUES({literal},'u',{newKey});
                END;
                CREATE TRIGGER _igw_au_{suffix} AFTER UPDATE ON {q} WHEN {active} BEGIN
                    INSERT INTO {OutboxTable}(tbl,op,pk) SELECT {literal},'d',{oldKey} WHERE {oldKey} IS NOT {newKey};
                    INSERT INTO {OutboxTable}(tbl,op,pk) VALUES({literal},'u',{newKey});
                END;
                CREATE TRIGGER _igw_ad_{suffix} AFTER DELETE ON {q} WHEN {active} BEGIN
                    INSERT INTO {OutboxTable}(tbl,op,pk) VALUES({literal},'d',{oldKey});
                END;
                """);
            added.Add(table.Name);
        }
        return added;
    }

    public static string? GetState(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT value FROM {StateTable} WHERE key=$key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    public static void SetState(SqliteConnection connection, string key, string? value, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = value is null
            ? $"DELETE FROM {StateTable} WHERE key=$key;"
            : $"INSERT INTO {StateTable}(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value;";
        command.Parameters.AddWithValue("$key", key);
        if (value is not null) command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    internal static string KeyExpression(LocalTable table, string alias) => table.UsesRowId
        ? $"json_array({alias}.rowid)"
        : $"json_array({string.Join(",", table.KeyColumns.Select(column => alias + "." + Quote(column.Name)))})";

    internal static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private static string Literal(string value) => "'" + value.Replace("'", "''") + "'";

    private static string TriggerSuffix(string table)
    {
        var chars = table.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray();
        return new string(chars);
    }

    internal static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
