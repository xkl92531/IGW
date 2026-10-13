using System.Reflection;
using System.Text;
using IGW.Core;
using Microsoft.Data.Sqlite;

namespace IGW.Persistence.Sqlite;

internal sealed record ProjectSchemaMigration(int Version, string Sql);

/// <summary>按版本顺序、逐个事务执行工程数据库迁移。</summary>
public sealed class ProjectSchemaMigrator
{
    private static readonly string[] OwnedTables =
    ["project_meta", "channels", "devices", "tag_groups", "tags", "tag_options", "project_sections",
        "mes_tasks", "mes_task_index", "flow_templates", "device_templates", "secret_entries", "schema_history"];
    private static readonly IReadOnlyDictionary<string, IReadOnlyCollection<string>> OwnershipSignature =
        new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["project_meta"] = ["singleton_id", "project_id", "name", "description", "schema_version", "revision",
                "runtime_generation", "legacy_source_hash", "migration_completed_at", "created_at", "updated_at"]
        };
    private readonly ProjectDatabase _database;
    private readonly IReadOnlyList<ProjectSchemaMigration> _migrations;

    public ProjectSchemaMigrator(ProjectDatabase database)
        : this(database, LoadEmbeddedMigrations())
    {
    }

    internal ProjectSchemaMigrator(
        ProjectDatabase database,
        IReadOnlyList<ProjectSchemaMigration> migrations)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _migrations = migrations.OrderBy(item => item.Version).ToArray();
        if (_migrations.Select(item => item.Version).Distinct().Count() != _migrations.Count)
            throw new ArgumentException("工程数据库迁移版本不能重复。", nameof(migrations));
    }

    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        // Older initialization committed its empty history table before migration 001. Accept
        // only that exact, pristine interrupted state; applied/foreign schemas still need the marker.
        bool interruptedInitialization = await IsPristineHistoryOnlyAsync(connection, cancellationToken).ConfigureAwait(false);
        IgwSqliteTableNamespace.Ensure(connection, OwnedTables, OwnershipSignature,
            requireSignatureTables: !interruptedInitialization);
        HashSet<int> applied = await ReadAppliedVersionsAsync(connection, cancellationToken).ConfigureAwait(false);

        foreach (ProjectSchemaMigration migration in _migrations)
        {
            if (applied.Contains(migration.Version)) continue;
            await ApplyAsync(connection, migration, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task EnsureHistoryTableAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS IGW_schema_history(
                version INTEGER PRIMARY KEY,
                applied_at TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<HashSet<int>> ReadAppliedVersionsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='IGW_schema_history';";
        if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null) return [];
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM IGW_schema_history;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var versions = new HashSet<int>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            versions.Add(reader.GetInt32(0));
        return versions;
    }

    private static async Task ApplyAsync(
        SqliteConnection connection,
        ProjectSchemaMigration migration,
        CancellationToken cancellationToken)
    {
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // History creation and the first migration share one commit, so failure/cancellation
            // cannot leave a new history-only database that obstructs the next startup.
            await EnsureHistoryTableAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                // Historical resources remain immutable. Rewrite only SQL identifiers, never
                // literal values, parameters or comments, when executing their DDL.
                command.CommandText = NormalizeHistoricalSql(migration.Sql);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var record = connection.CreateCommand())
            {
                record.Transaction = transaction;
                record.CommandText = "INSERT INTO IGW_schema_history(version, applied_at) VALUES($version, $at);";
                record.Parameters.AddWithValue("$version", migration.Version);
                record.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
                await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<bool> IsPristineHistoryOnlyAsync(SqliteConnection connection, CancellationToken ct)
    {
        string? history = null;
        await using (var inventory = connection.CreateCommand())
        {
            inventory.CommandText = "SELECT name,type FROM sqlite_master WHERE lower(substr(name,1,7)) <> 'sqlite_';";
            await using var reader = await inventory.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.GetString(1) != "table") return false;
            history = reader.GetString(0);
            if (!(history.Equals("schema_history", StringComparison.OrdinalIgnoreCase)
                  || history.Equals("IGW_schema_history", StringComparison.OrdinalIgnoreCase))
                || await reader.ReadAsync(ct).ConfigureAwait(false)) return false;
        }
        await using (var columns = connection.CreateCommand())
        {
            columns.CommandText = $"PRAGMA table_info(\"{history}\");";
            await using var reader = await columns.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)
                || reader.GetString(1) != "version" || !reader.GetString(2).Equals("INTEGER", StringComparison.OrdinalIgnoreCase)
                || reader.GetInt32(3) != 0 || reader.GetInt32(5) != 1 || !reader.IsDBNull(4)) return false;
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)
                || reader.GetString(1) != "applied_at" || !reader.GetString(2).Equals("TEXT", StringComparison.OrdinalIgnoreCase)
                || reader.GetInt32(3) != 1 || reader.GetInt32(5) != 0 || !reader.IsDBNull(4)
                || await reader.ReadAsync(ct).ConfigureAwait(false)) return false;
        }
        await using var empty = connection.CreateCommand();
        empty.CommandText = $"SELECT 1 FROM \"{history}\" LIMIT 1;";
        return await empty.ExecuteScalarAsync(ct).ConfigureAwait(false) is null;
    }

    private static string NormalizeHistoricalSql(string sql)
    {
        var names = OwnedTables.ToDictionary(name => name, IgwSqliteTableNamespace.PhysicalName, StringComparer.OrdinalIgnoreCase);
        var result = new StringBuilder(sql.Length + 256);
        for (int position = 0; position < sql.Length;)
        {
            int start = position;
            char character = sql[position];
            if (character == '\'' || character is '"' or '`' or '[')
            {
                char closing = character == '[' ? ']' : character;
                position++;
                while (position < sql.Length)
                {
                    if (sql[position++] != closing) continue;
                    if (position < sql.Length && sql[position] == closing) { position++; continue; }
                    break;
                }
                string token = sql[start..position];
                string identifier = token.Length >= 2 ? token[1..^1] : "";
                if (character != '\'' && names.TryGetValue(identifier, out string? physical))
                    result.Append(character).Append(physical).Append(closing);
                else result.Append(token);
            }
            else if (character == '-' && position + 1 < sql.Length && sql[position + 1] == '-')
            {
                while (position < sql.Length && sql[position] != '\n') position++;
                result.Append(sql[start..position]);
            }
            else if (character == '/' && position + 1 < sql.Length && sql[position + 1] == '*')
            {
                int end = sql.IndexOf("*/", position + 2, StringComparison.Ordinal);
                position = end < 0 ? sql.Length : end + 2;
                result.Append(sql[start..position]);
            }
            else if (char.IsAsciiLetter(character) || character == '_' || character is '$' or '@' or ':')
            {
                position++;
                while (position < sql.Length && (char.IsAsciiLetterOrDigit(sql[position]) || sql[position] == '_')) position++;
                string token = sql[start..position];
                result.Append(names.TryGetValue(token, out string? physical) ? physical : token);
            }
            else { result.Append(character); position++; }
        }
        return result.ToString();
    }

    private static IReadOnlyList<ProjectSchemaMigration> LoadEmbeddedMigrations()
    {
        Assembly assembly = typeof(ProjectSchemaMigrator).Assembly;
        const string prefix = "IGW.Persistence.Sqlite.Schema.";
        const string suffix = ".sql";
        var migrations = new List<ProjectSchemaMigration>();
        foreach (string resourceName in assembly.GetManifestResourceNames()
                     .Where(name => name.StartsWith(prefix, StringComparison.Ordinal)
                         && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(name => name, StringComparer.Ordinal))
        {
            string fileName = resourceName[prefix.Length..^suffix.Length];
            int separator = fileName.IndexOf('_');
            if (separator <= 0 || !int.TryParse(fileName[..separator], out int version))
                throw new InvalidOperationException($"工程数据库迁移资源名称无效:{resourceName}");
            using Stream stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"缺少工程数据库迁移资源:{resourceName}");
            using var reader = new StreamReader(stream);
            migrations.Add(new ProjectSchemaMigration(version, reader.ReadToEnd()));
        }
        if (migrations.Count == 0) throw new InvalidOperationException("没有找到工程数据库迁移资源。");
        return migrations;
    }
}
