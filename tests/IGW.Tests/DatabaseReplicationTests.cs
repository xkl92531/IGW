using IGW.Persistence.Replication;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

/// <summary>
/// 需要真实数据库服务端时才运行：设置环境变量 IGW_DB_TESTS=1，并可用
/// IGW_DB_TEST_PASSWORD 指定本机测试库密码(默认 IgwTest#2026)。未设置时测试显示为“已跳过”。
/// </summary>
public sealed class DatabaseFactAttribute : TheoryAttribute
{
    public DatabaseFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("IGW_DB_TESTS") != "1")
            Skip = "未设置 IGW_DB_TESTS=1：跳过需要 SQL Server/PostgreSQL/MySQL 服务端的测试。";
    }
}

public sealed class DatabaseReplicationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "igw-repl-" + Guid.NewGuid().ToString("N"));

    public DatabaseReplicationTests() => Directory.CreateDirectory(_dir);

    public static TheoryData<string> Kinds => new() { "sqlServer", "postgreSql", "mySql" };

    private static DatabaseSettings Settings(string kind, string database)
    {
        string password = Environment.GetEnvironmentVariable("IGW_DB_TEST_PASSWORD") ?? "IgwTest#2026";
        return kind switch
        {
            "sqlServer" => new DatabaseSettings(DatabaseKind.SqlServer, "127.0.0.1", 1433, database, "sa", password),
            "postgreSql" => new DatabaseSettings(DatabaseKind.PostgreSql, "127.0.0.1", 5432, database, "postgres", password),
            _ => new DatabaseSettings(DatabaseKind.MySql, "127.0.0.1", 3306, database, "root", password),
        };
    }

    private string CreateSource(string name)
    {
        string path = Path.Combine(_dir, name + ".db");
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        Exec(connection, """
            CREATE TABLE IGW_tags(id TEXT PRIMARY KEY, name TEXT NOT NULL, address TEXT, scan_rate_ms INTEGER, deadband REAL, blob_data BLOB);
            CREATE TABLE IGW_mes_task_index(task_id TEXT NOT NULL, node_type TEXT NOT NULL, referenced_tag_id TEXT NULL, referenced_path TEXT, PRIMARY KEY(task_id, node_type, referenced_tag_id));
            CREATE TABLE IGW_upload_log(ts TEXT, message TEXT);
            INSERT INTO IGW_tags VALUES('t1','A','DB1.DBW0',100,0.5,X'0102'),('t2','B','DB1.DBW2',200,NULL,NULL);
            INSERT INTO IGW_mes_task_index VALUES('m1','Payload',NULL,'C.D.A'),('m1','Trigger','t1','C.D.B');
            INSERT INTO IGW_upload_log VALUES('2026-09-19','第一条'),('2026-09-19','第二条');
            """);
        return path;
    }

    [DatabaseFact]
    [MemberData(nameof(Kinds))]
    public async Task Connection_test_reports_success_and_wrong_password_clearly(string kind)
    {
        var settings = Settings(kind, "igw_conn_test");
        Assert.True((await DatabaseConnector.TestAsync(settings)).Ok);
        var wrong = await DatabaseConnector.TestAsync(settings with { Password = "wrong-password" });
        Assert.Equal(DatabaseTestStatus.AuthenticationFailed, wrong.Status);
        var closed = await DatabaseConnector.TestAsync(settings with { Host = "127.0.0.1", Port = 1 });
        Assert.Equal(DatabaseTestStatus.Unreachable, closed.Status);
    }

    [DatabaseFact]
    [MemberData(nameof(Kinds))]
    public async Task Local_changes_replicate_and_a_fresh_machine_pulls_configuration_back(string kind)
    {
        string database = "igw_repl_" + Guid.NewGuid().ToString("N")[..8];
        var settings = Settings(kind, database);
        await DatabaseConnector.EnsureDatabaseAsync(settings);
        try
        {
            string path = CreateSource("project");
            await using (var engine = new ReplicationEngine(settings, [new ReplicationSource("project", path, true)], NullLogger.Instance))
            {
                Assert.True(await engine.InitializeAsync(default));
                engine.RunOnce(default);   // 后台整表推送
                using (var connection = Open(path))
                {
                    Exec(connection, """
                        UPDATE IGW_tags SET name='A2' WHERE id='t1';
                        DELETE FROM IGW_tags WHERE id='t2';
                        INSERT INTO IGW_tags VALUES('t3','C','DB1.DBW4',100,1.25,NULL);
                        DELETE FROM IGW_mes_task_index WHERE node_type='Trigger';
                        INSERT INTO IGW_upload_log VALUES('2026-09-19','第三条');
                        """);
                }
                engine.RunOnce(default);
                Assert.Equal("online", engine.Status.State);
                Assert.Equal(0, engine.Status.Pending);
            }

            using (var mirror = new ServerMirrorProbe(settings))
            {
                Assert.Equal(["A2", "C"], mirror.Strings("IGW_project_tags", "name"));
                Assert.Equal(1, mirror.Count("IGW_project_mes_task_index"));
                Assert.Equal(3, mirror.Count("IGW_project_upload_log"));
            }

            // 换一台电脑：本机是全新的空库(同样的表结构)，启动时以主库为准拉回配置。
            string fresh = Path.Combine(_dir, "fresh");
            Directory.CreateDirectory(fresh);
            string freshPath = Path.Combine(fresh, "project.db");
            using (var connection = new SqliteConnection($"Data Source={freshPath};Pooling=False"))
            {
                connection.Open();
                Exec(connection, """
                    CREATE TABLE IGW_tags(id TEXT PRIMARY KEY, name TEXT NOT NULL, address TEXT, scan_rate_ms INTEGER, deadband REAL, blob_data BLOB);
                    CREATE TABLE IGW_mes_task_index(task_id TEXT NOT NULL, node_type TEXT NOT NULL, referenced_tag_id TEXT NULL, referenced_path TEXT, PRIMARY KEY(task_id, node_type, referenced_tag_id));
                    CREATE TABLE IGW_upload_log(ts TEXT, message TEXT);
                    INSERT INTO IGW_tags VALUES('initial','默认','',100,0,NULL);
                    """);
            }
            await using (var engine = new ReplicationEngine(settings, [new ReplicationSource("project", freshPath, true)], NullLogger.Instance))
            {
                Assert.True(await engine.InitializeAsync(default));
                Assert.True(engine.WasPulled("project"));
            }
            using (var connection = Open(freshPath))
            {
                Assert.Equal(["A2", "C"], Strings(connection, "SELECT name FROM IGW_tags ORDER BY name"));
                Assert.Equal([1.25], Doubles(connection, "SELECT deadband FROM IGW_tags WHERE id='t3'"));
                Assert.Equal(3L, Scalar(connection, "SELECT COUNT(*) FROM IGW_upload_log"));
                Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM IGW_outbox"));
            }
        }
        finally
        {
            DropDatabase(settings);
        }
    }

    [DatabaseFact]
    [MemberData(nameof(Kinds))]
    public async Task Server_outage_keeps_local_changes_queued_and_blocks_configuration_edits(string kind)
    {
        string database = "igw_repl_" + Guid.NewGuid().ToString("N")[..8];
        var settings = Settings(kind, database);
        await DatabaseConnector.EnsureDatabaseAsync(settings);
        try
        {
            string path = CreateSource("project");
            var unreachable = settings with { Port = 1 };
            await using (var offline = new ReplicationEngine(unreachable, [new ReplicationSource("project", path, true)], NullLogger.Instance))
            {
                Assert.False(await offline.InitializeAsync(default));
                Assert.Equal("offline", offline.Status.State);
                Assert.NotNull(offline.ConfigurationWriteBlockedReason);
            }

            await using var engine = new ReplicationEngine(settings, [new ReplicationSource("project", path, true)], NullLogger.Instance);
            Assert.True(await engine.InitializeAsync(default));
            engine.RunOnce(default);
            Assert.Null(engine.ConfigurationWriteBlockedReason);
            using var mirror = new ServerMirrorProbe(settings);
            Assert.Equal(2, mirror.Count("IGW_project_tags"));
        }
        finally
        {
            DropDatabase(settings);
        }
    }

    [DatabaseFact]
    [MemberData(nameof(Kinds))]
    public async Task Connection_loss_while_running_queues_changes_and_catches_up_after_recovery(string kind)
    {
        string database = "igw_repl_" + Guid.NewGuid().ToString("N")[..8];
        var direct = Settings(kind, database);
        await DatabaseConnector.EnsureDatabaseAsync(direct);
        using var proxy = new TcpProxy(direct.EffectivePort);
        var settings = direct with { Port = proxy.Port };
        try
        {
            string path = CreateSource("project");
            await using var engine = new ReplicationEngine(settings, [new ReplicationSource("project", path, true)], NullLogger.Instance);
            Assert.True(await engine.InitializeAsync(default));
            engine.RunOnce(default);

            proxy.Stop();   // 网络断开：本机继续写，修改排队
            using (var connection = Open(path))
                Exec(connection, "INSERT INTO IGW_tags VALUES('t9','断网期间新增','DB9.DBW0',100,0,NULL); INSERT INTO IGW_upload_log VALUES('2026-09-19','断网期间日志');");
            Assert.ThrowsAny<Exception>(() => engine.RunOnce(default));
            using (var connection = Open(path)) Assert.Equal(2L, Scalar(connection, "SELECT COUNT(*) FROM IGW_outbox"));

            proxy.Start();  // 网络恢复：补传
            engine.RunOnce(default);
            using var mirror = new ServerMirrorProbe(direct);
            Assert.Contains("断网期间新增", mirror.Strings("IGW_project_tags", "name"));
            Assert.Equal(3, mirror.Count("IGW_project_upload_log"));
            using (var connection = Open(path)) Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM IGW_outbox"));
        }
        finally
        {
            DropDatabase(direct);
        }
    }

    /// <summary>可随时断开/恢复的 TCP 转发，模拟网络中断。</summary>
    private sealed class TcpProxy : IDisposable
    {
        private readonly int _target;
        private System.Net.Sockets.TcpListener? _listener;
        private readonly List<System.Net.Sockets.TcpClient> _clients = [];
        public int Port { get; }

        public TcpProxy(int target)
        {
            _target = target;
            var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            Port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Start();
        }

        public void Start()
        {
            _listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, Port);
            _listener.Start();
            var listener = _listener;
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    System.Net.Sockets.TcpClient inbound;
                    try { inbound = await listener.AcceptTcpClientAsync(); } catch { return; }
                    var outbound = new System.Net.Sockets.TcpClient();
                    try { await outbound.ConnectAsync(System.Net.IPAddress.Loopback, _target); }
                    catch { inbound.Dispose(); outbound.Dispose(); continue; }
                    lock (_clients) { _clients.Add(inbound); _clients.Add(outbound); }
                    _ = Pump(inbound, outbound);
                    _ = Pump(outbound, inbound);
                }
            });
        }

        private static async Task Pump(System.Net.Sockets.TcpClient from, System.Net.Sockets.TcpClient to)
        {
            try { await from.GetStream().CopyToAsync(to.GetStream()); } catch { }
            try { from.Dispose(); to.Dispose(); } catch { }
        }

        public void Stop()
        {
            _listener?.Stop();
            lock (_clients)
            {
                foreach (var client in _clients) try { client.Dispose(); } catch { }
                _clients.Clear();
            }
            // 连接池里的旧连接已被切断，清掉以免复用
            Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
            Npgsql.NpgsqlConnection.ClearAllPools();
            MySqlConnector.MySqlConnection.ClearAllPools();
        }

        public void Dispose() => Stop();
    }

    /// <summary>
    /// 真实工程库上的批量注册点位：写库时摘掉触发器、事后一次性记队列；主库端按主键批量替换，不整表重传。
    /// 规模可用 IGW_DB_BULK_SEED / IGW_DB_BULK_ITEMS 放大做性能测量(默认 6 万 + 2.5 万，超过整表阈值但不到表的一半)。
    /// </summary>
    [DatabaseFact]
    [MemberData(nameof(Kinds))]
    public async Task Bulk_registration_replicates_by_key_without_resending_the_table(string kind)
    {
        int seed = int.TryParse(Environment.GetEnvironmentVariable("IGW_DB_BULK_SEED"), out int s) ? s : 60_000;
        int items = int.TryParse(Environment.GetEnvironmentVariable("IGW_DB_BULK_ITEMS"), out int i) ? i : 25_000;
        string database = "igw_bulk_" + Guid.NewGuid().ToString("N")[..8];
        var settings = Settings(kind, database);
        await DatabaseConnector.EnsureDatabaseAsync(settings);
        await using var fx = await ProjectBulkTagWriterTests.Fixture.CreateAsync(new IGW.Abstractions.IgwProject());
        try
        {
            for (int offset = 0; offset < seed; offset += 100_000)
            {
                var request = BulkRequest("Dev0", offset, Math.Min(100_000, seed - offset));
                Assert.True((await fx.Repository.BulkUpsertTagsAsync(request, null, default)).Applied);
            }
            await using var engine = new ReplicationEngine(settings,
                [new ReplicationSource("project", fx.DatabasePath, true)], NullLogger.Instance);
            Assert.True(await engine.InitializeAsync(default));
            while (engine.RunOnce(default)) { }   // 首次整表推送

            // 新设备注册一批点，并改写已有设备里的 1000 个点
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Assert.True((await fx.Repository.BulkUpsertTagsAsync(BulkRequest("Dev1", 0, items), null, default)).Applied);
            var changed = BulkRequest("Dev0", 0, 1000);
            foreach (var item in changed.Items!) item.Description = "改过";
            Assert.True((await fx.Repository.BulkUpsertTagsAsync(changed, null, default)).Applied);
            long writeMs = watch.ElapsedMilliseconds;
            watch.Restart();
            int rounds = 1;
            while (engine.RunOnce(default)) rounds++;
            long drainMs = watch.ElapsedMilliseconds;
            Assert.Equal("online", engine.Status.State);

            using var local = Open(fx.DatabasePath);
            using var mirror = new ServerMirrorProbe(settings);
            Assert.Equal(Scalar(local, "SELECT COUNT(*) FROM IGW_tags"), mirror.Count("IGW_project_tags"));
            Assert.Equal(Scalar(local, "SELECT COUNT(*) FROM IGW_tag_options"), mirror.Count("IGW_project_tag_options"));
            Assert.Equal(Scalar(local, "SELECT COUNT(*) FROM IGW_tag_groups"), mirror.Count("IGW_project_tag_groups"));
            Assert.Equal(1000, mirror.Count("IGW_project_tags", "description", "改过"));
            Assert.Equal(0L, Scalar(local, "SELECT COUNT(*) FROM IGW_outbox"));
            if (Environment.GetEnvironmentVariable("IGW_DB_BULK_REPORT") is { Length: > 0 } report)
                File.AppendAllText(report, $"[{kind}] 预置 {seed} 点，批量注册 {items} 点 + 改 1000 点：写库 {writeMs} ms，同步到主库 {drainMs} ms（{rounds} 轮）" + Environment.NewLine);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DropDatabase(settings);
        }
    }

    private static IGW.Abstractions.ProjectBulkTagRequest BulkRequest(string device, int offset, int count)
    {
        var request = ProjectBulkTagWriterTests.Request();
        request.Device = request.Device with { Name = device };
        request.Items = Enumerable.Range(offset, count)
            .Select(index => ProjectBulkTagWriterTests.Item("T" + index, "G" + index / 1000)).ToList();
        return request;
    }

    [DatabaseFact]
    [MemberData(nameof(Kinds))]
    public async Task Owned_legacy_remote_tables_are_migrated_before_a_fresh_machine_pulls_configuration(string kind)
    {
        var settings = Settings(kind, "igw_legacy_" + Guid.NewGuid().ToString("N")[..8]);
        await DatabaseConnector.EnsureDatabaseAsync(settings);
        try
        {
            string source = CreateSource("legacy-seed");
            using (var local = Open(source)) Exec(local, "UPDATE IGW_tags SET name='remote-authority' WHERE id='t1';");
            await using (var engine = new ReplicationEngine(settings, [new ReplicationSource("project", source, true)], NullLogger.Instance))
            {
                Assert.True(await engine.InitializeAsync(default));
                while (engine.RunOnce(default)) { }
            }
            using (var client = DatabaseConnector.CreateClient(settings))
            {
                foreach (string logical in new[] { "tags", "mes_task_index", "upload_log" })
                    RenameRemote(client, settings.Kind, "IGW_project_" + logical, "project_" + logical);
                // PostgreSQL keeps the old and new metadata spellings distinct. SQL Server/MySQL may resolve them as the same object.
                if (settings.Kind == DatabaseKind.PostgreSql) RenameRemote(client, settings.Kind, "IGW_sync_meta", "igw_sync_meta");
            }
            string fresh = CreateSource("legacy-fresh");
            await using (var engine = new ReplicationEngine(settings, [new ReplicationSource("project", fresh, true)], NullLogger.Instance))
            {
                Assert.True(await engine.InitializeAsync(default));
                Assert.True(engine.WasPulled("project"));
            }
            using var connection = Open(fresh);
            Assert.Equal(["remote-authority"], Strings(connection, "SELECT name FROM IGW_tags WHERE id='t1'"));
            using var mirror = new ServerMirror(settings);
            mirror.Ping();
            Assert.True(mirror.TableExists("IGW_project_tags"));
            Assert.False(mirror.TableExists("project_tags"));
        }
        finally { DropDatabase(settings); }
    }

    [DatabaseFact]
    [MemberData(nameof(Kinds))]
    public async Task A_line_table_using_a_legacy_mirror_name_is_preserved_even_with_source_metadata(string kind)
    {
        var settings = Settings(kind, "igw_foreign_" + Guid.NewGuid().ToString("N")[..8]);
        await DatabaseConnector.EnsureDatabaseAsync(settings);
        try
        {
            using (var mirror = new ServerMirror(settings))
            {
                mirror.Ping();
                mirror.EnsureMetaTable();
                mirror.SetGeneration("project", "foreign-metadata-is-not-table-ownership");
                using var client = DatabaseConnector.CreateClient(settings);
                string q = mirror.Q("project_tags");
                client.Ado.ExecuteCommand($"CREATE TABLE {q}(line_id INT, marker VARCHAR(50)); INSERT INTO {q} VALUES(7,'line-owned');");
            }
            string path = CreateSource("foreign-source");
            await using (var engine = new ReplicationEngine(settings, [new ReplicationSource("project", path, true)], NullLogger.Instance))
            {
                Assert.True(await engine.InitializeAsync(default));
                while (engine.RunOnce(default)) { }
            }
            using var probe = new ServerMirrorProbe(settings);
            Assert.Equal(["line-owned"], probe.Strings("project_tags", "marker"));
            Assert.Equal(2, probe.Count("IGW_project_tags"));
        }
        finally { DropDatabase(settings); }
    }

    private static void RenameRemote(SqlSugar.SqlSugarClient client, DatabaseKind kind, string from, string to)
    {
        string sql = kind switch
        {
            DatabaseKind.SqlServer => $"EXEC sp_rename N'[dbo].[{from}]', N'{to}', N'OBJECT';",
            DatabaseKind.MySql => $"RENAME TABLE `{from}` TO `{to}`;",
            _ => $"ALTER TABLE \"{from}\" RENAME TO \"{to}\";",
        };
        client.Ado.ExecuteCommand(sql);
    }

    [DatabaseFact]
    [MemberData(nameof(Kinds))]
    public async Task Lazy_runtime_store_migrates_legacy_mirrors_preserves_remote_history_and_drains_pending_deletes(string kind)
    {
        const int pendingItems = 21_000; // Exceeds the snapshot threshold; runtime history must still use a merge.
        var settings = Settings(kind, "igw_late_" + Guid.NewGuid().ToString("N")[..8]);
        await DatabaseConnector.EnsureDatabaseAsync(settings);
        try
        {
            string seed = CreateSource("late-remote-seed");
            await using (var engine = new ReplicationEngine(settings, [new ReplicationSource("project", seed, false)], NullLogger.Instance))
            {
                Assert.True(await engine.InitializeAsync(default));
                while (engine.RunOnce(default)) { }
            }
            string generation;
            using (var mirror = new ServerMirror(settings))
            {
                mirror.Ping();
                generation = mirror.GetGeneration("project")!;
                using var client = DatabaseConnector.CreateClient(settings);
                foreach (string logical in new[] { "tags", "mes_task_index", "upload_log" })
                    RenameRemote(client, settings.Kind, "IGW_project_" + logical, "project_" + logical);
            }
            string late = CreateSource("late-local");
            using (var connection = Open(late))
            {
                Exec(connection, """
                    ALTER TABLE IGW_tags RENAME TO tags;
                    ALTER TABLE IGW_mes_task_index RENAME TO mes_task_index;
                    ALTER TABLE IGW_upload_log RENAME TO upload_log;
                    DELETE FROM tags; DELETE FROM mes_task_index; DELETE FROM upload_log;
                    CREATE TABLE _igw_outbox(seq INTEGER PRIMARY KEY AUTOINCREMENT,tbl TEXT NOT NULL,op TEXT NOT NULL,pk TEXT NOT NULL);
                    CREATE TABLE _igw_sync(key TEXT PRIMARY KEY,value TEXT);
                    INSERT INTO _igw_outbox VALUES(55,'tags','d','["t2"]');
                    """);
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO _igw_sync VALUES('generation',$g)";
                command.Parameters.AddWithValue("$g", generation);
                command.ExecuteNonQuery();
                Exec(connection, $"""
                    WITH RECURSIVE ids(n) AS (SELECT 0 UNION ALL SELECT n+1 FROM ids WHERE n<{pendingItems - 1})
                    INSERT INTO tags(id,name) SELECT 'local-'||n,'late-'||n FROM ids;
                    INSERT INTO _igw_outbox(tbl,op,pk) SELECT 'tags','u',json_array(id) FROM tags;
                    """);
            }
            await using (var engine = new ReplicationEngine(settings, [new ReplicationSource("project", late, false)], NullLogger.Instance))
            {
                Assert.True(await engine.InitializeAsync(default));
                using (var connection = Open(late))
                {
                    Assert.Equal(pendingItems + 1L, Scalar(connection, "SELECT COUNT(*) FROM IGW_outbox"));
                    IGW.Core.IgwSqliteTableNamespace.Ensure(connection, ["tags", "mes_task_index", "upload_log"]);
                }
                while (engine.RunOnce(default)) { }
            }
            using var probe = new ServerMirrorProbe(settings);
            Assert.Equal(pendingItems + 1L, probe.Count("IGW_project_tags"));
            Assert.Equal(1L, probe.Count("IGW_project_tags", "id", "t1")); // Remote-only row survives migration and a >20k-key batch.
            Assert.Equal(0L, probe.Count("IGW_project_tags", "id", "t2")); // Legacy queued delete is still honored.
            using var local = Open(late);
            Assert.Equal(0L, Scalar(local, "SELECT COUNT(*) FROM IGW_outbox"));
        }
        finally { DropDatabase(settings); }
    }

    private static void DropDatabase(DatabaseSettings settings)
    {
        try
        {
            using var client = DatabaseConnector.CreateClient(settings, serverOnly: true);
            string name = settings.Database;
            string sql = settings.Kind switch
            {
                DatabaseKind.SqlServer => $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];",
                DatabaseKind.PostgreSql => $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE);",
                _ => $"DROP DATABASE IF EXISTS `{name}`;",
            };
            client.Ado.ExecuteCommand(sql);
        }
        catch { /* 测试库清理失败不影响结果 */ }
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        return connection;
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static List<string> Strings(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    private static List<double> Doubles(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var result = new List<double>();
        while (reader.Read()) result.Add(reader.GetDouble(0));
        return result;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>直接查询主库镜像表，验证同步结果。</summary>
    private sealed class ServerMirrorProbe(DatabaseSettings settings) : IDisposable
    {
        private readonly SqlSugar.SqlSugarClient _client = DatabaseConnector.CreateClient(settings);

        private string Q(string name) => settings.Kind switch
        {
            DatabaseKind.SqlServer => $"[{name}]",
            DatabaseKind.MySql => $"`{name}`",
            _ => $"\"{name}\"",
        };

        public long Count(string table) => Convert.ToInt64(_client.Ado.GetScalar($"SELECT COUNT(*) FROM {Q(table)}"));

        public long Count(string table, string column, string value) => Convert.ToInt64(_client.Ado.GetScalar(
            $"SELECT COUNT(*) FROM {Q(table)} WHERE {Q(column)} = @v", new SqlSugar.SugarParameter("@v", value)));

        public List<string> Strings(string table, string column) =>
            _client.Ado.SqlQuery<string>($"SELECT {Q(column)} FROM {Q(table)} ORDER BY {Q(column)}");

        public void Dispose() => _client.Dispose();
    }
}
