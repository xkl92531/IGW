#nullable enable
using System.Collections.ObjectModel;

namespace PlcSimulator.Core;

public sealed class SimulationProject
{
    private readonly List<SimulationDevice> _devices = new();
    public SimulationProject(string name, Guid? id = null) { Id = id ?? Guid.NewGuid(); Name = Clean(name, "未命名项目"); Devices = new ReadOnlyCollection<SimulationDevice>(_devices); }
    public Guid Id { get; }
    public string Name { get; set; }
    public IReadOnlyList<SimulationDevice> Devices { get; }
    public SimulationDevice AddDevice(string name, Guid? id = null) { var x = new SimulationDevice(this, name, id); _devices.Add(x); return x; }
    public bool RemoveDevice(SimulationDevice device) => device.Project == this && _devices.Remove(device);
    public IEnumerable<SimulationTag> EnumerateTags() => _devices.SelectMany(x => x.EnumerateTags());
    internal static string Clean(string name, string? fallback = null) => string.IsNullOrWhiteSpace(name) ? fallback ?? throw new ArgumentException("名称不能为空", nameof(name)) : name.Trim();
}

public sealed class SimulationDevice
{
    private readonly List<SimulationGroup> _groups = new();
    private readonly List<SimulationEndpoint> _endpoints = new();
    internal SimulationDevice(SimulationProject project, string name, Guid? id) { Project = project; Id = id ?? Guid.NewGuid(); Name = SimulationProject.Clean(name); Groups = new ReadOnlyCollection<SimulationGroup>(_groups); Endpoints = new ReadOnlyCollection<SimulationEndpoint>(_endpoints); }
    public Guid Id { get; }
    public string Name { get; set; }
    public SimulationProject Project { get; }
    public IReadOnlyList<SimulationGroup> Groups { get; }
    public IReadOnlyList<SimulationEndpoint> Endpoints { get; }
    public string SourceChannelName { get; set; } = string.Empty;
    public string SourceAddress { get; set; } = string.Empty;
    public SiemensSimulationProfile SiemensProfile { get; set; }
    public string ProtocolEvidence { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public SimulationGroup AddGroup(string name, Guid? id = null) { var x = new SimulationGroup(this, null, name, id); _groups.Add(x); return x; }
    public bool RemoveGroup(SimulationGroup group) => group.Device == this && group.Parent == null && _groups.Remove(group);
    public SimulationEndpoint AddEndpoint(SimulationProtocol protocol, int requestedPort, int listenPort) { var x = new SimulationEndpoint(protocol, requestedPort, listenPort); _endpoints.Add(x); return x; }
    public IEnumerable<SimulationTag> EnumerateTags() => _groups.SelectMany(x => x.EnumerateTags());
}

public enum SimulationProtocol { SiemensS7, SiemensS7Plus, OpcUa, OmronFinsTcp, OmronFinsUdp, OmronCip, Unknown }

public enum SiemensSimulationProfile { None, Classic, Plus, Hybrid }

public sealed class SimulationEndpoint
{
    internal SimulationEndpoint(SimulationProtocol protocol, int requestedPort, int listenPort) { Protocol = protocol; RequestedPort = requestedPort; ListenPort = listenPort; }
    public SimulationProtocol Protocol { get; }
    public int RequestedPort { get; }
    public int ListenPort { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class SimulationGroup
{
    private readonly List<SimulationGroup> _groups = new();
    private readonly List<SimulationTag> _tags = new();
    internal SimulationGroup(SimulationDevice device, SimulationGroup? parent, string name, Guid? id) { Device = device; Parent = parent; Id = id ?? Guid.NewGuid(); Name = SimulationProject.Clean(name); Groups = new ReadOnlyCollection<SimulationGroup>(_groups); Tags = new ReadOnlyCollection<SimulationTag>(_tags); }
    public Guid Id { get; }
    public string Name { get; set; }
    public SimulationDevice Device { get; }
    public SimulationGroup? Parent { get; }
    public IReadOnlyList<SimulationGroup> Groups { get; }
    public IReadOnlyList<SimulationTag> Tags { get; }
    public string SymbolPath => Parent == null ? $"{Device.Name}.{Name}" : $"{Parent.SymbolPath}.{Name}";
    public SimulationGroup AddGroup(string name, Guid? id = null) { var x = new SimulationGroup(Device, this, name, id); _groups.Add(x); return x; }
    public bool RemoveGroup(SimulationGroup group) => group.Device == Device && group.Parent == this && _groups.Remove(group);
    public SimulationTag AddTag(string name, SimulationDataType type, object? initialValue = null, int[]? dimensions = null, Guid? id = null) { var x = new SimulationTag(this, name, type, initialValue, dimensions, id); _tags.Add(x); return x; }
    public IEnumerable<SimulationTag> EnumerateTags() => _tags.Concat(_groups.SelectMany(x => x.EnumerateTags()));
}

public sealed class SimulationTag
{
    internal SimulationTag(SimulationGroup group, string name, SimulationDataType type, object? initialValue, int[]? dimensions, Guid? id) { Group = group; Id = id ?? Guid.NewGuid(); Name = SimulationProject.Clean(name); DataType = type; InitialValue = initialValue; Dimensions = Array.AsReadOnly(dimensions?.ToArray() ?? Array.Empty<int>()); }
    public Guid Id { get; }
    public string Name { get; set; }
    public string Description { get; set; } = string.Empty;
    public string ProtocolAddress { get; set; } = string.Empty;
    public string ClassicAddress { get; set; } = string.Empty;
    public string SymbolicAddress { get; set; } = string.Empty;
    public int? StringLength { get; set; }
    public bool IsWideString { get; set; }
    public SimulationTextEncoding TextEncoding { get; set; }
    internal SimulationTextEncoding? DetectedTextEncoding { get; set; }
    public int? ElementStrideBytes { get; set; }
    public int[] LowerBounds { get; set; } = Array.Empty<int>();
    public SimulationGroup Group { get; }
    public SimulationDataType DataType { get; }
    public object? InitialValue { get; }
    public IReadOnlyList<int> Dimensions { get; }
    public string SymbolPath => $"{Group.SymbolPath}.{Name}";
    public bool IsArray => Dimensions.Count > 0;
    public string AddressFor(SimulationProtocol protocol) => protocol switch
    {
        SimulationProtocol.SiemensS7 => string.IsNullOrWhiteSpace(ClassicAddress) ? ProtocolAddress : ClassicAddress,
        SimulationProtocol.SiemensS7Plus => string.IsNullOrWhiteSpace(SymbolicAddress) ? ProtocolAddress : SymbolicAddress,
        _ => ProtocolAddress,
    };
}
