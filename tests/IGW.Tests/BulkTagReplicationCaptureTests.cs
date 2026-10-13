using IGW.Abstractions;
using IGW.Core;
using IGW.Persistence.Replication;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IGW.Tests;

/// <summary>
/// 选择外部主数据库时，本机 SQLite 的增删改由触发器记入待同步队列 IGW_outbox。
/// 批量注册点位走私有缓存连接、长事务、预编译 INSERT/UPDATE 与临时表：这些写入同样必须进队列，不能被复制漏掉。
/// </summary>
public sealed class BulkTagReplicationCaptureTests
{
    [Fact]
    public async Task Bulk_writes_are_recorded_in_the_replication_outbox()
    {
        await using var fx = await ProjectBulkTagWriterTests.Fixture.CreateAsync(new IgwProject());
        InstallCapture(fx.DatabasePath);

        var created = await fx.Repository.BulkUpsertTagsAsync(ProjectBulkTagWriterTests.Request(
            ProjectBulkTagWriterTests.Item("Old", "DB1", "Sub"),
            ProjectBulkTagWriterTests.Item("Keep", "DB1"),
            ProjectBulkTagWriterTests.Item("Other", "DB2")), null, default);
        Assert.True(created.Applied, created.Error);

        using (var connection = Open(fx.DatabasePath))
        {
            var outbox = ReadOutbox(connection);
            foreach (string id in Ids(connection, "SELECT id FROM IGW_tags"))
                Assert.Contains(("IGW_tags", "u", $"[\"{id}\"]"), outbox);
            foreach (string id in Ids(connection, "SELECT id FROM IGW_tag_groups"))
                Assert.Contains(("IGW_tag_groups", "u", $"[\"{id}\"]"), outbox);
            foreach (string id in Ids(connection, "SELECT tag_id FROM IGW_tag_options"))
                Assert.Contains(("IGW_tag_options", "u", $"[\"{id}\"]"), outbox);
            Assert.Contains(outbox, row => row.Table == "IGW_channels");
            Assert.Contains(outbox, row => row.Table == "IGW_devices");
            Assert.Contains(outbox, row => row.Table == "IGW_project_meta");
            Assert.DoesNotContain(outbox, row => row.Table == "bulk_ids");   // 临时表不复制
        }

        string oldId;
        using (var connection = Open(fx.DatabasePath))
        {
            oldId = Ids(connection, "SELECT id FROM IGW_tags WHERE name='Old'").Single();
            Exec(connection, "DELETE FROM IGW_outbox;");
        }

        var request = ProjectBulkTagWriterTests.Request(ProjectBulkTagWriterTests.Item("Keep", "DB1"));
        request.ReplaceGroups = [["DB1"]];
        var replaced = await fx.Repository.BulkUpsertTagsAsync(request, null, default);
        Assert.True(replaced.Applied, replaced.Error);

        using (var connection = Open(fx.DatabasePath))
        {
            var outbox = ReadOutbox(connection);
            Assert.Contains(("IGW_tags", "d", $"[\"{oldId}\"]"), outbox);   // replaceGroups 删掉的点也要同步删除
            Assert.Contains(outbox, row => row.Table == "IGW_tag_groups" && row.Op == "d");
            string keepId = Ids(connection, "SELECT id FROM IGW_tags WHERE name='Keep'").Single();
            Assert.Contains(("IGW_tags", "u", $"[\"{keepId}\"]"), outbox);
        }
    }

    [Fact]
    public async Task Bulk_write_restores_the_capture_triggers_it_suspends()
    {
        await using var fx = await ProjectBulkTagWriterTests.Fixture.CreateAsync(new IgwProject());
        InstallCapture(fx.DatabasePath);
        List<string> before;
        using (var connection = Open(fx.DatabasePath))
            before = Ids(connection, "SELECT name || ':' || sql FROM sqlite_master WHERE type='trigger' ORDER BY name");

        var created = await fx.Repository.BulkUpsertTagsAsync(ProjectBulkTagWriterTests.Request(
            ProjectBulkTagWriterTests.Item("A", "DB1"), ProjectBulkTagWriterTests.Item("B", "DB1")), null, default);
        Assert.True(created.Applied, created.Error);
        var failed = ProjectBulkTagWriterTests.Request(ProjectBulkTagWriterTests.Item("C"));
        failed.Channel = failed.Channel with { DriverId = "Modbus.Tcp" };
        Assert.False((await fx.Repository.BulkUpsertTagsAsync(failed, null, default)).Applied);

        using (var connection = Open(fx.DatabasePath))
        {
            Assert.Equal(before, Ids(connection, "SELECT name || ':' || sql FROM sqlite_master WHERE type='trigger' ORDER BY name"));
            // 批量写入之后的普通修改照常逐行记录
            Exec(connection, "DELETE FROM IGW_outbox;");
            Exec(connection, "UPDATE IGW_tags SET description='changed' WHERE name='A';");
            string id = Ids(connection, "SELECT id FROM IGW_tags WHERE name='A'").Single();
            Assert.Equal([("IGW_tags", "u", $"[\"{id}\"]")], ReadOutbox(connection));
        }
    }

    [Fact]
    public void Bulk_writer_uses_the_replication_outbox_table()
    {
        Assert.Equal(SqliteChangeCapture.OutboxTable, IGW.Persistence.Sqlite.ReplicationOutbox.OutboxTable);
    }

    /// <summary>与 ReplicationEngine 启动时相同：建队列表并给每张业务表挂触发器。</summary>
    private static void InstallCapture(string path)
    {
        using var connection = Open(path);
        SqliteChangeCapture.EnsureInfrastructure(connection);
        var tables = SqliteChangeCapture.ReadTables(connection);
        Assert.Contains(tables, table => table.Name == "IGW_tags");
        SqliteChangeCapture.EnsureTriggers(connection, tables);
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        return connection;
    }

    private static List<(string Table, string Op, string Pk)> ReadOutbox(SqliteConnection connection)
    {
        var rows = new List<(string, string, string)>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT tbl, op, pk FROM IGW_outbox ORDER BY seq;";
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return rows;
    }

    private static List<string> Ids(SqliteConnection connection, string sql)
    {
        var ids = new List<string>();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetString(0));
        return ids;
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
