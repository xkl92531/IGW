using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using Xunit;

namespace IGW.Tests;

public sealed class ProjectMutationPatcherTests
{
    [Fact]
    public void Batch_is_applied_in_memory_without_touching_the_running_project()
    {
        IgwProject before = Sample();
        ProjectMutation batch = Batch(
            Upsert(ProjectNodeKind.Tag, "g1", new TagDef { Id = "t1", Name = "A", Address = "DB1.DBW0", Description = "改说明" }),
            Upsert(ProjectNodeKind.Group, "d1", new TagGroupDef { Id = "g-new", Name = "New" }),
            Upsert(ProjectNodeKind.Tag, "g-new", new TagDef { Id = "t-new", Name = "C", Address = "DB2.DBW0" }),
            new ProjectMutation(ProjectMutationKind.DeleteEntities, ProjectNodeKind.Tag, ["t2"], null, null));

        ProjectPatch patch = Assert.IsType<ProjectPatch>(ProjectMutationPatcher.TryApply(before, batch));

        DeviceDef device = patch.Project.Channels[0].Devices[0];
        Assert.Equal("改说明", device.Groups[0].Tags.Single().Description);
        Assert.Equal("C", device.Groups[1].Tags.Single().Name);
        Assert.Equal(["C1.D1"], patch.Summary.ChangedDevices);
        Assert.Contains("C1.D1.DB1.B", patch.Summary.RemovedPaths);
        // 旧工程保持原样，未受影响的设备对象共享。
        Assert.Equal(2, before.Channels[0].Devices[0].Groups[0].Tags.Count);
        Assert.Null(before.Channels[0].Devices[0].Groups[0].Tags[0].Description);
        Assert.Single(before.Channels[0].Devices[0].Groups);
        Assert.Same(before.Channels[0].Devices[1], patch.Project.Channels[0].Devices[1]);
    }

    [Fact]
    public void Cut_and_paste_recreating_tags_with_their_old_ids_stays_on_the_fast_path()
    {
        IgwProject before = Sample();
        ProjectPatch patch = Assert.IsType<ProjectPatch>(ProjectMutationPatcher.TryApply(before, Batch(
            new ProjectMutation(ProjectMutationKind.DeleteEntities, ProjectNodeKind.Group, ["g1"], null, null),
            Upsert(ProjectNodeKind.Group, "d2", new TagGroupDef { Id = "g-pasted", Name = "DB1" }),
            Upsert(ProjectNodeKind.Tag, "g-pasted", new TagDef { Id = "t1", Name = "A", Address = "DB1.DBW0" }),
            Upsert(ProjectNodeKind.Tag, "g-pasted", new TagDef { Id = "t2", Name = "B", Address = "DB1.DBW2" }))));

        Assert.Empty(patch.Project.Channels[0].Devices[0].Groups);
        Assert.Equal(["A", "B"], patch.Project.Channels[0].Devices[1].Groups.Single().Tags.Select(t => t.Name));
        Assert.Equal(["C1.D1", "C1.D2"], patch.Summary.ChangedDevices.Order());

        // 第二次合并复用缓存的位置索引：点位已经在 D2 下。
        ProjectPatch second = Assert.IsType<ProjectPatch>(ProjectMutationPatcher.TryApply(patch.Project,
            Upsert(ProjectNodeKind.Tag, "g-pasted", new TagDef { Id = "t1", Name = "A", Address = "DB1.DBW0", Description = "再改" })));
        Assert.Equal("再改", second.Project.Channels[0].Devices[1].Groups.Single().Tags[0].Description);
        Assert.Equal(["C1.D2"], second.Summary.ChangedDevices);
    }

    [Fact]
    public void Channel_rename_and_delete_are_applied_incrementally()
    {
        IgwProject before = Sample();
        ProjectPatch renamed = ProjectMutationPatcher.TryApply(before,
            Upsert(ProjectNodeKind.Channel, null, new ChannelDef { Id = "c1", Name = "C9", DriverId = "Simulator" }))!;
        Assert.Equal(["C9.D1", "C9.D2"], renamed.Summary.ChangedDevices.Order());
        Assert.Equal(["C1.D1", "C1.D2"], renamed.Summary.RemovedDevices.Order());
        Assert.Equal(["C1"], renamed.Summary.RemovedChannels);
        Assert.Equal(2, renamed.Project.Channels[0].Devices[0].Groups[0].Tags.Count);

        ProjectPatch deleted = ProjectMutationPatcher.TryApply(renamed.Project,
            new ProjectMutation(ProjectMutationKind.DeleteEntities, ProjectNodeKind.Channel, ["c1"], null, null))!;
        Assert.Empty(deleted.Project.Channels);
        Assert.Equal(["C9.D1", "C9.D2"], deleted.Summary.RemovedDevices.Order());
        Assert.Equal(["C9"], deleted.Summary.RemovedChannels);
    }

    [Fact]
    public void Settings_change_flags_tag_stream_only_when_stream_parameters_change()
    {
        IgwProject before = Sample();
        ProjectPatch retention = ProjectMutationPatcher.TryApply(before, new ProjectMutation(
            ProjectMutationKind.ReplaceProjectSections, ProjectNodeKind.Project, ["settings"], null,
            "{\"settings\":{\"logRetentionDays\":7}}"))!;
        Assert.True(retention.Summary.SettingsChanged);
        Assert.False(retention.Summary.TagStreamChanged);
        Assert.False(retention.Summary.SouthboundChanged);

        ProjectPatch stream = ProjectMutationPatcher.TryApply(before, new ProjectMutation(
            ProjectMutationKind.ReplaceProjectSections, ProjectNodeKind.Project, ["settings"], null,
            "{\"settings\":{\"tagStream\":{\"maxStreams\":5}}}"))!;
        Assert.True(stream.Summary.TagStreamChanged);
    }

    [Fact]
    public void Renamed_device_is_reported_as_removed_under_its_old_name()
    {
        IgwProject before = Sample();
        ProjectPatch patch = ProjectMutationPatcher.TryApply(before,
            Upsert(ProjectNodeKind.Device, "c1", new DeviceDef { Id = "d1", Name = "D1x", Endpoint = "10.0.0.1" }))!;

        Assert.Equal(["C1.D1x"], patch.Summary.ChangedDevices);
        Assert.Equal(["C1.D1"], patch.Summary.RemovedDevices);
        Assert.Equal(2, patch.Project.Channels[0].Devices[0].Groups[0].Tags.Count);
    }

    [Fact]
    public void Unsupported_or_inconsistent_mutations_fall_back_to_a_full_reload()
    {
        IgwProject before = Sample();
        Assert.Null(ProjectMutationPatcher.TryApply(before,
            new ProjectMutation(ProjectMutationKind.MoveEntities, ProjectNodeKind.Tag, ["t1"], "d2", null)));
        Assert.Null(ProjectMutationPatcher.TryApply(before,
            new ProjectMutation(ProjectMutationKind.DeleteEntities, ProjectNodeKind.Tag, ["missing"], null, null)));
        Assert.Null(ProjectMutationPatcher.TryApply(before,
            Upsert(ProjectNodeKind.Tag, "g1", new TagDef { Id = "", Name = "NoId" })));
    }

    [Fact]
    public void Mes_tasks_and_sections_are_replaced_without_southbound_changes()
    {
        IgwProject before = Sample();
        before.MesUpload.Tasks.Add(new MesTask { Id = "m1", Name = "DB1", GroupRef = "C1.D1.DB1", Enabled = false });
        var task = new MesTask { Id = "m1", Name = "DB1", GroupRef = "C1.D1.DB1", Enabled = true };
        ProjectPatch patch = ProjectMutationPatcher.TryApply(before, Batch(
            new ProjectMutation(ProjectMutationKind.SaveMesTasks, ProjectNodeKind.Project, [], null,
                JsonSerializer.Serialize(new MesTaskBatchSave { Tasks = [task] }, ProjectStore.Options)),
            new ProjectMutation(ProjectMutationKind.ReplaceProjectSections, ProjectNodeKind.Project, ["history"], null,
                "{\"history\":{\"enabled\":true}}")))!;

        Assert.True(patch.Project.MesUpload.Tasks.Single().Enabled);
        Assert.False(before.MesUpload.Tasks.Single().Enabled);
        Assert.True(patch.Project.History.Enabled);
        Assert.True(patch.Summary.MesChanged);
        Assert.True(patch.Summary.HistoryChanged);
        Assert.False(patch.Summary.SouthboundChanged);
    }

    private static ProjectMutation Upsert<T>(ProjectNodeKind kind, string? target, T document) =>
        new(ProjectMutationKind.UpsertEntity, kind, [], target, JsonSerializer.Serialize(document, ProjectStore.Options));

    private static ProjectMutation Batch(params ProjectMutation[] items) =>
        new(ProjectMutationKind.Batch, ProjectNodeKind.Project, [], null, JsonSerializer.Serialize(items, ProjectStore.Options));

    private static IgwProject Sample() => new()
    {
        Channels =
        [
            new ChannelDef
            {
                Id = "c1", Name = "C1", DriverId = "Simulator",
                Devices =
                [
                    new DeviceDef
                    {
                        Id = "d1", Name = "D1", Endpoint = "10.0.0.1",
                        Groups =
                        [
                            new TagGroupDef
                            {
                                Id = "g1", Name = "DB1",
                                Tags = [new TagDef { Id = "t1", Name = "A", Address = "DB1.DBW0" }, new TagDef { Id = "t2", Name = "B", Address = "DB1.DBW2" }],
                            },
                        ],
                    },
                    new DeviceDef { Id = "d2", Name = "D2", Endpoint = "10.0.0.2" },
                ],
            },
        ],
    };
}
