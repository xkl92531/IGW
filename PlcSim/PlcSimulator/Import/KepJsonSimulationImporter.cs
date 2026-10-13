#nullable enable
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PlcSimulator.Core;

namespace PlcSimulator.Import;

public static class KepJsonSimulationImporter
{
    public static SimulationImportResult ImportFile(string path) => ImportJson(File.ReadAllText(path));

    public static SimulationImportResult ImportJson(string json)
    {
        JObject root;
        try { root = JObject.Parse(json.TrimStart('\uFEFF')); }
        catch (JsonException ex) { throw new InvalidDataException("KEP JSON 格式无效", ex); }

        JArray? channels = root["project"]?["channels"] as JArray;
        if (channels == null) throw new InvalidDataException("未找到 project.channels，不是 KEPServerEX 原生配置 JSON");

        var result = new SimulationImportResult { Project = new SimulationProject(Text(root["project"], "servermain.PROJECT_TITLE", "KEP 导入项目")), ChannelCount = channels.Count };
        IgwTypeIndex igwTypes = IgwTypeIndex.From(root["igw"]);
        var allocatedPorts = new HashSet<int>();
        foreach (JObject channel in channels.OfType<JObject>())
        {
            string channelName = Text(channel, "common.ALLTYPES_NAME", "通道");
            string channelDriver = Text(channel, "servermain.MULTIPLE_TYPES_DEVICE_DRIVER", string.Empty);
            foreach (JObject sourceDevice in (channel["devices"] as JArray ?? new JArray()).OfType<JObject>())
            {
                string name = Text(sourceDevice, "common.ALLTYPES_NAME", "设备");
                string driver = Text(sourceDevice, "servermain.MULTIPLE_TYPES_DEVICE_DRIVER", channelDriver);
                SimulationProtocol protocol = MapProtocol(driver);
                bool declaredSiemens = SiemensProtocolClassifier.IsSiemensDriver(driver);
                var addresses = EnumerateTagAddresses(sourceDevice).ToArray();
                // 独立模拟工具可同时提供多种协议。源项目可能使用 OPC UA 或通用驱动，
                // 但导出的 DB 绝对地址仍足以建立 Classic S7 内存和监听端点。
                var classicAddresses = addresses.Where(SiemensProtocolClassifier.IsMappedClassicDbAddress).ToArray();
                SiemensProtocolDetection? siemens = declaredSiemens
                    ? SiemensProtocolClassifier.Detect(driver, addresses)
                    : classicAddresses.Length > 0
                        ? SiemensProtocolClassifier.Detect("", classicAddresses)
                        : null;
                if (siemens is not null && (declaredSiemens || protocol == SimulationProtocol.Unknown))
                    protocol = siemens.Profile == SiemensSimulationProfile.Plus
                        ? SimulationProtocol.SiemensS7Plus
                        : SimulationProtocol.SiemensS7;
                if (protocol == SimulationProtocol.Unknown) result.Warnings.Add($"设备 {channelName}/{name} 的驱动 {driver} 暂不支持，节点已保留");
                int sourcePort = FindPort(sourceDevice, protocol);
                int listenPort = AllocatePort(sourcePort, allocatedPorts);
                var device = result.Project.AddDevice(name);
                device.SourceChannelName = channelName;
                device.SourceAddress = Text(sourceDevice, "servermain.DEVICE_ID_STRING", string.Empty);
                if (siemens is not null)
                {
                    device.SiemensProfile = siemens.Profile;
                    device.ProtocolEvidence = siemens.Evidence;
                }
                if (siemens?.Profile == SiemensSimulationProfile.Hybrid)
                {
                    device.AddEndpoint(SimulationProtocol.SiemensS7, sourcePort, listenPort);
                    device.AddEndpoint(SimulationProtocol.SiemensS7Plus, sourcePort, listenPort);
                }
                else device.AddEndpoint(protocol, sourcePort, listenPort);
                if (siemens is not null && protocol is not (SimulationProtocol.SiemensS7 or SimulationProtocol.SiemensS7Plus))
                    device.AddEndpoint(SimulationProtocol.SiemensS7, 102, AllocatePort(102, allocatedPorts));
                AddNodeContents(sourceDevice, device, null, result.Warnings, igwTypes);
            }
        }
        return result;
    }

    private static void AddNodeContents(JObject source, SimulationDevice device, SimulationGroup? target, List<string> warnings, IgwTypeIndex igwTypes)
    {
        foreach (JObject tagNode in (source["tags"] as JArray ?? new JArray()).OfType<JObject>()) AddTag(tagNode, target ?? EnsureRoot(device), device, warnings, igwTypes);
        foreach (JObject groupNode in (source["tag_groups"] as JArray ?? new JArray()).OfType<JObject>())
        {
            string name = Text(groupNode, "common.ALLTYPES_NAME", "组");
            SimulationGroup group = target == null ? device.AddGroup(name) : target.AddGroup(name);
            AddNodeContents(groupNode, device, group, warnings, igwTypes);
        }
    }

    private static SimulationGroup EnsureRoot(SimulationDevice device) => device.Groups.FirstOrDefault(x => x.Name == "Tags") ?? device.AddGroup("Tags");

    private static void AddTag(JObject source, SimulationGroup target, SimulationDevice device, List<string> warnings, IgwTypeIndex igwTypes)
    {
        string name = Text(source, "common.ALLTYPES_NAME", string.Empty);
        if (name.Length == 0) { warnings.Add($"组 {target.SymbolPath} 中有一个无名称标记，已跳过"); return; }
        string address = Text(source, "servermain.TAG_ADDRESS", string.Empty);
        int typeCode = source.Value<int?>("servermain.TAG_DATA_TYPE") ?? 0;
        // IGW 网关导出的文件同时带有 IGW 层：它记录了 KEP 类型码无法区分的真实类型(字符串、DTL 等)，优先采用。
        var metadata = igwTypes.Find(device.SourceChannelName, $"{target.SymbolPath}.{name}", address);
        SimulationDataType type = metadata?.Type ?? MapType(typeCode, address);
        if (type == SimulationDataType.Unknown) warnings.Add($"标记 {target.SymbolPath}.{name} 的 KEP 类型码 {typeCode} 暂不支持，节点已保留");
        int[]? dimensions = metadata?.Dimensions;
        object? initial = dimensions is { Length: > 0 }
            ? Enumerable.Repeat(InitialValue(type), dimensions.Aggregate(1, (a, b) => checked(a * b))).ToArray()
            : InitialValue(type);
        var tag = target.AddTag(name, type, initial, dimensions);
        tag.StringLength = metadata?.StringLength;
        tag.IsWideString = metadata?.IsWideString ?? false;
        tag.TextEncoding = metadata?.TextEncoding ?? SimulationTextEncoding.Auto;
        tag.ElementStrideBytes = metadata?.ElementStrideBytes;
        tag.LowerBounds = metadata?.LowerBounds ?? Array.Empty<int>();
        tag.Description = Text(source, "common.ALLTYPES_DESCRIPTION", string.Empty);
        tag.ProtocolAddress = address;
        if (device.SiemensProfile != SiemensSimulationProfile.None)
        {
            if (SiemensProtocolClassifier.IsClassicAddress(address)) tag.ClassicAddress = address;
            else tag.SymbolicAddress = address;
        }
    }

    private static SimulationProtocol MapProtocol(string driver)
    {
        string value = driver.ToLowerInvariant();
        if (value.Contains("omron") && value.Contains("fins")) return SimulationProtocol.OmronFinsTcp;
        if (value.Contains("omron") && (value.Contains("nj") || value.Contains("nx") || value.Contains("ethernet"))) return SimulationProtocol.OmronCip;
        if (value.Contains("siemens") || value.Contains("s7")) return SimulationProtocol.SiemensS7;
        if (value.Contains("opc ua")) return SimulationProtocol.OpcUa;
        return SimulationProtocol.Unknown;
    }

    private static int FindPort(JObject device, SimulationProtocol protocol)
    {
        foreach (JProperty property in device.Properties())
            if (property.Name.EndsWith("DEVICE_PORT_NUMBER", StringComparison.OrdinalIgnoreCase) && property.Value.Type == JTokenType.Integer) return property.Value.Value<int>();
        return protocol switch { SimulationProtocol.SiemensS7 or SimulationProtocol.SiemensS7Plus => 102, SimulationProtocol.OpcUa => 4840, SimulationProtocol.OmronFinsTcp or SimulationProtocol.OmronFinsUdp => 9600, SimulationProtocol.OmronCip => 44818, _ => 20000 };
    }

    private static int AllocatePort(int requested, HashSet<int> ports)
    {
        int candidate = Math.Clamp(requested, 1, 65535);
        while (!ports.Add(candidate)) { candidate++; if (candidate > 65535) candidate = 1024; }
        return candidate;
    }

    private static SimulationDataType MapType(int code, string address)
    {
        if (address.LastIndexOf('/') >= 0) return SimulationDataType.Boolean;
        return code switch { 1 => SimulationDataType.Boolean, 2 => SimulationDataType.SByte, 3 => SimulationDataType.Byte, 4 => SimulationDataType.Int16, 5 => SimulationDataType.UInt16, 6 => SimulationDataType.Int32, 7 => SimulationDataType.UInt32, 8 => SimulationDataType.Float32, 9 or 10 => SimulationDataType.Float64, 11 or 12 => SimulationDataType.String, 14 => SimulationDataType.Int64, 15 => SimulationDataType.UInt64, _ => SimulationDataType.Unknown };
    }

    /// <summary>西门子 DTL 的 12 字节大端编码：1970-01-01 00:00:00(星期四)。</summary>
    public static byte[] InitialDtl() => new byte[] { 0x07, 0xB2, 1, 1, 5, 0, 0, 0, 0, 0, 0, 0 };

    private static object? InitialValue(SimulationDataType type) => type switch
    {
        SimulationDataType.Bytes => InitialDtl(),
        SimulationDataType.DateTime => new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero),
        SimulationDataType.Boolean => false, SimulationDataType.Byte => (byte)0, SimulationDataType.SByte => (sbyte)0,
        SimulationDataType.Int16 => (short)0, SimulationDataType.UInt16 => (ushort)0, SimulationDataType.Int32 => 0,
        SimulationDataType.UInt32 => 0u, SimulationDataType.Int64 => 0L, SimulationDataType.UInt64 => 0UL,
        SimulationDataType.Float32 => 0f, SimulationDataType.Float64 => 0d, SimulationDataType.String => string.Empty, _ => null
    };

    private static IEnumerable<string> EnumerateTagAddresses(JObject source)
    {
        foreach (JObject tag in (source["tags"] as JArray ?? new JArray()).OfType<JObject>())
            yield return Text(tag, "servermain.TAG_ADDRESS", string.Empty);
        foreach (JObject group in (source["tag_groups"] as JArray ?? new JArray()).OfType<JObject>())
            foreach (string address in EnumerateTagAddresses(group))
                yield return address;
    }

    /// <summary>IGW 层(igw.entities)中的点位类型，按“通道 + 设备内路径”定位，路径缺失时按“通道 + 地址”。</summary>
    private sealed record TagType(SimulationDataType Type, int[]? Dimensions, int? StringLength,
        bool IsWideString, SimulationTextEncoding TextEncoding, int? ElementStrideBytes, int[] LowerBounds);

    private sealed class IgwTypeIndex
    {
        private readonly Dictionary<string, TagType> _byPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TagType> _byAddress = new(StringComparer.Ordinal);

        public static IgwTypeIndex From(JToken? igw)
        {
            var index = new IgwTypeIndex();
            if (igw?["entities"] is not JObject entities) return index;
            var nodes = new Dictionary<string, (string? Parent, string Name)>(StringComparer.Ordinal);
            foreach (string kind in new[] { "channels", "devices", "groups" })
                foreach (JObject entity in (entities[kind] as JArray ?? new JArray()).OfType<JObject>())
                {
                    string? id = entity.Value<string>("id");
                    if (id != null) nodes[id] = (entity.Value<string>("parentId"), entity["document"]?.Value<string>("name") ?? string.Empty);
                }
            foreach (JObject entity in (entities["tags"] as JArray ?? new JArray()).OfType<JObject>())
            {
                if (entity["document"] is not JObject document) continue;
                SimulationDataType? type = MapIgwType(document.Value<string>("dataType"));
                if (type == null) continue;
                int[]? dimensions = null;
                if (document.Value<bool?>("isArray") == true)
                {
                    dimensions = document["array"]?["dimensions"]?.ToObject<int[]>();
                    if (dimensions is { Length: > 1 }) throw new FormatException($"数组 {document.Value<string>("name")} 为多维数组，当前模拟器仅支持一维数组");
                    if (dimensions is not { Length: > 0 } || dimensions.Any(x => x <= 0) || dimensions.Aggregate(1L, (a, b) => checked(a * b)) > 1_000_000)
                        throw new FormatException($"数组 {document.Value<string>("name")} 的维度无效或超过 1000000 个元素");
                }
                int? stringLength = document.Value<int?>("stringLength") ?? document["array"]?.Value<int?>("stringLength");
                bool wide = string.Equals(document.Value<string>("dataType"), "wstring", StringComparison.OrdinalIgnoreCase);
                if (stringLength is < 0 || stringLength > (wide ? 16382 : 254))
                    throw new FormatException($"点位 {document.Value<string>("name")} 的字符串长度无效：{stringLength}");
                var lower = document["array"]?["lowerBounds"]?.ToObject<int[]>() ?? Array.Empty<int>();
                Enum.TryParse<SimulationTextEncoding>(document.Value<string>("textEncoding"), true, out var textEncoding);
                var metadata = new TagType(type.Value, dimensions, stringLength, wide, textEncoding,
                    document["array"]?.Value<int?>("elementStrideBytes"), lower);
                var path = new List<string> { document.Value<string>("name") ?? string.Empty };
                string? parent = entity.Value<string>("parentId");
                for (int depth = 0; parent != null && nodes.TryGetValue(parent, out var node) && depth < 256; depth++)
                {
                    path.Add(node.Name);
                    parent = node.Parent;
                }
                path.Reverse();
                if (path.Count < 3) continue;
                string channel = path[0];
                // 模拟工程的路径以设备开头(设备.组….标记)，与 SimulationGroup.SymbolPath 对齐。
                index._byPath[$"{channel}|{string.Join('.', path.Skip(1))}"] = metadata;
                string address = document.Value<string>("address")?.Trim() ?? string.Empty;
                if (address.Length > 0) index._byAddress.TryAdd($"{channel}|{address}", metadata);
            }
            return index;
        }

        public TagType? Find(string channel, string symbolPath, string address)
        {
            if (_byPath.TryGetValue($"{channel}|{symbolPath}", out var type)) return type;
            return _byAddress.TryGetValue($"{channel}|{address}", out type) ? type : null;
        }

        private static SimulationDataType? MapIgwType(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            return name.Trim().ToLowerInvariant() switch
            {
                "bool" => SimulationDataType.Boolean,
                "byte" => SimulationDataType.Byte,
                "char" or "sint" => SimulationDataType.SByte,
                "int16" => SimulationDataType.Int16,
                "uint16" or "word" => SimulationDataType.UInt16,
                "int32" => SimulationDataType.Int32,
                "uint32" or "dword" => SimulationDataType.UInt32,
                "int64" => SimulationDataType.Int64,
                "uint64" or "lword" => SimulationDataType.UInt64,
                "float" => SimulationDataType.Float32,
                "double" => SimulationDataType.Float64,
                "string" or "wstring" or "s7char" or "wchar" => SimulationDataType.String,
                "dtl" => SimulationDataType.Bytes,
                "datetime" => SimulationDataType.DateTime,
                _ => null,
            };
        }
    }

    private static string Text(JToken? node, string property, string fallback) => node?[property]?.Value<string>()?.Trim() is { Length: > 0 } value ? value : fallback;
}
