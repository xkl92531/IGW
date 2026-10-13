using IGW.Abstractions;
using libplctag;

namespace IGW.Drivers.EtherNetIP;

/// <summary>Allen-Bradley EtherNet/IP(ControlLogix/CompactLogix/Micro800 等)。对标 KEP「Allen-Bradley ControlLogix Ethernet」。</summary>
public sealed class AbEipDriverFactory : IDriverFactory
{
    public const string Id = "AB.Eip";
    public string DriverId => Id;

    public DriverDescriptor Describe() => new()
    {
        DriverId = Id,
        DisplayName = "Allen-Bradley ControlLogix Ethernet",
        AddressHelp = "PLC 符号标记名:MyTag、Program:MainProgram.Count、MyArray[3]、MyUdt.Member。",
        ChannelSettings = new[] { new DriverSetting { Key = "timeoutMs", Label = "超时 (ms)", Default = "5000" } },
        DeviceSettings = new[]
        {
            new DriverSetting { Key = "plcType", Label = "PLC 型号 (ControlLogix/CompactLogix/Micro800/MicroLogix/SLC500/PLC5)", Default = "ControlLogix" },
            new DriverSetting { Key = "path", Label = "CPU 路径 (ControlLogix 背板槽:1,0)", Default = "1,0" },
        },
    };

    public IDeviceDriver CreateDevice(ChannelDef channel, DeviceDef device) =>
        EipShared.Create(Id, channel, device, defaultPath: "1,0", defaultPlc: "ControlLogix");
}

/// <summary>欧姆龙 NJ/NX EtherNet/IP(CIP)。对标 KEP「Omron NJ Ethernet」。</summary>
public sealed class OmronCipDriverFactory : IDriverFactory
{
    public const string Id = "Omron.Cip";
    public string DriverId => Id;

    public DriverDescriptor Describe() => new()
    {
        DriverId = Id,
        DisplayName = "Omron NJ/NX EtherNet/IP",
        AddressHelp = "NJ/NX 变量名(全局变量需在 Sysmac 里勾选『发布到网络』):MyVar、MyStruct.Member、MyArray[3]。",
        ChannelSettings = new[] { new DriverSetting { Key = "timeoutMs", Label = "超时 (ms)", Default = "5000" } },
        DeviceSettings = new[] { new DriverSetting { Key = "path", Label = "CPU 路径 (NJ/NX 一般留空)", Default = "" } },
    };

    public IDeviceDriver CreateDevice(ChannelDef channel, DeviceDef device) =>
        EipShared.Create(Id, channel, device, defaultPath: "", defaultPlc: "Omron");
}

internal static class EipShared
{
    public static IDeviceDriver Create(string driverId, ChannelDef channel, DeviceDef device, string defaultPath, string defaultPlc)
    {
        string host = (device.Endpoint ?? "").Trim();
        int colon = host.IndexOf(':');
        if (colon > 0) host = host[..colon]; // 去掉端口,EtherNet/IP 固定 44818
        string path = device.Settings.GetValueOrDefault("path", defaultPath);
        var plc = ParsePlc(device.Settings.GetValueOrDefault("plcType", defaultPlc));
        int timeout = GetInt(channel.Settings, "timeoutMs", 5000);
        int arrayChunkElements = GetInt(device.Settings, "arrayChunkElements", 128);
        return new EtherNetIpDeviceDriver(driverId, host, string.IsNullOrWhiteSpace(path) ? null : path, plc,
            timeout, arrayChunkElements);
    }

    private static PlcType ParsePlc(string s) => s.Trim().ToLowerInvariant() switch
    {
        "controllogix" or "compactlogix" or "logix" => PlcType.ControlLogix,
        "micro800" or "micro850" => PlcType.Micro800,
        "micrologix" => PlcType.MicroLogix,
        "slc500" or "slc" => PlcType.Slc500,
        "plc5" => PlcType.Plc5,
        "omron" or "nj" or "nx" or "omron-njnx" => PlcType.Omron,
        _ => PlcType.ControlLogix,
    };

    private static int GetInt(Dictionary<string, string> d, string key, int def) =>
        d.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : def;
}
