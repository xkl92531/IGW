using IGW.Abstractions;

namespace IGW.Drivers.Simulator;

/// <summary>Simulator 驱动:内置仿真,不连任何真实设备,按地址关键字自动生成数据。对标 KEP「Simulator」。</summary>
public sealed class SimulatorDriverFactory : IDriverFactory
{
    public const string Id = "Simulator";
    public string DriverId => Id;

    public DriverDescriptor Describe() => new()
    {
        DriverId = Id,
        DisplayName = "Simulator",
        AddressHelp = "内置仿真(不连真实设备):导入真实 DB 后给通道设「模拟模式」即可就地出数据(数值斜坡/正弦/随机、String→NK码、WString→中文、Bool 翻转,各点按名字不同)。关键字地址:RAMP/SINE/RANDOM/BOOL/R<n> 可读写寄存器。",
        ChannelSettings = Array.Empty<DriverSetting>(),
        DeviceSettings = Array.Empty<DriverSetting>(),
    };

    public IDeviceDriver CreateDevice(ChannelDef channel, DeviceDef device)
    {
        // 变化速度倍率 + 读取延迟(模拟现场):优先设备设置,回退通道设置
        string Get(string k, string def) => device.Settings.TryGetValue(k, out var dv) && !string.IsNullOrWhiteSpace(dv) ? dv
            : channel.Settings.TryGetValue(k, out var cv) && !string.IsNullOrWhiteSpace(cv) ? cv : def;
        double speed = double.TryParse(Get("simSpeed", "1"), out var sp) && sp > 0 ? sp : 1.0;
        int delay = int.TryParse(Get("simDelayMs", "0"), out var d) ? d : 0;
        return new SimulatorDeviceDriver(speed, delay);
    }
}

/// <summary>Simulator 地址解析:任意非空地址都接受(按关键字生成),无需真实寄存器。</summary>
public sealed class SimulatorAddressParser : IAddressParser
{
    public bool TryParse(string address, IgwDataType type, out TagAddress result, out string? error)
    {
        result = new TagAddress(0, 0, 1);
        error = null;
        if (string.IsNullOrWhiteSpace(address)) { error = "空地址(如 RAMP / SINE / RANDOM / BOOL / R0)"; return false; }
        return true;
    }
}
