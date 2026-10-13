using IGW.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IGW.Tests;

public sealed class ProjectDatabaseSchemaTests
{
    [Fact]
    public async Task New_database_uses_wal_foreign_keys_and_all_required_tables()
    {
        await using var fixture = await ProjectDbFixture.CreateAsync();
        await using var connection = await fixture.Database.OpenAsync(default);

        Assert.Equal("wal", await ScalarAsync<string>(connection, "PRAGMA journal_mode;"));
        Assert.Equal(1L, await ScalarAsync<long>(connection, "PRAGMA foreign_keys;"));

        string[] tables = await ReadNamesAsync(connection,
            "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;");
        Assert.Contains("IGW_project_meta", tables);
        Assert.Contains("IGW_channels", tables);
        Assert.Contains("IGW_devices", tables);
        Assert.Contains("IGW_tag_groups", tables);
        Assert.Contains("IGW_tags", tables);
        Assert.Contains("IGW_tag_options", tables);
        Assert.Contains("IGW_project_sections", tables);
        Assert.Contains("IGW_mes_tasks", tables);
        Assert.Contains("IGW_mes_task_index", tables);
        Assert.Contains("IGW_flow_templates", tables);
        Assert.Contains("IGW_device_templates", tables);
        Assert.Contains("IGW_secret_entries", tables);
        Assert.Equal(2L, await ScalarAsync<long>(connection,
            "SELECT MAX(version) FROM IGW_schema_history;"));
        string[] indexes = await ReadNamesAsync(connection,
            "SELECT name FROM sqlite_master WHERE type='index' ORDER BY name;");
        Assert.Contains("ix_tags_group_only_order", indexes);
    }

    [Fact]
    public async Task Failed_migration_rolls_back_schema_and_history()
    {
        string directory = Path.Combine(Path.GetTempPath(), "igw-schema-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var database = new ProjectDatabase(Path.Combine(directory, "project.db"));
            var migrator = new ProjectSchemaMigrator(database,
            [
                new ProjectSchemaMigration(99,
                    "CREATE TABLE partial_write(id INTEGER PRIMARY KEY); INVALID SQL;")
            ]);

            await Assert.ThrowsAsync<SqliteException>(() => migrator.MigrateAsync(default));

            await using var connection = await database.OpenAsync(default);
            Assert.Equal(0L, await ScalarAsync<long>(connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='partial_write';"));
            Assert.Equal(0L, await ScalarAsync<long>(connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='IGW_schema_history';"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<T> ScalarAsync<T>(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        object? value = await command.ExecuteScalarAsync();
        return (T)Convert.ChangeType(value!, typeof(T));
    }

    private static async Task<string[]> ReadNamesAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        return names.ToArray();
    }

    private sealed class ProjectDbFixture : IAsyncDisposable
    {
        private readonly string _directory;
        public ProjectDatabase Database { get; }

        private ProjectDbFixture(string directory, ProjectDatabase database)
        {
            _directory = directory;
            Database = database;
        }

        public static async Task<ProjectDbFixture> CreateAsync()
        {
            string directory = Path.Combine(Path.GetTempPath(), "igw-schema-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var database = new ProjectDatabase(Path.Combine(directory, "project.db"));
            await new ProjectSchemaMigrator(database).MigrateAsync(default);
            return new ProjectDbFixture(directory, database);
        }

        public ValueTask DisposeAsync()
        {
            Directory.Delete(_directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
