using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IGW.Abstractions;

namespace IGW.Core.Security;

/// <summary>网页各功能页面的权限级别。</summary>
public enum AccessLevel
{
    None = 0,   // 看不到
    View = 1,   // 只能看
    Edit = 2,   // 可以修改
}

/// <summary>可以单独分配权限的功能页面(和网页左侧菜单一一对应)。</summary>
public static class GatewayModules
{
    public const string Project = "project";
    public const string Monitor = "monitor";
    public const string Flow = "flow";
    public const string Performance = "performance";
    public const string Logs = "logs";
    public const string Settings = "settings";

    public sealed record Module(string Id, string Name);

    public static readonly IReadOnlyList<Module> All =
    [
        new(Monitor, "监控中心"),
        new(Project, "工程配置"),
        new(Flow, "采集流程"),
        new(Performance, "采集性能"),
        new(Logs, "日志中心"),
        new(Settings, "IGW 设置"),
    ];

    public static bool IsKnown(string id) => All.Any(module => module.Id == id);
}

/// <summary>网关账号。账号存在网关本机数据目录(accounts.json)，不属于工程，导入导出工程不会带走。</summary>
public sealed class GatewayAccount
{
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool IsAdmin { get; set; }
    /// <summary>初始密码或管理员重置过的密码：登录后必须先改密码。</summary>
    public bool MustChangePassword { get; set; }
    public bool Disabled { get; set; }
    /// <summary>页面 → 权限级别；没列出的页面 = 看不到。管理员不看这里，全部可修改。</summary>
    public Dictionary<string, AccessLevel> Permissions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>改密码、改权限、停用时加一：之前签发的登录凭证随即失效。</summary>
    public int Stamp { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? LastLoginUtc { get; set; }

    public AccessLevel LevelOf(string module) =>
        IsAdmin ? AccessLevel.Edit : Permissions.TryGetValue(module, out AccessLevel level) ? level : AccessLevel.None;
}

/// <summary>账号操作失败(用户名重复、改管理员等)，Message 直接给界面显示。</summary>
public sealed class GatewayAccountException(string message) : Exception(message);

/// <summary>
/// 网关账号存储：只有一个内置管理员 admin(不能删除、不能新增第二个)，管理员增删改普通用户并分配页面权限。
/// 首次运行自动建 admin / admin，登录后强制改密码。
/// </summary>
public sealed class GatewayAccountStore
{
    public const string AdminUsername = "admin";
    public const string InitialAdminPassword = "admin";
    public const int MinPasswordLength = 6;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _file;
    private readonly Func<DateTime> _utcNow;
    private readonly object _gate = new();
    private List<GatewayAccount> _accounts;

    public GatewayAccountStore(string dataDir) : this(dataDir, static () => DateTime.UtcNow) { }

    internal GatewayAccountStore(string dataDir, Func<DateTime> utcNow)
    {
        _file = Path.Combine(dataDir, "accounts.json");
        _utcNow = utcNow;
        _accounts = Load();
        if (!_accounts.Any(account => account.IsAdmin))
        {
            _accounts.RemoveAll(account => account.Username.Equals(AdminUsername, StringComparison.OrdinalIgnoreCase));
            _accounts.Insert(0, new GatewayAccount
            {
                Username = AdminUsername,
                PasswordHash = ProjectSecrets.HashPassword(InitialAdminPassword),
                IsAdmin = true,
                MustChangePassword = true,
                CreatedUtc = _utcNow(),
            });
            Save();
        }
    }

    public IReadOnlyList<GatewayAccount> List()
    {
        lock (_gate) return _accounts.Select(Clone).ToList();
    }

    public GatewayAccount? Find(string username)
    {
        lock (_gate) return FindLocked(username) is { } account ? Clone(account) : null;
    }

    /// <summary>核对用户名密码；成功时记下登录时间并返回账号，失败(含停用)返回 null。</summary>
    public GatewayAccount? Authenticate(string username, string password)
    {
        lock (_gate)
        {
            GatewayAccount? account = FindLocked(username);
            if (account is null || account.Disabled) return null;
            if (!ProjectSecrets.VerifyPassword(new UserDef { PasswordHash = account.PasswordHash }, password ?? "")) return null;
            account.LastLoginUtc = _utcNow();
            Save();
            return Clone(account);
        }
    }

    /// <summary>管理员新建普通用户。</summary>
    public GatewayAccount Create(string username, string displayName, string password, IDictionary<string, AccessLevel> permissions)
    {
        username = (username ?? "").Trim();
        ValidateUsername(username);
        ValidatePassword(password);
        lock (_gate)
        {
            if (FindLocked(username) is not null) throw new GatewayAccountException($"用户名 {username} 已存在。");
            var account = new GatewayAccount
            {
                Username = username,
                DisplayName = (displayName ?? "").Trim(),
                PasswordHash = ProjectSecrets.HashPassword(password),
                MustChangePassword = true,   // 管理员设的初始密码，用户首次登录自己改
                Permissions = Normalize(permissions),
                CreatedUtc = _utcNow(),
            };
            _accounts.Add(account);
            Save();
            return Clone(account);
        }
    }

    /// <summary>管理员修改普通用户：显示名、权限、停用；password 非空时重置密码(用户下次登录须改密码)。</summary>
    public GatewayAccount Update(string username, string? displayName, IDictionary<string, AccessLevel>? permissions,
        bool? disabled, string? password)
    {
        lock (_gate)
        {
            GatewayAccount account = FindLocked(username) ?? throw new GatewayAccountException($"用户 {username} 不存在。");
            if (account.IsAdmin && (permissions is not null || disabled == true))
                throw new GatewayAccountException("管理员拥有全部权限，不能修改权限或停用。");
            if (!string.IsNullOrEmpty(password))
            {
                ValidatePassword(password);
                account.PasswordHash = ProjectSecrets.HashPassword(password);
                account.MustChangePassword = !account.IsAdmin;
            }
            if (displayName is not null) account.DisplayName = displayName.Trim();
            if (permissions is not null) account.Permissions = Normalize(permissions);
            if (disabled is not null) account.Disabled = disabled.Value;
            account.Stamp++;
            Save();
            return Clone(account);
        }
    }

    public void Delete(string username)
    {
        lock (_gate)
        {
            GatewayAccount account = FindLocked(username) ?? throw new GatewayAccountException($"用户 {username} 不存在。");
            if (account.IsAdmin) throw new GatewayAccountException("管理员账号不能删除。");
            _accounts.Remove(account);
            Save();
        }
    }

    /// <summary>用户自己改密码：必须提供正确的旧密码。</summary>
    public GatewayAccount ChangePassword(string username, string oldPassword, string newPassword)
    {
        lock (_gate)
        {
            GatewayAccount account = FindLocked(username) ?? throw new GatewayAccountException($"用户 {username} 不存在。");
            if (!ProjectSecrets.VerifyPassword(new UserDef { PasswordHash = account.PasswordHash }, oldPassword ?? ""))
                throw new GatewayAccountException("原密码不正确。");
            ValidatePassword(newPassword);
            if (newPassword == oldPassword) throw new GatewayAccountException("新密码不能和原密码相同。");
            account.PasswordHash = ProjectSecrets.HashPassword(newPassword);
            account.MustChangePassword = false;
            account.Stamp++;
            Save();
            return Clone(account);
        }
    }

    private GatewayAccount? FindLocked(string username) =>
        _accounts.FirstOrDefault(account => account.Username.Equals((username ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

    private static Dictionary<string, AccessLevel> Normalize(IDictionary<string, AccessLevel> permissions)
    {
        var result = new Dictionary<string, AccessLevel>(StringComparer.OrdinalIgnoreCase);
        foreach ((string module, AccessLevel level) in permissions)
            if (GatewayModules.IsKnown(module) && level != AccessLevel.None) result[module] = level;
        return result;
    }

    private static void ValidateUsername(string username)
    {
        if (username.Length is < 2 or > 32 || !username.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.'))
            throw new GatewayAccountException("用户名为 2–32 位字母、数字、下划线、横线或点。");
        if (username.Equals(AdminUsername, StringComparison.OrdinalIgnoreCase))
            throw new GatewayAccountException("admin 是内置管理员，只能有一个。");
    }

    private static void ValidatePassword(string? password)
    {
        if ((password ?? "").Length < MinPasswordLength)
            throw new GatewayAccountException($"密码至少 {MinPasswordLength} 位。");
    }

    private List<GatewayAccount> Load()
    {
        try
        {
            if (!File.Exists(_file)) return [];
            return JsonSerializer.Deserialize<List<GatewayAccount>>(File.ReadAllText(_file), Json) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // 文件损坏：保留原文件备查，重新建管理员，避免所有人都登录不进去
            try { File.Copy(_file, _file + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss"), overwrite: true); } catch { }
            return [];
        }
    }

    // 先写临时文件再替换，写到一半断电也不会把账号文件写坏
    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        string temp = _file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_accounts, Json), Encoding.UTF8);
        File.Move(temp, _file, overwrite: true);
    }

    private static GatewayAccount Clone(GatewayAccount account) => new()
    {
        Username = account.Username,
        DisplayName = account.DisplayName,
        PasswordHash = account.PasswordHash,
        IsAdmin = account.IsAdmin,
        MustChangePassword = account.MustChangePassword,
        Disabled = account.Disabled,
        Permissions = new Dictionary<string, AccessLevel>(account.Permissions, StringComparer.OrdinalIgnoreCase),
        Stamp = account.Stamp,
        CreatedUtc = account.CreatedUtc,
        LastLoginUtc = account.LastLoginUtc,
    };
}

/// <summary>
/// 登录凭证：用户名 + 账号版本号 + 到期时间，用本机密钥(auth.key)签名。网关重启后照样有效；
/// 改密码/改权限/停用会让账号版本号变化，旧凭证随即失效。
/// </summary>
public sealed class GatewaySessionTokens
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);
    private readonly byte[] _key;
    private readonly Func<DateTime> _utcNow;

    public GatewaySessionTokens(string dataDir) : this(dataDir, static () => DateTime.UtcNow) { }

    internal GatewaySessionTokens(string dataDir, Func<DateTime> utcNow)
    {
        _utcNow = utcNow;
        string file = Path.Combine(dataDir, "auth.key");
        byte[]? key = null;
        try { if (File.Exists(file)) key = Convert.FromBase64String(File.ReadAllText(file).Trim()); } catch { }
        if (key is not { Length: >= 32 })
        {
            key = RandomNumberGenerator.GetBytes(32);
            Directory.CreateDirectory(dataDir);
            File.WriteAllText(file, Convert.ToBase64String(key));
        }
        _key = key;
    }

    public string Issue(GatewayAccount account)
    {
        long expires = new DateTimeOffset(_utcNow() + Lifetime).ToUnixTimeSeconds();
        string body = B64(Encoding.UTF8.GetBytes($"{account.Username}\n{account.Stamp}\n{expires}"));
        return body + "." + B64(Sign(body));
    }

    /// <summary>验证凭证并返回当前账号；过期、签名不对、账号已改密码/改权限/停用/删除都返回 null。</summary>
    public GatewayAccount? Validate(string? token, GatewayAccountStore accounts)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 1024) return null;
        int dot = token.IndexOf('.');
        if (dot <= 0) return null;
        string body = token[..dot];
        byte[] signature;
        string[] parts;
        try
        {
            signature = Unb64(token[(dot + 1)..]);
            parts = Encoding.UTF8.GetString(Unb64(body)).Split('\n');
        }
        catch (FormatException) { return null; }
        if (!CryptographicOperations.FixedTimeEquals(signature, Sign(body)) || parts.Length != 3) return null;
        if (!long.TryParse(parts[2], out long expires) || DateTimeOffset.FromUnixTimeSeconds(expires) < _utcNow()) return null;
        GatewayAccount? account = accounts.Find(parts[0]);
        return account is { Disabled: false } && account.Stamp.ToString() == parts[1] ? account : null;
    }

    private byte[] Sign(string body) => HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes(body));
    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] Unb64(string text)
    {
        text = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(text + new string('=', (4 - text.Length % 4) % 4));
    }
}
