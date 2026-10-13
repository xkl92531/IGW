#nullable enable
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Opc.Ua;
using Opc.Ua.Server;
using PlcSimulator.Core;

namespace PlcSimulator.Protocols;

internal sealed class OpcUaSimulationNodeManager : CustomNodeManager2
{
    private const string NamespaceUri = "urn:plcsimulator";
    private readonly IReadOnlyList<SimulationDevice> _devices;
    private readonly SimulationValueStore _store;
    // 同一 PLC 里多个标记可以指向同一个地址：它们共用一个 OPC UA 变量，协议写入同步到全部标记。
    private readonly Dictionary<NodeId, (List<SimulationTag> Tags, BaseDataVariableState Variable)> _variables = new();
    private readonly Dictionary<Guid, BaseDataVariableState> _variablesByTag = new();
    private readonly int _minimumSamplingIntervalMs;

    public OpcUaSimulationNodeManager(IServerInternal server, ApplicationConfiguration configuration, IReadOnlyList<SimulationDevice> devices, SimulationValueStore store, int minimumSamplingIntervalMs = 0)
        : base(server, configuration, false, NullLogger.Instance, new[] { NamespaceUri, OpcUaSimulationAddress.SiemensNamespaceUri })
    {
        _devices = devices; _store = store; _minimumSamplingIntervalMs = minimumSamplingIntervalMs; _store.ValueChanged += OnValueChanged;
    }

    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        lock (Lock)
        {
            if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var references)) externalReferences[ObjectIds.ObjectsFolder] = references = new List<IReference>();
            foreach (var device in _devices)
            {
                var folder = CreateFolder(null, device.Name, device.Name);
                foreach (var group in device.Groups) AddGroup(folder, group);
                folder.AddReference(ReferenceTypeIds.Organizes, true, ObjectIds.ObjectsFolder);
                references.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, folder.NodeId));
                AddPredefinedNode(SystemContext, folder);
            }
            AddStructArrays();
        }
    }

    /// <summary>
    /// 西门子把 DB 里的 UDT 数组同时暴露成“数组节点”和每个成员一个叶子节点。导入的现场工程只有叶子节点，
    /// 这里按地址 <c>前缀[索引]."成员"</c> 还原出数组节点和它的结构定义，客户端就能一次读回整个数组。
    /// </summary>
    private void AddStructArrays()
    {
        ushort siemens = NamespaceIndexes[1];
        var byArray = new Dictionary<string, SortedDictionary<int, Dictionary<string, SimulationTag>>>(StringComparer.Ordinal);
        foreach (var entry in _variables.Values)
        foreach (var tag in entry.Tags)
        {
            if (!OpcUaSimulationAddress.TryParseSiemens(tag.ProtocolAddress, out ushort ns, out string identifier) || ns != siemens) continue;
            if (!TrySplitElement(identifier, out string arrayIdentifier, out int index, out string member)) continue;
            if (!byArray.TryGetValue(arrayIdentifier, out var elements)) byArray[arrayIdentifier] = elements = new();
            if (!elements.TryGetValue(index, out var members)) elements[index] = members = new(StringComparer.Ordinal);
            members[member] = tag;
        }

        foreach (var (arrayIdentifier, elements) in byArray)
        {
            // 成员集合必须一致，才可能是同一个 UDT 数组。
            string[] fields = elements.Values.First().Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray();
            if (fields.Length == 0 || elements.Values.Any(members => members.Count != fields.Length ||
                                                                     !fields.All(members.ContainsKey))) continue;
            if (fields.Any(field => !OpcUaStructureCodec.Supports(elements.Values.First()[field].DataType)) ||
                elements.Values.Any(members => fields.Any(field => members[field].DataType != elements.Values.First()[field].DataType ||
                    !members[field].Dimensions.SequenceEqual(elements.Values.First()[field].Dimensions)))) continue;
            if (elements.Keys.Last() - elements.Keys.First() + 1 != elements.Count) continue;
            if (_variables.ContainsKey(new NodeId(arrayIdentifier, siemens))) continue;
            var dataType = new DataTypeState
            {
                NodeId = new NodeId(arrayIdentifier + "::UDT", siemens),
                BrowseName = new QualifiedName(LastSegment(arrayIdentifier) + "_UDT", siemens),
                DisplayName = LastSegment(arrayIdentifier) + "_UDT",
                SuperTypeId = DataTypeIds.Structure,
                DataTypeDefinition = new ExtensionObject(new StructureDefinition
                {
                    BaseDataType = DataTypeIds.Structure,
                    StructureType = StructureType.Structure,
                    Fields = new StructureFieldCollection(fields.Select(name => new StructureField
                    {
                        Name = name,
                        DataType = MapType(elements.Values.First()[name].DataType),
                        ValueRank = elements.Values.First()[name].IsArray ? ValueRanks.OneDimension : ValueRanks.Scalar,
                        ArrayDimensions = elements.Values.First()[name].IsArray ? elements.Values.First()[name].Dimensions.Select(x => checked((uint)x)).ToArray() : null,
                    })),
                }),
            };
            var binaryEncoding = new BaseObjectState(null)
            {
                NodeId = new NodeId(arrayIdentifier + "::UDT::DefaultBinary", siemens),
                BrowseName = BrowseNames.DefaultBinary, DisplayName = BrowseNames.DefaultBinary,
                TypeDefinitionId = ObjectTypeIds.DataTypeEncodingType,
            };
            ((StructureDefinition)dataType.DataTypeDefinition.Body).DefaultEncodingId = binaryEncoding.NodeId;
            dataType.AddReference(ReferenceTypeIds.HasSubtype, true, DataTypeIds.Structure);
            dataType.AddReference(ReferenceTypeIds.HasEncoding, false, binaryEncoding.NodeId);
            binaryEncoding.AddReference(ReferenceTypeIds.HasEncoding, true, dataType.NodeId);
            AddPredefinedNode(SystemContext, dataType);
            AddPredefinedNode(SystemContext, binaryEncoding);
            Server.TypeTree.AddSubtype(dataType.NodeId, DataTypeIds.Structure);
            Server.TypeTree.AddEncoding(dataType.NodeId, binaryEncoding.NodeId);

            var ordered = elements.ToArray();
            var variable = new BaseDataVariableState(null)
            {
                SymbolicName = LastSegment(arrayIdentifier),
                NodeId = new NodeId(arrayIdentifier, siemens),
                BrowseName = new QualifiedName(LastSegment(arrayIdentifier), siemens),
                DisplayName = LastSegment(arrayIdentifier),
                ReferenceTypeId = ReferenceTypeIds.Organizes,
                TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
                DataType = dataType.NodeId,
                ValueRank = ValueRanks.OneDimension,
                ArrayDimensions = new uint[] { (uint)ordered.Length },
                AccessLevel = AccessLevels.CurrentReadOrWrite,
                UserAccessLevel = AccessLevels.CurrentReadOrWrite,
                MinimumSamplingInterval = _minimumSamplingIntervalMs,
                StatusCode = StatusCodes.Good,
                Timestamp = DateTime.UtcNow,
            };
            var layout = new StructArray(binaryEncoding.NodeId, fields, ordered);
            variable.OnSimpleReadValue = (ISystemContext context, NodeState node, ref object value) =>
            {
                value = EncodeStructArray(layout);
                return ServiceResult.Good;
            };
            _structArrays[variable.NodeId] = layout;
            variable.Value = EncodeStructArray(layout);
            variable.OnWriteValue = OnWriteArrayValue;
            AddPredefinedNode(SystemContext, variable);
        }
    }

    private sealed record StructArray(NodeId DataType, string[] Fields,
        KeyValuePair<int, Dictionary<string, SimulationTag>>[] Elements)
    {
        public int MembersPerElement => Fields.Length;
        public int ElementCount => Elements.Length;
    }

    private readonly Dictionary<NodeId, StructArray> _structArrays = new();

    /// <summary>数组节点 → (元素数, 每元素成员数)，供服务器按数据量估算处理耗时。</summary>
    public bool TryGetStructArray(NodeId nodeId, out int elements, out int membersPerElement)
    {
        if (_structArrays.TryGetValue(nodeId, out var layout))
        {
            elements = layout.ElementCount; membersPerElement = layout.MembersPerElement; return true;
        }
        elements = 0; membersPerElement = 0; return false;
    }

    private ExtensionObject[] EncodeStructArray(StructArray layout)
    {
        var result = new ExtensionObject[layout.Elements.Length];
        for (int i = 0; i < result.Length; i++)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            foreach (string field in layout.Fields)
            {
                var tag = layout.Elements[i].Value[field];
                OpcUaStructureCodec.Write(writer, tag, _store.Read(tag.Id).Value);
            }
            result[i] = new ExtensionObject(new ExpandedNodeId(layout.DataType), stream.ToArray());
        }
        return result;
    }

    private static string LastSegment(string identifier)
    {
        int dot = identifier.LastIndexOf('.');
        return (dot >= 0 ? identifier[(dot + 1)..] : identifier).Trim('"');
    }

    /// <summary>拆 <c>"DB"."Block"."Array"[3]."Member"</c>。</summary>
    private static bool TrySplitElement(string identifier, out string arrayIdentifier, out int index, out string member)
    {
        arrayIdentifier = ""; index = 0; member = "";
        int lastDot = identifier.LastIndexOf('.');
        if (lastDot <= 0 || lastDot == identifier.Length - 1) return false;
        member = identifier[(lastDot + 1)..].Trim('"');
        if (member.Length == 0 || member.Contains('[')) return false;
        string head = identifier[..lastDot];
        if (head.Length == 0 || head[^1] != ']') return false;
        int open = head.LastIndexOf('[');
        if (open <= 0 || !int.TryParse(head.AsSpan(open + 1, head.Length - open - 2), out index)) return false;
        arrayIdentifier = head[..open];
        return arrayIdentifier.Length > 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _store.ValueChanged -= OnValueChanged;
        base.Dispose(disposing);
    }

    private void AddGroup(NodeState parent, SimulationGroup group)
    {
        var folder = CreateFolder(parent, group.SymbolPath, group.Name);
        foreach (var tag in group.Tags) AddVariable(folder, tag);
        foreach (var child in group.Groups) AddGroup(folder, child);
    }

    private FolderState CreateFolder(NodeState? parent, string id, string name)
    {
        var folder = new FolderState(parent) { SymbolicName = name, ReferenceTypeId = ReferenceTypeIds.Organizes, TypeDefinitionId = ObjectTypeIds.FolderType, NodeId = new NodeId(id, NamespaceIndex), BrowseName = new QualifiedName(name, NamespaceIndex), DisplayName = name, EventNotifier = EventNotifiers.None };
        parent?.AddChild(folder); return folder;
    }

    private NodeId NodeIdFor(SimulationTag tag)
    {
        // 西门子 S7-1500 的 OPC UA 地址(ns=3;s="DB".“成员”)原样作为节点号，网关按现场地址即可访问。
        ushort siemens = NamespaceIndexes[1];
        return OpcUaSimulationAddress.TryParseSiemens(tag.ProtocolAddress, out ushort ns, out string identifier) && ns == siemens
            ? new NodeId(identifier, siemens)
            : new NodeId(tag.SymbolPath, NamespaceIndex);
    }

    private void AddVariable(NodeState parent, SimulationTag tag)
    {
        NodeId nodeId = NodeIdFor(tag);
        if (_variables.TryGetValue(nodeId, out var shared))
        {
            shared.Tags.Add(tag);
            _variablesByTag[tag.Id] = shared.Variable;
            return;
        }
        var variable = new BaseDataVariableState(parent)
        {
            SymbolicName = tag.Name, ReferenceTypeId = ReferenceTypeIds.Organizes, TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
            NodeId = nodeId, BrowseName = new QualifiedName(tag.Name, NamespaceIndex), DisplayName = tag.Name,
            Description = tag.Description, DataType = MapType(tag.DataType), ValueRank = tag.IsArray ? ValueRanks.OneDimension : ValueRanks.Scalar,
            ArrayDimensions = tag.IsArray ? new uint[] { checked((uint)tag.Dimensions.Aggregate(1, (a, b) => checked(a * b))) } : null,
            Value = _store.Read(tag.Id).Value,
            AccessLevel = AccessLevels.CurrentReadOrWrite, UserAccessLevel = AccessLevels.CurrentReadOrWrite, StatusCode = StatusCodes.Good, Timestamp = DateTime.UtcNow,
            MinimumSamplingInterval = _minimumSamplingIntervalMs,
        };
        variable.OnSimpleReadValue = OnRead; variable.OnWriteValue = OnWriteArrayValue; parent.AddChild(variable);
        _variables[nodeId] = (new List<SimulationTag> { tag }, variable);
        _variablesByTag[tag.Id] = variable;
    }

    private ServiceResult OnRead(ISystemContext context, NodeState node, ref object value)
    {
        if (!_variables.TryGetValue(node.NodeId, out var item)) return StatusCodes.BadNodeIdUnknown;
        var current = _store.Read(item.Tags[0].Id);
        // 共用地址的标记可能分别被界面或脚本改过，读出最后写入的那一个。
        for (int index = 1; index < item.Tags.Count; index++)
        {
            var candidate = _store.Read(item.Tags[index].Id);
            if (candidate.Timestamp > current.Timestamp) current = candidate;
        }
        value = current.Value!;
        item.Variable.Timestamp = current.Timestamp.UtcDateTime; item.Variable.StatusCode = StatusCodes.Good; return ServiceResult.Good;
    }

    private ServiceResult OnWrite(ISystemContext context, NodeState node, ref object value)
    {
        if (!_variables.TryGetValue(node.NodeId, out var item)) return StatusCodes.BadNodeIdUnknown;
        try
        {
            object written = value;
            foreach (var tag in item.Tags) written = _store.Write(tag.Id, value, SimulationWriteSource.Protocol).Value!;
            value = written;
            return ServiceResult.Good;
        }
        catch (SimulationValueException ex) { return new ServiceResult(StatusCodes.BadTypeMismatch, ex); }
    }

    private ServiceResult OnWriteArrayValue(ISystemContext context, NodeState node, NumericRange range,
        QualifiedName encoding, ref object value, ref StatusCode status, ref DateTime timestamp)
    {
        object candidate = value;
        if (range != NumericRange.Empty)
        {
            object current = _structArrays.TryGetValue(node.NodeId, out var layout)
                ? EncodeStructArray(layout) : _store.Read(_variables[node.NodeId].Tags[0].Id).Value!;
            var result = range.UpdateRange(ref current, candidate);
            if (StatusCode.IsBad(result)) return result;
            candidate = current;
        }
        ServiceResult written = _structArrays.TryGetValue(node.NodeId, out var structure)
            ? WriteStructArray(structure, candidate) : OnWrite(context, node, ref candidate);
        if (ServiceResult.IsBad(written)) return written;
        value = candidate; timestamp = DateTime.UtcNow; status = StatusCodes.Good;
        return ServiceResult.Good;
    }

    private ServiceResult WriteStructArray(StructArray layout, object value)
    {
        if (value is not ExtensionObject[] items || items.Length != layout.ElementCount) return StatusCodes.BadTypeMismatch;
        var pending = new List<(SimulationTag Tag, object? Value)>();
        try
        {
            for (int i = 0; i < items.Length; i++)
            {
                if (ExpandedNodeId.ToNodeId(items[i].TypeId, Server.NamespaceUris) != layout.DataType || items[i].Body is not byte[] bytes) return StatusCodes.BadTypeMismatch;
                using var stream = new MemoryStream(bytes, writable: false);
                using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8);
                foreach (string field in layout.Fields)
                {
                    var tag = layout.Elements[i].Value[field];
                    object decoded = OpcUaStructureCodec.Read(reader, tag);
                    pending.Add((tag, SimulationValueStore.ConvertValue(tag, decoded)));
                }
                if (stream.Position != stream.Length) return StatusCodes.BadTypeMismatch;
            }
            // 完成所有元素校验之后才写入，畸形报文不会部分覆盖现有数据。
            foreach (var item in pending) _store.Write(item.Tag.Id, item.Value, SimulationWriteSource.Protocol);
            return ServiceResult.Good;
        }
        catch (Exception ex) when (ex is IOException or SimulationValueException or ArgumentException)
        { return new ServiceResult(StatusCodes.BadTypeMismatch, ex); }
    }

    private void OnValueChanged(SimulationValueChanged change)
    {
        if (!_variablesByTag.TryGetValue(change.TagId, out var variable)) return;
        lock (Lock) { variable.Value = change.Value; variable.Timestamp = change.Timestamp.UtcDateTime; variable.StatusCode = StatusCodes.Good; variable.ClearChangeMasks(SystemContext, false); }
    }

    private static NodeId MapType(SimulationDataType type) => type switch
    {
        SimulationDataType.Boolean => DataTypeIds.Boolean, SimulationDataType.Byte => DataTypeIds.Byte, SimulationDataType.SByte => DataTypeIds.SByte,
        SimulationDataType.Int16 => DataTypeIds.Int16, SimulationDataType.UInt16 => DataTypeIds.UInt16, SimulationDataType.Int32 => DataTypeIds.Int32,
        SimulationDataType.UInt32 => DataTypeIds.UInt32, SimulationDataType.Int64 => DataTypeIds.Int64, SimulationDataType.UInt64 => DataTypeIds.UInt64,
        SimulationDataType.Float32 => DataTypeIds.Float, SimulationDataType.Float64 => DataTypeIds.Double, SimulationDataType.String => DataTypeIds.String,
        SimulationDataType.DateTime => DataTypeIds.DateTime, SimulationDataType.Bytes => DataTypeIds.ByteString, _ => DataTypeIds.BaseDataType,
    };
}

public static class OpcUaSimulationAddress
{
    /// <summary>S7-1500 OPC UA 服务器的命名空间；在模拟服务器中固定注册为第 2 个自定义命名空间(索引 3)，与现场一致。</summary>
    public const string SiemensNamespaceUri = "http://www.siemens.com/simatic-s7-opcua";
    public const ushort SiemensNamespaceIndex = 3;

    public static bool TryParseSiemens(string? address, out ushort namespaceIndex, out string identifier)
    {
        namespaceIndex = 0; identifier = string.Empty;
        if (string.IsNullOrWhiteSpace(address)) return false;
        string text = address.Trim();
        if (!text.StartsWith("ns=", StringComparison.Ordinal)) return false;
        int separator = text.IndexOf(";s=", StringComparison.Ordinal);
        if (separator <= 3 || !ushort.TryParse(text.AsSpan(3, separator - 3), out namespaceIndex)) return false;
        identifier = text[(separator + 3)..];
        return identifier.Length > 0;
    }

    /// <summary>界面与测试展示用的节点号：西门子地址显示原地址，其余显示符号路径。</summary>
    public static string DisplayNodeId(SimulationTag tag) =>
        TryParseSiemens(tag.ProtocolAddress, out ushort ns, out _) && ns == SiemensNamespaceIndex ? tag.ProtocolAddress.Trim() : tag.SymbolPath;
}
