using Microsoft.Data.Sqlite;

namespace IGW.Core;

/// <summary>
/// Web UI preferences stored outside the project file. The SQLite file lives in the IGW data directory,
/// so browser refreshes and application upgrades do not reset operator preferences.
/// </summary>
public sealed class UiPreferenceStore : IDisposable
{
    public const string DefaultLocale = "zh-CN";
    public const int DefaultProjectAutoSaveSeconds = 30;
    public const int MinProjectAutoSaveSeconds = 5;
    public const int MaxProjectAutoSaveSeconds = 3600;

    private readonly object _sync = new();
    private readonly SqliteConnection _connection;

    public UiPreferenceStore(string dbFile)
    {
        if (string.IsNullOrWhiteSpace(dbFile)) throw new ArgumentException("Database path is required.", nameof(dbFile));
        string fullPath = Path.GetFullPath(dbFile);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connection = new SqliteConnection($"Data Source={fullPath}");
        _connection.Open();
        try
        {
            IgwSqliteTableNamespace.Ensure(_connection, ["ui_preferences"], new Dictionary<string, IReadOnlyCollection<string>>
            {
                ["ui_preferences"] = ["key", "value", "updated_utc"]
            });
        }
        catch { _connection.Dispose(); throw; }
        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA busy_timeout=5000;");
        Execute("CREATE TABLE IF NOT EXISTS IGW_ui_preferences(key TEXT PRIMARY KEY, value TEXT NOT NULL, updated_utc TEXT NOT NULL);");
    }

    public string GetLocale()
    {
        lock (_sync)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT value FROM IGW_ui_preferences WHERE key = 'locale' LIMIT 1;";
            string? value = command.ExecuteScalar() as string;
            return IsSupportedLocale(value) ? value! : DefaultLocale;
        }
    }

    public void SetLocale(string locale)
    {
        if (!IsSupportedLocale(locale))
            throw new ArgumentException("Locale must be zh-CN or en-US.", nameof(locale));

        lock (_sync)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO IGW_ui_preferences(key, value, updated_utc)
                VALUES('locale', $value, $updated)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_utc = excluded.updated_utc;
                """;
            command.Parameters.AddWithValue("$value", locale);
            command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
    }

    public static bool IsSupportedLocale(string? locale) => locale is "zh-CN" or "en-US";

    /// <summary>工程配置页有修改时是否按周期后台自动保存(默认开启)。</summary>
    public bool GetProjectAutoSave() => Read("projectAutoSave") is not "false";

    public int GetProjectAutoSaveSeconds() =>
        int.TryParse(Read("projectAutoSaveSeconds"), out int seconds)
        && seconds is >= MinProjectAutoSaveSeconds and <= MaxProjectAutoSaveSeconds
            ? seconds
            : DefaultProjectAutoSaveSeconds;

    public void SetProjectAutoSave(bool enabled, int seconds)
    {
        if (seconds is < MinProjectAutoSaveSeconds or > MaxProjectAutoSaveSeconds)
            throw new ArgumentException($"自动保存间隔应在 {MinProjectAutoSaveSeconds}–{MaxProjectAutoSaveSeconds} 秒之间。", nameof(seconds));
        lock (_sync)
        {
            Write("projectAutoSave", enabled ? "true" : "false");
            Write("projectAutoSaveSeconds", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private string? Read(string key)
    {
        lock (_sync)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT value FROM IGW_ui_preferences WHERE key = $key LIMIT 1;";
            command.Parameters.AddWithValue("$key", key);
            return command.ExecuteScalar() as string;
        }
    }

    private void Write(string key, string value)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO IGW_ui_preferences(key, value, updated_utc)
            VALUES($key, $value, $updated)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        lock (_sync) _connection.Dispose();
    }
}
