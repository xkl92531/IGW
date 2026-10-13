using Microsoft.Data.Sqlite;

namespace IGW.Persistence.Sqlite;

/// <summary>
/// 选择外部主数据库时，IGW.Persistence.Replication 在本机库上建待同步队列并给每张业务表挂触发器，逐行记录增删改。
/// 批量注册点位一次写十万行级，逐行触发器会让写库慢约 4 倍，并把写事务拖过其它写者的锁等待上限。
/// 批量写入因此在自己的事务里摘掉这几张表的触发器，把写过的主键记进临时表，提交前用一条集合语句写入队列：
/// 队列内容与触发器写出的逐字相同(按主键 'u')，同步引擎无需区分来源。
/// 表名与格式须与 SqliteChangeCapture 一致(有测试核对)。
/// </summary>
public static class ReplicationOutbox
{
    public const string OutboxTable = "IGW_outbox";

    /// <summary>本机库是否已挂上复制队列(只有选择了外部数据库才有)。</summary>
    internal static async Task<bool> IsInstalledAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name;";
        command.Parameters.AddWithValue("$name", OutboxTable);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    /// <summary>
    /// 在本事务内摘掉 tag_groups/tags/tag_options 上的复制触发器(提交前原样恢复)，并准备记录写过的主键的临时表。
    /// 只设暂停标志不够：被暂停的触发器每行仍要判断一次条件，十万行级仍多花数秒。
    /// SQLite 的 DDL 随事务提交/回滚，BEGIN IMMEDIATE 期间也没有其它写者会漏记。返回被摘掉的触发器定义。
    /// </summary>
    internal static async Task<IReadOnlyList<string>> BeginAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        var triggers = new List<(string Name, string Sql)>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT name, sql FROM sqlite_master
                WHERE type='trigger' AND tbl_name IN ('IGW_tag_groups','IGW_tags','IGW_tag_options') AND name LIKE '\_igw\_%' ESCAPE '\';
                """;
            await using SqliteDataReader reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                triggers.Add((reader.GetString(0), reader.GetString(1)));
        }
        var sql = new System.Text.StringBuilder("""
            CREATE TEMP TABLE IF NOT EXISTS IGW_bulk_touched_tags(id TEXT PRIMARY KEY) WITHOUT ROWID;
            CREATE TEMP TABLE IF NOT EXISTS IGW_bulk_touched_groups(id TEXT PRIMARY KEY) WITHOUT ROWID;
            DELETE FROM IGW_bulk_touched_tags;
            DELETE FROM IGW_bulk_touched_groups;
            """);
        foreach ((string name, _) in triggers)
            sql.Append("DROP TRIGGER main.\"").Append(name.Replace("\"", "\"\"")).Append("\";");
        await ExecAsync(connection, transaction, sql.ToString(), cancellationToken).ConfigureAwait(false);
        return triggers.Select(trigger => trigger.Sql).ToList();
    }

    /// <summary>恢复摘掉的触发器，并把写过的点(tags + tag_options)和组一次性记入队列。</summary>
    internal static async Task EndAsync(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<string> triggers, CancellationToken cancellationToken)
    {
        foreach (string trigger in triggers)
            await ExecAsync(connection, transaction, trigger + ";", cancellationToken).ConfigureAwait(false);
        await ExecAsync(connection, transaction, $"""
            INSERT INTO {OutboxTable}(tbl,op,pk) SELECT 'IGW_tag_groups','u',json_array(id) FROM IGW_bulk_touched_groups;
            INSERT INTO {OutboxTable}(tbl,op,pk) SELECT 'IGW_tags','u',json_array(id) FROM IGW_bulk_touched_tags;
            INSERT INTO {OutboxTable}(tbl,op,pk) SELECT 'IGW_tag_options','u',json_array(id) FROM IGW_bulk_touched_tags;
            DELETE FROM IGW_bulk_touched_tags;
            DELETE FROM IGW_bulk_touched_groups;
            """, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecAsync(
        SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
