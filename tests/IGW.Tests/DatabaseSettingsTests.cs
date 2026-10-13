using IGW.Core;
using IGW.Persistence.Replication;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IGW.Tests;

public sealed class DatabaseSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "igw-dbsettings-" + Guid.NewGuid().ToString("N"));

    public DatabaseSettingsTests() => Directory.CreateDirectory(_dir);

    [Theory]
    [InlineData(DatabaseKind.SqlServer, 1433)]
    [InlineData(DatabaseKind.PostgreSql, 5432)]
    [InlineData(DatabaseKind.MySql, 3306)]
    public void Default_ports_follow_the_database_kind(DatabaseKind kind, int port)
    {
        var settings = new DatabaseSettings(kind, "10.0.0.5", 0, "igw", "u", "p");
        Assert.Equal(port, settings.EffectivePort);
        Assert.Null(settings.Validate());
    }

    [Theory]
    [InlineData("", "igw", "sa", "请填写数据库地址")]
    [InlineData("db", "igw-prod", "sa", "数据库名只能包含")]
    [InlineData("db", "1igw", "sa", "数据库名只能包含")]
    [InlineData("db", "igw", "", "请填写数据库账号")]
    public void Invalid_settings_are_reported_before_connecting(string host, string database, string user, string expected)
    {
        var settings = new DatabaseSettings(DatabaseKind.PostgreSql, host, 5432, database, user, "x");
        Assert.Contains(expected, settings.Validate());
    }

    [Fact]
    public void Passwords_with_separators_are_escaped_in_connection_strings()
    {
        var settings = new DatabaseSettings(DatabaseKind.SqlServer, "db", 1433, "igw", "sa", "a;b=c'd\"e");
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(settings.ConnectionString());
        Assert.Equal("a;b=c'd\"e", builder.Password);
        Assert.Equal("igw", builder.InitialCatalog);
        Assert.Equal("master", new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(settings.ConnectionString(serverOnly: true)).InitialCatalog);
        var pg = new Npgsql.NpgsqlConnectionStringBuilder(
            (settings with { Kind = DatabaseKind.PostgreSql, Port = 5432 }).ConnectionString());
        Assert.Equal("a;b=c'd\"e", pg.Password);
    }

    [Fact]
    public void Settings_file_stores_the_password_encrypted_and_defaults_to_sqlite()
    {
        var protector = new InstallationSecretProtector(Path.Combine(_dir, "igw.masterkey"));
        Assert.Equal(DatabaseKind.Sqlite, DatabaseSettingsFile.Load(_dir, protector).Kind);

        var settings = new DatabaseSettings(DatabaseKind.MySql, "db.local", 3306, "igw", "root", "秘密#2026");
        DatabaseSettingsFile.Save(_dir, settings, protector);
        string raw = File.ReadAllText(DatabaseSettingsFile.PathIn(_dir));
        Assert.DoesNotContain("秘密#2026", raw);
        Assert.DoesNotContain("effectivePort", raw);
        Assert.Equal(settings, DatabaseSettingsFile.Load(_dir, protector));
    }

    [Fact]
    public void Change_capture_records_inserts_updates_deletes_and_can_be_suppressed()
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_dir, "c.db")};Pooling=False");
        connection.Open();
        Exec(connection, "CREATE TABLE IGW_tags(id TEXT PRIMARY KEY, name TEXT); CREATE TABLE IGW_log(ts TEXT, message TEXT);");
        SqliteChangeCapture.EnsureInfrastructure(connection);
        var tables = SqliteChangeCapture.ReadTables(connection);
        Assert.Equal(2, SqliteChangeCapture.EnsureTriggers(connection, tables).Count);
        Assert.Empty(SqliteChangeCapture.EnsureTriggers(connection, tables));   // 已挂上的不重复处理

        Exec(connection, """
            INSERT INTO IGW_tags VALUES('t1','A');
            UPDATE IGW_tags SET id='t2' WHERE id='t1';
            DELETE FROM IGW_tags WHERE id='t2';
            INSERT INTO IGW_log VALUES('x','y');
            """);
        Assert.Equal(["u:[\"t1\"]", "d:[\"t1\"]", "u:[\"t2\"]", "d:[\"t2\"]", "u:[1]"], Outbox(connection));

        SqliteChangeCapture.SetState(connection, "suppress", "1");
        Exec(connection, "INSERT INTO IGW_tags VALUES('t3','C');");
        Assert.Equal(5, Outbox(connection).Count);
        Assert.True(tables.Single(table => table.Name == "IGW_log").UsesRowId);
    }

    private static List<string> Outbox(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT op || ':' || pk FROM IGW_outbox ORDER BY seq";
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read()) rows.Add(reader.GetString(0));
        return rows;
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
