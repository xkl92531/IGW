using System.Windows;
using System.Windows.Input;

namespace IgwKeygenGui;

/// <summary>启动时的管理员密码验证：用密码解开内嵌私钥，连错 5 次程序退出。</summary>
public partial class PasswordWindow : Window
{
    private const int MaxAttempts = 5;
    private readonly string _protectedKey;
    private int _failures;

    /// <summary>解开的私钥(PKCS#8)；取消或连错退出时为 null。</summary>
    public byte[]? PrivateKey { get; private set; }

    public PasswordWindow(string protectedKey)
    {
        InitializeComponent();
        _protectedKey = protectedKey;
    }

    private void PasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Ok_Click(sender, e);
    }

    private async void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!OkButton.IsEnabled) return;
        string password = PasswordBox.Password;
        if (password.Length == 0) { ShowError("请输入管理员密码"); return; }

        OkButton.IsEnabled = false;
        PasswordBox.IsEnabled = false;
        ShowError("正在验证…");
        // 密钥派生故意比较慢(防暴力猜密码)，放到后台免得窗口卡住
        var (ok, key) = await Task.Run(() => ProtectedPrivateKey.TryUnprotect(_protectedKey, password, out var k) ? (true, k) : (false, k));
        if (ok)
        {
            PrivateKey = key;
            DialogResult = true;
            return;
        }

        _failures++;
        if (_failures >= MaxAttempts)
        {
            MessageBox.Show($"管理员密码连续输错 {MaxAttempts} 次，程序将退出。", "密码错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            DialogResult = false;
            return;
        }
        ShowError($"密码不正确，还可以再试 {MaxAttempts - _failures} 次");
        OkButton.IsEnabled = true;
        PasswordBox.IsEnabled = true;
        PasswordBox.Clear();
        PasswordBox.Focus();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
