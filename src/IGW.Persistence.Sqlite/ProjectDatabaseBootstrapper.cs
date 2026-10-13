using System.Security.Cryptography;
using IGW.Abstractions;
using IGW.Core;

namespace IGW.Persistence.Sqlite;

public enum ProjectStorageSource
{
    ExistingDatabase,
    MigratedLegacy,
    InitialProject,
}

public sealed record ProjectStoragePaths(
    string DatabasePath,
    string LegacyProjectPath,
    string MasterKeyPath);

public sealed record ProjectBootstrapResult(
    IProjectRepository Repository,
    ProjectStorageSource Source,
    string? LegacyBackupPath);

/// <summary>选择现有 SQLite，或以锁保护的 staging 数据库原子迁移旧 JSON 工程。</summary>
public sealed class ProjectDatabaseBootstrapper
{
    private readonly LegacyProjectMigrator _migrator;

    public ProjectDatabaseBootstrapper(LegacyProjectMigrator? migrator = null) =>
        _migrator = migrator ?? new LegacyProjectMigrator();

    public async Task<ProjectBootstrapResult> OpenOrMigrateAsync(
        ProjectStoragePaths paths,
        CancellationToken cancellationToken)
    {
        ProjectStoragePaths normalized = Normalize(paths);
        Directory.CreateDirectory(Path.GetDirectoryName(normalized.DatabasePath)!);
        if (File.Exists(normalized.DatabasePath))
            return await OpenExistingAsync(normalized, cancellationToken).ConfigureAwait(false);

        string stagingPath = normalized.DatabasePath + ".migrating";
        string lockPath = stagingPath + ".lock";
        FileStream migrationLock;
        try
        {
            migrationLock = new FileStream(
                lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                bufferSize: 1, FileOptions.WriteThrough);
        }
        catch (IOException ex)
        {
            throw new IOException("另一个 IGW 实例正在迁移工程数据库，请等待其完成。", ex);
        }

        await using (migrationLock.ConfigureAwait(false))
        {
            try
            {
                if (File.Exists(normalized.DatabasePath))
                    return await OpenExistingAsync(normalized, cancellationToken).ConfigureAwait(false);

                DeleteDatabaseFiles(stagingPath);
                bool hasLegacy = File.Exists(normalized.LegacyProjectPath);
                IgwProject project;
                string? sourceHash = null;
                if (hasLegacy)
                {
                    sourceHash = await HashFileAsync(normalized.LegacyProjectPath, cancellationToken).ConfigureAwait(false);
                    string legacyMasterKeyPath = Path.Combine(
                        Path.GetDirectoryName(normalized.LegacyProjectPath)!, "igw.masterkey");
                    project = ProjectStore.Load(normalized.LegacyProjectPath, legacyMasterKeyPath);
                }
                else
                {
                    project = new IgwProject { Name = "项目" };
                    ProjectStore.Validate(project);
                }

                await _migrator.CreateVerifiedDatabaseAsync(
                    stagingPath, normalized.MasterKeyPath, project, sourceHash, cancellationToken).ConfigureAwait(false);
                DeleteSidecars(stagingPath);
                File.Move(stagingPath, normalized.DatabasePath);

                string? backupPath = null;
                ProjectStorageSource source;
                if (hasLegacy)
                {
                    backupPath = CreateBackupPath(normalized.LegacyProjectPath);
                    File.Move(normalized.LegacyProjectPath, backupPath);
                    File.SetAttributes(backupPath, File.GetAttributes(backupPath) | FileAttributes.ReadOnly);
                    source = ProjectStorageSource.MigratedLegacy;
                }
                else
                {
                    source = ProjectStorageSource.InitialProject;
                }

                SqliteProjectRepository repository = CreateRepository(normalized);
                await repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
                return new ProjectBootstrapResult(repository, source, backupPath);
            }
            catch
            {
                if (!File.Exists(normalized.DatabasePath)) DeleteDatabaseFiles(stagingPath);
                throw;
            }
            finally
            {
                migrationLock.Close();
                TryDelete(lockPath);
            }
        }
    }

    private static async Task<ProjectBootstrapResult> OpenExistingAsync(
        ProjectStoragePaths paths,
        CancellationToken cancellationToken)
    {
        SqliteProjectRepository repository = CreateRepository(paths);
        try
        {
            await repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return new ProjectBootstrapResult(repository, ProjectStorageSource.ExistingDatabase, null);
        }
        catch
        {
            await repository.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static SqliteProjectRepository CreateRepository(ProjectStoragePaths paths) =>
        new(
            new ProjectDatabase(paths.DatabasePath),
            new ProjectSecretVault(new InstallationSecretProtector(paths.MasterKeyPath)));

    private static ProjectStoragePaths Normalize(ProjectStoragePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return new ProjectStoragePaths(
            Path.GetFullPath(paths.DatabasePath),
            Path.GetFullPath(paths.LegacyProjectPath),
            Path.GetFullPath(paths.MasterKeyPath));
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string CreateBackupPath(string legacyPath)
    {
        string directory = Path.GetDirectoryName(legacyPath)!;
        string name = Path.GetFileName(legacyPath);
        string timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ");
        string candidate = Path.Combine(directory, $"{name}.migrated-{timestamp}.bak");
        for (int suffix = 2; File.Exists(candidate); suffix++)
            candidate = Path.Combine(directory, $"{name}.migrated-{timestamp}-{suffix}.bak");
        return candidate;
    }

    private static void DeleteDatabaseFiles(string databasePath)
    {
        TryDelete(databasePath);
        DeleteSidecars(databasePath);
    }

    private static void DeleteSidecars(string databasePath)
    {
        TryDelete(databasePath + "-wal");
        TryDelete(databasePath + "-shm");
    }

    private static void TryDelete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
