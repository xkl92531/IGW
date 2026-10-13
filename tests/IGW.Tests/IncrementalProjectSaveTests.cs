using IGW.Abstractions;
using IGW.Core;
using IGW.Core.Security;
using IGW.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IGW.Tests;

/// <summary>
/// 工程保存只写改动：新增/修改的行写入，删除的行删掉，没变的行不动。
/// 结果必须和“清空后全量写入”完全一致。
/// </summary>
public sealed class IncrementalProjectSaveTests
{
    [Fact]
    public async Task Structural_edits_are_saved_incrementally_and_match_a_full_rewrite()
    {
        await using var incremental = await Fixture.CreateAsync(Project(groups: 3, tagsPerGroup: 20));
        IgwProject edited = await incremental.LoadAsync();
        var device = edited.Channels[0].Devices[0];
        device.Groups[0].Tags.Add(new TagDef { Id = "", Name = "Added", Address = "DB9.DBW0", DataType = IgwDataType.Int16 });
        device.Groups[1].Tags.RemoveAt(3);                                    // 删除
        var moved = device.Groups[2].Tags[0];                                  // 移到另一个组(同组不能重名)
        device.Groups[2].Tags.RemoveAt(0);
        moved.Name = "Moved";
        device.Groups[0].Tags.Add(moved);
        device.Groups[1].Tags[0].Name = "Renamed";                             // 改名
        device.Groups[1].Tags[1].Address = "DB1.DBW999";                       // 改地址
        device.Groups.Add(new TagGroupDef { Id = "", Name = "NewGroup",
            Tags = [new TagDef { Id = "", Name = "InNewGroup", Address = "DB8.DBW0" }] });
        device.Groups.RemoveAt(2);                                             // 删除整组(连带组内点)

        await incremental.SaveAsync(edited);

        await using var full = await Fixture.CreateAsync(await incremental.LoadAsync());
        Assert.Equal(ProjectStore.ToJson(await full.LoadAsync()), ProjectStore.ToJson(await incremental.LoadAsync()));
        Assert.Equal(await full.CountAsync("IGW_tags"), await incremental.CountAsync("IGW_tags"));
        Assert.Equal(await full.CountAsync("IGW_tag_options"), await incremental.CountAsync("IGW_tag_options"));
        Assert.Equal(await full.CountAsync("IGW_tag_groups"), await incremental.CountAsync("IGW_tag_groups"));
    }

    [Fact]
    public async Task Unchanged_rows_are_not_rewritten()
    {
        await using var fixture = await Fixture.CreateAsync(Project(groups: 2, tagsPerGroup: 200));
        IgwProject edited = await fixture.LoadAsync();
        edited.Channels[0].Devices[0].Groups[0].Tags.Add(new TagDef { Id = "", Name = "Added", Address = "DB9.DBW0" });
        edited.Channels[0].Devices[0].Groups[1].Tags[5].Description = "只改了这一个";

        await fixture.SaveAsync(edited);

        // 400 个点只有 1 个被修改(row_version 变 2)，1 个新增，其余保持第一次写入时的 1。
        Assert.Equal(1, await fixture.ScalarAsync("SELECT COUNT(*) FROM IGW_tags WHERE row_version > 1"));
        Assert.Equal(401, await fixture.CountAsync("IGW_tags"));
    }

    [Fact]
    public async Task Deleting_and_re_adding_the_same_name_saves_in_one_go()
    {
        await using var fixture = await Fixture.CreateAsync(Project(groups: 1, tagsPerGroup: 5));
        IgwProject edited = await fixture.LoadAsync();
        var tags = edited.Channels[0].Devices[0].Groups[0].Tags;
        string name = tags[2].Name;
        tags.RemoveAt(2);
        tags.Add(new TagDef { Id = "", Name = name, Address = "DB7.DBW7" });

        await fixture.SaveAsync(edited);

        var saved = (await fixture.LoadAsync()).Channels[0].Devices[0].Groups[0].Tags;
        Assert.Single(saved, tag => tag.Name == name);
        Assert.Equal("DB7.DBW7", saved.Single(tag => tag.Name == name).Address);
    }

    [Fact]
    public async Task Swapping_two_names_falls_back_to_a_full_rewrite_and_stays_correct()
    {
        await using var fixture = await Fixture.CreateAsync(Project(groups: 1, tagsPerGroup: 3));
        IgwProject edited = await fixture.LoadAsync();
        var tags = edited.Channels[0].Devices[0].Groups[0].Tags;
        (tags[0].Name, tags[1].Name) = (tags[1].Name, tags[0].Name);
        string first = tags[0].Id, second = tags[1].Id;

        await fixture.SaveAsync(edited);

        var saved = (await fixture.LoadAsync()).Channels[0].Devices[0].Groups[0].Tags;
        Assert.Equal(tags[0].Name, saved.Single(tag => tag.Id == first).Name);
        Assert.Equal(tags[1].Name, saved.Single(tag => tag.Id == second).Name);
    }

    [Fact]
    public async Task Incremental_save_keeps_runtime_flags_that_are_not_part_of_the_project_document()
    {
        await using var fixture = await Fixture.CreateAsync(Project(groups: 1, tagsPerGroup: 3));
        await fixture.ExecuteAsync("UPDATE IGW_devices SET enabled=0; UPDATE IGW_tag_groups SET flow_enabled=1;");
        IgwProject edited = await fixture.LoadAsync();
        edited.Channels[0].Devices[0].Groups[0].Tags.Add(new TagDef { Id = "", Name = "Added", Address = "DB9.DBW0" });

        await fixture.SaveAsync(edited);

        Assert.Equal(0, await fixture.ScalarAsync("SELECT enabled FROM IGW_devices"));
        Assert.Equal(1, await fixture.ScalarAsync("SELECT flow_enabled FROM IGW_tag_groups"));
    }

    [Fact]
    public async Task Moving_a_subgroup_out_of_a_deleted_group_keeps_its_tags()
    {
        // 删掉 G1 再新建同名 G1(新 id)，把原子组 Sub 挪进去：Sub 下点位路径不变，
        // 不能因为删除旧 G1 的级联把它们一起删掉。
        IgwProject project = Project(groups: 2, tagsPerGroup: 2);
        project.Channels[0].Devices[0].Groups[1].SubGroups.Add(new TagGroupDef
        {
            Id = "sub", Name = "Sub",
            Tags = [new TagDef { Id = "sub-t", Name = "S0", Address = "DB5.DBW0" }],
        });
        await using var fixture = await Fixture.CreateAsync(project);
        IgwProject edited = await fixture.LoadAsync();
        var device = edited.Channels[0].Devices[0];
        var sub = device.Groups[1].SubGroups[0];
        device.Groups.RemoveAt(1);
        device.Groups.Add(new TagGroupDef { Id = "", Name = "G1", SubGroups = [sub] });

        await fixture.SaveAsync(edited);

        var saved = (await fixture.LoadAsync()).Channels[0].Devices[0].Groups.Single(g => g.Name == "G1");
        Assert.Equal("S0", Assert.Single(Assert.Single(saved.SubGroups).Tags).Name);
        Assert.Equal(1, await fixture.ScalarAsync("SELECT COUNT(*) FROM IGW_tag_options WHERE tag_id='sub-t'"));
    }

    [Fact]
    public async Task Topology_changes_relink_mes_task_group_and_tag_references_without_rewriting_tasks()
    {
        // 任务先引用了还不存在的组和点位；之后只加组、加点(任务本身不变)，关联必须跟上，
        // 否则任务目录按设备/通道筛选时会漏掉它。
        IgwProject project = Project(groups: 1, tagsPerGroup: 1);
        var task = new MesTask { Id = "task", Name = "Late", GroupRef = "PLC.OP.Late" };
        task.Heartbeat.TagId = "PLC.OP.G0.LateTag";
        project.MesUpload.Tasks.Add(task);
        await using var fixture = await Fixture.CreateAsync(project);
        Assert.Equal(0, await fixture.ScalarAsync("SELECT COUNT(*) FROM IGW_mes_tasks WHERE group_id IS NOT NULL"));

        IgwProject edited = await fixture.LoadAsync();
        var device = edited.Channels[0].Devices[0];
        device.Groups.Add(new TagGroupDef { Id = "late-group", Name = "Late" });
        device.Groups[0].Tags.Add(new TagDef { Id = "late-tag", Name = "LateTag", Address = "DB1.DBW10" });
        await fixture.SaveAsync(edited);

        Assert.Equal(1, await fixture.ScalarAsync("SELECT COUNT(*) FROM IGW_mes_tasks WHERE group_id='late-group'"));
        Assert.Equal(1, await fixture.ScalarAsync("SELECT COUNT(*) FROM IGW_mes_task_index WHERE referenced_tag_id='late-tag'"));
        Assert.Equal(1, await fixture.ScalarAsync("SELECT row_version FROM IGW_mes_tasks WHERE id='task'"));
    }

    [Fact]
    public async Task Duplicate_ids_from_copy_paste_do_not_overwrite_each_other()
    {
        await using var fixture = await Fixture.CreateAsync(Project(groups: 2, tagsPerGroup: 2));
        IgwProject edited = await fixture.LoadAsync();
        var device = edited.Channels[0].Devices[0];
        var original = device.Groups[0].Tags[0];
        device.Groups[1].Tags.Add(new TagDef { Id = original.Id, Name = "Copy", Address = original.Address });

        await fixture.SaveAsync(edited);

        var saved = (await fixture.LoadAsync()).Channels[0].Devices[0];
        Assert.Equal(original.Id, saved.Groups[0].Tags[0].Id);
        Assert.Contains(saved.Groups[1].Tags, tag => tag.Name == "Copy" && tag.Id != original.Id);
        Assert.Equal(5, await fixture.CountAsync("IGW_tags"));
    }

    private static IgwProject Project(int groups, int tagsPerGroup) => new()
    {
        Name = "增量保存",
        Channels =
        [
            new ChannelDef
            {
                Id = "channel", Name = "PLC", DriverId = "Simulator",
                Devices =
                [
                    new DeviceDef
                    {
                        Id = "device", Name = "OP",
                        Groups = Enumerable.Range(0, groups).Select(g => new TagGroupDef
                        {
                            Id = $"group-{g}", Name = $"G{g}",
                            Tags = Enumerable.Range(0, tagsPerGroup).Select(t => new TagDef
                            {
                                Id = $"tag-{g}-{t}", Name = $"T{t}", Address = $"DB{g}.DBW{t * 2}", DataType = IgwDataType.Int16,
                            }).ToList(),
                        }).ToList(),
                    },
                ],
            },
        ],
    };

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly ProjectDatabase _database;
        private readonly SqliteProjectRepository _repository;

        private Fixture(string directory, ProjectDatabase database, SqliteProjectRepository repository)
        {
            _directory = directory; _database = database; _repository = repository;
        }

        public static async Task<Fixture> CreateAsync(IgwProject project)
        {
            string directory = Path.Combine(Path.GetTempPath(), "igw-incremental-save", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var database = new ProjectDatabase(Path.Combine(directory, "project.db"));
            var repository = new SqliteProjectRepository(database,
                new ProjectSecretVault(new InstallationSecretProtector(Path.Combine(directory, "igw.masterkey"))));
            await repository.InitializeAsync(default);
            await repository.ReplaceFromSnapshotAsync(project, new ProjectRevision(0), default);
            return new Fixture(directory, database, repository);
        }

        public Task<IgwProject> LoadAsync() => _repository.LoadRuntimeProjectAsync(default);

        public async Task SaveAsync(IgwProject project)
        {
            ProjectRevision revision = await _repository.GetRevisionAsync(default);
            await _repository.ReplaceFromSnapshotAsync(project, revision, default);
        }

        public Task<long> CountAsync(string table) => ScalarAsync($"SELECT COUNT(*) FROM {table}");

        public async Task<long> ScalarAsync(string sql)
        {
            await using SqliteConnection connection = await _database.OpenAsync(default);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        public async Task ExecuteAsync(string sql)
        {
            await using SqliteConnection connection = await _database.OpenAsync(default);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await _repository.DisposeAsync();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        }
    }
}
