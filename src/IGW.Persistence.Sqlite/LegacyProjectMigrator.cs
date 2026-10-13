using IGW.Abstractions;
using IGW.Core;
using Microsoft.Data.Sqlite;

namespace IGW.Persistence.Sqlite;

/// <summary>在隔离的 staging 数据库中写入并验证旧工程，绝不直接改最终数据库。</summary>
public sealed class LegacyProjectMigrator
{
    public async Task CreateVerifiedDatabaseAsync(
        string stagingDatabasePath,
        string masterKeyPath,
        IgwProject project,
        string? legacySourceHash,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(masterKeyPath);
        ArgumentNullException.ThrowIfNull(project);

        var database = new ProjectDatabase(stagingDatabasePath);
        await new ProjectSchemaMigrator(database).MigrateAsync(cancellationToken).ConfigureAwait(false);
        var vault = new ProjectSecretVault(new InstallationSecretProtector(masterKeyPath));
        var writer = new ProjectSnapshotWriter(database, vault);
        var reader = new ProjectSnapshotReader(database, vault);
        await writer.WriteAsync(project, new ProjectRevision(0), cancellationToken).ConfigureAwait(false);

        IgwProject verified = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        ProjectStore.Validate(verified);
        if (!SemanticallyEqual(project, verified))
            throw new InvalidDataException("SQLite staging 工程与旧工程往返结果不一致。");

        await using SqliteConnection connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var metadata = connection.CreateCommand())
        {
            metadata.CommandText = """
                UPDATE IGW_project_meta
                SET legacy_source_hash=$sourceHash, migration_completed_at=$completedAt
                WHERE singleton_id=1;
                """;
            metadata.Parameters.AddWithValue("$sourceHash", (object?)legacySourceHash ?? DBNull.Value);
            metadata.Parameters.AddWithValue("$completedAt", legacySourceHash is null
                ? DBNull.Value
                : DateTimeOffset.UtcNow.ToString("O"));
            await metadata.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var checkpoint = connection.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await checkpoint.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool SemanticallyEqual(IgwProject source, IgwProject restored)
    {
        IgwProject expected = ProjectStore.Clone(source);
        IgwProject actual = ProjectStore.Clone(restored);
        AlignGeneratedIds(expected, actual);
        AlignMigratedPasswords(expected, actual);
        return string.Equals(ProjectStore.ToJson(expected), ProjectStore.ToJson(actual), StringComparison.Ordinal);
    }

    private static void AlignGeneratedIds(IgwProject expected, IgwProject actual)
    {
        for (int index = 0; index < Math.Min(expected.Channels.Count, actual.Channels.Count); index++)
        {
            ChannelDef left = expected.Channels[index];
            ChannelDef right = actual.Channels[index];
            ClearGeneratedId(left.Id, value => right.Id = value);
            for (int deviceIndex = 0; deviceIndex < Math.Min(left.Devices.Count, right.Devices.Count); deviceIndex++)
            {
                DeviceDef leftDevice = left.Devices[deviceIndex];
                DeviceDef rightDevice = right.Devices[deviceIndex];
                ClearGeneratedId(leftDevice.Id, value => rightDevice.Id = value);
                AlignGroupIds(leftDevice.Groups, rightDevice.Groups);
                AlignTagIds(leftDevice.Tags, rightDevice.Tags);
            }
        }

        for (int index = 0; index < Math.Min(expected.Templates.Count, actual.Templates.Count); index++)
        {
            DeviceTemplate left = expected.Templates[index];
            DeviceTemplate right = actual.Templates[index];
            ClearGeneratedId(left.Id, value => right.Id = value);
            AlignGroupIds(left.Groups, right.Groups);
            AlignTagIds(left.Tags, right.Tags);
        }
        for (int index = 0; index < Math.Min(expected.MesUpload.Tasks.Count, actual.MesUpload.Tasks.Count); index++)
            ClearGeneratedId(expected.MesUpload.Tasks[index].Id, value => actual.MesUpload.Tasks[index].Id = value);
        for (int index = 0; index < Math.Min(expected.MesUpload.FlowTemplates.Count, actual.MesUpload.FlowTemplates.Count); index++)
            ClearGeneratedId(expected.MesUpload.FlowTemplates[index].Id,
                value => actual.MesUpload.FlowTemplates[index].Id = value);
    }

    private static void AlignGroupIds(IReadOnlyList<TagGroupDef> expected, IReadOnlyList<TagGroupDef> actual)
    {
        for (int index = 0; index < Math.Min(expected.Count, actual.Count); index++)
        {
            ClearGeneratedId(expected[index].Id, value => actual[index].Id = value);
            AlignTagIds(expected[index].Tags, actual[index].Tags);
            AlignGroupIds(expected[index].SubGroups, actual[index].SubGroups);
        }
    }

    private static void AlignTagIds(IReadOnlyList<TagDef> expected, IReadOnlyList<TagDef> actual)
    {
        for (int index = 0; index < Math.Min(expected.Count, actual.Count); index++)
            ClearGeneratedId(expected[index].Id, value => actual[index].Id = value);
    }

    private static void ClearGeneratedId(string expectedId, Action<string> setActual)
    {
        if (string.IsNullOrWhiteSpace(expectedId)) setActual(string.Empty);
    }

    private static void AlignMigratedPasswords(IgwProject expected, IgwProject actual)
    {
        for (int index = 0; index < Math.Min(expected.Security.Users.Count, actual.Security.Users.Count); index++)
        {
            UserDef left = expected.Security.Users[index];
            UserDef right = actual.Security.Users[index];
            if (string.IsNullOrWhiteSpace(left.Password) || !string.IsNullOrWhiteSpace(left.PasswordHash)) continue;
            left.Password = right.Password;
            left.PasswordHash = right.PasswordHash;
        }
    }
}
