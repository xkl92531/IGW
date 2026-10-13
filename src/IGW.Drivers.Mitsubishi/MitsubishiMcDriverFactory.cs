using IGW.Abstractions;

namespace IGW.Drivers.Mitsubishi;

/// <summary>三菱 MELSEC MC 协议驱动工厂(3E 帧二进制 / TCP,Q/L/iQ-R)。对标 KEP「Mitsubishi Ethernet」。</summary>
public sealed class MitsubishiMcDriverFactory : IDriverFactory
{
    public const string Id = "Mitsubishi.Mc";
    public string DriverId => Id;

    public DriverDescriptor Describe() => new()
    {
        DriverId = Id,
        DisplayName = "Mitsubishi Ethernet (MC 3E)",
        AddressHelp = "MC 地址:D100(数据寄存器)/ R200(文件寄存器)/ W1A(链接寄存器,hex)/ M50(内部继电器)/ X10、Y20(hex)/ B1F。需在 GX Works 里给以太网模块开 MC 协议(二进制)+ 设端口。",
        ChannelSettings = new[] { new DriverSetting { Key = "timeoutMs", Label = "超时 (ms)", Default = "2000" } },
        DeviceSettings = Array.Empty<DriverSetting>(),
    };

    /// <summary>设备端点 = PLC IP[:port](MC 协议端口在 PLC 以太网模块里配置,默认 5000)。</summary>
    public IDeviceDriver CreateDevice(ChannelDef channel, DeviceDef device)
    {
        string host = (device.Endpoint ?? "").Trim();
        int port = 5000;
        int colon = host.IndexOf(':');
        if (colon > 0)
        {
            if (int.TryParse(host[(colon + 1)..], out var p) && p > 0) port = p;
            host = host[..colon];
        }
        int timeout = channel.Settings.TryGetValue("timeoutMs", out var v) && int.TryParse(v, out var n) ? n : 2000;
        return new McTcpDeviceDriver(host, port, timeout, DriverBatchingSettings.Parse(channel.Settings));
    }
}
