using System.ServiceProcess;

namespace IGW.DataMover;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // 可带参数指定数据目录(测试或非默认安装)；默认就是网关和安装包固定使用的 C:\ProgramData\IGW
        string data = args.Length > 0 ? args[0] : DataDirectoryMover.DefaultDataDirectory;
        Application.Run(new MainForm(data, new WindowsGatewayService()));
    }
}

/// <summary>网关的 Windows 服务(名称与安装包、Runtime 里的 UseWindowsService 一致)。</summary>
internal sealed class WindowsGatewayService : IGatewayService
{
    private const string ServiceName = "IGW Gateway";

    public bool Exists => ServiceController.GetServices().Any(item =>
        string.Equals(item.ServiceName, ServiceName, StringComparison.OrdinalIgnoreCase));

    public bool IsRunning
    {
        get
        {
            if (!Exists) return false;
            using var service = new ServiceController(ServiceName);
            return service.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending;
        }
    }

    public void Stop(TimeSpan timeout)
    {
        using var service = new ServiceController(ServiceName);
        if (service.Status is ServiceControllerStatus.Stopped) return;
        if (service.Status != ServiceControllerStatus.StopPending) service.Stop();
        service.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
        // 服务停了但进程还在收尾时，文件句柄可能没放：给几秒让它退出
        for (int i = 0; i < 20 && System.Diagnostics.Process.GetProcessesByName("IGW.Runtime").Length > 0; i++)
            Thread.Sleep(500);
    }

    public void Start(TimeSpan timeout)
    {
        using var service = new ServiceController(ServiceName);
        if (service.Status is ServiceControllerStatus.Running) return;
        service.Start();
        service.WaitForStatus(ServiceControllerStatus.Running, timeout);
    }
}
