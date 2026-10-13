using IGW.Persistence.Replication;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IGW.Tests;

public sealed class ReplicationTableNamespaceTests
{
    [Theory]
    [InlineData("project", "tags", "IGW_project_tags")]
    [InlineData("project", "IGW_tags", "IGW_project_tags")]
    [InlineData("project", "igw_tags", "IGW_project_tags")]
    [InlineData("project", "_igw_tags", "IGW_project_tags")]
    public void Mirror_names_namespace_logical_tables_once(string source, string table, string expected) =>
        Assert.Equal(expected, ServerMirror.MirrorName(source, table));

    [Fact]
    public void Mirror_names_do_not_collide_after_sanitizing_or_at_source_boundary()
    {
        Assert.NotEqual(ServerMirror.MirrorName("a-b", "tags"), ServerMirror.MirrorName("a_b", "tags"));
        Assert.NotEqual(ServerMirror.MirrorName("a", "b_tags"), ServerMirror.MirrorName("a_b", "tags"));
        string name = ServerMirror.MirrorName(new string('a', 80), new string('b', 80));
        Assert.StartsWith("IGW_", name);
        Assert.InRange(name.Length, 1, 63);
        Assert.NotEqual(name, ServerMirror.MirrorName(new string('a', 80), new string('b', 79) + "c"));
    }

    [Fact]
    public void Capture_preserves_pending_events_sequence_and_state_when_infrastructure_is_renamed()
    {
        using var connection = Open();
        Exec(connection, """
            CREATE TABLE IGW_tags(id TEXT PRIMARY KEY, value TEXT);
            CREATE TABLE _igw_outbox(seq INTEGER PRIMARY KEY AUTOINCREMENT,tbl TEXT NOT NULL,op TEXT NOT NULL,pk TEXT NOT NULL);
            CREATE TABLE _igw_sync(key TEXT PRIMARY KEY,value TEXT);
            INSERT INTO _igw_outbox(seq,tbl,op,pk) VALUES(41,'tags','d','["gone"]'),(42,'tags','u','["t1"]');
            INSERT INTO _igw_sync VALUES('generation','gen1'),('suppress','1');
            CREATE TRIGGER _igw_ai_tags AFTER INSERT ON IGW_tags WHEN (SELECT value FROM _igw_sync WHERE key='suppress') IS NOT '1'
            BEGIN INSERT INTO _igw_outbox(tbl,op,pk) VALUES('tags','u',json_array(NEW.id)); END;
            """);
        SqliteChangeCapture.EnsureInfrastructure(connection);
        SqliteChangeCapture.EnsureTriggers(connection, SqliteChangeCapture.ReadTables(connection));
        Assert.Equal(2L, Scalar(connection, "SELECT COUNT(*) FROM IGW_outbox"));
        Assert.Equal("gen1", SqliteChangeCapture.GetState(connection, "generation"));
        Assert.Equal("1", SqliteChangeCapture.GetState(connection, "suppress"));
        Assert.Equal(42L, Scalar(connection, "SELECT MAX(seq) FROM IGW_outbox"));
        Assert.Equal(2L, Scalar(connection, "SELECT COUNT(*) FROM IGW_outbox WHERE tbl='IGW_tags'"));
        Exec(connection, "INSERT INTO IGW_tags VALUES('suppressed','v');");
        Assert.Equal(2L, Scalar(connection, "SELECT COUNT(*) FROM IGW_outbox"));
        SqliteChangeCapture.SetState(connection, "suppress", null);
        Exec(connection, "INSERT INTO IGW_tags VALUES('t2','v');");
        Assert.Equal(43L, Scalar(connection, "SELECT MAX(seq) FROM IGW_outbox"));
        Assert.Equal("IGW_tags", Text(connection, "SELECT tbl FROM IGW_outbox WHERE seq=43"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('_igw_outbox','_igw_sync')"));
        Assert.Equal(["IGW_tags"], SqliteChangeCapture.ReadTables(connection).Select(table => table.Name));
        SqliteChangeCapture.EnsureTriggers(connection, SqliteChangeCapture.ReadTables(connection));
        Exec(connection, "UPDATE IGW_tags SET value='v2' WHERE id='t2';");
        Assert.Equal(4L, Scalar(connection, "SELECT COUNT(*) FROM IGW_outbox"));
    }

    [Theory]
    [InlineData("IGW_outbox")]
    [InlineData("IGW_sync")]
    [InlineData("IGW_sync_meta")]
    [InlineData("_igw_outbox")]
    [InlineData("sqlite_sequence")]
    public void Capture_excludes_replication_infrastructure(string table) =>
        Assert.False(SqliteChangeCapture.IsReplicated(table));

    [Fact]
    public void Capture_does_not_replicate_unowned_production_tables()
    {
        using var connection = Open();
        Exec(connection, "CREATE TABLE project_tags(id TEXT PRIMARY KEY,value TEXT); CREATE TABLE tags(id TEXT PRIMARY KEY,value TEXT); CREATE TABLE IGW_tags(id TEXT PRIMARY KEY,value TEXT);");
        SqliteChangeCapture.EnsureInfrastructure(connection);
        Assert.Equal(["IGW_tags"], SqliteChangeCapture.ReadTables(connection).Select(table => table.Name));
        SqliteChangeCapture.EnsureTriggers(connection, SqliteChangeCapture.ReadTables(connection));
        Exec(connection, "INSERT INTO tags VALUES('foreign','value'); INSERT INTO project_tags VALUES('line','value'); INSERT INTO IGW_tags VALUES('owned','value');");
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM IGW_outbox"));
        Assert.Equal("IGW_tags", Text(connection, "SELECT tbl FROM IGW_outbox"));
    }

    [Theory]
    [InlineData(DatabaseKind.SqlServer, "EXEC sp_rename N'[dbo].[project_tags]', N'IGW_project_tags', N'OBJECT';")]
    [InlineData(DatabaseKind.PostgreSql, "ALTER TABLE \"project_tags\" RENAME TO \"IGW_project_tags\";")]
    [InlineData(DatabaseKind.MySql, "RENAME TABLE `project_tags` TO `IGW_project_tags`;")]
    public void Legacy_migration_requires_source_metadata_and_exact_schema_before_rename(DatabaseKind kind, string expected)
    {
        using var mirror = new ServerMirror(new DatabaseSettings(kind, "localhost", 0, "igw_test", "test", "test"));
        var local = new LocalTable("IGW_tags", [new LocalColumn("id", "TEXT", true, 1), new LocalColumn("value", "TEXT", false, 0)]);
        string keyType = kind == DatabaseKind.SqlServer ? "NVARCHAR(450)" : kind == DatabaseKind.MySql ? "VARCHAR(255)" : "TEXT";
        string valueType = kind == DatabaseKind.SqlServer ? "NVARCHAR(MAX)" : kind == DatabaseKind.MySql ? "LONGTEXT" : "TEXT";
        var exact = new[] { new MirrorColumn("id", keyType, false, true), new MirrorColumn("value", valueType, true, false) };
        Assert.Equal(expected, mirror.PlanLegacyMigration("project", local, "generation", exact, false));
        Assert.Null(mirror.PlanLegacyMigration("project", local, null, exact, false));
        Assert.Null(mirror.PlanLegacyMigration("project", local, "generation", [new MirrorColumn("id", "BIGINT", false, true)], false));
        Assert.Null(mirror.PlanLegacyMigration("project", local, "generation", exact.Append(new MirrorColumn("production_line", "TEXT", true, false)).ToArray(), false));
        Assert.Throws<InvalidDataException>(() => mirror.PlanLegacyMigration("project", local, "generation", exact, true));
        Assert.Null(mirror.PlanLegacyMigration("project", local, null, exact, true));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void MySql_catalog_lookup_obeys_server_table_case_rules(int mode, bool equivalent)
    {
        using var mirror = new ServerMirror(new DatabaseSettings(DatabaseKind.MySql, "localhost", 0, "igw_test", "test", "test"));
        Assert.Equal(equivalent, mirror.TableIdentifiersEqual("IGW_project_tags", "igw_project_tags", mode));
        Assert.False(mirror.TableIdentifiersEqual("IGW_project_tags", "project_tags", mode));
    }

    [Theory]
    [InlineData(DatabaseKind.SqlServer, "NVARCHAR(450)")]
    [InlineData(DatabaseKind.PostgreSql, "TEXT")]
    [InlineData(DatabaseKind.MySql, "VARCHAR(255)")]
    public void Metadata_ownership_uses_the_original_bounded_text_schema(DatabaseKind kind, string type)
    {
        using var mirror = new ServerMirror(new DatabaseSettings(kind, "localhost", 0, "igw_test", "test", "test"));
        var columns = new[] { new MirrorColumn("source", type, false, true), new MirrorColumn("generation", type, true, false), new MirrorColumn("updated_utc", type, true, false) };
        Assert.True(mirror.IsOwnedMetadata(columns));
        Assert.False(mirror.IsOwnedMetadata(columns.Append(new MirrorColumn("line_id", type, true, false)).ToArray()));
        Assert.False(mirror.IsOwnedMetadata([new MirrorColumn("source", type, true, false)]));
    }

    [Theory]
    [InlineData(DatabaseKind.SqlServer, "[")]
    [InlineData(DatabaseKind.PostgreSql, "\"")]
    [InlineData(DatabaseKind.MySql, "`")]
    public void Provider_identifiers_remain_namespaced_and_within_the_shortest_identifier_limit(DatabaseKind kind, string quote)
    {
        using var mirror = new ServerMirror(new DatabaseSettings(kind, "localhost", 0, "igw_test", "test", "test"));
        string name = ServerMirror.MirrorName(new string('s', 100), "IGW_" + new string('t', 100));
        Assert.StartsWith("IGW_", name);
        Assert.Equal(63, name.Length);
        Assert.StartsWith(quote + "IGW_", mirror.Q(name));
        Assert.Equal(name, ServerMirror.MirrorName(new string('s', 100), new string('t', 100)));
    }

    [Fact]
    public void Infrastructure_collision_preserves_both_queues_and_does_not_partially_rename_state()
    {
        using var connection = Open();
        Exec(connection, """
            CREATE TABLE _igw_outbox(seq INTEGER PRIMARY KEY,tbl TEXT,op TEXT,pk TEXT);
            CREATE TABLE IGW_outbox(seq INTEGER PRIMARY KEY,tbl TEXT,op TEXT,pk TEXT);
            CREATE TABLE _igw_sync(key TEXT PRIMARY KEY,value TEXT);
            INSERT INTO _igw_outbox VALUES(3,'tags','u','["old"]');
            INSERT INTO IGW_outbox VALUES(4,'IGW_tags','d','["new"]');
            INSERT INTO _igw_sync VALUES('generation','preserved');
            """);
        Assert.Throws<InvalidDataException>(() => SqliteChangeCapture.EnsureInfrastructure(connection));
        Assert.Equal(3L, Scalar(connection, "SELECT seq FROM _igw_outbox"));
        Assert.Equal(4L, Scalar(connection, "SELECT seq FROM IGW_outbox"));
        Assert.Equal("preserved", Text(connection, "SELECT value FROM _igw_sync WHERE key='generation'"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name='IGW_sync'"));
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, "igw_project_tags")]
    [InlineData(2, "igw_project_tags")]
    public void Existing_MySql_targets_resolve_to_catalog_spelling_for_second_startup_schema_reads(int mode, string? expected)
    {
        using var mirror = new ServerMirror(new DatabaseSettings(DatabaseKind.MySql, "localhost", 0, "igw_test", "test", "test"));
        Assert.Equal(expected, mirror.ResolveCatalogTableName("IGW_project_tags", ["project_tags", "igw_project_tags"], mode));
        Assert.Null(mirror.ResolveCatalogTableName("IGW_project_other", ["project_tags", "igw_project_tags"], mode));
    }

    [Fact]
    public async Task A_lazy_store_with_only_legacy_tables_keeps_its_pending_events_until_owned_tables_are_ready()
    {
        string path = Path.Combine(Path.GetTempPath(), "igw-late-replication-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                Exec(connection, """
                    CREATE TABLE tags(id TEXT PRIMARY KEY,value TEXT);
                    CREATE TABLE _igw_outbox(seq INTEGER PRIMARY KEY AUTOINCREMENT,tbl TEXT NOT NULL,op TEXT NOT NULL,pk TEXT NOT NULL);
                    CREATE TABLE _igw_sync(key TEXT PRIMARY KEY,value TEXT);
                    INSERT INTO _igw_outbox VALUES(55,'tags','d','["old-remote"]');
                    INSERT INTO _igw_sync VALUES('generation','old-generation');
                    """);
            }
            var settings = new DatabaseSettings(DatabaseKind.PostgreSql, "127.0.0.1", 1, "test", "test", "test");
            await using var engine = new ReplicationEngine(settings, [new ReplicationSource("late", path, false)], Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            using var mirror = new ServerMirror(settings);
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var client = (SqlSugar.SqlSugarClient)typeof(ServerMirror).GetField("_client", flags)!.GetValue(mirror)!;
            // SQL execution is forbidden at the dependency boundary. This unit test never opens a server connection.
            client.Aop.OnExecutingChangeSql = (_, _) => throw new InvalidOperationException("The local store is not ready for remote operations.");
            var states = (System.Collections.IDictionary)typeof(ReplicationEngine).GetField("_states", flags)!.GetValue(engine)!;
            typeof(ReplicationEngine).GetMethod("Handshake", flags)!.Invoke(engine, [mirror, states["late"]!, true, true]);
            using var inspect = new SqliteConnection($"Data Source={path};Pooling=False");
            inspect.Open();
            Assert.Equal(55L, Scalar(inspect, "SELECT seq FROM IGW_outbox"));
            Assert.Equal("tags", Text(inspect, "SELECT tbl FROM IGW_outbox"));
            Assert.Equal("old-generation", SqliteChangeCapture.GetState(inspect, "generation"));
            Assert.False((bool)states["late"]!.GetType().GetProperty("HandshakeDone")!.GetValue(states["late"])!);
        }
        finally { File.Delete(path); }
    }

    private static SqliteConnection Open()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
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

    private static string? Text(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() as string;
    }
}
