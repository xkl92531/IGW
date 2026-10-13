using System.Text;
using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using IGW.Persistence.Sqlite;
using Xunit;

namespace IGW.Tests;

public sealed class SqliteProjectRepositoryTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task Group_edit_can_save_with_a_disabled_mes_flow(bool initiallyAvailable, bool requestedAvailable)
    {
        IgwProject project = ProjectWithTags(3);
        project.MesUpload.Tasks.Add(new MesTask
        {
            Id = "mes-group-task", Name = "Group task", GroupRef = "Factory.PLC.Many",
            DeviceRef = "Factory.PLC", Enabled = false, FlowAvailable = initiallyAvailable,
        });
        await using var fixture = await RepositoryFixture.CreateAsync(project);
        var revision = await fixture.Repository.GetRevisionAsync(default);
        MesTask draft = project.MesUpload.Tasks[0];
        draft.FlowAvailable = requestedAvailable;
        ProjectMutation[] changes =
        [
            new(ProjectMutationKind.UpsertEntity, ProjectNodeKind.Group, ["group-many"], "device-plc",
                JsonSerializer.Serialize(new TagGroupDef
                {
                    Id = "group-many", Name = "Many", Description = "saved group note",
                }, ProjectStore.Options)),
            new(ProjectMutationKind.SaveMesTasks, ProjectNodeKind.Project, [], null,
                JsonSerializer.Serialize(new MesTaskBatchSave { Tasks = [draft] }, ProjectStore.Options)),
        ];

        ProjectMutationResult saved = await fixture.Repository.ExecuteAsync(
            new ProjectMutation(ProjectMutationKind.Batch, ProjectNodeKind.Project, [], null,
                JsonSerializer.Serialize(changes, ProjectStore.Options)), revision, default);

        Assert.True(saved.Applied, saved.Error);
        Assert.Equal(revision.Value + 1, saved.Revision.Value);
        IgwProject reloaded = await fixture.Repository.LoadRuntimeProjectAsync(default);
        Assert.Equal("saved group note", reloaded.Channels[0].Devices[0].Groups[0].Description);
        Assert.Equal(requestedAvailable, Assert.Single(reloaded.MesUpload.Tasks).FlowAvailable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Snapshot_small_edit_does_not_rewrite_unchanged_tags(bool editTag)
    {
        await using var fixture = await RepositoryFixture.CreateAsync(ProjectWithTags(3));
        var project = await fixture.Repository.LoadRuntimeProjectAsync(default);
        var revision = await fixture.Repository.GetRevisionAsync(default);
        var page = await fixture.Repository.GetTagsAsync(new ProjectTagQuery("group-many", null, 0, 3), default);
        string changedId = page.Items[0].Id;
        await using (var connection = await fixture.Database.OpenAsync(default))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER reject_tag_delete BEFORE DELETE ON IGW_tags BEGIN SELECT RAISE(ABORT,'unchanged tags deleted'); END;
                CREATE TRIGGER reject_other_tag_update BEFORE UPDATE ON IGW_tags
                WHEN OLD.id <> 'tag-00000' BEGIN SELECT RAISE(ABORT,'unchanged tag updated'); END;
                """;
            // The fixture's first tag is used rather than assuming its ID format.
            command.CommandText = command.CommandText.Replace("tag-00000", changedId.Replace("'", "''"));
            await command.ExecuteNonQueryAsync();
        }
        project.Description = "new project note";
        if (editTag)
            project.Channels[0].Devices[0].Groups[0].Tags[0].Description = "new point note";

        var updated = await fixture.Repository.ReplaceFromSnapshotAsync(project, revision, default);

        Assert.Equal(revision.Value + 1, updated.Value);
        var saved = await fixture.Repository.LoadRuntimeProjectAsync(default);
        Assert.Equal("new project note", saved.Description);
        if (editTag) Assert.Equal("new point note", saved.Channels[0].Devices[0].Groups[0].Tags[0].Description);
        Assert.Equal(3, (await fixture.Repository.GetTagsAsync(new ProjectTagQuery("group-many", null, 0, 3), default)).Total);
    }

    [Fact]
    public async Task Paged_tag_query_and_tree_children_are_bounded_ordered_and_path_complete()
    {
        await using var fixture = await RepositoryFixture.CreateAsync(ProjectWithTags(25_050));

        ProjectPage<ProjectTagRow> page = await fixture.Repository.GetTagsAsync(
            new ProjectTagQuery("group-many", null, 10_000, 200), default);

        Assert.Equal(25_050, page.Total);
        Assert.Equal(200, page.Items.Count);
        Assert.Equal("tag-10000", page.Items[0].Id);
        Assert.Equal("Factory.PLC.Many.Tag10000", page.Items[0].ActualPath);
        Assert.Equal("tag-10199", page.Items[^1].Id);

        ProjectPage<ProjectTagRow> literalWildcard = await fixture.Repository.GetTagsAsync(
            new ProjectTagQuery("group-many", "Needle_%", 0, 20), default);
        Assert.Single(literalWildcard.Items);
        Assert.Equal("tag-special", literalWildcard.Items[0].Id);

        IReadOnlyList<ProjectTreeNode> children = await fixture.Repository.GetChildrenAsync(
            new ProjectNodeRef(ProjectNodeKind.Group, "group-tree"), default);
        ProjectTreeNode child = Assert.Single(children);
        Assert.Equal("group-tree-child", child.Id);
        Assert.Equal(ProjectNodeKind.Group, child.Kind);
        Assert.Equal("Factory.PLC.Tree.Child", child.ActualPath);
        Assert.Equal("直接子组", child.Description);
        Assert.False(child.HasChildren);
        Assert.Equal(1, child.DirectTagCount);
    }

    [Fact]
    public async Task Multi_selection_move_is_atomic_and_stale_revision_cannot_delete_rows()
    {
        await using var fixture = await RepositoryFixture.CreateAsync(ProjectWithTags(3));
        ProjectRevision revision = await fixture.Repository.GetRevisionAsync(default);
        string[] ids = ["tag-00000", "tag-00001", "tag-00002"];

        ProjectMutationResult moved = await fixture.Repository.ExecuteAsync(
            new ProjectMutation(ProjectMutationKind.MoveEntities, ProjectNodeKind.Tag, ids, "group-b", null),
            revision, default);
        ProjectMutationResult stale = await fixture.Repository.ExecuteAsync(
            new ProjectMutation(ProjectMutationKind.DeleteEntities, ProjectNodeKind.Tag, ids, null, null),
            revision, default);

        Assert.True(moved.Applied);
        Assert.False(moved.Conflict);
        Assert.Equal(revision.Value + 1, moved.Revision.Value);
        Assert.False(stale.Applied);
        Assert.True(stale.Conflict);
        Assert.Equal(moved.Revision, stale.Revision);

        ProjectPage<ProjectTagRow> destination = await fixture.Repository.GetTagsAsync(
            new ProjectTagQuery("group-b", null, 0, 20), default);
        Assert.Equal(3, destination.Total);
        Assert.All(destination.Items, item => Assert.StartsWith("Factory.PLC.Destination.", item.ActualPath));
    }

    [Fact]
    public async Task Group_move_accepts_a_device_target_and_updates_descendant_paths()
    {
        await using var fixture = await RepositoryFixture.CreateAsync(ProjectWithTags(3));
        ProjectRevision revision = await fixture.Repository.GetRevisionAsync(default);

        ProjectMutationResult moved = await fixture.Repository.ExecuteAsync(
            new ProjectMutation(
                ProjectMutationKind.MoveEntities,
                ProjectNodeKind.Group,
                ["group-tree-child"],
                "device-plc",
                null),
            revision,
            default);

        Assert.True(moved.Applied, moved.Error);
        IReadOnlyList<ProjectTreeNode> deviceChildren = await fixture.Repository.GetChildrenAsync(
            new ProjectNodeRef(ProjectNodeKind.Device, "device-plc"), default);
        ProjectTreeNode child = Assert.Single(deviceChildren, item => item.Id == "group-tree-child");
        Assert.Equal("Factory.PLC.Child", child.ActualPath);

        ProjectPage<ProjectTagRow> childTags = await fixture.Repository.GetTagsAsync(
            new ProjectTagQuery("group-tree-child", null, 0, 20), default);
        Assert.Equal("Factory.PLC.Child.Grandchild", Assert.Single(childTags.Items).ActualPath);
    }

    [Fact]
    public async Task Device_tag_query_returns_only_device_direct_tags()
    {
        // Kepware 导入常见形态：点位直挂设备、组很少甚至为空——设备视图必须能看到这些点。
        var project = new IgwProject
        {
            Name = "设备直挂点工程",
            Channels =
            [
                new ChannelDef
                {
                    Id = "channel-factory",
                    Name = "Factory",
                    DriverId = "Simulator",
                    Devices =
                    [
                        new DeviceDef
                        {
                            Id = "device-plc",
                            Name = "PLC",
                            Endpoint = "local",
                            Tags =
                            [
                                new TagDef { Id = "tag-device-0", Name = "Dev0", Address = "DB1.DBW0" },
                                new TagDef { Id = "tag-device-1", Name = "Dev1", Address = "DB1.DBW2" },
                            ],
                            Groups =
                            [
                                new TagGroupDef
                                {
                                    Id = "group-a",
                                    Name = "A",
                                    Tags = [new TagDef { Id = "tag-group-0", Name = "Grp0", Address = "DB2.DBW0" }],
                                },
                            ],
                        },
                        new DeviceDef
                        {
                            Id = "device-other",
                            Name = "Other",
                            Endpoint = "remote",
                            Tags = [new TagDef { Id = "tag-other-0", Name = "Other0", Address = "DB3.DBW0" }],
                        },
                    ],
                },
            ],
        };
        await using var fixture = await RepositoryFixture.CreateAsync(project);

        ProjectPage<ProjectTagRow> direct = await fixture.Repository.GetTagsAsync(
            new ProjectTagQuery(null, null, 0, 50, "device-plc"), default);
        Assert.Equal(2, direct.Total);
        Assert.Equal(
            ["tag-device-0", "tag-device-1"],
            direct.Items.Select(item => item.Id).Order().ToArray());

        ProjectPage<ProjectTagRow> searched = await fixture.Repository.GetTagsAsync(
            new ProjectTagQuery(null, "Dev1", 0, 50, "device-plc"), default);
        Assert.Equal("tag-device-1", Assert.Single(searched.Items).Id);

        Assert.Throws<ArgumentException>(() =>
            new ProjectTagQuery("group-a", null, 0, 50, "device-plc").Validate());
    }

    [Fact]
    public async Task Group_move_migrates_mes_task_references_to_the_new_path()
    {
        IgwProject project = ProjectWithTags(3);
        project.MesUpload.Tasks.Add(new MesTask
        {
            Id = "task-child",
            Name = "Child upload",
            GroupRef = "Factory.PLC.Tree.Child",
            DeviceRef = "Factory.PLC",
            Trigger = new MesTrigger { TagId = "Factory.PLC.Tree.Child.Grandchild" },
            Payload = new MesPayload
            {
                Fields = [new MesField { TagId = "Factory.PLC.Tree.Child.Grandchild", FieldName = "value" }],
            },
        });
        await using var fixture = await RepositoryFixture.CreateAsync(project);
        ProjectRevision revision = await fixture.Repository.GetRevisionAsync(default);

        ProjectMutationResult moved = await fixture.Repository.ExecuteAsync(
            new ProjectMutation(
                ProjectMutationKind.MoveEntities,
                ProjectNodeKind.Group,
                ["group-tree-child"],
                "group-b",
                null),
            revision,
            default);

        Assert.True(moved.Applied, moved.Error);
        MesTask task = Assert.Single((await fixture.Repository.LoadRuntimeProjectAsync(default)).MesUpload.Tasks);
        Assert.Equal("Factory.PLC.Destination.Child", task.GroupRef);
        Assert.Equal("Factory.PLC.Destination.Child.Grandchild", task.Trigger.TagId);
        Assert.Equal("Factory.PLC.Destination.Child.Grandchild", Assert.Single(task.Payload.Fields).TagId);
    }

    [Fact]
    public async Task Multi_selection_copy_is_atomic_and_preserves_the_source_rows()
    {
        await using var fixture = await RepositoryFixture.CreateAsync(ProjectWithTags(3));
        ProjectRevision revision = await fixture.Repository.GetRevisionAsync(default);
        string[] ids = ["tag-00000", "tag-00001", "tag-00002"];

        ProjectMutationResult copied = await fixture.Repository.ExecuteAsync(
            new ProjectMutation(ProjectMutationKind.CopyEntities, ProjectNodeKind.Tag, ids, "group-b", null),
            revision, default);

        Assert.True(copied.Applied, copied.Error);
        ProjectPage<ProjectTagRow> source = await fixture.Repository.GetTagsAsync(
            new ProjectTagQuery("group-many", null, 0, 20), default);
        ProjectPage<ProjectTagRow> destination = await fixture.Repository.GetTagsAsync(
            new ProjectTagQuery("group-b", null, 0, 20), default);
        Assert.Equal(3, source.Total);
        Assert.Equal(3, destination.Total);
        Assert.Equal(["Tag00000", "Tag00001", "Tag00002"], destination.Items.Select(item => item.Name));
        Assert.DoesNotContain(destination.Items, item => ids.Contains(item.Id));
    }

    [Fact]
    public async Task Read_snapshot_streams_complete_entity_documents_at_one_fixed_revision()
    {
        await using var fixture = await RepositoryFixture.CreateAsync(ProjectWithTags(3));
        await using IProjectReadSnapshot snapshot = await fixture.Repository.OpenReadSnapshotAsync(default);
        var documents = new List<ProjectEntityDocument>();

        await foreach (ProjectEntityDocument document in snapshot.StreamEntitiesAsync(
                           ProjectNodeKind.Tag, null, default))
            documents.Add(document);

        Assert.Equal(5, documents.Count);
        Assert.Equal("group-many", documents[0].ParentId);
        TagDef first = JsonSerializer.Deserialize<TagDef>(documents[0].DocumentJson, ProjectStore.Options)!;
        Assert.Equal("tag-00000", first.Id);
        Assert.Equal("Tag00000", first.Name);
        Assert.Equal(await fixture.Repository.GetRevisionAsync(default), snapshot.Revision);
    }

    [Fact]
    public async Task Local_channel_update_encrypts_new_reversible_secret_before_commit()
    {
        await using var fixture = await RepositoryFixture.CreateAsync(ProjectWithTags(3));
        ProjectRevision revision = await fixture.Repository.GetRevisionAsync(default);
        var channel = new ChannelDef
        {
            Id = "channel-factory",
            Name = "Factory",
            DriverId = "Simulator",
            Settings = new Dictionary<string, string>
            {
                ["password"] = "mutation-secret-marker",
                ["mode"] = "fast",
            },
        };

        ProjectMutationResult result = await fixture.Repository.ExecuteAsync(
            new ProjectMutation(
                ProjectMutationKind.UpsertEntity,
                ProjectNodeKind.Channel,
                [channel.Id],
                null,
                JsonSerializer.Serialize(channel, ProjectStore.Options)),
            revision,
            default);

        Assert.True(result.Applied, result.Error);
        string databaseBytes = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(fixture.Database.FilePath));
        Assert.DoesNotContain("mutation-secret-marker", databaseBytes);
        IgwProject restored = await fixture.Repository.LoadRuntimeProjectAsync(default);
        Assert.Equal("mutation-secret-marker", restored.Channels.Single().Settings["password"]);

        string storedDocument = await fixture.Repository.GetEntityDocumentAsync(
            new ProjectNodeRef(ProjectNodeKind.Channel, channel.Id), default);
        ChannelDef secondEdit = JsonSerializer.Deserialize<ChannelDef>(storedDocument, ProjectStore.Options)!;
        secondEdit.Settings["mode"] = "safe";
        ProjectMutationResult second = await fixture.Repository.ExecuteAsync(
            new ProjectMutation(
                ProjectMutationKind.UpsertEntity,
                ProjectNodeKind.Channel,
                [channel.Id],
                null,
                JsonSerializer.Serialize(secondEdit, ProjectStore.Options)),
            result.Revision,
            default);

        Assert.True(second.Applied, second.Error);
        IgwProject restoredAgain = await fixture.Repository.LoadRuntimeProjectAsync(default);
        Assert.Equal("mutation-secret-marker", restoredAgain.Channels.Single().Settings["password"]);
        Assert.Equal("safe", restoredAgain.Channels.Single().Settings["mode"]);
    }

    [Fact]
    public async Task Sibling_name_conflict_rolls_back_the_entire_multi_selection_move()
    {
        await using var fixture = await RepositoryFixture.CreateAsync(ProjectWithTags(3));
        ProjectRevision revision = await fixture.Repository.GetRevisionAsync(default);
        var duplicate = new TagDef
        {
            Id = "tag-destination-existing",
            Name = "Tag00000",
            Address = "DB9.DBW0",
        };
        ProjectMutationResult inserted = await fixture.Repository.ExecuteAsync(
            new ProjectMutation(
                ProjectMutationKind.UpsertEntity,
                ProjectNodeKind.Tag,
                [duplicate.Id],
                "group-b",
                JsonSerializer.Serialize(duplicate, ProjectStore.Options)),
            revision,
            default);
        Assert.True(inserted.Applied, inserted.Error);

        ProjectMutationResult moved = await fixture.Repository.ExecuteAsync(
            new ProjectMutation(
                ProjectMutationKind.MoveEntities,
                ProjectNodeKind.Tag,
                ["tag-00000", "tag-00001", "tag-00002"],
                "group-b",
                null),
            inserted.Revision,
            default);

        Assert.False(moved.Applied);
        Assert.False(moved.Conflict);
        Assert.Contains("名称重复", moved.Error);
        Assert.Equal(3, (await fixture.Repository.GetTagsAsync(
            new ProjectTagQuery("group-many", null, 0, 20), default)).Total);
        Assert.Equal(1, (await fixture.Repository.GetTagsAsync(
            new ProjectTagQuery("group-b", null, 0, 20), default)).Total);
    }

    private static IgwProject ProjectWithTags(int count)
    {
        var many = new TagGroupDef
        {
            Id = "group-many",
            Name = "Many",
            Description = "大点表",
        };
        for (int index = 0; index < count; index++)
        {
            many.Tags.Add(new TagDef
            {
                Id = $"tag-{index:D5}",
                Name = $"Tag{index:D5}",
                Address = $"DB1.DBW{index * 2}",
                DataType = IgwDataType.Int16,
                Access = TagAccess.ReadWrite,
                ScanRateMs = 100,
                Description = $"点 {index}",
            });
        }
        if (count > 10)
        {
            many.Tags[^1].Id = "tag-special";
            many.Tags[^1].Name = "Needle_%";
            many.Tags[^1].Address = "DB2.DBW0";
            many.Tags[^1].Description = "LIKE 转义验证";
        }

        return new IgwProject
        {
            Name = "分页工程",
            Channels =
            [
                new ChannelDef
                {
                    Id = "channel-factory",
                    Name = "Factory",
                    DriverId = "Simulator",
                    Devices =
                    [
                        new DeviceDef
                        {
                            Id = "device-plc",
                            Name = "PLC",
                            Endpoint = "local",
                            Groups =
                            [
                                many,
                                new TagGroupDef
                                {
                                    Id = "group-tree",
                                    Name = "Tree",
                                    Description = "树测试",
                                    SubGroups =
                                    [
                                        new TagGroupDef
                                        {
                                            Id = "group-tree-child",
                                            Name = "Child",
                                            Description = "直接子组",
                                            Tags =
                                            [
                                                new TagDef
                                                {
                                                    Id = "tag-tree-grandchild",
                                                    Name = "Grandchild",
                                                    Address = "DB3.DBW0",
                                                },
                                            ],
                                        },
                                    ],
                                    Tags =
                                    [
                                        new TagDef
                                        {
                                            Id = "tag-tree-direct",
                                            Name = "Direct",
                                            Address = "DB3.DBW2",
                                            Description = "直接点",
                                        },
                                    ],
                                },
                                new TagGroupDef
                                {
                                    Id = "group-b",
                                    Name = "Destination",
                                    Description = "移动目标",
                                },
                            ],
                        },
                    ],
                },
            ],
        };
    }

    private sealed class RepositoryFixture : IAsyncDisposable
    {
        private readonly string _directory;
        public ProjectDatabase Database { get; }
        public SqliteProjectRepository Repository { get; }

        private RepositoryFixture(
            string directory,
            ProjectDatabase database,
            SqliteProjectRepository repository)
        {
            _directory = directory;
            Database = database;
            Repository = repository;
        }

        public static async Task<RepositoryFixture> CreateAsync(IgwProject project)
        {
            string directory = Path.Combine(Path.GetTempPath(), "igw-repository-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var database = new ProjectDatabase(Path.Combine(directory, "project.db"));
            var vault = new ProjectSecretVault(
                new InstallationSecretProtector(Path.Combine(directory, "igw.masterkey")));
            var repository = new SqliteProjectRepository(database, vault);
            await repository.InitializeAsync(default);
            await repository.ReplaceFromSnapshotAsync(project, new ProjectRevision(0), default);
            return new RepositoryFixture(directory, database, repository);
        }

        public async ValueTask DisposeAsync()
        {
            await Repository.DisposeAsync();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
