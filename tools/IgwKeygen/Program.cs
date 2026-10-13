using System.Globalization;
using System.Security.Cryptography;
using IGW.Licensing;

// IGW 授权发码工具(仅厂商内部使用,持私钥)。
//   genkey                                     生成密钥对(private.key + 打印公钥,回填 LicenseCode.PublicKeyB64)
//   issue -c "客户名" -m 机器码 [-d 天数 | -e yyyy-MM-dd] [-g 宽限天] [-n 备注]
// 私钥文件 private.key 与本工具同目录;绝不提交、绝不进安装包。

// 私钥查找:环境变量 > exe 旁 > 上溯到 tools\IgwKeygen\private.key(源目录长期保管,不随 clean 删)
string PrivKeyPath = ResolvePrivKey();
static string ResolvePrivKey()
{
    var env = Environment.GetEnvironmentVariable("IGW_KEYGEN_PRIVATE");
    if (!string.IsNullOrWhiteSpace(env)) return env;
    string beside = Path.Combine(AppContext.BaseDirectory, "private.key");
    if (File.Exists(beside)) return beside;
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
    {
        string p = Path.Combine(d.FullName, "private.key");
        if (File.Exists(p)) return p;
        if (string.Equals(d.Name, "IgwKeygen", StringComparison.OrdinalIgnoreCase)) break;
    }
    return beside;   // 不存在时默认写这里(genkey 用)
}

if (args.Length == 0) { Help(); return; }

switch (args[0].ToLowerInvariant())
{
    case "genkey": GenKey(); break;
    case "issue": Issue(args); break;
    case "verify": Verify(args); break;
    case "protect-key": ProtectKey(args); break;
    default: Help(); break;
}

void Help()
{
    Console.WriteLine("""
IGW 授权发码工具
  genkey                                            生成 ECDSA 密钥对(首次用一次);打印公钥,回填 src/IGW.Licensing/LicenseCode.cs 的 PublicKeyB64
  issue -c "客户名" -m 机器码 [-d 天数|-e yyyy-MM-dd] [-g 宽限天=7] [-n 备注]   签发授权码
  verify <授权码>                                   本地验证一段授权码
  protect-key <输出文件>                            用管理员密码加密 private.key(发码工具 publish.ps1 调用,嵌进 exe)
示例:
  IgwKeygen issue -c "南卡工厂" -m A1B2-C3D4-E5F6-0718 -d 365 -g 7
  (机器码由现场在配置工具/托盘「授权」窗口里读取后报给你)
""");
}

void GenKey()
{
    if (File.Exists(PrivKeyPath))
    {
        Console.WriteLine($"⚠ 私钥已存在:{PrivKeyPath}");
        Console.WriteLine("  重新生成会作废所有已发授权码!确定请先手动删除该文件再运行。");
        return;
    }
    using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    string priv = Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey());
    string pub = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
    File.WriteAllText(PrivKeyPath, priv);
    Console.WriteLine($"✓ 私钥已保存:{PrivKeyPath}(务必保管好、绝不外泄/入库/进安装包)");
    Console.WriteLine();
    Console.WriteLine("把下面这行公钥填进 src/IGW.Licensing/LicenseCode.cs 的 PublicKeyB64 常量,然后重编译:");
    Console.WriteLine();
    Console.WriteLine(pub);
}

void Issue(string[] a)
{
    if (!File.Exists(PrivKeyPath)) { Console.WriteLine($"✗ 找不到私钥 {PrivKeyPath},先运行 genkey。"); return; }
    string customer = Opt(a, "-c") ?? "";
    string machine = MachineId.Normalize(Opt(a, "-m") ?? "");
    string? days = Opt(a, "-d");
    string? expiry = Opt(a, "-e");
    int grace = int.TryParse(Opt(a, "-g"), out var g) ? g : 7;
    string note = Opt(a, "-n") ?? "";

    if (string.IsNullOrWhiteSpace(customer)) { Console.WriteLine("✗ 必须 -c 客户名"); return; }
    if (machine.Length != 16) { Console.WriteLine("✗ 必须 -m 机器码(16 位,现场读取,可带 - 分隔)"); return; }

    var now = DateTime.UtcNow;
    DateTime exp;
    if (!string.IsNullOrEmpty(expiry))
    {
        if (!DateTime.TryParse(expiry, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var e))
        { Console.WriteLine("✗ -e 日期格式应为 yyyy-MM-dd"); return; }
        exp = e.ToUniversalTime().Date.AddDays(1).AddSeconds(-1);   // 到当天 23:59:59
    }
    else
    {
        int d = int.TryParse(days, out var dd) ? dd : 365;
        exp = now.AddDays(d);
    }

    var payload = new LicensePayload
    {
        Customer = customer, Machine = machine, IssuedUtc = now, ExpiryUtc = exp, GraceDays = grace, Note = note,
    };

    using var ecdsa = ECDsa.Create();
    ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(File.ReadAllText(PrivKeyPath).Trim()), out _);
    string code = LicenseCode.Sign(payload, ecdsa);

    // 发码台账(与私钥同目录,便于日后查"发过谁/何时到期/补发")。
    // 先记账再打印:打印被下游管道提前掐断(如 | Select -First 1)也不丢账。
    IssueLedger.Append(PrivKeyPath, new IssueLedger.Entry
    {
        Ts = DateTime.Now, Customer = customer, Machine = machine,
        Expiry = exp.ToLocalTime().ToString("yyyy-MM-dd"), Grace = grace, Note = note, Code = code,
    });

    Console.WriteLine($"客户:{customer}");
    Console.WriteLine($"机器码:{machine}");
    Console.WriteLine($"到期:{exp.ToLocalTime():yyyy-MM-dd}(宽限 {grace} 天)");
    Console.WriteLine($"(已记台账 {IssueLedger.PathFor(PrivKeyPath)})");
    Console.WriteLine();
    Console.WriteLine("授权码(发给现场,在配置工具/托盘「授权」窗口粘贴激活):");
    Console.WriteLine();
    Console.WriteLine(code);
}

void Verify(string[] a)
{
    if (a.Length < 2) { Console.WriteLine("用法:verify <授权码>"); return; }
    if (LicenseCode.Verify(a[1], out var p) && p != null)
        Console.WriteLine($"✓ 有效  客户={p.Customer} 机器={p.Machine} 到期={p.ExpiryUtc.ToLocalTime():yyyy-MM-dd} 宽限={p.GraceDays}天");
    else
        Console.WriteLine("✗ 无效(签名不匹配或已损坏)");
}

// 发码工具内嵌私钥用：输两遍管理员密码，把 private.key 加密写到输出文件(输出文件只在发布时临时用，发布完删掉)。
void ProtectKey(string[] a)
{
    if (a.Length < 2) { Console.WriteLine("✗ 用法:protect-key <输出文件>"); Environment.ExitCode = 2; return; }
    if (!File.Exists(PrivKeyPath)) { Console.WriteLine($"✗ 找不到私钥 {PrivKeyPath}"); Environment.ExitCode = 1; return; }

    byte[] pkcs8 = Convert.FromBase64String(File.ReadAllText(PrivKeyPath).Trim());
    using (var ec = ECDsa.Create())
    {
        ec.ImportPkcs8PrivateKey(pkcs8, out _);
        if (Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo()) != LicenseCode.PublicKeyB64)
        {
            Console.WriteLine($"✗ {PrivKeyPath} 与软件内嵌公钥不匹配,用它发的码软件会判无效,不打包。");
            Environment.ExitCode = 1;
            return;
        }
    }

    string password = ReadPassword("设置发码工具管理员密码(至少 8 位):");
    if (password.Length < 8) { Console.WriteLine("✗ 密码至少 8 位"); Environment.ExitCode = 1; return; }
    if (ReadPassword("再输一遍确认:") != password) { Console.WriteLine("✗ 两次输入不一致"); Environment.ExitCode = 1; return; }

    File.WriteAllText(a[1], IgwKeygenGui.ProtectedPrivateKey.Protect(pkcs8, password));
    System.Security.Cryptography.CryptographicOperations.ZeroMemory(pkcs8);
    Console.WriteLine($"✓ 已用管理员密码加密私钥:{a[1]}");
}

static string ReadPassword(string prompt)
{
    Console.Write(prompt);
    if (Console.IsInputRedirected) { string line = Console.ReadLine() ?? ""; Console.WriteLine(); return line; }
    var sb = new System.Text.StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) break;
        if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) { sb.Length--; Console.Write("\b \b"); } continue; }
        if (!char.IsControl(key.KeyChar)) { sb.Append(key.KeyChar); Console.Write('*'); }
    }
    Console.WriteLine();
    return sb.ToString();
}

static string? Opt(string[] a, string key)
{
    int i = Array.IndexOf(a, key);
    return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
}
