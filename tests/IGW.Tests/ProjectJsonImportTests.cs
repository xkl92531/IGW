using IGW.Abstractions;
using IGW.Core;
using IGW.Drivers.Simulator.Kep;
using IGW.Persistence.Sqlite;
using IGW.ProjectExchange;
using Xunit;

namespace IGW.Tests;

public sealed class ProjectJsonImportTests
{
    [Fact]
    public async Task Pure_kep_import_assigns_stable_ids_and_replaces_the_project_once()
    {
        await using ImportFixture fixture = await ImportFixture.CreateAsync();
        string fixtureJson = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Kep", "common-nested-project.json"));
        byte[] json = System.Text.Encoding.UTF8.GetBytes(
            fixtureJson.Replace("\"servermain.TAG_DATA_TYPE\": 21", "\"servermain.TAG_DATA_TYPE\": 4", StringComparison.Ordinal));

        ProjectImportPreview preview = await fixture.Service.InspectAsync(new MemoryStream(json), default);
        ProjectImportResult result = await fixture.Service.ImportAsync(
            new MemoryStream(json), new ProjectImportOptions(preview.Token, preview.FileHash, null), default);

        Assert.True(result.Applied);
        Assert.Equal(2, result.Revision.Value);
        IgwProject imported = await fixture.Repository.LoadRuntimeProjectAsync(default);
        Assert.Equal("Factory", imported.Name);
        Assert.Single(imported.Channels);
        Assert.All(imported.Channels.SelectMany(channel => channel.Devices), device =>
            Assert.False(string.IsNullOrWhiteSpace(device.Id)));
        Assert.Equal(2, CountTags(imported));
    }

    [Fact]
    public async Task Igw_round_trip_preserves_extensions_and_requires_the_correct_secret_password()
    {
        await using ImportFixture source = await ImportFixture.CreateAsync();
        IgwProject expected = ProjectStorageSmokePerformanceTests.BuildProject(8);
        expected.Name = "可迁移工程";
        expected.MqttPublish.Password = "portable-secret-marker";
        expected.Channels[0].Devices[0].Groups[0].Tags[0].Description = "完整说明";
        expected.Channels[0].Devices[0].Groups[0].Tags[0].WriteConfirmation =
            new WriteConfirmationDef { Mode = WriteConfirmationMode.ReadBack };
        await source.Repository.ReplaceFromSnapshotAsync(expected, new ProjectRevision(1), default);
        using var exported = new MemoryStream();
        await source.Service.ExportAsync(exported, new ProjectExportOptions(true, "correct-password"), default);
        byte[] json = exported.ToArray();

        await using ImportFixture target = await ImportFixture.CreateAsync();
        ProjectRevision before = await target.Repository.GetRevisionAsync(default);
        ProjectImportPreview preview = await target.Service.InspectAsync(new MemoryStream(json), default);
        ProjectImportResult rejected = await target.Service.ImportAsync(
            new MemoryStream(json), new ProjectImportOptions(preview.Token, preview.FileHash, "wrong-password"), default);

        Assert.False(rejected.Applied);
        Assert.Equal("portable_secret_authentication_failed", rejected.ErrorCode);
        Assert.Equal(before, await target.Repository.GetRevisionAsync(default));

        ProjectImportResult imported = await target.Service.ImportAsync(
            new MemoryStream(json), new ProjectImportOptions(preview.Token, preview.FileHash, "correct-password"), default);
        Assert.True(imported.Applied);
        IgwProject actual = await target.Repository.LoadRuntimeProjectAsync(default);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Channels[0].Id, actual.Channels[0].Id);
        Assert.Equal("完整说明", actual.Channels[0].Devices[0].Groups[0].Tags[0].Description);
        Assert.Equal("portable-secret-marker", actual.MqttPublish.Password);
        Assert.Equal(8, CountTags(actual));
    }

    [Fact]
    public async Task Preview_token_is_bound_to_the_exact_file_and_mismatch_never_changes_revision()
    {
        await using ImportFixture fixture = await ImportFixture.CreateAsync();
        byte[] fileA = await ExportAsync(fixture, ProjectStorageSmokePerformanceTests.BuildProject(2));
        byte[] fileB = await ExportAsync(fixture, ProjectStorageSmokePerformanceTests.BuildProject(3));
        ProjectImportPreview preview = await fixture.Service.InspectAsync(new MemoryStream(fileA), default);
        ProjectRevision before = await fixture.Repository.GetRevisionAsync(default);

        ProjectImportResult result = await fixture.Service.ImportAsync(
            new MemoryStream(fileB), new ProjectImportOptions(preview.Token, preview.FileHash, null), default);

        Assert.False(result.Applied);
        Assert.Equal("import_file_changed", result.ErrorCode);
        Assert.Equal(before, await fixture.Repository.GetRevisionAsync(default));
    }

    private static async Task<byte[]> ExportAsync(ImportFixture fixture, IgwProject project)
    {
        ProjectRevision revision = await fixture.Repository.GetRevisionAsync(default);
        await fixture.Repository.ReplaceFromSnapshotAsync(project, revision, default);
        using var output = new MemoryStream();
        await fixture.Service.ExportAsync(output, new ProjectExportOptions(false, null), default);
        return output.ToArray();
    }

    private static int CountTags(IgwProject project) => project.Channels.Sum(channel =>
        channel.Devices.Sum(device => device.Tags.Count + CountGroups(device.Groups)));

    private static int CountGroups(IEnumerable<TagGroupDef> groups) => groups.Sum(group =>
        group.Tags.Count + CountGroups(group.SubGroups));

    private sealed class ImportFixture : IAsyncDisposable
    {
        private readonly string _directory;
        public SqliteProjectRepository Repository { get; }
        public ProjectJsonImporter Service { get; }

        private ImportFixture(string directory, SqliteProjectRepository repository, ProjectJsonImporter service)
        {
            _directory = directory;
            Repository = repository;
            Service = service;
        }

        public static async Task<ImportFixture> CreateAsync()
        {
            string directory = Path.Combine(Path.GetTempPath(), "igw-project-import", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var repository = new SqliteProjectRepository(
                new ProjectDatabase(Path.Combine(directory, "project.db")),
                new ProjectSecretVault(new InstallationSecretProtector(Path.Combine(directory, "igw.masterkey"))));
            await repository.InitializeAsync(default);
            await repository.ReplaceFromSnapshotAsync(
                ProjectStorageSmokePerformanceTests.BuildProject(1), new ProjectRevision(0), default);
            var codecs = new IKepDriverCodec[] { new SimulatorKepDriverCodec() };
            var sessions = new ProjectImportSessionStore(Path.Combine(directory, "sessions"));
            return new ImportFixture(directory, repository,
                new ProjectJsonImporter(repository, codecs, sessions));
        }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            await Repository.DisposeAsync();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
