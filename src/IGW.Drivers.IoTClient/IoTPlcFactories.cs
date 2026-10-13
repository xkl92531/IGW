using IGW.Abstractions;
using IoTClient.Clients.PLC;
using IoTClient.Common.Enums;   // SiemensVersion
using IoTClient.Enums;          // MitsubishiVersion / EndianFormat

namespace IGW.Drivers.IoTClient;

internal static class IoTUtil
{
    public static (string host, int port) Endpoint(string endpoint, int defPort)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) throw new FormatException("设备 Endpoint 为空(应为 ip:port)");
        var parts = endpoint.Split(':');
        string host = parts[0].Trim();
        int port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : defPort;
        return (host, port);
    }
    public static string Get(Dictionary<string, string> d, string k, string def) =>
        d.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v : def;
    public static int GetInt(Dictionary<string, string> d, string k, int def) =>
        d.TryGetValue(k, out var v) && int.TryParse(v, out var n) ? n : def;
    public static EndianFormat Endian(string s) => s.ToUpperInvariant() switch
    {
        "CDAB" => EndianFormat.CDAB, "BADC" => EndianFormat.BADC, "DCBA" => EndianFormat.DCBA, _ => EndianFormat.ABCD,
    };
}

/// <summary>西门子 S7。1200/1500 按设备选择 Classic 绝对地址或 S7 Plus 符号地址；旧型号保持 Classic。</summary>
public sealed class IoTSiemensDriverFactory : IDriverFactory
{
    private readonly Func<DeviceDef, ISiemensCredentialResolver?>? _credentialResolverFactory;

    public IoTSiemensDriverFactory(Func<DeviceDef, ISiemensCredentialResolver?>? credentialResolverFactory = null) =>
        _credentialResolverFactory = credentialResolverFactory;

    public const string Id = "Siemens.S7";
    public string DriverId => Id;

    public DriverDescriptor Describe() => new()
    {
        DriverId = Id, DisplayName = "Siemens TCP/IP Ethernet",
        AddressHelp = "1200/1500 支持 DB1.DBW0 等绝对地址或 DataDb.Struct.Value 等符号地址，同一设备不能混用；200/300/400 使用 Classic 地址。",
        ChannelSettings = Array.Empty<DriverSetting>(),
        DeviceSettings = new[] { new DriverSetting { Key = "model", Label = "型号", Default = "S7-1200" } },
    };

    public IDeviceDriver CreateDevice(ChannelDef ch, DeviceDef dev)
    {
        var batching = DriverBatchingSettings.Parse(ch.Settings);
        var points = ConfiguredPoints(ch, dev).ToArray();
        SiemensHybridDeviceDriver.ValidateConfiguration(points);
        var (host, port) = IoTUtil.Endpoint(dev.Endpoint, IoTUtil.GetInt(dev.Settings, "port", 102));
        if (port is < 1 or > 65535) throw new FormatException("Siemens S7 端口必须在 1..65535 范围内");
        var ver = ParseVersion(IoTUtil.Get(dev.Settings, "model", "S7-1200"));
        byte rack = (byte)IoTUtil.GetInt(dev.Settings, "rack", 0);
        byte slot = (byte)IoTUtil.GetInt(dev.Settings, "slot", ver is SiemensVersion.S7_300 or SiemensVersion.S7_400 ? 2 : 1);
        int timeout = IoTUtil.GetInt(dev.Settings, "timeoutMs", IoTUtil.GetInt(ch.Settings, "timeoutMs", 2000));
        int retries = IoTUtil.GetInt(dev.Settings, "retries", IoTUtil.GetInt(ch.Settings, "retries", 2));
        // 按型号 PDU 大小取更优默认打包数(请求侧约 12 字节/项):S7-1500(PDU~960)能装更多,S7-1200/300(PDU~240)约19
        int defBatch = ver switch
        {
            SiemensVersion.S7_1500 => 50,
            SiemensVersion.S7_400 => 30,
            _ => 19,   // S7-1200 / S7-300 / S7-200 / S7-200Smart(PDU≈240)
        };
        int batch = IoTUtil.GetInt(dev.Settings, "batchSize", IoTUtil.GetInt(ch.Settings, "batchSize", defBatch));
        int configuredPdu = IoTUtil.GetInt(dev.Settings, "pdu", IoTUtil.GetInt(ch.Settings, "pdu", 240));
        // Request the configured PDU during S7 setup communication. The endpoint may negotiate a
        // lower value; IoTSiemensByteTransport applies that actual value after Connect. Keep 18
        // bytes for the S7 response envelope and retain smaller values as a conservative read cap.
        int requestedPdu = Math.Clamp(configuredPdu, 240, 960);
        int payload = Math.Max(1, Math.Min(configuredPdu, requestedPdu) - 18);
        var supportsPlus = ver is SiemensVersion.S7_1200 or SiemensVersion.S7_1500;
        ISiemensClassicTransport CreateClassic() => new SiemensClassicTransport(
            new IoTPlcDeviceDriver(Id, () => new TimedSiemensClient(ver, host, port, slot, rack, timeout, requestedPdu), batch, retries,
                maxPayloadBytes: payload, batchingSettings: batching));
        // 两个自适应窗口在设备级共享:熔断恢复会重置连接并重建传输对象,若窗口跟着传输走,
        // 学习到的分片大小每次都被清零,永远以初始窗口重试而收敛不到 PLC 能接受的大小。
        var plusReadWindow = new AdaptiveRequestWindow(64, 4096);
        var plusArrayWindow = new AdaptiveRequestWindow(SiemensSymbolicArrayCodec.AdaptiveInitialBatchSize, 256);
        // igw_s7plus.dll 缺失时 P/Invoke 在构造阶段即抛 DllNotFoundException，会把整台设备(含 Classic 点位)拖死。
        // 探测一次：缺 DLL 则降级为 Disabled 传输 + supportsPlus=false——Classic 地址照常工作，
        // 符号地址在点位级报「S7 Plus 不可用」而不是整台设备创建失败。
        ISiemensPlusTransport? probed = null;
        if (supportsPlus)
        {
            try { probed = new SiemensPlusTransport(readWindow: plusReadWindow, arrayWindow: plusArrayWindow); }
            catch (DllNotFoundException) { /* igw_s7plus.dll 缺失：降级 Classic-only */ }
        }
        bool plusActive = probed is not null;
        ISiemensPlusTransport CreatePlus() => plusActive
            ? new SiemensPlusTransport(readWindow: plusReadWindow, arrayWindow: plusArrayWindow)
            : new DisabledSiemensPlusTransport();
        return new SiemensHybridDeviceDriver(
            host,
            checked((ushort)port),
            TimeSpan.FromMilliseconds(timeout),
            plusActive,
            CreateClassic(),
            probed ?? (ISiemensPlusTransport)new DisabledSiemensPlusTransport(),
            _credentialResolverFactory?.Invoke(dev),
            classicFactory: CreateClassic,
            plusFactory: CreatePlus,
            configuredPoints: points);
    }

    private static IEnumerable<(string Path, TagDef Def)> ConfiguredPoints(ChannelDef channel, DeviceDef device)
    {
        string prefix = $"{channel.Name}.{device.Name}";
        foreach (var tag in device.Tags) yield return ($"{prefix}.{tag.Name}", tag);
        foreach (var point in Groups(device.Groups, prefix)) yield return point;
        static IEnumerable<(string Path, TagDef Def)> Groups(IEnumerable<TagGroupDef> groups, string parent)
        {
            foreach (var group in groups)
            {
                string path = $"{parent}.{group.Name}";
                foreach (var tag in group.Tags) yield return ($"{path}.{tag.Name}", tag);
                foreach (var point in Groups(group.SubGroups, path)) yield return point;
            }
        }
    }

    private static SiemensVersion ParseVersion(string model) => model.Replace(" ", "").ToUpperInvariant() switch
    {
        "S7-1500" => SiemensVersion.S7_1500,
        "S7-300" => SiemensVersion.S7_300,
        "S7-400" => SiemensVersion.S7_400,
        "S7-200SMART" => SiemensVersion.S7_200Smart,
        "S7-200" => SiemensVersion.S7_200,
        _ => SiemensVersion.S7_1200,
    };

    private sealed class DisabledSiemensPlusTransport : ISiemensPlusTransport
    {
        public bool IsConnected => false;
        public Task ConnectAsync(string host, ushort port, TimeSpan timeout, bool legacy, CancellationToken ct) => throw new NotSupportedException();
        public Task AuthenticateAsync(string username, string password, CancellationToken ct) => throw new NotSupportedException();
        public Task ProbeAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SiemensPlusNativeBrowseTag>> BrowseAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) => throw new NotSupportedException();
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>欧姆龙 FINS(IoTClient)。地址:D200 / W100 / H0 / CIO0 等。</summary>
public sealed class IoTOmronFinsDriverFactory : IDriverFactory
{
    public const string Id = "Omron.Fins";
    public string DriverId => Id;

    public DriverDescriptor Describe() => new()
    {
        DriverId = Id, DisplayName = "欧姆龙 FINS",
        AddressHelp = "D200(DM 区)/ W100 / H0 / CIO0 / A0;位:CIO0.1。",
        ChannelSettings = Array.Empty<DriverSetting>(),
        DeviceSettings = new[] { new DriverSetting { Key = "wordOrder", Label = "字序", Default = "ABCD" } },
    };

    public IDeviceDriver CreateDevice(ChannelDef ch, DeviceDef dev)
    {
        var (host, port) = IoTUtil.Endpoint(dev.Endpoint, 9600);
        int timeout = IoTUtil.GetInt(dev.Settings, "timeoutMs", IoTUtil.GetInt(ch.Settings, "timeoutMs", 2000));
        int retries = IoTUtil.GetInt(dev.Settings, "retries", IoTUtil.GetInt(ch.Settings, "retries", 2));
        var orderText = IoTUtil.Get(dev.Settings, "wordOrder", "ABCD");
        var endian = IoTUtil.Endian(orderText);
        return new IoTPlcDeviceDriver(Id, () => new TimedFinsClient(host, port, timeout, endian),
            readRetries: retries, registerOrder: ModbusBlockCodec.ParseOrder(orderText));
    }
}

/// <summary>三菱 MC(IoTClient)。Qna_3E(Q/L/iQ-R)或 A_1E。地址:D200 / M100 / X0 / Y0。</summary>
public sealed class IoTMitsubishiDriverFactory : IDriverFactory
{
    public const string Id = "Mitsubishi.Mc";
    public string DriverId => Id;

    public DriverDescriptor Describe() => new()
    {
        DriverId = Id, DisplayName = "三菱 MC",
        AddressHelp = "D200(数据寄存器)/ M100(继电器)/ X0 / Y0 / W0;位区直接写点号。",
        ChannelSettings = Array.Empty<DriverSetting>(),
        DeviceSettings = new[] { new DriverSetting { Key = "mcVersion", Label = "帧 (Qna_3E/A_1E)", Default = "Qna_3E" } },
    };

    public IDeviceDriver CreateDevice(ChannelDef ch, DeviceDef dev)
    {
        var (host, port) = IoTUtil.Endpoint(dev.Endpoint, 6000);
        int timeout = IoTUtil.GetInt(dev.Settings, "timeoutMs", IoTUtil.GetInt(ch.Settings, "timeoutMs", 2000));
        int retries = IoTUtil.GetInt(dev.Settings, "retries", IoTUtil.GetInt(ch.Settings, "retries", 2));
        var ver = IoTUtil.Get(dev.Settings, "mcVersion", "Qna_3E").Equals("A_1E", StringComparison.OrdinalIgnoreCase)
            ? MitsubishiVersion.A_1E : MitsubishiVersion.Qna_3E;
        return new IoTPlcDeviceDriver(Id, () => new TimedMitsubishiClient(ver, host, port, timeout),
            readRetries: retries, registerOrder: WordOrder.DCBA);
    }
}

/// <summary>Allen-Bradley(IoTClient,CIP/EtherNet-IP)。地址:标记名(如 程序变量 / 控制器标记)。</summary>
public sealed class IoTAbDriverFactory : IDriverFactory
{
    public const string Id = "AB.Eip";
    public string DriverId => Id;

    public DriverDescriptor Describe() => new()
    {
        DriverId = Id, DisplayName = "Allen-Bradley (EtherNet/IP)",
        AddressHelp = "控制器/程序标记名(如 MyTag、Program:Main.Count)。",
        ChannelSettings = Array.Empty<DriverSetting>(),
        DeviceSettings = new[] { new DriverSetting { Key = "slot", Label = "CPU 槽", Default = "0" } },
    };

    public IDeviceDriver CreateDevice(ChannelDef ch, DeviceDef dev)
    {
        var (host, port) = IoTUtil.Endpoint(dev.Endpoint, 44818);
        byte slot = (byte)IoTUtil.GetInt(dev.Settings, "slot", 0);
        int timeout = IoTUtil.GetInt(dev.Settings, "timeoutMs", IoTUtil.GetInt(ch.Settings, "timeoutMs", 2000));
        int retries = IoTUtil.GetInt(dev.Settings, "retries", IoTUtil.GetInt(ch.Settings, "retries", 2));
        return new IoTPlcDeviceDriver(Id, () => new TimedAbClient(host, port, slot, timeout), readRetries: retries);
    }
}
