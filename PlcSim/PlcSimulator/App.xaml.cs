using System.Configuration;
using System.Data;
using System.Windows;

namespace PlcSimulator;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private static Mutex _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "PlcSimulator.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("PLC 模拟器已在运行，请勿重复打开。", "提示");
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }
}

