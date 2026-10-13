using Microsoft.Data.Sqlite;

namespace IGW.Persistence.Sqlite;

/// <summary>工程 SQLite 连接工厂。每个连接都启用相同的完整性和并发参数。</summary>
public sealed class ProjectDatabase
{
    public string FilePath { get; }

    public ProjectDatabase(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = Path.GetFullPath(filePath);
    }

    public Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken) =>
        OpenAsync(cancellationToken, privateCache: false);

    /// <summary>
    /// <paramref name="privateCache"/>=true 时用私有页缓存打开（其余 PRAGMA 相同）。共享缓存下写事务持有表级锁，
    /// 同进程其它共享缓存连接读同一张表会拿到 SQLITE_LOCKED 并一直等到写事务结束；私有缓存连接走普通 WAL 锁，
    /// 读者按快照并发读取。写者之间仍由 BEGIN IMMEDIATE + busy_timeout 串行。只给长写事务（批量注册点位）使用。
    /// </summary>
    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken, bool privateCache)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = privateCache ? SqliteCacheMode.Private : SqliteCacheMode.Shared,
            // 工程导入需要在关闭连接后原子替换数据库；连接池不能继续占用旧文件句柄。
            Pooling = false,
        }.ToString());

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA foreign_keys=ON;
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                PRAGMA busy_timeout=5000;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
