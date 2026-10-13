using System.Text.Json;
using System.Text.Json.Serialization;
using IGW.Core;
using SqlSugar;

namespace IGW.Persistence.Replication;

/// <summary>用户在安装时选择的数据存储。SQLite 为默认，其余为主库(本机 SQLite 作为工作库和断线缓存)。</summary>
public enum DatabaseKind
{
    Sqlite,
    SqlServer,
    PostgreSql,
    MySql,
}

public sealed record DatabaseSettings(
    DatabaseKind Kind,
    string Host = "",
    int Port = 0,
    string Database = "igw",
    string Username = "",
    string Password = "")
{
    public const string DefaultDatabase = "igw";

    public static int DefaultPort(DatabaseKind kind) => kind switch
    {
        DatabaseKind.SqlServer => 1433,
        DatabaseKind.PostgreSql => 5432,
        DatabaseKind.MySql => 3306,
        _ => 0,
    };

    public static string DisplayName(DatabaseKind kind) => kind switch
    {
        DatabaseKind.SqlServer => "SQL Server",
        DatabaseKind.PostgreSql => "PostgreSQL",
        DatabaseKind.MySql => "MySQL",
        _ => "SQLite",
    };

    [JsonIgnore]
    public bool IsExternal => Kind != DatabaseKind.Sqlite;

    [JsonIgnore]
    public int EffectivePort => Port > 0 ? Port : DefaultPort(Kind);

    [JsonIgnore]
    public DbType SugarType => Kind switch
    {
        DatabaseKind.SqlServer => DbType.SqlServer,
        DatabaseKind.PostgreSql => DbType.PostgreSQL,
        DatabaseKind.MySql => DbType.MySqlConnector,
        _ => DbType.Sqlite,
    };

    /// <summary>连接字符串；<paramref name="serverOnly"/> 为 true 时连到服务器的系统库(用于建库)。用驱动自带的构造器转义特殊字符。</summary>
    public string ConnectionString(bool serverOnly = false)
    {
        string database = serverOnly ? SystemDatabase : Database;
        return Kind switch
        {
            DatabaseKind.SqlServer => new Microsoft.Data.SqlClient.SqlConnectionStringBuilder
            {
                DataSource = $"{Host},{EffectivePort}",
                InitialCatalog = database,
                UserID = Username,
                Password = Password,
                TrustServerCertificate = true,
                Encrypt = false,
                ConnectTimeout = 8,
                ApplicationName = "IGW",
                // 断网恢复后立即能重连：默认连接失败后连接池会“阻塞”数秒到数分钟，期间一直报旧错误。
                PoolBlockingPeriod = Microsoft.Data.SqlClient.PoolBlockingPeriod.NeverBlock,
            }.ConnectionString,
            DatabaseKind.PostgreSql => new Npgsql.NpgsqlConnectionStringBuilder
            {
                Host = Host,
                Port = EffectivePort,
                Database = database,
                Username = Username,
                Password = Password,
                Timeout = 8,
                ApplicationName = "IGW",
            }.ConnectionString,
            DatabaseKind.MySql => new MySqlConnector.MySqlConnectionStringBuilder
            {
                Server = Host,
                Port = (uint)EffectivePort,
                Database = database,
                UserID = Username,
                Password = Password,
                ConnectionTimeout = 8,
                AllowPublicKeyRetrieval = true,
                SslMode = MySqlConnector.MySqlSslMode.Preferred,
                CharacterSet = "utf8mb4",
            }.ConnectionString,
            _ => throw new InvalidOperationException("SQLite 不需要远端连接字符串。"),
        };
    }

    private string SystemDatabase => Kind switch
    {
        DatabaseKind.SqlServer => "master",
        DatabaseKind.PostgreSql => "postgres",
        DatabaseKind.MySql => "mysql",
        _ => Database,
    };

    /// <summary>数据库名只允许字母、数字和下划线，避免拼进建库语句时出问题。</summary>
    public string? Validate()
    {
        if (!IsExternal) return null;
        if (string.IsNullOrWhiteSpace(Host)) return "请填写数据库地址。";
        if (EffectivePort is < 1 or > 65535) return "端口必须在 1–65535 之间。";
        if (string.IsNullOrWhiteSpace(Database) || !Database.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')
            || char.IsAsciiDigit(Database[0]))
            return "数据库名只能包含字母、数字和下划线，且不能以数字开头。";
        if (string.IsNullOrWhiteSpace(Username)) return "请填写数据库账号。";
        return null;
    }

    [JsonIgnore]
    public bool IsLocalHost =>
        Host.Trim().ToLowerInvariant() is "localhost" or "127.0.0.1" or "::1" or "." or "(local)"
        || string.Equals(Host.Trim(), Environment.MachineName, StringComparison.OrdinalIgnoreCase);

    public override string ToString() =>
        IsExternal ? $"{DisplayName(Kind)} {Host}:{EffectivePort}/{Database}" : "SQLite(本机)";
}

/// <summary>数据目录下的 database.json：密码用本机安装密钥加密保存，不以明文落盘。</summary>
public static class DatabaseSettingsFile
{
    public const string FileName = "database.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string PathIn(string dataDir) => Path.Combine(dataDir, FileName);

    /// <summary>没有配置文件时视为 SQLite(老版本升级和默认安装)。</summary>
    public static DatabaseSettings Load(string dataDir, InstallationSecretProtector protector)
    {
        string path = PathIn(dataDir);
        if (!File.Exists(path)) return new DatabaseSettings(DatabaseKind.Sqlite);
        var stored = JsonSerializer.Deserialize<DatabaseSettings>(File.ReadAllText(path), Options)
            ?? new DatabaseSettings(DatabaseKind.Sqlite);
        return stored with
        {
            Password = protector.IsProtected(stored.Password) ? protector.Unprotect(stored.Password) : stored.Password,
        };
    }

    public static void Save(string dataDir, DatabaseSettings settings, InstallationSecretProtector protector)
    {
        Directory.CreateDirectory(dataDir);
        var stored = settings with
        {
            Password = string.IsNullOrEmpty(settings.Password) ? "" : protector.Protect(settings.Password),
        };
        string path = PathIn(dataDir);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(stored, Options));
        File.Move(temp, path, overwrite: true);
    }
}
