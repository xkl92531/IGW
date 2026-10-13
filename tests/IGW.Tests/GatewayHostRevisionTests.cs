using IGW.Abstractions;
using IGW.Core;
using IGW.Runtime;
using IGW.Persistence.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using IGW.Drivers.Simulator;

namespace IGW.Tests;

public sealed class GatewayHostRevisionTests
{
    [Fact]
    public async Task Full_project_note_and_mes_saves_keep_unaffected_live_components()
    {
        string dir = Path.Combine(Path.GetTempPath(), "igw-save-runtime", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "project.igwproj");
        try
        {
            var initial = ProjectSaveRuntimeTests.Project();
            initial.OpcUa.Enabled = false;
            initial.MesUpload.LogDbPath = "";
            initial.MesUpload.Tasks.Add(new MesTask { Id = "task", Name = "Task", Enabled = true,
                GroupRef = "Line.PLC.Points", CollectIntervalMs = 60000 });
            ProjectStore.Save(initial, path);
            await using var host = await CreateHostAsync(initial, path, dir);
            await host.StartAsync();
            Assert.NotNull(host.Engine);
            var engine = host.Engine;
            var alarms = host.Alarms;
            var computed = host.Computed;
            var scheduler = host.Scheduler;
            var streams = host.RealtimeStreams;
            const string id = "Line.PLC.Points.Value";
            host.Hub.Update(id, DataValue.GoodAt((short)42, DateTime.UtcNow));
            Assert.True(host.Hub.TryGetHandleId(id, out int handle));
            var note = ProjectStore.Clone(host.Project);
            note.Channels[0].Devices[0].Groups[0].Tags[0].Description = "new note";
            Assert.True((await host.ApplyProjectAsync(note, host.ProjectRevision, default)).Applied);
            Assert.Same(engine, host.Engine);
            Assert.Same(alarms, host.Alarms);
            Assert.Same(computed, host.Computed);
            Assert.Same(scheduler, host.Scheduler);
            Assert.Same(streams, host.RealtimeStreams);
            Assert.True(host.Hub.TryGetHandleId(id, out int afterHandle));
            Assert.Equal(handle, afterHandle);
            Assert.True(host.Hub.TryGetDef(id, out var definition));
            Assert.Equal("new note", definition.Description);

            var mes = ProjectStore.Clone(host.Project);
            mes.MesUpload.Tasks[0].CollectIntervalMs = 90000;
            Assert.True((await host.ApplyProjectAsync(mes, host.ProjectRevision, default)).Applied);
            Assert.Same(engine, host.Engine);
            Assert.Same(alarms, host.Alarms);
            Assert.Same(computed, host.Computed);
            Assert.Same(scheduler, host.Scheduler);
            Assert.Same(streams, host.RealtimeStreams);
            Assert.Equal(90000, host.Project.MesUpload.Tasks[0].CollectIntervalMs);
            Assert.Single(host.MesTaskStatuses());
        }
        finally { DeleteTree(dir); }
    }

    [Fact]
    public async Task Two_project_updates_from_the_same_revision_cannot_both_apply()
    {
        string dir = Path.Combine(Path.GetTempPath(), "igw-revision-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "project.igwproj");
        try
        {
            var initial = new IgwProject { Name = "initial" };
            ProjectStore.Save(initial, path);
            await using var host = await CreateHostAsync(initial, path, dir);
            string revision = host.ProjectRevision;

            Task<ProjectUpdateResult> first = host.ApplyProjectAsync(
                new IgwProject { Name = "first" }, revision, CancellationToken.None);
            Task<ProjectUpdateResult> second = host.ApplyProjectAsync(
                new IgwProject { Name = "second" }, revision, CancellationToken.None);
            ProjectUpdateResult[] results = await Task.WhenAll(first, second);

            Assert.Single(results, result => result.Applied);
            Assert.Single(results, result => result.Conflict);
            string appliedName = results[0].Applied ? "first" : "second";
            Assert.Equal(appliedName, host.Project.Name);
            Assert.Equal(host.ProjectRevision, results.Single(result => result.Conflict).Revision);
        }
        finally { DeleteTree(dir); }
    }

    [Fact]
    public async Task Mes_update_builds_from_the_live_project_only_after_revision_is_accepted()
    {
        string dir = Path.Combine(Path.GetTempPath(), "igw-revision-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "project.igwproj");
        try
        {
            var initial = new IgwProject { Name = "initial" };
            ProjectStore.Save(initial, path);
            await using var host = await CreateHostAsync(initial, path, dir);
            string revision = host.ProjectRevision;

            ProjectUpdateResult full = await host.ApplyProjectAsync(
                new IgwProject { Name = "new-live-name" }, revision, CancellationToken.None);
            ProjectUpdateResult staleMes = await host.ApplyMesUploadAsync(
                new MesUploadSettings { Enabled = true }, revision, CancellationToken.None);

            Assert.True(full.Applied);
            Assert.True(staleMes.Conflict);
            Assert.Equal("new-live-name", host.Project.Name);
            Assert.False(host.Project.MesUpload.Enabled);
        }
        finally { DeleteTree(dir); }
    }

    private static async Task<GatewayHost> CreateHostAsync(IgwProject initial, string legacyPath, string directory)
    {
        ProjectBootstrapResult storage = await new ProjectDatabaseBootstrapper().OpenOrMigrateAsync(
            new ProjectStoragePaths(
                Path.Combine(directory, "project.db"),
                legacyPath,
                Path.Combine(directory, "igw.masterkey")),
            default);
        IgwProject project = await storage.Repository.LoadRuntimeProjectAsync(default);
        ProjectRevision revision = await storage.Repository.GetRevisionAsync(default);
        var registry = new DriverRegistry();
        registry.Register(new SimulatorDriverFactory());
        return new GatewayHost(
            project,
            storage.Repository,
            revision,
            registry,
            NullLoggerFactory.Instance,
            directory);
    }

    private static void DeleteTree(string directory)
    {
        foreach (string file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(directory, recursive: true);
    }
}
