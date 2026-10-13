using System.Data.Common;
using System.Net.Sockets;
using SqlSugar;

namespace IGW.Persistence.Replication;

public enum DatabaseTestStatus
{
    Ok,
    InvalidSettings,
    /// <summary>选择了本机，但本机没有安装该类型的数据库服务。</summary>
    LocalServerNotInstalled,
    /// <summary>地址或端口连不上(服务没启动、防火墙、地址填错)。</summary>
    Unreachable,
    /// <summary>账号或密码错误，或没有登录权限。</summary>
    AuthenticationFailed,
    /// <summary>能登录，但没有建库/建表权限。</summary>
    PermissionDenied,
    Failed,
}

public sealed record DatabaseTestResult(DatabaseTestStatus Status, string Message, string? ServerVersion = null)
{
    public bool Ok => Status == DatabaseTestStatus.Ok;
}

/// <summary>安装向导和网关共用：测试连接、创建数据库。</summary>
public static class DatabaseConnector
{
    public static SqlSugarClient CreateClient(DatabaseSettings settings, bool serverOnly = false) => new(new ConnectionConfig
    {
        DbType = settings.SugarType,
        ConnectionString = settings.ConnectionString(serverOnly),
        IsAutoCloseConnection = true,
        InitKeyType = InitKeyType.Attribute,
        MoreSettings = new ConnMoreSettings { PgSqlIsAutoToLower = false, IsAutoToUpper = false },
    });

    /// <summary>
    /// 测试连接：先检查本机是否装了所选数据库(选本机时)，再测端口，最后用账号密码登录服务器。
    /// 目标库不存在不算失败(安装时会自动创建)，但要求账号有建库权限。
    /// </summary>
    public static async Task<DatabaseTestResult> TestAsync(DatabaseSettings settings, CancellationToken ct = default)
    {
        if (settings.Validate() is { } invalid) return new(DatabaseTestStatus.InvalidSettings, invalid);
        string name = DatabaseSettings.DisplayName(settings.Kind);
        if (settings.IsLocalHost && OperatingSystem.IsWindows() && !LocalServerDetector.IsInstalled(settings.Kind))
            return new(DatabaseTestStatus.LocalServerNotInstalled,
                $"本机没有安装 {name} 数据库服务。请先安装 {name}，或改填已安装数据库的电脑地址。");

        if (!await PortOpenAsync(settings.Host, settings.EffectivePort, ct).ConfigureAwait(false))
            return new(DatabaseTestStatus.Unreachable,
                $"无法连接 {settings.Host}:{settings.EffectivePort}。请确认 {name} 服务已启动、端口正确、防火墙已放行。");

        try
        {
            // 用驱动原生连接(不经 SqlSugar 包装)，才能拿到准确的错误号区分“密码错误/没有权限/连不上”。
            await using DbConnection connection = OpenRaw(settings, serverOnly: true);
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = VersionSql(settings.Kind);
            string version = Convert.ToString(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))?.Trim() ?? "";
            return new(DatabaseTestStatus.Ok, $"连接成功：{name} {version}", version);
        }
        catch (Exception ex)
        {
            return Classify(ex, settings);
        }
    }

    /// <summary>目标库不存在时创建(数据文件放在数据库服务的默认目录，字符集为 UTF-8)。</summary>
    public static async Task EnsureDatabaseAsync(DatabaseSettings settings, CancellationToken ct = default)
    {
        if (!settings.IsExternal) return;
        if (settings.Validate() is { } invalid) throw new InvalidOperationException(invalid);
        await using DbConnection connection = OpenRaw(settings, serverOnly: true);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        string name = settings.Database;   // Validate 已限定为字母、数字、下划线
        string sql = settings.Kind switch
        {
            DatabaseKind.SqlServer => $"IF DB_ID(N'{name}') IS NULL CREATE DATABASE [{name}]",
            DatabaseKind.MySql => $"CREATE DATABASE IF NOT EXISTS `{name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci",
            _ => "",
        };
        if (settings.Kind == DatabaseKind.PostgreSql)
        {
            await using var exists = connection.CreateCommand();
            exists.CommandText = $"SELECT 1 FROM pg_database WHERE datname = '{name}'";
            if (await exists.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null) return;
            sql = $"CREATE DATABASE \"{name}\" ENCODING 'UTF8'";
        }
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static DbConnection OpenRaw(DatabaseSettings settings, bool serverOnly) => settings.Kind switch
    {
        DatabaseKind.SqlServer => new Microsoft.Data.SqlClient.SqlConnection(settings.ConnectionString(serverOnly)),
        DatabaseKind.PostgreSql => new Npgsql.NpgsqlConnection(settings.ConnectionString(serverOnly)),
        DatabaseKind.MySql => new MySqlConnector.MySqlConnection(settings.ConnectionString(serverOnly)),
        _ => throw new InvalidOperationException("SQLite 不需要连接测试。"),
    };

    private static string VersionSql(DatabaseKind kind) => kind switch
    {
        DatabaseKind.SqlServer => "SELECT CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR(64))",
        DatabaseKind.PostgreSql => "SHOW server_version",
        _ => "SELECT VERSION()",
    };

    private static async Task<bool> PortOpenAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            string target = host.Trim() is "." or "(local)" ? "127.0.0.1" : host.Trim();
            await tcp.ConnectAsync(target, port, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static DatabaseTestResult Classify(Exception exception, DatabaseSettings settings)
    {
        string name = DatabaseSettings.DisplayName(settings.Kind);
        Exception root = exception;
        while (root.InnerException is not null && root is not DbException) root = root.InnerException;
        string message = root.Message;
        bool auth = root switch
        {
            Microsoft.Data.SqlClient.SqlException sql => sql.Number is 18456 or 18470 or 18486 or 4060,
            Npgsql.PostgresException pg => pg.SqlState is "28P01" or "28000",
            MySqlConnector.MySqlException my => my.ErrorCode is MySqlConnector.MySqlErrorCode.AccessDenied,
            _ => false,
        };
        if (auth) return new(DatabaseTestStatus.AuthenticationFailed, $"{name} 拒绝登录：账号或密码错误，或该账号没有登录权限。（{message}）");
        bool permission = root switch
        {
            Microsoft.Data.SqlClient.SqlException sql => sql.Number is 262 or 229 or 230,
            Npgsql.PostgresException pg => pg.SqlState is "42501",
            MySqlConnector.MySqlException my => my.ErrorCode is MySqlConnector.MySqlErrorCode.DatabaseAccessDenied
                or MySqlConnector.MySqlErrorCode.TableAccessDenied,
            _ => false,
        };
        if (permission) return new(DatabaseTestStatus.PermissionDenied, $"账号没有建库/建表权限，请换用有权限的账号。（{message}）");
        return new(DatabaseTestStatus.Failed, $"连接 {name} 失败：{message}");
    }
}

/// <summary>判断本机是否安装了某类数据库服务(看 Windows 服务名)。</summary>
public static class LocalServerDetector
{
    public static bool IsInstalled(DatabaseKind kind)
    {
        if (!OperatingSystem.IsWindows()) return true;
        try
        {
            return System.ServiceProcess.ServiceController.GetServices().Any(service => OperatingSystem.IsWindows() && Matches(kind, service.ServiceName));
        }
        catch
        {
            return true;   // 查不了服务列表时不拦截，交给后续的端口和登录测试
        }
    }

    internal static bool Matches(DatabaseKind kind, string serviceName) => kind switch
    {
        DatabaseKind.SqlServer => serviceName.Equals("MSSQLSERVER", StringComparison.OrdinalIgnoreCase)
            || serviceName.StartsWith("MSSQL$", StringComparison.OrdinalIgnoreCase),
        DatabaseKind.PostgreSql => serviceName.StartsWith("postgresql", StringComparison.OrdinalIgnoreCase),
        DatabaseKind.MySql => serviceName.StartsWith("MySQL", StringComparison.OrdinalIgnoreCase)
            || serviceName.StartsWith("MariaDB", StringComparison.OrdinalIgnoreCase),
        _ => true,
    };
}
