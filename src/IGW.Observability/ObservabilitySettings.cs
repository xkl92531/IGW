using System.Security.Cryptography;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IGW.Observability;

public sealed class ObservabilitySettings
{
    public const int DefaultPort = 28020;
    private const int PreviousGeneratedDefaultPort = 5088;
    public const int DefaultRetentionDays = 30;
    public const string FileName = "observability.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public string StorageDirectory { get; init; } = "";
    public int Port { get; init; } = DefaultPort;
    public string Username { get; init; } = "";
    public string ProtectedPassword { get; init; } = "";
    public int RetentionDays { get; init; } = DefaultRetentionDays;
    /// <summary>MES 等接口报文存档的总容量上限(MB)，超出后从最旧的开始删除。</summary>
    public int PayloadMaxMegabytes { get; init; } = DefaultPayloadMaxMegabytes;
    public const int DefaultPayloadMaxMegabytes = 2048;
    public IReadOnlyList<string> PreviousStorageDirectories { get; init; } = [];
    public string InstanceId { get; init; } = "";

    [JsonIgnore]
    public string Password { get; private set; } = "";

    public static ObservabilitySettings Initialize(
        string dataDirectory,
        string? storageDirectory = null,
        int? port = null)
        => Initialize(dataDirectory, storageDirectory, port, IsLoopbackPortAvailable);

    internal static ObservabilitySettings Initialize(
        string dataDirectory,
        string? storageDirectory,
        int? port,
        Func<int, bool> isPortAvailable)
    {
        ArgumentNullException.ThrowIfNull(isPortAvailable);
        string data = NormalizeDirectory(dataDirectory, nameof(dataDirectory));
        int? requestedPort = port.HasValue ? ValidatePort(port.Value) : null;
        string selectedStorage = NormalizeDirectory(
            storageDirectory ?? Path.Combine(data, "observability"),
            nameof(storageDirectory));

        string settingsPath = Path.Combine(data, FileName);
        if (File.Exists(settingsPath))
        {
            ObservabilitySettings existing = Load(data)!;
            bool changed = false;
            if (existing.Username.EndsWith("@localhost", StringComparison.OrdinalIgnoreCase))
            {
                existing = existing.WithUsername(BuildUsername(existing.InstanceId));
                changed = true;
            }
            if (existing.Port == PreviousGeneratedDefaultPort && requestedPort is null)
            {
                existing = existing.WithPort(SelectAvailablePortPair(DefaultPort, isPortAvailable));
                changed = true;
            }
            if (changed) SaveFile(settingsPath, existing);
            return existing;
        }

        Directory.CreateDirectory(data);
        Directory.CreateDirectory(selectedStorage);
        int selectedPort = requestedPort
            ?? SelectAvailablePortPair(DefaultPort, isPortAvailable);
        string instanceId = Guid.NewGuid().ToString("N");
        string password = Base64Url(RandomNumberGenerator.GetBytes(32));
        var settings = new ObservabilitySettings
        {
            StorageDirectory = selectedStorage,
            Port = selectedPort,
            Username = BuildUsername(instanceId),
            ProtectedPassword = PrivateFiles.Protect(password),
            RetentionDays = DefaultRetentionDays,
            PreviousStorageDirectories = [],
            InstanceId = instanceId,
            Password = password
        };
        SaveFile(settingsPath, settings);
        return settings;
    }

    public static ObservabilitySettings? Load(string dataDirectory)
    {
        string data = NormalizeDirectory(dataDirectory, nameof(dataDirectory));
        string settingsPath = Path.Combine(data, FileName);
        if (!File.Exists(settingsPath)) return null;

        ObservabilitySettings settings = JsonSerializer.Deserialize<ObservabilitySettings>(
            File.ReadAllBytes(settingsPath), JsonOptions)
            ?? throw new InvalidDataException("OpenObserve 配置为空。");
        ValidateLoaded(settings);
        try { settings.Password = PrivateFiles.Unprotect(settings.ProtectedPassword); }
        catch (Exception error) when (error is CryptographicException or FormatException)
        {
            // 主密钥丢了解不开：Docker 升级重建容器时 /usr/share/IGW/secrets 不在数据卷里，原先网关直接启动失败。
            // 这个旧 OpenObserve 凭据已没人用(日志改为网关自带存储)：换一个新的写回，照常启动，保留天数、存储位置不丢。
            settings = settings.WithNewPassword(Base64Url(RandomNumberGenerator.GetBytes(32)));
            SaveFile(settingsPath, settings);
        }
        return settings;
    }

    private ObservabilitySettings WithNewPassword(string password) => new()
    {
        StorageDirectory = StorageDirectory,
        Port = Port,
        Username = Username,
        ProtectedPassword = PrivateFiles.Protect(password),
        Password = password,
        RetentionDays = RetentionDays,
        PreviousStorageDirectories = PreviousStorageDirectories,
        PayloadMaxMegabytes = PayloadMaxMegabytes,
        InstanceId = InstanceId
    };

    public IReadOnlyDictionary<string, string> OpenObserveEnvironment()
    {
        ValidateLoaded(this);
        if (string.IsNullOrEmpty(Password))
            throw new InvalidOperationException("OpenObserve 凭据尚未解密。");

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ZO_HTTP_ADDR"] = "127.0.0.1",
            ["ZO_HTTP_PORT"] = Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["ZO_GRPC_ADDR"] = "127.0.0.1",
            ["ZO_GRPC_PORT"] = GrpcPortFor(Port).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["ZO_LOCAL_MODE"] = "true",
            ["ZO_DATA_DIR"] = StorageDirectory,
            ["ZO_WAL_DIR"] = Path.Combine(StorageDirectory, "wal"),
            ["ZO_STREAM_DIR"] = Path.Combine(StorageDirectory, "stream"),
            ["ZO_IDX_DIR"] = Path.Combine(StorageDirectory, "index"),
            ["ZO_SQLITE_DB_DIR"] = Path.Combine(StorageDirectory, "database"),
            ["ZO_META_STORE"] = "sqlite",
            ["ZO_ROOT_USER_EMAIL"] = Username,
            ["ZO_ROOT_USER_PASSWORD"] = Password,
            ["ZO_COMPACT_DATA_RETENTION_DAYS"] = RetentionDays.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["ZO_TELEMETRY"] = "false",
            // OpenObserve 默认每类数据(日志/追踪/指标/元数据)预分配 512MB 的 WAL 文件，
            // 一启动就占 2GB 磁盘；网关写入量小，32MB 足够，四类合计约 128MB。
            ["ZO_MAX_FILE_SIZE_ON_DISK"] = "32"
        };
    }

    public ObservabilitySettings WithStorageDirectory(string storageDirectory)
    {
        string selected = NormalizeDirectory(storageDirectory, nameof(storageDirectory));
        var history = PreviousStorageDirectories
            .Append(StorageDirectory)
            .Where(path => !string.Equals(path, selected, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToArray();
        return new ObservabilitySettings
        {
            StorageDirectory = selected,
            Port = Port,
            Username = Username,
            ProtectedPassword = ProtectedPassword,
            Password = Password,
            RetentionDays = RetentionDays,
            PreviousStorageDirectories = history,
            PayloadMaxMegabytes = PayloadMaxMegabytes,
            InstanceId = InstanceId
        };
    }

    public ObservabilitySettings WithRetentionDays(int retentionDays)
    {
        if (retentionDays is < 1 or > 3650)
            throw new ArgumentOutOfRangeException(nameof(retentionDays), "OpenObserve 日志保留天数必须在 1 到 3650 之间。");
        return new ObservabilitySettings
        {
            StorageDirectory = StorageDirectory,
            Port = Port,
            Username = Username,
            ProtectedPassword = ProtectedPassword,
            Password = Password,
            RetentionDays = retentionDays,
            PreviousStorageDirectories = PreviousStorageDirectories.ToArray(),
            PayloadMaxMegabytes = PayloadMaxMegabytes,
            InstanceId = InstanceId
        };
    }

    public void Persist(string dataDirectory)
    {
        string data = NormalizeDirectory(dataDirectory, nameof(dataDirectory));
        SaveFile(Path.Combine(data, FileName), this);
    }

    private ObservabilitySettings WithUsername(string username)
    {
        return new ObservabilitySettings
        {
            StorageDirectory = StorageDirectory,
            Port = Port,
            Username = username,
            ProtectedPassword = ProtectedPassword,
            Password = Password,
            RetentionDays = RetentionDays,
            PreviousStorageDirectories = PreviousStorageDirectories.ToArray(),
            PayloadMaxMegabytes = PayloadMaxMegabytes,
            InstanceId = InstanceId
        };
    }

    private ObservabilitySettings WithPort(int port)
    {
        return new ObservabilitySettings
        {
            StorageDirectory = StorageDirectory,
            Port = ValidatePort(port),
            Username = Username,
            ProtectedPassword = ProtectedPassword,
            Password = Password,
            RetentionDays = RetentionDays,
            PreviousStorageDirectories = PreviousStorageDirectories.ToArray(),
            PayloadMaxMegabytes = PayloadMaxMegabytes,
            InstanceId = InstanceId
        };
    }

    private static void SaveFile(string settingsPath, ObservabilitySettings settings)
    {
        ValidateLoaded(settings);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);
        try { PrivateFiles.AtomicWrite(settingsPath, json); }
        finally { CryptographicOperations.ZeroMemory(json); }
    }

    private static void ValidateLoaded(ObservabilitySettings settings)
    {
        if (!Path.IsPathFullyQualified(settings.StorageDirectory))
            throw new InvalidDataException("OpenObserve 存储目录必须是绝对路径。");
        ValidatePort(settings.Port);
        if (settings.RetentionDays is < 1 or > 3650)
            throw new InvalidDataException("OpenObserve 日志保留天数必须在 1 到 3650 之间。");
        if (string.IsNullOrWhiteSpace(settings.Username)
            || string.IsNullOrWhiteSpace(settings.ProtectedPassword)
            || !Guid.TryParseExact(settings.InstanceId, "N", out _))
            throw new InvalidDataException("OpenObserve 配置身份信息无效。");
    }

    private static int ValidatePort(int port)
    {
        if (port is < 1024 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), port, "OpenObserve 端口必须在 1024 到 65535 之间。");
        return port;
    }

    internal static int SelectAvailablePortPair(int preferredHttpPort, Func<int, bool> isPortAvailable)
    {
        ArgumentNullException.ThrowIfNull(isPortAvailable);
        ValidatePort(preferredHttpPort);
        if (preferredHttpPort > 65534)
            throw new ArgumentOutOfRangeException(
                nameof(preferredHttpPort),
                preferredHttpPort,
                "OpenObserve 自动端口的 HTTP 和 gRPC 必须能使用两个连续端口。");

        for (int candidate = preferredHttpPort; candidate <= 65534; candidate += 2)
        {
            if (isPortAvailable(candidate) && isPortAvailable(candidate + 1)) return candidate;
        }

        throw new IOException($"从 {preferredHttpPort} 开始未找到可用的 OpenObserve 连续端口。");
    }

    private static bool IsLoopbackPortAvailable(int port)
    {
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Server.ExclusiveAddressUse = true;
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            listener?.Stop();
        }
    }

    private static string NormalizeDirectory(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("目录必须是绝对路径。", parameterName);
        return Path.GetFullPath(path);
    }

    private static string Base64Url(byte[] bytes)
    {
        try { return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static string BuildUsername(string instanceId) => $"igw-{instanceId[..12]}@igw.local";

    private static int GrpcPortFor(int httpPort) => httpPort == 65535 ? 65534 : httpPort + 1;
}
