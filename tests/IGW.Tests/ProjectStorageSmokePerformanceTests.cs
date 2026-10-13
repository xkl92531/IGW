using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using IGW.Persistence.Sqlite;
using Xunit;

namespace IGW.Tests;

public sealed class ProjectStorageSmokePerformanceTests
{
    [Fact]
    public async Task Hundred_thousand_points_remain_bounded_for_pages_update_and_export_snapshot()
    {
        string directory = Path.Combine(Path.GetTempPath(), "igw-storage-smoke", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var repository = new SqliteProjectRepository(
            new ProjectDatabase(Path.Combine(directory, "project.db")),
            new ProjectSecretVault(new InstallationSecretProtector(Path.Combine(directory, "igw.masterkey"))));
        try
        {
            await repository.InitializeAsync(default);
            ProjectRevision imported = await repository.ReplaceFromSnapshotAsync(
                BuildProject(100_000), new ProjectRevision(0), default);
            Assert.Equal(1, imported.Value);

            int[] offsets = [0, 1_003, 9_997, 20_001, 31_337, 49_999, 63_001, 77_777, 88_888, 99_743];
            foreach (int offset in offsets)
            {
                ProjectPage<ProjectTagRow> page = await repository.GetTagsAsync(
                    new ProjectTagQuery("group-points", null, offset, 137), default);
                Assert.Equal(100_000, page.Total);
                Assert.InRange(page.Items.Count, 1, 137);
                Assert.Equal(offset, page.Offset);
            }

            string json = await repository.GetEntityDocumentAsync(
                new ProjectNodeRef(ProjectNodeKind.Tag, "tag-0050000"), default);
            TagDef tag = JsonSerializer.Deserialize<TagDef>(json, ProjectStore.Options)!;
            tag.Description = "UpdatedNeedle";
            ProjectMutationResult updated = await repository.ExecuteAsync(
                new ProjectMutation(
                    ProjectMutationKind.UpsertEntity,
                    ProjectNodeKind.Tag,
                    [tag.Id],
                    "group-points",
                    JsonSerializer.Serialize(tag, ProjectStore.Options)),
                imported,
                default);
            Assert.True(updated.Applied, updated.Error);
            Assert.Equal(2, updated.Revision.Value);
            ProjectPage<ProjectTagRow> found = await repository.GetTagsAsync(
                new ProjectTagQuery("group-points", "UpdatedNeedle", 0, 5), default);
            Assert.Single(found.Items);
            Assert.Equal("tag-0050000", found.Items[0].Id);

            await using IProjectReadSnapshot snapshot = await repository.OpenReadSnapshotAsync(default);
            int streamed = 0;
            await foreach (ProjectEntityDocument _ in snapshot.StreamEntitiesAsync(
                               ProjectNodeKind.Tag, null, default))
                streamed++;
            Assert.Equal(100_000, streamed);
            Assert.Equal(updated.Revision, snapshot.Revision);
        }
        finally
        {
            await repository.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    internal static IgwProject BuildProject(int points)
    {
        var group = new TagGroupDef { Id = "group-points", Name = "Points" };
        for (int index = 0; index < points; index++)
        {
            group.Tags.Add(new TagDef
            {
                Id = $"tag-{index:D7}",
                Name = $"Value{index:D7}",
                Address = $"DB1.DBD{index * 4}",
                DataType = IgwDataType.Int32,
                Access = TagAccess.ReadWrite,
                ScanRateMs = 100,
                Description = $"Point {index}",
            });
        }
        return new IgwProject
        {
            Name = $"Storage-{points}",
            Channels =
            [
                new ChannelDef
                {
                    Id = "channel-benchmark",
                    Name = "Factory",
                    DriverId = "Simulator",
                    Devices =
                    [
                        new DeviceDef
                        {
                            Id = "device-benchmark",
                            Name = "PLC",
                            Groups = [group],
                        },
                    ],
                },
            ],
        };
    }
}
