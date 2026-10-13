using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace IGW.Licensing;

/// <summary>
/// 授权管理:从程序目录 license.key 读授权码 → 验签 → 验机器码 → 防时间回拨 → 算状态。
/// 激活 = 把新授权码写进 license.key。全部无锁,状态每次现算(便于长跑复查/激活后即时生效)。
/// 防回拨(离线时钟作弊):把"见过的最大 UTC"水位**多副本冗余**存(程序目录 + ProgramData 两处 + 注册表),
/// 读取取所有副本最大值——删/改单个副本无效,客户须同时清光所有副本才能重置,门槛远高于单文件。
/// 按安装目录哈希隔离,不同实例/测试互不串扰(否则会误伤别的实例)。
/// </summary>
public sealed class LicenseManager
{
    private readonly string _keyFile;          // license.key(程序目录,主位置)
    private readonly string _keyFileFallback;  // ProgramData 备用(Program Files 不可写时激活仍能落盘)
    private readonly string[] _seenFiles;      // 防回拨水位:多副本文件(取最大)
    private readonly string[] _trialFiles;     // 首次试用时间:卸载不删除，重复安装沿用
    private readonly string _regSub;           // 注册表子键 SOFTWARE\IGW
    private readonly string _regVal;           // 注册表值名(按安装隔离)
    private readonly string _trialRegVal;
    private readonly Func<DateTime> _utcNow;
    private readonly bool _useRegistry;
    private readonly DateTime _trialStartedUtc;
    private const long Obf = unchecked((long)0x5A3C1E7B9D4286F1);   // 水位混淆(非加密,只为不让人一眼看懂/顺手改)
    private const long TrialObf = unchecked((long)0x31D7A95C6E208BF4);
    private const int RollbackSkewHours = 26;  // 允许的时钟误差/时区宽容
    public const int TrialDays = 7;
    private DateTime _lastPersisted = DateTime.MinValue;   // 内存节流:避免 /health 高频触发狂写多副本

    // 结果缓存:托盘 5s/配置工具 3s 都在轮询 /health → Evaluate;不缓存则每次 4 次文件读+2 次注册表读+1 次 ECDSA 验签
    private readonly object _sync = new();
    private LicenseState? _cached;
    private DateTime _cachedAtUtc;
    private const int CacheSeconds = 15;

    public LicenseManager(string baseDir) : this(baseDir, static () => DateTime.UtcNow, useRegistry: true)
    {
    }

    internal LicenseManager(string baseDir, Func<DateTime> utcNow, bool useRegistry)
    {
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        _useRegistry = useRegistry;
        string tag = ShortHash(baseDir);   // 按安装目录隔离,生产/测试各自独立水位,互不干扰
        if (OperatingSystem.IsWindows())
        {
            string pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);   // C:\ProgramData
            _keyFile = Path.Combine(baseDir, "license.key");
            _keyFileFallback = Path.Combine(pd, "IGW", tag, "license.key");
            _seenFiles = useRegistry
                ? [
                    Path.Combine(baseDir, ".igwlic"),                 // 程序目录(原位置,兼容旧版)
                    Path.Combine(pd, "IGW", tag, "cfg.dat"),          // ProgramData(重装/升级不丢、稍隐蔽)
                    Path.Combine(pd, "IGW", tag, ".state"),           // ProgramData 第二副本
                ]
                : [Path.Combine(baseDir, ".igwlic")];
            _trialFiles = useRegistry
                ? [
                    Path.Combine(baseDir, ".igwtrial"),
                    Path.Combine(pd, "IGW", tag, "trial.dat"),
                    Path.Combine(pd, "IGW", tag, ".trial"),
                ]
                : [Path.Combine(baseDir, ".igwtrial")];
        }
        else
        {
            // Linux:注册表不存在 → 全文件多副本(系统区 /var/lib、/etc + 用户区 ~/.config,各自 best-effort)
            string sys = Path.Combine("/var/lib/igw/.lic", tag);
            string usr = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "igw");   // ~/.config/igw
            _keyFile = Path.Combine(baseDir, "license.key");
            _keyFileFallback = Path.Combine(sys, "license.key");
            _seenFiles = useRegistry
                ? [
                    Path.Combine(baseDir, ".igwlic"),
                    Path.Combine(sys, "cfg.dat"),
                    Path.Combine(sys, ".state"),
                    Path.Combine("/etc/igw", "st_" + tag),            // root 服务可写;非 root 静默失败不影响
                    Path.Combine(usr, "st_" + tag),
                ]
                : [Path.Combine(baseDir, ".igwlic")];
            _trialFiles = useRegistry
                ? [
                    Path.Combine(baseDir, ".igwtrial"),
                    Path.Combine(sys, "trial.dat"),
                    Path.Combine("/etc/igw", "tr_" + tag),
                    Path.Combine(usr, "tr_" + tag),
                ]
                : [Path.Combine(baseDir, ".igwtrial")];
        }
        _regSub = @"SOFTWARE\IGW";
        _regVal = "Cfg_" + tag;
        _trialRegVal = "Trial_" + tag;
        _trialStartedUtc = EnsureTrialStarted();
    }

    public string KeyFilePath => _keyFile;

    /// <summary>当前授权状态(15s 缓存;激活后立即失效重算)。</summary>
    public LicenseState Evaluate()
    {
        lock (_sync)
        {
            if (_cached != null && (DateTime.UtcNow - _cachedAtUtc).TotalSeconds < CacheSeconds) return _cached;
            _cached = Evaluate(ReadBestCode(), updateSeen: true);
            _cachedAtUtc = DateTime.UtcNow;
            return _cached;
        }
    }

    /// <summary>验证一段授权码(不落盘;用于激活对话框预检)。</summary>
    public LicenseState Check(string? code) => Evaluate(code, updateSeen: false);

    /// <summary>
    /// 激活:验证通过(且机器码匹配本机)才写入 license.key。
    /// 防误自杀:新码已失效(如粘了张过期旧码)而当前授权还正常 → 拒绝落盘,保留现有授权。
    /// 主位置(程序目录)写不进(如非管理员对 Program Files)→ 自动落 ProgramData 备用位。
    /// </summary>
    public LicenseState Activate(string code)
    {
        var st = Evaluate(code, updateSeen: false);
        if (st.Status is LicenseStatus.Invalid or LicenseStatus.MachineMismatch)
            return st;   // 假码/别机的码:拒绝,不动现有授权

        LicenseState cur;
        lock (_sync) { cur = Evaluate(ReadBestCode(), updateSeen: false); }
        if (st.Blocked && !cur.Blocked)
            return new LicenseState
            {
                Status = st.Status, Payload = st.Payload, DaysLeft = st.DaysLeft,
                Message = "已拒绝激活:该授权码已失效(" + st.Message + ")当前有效授权保持不变。",
            };

        string trimmed = code.Trim();
        string err;
        try
        {
            File.WriteAllText(_keyFile, trimmed);
            try { if (File.Exists(_keyFileFallback)) File.Delete(_keyFileFallback); } catch { }   // 主位可写则清备用,避免旧码残留抢优先
            err = "";
        }
        catch (Exception ex)
        {
            err = ex.Message;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_keyFileFallback)!);
                File.WriteAllText(_keyFileFallback, trimmed);
                err = "";
            }
            catch (Exception ex2) { err += ";备用位也失败:" + ex2.Message; }
        }
        if (err.Length > 0)
            return new LicenseState { Status = LicenseStatus.Invalid, Message = "写入 license.key 失败:" + err };

        lock (_sync) { _cached = null; }   // 立即生效
        return Evaluate(trimmed, updateSeen: true);
    }

    // 读主/备两处 license.key,都存在时取"更优"的那份(未阻断优先,其次到期更晚)
    private string? ReadBestCode()
    {
        string? a = TryReadFile(_keyFile), b = TryReadFile(_keyFileFallback);
        if (b is null) return a;
        if (a is null) return b;
        var sa = Evaluate(a, updateSeen: false);
        var sb = Evaluate(b, updateSeen: false);
        if (sa.Blocked != sb.Blocked) return sa.Blocked ? b : a;
        return (sb.ExpiryUtc ?? DateTime.MinValue) > (sa.ExpiryUtc ?? DateTime.MinValue) ? b : a;
    }

    private static string? TryReadFile(string p)
    {
        try { return File.Exists(p) ? File.ReadAllText(p).Trim() : null; } catch { return null; }
    }

    private LicenseState Evaluate(string? code, bool updateSeen)
    {
        if (string.IsNullOrWhiteSpace(code))
            return EvaluateTrial(updateSeen);

        if (!LicenseCode.Verify(code, out var p) || p == null)
            return new LicenseState { Status = LicenseStatus.Invalid, Message = "授权码无效或已损坏。" };

        // 绑机:授权码里的机器码必须等于本机(空=不绑,放行)
        if (!string.IsNullOrEmpty(p.Machine) &&
            !string.Equals(MachineId.Normalize(p.Machine), MachineId.Normalize(MachineId.Current), StringComparison.Ordinal))
            return new LicenseState { Status = LicenseStatus.MachineMismatch, Payload = p,
                Message = $"授权码绑定的是另一台机器({p.Machine}),本机机器码为 {MachineId.Current}。" };

        var now = _utcNow();

        // 防时间回拨:当前时间明显早于历史见过的最大时间 → 判作弊
        var seen = ReadSeen();
        if (seen.HasValue && now < seen.Value.AddHours(-RollbackSkewHours))
            return new LicenseState { Status = LicenseStatus.Tampered, Payload = p,
                Message = "检测到系统时间被回拨,授权暂停。请把系统时间调回正确值。" };
        if (updateSeen && (!seen.HasValue || now > seen.Value)) WriteSeen(now);

        int daysLeft = (int)Math.Floor((p.ExpiryUtc - now).TotalDays);
        if (now < p.ExpiryUtc)
        {
            var status = daysLeft <= 30 ? LicenseStatus.ExpiringSoon : LicenseStatus.Valid;
            string msg = status == LicenseStatus.ExpiringSoon
                ? $"授权将于 {Local(p.ExpiryUtc)} 到期(剩 {daysLeft} 天),请及时续期。"
                : $"授权有效,到期 {Local(p.ExpiryUtc)}。";
            return new LicenseState { Status = status, Payload = p, DaysLeft = daysLeft, Message = msg };
        }

        // 已过期:看宽限期
        double intoGrace = (now - p.ExpiryUtc).TotalDays;
        if (intoGrace <= Math.Max(0, p.GraceDays))
        {
            int graceLeft = (int)Math.Ceiling(p.GraceDays - intoGrace);
            return new LicenseState { Status = LicenseStatus.Grace, Payload = p, DaysLeft = daysLeft,
                Message = $"授权已于 {Local(p.ExpiryUtc)} 到期,处于宽限期(剩 {graceLeft} 天),请尽快续期,否则将停止采集。" };
        }

        return new LicenseState { Status = LicenseStatus.Expired, Payload = p, DaysLeft = daysLeft,
            Message = $"授权已过期(到期 {Local(p.ExpiryUtc)},宽限期已用完),采集与北向已停止。请更换新授权码激活。" };
    }

    private LicenseState EvaluateTrial(bool updateSeen)
    {
        DateTime now = _utcNow();
        DateTime expiry = _trialStartedUtc.AddDays(TrialDays);
        var payload = new LicensePayload
        {
            Customer = "本机试用",
            IssuedUtc = _trialStartedUtc,
            ExpiryUtc = expiry,
            Machine = MachineId.Normalize(MachineId.Current),
            GraceDays = 0,
            Edition = "Trial",
            Note = "首次安装 7 天试用",
        };

        var seen = ReadSeen();
        if (seen.HasValue && now < seen.Value.AddHours(-RollbackSkewHours))
            return new LicenseState
            {
                Status = LicenseStatus.Tampered,
                Payload = payload,
                Message = "检测到系统时间被回拨，试用已暂停。请把系统时间调回正确值。",
            };
        if (updateSeen && (!seen.HasValue || now > seen.Value)) WriteSeen(now);

        double remaining = (expiry - now).TotalDays;
        if (remaining > 0)
        {
            int daysLeft = Math.Max(1, (int)Math.Ceiling(remaining));
            return new LicenseState
            {
                Status = LicenseStatus.Trial,
                Payload = payload,
                DaysLeft = daysLeft,
                Message = $"7 天试用中，到期 {Local(expiry)}（剩 {daysLeft} 天）。试用结束后需使用授权码激活。",
            };
        }

        return new LicenseState
        {
            Status = LicenseStatus.TrialExpired,
            Payload = payload,
            DaysLeft = (int)Math.Floor(remaining),
            Message = $"本机 7 天试用已结束（到期 {Local(expiry)}）。重复安装不会重新获得试用，请使用授权码激活。",
        };
    }

    private static string Local(DateTime utc) => utc.ToLocalTime().ToString("yyyy-MM-dd");

    // ---- 防回拨水位:多副本(文件×3 + 注册表),读取全部取最大值 ----
    // 全部副本都读不到(全新安装/被全部清除)→ 返回 null(宽容不误杀,重新建立水位)。
    private DateTime? ReadSeen()
    {
        DateTime? max = null;
        foreach (var f in _seenFiles) Consider(ReadFileTicks(f), ref max);
        Consider(ReadRegTicks(), ref max);
        return max;
    }

    // 注意:不过滤"未来值"——防回拨的判据本就是"水位比回拨后的当前时间更大"。
    // 只丢弃越界坏值(避免 new DateTime 抛异常)。副本被注入超大值只会自锁(客户无动机),损坏概率极低。
    private static void Consider(long? ticks, ref DateTime? max)
    {
        if (ticks is null || ticks < 0 || ticks > DateTime.MaxValue.Ticks) return;
        var t = new DateTime(ticks.Value, DateTimeKind.Utc);
        if (max is null || t > max.Value) max = t;
    }

    private void WriteSeen(DateTime utc)
    {
        // 节流:距上次落盘 < 30 分钟不写(/health 每几秒触发 Evaluate,不能每次都写一堆副本+注册表)
        if ((utc - _lastPersisted).TotalMinutes < 30 && _lastPersisted != DateTime.MinValue) return;
        _lastPersisted = utc;
        string enc = "g" + (utc.Ticks ^ Obf).ToString(CultureInfo.InvariantCulture);   // "g" 版本标记
        foreach (var f in _seenFiles) WriteFileTicks(f, enc);
        WriteRegTicks(enc);
    }

    // ---- 一次性试用起点:ProgramData/安装数据目录/注册表多副本，普通卸载与重复安装均保留 ----
    // 读取时取最早值：丢失或篡改单个副本不能把试用开始时间向后推迟。
    private DateTime EnsureTrialStarted()
    {
        DateTime? existing = ReadTrialStart();
        DateTime started = existing ?? _utcNow();
        WriteTrialStart(started);   // 同时修复缺失副本；始终写回最早的可信起点
        return started;
    }

    private DateTime? ReadTrialStart()
    {
        DateTime? earliest = null;
        foreach (string file in _trialFiles) ConsiderEarliest(ReadTrialFileTicks(file), ref earliest);
        ConsiderEarliest(ReadTrialRegTicks(), ref earliest);
        return earliest;
    }

    private static void ConsiderEarliest(long? ticks, ref DateTime? earliest)
    {
        if (ticks is null || ticks < DateTime.UnixEpoch.Ticks || ticks > DateTime.MaxValue.Ticks) return;
        var value = new DateTime(ticks.Value, DateTimeKind.Utc);
        if (earliest is null || value < earliest.Value) earliest = value;
    }

    private void WriteTrialStart(DateTime startedUtc)
    {
        string encoded = "t" + (startedUtc.Ticks ^ TrialObf).ToString(CultureInfo.InvariantCulture);
        foreach (string file in _trialFiles) WriteFileTicks(file, encoded);
        WriteTrialRegTicks(encoded);
    }

    private static long? DecodeTrial(string? source)
    {
        source = (source ?? "").Trim();
        if (source.Length < 2 || source[0] != 't') return null;
        return long.TryParse(source.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)
            ? value ^ TrialObf
            : null;
    }

    private static long? ReadTrialFileTicks(string path)
    {
        try { return File.Exists(path) ? DecodeTrial(File.ReadAllText(path)) : null; }
        catch { return null; }
    }

    // 需带 "g" 标记才采纳:旧版明文 ticks 的 .igwlic 无标记 → 忽略(水位重建,宽容不误杀,避免误解码成假的未来时间)
    private static long? Decode(string? s)
    {
        s = (s ?? "").Trim();
        if (s.Length < 2 || s[0] != 'g') return null;
        return long.TryParse(s.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v ^ Obf : null;
    }

    private static long? ReadFileTicks(string path)
    {
        try { if (File.Exists(path)) return Decode(File.ReadAllText(path)); } catch { }
        return null;
    }

    private static void WriteFileTicks(string path, string enc)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // 对已存在的隐藏文件直接 WriteAllText 会抛 UnauthorizedAccess → 先清隐藏,写完再隐藏(否则水位无法推进)
            if (File.Exists(path)) { try { File.SetAttributes(path, FileAttributes.Normal); } catch { } }
            File.WriteAllText(path, enc);
            try { File.SetAttributes(path, FileAttributes.Hidden); } catch { }
        }
        catch { }
    }

    // 注册表副本(仅 Windows;Linux 由 _seenFiles 里的额外文件副本承担):
    // HKLM(需管理员)与 HKCU 都读,取较大;写优先 HKLM,失败退 HKCU。
    private long? ReadRegTicks()
    {
        if (!_useRegistry || !OperatingSystem.IsWindows()) return null;
        long? best = null;
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var k = hive.OpenSubKey(_regSub);
                var t = Decode(k?.GetValue(_regVal) as string);
                if (t.HasValue && (best is null || t > best)) best = t;
            }
            catch { }
        }
        return best;
    }

    private void WriteRegTicks(string enc)
    {
        if (!_useRegistry || !OperatingSystem.IsWindows()) return;
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var k = hive.CreateSubKey(_regSub);
                if (k != null) { k.SetValue(_regVal, enc, RegistryValueKind.String); return; }
            }
            catch { }   // HKLM 无管理员权限 → 退 HKCU
        }
    }

    private long? ReadTrialRegTicks()
    {
        if (!_useRegistry || !OperatingSystem.IsWindows()) return null;
        DateTime? earliest = null;
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var key = hive.OpenSubKey(_regSub);
                ConsiderEarliest(DecodeTrial(key?.GetValue(_trialRegVal) as string), ref earliest);
            }
            catch { }
        }
        return earliest?.Ticks;
    }

    private void WriteTrialRegTicks(string encoded)
    {
        if (!_useRegistry || !OperatingSystem.IsWindows()) return;
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var key = hive.CreateSubKey(_regSub);
                if (key != null) { key.SetValue(_trialRegVal, encoded, RegistryValueKind.String); return; }
            }
            catch { }
        }
    }

    // 安装目录 → 短哈希(隔离不同实例的水位;大小写无关)
    private static string ShortHash(string s)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes((s ?? "").ToLowerInvariant()));
        return Convert.ToHexString(h, 0, 6);   // 12 hex
    }
}
