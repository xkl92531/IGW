using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using IGW.Licensing;

namespace IgwKeygenGui;

/// <summary>
/// IGW 发码工具(厂商内部,持私钥)图形版:签发 / 验证 / 发码台账,所有参数界面上设置。
/// 与命令行版 IgwKeygen 共用同一把私钥 tools\IgwKeygen\private.key,发出的码由软件内嵌公钥验签。
/// 用 publish.ps1 发布时私钥加密内嵌在 exe 里，启动输对管理员密码后直接可用，不用再带 private.key。
/// </summary>
public partial class MainWindow : Window
{
    private string _privPath = "";
    private ECDsa? _priv;
    private bool _keyMatches;

    public MainWindow(byte[]? embeddedKey = null)
    {
        InitializeComponent();
        ExpDate.SelectedDate = DateTime.Today.AddYears(1);
        PopulateChoices(DriversPanel, LicenseCatalog.Drivers);
        PopulateChoices(FeaturesPanel, LicenseCatalog.Features);
        Loaded += (_, _) =>
        {
            if (embeddedKey != null) LoadEmbeddedKey(embeddedKey);
            else LoadKey(ResolvePrivKey());
            RefreshLedger();
        };
    }

    private static void PopulateChoices(WrapPanel panel, IReadOnlyList<LicenseCatalog.Item> catalog)
    {
        foreach (var item in catalog)
            panel.Children.Add(new CheckBox
            {
                Content = item.Name,
                Tag = item.Id,
                IsChecked = true,
                Width = 178,
                Margin = new Thickness(0, 2, 8, 4),
            });
    }

    private static List<string> SelectedIds(WrapPanel panel) => panel.Children.OfType<CheckBox>()
        .Where(box => box.IsChecked == true)
        .Select(box => (string)box.Tag)
        .ToList();

    // 私钥查找:有效环境变量 > exe 旁 > 上溯到 tools\IgwKeygen\private.key。
    // 找不到时仍返回 exe 旁的可迁移路径，不绑定任何开发机盘符。
    private static string ResolvePrivKey()
        => KeyPathPolicy.ResolvePrivateKey(
            AppContext.BaseDirectory,
            Environment.GetEnvironmentVariable("IGW_KEYGEN_PRIVATE"));

    private void LoadEmbeddedKey(byte[] pkcs8)
    {
        // 私钥在程序里，没有文件路径；发码台账(按私钥所在目录存)记在程序旁的 issued-codes.jsonl
        _privPath = Path.Combine(AppContext.BaseDirectory, "private.key");
        KeyPathText.Text = "内嵌私钥(已用管理员密码解锁)";
        ApplyKey(() => pkcs8);
        CryptographicOperations.ZeroMemory(pkcs8);
    }

    private void LoadKey(string path)
    {
        _privPath = path;
        KeyPathText.Text = path;
        if (!File.Exists(path))
        {
            _priv?.Dispose();
            _priv = null;
            _keyMatches = false;
            SetKeyStatus(Colors.Gray, "未找到私钥——请用 publish.ps1 打包内嵌私钥的发码工具");
            return;
        }
        ApplyKey(() => Convert.FromBase64String(File.ReadAllText(path).Trim()));
    }

    private void ApplyKey(Func<byte[]> readPkcs8)
    {
        _priv?.Dispose();
        _priv = null;
        _keyMatches = false;
        try
        {
            var ec = ECDsa.Create();
            ec.ImportPkcs8PrivateKey(readPkcs8(), out _);
            _priv = ec;
            string pub = Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo());
            _keyMatches = string.Equals(pub, LicenseCode.PublicKeyB64, StringComparison.Ordinal);
            if (_keyMatches) SetKeyStatus(Color.FromRgb(0x1B, 0x7F, 0x37), "私钥已就绪(与软件内嵌公钥匹配,可发码)");
            else SetKeyStatus(Color.FromRgb(0xC0, 0x39, 0x2B), "警告:此私钥与软件内嵌公钥不匹配 → 发出的码将验不过!");
        }
        catch (Exception ex)
        {
            _priv = null;
            _keyMatches = false;
            SetKeyStatus(Color.FromRgb(0xC0, 0x39, 0x2B), "私钥读取失败:" + ex.Message);
        }
    }

    private void SetKeyStatus(Color c, string msg)
    {
        KeyDot.Fill = new SolidColorBrush(c);
        KeyStatus.Text = msg;
        KeyStatus.Foreground = new SolidColorBrush(c);
    }

    private void FillLocal_Click(object sender, RoutedEventArgs e) => MachineBox.Text = MachineId.Current;

    private void ExpMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        DaysBox.IsEnabled = ByDays.IsChecked == true;
        ExpDate.IsEnabled = ByDate.IsChecked == true;
    }

    private void Issue_Click(object sender, RoutedEventArgs e)
    {
        if (_priv == null) { Warn("尚未加载有效私钥,无法发码。"); return; }
        if (!_keyMatches && MessageBox.Show("当前私钥与软件内嵌公钥不匹配,发出的码软件会判无效。仍要继续?",
                "确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        string customer = (CustomerBox.Text ?? "").Trim();
        if (customer.Length == 0) { Warn("请填客户名。"); CustomerBox.Focus(); return; }

        string machine = MachineId.Normalize(MachineBox.Text ?? "");
        if (machine.Length != 16) { Warn("机器码应为 16 位(可带 - 分隔),请核对客户报来的机器码。"); MachineBox.Focus(); return; }

        DateTime now = DateTime.UtcNow, exp;
        if (ByDate.IsChecked == true)
        {
            if (ExpDate.SelectedDate is not DateTime d) { Warn("请选择到期日。"); return; }
            exp = d.ToUniversalTime().Date.AddDays(1).AddSeconds(-1);   // 到当天 23:59:59
        }
        else
        {
            int days = int.TryParse(DaysBox.Text, out var dd) && dd > 0 ? dd : 365;
            exp = now.AddDays(days);
        }
        if (exp <= now && MessageBox.Show("到期日已是过去——生成出来就是过期码,现场激活会被拒绝。仍要生成吗?",
                "确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        int grace = int.TryParse(GraceBox.Text, out var g) && g >= 0 ? g : 7;
        List<string> drivers = SelectedIds(DriversPanel);
        if (drivers.Count == 0) { Warn("至少选择一个可用驱动。"); return; }
        List<string> features = SelectedIds(FeaturesPanel);

        var payload = new LicensePayload
        {
            Customer = customer, Machine = machine, IssuedUtc = now, ExpiryUtc = exp,
            GraceDays = grace, Note = (NoteBox.Text ?? "").Trim(),
        };
        try
        {
            LicenseIssueScope.Apply(payload, drivers, features);
            string code = LicenseCode.Sign(payload, _priv);
            CodeBox.Text = code;
            bool selfOk = LicenseCode.Verify(code, out _);
            IssueSummary.Text = $"客户 {customer} · 机器 {machine} · 到期 {exp.ToLocalTime():yyyy-MM-dd} · 宽限 {grace} 天 · "
                + (selfOk ? "内嵌公钥自检 ✓ 通过" : "自检 ✗ 未过(私钥与内嵌公钥不匹配)")
                + $"\n驱动：{LicenseCatalog.Describe(payload.Drivers, LicenseCatalog.Drivers)}"
                + $"\n功能：{LicenseCatalog.Describe(payload.Features, LicenseCatalog.Features)}";
            // 记台账(与私钥同目录;失败静默不挡发码)
            IssueLedger.Append(_privPath, new IssueLedger.Entry
            {
                Ts = DateTime.Now, Customer = customer, Machine = machine,
                Expiry = exp.ToLocalTime().ToString("yyyy-MM-dd"), Grace = grace, Note = payload.Note, Code = code,
            });
            RefreshLedger();
            StatusText.Text = "已生成授权码(已记台账),可复制或保存发给客户。";
        }
        catch (Exception ex) { Warn("生成失败:" + ex.Message); }
    }

    // ---------- 发码台账 ----------
    private List<IssueLedger.Entry> _ledger = new();

    private void RefreshLedger()
    {
        _ledger = IssueLedger.Load(_privPath);
        ApplyLedgerFilter();
    }

    private void ApplyLedgerFilter()
    {
        string f = (LedgerFilter.Text ?? "").Trim();
        string fm = MachineId.Normalize(f);   // 机器码可带 - 输入
        LedgerGrid.ItemsSource = f.Length == 0 ? _ledger
            : _ledger.Where(e => e.Customer.Contains(f, StringComparison.OrdinalIgnoreCase)
                              || e.Note.Contains(f, StringComparison.OrdinalIgnoreCase)
                              || (fm.Length >= 4 && e.Machine.Contains(fm, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    private void RefreshLedger_Click(object sender, RoutedEventArgs e) => RefreshLedger();
    private void LedgerFilter_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => ApplyLedgerFilter();

    private void CopyLedgerCode_Click(object sender, RoutedEventArgs e)
    {
        if (LedgerGrid.SelectedItem is not IssueLedger.Entry en) { Warn("先在台账里选中一行。"); return; }
        try { Clipboard.SetText(en.Code); StatusText.Text = $"已复制 {en.Customer}({en.Machine})的授权码,可直接补发。"; } catch { }
    }

    private void OpenLedger_Click(object sender, RoutedEventArgs e)
    {
        string p = IssueLedger.PathFor(_privPath);
        if (!File.Exists(p)) { Warn("还没有台账(尚未签发过)。"); return; }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{p}\"")); } catch { }
    }

    private void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(CodeBox.Text)) return;
        try { Clipboard.SetText(CodeBox.Text); StatusText.Text = "授权码已复制到剪贴板。"; } catch { }
    }

    private void SaveCode_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(CodeBox.Text)) { Warn("还没有生成授权码。"); return; }
        string cust = SafeName((CustomerBox.Text ?? "").Trim());
        string mach = MachineId.Normalize(MachineBox.Text ?? "");
        var sfd = new SaveFileDialog { Title = "保存授权码", FileName = $"IGW授权码_{cust}_{mach}.txt", Filter = "文本 (*.txt)|*.txt|所有文件 (*.*)|*.*" };
        if (sfd.ShowDialog() != true) return;
        try { File.WriteAllText(sfd.FileName, CodeBox.Text); StatusText.Text = "已保存:" + sfd.FileName; }
        catch (Exception ex) { Warn("保存失败:" + ex.Message); }
    }

    private void Verify_Click(object sender, RoutedEventArgs e)
    {
        string code = (VerifyInput.Text ?? "").Trim();
        VerifyCard.Visibility = Visibility.Visible;
        if (code.Length == 0) { ShowVerify(false, "请先粘贴授权码。", ""); return; }
        if (LicenseCode.Verify(code, out var p) && p != null)
        {
            var nowU = DateTime.UtcNow;
            string state = nowU < p.ExpiryUtc
                ? $"在有效期内(剩 {(int)(p.ExpiryUtc - nowU).TotalDays} 天)"
                : (nowU - p.ExpiryUtc).TotalDays <= p.GraceDays
                    ? "已过期(处于宽限期,现场很快会停采集)"
                    : "已过期(宽限期已过,现场已停采集)";
            string detail = $"客户:{p.Customer}\n机器码:{p.Machine}\n签发:{p.IssuedUtc.ToLocalTime():yyyy-MM-dd}\n"
                + $"到期:{p.ExpiryUtc.ToLocalTime():yyyy-MM-dd}\n宽限:{p.GraceDays} 天\n状态:{state}"
                + (string.IsNullOrEmpty(p.Note) ? "" : $"\n备注:{p.Note}")
                + $"\n驱动:{LicenseCatalog.Describe(p.Drivers, LicenseCatalog.Drivers)}"
                + $"\n功能:{LicenseCatalog.Describe(p.Features, LicenseCatalog.Features)}";
            ShowVerify(true, "✓ 有效(签名正确)", detail);
        }
        else ShowVerify(false, "✗ 无效(签名不匹配或已损坏——可能不是本软件公钥发的,或复制不全)", "");
    }

    private void ShowVerify(bool ok, string title, string detail)
    {
        VerifyResult.Text = title;
        VerifyResult.Foreground = new SolidColorBrush(ok ? Color.FromRgb(0x1B, 0x7F, 0x37) : Color.FromRgb(0xC0, 0x39, 0x2B));
        VerifyDetail.Text = detail;
    }

    private static string SafeName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return string.IsNullOrEmpty(s) ? "客户" : s;
    }

    private void Warn(string msg) => MessageBox.Show(msg, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
}
