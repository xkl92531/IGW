using System.Windows;

namespace IgwKeygenGui;

public partial class App : Application
{
    // 程序里内嵌了加密私钥时，先输管理员密码解开才进主窗口；开发时直接编译(没内嵌)照旧找 private.key 文件。
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        byte[]? embeddedKey = null;
        string? protectedKey = ProtectedPrivateKey.LoadEmbedded();
        if (protectedKey != null)
        {
            var dialog = new PasswordWindow(protectedKey);
            if (dialog.ShowDialog() != true || dialog.PrivateKey == null)
            {
                Shutdown();
                return;
            }
            embeddedKey = dialog.PrivateKey;
        }

        var main = new MainWindow(embeddedKey);
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        main.Show();
    }
}
