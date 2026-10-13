using IGW.Abstractions;
using IGW.Core;
using IGW.Core.Security;
using IGW.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IGW.Tests;

public sealed class ProjectBulkTagWriterTests
{
    [Fact]
    public async Task Creates_channel_device_nested_groups_and_tags_in_one_revision()
    {
        await using var fx = await Fixture.CreateAsync(new IgwProject());
        long initial = (await fx.Repository.GetRevisionAsync(default)).Value;
        var result = await fx.Repository.BulkUpsertTagsAsync(Request(
            Item("Error1", "DB490", "Type", "Errors"), Item("Error2", "DB490", "Type", "Errors"), Item("Run")), null, default);

        Assert.True(result.Applied);
        Assert.Equal(3, result.Created);
        Assert.Equal(initial + 1, result.Revision.Value);
        var project = await fx.Repository.LoadRuntimeProjectAsync(default);
        var device = Assert.Single(Assert.Single(project.Channels).Devices);
        Assert.Equal("S7Plus", device.Name);
        Assert.Equal("S7-1500", device.Settings["model"]);
        var errors = device.Groups.Single(g => g.Name == "DB490").SubGroups.Single(g => g.Name == "Type").SubGroups.Single();
        Assert.Equal(["Error1", "Error2"], errors.Tags.Select(t => t.Name));
        Assert.Equal("Run", Assert.Single(device.Tags).Name);
        Assert.Equal("PLC1.S7Plus.DB490.Type.Errors.Error1",
            await fx.TextAsync("SELECT canonical_path FROM IGW_tags WHERE name='Error1'"));
    }

    [Fact]
    public async Task Existing_channel_and_device_settings_are_kept_and_missing_keys_added()
    {
        await using var fx = await Fixture.CreateAsync(new IgwProject());
        await fx.Repository.BulkUpsertTagsAsync(Request(Item("A")), null, default);
        await fx.ExecAsync("UPDATE IGW_devices SET settings_json='{\"model\":\"S7-1500\",\"slot\":\"3\"}'");

        var request = Request(Item("B"));
        request.Device = request.Device with
        {
            Endpoint = "10.0.0.9:102",
            Settings = new() { ["model"] = "S7-1200", ["slot"] = "1", ["dcsLogicalDevice"] = "S1500_10.0.0.9" },
        };
        await fx.Repository.BulkUpsertTagsAsync(request, null, default);

        var device = (await fx.Repository.LoadRuntimeProjectAsync(default)).Channels[0].Devices[0];
        Assert.Equal("10.0.0.9:102", device.Endpoint);
        Assert.Equal("S7-1500", device.Settings["model"]);
        Assert.Equal("3", device.Settings["slot"]);
        Assert.Equal("S1500_10.0.0.9", device.Settings["dcsLogicalDevice"]);
        Assert.Equal(ProjectBulkTagMetadata.ManagedByBulk, device.Settings[ProjectBulkTagMetadata.ManagedByKey]);
    }

    [Fact]
    public async Task Created_and_taken_over_devices_are_marked_bulk_managed()
    {
        await using var fx = await Fixture.CreateAsync(new IgwProject());
        await fx.Repository.BulkUpsertTagsAsync(Request(Item("A")), null, default);
        var created = (await fx.Repository.LoadRuntimeProjectAsync(default)).Channels[0].Devices[0];
        Assert.True(ProjectBulkTagMetadata.IsBulkManaged(created));

        // 已有设备（如 AddTags 建的）被批量注册接管：只加归属标记，其它 settings 原样保留
        await fx.ExecAsync(
            "UPDATE IGW_devices SET settings_json='{\"model\":\"S7-1500\",\"dcsLogicalDevice\":\"S1500_10.0.0.9\",\"dcsCommunicationType\":\"S1500\"}'");
        var result = await fx.Repository.BulkUpsertTagsAsync(Request(Item("B")), null, default);
        Assert.True(result.Applied, result.Error);

        var device = (await fx.Repository.LoadRuntimeProjectAsync(default)).Channels[0].Devices[0];
        Assert.True(ProjectBulkTagMetadata.IsBulkManaged(device));
        Assert.Equal("S1500_10.0.0.9", device.Settings["dcsLogicalDevice"]);
        Assert.Equal("S1500", device.Settings["dcsCommunicationType"]);
        Assert.Equal("S7-1500", device.Settings["model"]);
    }

    [Fact]
    public async Task Bulk_update_of_existing_device_keeps_kep_properties()
    {
        // 设备 upsert(补设置/改 endpoint)会整列写回 kep_properties_json：读回的设备文档必须带着原有 KEP 属性
        await using var fx = await Fixture.CreateAsync(new IgwProject());
        await fx.Repository.BulkUpsertTagsAsync(Request(Item("A", "DB1")), null, default);
        await fx.ExecAsync("UPDATE IGW_devices SET kep_properties_json='{\"servermain.DEVICE_ID_STRING\":\"<10.0.0.1>0\"}'");
        await fx.ExecAsync("UPDATE IGW_tag_groups SET kep_properties_json='{\"servermain.GROUP_X\":1}'");
        await fx.ExecAsync("UPDATE IGW_tag_options SET kep_properties_json='{\"servermain.TAG_X\":2}'");

        var request = Request(Item("A", "DB1"), Item("B", "DB1"));
        request.Device = request.Device with
        {
            Endpoint = "10.0.0.9:102",
            Settings = new() { ["model"] = "S7-1500", ["newKey"] = "1" },
        };
        var result = await fx.Repository.BulkUpsertTagsAsync(request, null, default);
        Assert.True(result.Applied, result.Error);

        var device = (await fx.Repository.LoadRuntimeProjectAsync(default)).Channels[0].Devices[0];
        Assert.Equal("10.0.0.9:102", device.Endpoint);
        Assert.Equal("1", device.Settings["newKey"]);
        Assert.NotNull(device.KepProperties);
        Assert.Equal("<10.0.0.1>0", device.KepProperties!["servermain.DEVICE_ID_STRING"].GetString());
        var group = Assert.Single(device.Groups);
        Assert.Equal(1, group.KepProperties!["servermain.GROUP_X"].GetInt32());
        Assert.Equal(2, group.Tags.Single(t => t.Name == "A").KepProperties!["servermain.TAG_X"].GetInt32());
        Assert.Null(group.Tags.Single(t => t.Name == "B").KepProperties);   // 新点默认 '{}'
        var loaded = await fx.Repository.LoadDeviceAsync(result.DeviceId!, default);
        Assert.Equal("<10.0.0.1>0", loaded.Device.KepProperties!["servermain.DEVICE_ID_STRING"].GetString());
    }

    [Fact]
    public async Task Existing_tags_are_updated_in_place_and_keep_their_id()
    {
        await using var fx = await Fixture.CreateAsync(new IgwProject());
        await fx.Repository.BulkUpsertTagsAsync(Request(Item("A", "DB1")), null, default);
        string id = await fx.TextAsync("SELECT id FROM IGW_tags WHERE name='A'");

        var changed = Item("A", "DB1");
        changed.Address = "DB1.Changed";
        var result = await fx.Repository.BulkUpsertTagsAsync(Request(changed), null, default);

        Assert.Equal((0, 1), (result.Created, result.Updated));
        Assert.Equal(id, await fx.TextAsync("SELECT id FROM IGW_tags WHERE name='A'"));
        Assert.Equal("DB1.Changed", await fx.TextAsync("SELECT address FROM IGW_tags WHERE name='A'"));
    }

    [Fact]
    public async Task Replace_groups_removes_stale_subtree_before_writing()
    {
        await using var fx = await Fixture.CreateAsync(new IgwProject());
        await fx.Repository.BulkUpsertTagsAsync(Request(
            Item("Old", "DB1", "Sub"), Item("Keep", "DB1"), Item("Other", "DB2")), null, default);

        var request = Request(Item("Keep", "DB1"));
        request.ReplaceGroups = [["DB1"]];
        var result = await fx.Repository.BulkUpsertTagsAsync(request, null, default);

        Assert.Equal(2, result.Deleted);
        Assert.Equal(1, result.Created);
        Assert.Equal(2, await fx.CountAsync("IGW_tags"));
        Assert.Equal(0, await fx.CountAsync("IGW_tag_groups WHERE name='Sub'"));
        Assert.Equal(1, await fx.CountAsync("IGW_tags WHERE name='Other'"));
    }

    [Fact]
    public async Task Driver_mismatch_rolls_back_everything()
    {
        await using var fx = await Fixture.CreateAsync(new IgwProject());
        await fx.Repository.BulkUpsertTagsAsync(Request(Item("A")), null, default);
        long before = (await fx.Repository.GetRevisionAsync(default)).Value;
        var request = Request(Item("B"));
        request.Channel = request.Channel with { DriverId = "Modbus.Tcp" };

        var result = await fx.Repository.BulkUpsertTagsAsync(request, null, default);

        Assert.False(result.Applied);
        Assert.Equal("channel_driver_mismatch", result.ErrorCode);
        Assert.Equal(before, (await fx.Repository.GetRevisionAsync(default)).Value);
        Assert.Equal(1, await fx.CountAsync("IGW_tags"));
    }

    [Fact]
    public async Task Stale_expected_revision_is_a_conflict()
    {
        await using var fx = await Fixture.CreateAsync(new IgwProject());
        await fx.Repository.BulkUpsertTagsAsync(Request(Item("A")), null, default);
        long before = (await fx.Repository.GetRevisionAsync(default)).Value;
        var result = await fx.Repository.BulkUpsertTagsAsync(Request(Item("B")), new ProjectRevision(0), default);
        Assert.True(result.Conflict);
        Assert.False(result.Applied);
        Assert.Equal("revision_conflict", result.ErrorCode);
        Assert.Equal(before, result.Revision.Value);
        Assert.Equal(before, (await fx.Repository.GetRevisionAsync(default)).Value);
        Assert.Equal(1, await fx.CountAsync("IGW_tags"));
    }

    [Fact]
    public async Task Update_keeps_on_site_options_and_new_tags_get_default_options()
    {
        await using var fx = await Fixture.CreateAsync(new IgwProject());
        await fx.Repository.BulkUpsertTagsAsync(Request(Item("A", "DB1")), null, default);
        await fx.ExecAsync("""
            UPDATE IGW_tag_options SET scaling_json='{"k":1}', alarm_json='{"a":1}', deadband=2.5, deadband_type=1,
                write_confirmation_json='{"w":1}';
            UPDATE IGW_tags SET text_encoding=2, sort_order=42;
            """);

        var changed = Item("A", "DB1");
        changed.Address = "DB1.Moved";
        var result = await fx.Repository.BulkUpsertTagsAsync(Request(changed, Item("New", "DB1")), null, default);

        Assert.Equal((1, 1), (result.Created, result.Updated));
        Assert.Equal("DB1.Moved", await fx.TextAsync("SELECT address FROM IGW_tags WHERE name='A'"));
        Assert.Equal("{\"k\":1}|{\"a\":1}|2.5|1|{\"w\":1}|2|42", await fx.TextAsync("""
            SELECT o.scaling_json||'|'||o.alarm_json||'|'||o.deadband||'|'||o.deadband_type||'|'||
                   o.write_confirmation_json||'|'||t.text_encoding||'|'||t.sort_order
            FROM IGW_tags t JOIN IGW_tag_options o ON o.tag_id=t.id WHERE t.name='A'
            """));
        Assert.Equal("1|0.0|0|0", await fx.TextAsync("""
            SELECT (o.scaling_json IS NULL AND o.alarm_json IS NULL AND o.write_confirmation_json IS NULL
                    AND t.text_encoding IS NULL)||'|'||o.deadband||'|'||o.deadband_type||'|'||o.is_array
            FROM IGW_tags t JOIN IGW_tag_options o ON o.tag_id=t.id WHERE t.name='New'
            """));
    }

    [Fact]
    public async Task Omitted_optional_fields_keep_stored_values_and_non_null_values_update_them()
    {
        await using var fx = await Fixture.CreateAsync(new IgwProject());
        var first = Item("A");
        first.Description = "desc1";
        first.EngUnit = "mm";
        first.StringLength = 20;
        first.Metadata = new() { ["k"] = "v1" };
        await fx.Repository.BulkUpsertTagsAsync(Request(first), null, default);

        await fx.Repository.BulkUpsertTagsAsync(Request(Item("A")), null, default);
        Assert.Equal("desc1|mm|20|v1", await ReadOptionalAsync(fx));

        var second = Item("A");
        second.Description = "desc2";
        second.EngUnit = "cm";
        second.StringLength = 30;
        second.Metadata = new() { ["k"] = "v2" };
        await fx.Repository.BulkUpsertTagsAsync(Request(second), null, default);
        Assert.Equal("desc2|cm|30|v2", await ReadOptionalAsync(fx));

        static Task<string> ReadOptionalAsync(Fixture fx) => fx.TextAsync("""
            SELECT t.description||'|'||t.eng_unit||'|'||t.string_length||'|'||json_extract(o.metadata_json,'$.k')
            FROM IGW_tags t JOIN IGW_tag_options o ON o.tag_id=t.id WHERE t.name='A'
            """);
    }

    [Fact]
    public async Task Names_match_case_insensitively_and_keep_stored_casing()
    {
        await using var fx = await Fixture.CreateAsync(new IgwProject());
        await fx.Repository.BulkUpsertTagsAsync(Request(Item("A", "DB1")), null, default);
        string id = await fx.TextAsync("SELECT id FROM IGW_tags WHERE name='A'");

        var request = Request(Item("a", "db1"), Item("b", "db1"));
        request.Channel = request.Channel with { Name = "plc1" };
        request.Device = request.Device with { Name = "s7plus" };
        var result = await fx.Repository.BulkUpsertTagsAsync(request, null, default);

        Assert.True(result.Applied);
        Assert.Equal((1, 1), (result.Created, result.Updated));
        Assert.Equal(1, await fx.CountAsync("IGW_channels"));
        Assert.Equal(1, await fx.CountAsync("IGW_devices"));
        Assert.Equal(1, await fx.CountAsync("IGW_tag_groups"));
        Assert.Equal(2, await fx.CountAsync("IGW_tags"));
        Assert.Equal(id, await fx.TextAsync("SELECT id FROM IGW_tags WHERE name='A'"));
        Assert.Equal("PLC1.S7Plus.DB1.b", await fx.TextAsync("SELECT canonical_path FROM IGW_tags WHERE name='b'"));
        Assert.Equal("PLC1.S7Plus.DB1.a", await fx.TextAsync("SELECT canonical_path FROM IGW_tags WHERE id='" + id + "'"));
    }

    [Fact]
    public async Task Hundred_thousand_tags_are_written_in_one_request()
    {
        // 只验证功能（单请求 10 万点全部创建）；耗时由 benchmarks/IGW.ProjectStorage.Performance 的 bulk 场景覆盖，
        // 单元测试在全量并行下跑墙钟断言会偶发失败。
        await using var fx = await Fixture.CreateAsync(new IgwProject());
        var items = Enumerable.Range(0, 100_000).Select(i => Item("T" + i, "G" + (i / 1000))).ToArray();
        var result = await fx.Repository.BulkUpsertTagsAsync(Request(items), null, default);
        Assert.True(result.Applied);
        Assert.Equal(100_000, result.Created);
        Assert.Equal(100_000, await fx.CountAsync("IGW_tags"));
    }

    [Fact]
    public async Task New_tag_and_group_ids_are_time_ordered()
    {
        await using var fx = await Fixture.CreateAsync(new IgwProject());
        await fx.Repository.BulkUpsertTagsAsync(Request(Item("First", "G1")), null, default);
        await Task.Delay(5); // v7 GUID 以毫秒时间戳开头；跨毫秒才保证有序
        await fx.Repository.BulkUpsertTagsAsync(Request(Item("Second", "G2")), null, default);

        string first = await fx.TextAsync("SELECT id FROM IGW_tags WHERE name='First'");
        string second = await fx.TextAsync("SELECT id FROM IGW_tags WHERE name='Second'");
        Assert.True(string.CompareOrdinal(first, second) < 0, $"{first} 应排在 {second} 之前");
        Assert.Matches("^[0-9a-f]{32}$", first);
        Assert.Equal('7', first[12]); // 版本位
        string g1 = await fx.TextAsync("SELECT id FROM IGW_tag_groups WHERE name='G1'");
        string g2 = await fx.TextAsync("SELECT id FROM IGW_tag_groups WHERE name='G2'");
        Assert.True(string.CompareOrdinal(g1, g2) < 0);
    }

    [Fact]
    public async Task Concurrent_mutation_and_reader_during_bulk_write_wait_instead_of_failing()
    {
        await using var fx = await Fixture.CreateAsync(new IgwProject());
        var seed = Request(Item("R0", "Existing"), Item("R1", "Existing"));
        seed.Device = seed.Device with { Name = "Reader" };
        await fx.Repository.BulkUpsertTagsAsync(seed, null, default);
        string groupId = await fx.TextAsync("SELECT id FROM IGW_tag_groups WHERE name='Existing'");
        ProjectRevision before = await fx.Repository.GetRevisionAsync(default);

        var items = Enumerable.Range(0, 50_000).Select(i => Item("T" + i, "G" + (i / 1000))).ToArray();
        Task<ProjectBulkTagResult> bulk = Task.Run(() => fx.Repository.BulkUpsertTagsAsync(Request(items), null, default));
        await Task.Delay(50);

        // 共享缓存连接上的读者：批量写走私有缓存连接，不再被表锁卡住（这里只断言成功，不断言耗时）
        ProjectPage<ProjectTagRow> page = await fx.Repository.GetTagsAsync(
            new ProjectTagQuery(groupId, null, 0, 10), default);
        Assert.Equal(2, page.Total);

        // 另一个写者（界面 mutation，BEGIN IMMEDIATE）：等待写锁后正常执行，不能抛 SQLITE_BUSY/LOCKED
        var channel = new ChannelDef { Id = "", Name = "Other", DriverId = "Simulator" };
        ProjectMutationResult mutation = await fx.Repository.ExecuteAsync(
            new ProjectMutation(ProjectMutationKind.UpsertEntity, ProjectNodeKind.Channel, [channel.Id], null,
                System.Text.Json.JsonSerializer.Serialize(channel, ProjectStore.Options)),
            before,
            default);
        ProjectBulkTagResult result = await bulk;

        Assert.True(result.Applied, result.Error);
        Assert.Equal(50_000, result.Created);
        // 先拿到写锁则直接成功；排在批量之后则修订号已变、按乐观并发返回冲突——两种都不是异常
        Assert.True(mutation.Applied || mutation.Conflict, mutation.Error);
    }

    [Fact]
    public async Task Template_instance_device_is_rejected_by_the_writer()
    {
        var project = new IgwProject
        {
            Templates = [new DeviceTemplate { Name = "Tpl", DriverId = "Siemens.S7", Tags = [new TagDef { Name = "A", Address = "DB1.DBX0.0" }] }],
            Channels =
            [
                new ChannelDef
                {
                    Name = "PLC1", DriverId = "Siemens.S7",
                    Devices = [new DeviceDef { Name = "S7Plus", TemplateRef = "Tpl" }],
                },
            ],
        };
        await using var fx = await Fixture.CreateAsync(project);
        long before = (await fx.Repository.GetRevisionAsync(default)).Value;

        var result = await fx.Repository.BulkUpsertTagsAsync(Request(Item("B", "G")), null, default);

        Assert.False(result.Applied);
        Assert.Equal("template_instance_not_supported", result.ErrorCode);
        Assert.Equal(before, (await fx.Repository.GetRevisionAsync(default)).Value);
        Assert.Equal(0, await fx.CountAsync("IGW_tags"));
    }

    [Fact]
    public async Task Non_unique_constraint_failure_is_a_non_retryable_persistence_failure()
    {
        await using var fx = await Fixture.CreateAsync(new IgwProject());
        await fx.Repository.BulkUpsertTagsAsync(Request(Item("A")), null, default);
        await fx.ExecAsync("CREATE TRIGGER boom BEFORE INSERT ON IGW_tags WHEN NEW.name='Boom' BEGIN SELECT RAISE(ABORT,'dup'); END;");

        var result = await fx.Repository.BulkUpsertTagsAsync(Request(Item("Boom")), null, default);

        Assert.False(result.Applied);
        Assert.Equal("persistence_failed", result.ErrorCode);
        Assert.False(result.Retryable);
    }

    [Theory]
    [InlineData(5, 5, "persistence_failed", true)]        // SQLITE_BUSY
    [InlineData(6, 6, "persistence_failed", true)]        // SQLITE_LOCKED
    [InlineData(10, 10, "persistence_failed", true)]      // SQLITE_IOERR
    [InlineData(13, 13, "persistence_failed", true)]      // SQLITE_FULL
    [InlineData(19, 2067, "name_conflict", false)]        // SQLITE_CONSTRAINT_UNIQUE
    [InlineData(19, 1555, "name_conflict", false)]        // SQLITE_CONSTRAINT_PRIMARYKEY
    [InlineData(19, 1811, "persistence_failed", false)]   // SQLITE_CONSTRAINT_TRIGGER
    [InlineData(19, 787, "persistence_failed", false)]    // SQLITE_CONSTRAINT_FOREIGNKEY
    [InlineData(11, 11, "persistence_failed", false)]     // SQLITE_CORRUPT
    public void Sqlite_failures_are_classified(int code, int extended, string expected, bool retryable)
    {
        var exception = new SqliteException("x", code, extended);
        Assert.Equal(expected, ProjectBulkTagWriter.FailureCode(exception));
        Assert.Equal(retryable, ProjectBulkTagWriter.IsRetryable(exception));
    }

    [Fact]
    public void Corrupt_stored_documents_are_not_retryable()
    {
        Assert.Equal("persistence_failed", ProjectBulkTagWriter.FailureCode(new System.Text.Json.JsonException("bad")));
        Assert.False(ProjectBulkTagWriter.IsRetryable(new System.Text.Json.JsonException("bad")));
        Assert.False(ProjectBulkTagWriter.IsRetryable(new InvalidDataException("bad")));
    }

    internal static ProjectBulkTagRequest Request(params ProjectBulkTagItem[] items) => new()
    {
        Channel = new ProjectBulkChannelSpec("PLC1", "Siemens.S7", new() { ["timeoutMs"] = "2000" }),
        Device = new ProjectBulkDeviceSpec("S7Plus", "10.0.0.1:102", new() { ["model"] = "S7-1500" }),
        Items = [.. items],
    };

    internal static ProjectBulkTagItem Item(string name, params string[] groups) => new()
    {
        GroupPath = [.. groups], Name = name, Address = "DB1." + name, DataType = IgwDataType.Bool,
        Access = TagAccess.ReadWrite, ScanRateMs = 100,
    };

    internal sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly ProjectDatabase _database;
        public SqliteProjectRepository Repository { get; }
        public string DatabasePath => _database.FilePath;

        private Fixture(string directory, ProjectDatabase database, SqliteProjectRepository repository)
        { _directory = directory; _database = database; Repository = repository; }

        public static async Task<Fixture> CreateAsync(IgwProject project)
        {
            string directory = Path.Combine(Path.GetTempPath(), "igw-bulk-tags", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var database = new ProjectDatabase(Path.Combine(directory, "project.db"));
            var repository = new SqliteProjectRepository(database,
                new ProjectSecretVault(new InstallationSecretProtector(Path.Combine(directory, "igw.masterkey"))));
            await repository.InitializeAsync(default);
            await repository.ReplaceFromSnapshotAsync(project, new ProjectRevision(0), default);
            return new Fixture(directory, database, repository);
        }

        public async Task<long> CountAsync(string tableAndWhere) =>
            Convert.ToInt64(await ScalarAsync($"SELECT COUNT(*) FROM {tableAndWhere}"));
        public async Task<string> TextAsync(string sql) => Convert.ToString(await ScalarAsync(sql))!;
        public async Task ExecAsync(string sql)
        {
            await using SqliteConnection connection = await _database.OpenAsync(default);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
        private async Task<object?> ScalarAsync(string sql)
        {
            await using SqliteConnection connection = await _database.OpenAsync(default);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return await command.ExecuteScalarAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Repository.DisposeAsync();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_directory, recursive: true); } catch { }
        }
    }
}
