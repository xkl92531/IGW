using IGW.Core;
using IGW.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IGW.Tests;

public sealed class ProjectTableNamespaceTests
{
    private static readonly string[] Tables =
    ["project_meta", "channels", "devices", "tag_groups", "tags", "tag_options", "project_sections",
        "mes_tasks", "mes_task_index", "flow_templates", "device_templates", "secret_entries", "schema_history"];

    [Fact]
    public async Task New_project_database_has_only_IGW_owned_table_names()
    {
        await using var fixture = new Fixture();
        await new ProjectSchemaMigrator(fixture.Database).MigrateAsync(default);
        await using var connection = await fixture.Database.OpenAsync(default);
        Assert.Equal(Tables.Select(name => "IGW_" + name).Order(), (await Names(connection)).Order());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Legacy_schema_migrates_without_changing_ids_values_or_history_and_reopens(int version)
    {
        await using var fixture = new Fixture();
        await fixture.CreateLegacy(version);
        var originalRows = new Dictionary<string, string[]>();
        await using (var legacy = await fixture.Database.OpenAsync(default))
            foreach (string table in Tables.Where(name => name != "schema_history"))
                originalRows[table] = await Rows(legacy, table);
        var migrator = new ProjectSchemaMigrator(fixture.Database);
        await migrator.MigrateAsync(default);
        await migrator.MigrateAsync(default);
        await using var connection = await fixture.Database.OpenAsync(default);
        Assert.Equal(Tables.Select(name => "IGW_" + name).Order(), (await Names(connection)).Order());
        foreach ((string table, string[] rows) in originalRows)
            Assert.Equal(rows, await Rows(connection, "IGW_" + table));
        Assert.Equal("old-project|41|7|legacy-hash", await Scalar(connection,
            "SELECT project_id||'|'||revision||'|'||runtime_generation||'|'||legacy_source_hash FROM IGW_project_meta"));
        Assert.Equal("tag-id|group-id|C.D.G.tags|17", await Scalar(connection,
            "SELECT id||'|'||group_id||'|'||canonical_path||'|'||row_version FROM IGW_tags"));
        Assert.Equal("{\"tags\":\"channels\",\"name\":\"devices\"}", await Scalar(connection,
            "SELECT metadata_json FROM IGW_tag_options WHERE tag_id='tag-id'"));
        Assert.Equal("stamp-1", await Scalar(connection, "SELECT applied_at FROM IGW_schema_history WHERE version=1"));
        if (version == 2)
            Assert.Equal("stamp-2", await Scalar(connection, "SELECT applied_at FROM IGW_schema_history WHERE version=2"));
        Assert.Equal("2", await Scalar(connection, "SELECT MAX(version) FROM IGW_schema_history"));
        Assert.Equal("ok", await Scalar(connection, "PRAGMA integrity_check"));
        Assert.Null(await Scalar(connection, "PRAGMA foreign_key_check"));
        await Exec(connection, "DELETE FROM IGW_devices WHERE id='device-id'");
        Assert.Equal("0", await Scalar(connection, "SELECT COUNT(*) FROM IGW_tags"));
        Assert.Equal("0", await Scalar(connection, "SELECT COUNT(*) FROM IGW_tag_options"));
        Assert.Equal("1", await Scalar(connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ix_tags_group_only_order' AND tbl_name='IGW_tags'"));
    }

    [Fact]
    public async Task Existing_target_collision_leaves_every_legacy_table_and_value_unchanged()
    {
        await using var fixture = new Fixture();
        await fixture.CreateLegacy(2);
        await using (var connection = await fixture.Database.OpenAsync(default))
            await Exec(connection, "CREATE TABLE IGW_tags(id TEXT); INSERT INTO IGW_tags VALUES('foreign-id');");
        await Assert.ThrowsAsync<InvalidDataException>(() => new ProjectSchemaMigrator(fixture.Database).MigrateAsync(default));
        await using var check = await fixture.Database.OpenAsync(default);
        Assert.Equal(Tables.Append("IGW_tags").Order(), (await Names(check)).Order());
        Assert.Equal("41", await Scalar(check, "SELECT revision FROM project_meta"));
        Assert.Equal("foreign-id", await Scalar(check, "SELECT id FROM IGW_tags"));
    }

    [Theory]
    [InlineData("channels")]
    [InlineData("IGW_channels")]
    public async Task Foreign_generic_tables_are_rejected_without_mutation(string table)
    {
        await using var fixture = new Fixture();
        await using (var connection = await fixture.Database.OpenAsync(default))
            await Exec(connection, $"CREATE TABLE {table}(id TEXT); INSERT INTO {table} VALUES('foreign-id');");
        await Assert.ThrowsAsync<InvalidDataException>(() => new ProjectSchemaMigrator(fixture.Database).MigrateAsync(default));
        await using var check = await fixture.Database.OpenAsync(default);
        Assert.Equal(new[] { table }, await Names(check));
        Assert.Equal("foreign-id", await Scalar(check, $"SELECT id FROM {table}"));
    }

    [Fact]
    public async Task Optional_owned_tables_can_be_missing_when_existing_ownership_signature_matches()
    {
        await using var fixture = new Fixture();
        await using var connection = await fixture.Database.OpenAsync(default);
        await Exec(connection, "CREATE TABLE owned_a(id TEXT); INSERT INTO owned_a VALUES('keep');");
        IgwSqliteTableNamespace.Ensure(connection, ["owned_a", "owned_b"],
            new Dictionary<string, IReadOnlyCollection<string>> { ["owned_a"] = ["id"], ["owned_b"] = ["body"] });
        Assert.Equal(new[] { "IGW_owned_a" }, await Names(connection));
        Assert.Equal("keep", await Scalar(connection, "SELECT id FROM IGW_owned_a"));
    }

    [Fact]
    public async Task Already_prefixed_table_with_wrong_schema_is_rejected()
    {
        await using var fixture = new Fixture();
        await using var connection = await fixture.Database.OpenAsync(default);
        await Exec(connection, "CREATE TABLE IGW_owned(id TEXT);");
        Assert.Throws<InvalidDataException>(() => IgwSqliteTableNamespace.Ensure(connection, ["owned"],
            new Dictionary<string, IReadOnlyCollection<string>> { ["owned"] = ["expected_id"] }));
        Assert.Equal(new[] { "IGW_owned" }, await Names(connection));
    }

    [Theory]
    [InlineData("tags", "IGW_tags")]
    [InlineData("IGW_tags", "IGW_tags")]
    [InlineData("igw_Tags", "IGW_Tags")]
    [InlineData("_igw_tags", "IGW_tags")]
    [InlineData("_IGW_123", "IGW_123")]
    [InlineData("IGW_IGW_tags", "IGW_IGW_tags")]
    public void Physical_name_strips_only_one_namespace_prefix_and_preserves_suffix(string old, string expected) =>
        Assert.Equal(expected, IgwSqliteTableNamespace.PhysicalName(old));

    [Fact]
    public async Task Namespace_renames_only_whitelisted_tables_and_normalizes_case_idempotently()
    {
        await using var fixture = new Fixture();
        await using var connection = await fixture.Database.OpenAsync(default);
        await Exec(connection, "CREATE TABLE igw_cache(id TEXT); INSERT INTO igw_cache VALUES('keep'); CREATE TABLE customers(id TEXT);");
        IgwSqliteTableNamespace.Ensure(connection, ["igw_cache"]);
        IgwSqliteTableNamespace.Ensure(connection, ["igw_cache"]);
        Assert.Equal(new[] { "IGW_cache", "customers" }, await Names(connection));
        Assert.Equal("keep", await Scalar(connection, "SELECT id FROM IGW_cache"));
        Assert.Throws<ArgumentException>(() => IgwSqliteTableNamespace.Ensure(connection, ["customers; DROP TABLE IGW_cache"]));
        Assert.Equal("keep", await Scalar(connection, "SELECT id FROM IGW_cache"));
    }

    [Fact]
    public async Task Historical_sql_rewrites_identifiers_and_preserves_literal_documents()
    {
        await using var fixture = new Fixture();
        await new ProjectSchemaMigrator(fixture.Database,
        [new ProjectSchemaMigration(10, """
            -- tags is a logical document key
            CREATE TABLE "tags"(id TEXT, document TEXT DEFAULT 'channels');
            /* devices stays in this comment */
            INSERT INTO [tags](id,document) VALUES('tags','{"tags":"FROM channels","devices":"tag_groups"}');
            """)]).MigrateAsync(default);
        await using var connection = await fixture.Database.OpenAsync(default);
        Assert.Equal("{\"tags\":\"FROM channels\",\"devices\":\"tag_groups\"}",
            await Scalar(connection, "SELECT document FROM IGW_tags WHERE id='tags'"));
    }

    [Theory]
    [InlineData("schema_history")]
    [InlineData("IGW_schema_history")]
    public async Task Empty_history_left_before_initial_migration_can_recover_and_reopen(string table)
    {
        await using var fixture = new Fixture();
        await using (var connection = await fixture.Database.OpenAsync(default))
            await Exec(connection, $"CREATE TABLE {table}(version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL);");
        var migrator = new ProjectSchemaMigrator(fixture.Database);
        await migrator.MigrateAsync(default);
        await migrator.MigrateAsync(default);
        await using var check = await fixture.Database.OpenAsync(default);
        Assert.Equal(Tables.Select(name => "IGW_" + name).Order(), (await Names(check)).Order());
        Assert.Equal("2", await Scalar(check, "SELECT MAX(version) FROM IGW_schema_history"));
    }

    [Fact]
    public async Task Failed_first_migration_does_not_commit_history_and_next_initialize_recovers()
    {
        await using var fixture = new Fixture();
        await Assert.ThrowsAsync<SqliteException>(() => new ProjectSchemaMigrator(fixture.Database,
            [new ProjectSchemaMigration(1, "CREATE TABLE project_meta(singleton_id INTEGER); INVALID SQL;")]).MigrateAsync(default));
        await using (var check = await fixture.Database.OpenAsync(default))
            Assert.Empty(await Names(check));
        await new ProjectSchemaMigrator(fixture.Database).MigrateAsync(default);
        await using var reopened = await fixture.Database.OpenAsync(default);
        Assert.Equal("2", await Scalar(reopened, "SELECT MAX(version) FROM IGW_schema_history"));
    }

    [Theory]
    [InlineData("CREATE TABLE IGW_schema_history(version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL); INSERT INTO IGW_schema_history VALUES(1,'stamp');")]
    [InlineData("CREATE TABLE IGW_schema_history(version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL, foreign_column TEXT);")]
    [InlineData("CREATE TABLE IGW_schema_history(version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL); CREATE TABLE customers(id TEXT);")]
    public async Task History_without_project_marker_only_recovers_if_pristine_and_empty(string sql)
    {
        await using var fixture = new Fixture();
        await using var connection = await fixture.Database.OpenAsync(default);
        await Exec(connection, sql);
        string[] before = await Names(connection);
        await Assert.ThrowsAsync<InvalidDataException>(() => new ProjectSchemaMigrator(fixture.Database).MigrateAsync(default));
        Assert.Equal(before, await Names(connection));
    }

    private static async Task<string?> Scalar(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (await command.ExecuteScalarAsync())?.ToString();
    }

    private static async Task Exec(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string[]> Names(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
        await using var reader = await command.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        return names.ToArray();
    }

    private static async Task<string[]> Rows(SqliteConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT rowid,* FROM {table} ORDER BY rowid";
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
            rows.Add(System.Text.Json.JsonSerializer.Serialize(Enumerable.Range(0, reader.FieldCount)
                .Select(index => reader.IsDBNull(index) ? null : reader.GetValue(index)).ToArray()));
        return rows.ToArray();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "igw-table-namespace", Guid.NewGuid().ToString("N"));
        public ProjectDatabase Database { get; }
        public Fixture() => Database = new ProjectDatabase(Path.Combine(_directory, "project.db"));

        public async Task CreateLegacy(int version)
        {
            await using var connection = await Database.OpenAsync(default);
            await Exec(connection, "CREATE TABLE schema_history(version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL);");
            var assembly = typeof(ProjectSchemaMigrator).Assembly;
            foreach (string resource in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".sql")).Order())
            {
                int migrationVersion = int.Parse(resource.Split('.')[4].Split('_')[0]);
                if (migrationVersion > version) continue;
                using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
                await Exec(connection, await reader.ReadToEndAsync());
                await Exec(connection, $"INSERT INTO schema_history VALUES({migrationVersion},'stamp-{migrationVersion}');");
            }
            await Exec(connection, """
                INSERT INTO project_meta(singleton_id,project_id,name,schema_version,revision,runtime_generation,legacy_source_hash,created_at,updated_at)
                    VALUES(1,'old-project','channels',1,41,7,'legacy-hash','created','updated');
                INSERT INTO channels(id,name,driver_id) VALUES('channel-id','C','simulator');
                INSERT INTO devices(id,channel_id,name) VALUES('device-id','channel-id','D');
                INSERT INTO tag_groups(id,device_id,name,canonical_path) VALUES('group-id','device-id','G','C.D.G');
                INSERT INTO tags(id,device_id,group_id,name,canonical_path,data_type,access,scan_rate_ms,row_version)
                    VALUES('tag-id','device-id','group-id','tags','C.D.G.tags',1,1,1000,17);
                INSERT INTO tag_options(tag_id,metadata_json) VALUES('tag-id','{"tags":"channels","name":"devices"}');
                INSERT INTO project_sections(section_key,schema_version,document_json,row_version) VALUES('runtime',1,'{"tags":"secret_entries"}',3);
                INSERT INTO mes_tasks(id,group_id,name,flow_document_json,row_version) VALUES('task-id','group-id','Task','{"tags":"mes_tasks"}',9);
                INSERT INTO mes_task_index(task_id,node_type,referenced_tag_id,referenced_path) VALUES('task-id','read','tag-id','C.D.G.tags');
                INSERT INTO flow_templates(id,name,template_document_json,row_version) VALUES('flow-id','Flow','{"channels":[]}',5);
                INSERT INTO device_templates(id,driver_id,name,template_document_json,row_version) VALUES('template-id','simulator','Template','{"tags":[]}',7);
                INSERT INTO secret_entries(owner_type,owner_id,secret_name,ciphertext,nonce,authentication_tag,key_version)
                    VALUES('devices','device-id','password','cipher-tags','nonce','auth',4);
                """);
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
            return ValueTask.CompletedTask;
        }
    }
}
