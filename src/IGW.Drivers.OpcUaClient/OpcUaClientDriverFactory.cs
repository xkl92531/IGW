using IGW.Abstractions;

namespace IGW.Drivers.OpcUaClient;

/// <summary>OPC UA Client 驱动工厂:连接远程 OPC UA 服务器,按 NodeId 读写。</summary>
public sealed class OpcUaClientDriverFactory : IDriverFactory
{
    public const string Id = "OpcUa.Client";
    public const string RequestBudgetKey = "requestBudgetMs";
    /// <summary>UDT 数组的成员点是否整读：一次读回整个数组，再拆回各点位，默认开启。</summary>
    public const string StructArrayReadKey = "structArrayReads";
    public string DriverId => Id;

    public DriverDescriptor Describe() => new()
    {
        DriverId = Id,
        DisplayName = "OPC UA Client",
        AddressHelp = "OPC UA NodeId:ns=<命名空间索引>;s=<字符串> 或 ns=2;i=<数字>。例:ns=2;s=PLC1.Dev1.DB1.Ramp",
        ChannelSettings = new[]
        {
            new DriverSetting { Key = "url", Label = "上游 UA 服务器地址", Default = "" },
            new DriverSetting { Key = "securityPolicy", Label = "安全策略 (None / Basic256Sha256)", Default = "None" },
            new DriverSetting { Key = "anonymous", Label = "匿名登录 (true/false)", Default = "true" },
            new DriverSetting { Key = "username", Label = "用户名", Default = "" },
            new DriverSetting { Key = "password", Label = "密码", Default = "" },
            new DriverSetting { Key = "certificateTrust", Label = "服务器证书信任 (TrustOnFirstUse / Strict / AcceptAll)", Default = "TrustOnFirstUse" },
            new DriverSetting { Key = "timeoutMs", Label = "操作超时 (ms)", Default = "10000" },
            new DriverSetting { Key = RequestBudgetKey, Label = "单请求耗时预算 (auto=按实测自动 / 0=不限 / 毫秒)", Default = "auto" },
            new DriverSetting { Key = StructArrayReadKey, Label = "结构体数组整读 (true/false)", Default = "true" },
        },
        DeviceSettings = Array.Empty<DriverSetting>(),
    };

    /// <summary>设备端点 = 远程 opc.tcp 地址(如 opc.tcp://192.168.1.10:4840)。</summary>
    public IDeviceDriver CreateDevice(ChannelDef channel, DeviceDef device)
    {
        string url = ResolveUrl(channel, device);

        int timeout = GetInt(channel.Settings, "timeoutMs", 10000);
        string sec = channel.Settings.GetValueOrDefault("securityPolicy", "None");
        bool anon = !string.Equals(channel.Settings.GetValueOrDefault("anonymous", "true"), "false", StringComparison.OrdinalIgnoreCase);
        string user = channel.Settings.GetValueOrDefault("username", "");
        string pass = channel.Settings.GetValueOrDefault("password", "");
        string certificateTrust = channel.Settings.GetValueOrDefault("certificateTrust", "TrustOnFirstUse");
        var budget = RequestBudget.Parse(channel.Settings.GetValueOrDefault(RequestBudgetKey));
        bool structArrays = !string.Equals(
            channel.Settings.GetValueOrDefault(StructArrayReadKey, "true"), "false", StringComparison.OrdinalIgnoreCase);
        return new OpcUaClientDeviceDriver(url, timeout, anon, user, pass, sec, certificateTrust, budget, structArrays);
    }

    public static string ResolveUrl(ChannelDef channel, DeviceDef device)
    {
        string url = channel.Settings.GetValueOrDefault("url", "").Trim();
        if (url.Length == 0) url = device.Endpoint.Trim();
        if (url.Length == 0)
            throw new InvalidOperationException("OPC UA Client 通道未配置上游服务器地址，已拒绝回退到 IGW 本机 OPC UA 服务。");

        url = url.Contains("://", StringComparison.Ordinal) ? url : "opc.tcp://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint) ||
            !string.Equals(endpoint.Scheme, "opc.tcp", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(endpoint.Host) || endpoint.Port <= 0)
            throw new InvalidOperationException($"OPC UA Client 上游服务器地址无效：{url}");
        return endpoint.ToString().TrimEnd('/');
    }

    private static int GetInt(Dictionary<string, string> d, string key, int def) =>
        d.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : def;
}
