using IGW.Abstractions;
using IGW.Core;
using IGW.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IGW.Tests;

public sealed class LegacyProjectMigrationTests
{
    [Fact]
    public async Task No_existing_files_creates_an_initial_sqlite_project()
    {
        await using var fixture = MigrationFixture.Create();

        ProjectBootstrapResult result = await fixture.Bootstrapper.OpenOrMigrateAsync(fixture.Paths, default);

        Assert.Equal(ProjectStorageSource.InitialProject, result.Source);
        Assert.Null(result.LegacyBackupPath);
        Assert.True(File.Exists(fixture.Paths.DatabasePath));
        Assert.Equal("项目", (await result.Repository.LoadRuntimeProjectAsync(default)).Name);
        await result.Repository.DisposeAsync();
    }

    [Fact]
    public async Task Legacy_only_is_imported_verified_backed_up_and_marked_read_only()
    {
        await using var fixture = MigrationFixture.Create();
        ProjectStore.Save(new IgwProject
        {
            Name = "legacy-project",
            Description = "旧工程",
            Channels =
            [
                new ChannelDef
                {
                    Name = "LegacyChannel",
                    DriverId = "Simulator",
                    Devices =
                    [
                        new DeviceDef
                        {
                            Name = "LegacyDevice",
                            Groups =
                            [
                                new TagGroupDef
                                {
                                    Name = "LegacyGroup",
                                    Tags = [new TagDef { Name = "LegacyTag", Address = "D0" }],
                                },
                            ],
                        },
                    ],
                },
            ],
        }, fixture.Paths.LegacyProjectPath);

        ProjectBootstrapResult result = await fixture.Bootstrapper.OpenOrMigrateAsync(fixture.Paths, default);

        Assert.Equal(ProjectStorageSource.MigratedLegacy, result.Source);
        Assert.NotNull(result.LegacyBackupPath);
        Assert.True(File.Exists(result.LegacyBackupPath));
        Assert.True((File.GetAttributes(result.LegacyBackupPath!) & FileAttributes.ReadOnly) != 0);
        Assert.False(File.Exists(fixture.Paths.LegacyProjectPath));
        IgwProject restored = await result.Repository.LoadRuntimeProjectAsync(default);
        Assert.Equal("legacy-project", restored.Name);
        Assert.False(string.IsNullOrWhiteSpace(restored.Channels.Single().Id));
        Assert.False(string.IsNullOrWhiteSpace(restored.Channels.Single().Devices.Single().Groups.Single().Tags.Single().Id));

        await using SqliteConnection connection = await new ProjectDatabase(fixture.Paths.DatabasePath).OpenAsync(default);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT legacy_source_hash,migration_completed_at FROM IGW_project_meta WHERE singleton_id=1;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.False(reader.IsDBNull(0));
        Assert.Equal(64, reader.GetString(0).Length);
        Assert.False(reader.IsDBNull(1));
        await result.Repository.DisposeAsync();
    }

    [Fact]
    public async Task Existing_database_always_wins_and_legacy_file_is_not_touched()
    {
        await using var fixture = MigrationFixture.Create();
        ProjectBootstrapResult initial = await fixture.Bootstrapper.OpenOrMigrateAsync(fixture.Paths, default);
        await initial.Repository.DisposeAsync();
        ProjectStore.Save(new IgwProject { Name = "must-not-import" }, fixture.Paths.LegacyProjectPath);

        ProjectBootstrapResult reopened = await fixture.Bootstrapper.OpenOrMigrateAsync(fixture.Paths, default);

        Assert.Equal(ProjectStorageSource.ExistingDatabase, reopened.Source);
        Assert.Null(reopened.LegacyBackupPath);
        Assert.True(File.Exists(fixture.Paths.LegacyProjectPath));
        Assert.Equal("项目", (await reopened.Repository.LoadRuntimeProjectAsync(default)).Name);
        await reopened.Repository.DisposeAsync();
    }

    [Fact]
    public async Task Invalid_legacy_leaves_source_untouched_and_no_partial_database()
    {
        await using var fixture = MigrationFixture.Create();
        await File.WriteAllTextAsync(fixture.Paths.LegacyProjectPath, "{ invalid json");

        await Assert.ThrowsAnyAsync<Exception>(() =>
            fixture.Bootstrapper.OpenOrMigrateAsync(fixture.Paths, default));

        Assert.True(File.Exists(fixture.Paths.LegacyProjectPath));
        Assert.False(File.Exists(fixture.Paths.DatabasePath));
        Assert.False(File.Exists(fixture.Paths.DatabasePath + ".migrating"));
        Assert.Empty(Directory.GetFiles(fixture.Directory, "*.migrated-*.bak"));
    }

    [Fact]
    public async Task Stale_staging_is_replaced_only_when_its_owner_lock_is_not_held()
    {
        await using var fixture = MigrationFixture.Create();
        string staging = fixture.Paths.DatabasePath + ".migrating";
        string lockPath = staging + ".lock";
        await File.WriteAllTextAsync(staging, "stale-partial-database");
        await File.WriteAllTextAsync(lockPath, "stale-lock-file");

        ProjectBootstrapResult result = await fixture.Bootstrapper.OpenOrMigrateAsync(fixture.Paths, default);

        Assert.Equal(ProjectStorageSource.InitialProject, result.Source);
        Assert.True(File.Exists(fixture.Paths.DatabasePath));
        Assert.False(File.Exists(staging));
        await result.Repository.DisposeAsync();
    }

    [Fact]
    public async Task Active_migration_lock_prevents_deleting_another_process_staging_database()
    {
        await using var fixture = MigrationFixture.Create();
        string staging = fixture.Paths.DatabasePath + ".migrating";
        string lockPath = staging + ".lock";
        await File.WriteAllTextAsync(staging, "owned-partial-database");
        await using var owner = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        IOException error = await Assert.ThrowsAsync<IOException>(() =>
            fixture.Bootstrapper.OpenOrMigrateAsync(fixture.Paths, default));

        Assert.Contains("另一个 IGW 实例", error.Message);
        Assert.Equal("owned-partial-database", await File.ReadAllTextAsync(staging));
        Assert.False(File.Exists(fixture.Paths.DatabasePath));
    }

    private sealed class MigrationFixture : IAsyncDisposable
    {
        public string Directory { get; }
        public ProjectStoragePaths Paths { get; }
        public ProjectDatabaseBootstrapper Bootstrapper { get; } = new();

        private MigrationFixture(string directory)
        {
            Directory = directory;
            Paths = new ProjectStoragePaths(
                Path.Combine(directory, "project.db"),
                Path.Combine(directory, "project.igwproj"),
                Path.Combine(directory, "igw.masterkey"));
        }

        public static MigrationFixture Create()
        {
            string directory = Path.Combine(Path.GetTempPath(), "igw-migration-tests", Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            return new MigrationFixture(directory);
        }

        public ValueTask DisposeAsync()
        {
            foreach (string file in System.IO.Directory.GetFiles(Directory, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            System.IO.Directory.Delete(Directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
