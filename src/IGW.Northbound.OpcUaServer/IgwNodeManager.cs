using IGW.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Opc.Ua;
using Opc.Ua.Server;

namespace IGW.Northbound.OpcUaServer;

/// <summary>把工程 Channel→Device→Group→Tag 树映射成 OPC UA 地址空间;NodeId 为路径式(对齐 KEP)。</summary>
public sealed class IgwNodeManager : CustomNodeManager2
{
    public const string NamespaceUri = "urn:igw:opcua";

    private readonly IgwProject _project;
    private readonly IDataHub _hub;
    private readonly Func<string, object, Task<bool>> _writer;
    private readonly Func<string, ArraySlice, ArrayRuntimeValue, Task<ArrayWriteResult>> _arrayWriter;
    private readonly IPointSubscription _pushSub;   // 推送订阅:钉点即注册、解钉即反注册(计数化,同点多监控项安全)
    private readonly Dictionary<string, BaseDataVariableState> _vars = new();
    private readonly Dictionary<string, TagDef> _defs = new(StringComparer.Ordinal);
    // 通道/设备/组文件夹按路径登记(运行期增量同步时定位);键与 NodeId 字符串一致
    private readonly Dictionary<string, FolderState> _folders = new(StringComparer.Ordinal);
    // 每点当前监控项数(节点被删时按此数量释放钉点/推送;Hub 条目被重建时按此数量重新注册推送)
    private readonly Dictionary<string, int> _monitored = new(StringComparer.Ordinal);
    // 已在 Objects 下挂好正向引用的通道(运行期新建通道挂引用失败时,下次同步重试)
    private readonly HashSet<string> _linkedChannels = new(StringComparer.Ordinal);

    public IgwNodeManager(IServerInternal server, ApplicationConfiguration configuration,
        IgwProject project, IDataHub hub, Func<string, object, Task<bool>> writer,
        Func<string, ArraySlice, ArrayRuntimeValue, Task<ArrayWriteResult>> arrayWriter,
        IPointSubscription pushSub)
        : base(server, configuration, false, NullLogger.Instance, new[] { NamespaceUri })
    {
        _project = project;
        _hub = hub;
        _writer = writer;
        _arrayWriter = arrayWriter;
        _pushSub = pushSub;
    }

    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        lock (Lock)
        {
            if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var references))
                externalReferences[ObjectIds.ObjectsFolder] = references = new List<IReference>();

            // 百万点工程：各通道的节点对象并行创建(互不共享父节点)，再按顺序登记到地址空间(SDK 要求串行)。
            var channels = _project.Channels.ToArray();
            var built = new (FolderState Folder, BuildScope Scope)[channels.Length];
            Parallel.For(0, channels.Length, index =>
            {
                var ch = channels[index];
                var scope = new BuildScope();
                var chFolder = CreateFolder(null, ch.Name, ch.Name, scope);
                foreach (var dev in ch.Devices)
                {
                    string devPath = $"{ch.Name}.{dev.Name}";
                    var devFolder = CreateFolder(chFolder, devPath, dev.Name, scope);
                    foreach (var t in dev.Tags) CreateTag(devFolder, devPath, t, scope);
                    foreach (var g in dev.Groups) BuildGroup(devFolder, devPath, g, scope);
                }
                built[index] = (chFolder, scope);
            });
            foreach (var (chFolder, scope) in built)
            {
                foreach (var (id, folder) in scope.Folders) _folders.TryAdd(id, folder);
                foreach (var (id, variable) in scope.Vars)
                    if (_vars.TryAdd(id, variable)) _defs[id] = scope.Defs[id];
                chFolder.AddReference(ReferenceTypeIds.Organizes, true, ObjectIds.ObjectsFolder);
                references.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, chFolder.NodeId));
                AddPredefinedNode(SystemContext, chFolder);
                _linkedChannels.Add(chFolder.SymbolicName);
            }

            // 计算/派生标记:统一放在 _Computed 文件夹(NodeId=完整名,与 DataHub 键一致)
            if (_project.ComputedTags.Count > 0)
            {
                var cf = CreateFolder(null, "_Computed", "_Computed");
                foreach (var ct in _project.ComputedTags)
                    if (!string.IsNullOrWhiteSpace(ct.Name)) CreateComputedTag(cf, ct);
                cf.AddReference(ReferenceTypeIds.Organizes, true, ObjectIds.ObjectsFolder);
                references.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, cf.NodeId));
                AddPredefinedNode(SystemContext, cf);
            }
        }
    }

    private void CreateComputedTag(NodeState parent, IGW.Abstractions.ComputedTagDef ct)
    {
        if (string.IsNullOrWhiteSpace(ct.Name) || _vars.ContainsKey(ct.Name)) return;   // 名字为空或与现有点/计算标记冲突 → 跳过,避免 NodeId 冲突
        string disp = ct.Name.Contains('.') ? ct.Name[(ct.Name.LastIndexOf('.') + 1)..] : ct.Name;
        var v = new BaseDataVariableState(parent)
        {
            SymbolicName = disp,
            ReferenceTypeId = ReferenceTypeIds.Organizes,
            TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
            NodeId = new NodeId(ct.Name, NamespaceIndex),
            BrowseName = new QualifiedName(disp, NamespaceIndex),
            DisplayName = new LocalizedText(disp),
            Description = ct.Description ?? string.Empty,
            DataType = MapComputedType(ct.DataType),
            ValueRank = ValueRanks.Scalar,
            AccessLevel = AccessLevels.CurrentRead,
            UserAccessLevel = AccessLevels.CurrentRead,
            StatusCode = StatusCodes.BadWaitingForInitialData,
            Timestamp = DateTime.UtcNow,
        };
        v.OnSimpleReadValue = OnRead;   // 计算标记同样读直通缓存
        parent.AddChild(v);
        _vars[ct.Name] = v;
    }

    private static NodeId MapComputedType(IGW.Abstractions.IgwDataType t) => t switch
    {
        IGW.Abstractions.IgwDataType.Bool => DataTypeIds.Boolean,
        IGW.Abstractions.IgwDataType.Int16 => DataTypeIds.Int16,
        IGW.Abstractions.IgwDataType.UInt16 => DataTypeIds.UInt16,
        IGW.Abstractions.IgwDataType.Int32 => DataTypeIds.Int32,
        IGW.Abstractions.IgwDataType.UInt32 => DataTypeIds.UInt32,
        IGW.Abstractions.IgwDataType.Int64 => DataTypeIds.Int64,
        IGW.Abstractions.IgwDataType.UInt64 => DataTypeIds.UInt64,
        IGW.Abstractions.IgwDataType.Float => DataTypeIds.Float,
        _ => DataTypeIds.Double,
    };

    /// <summary>并行构建时每个通道各自的节点登记表，构建完再合并。</summary>
    private sealed class BuildScope
    {
        public readonly Dictionary<string, BaseDataVariableState> Vars = new(StringComparer.Ordinal);
        public readonly Dictionary<string, TagDef> Defs = new(StringComparer.Ordinal);
        public readonly Dictionary<string, FolderState> Folders = new(StringComparer.Ordinal);
    }

    private FolderState BuildGroup(NodeState parent, string parentPath, TagGroupDef g, BuildScope? scope = null)
    {
        string gp = $"{parentPath}.{g.Name}";
        var folder = CreateFolder(parent, gp, g.Name, scope);
        foreach (var t in g.Tags) CreateTag(folder, gp, t, scope);
        foreach (var sg in g.SubGroups) BuildGroup(folder, gp, sg, scope);
        return folder;
    }

    /// <summary>
    /// 新建点节点。scope 非空=启动时并行构建(登记到通道自己的表,之后由 PrimeAll 统一填值);
    /// scope 为空=运行期增量新建(须持 Lock):直接登记并立即带上缓存当前值,而不是停在“等待初始数据”。
    /// </summary>
    private BaseDataVariableState? CreateTag(NodeState parent, string parentPath, TagDef def, BuildScope? scope = null)
    {
        if (string.IsNullOrWhiteSpace(def.Name)) return null;
        string tagId = $"{parentPath}.{def.Name}";
        var vars = scope?.Vars ?? _vars;
        if (vars.ContainsKey(tagId)) return null;   // 同路径重复(重名组/点)→ 跳过,避免 OPC UA NodeId 冲突/静默覆盖
        var v = new BaseDataVariableState(parent)
        {
            SymbolicName = def.Name,
            ReferenceTypeId = ReferenceTypeIds.Organizes,
            TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
            NodeId = new NodeId(tagId, NamespaceIndex),
            BrowseName = new QualifiedName(def.Name, NamespaceIndex),
            DisplayName = new LocalizedText(def.Name),
            Historizing = false,
            StatusCode = StatusCodes.BadWaitingForInitialData,
            Timestamp = DateTime.UtcNow,
        };
        ApplyTagDef(v, def);
        v.OnSimpleReadValue = OnRead;   // 读请求直通缓存:未被订阅的点(不再推送)读到的也是新值
        if (scope is null && _hub.TryGet(tagId, out var current)) ApplyValue(v, current);
        parent.AddChild(v);
        vars[tagId] = v;
        (scope?.Defs ?? _defs)[tagId] = def;
        return v;
    }

    // 点定义 → 节点元数据(新建与增量同步时原地更新共用)
    private void ApplyTagDef(BaseDataVariableState v, TagDef def)
    {
        v.Description = def.Description ?? string.Empty;
        v.DataType = MapDataType(def);
        v.ValueRank = ResolveValueRank(def);
        v.ArrayDimensions = def.Array is null
            ? null
            : new ReadOnlyList<uint>(def.Array.Dimensions.Select(static d => (uint)d).ToArray());
        v.AccessLevel = MapAccess(def.Access);
        v.UserAccessLevel = MapAccess(def.Access);
        v.OnSimpleWriteValue = def.Access != TagAccess.Read ? OnWrite : null;
    }

    // ===== 运行期增量同步(界面局部保存与批量注册共用;其它设备的会话/订阅不受影响)=====

    /// <summary>
    /// 工程局部修改后只同步受影响的节点(唯一的增量入口):
    /// <list type="bullet">
    /// <item>changedDevices(新工程全名):设备子树与新定义对齐——同名同路径的点/组节点原地保留(元数据按新定义更新,
    /// 其上的监控项不中断),多余的节点删除(释放其上的钉点/推送注册),缺少的节点新建并带上缓存当前值;</item>
    /// <item>removedDevices(旧工程全名)/removedChannels(已不在新工程中的旧通道名):整棵子树删除并释放钉点/推送注册;</item>
    /// <item>新工程里还没有文件夹的通道:新建通道文件夹并挂到 Objects 下(挂失败时下次同步重试)。</item>
    /// </list>
    /// recreatedHubTagIds:本次 Hub 条目被重建(旧句柄作废,推送注册已随旧条目摘除)的点——保留节点上已有监控项时按监控项数重新注册推送。
    /// 调用方须串行调用(GatewayHost 在其应用闸门内调用)。
    /// </summary>
    public bool SyncDevices(IgwProject project, IReadOnlyCollection<string> changedDevices,
        IReadOnlyCollection<string> removedDevices, IReadOnlyCollection<string>? removedChannels = null,
        IReadOnlyCollection<string>? recreatedHubTagIds = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        var changed = changedDevices as IReadOnlySet<string> ?? changedDevices.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var addedRoots = new List<(string Channel, IReference Reference)>();
        var removedRoots = new List<IReference>();
        var rebind = new List<(string TagId, int Count)>();
        var released = new List<(string TagId, int Count)>();
        lock (Lock)
        {
            // 1) 已删除/改名的旧设备与旧通道:整棵子树删除(改名的新名字在下面按新增处理)
            foreach (string oldDevice in removedDevices)
                if (!changed.Contains(oldDevice) && _folders.TryGetValue(oldDevice, out var deviceFolder))
                    RemoveSubtree(deviceFolder, released);
            var currentChannels = project.Channels.Select(channel => channel.Name).ToHashSet(StringComparer.Ordinal);
            foreach (string oldChannel in removedChannels ?? Array.Empty<string>())
            {
                if (currentChannels.Contains(oldChannel) || !_folders.TryGetValue(oldChannel, out var root)) continue;
                if (_linkedChannels.Remove(oldChannel))
                    removedRoots.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, root.NodeId));
                RemoveSubtree(root, released);
            }

            // 2) 新通道文件夹(空文件夹先登记,设备子树随后挂入)
            foreach (var channel in project.Channels)
            {
                if (!_folders.TryGetValue(channel.Name, out var chFolder))
                {
                    chFolder = CreateFolder(null, channel.Name, channel.Name);
                    chFolder.AddReference(ReferenceTypeIds.Organizes, true, ObjectIds.ObjectsFolder);
                    AddPredefinedNode(SystemContext, chFolder);
                }
                if (!_linkedChannels.Contains(channel.Name))
                    addedRoots.Add((channel.Name, new NodeStateReference(ReferenceTypeIds.Organizes, false, chFolder.NodeId)));
            }

            // 3) 变化设备:与新定义对齐
            foreach (var channel in project.Channels)
                foreach (var device in channel.Devices)
                {
                    string devPath = $"{channel.Name}.{device.Name}";
                    if (!changed.Contains(devPath)) continue;
                    if (_folders.TryGetValue(devPath, out var devFolder))
                    {
                        MergeFolder(devFolder, devPath, device.Tags, device.Groups, released);
                        continue;
                    }
                    var chFolder = _folders[channel.Name];
                    devFolder = CreateFolder(chFolder, devPath, device.Name);
                    foreach (var t in device.Tags) CreateTag(devFolder, devPath, t);
                    foreach (var g in device.Groups) BuildGroup(devFolder, devPath, g);
                    AddPredefinedNode(SystemContext, devFolder);
                }

            if (recreatedHubTagIds is not null)
                foreach (var tagId in recreatedHubTagIds)
                    if (_vars.ContainsKey(tagId) && _monitored.TryGetValue(tagId, out int n) && n > 0) rebind.Add((tagId, n));
        }

        // 被删节点上的监控项:钉点/推送注册在此释放(之后客户端删这些死监控项时不再重复释放,见 IsLiveVariable)。
        // 点已从 Hub 注销时推送注册已由 DataHub.Unregister 摘除,按名 Remove 为 no-op;钉点按名计数,Unpin 同时释放设备需求。
        foreach (var (tagId, count) in released)
            for (int i = 0; i < count; i++) { _hub.Unpin(tagId); _pushSub.Remove(tagId); }

        // Hub 条目重建(停设备超时回退):旧条目上的注册已由 DataHub.Unregister 摘除,按监控项数在新条目上重新注册
        foreach (var (tagId, count) in rebind)
            for (int i = 0; i < count; i++) _pushSub.Add(tagId);

        // Objects 文件夹属于核心节点管理器，在本管理器的锁之外修改它的引用，避免跨管理器加锁。
        // 放在最后:即使抛出,上面的释放/重注册也已完成;挂成功才登记,失败则下次同步重试。
        if (removedRoots.Count > 0)
            Server.NodeManager.DeleteReferencesAsync(ObjectIds.ObjectsFolder, removedRoots, CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
        if (addedRoots.Count > 0)
        {
            Server.NodeManager.AddReferencesAsync(ObjectIds.ObjectsFolder, addedRoots.Select(root => root.Reference).ToList(),
                CancellationToken.None).AsTask().GetAwaiter().GetResult();
            lock (Lock)
                foreach (var (channel, _) in addedRoots) _linkedChannels.Add(channel);
        }
        return true;
    }

    // 须持 Lock:把 folder 的直接子节点与新定义对齐(保留同名同类节点、删多余、建缺失),组递归
    private void MergeFolder(FolderState folder, string path, List<TagDef> tags, List<TagGroupDef> groups,
        List<(string TagId, int Count)> released)
    {
        var wantTags = new Dictionary<string, TagDef>(StringComparer.Ordinal);
        foreach (var t in tags)
            if (!string.IsNullOrWhiteSpace(t.Name)) wantTags.TryAdd(t.Name, t);   // 重名取第一个(与 CreateTag 跳过重复一致)
        var wantGroups = new Dictionary<string, TagGroupDef>(StringComparer.Ordinal);
        foreach (var g in groups)
            if (!string.IsNullOrWhiteSpace(g.Name) && !wantTags.ContainsKey(g.Name)) wantGroups.TryAdd(g.Name, g);

        var children = new List<BaseInstanceState>();
        folder.GetChildren(SystemContext, children);
        var keptTags = new HashSet<string>(StringComparer.Ordinal);
        var keptGroups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var child in children)
        {
            string name = child.BrowseName?.Name ?? string.Empty;
            string id = (child.NodeId?.Identifier as string) ?? string.Empty;
            string expected = $"{path}.{name}";
            if (child is BaseDataVariableState v && id == expected && wantTags.TryGetValue(name, out var def)
                && ReferenceEquals(_vars.GetValueOrDefault(id), v))
            {
                var oldType = v.DataType;
                int oldRank = v.ValueRank;
                ApplyTagDef(v, def);
                _defs[id] = def;
                // 类型/维度变了:先换上 Hub 当前值再发布变更,避免把旧类型缓存值推给监控项
                if (_hub.TryGet(id, out var dv)) ApplyValue(v, dv);
                else if (oldType != v.DataType || oldRank != v.ValueRank)
                {
                    v.Value = null;
                    v.StatusCode = StatusCodes.BadWaitingForInitialData;
                    v.Timestamp = DateTime.UtcNow;
                }
                v.ClearChangeMasks(SystemContext, false);
                keptTags.Add(name);
            }
            else if (child is FolderState f && id == expected && wantGroups.TryGetValue(name, out var g))
            {
                MergeFolder(f, expected, g.Tags, g.SubGroups, released);
                keptGroups.Add(name);
            }
            else
            {
                RemoveSubtree(child, released);
            }
        }

        foreach (var t in tags)
        {
            if (string.IsNullOrWhiteSpace(t.Name) || keptTags.Contains(t.Name)) continue;
            if (CreateTag(folder, path, t) is { } created) AddPredefinedNode(SystemContext, created);
        }
        foreach (var g in groups)
        {
            if (!wantGroups.TryGetValue(g.Name ?? string.Empty, out var first) || !ReferenceEquals(first, g)) continue;
            if (!keptGroups.Add(g.Name!)) continue;
            AddPredefinedNode(SystemContext, BuildGroup(folder, path, g));
        }
    }

    // 须持 Lock:删除一个节点及其后代,同步清掉本地索引(_vars/_defs/_folders/_monitored);
    // 带监控项的变量先发 BadNodeIdUnknown,并把待释放的钉点/推送注册数记入 released(锁外释放)
    //
    // 不调 SDK 的 DeleteNode：它递归时把子节点逐个从父节点子列表头部删掉(List 每删一个整体前移一次)，
    // 一个文件夹下的点数是 n 就是 O(n²)——现场删一个百万点通道，保存接口几十分钟不返回，刷新后通道还在。
    // 这里按 SDK RemovePredefinedNode 的语义逐个下线节点(移出预定义节点表、发 Deleted 通知、
    // 收集指向其它节点管理器的引用)，但只把子树根从父节点摘一次；子树内部的父子链接随整棵子树丢弃。
    private void RemoveSubtree(BaseInstanceState node, List<(string TagId, int Count)> released)
    {
        var externalReferences = new List<LocalReference>();
        var references = new List<IReference>();
        var stack = new Stack<BaseInstanceState>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n.NodeId?.Identifier is string id)
            {
                if (n is BaseDataVariableState v && _vars.TryGetValue(id, out var cur) && ReferenceEquals(cur, v))
                {
                    _vars.Remove(id);
                    _defs.Remove(id);
                    if (_monitored.Remove(id, out int count) && count > 0) released.Add((id, count));
                    v.Value = null;
                    v.StatusCode = StatusCodes.BadNodeIdUnknown;
                    v.Timestamp = DateTime.UtcNow;
                    v.ClearChangeMasks(SystemContext, false);   // 已有监控项收到一条 Bad 通知
                }
                else if (n is FolderState f && _folders.TryGetValue(id, out var curF) && ReferenceEquals(curF, f))
                {
                    _folders.Remove(id);
                }
            }
            // 与 SDK RemovePredefinedNode 相同：移出预定义节点表、发 Deleted 通知、收集跨节点管理器的引用
            if (n.NodeId is not null && PredefinedNodes.TryRemove(n.NodeId, out _))
            {
                n.UpdateChangeMasks(NodeStateChangeMasks.Deleted);
                n.ClearChangeMasks(SystemContext, false);
                OnNodeRemoved(n);
                references.Clear();
                n.GetReferences(SystemContext, references);
                foreach (var reference in references)
                    if (!reference.TargetId.IsAbsolute)
                        externalReferences.Add(new LocalReference(
                            (NodeId)reference.TargetId, reference.ReferenceTypeId, !reference.IsInverse, n.NodeId));
            }
            var kids = new List<BaseInstanceState>();
            n.GetChildren(SystemContext, kids);
            foreach (var k in kids) stack.Push(k);
        }
        node.Parent?.RemoveChild(node);            // 只摘子树根：父文件夹浏览不再列出
        RemoveRootNotifier(node);
        if (externalReferences.Count > 0) Server.NodeManager.RemoveReferences(externalReferences);
    }

    // UA 读 → 直通 DataHub 取最新值(推送只服务被订阅的点;普通读走这里,永远新鲜)
    private ServiceResult OnRead(ISystemContext context, NodeState node, ref object value)
    {
        string tagId = (node.NodeId.Identifier as string) ?? string.Empty;
        if (_hub.TryGet(tagId, out var dv))
        {
            _hub.Touch(tagId);   // UA 读也算客户端引用(active 扫描模式下保持轮询)
            value = dv.Value is ArrayRuntimeValue array
                ? IgwOpcUaArrayCodec.Encode(array)
                : dv.Value ?? Variant.Null;
            if (node is BaseVariableState bv)
            {
                bv.StatusCode = (Opc.Ua.StatusCode)dv.Quality.Code;
                bv.Timestamp = dv.SourceTimestamp == default ? DateTime.UtcNow : dv.SourceTimestamp;
            }
        }
        return ServiceResult.Good;
    }

    private FolderState CreateFolder(NodeState? parent, string id, string name, BuildScope? scope = null)
    {
        var folder = new FolderState(parent)
        {
            SymbolicName = name,
            ReferenceTypeId = ReferenceTypeIds.Organizes,
            TypeDefinitionId = ObjectTypeIds.FolderType,
            NodeId = new NodeId(id, NamespaceIndex),
            BrowseName = new QualifiedName(name, NamespaceIndex),
            DisplayName = new LocalizedText(name),
            EventNotifier = EventNotifiers.None,
        };
        parent?.AddChild(folder);
        (scope?.Folders ?? _folders).TryAdd(id, folder);   // 重名组(同 NodeId)保留第一个,与点的"重复跳过"一致
        return folder;
    }

    private ServiceResult OnWrite(ISystemContext context, NodeState node, ref object value)
    {
        string tagId = (node.NodeId.Identifier as string) ?? string.Empty;
        object val = value;
        try
        {
            TagDef? definition;
            lock (Lock) _defs.TryGetValue(tagId, out definition);
            if (definition?.Array is { } array)
            {
                var decoded = IgwOpcUaArrayCodec.Decode(val, array);
                var slice = new ArraySlice(array.LowerBounds, array.Dimensions);
                var result = _arrayWriter(tagId, slice, decoded).GetAwaiter().GetResult();
                return result.Failures.Count == 0
                    ? ServiceResult.Good
                    : new ServiceResult((Opc.Ua.StatusCode)result.Status.Code);
            }
            bool ok = _writer(tagId, val).GetAwaiter().GetResult();
            return ok ? ServiceResult.Good : new ServiceResult(StatusCodes.BadNotWritable);
        }
        catch (Exception ex)
        {
            return new ServiceResult(StatusCodes.BadUnexpectedError, ex);
        }
    }

    // ===== active-tag:DCS 在某节点建立/删除监控项 → 钉住/取消钉住该点(订阅中始终视为活动)=====
    protected override void OnMonitoredItemCreated(ServerSystemContext context, NodeHandle handle, ISampledDataChangeMonitoredItem monitoredItem)
    {
        base.OnMonitoredItemCreated(context, handle, monitoredItem);
        if (handle?.NodeId?.Identifier is string tagId)
        {
            lock (Lock)
            {
                // 与 Deleted 对称:非当前活节点(已被子树替换删除)不钉点、不注册推送、不计数
                if (handle.Node is BaseDataVariableState && !IsLiveVariable(tagId, handle.Node)) return;
                if (IsLiveVariable(tagId, handle.Node)) _monitored[tagId] = _monitored.GetValueOrDefault(tagId) + 1;
            }
            _hub.Pin(tagId);
            _pushSub.Add(tagId);   // 订阅倒转:钉点即注册推送(计数化,与 Pin 同生命周期)
            if (_hub.TryGet(tagId, out var dv)) PushValue(tagId, dv);   // 订阅建立瞬间推一次当前值(未订阅期不推送,节点值可能旧)
        }
    }

    protected override void OnMonitoredItemDeleted(ServerSystemContext context, NodeHandle handle, ISampledDataChangeMonitoredItem monitoredItem)
    {
        base.OnMonitoredItemDeleted(context, handle, monitoredItem);
        if (handle?.NodeId?.Identifier is string tagId)
        {
            lock (Lock)
            {
                // 已被子树替换删除的变量:钉点/推送注册在删除时已释放,这里不能重复释放(同名新点的注册会被误摘)
                if (handle.Node is BaseDataVariableState && !IsLiveVariable(tagId, handle.Node)) return;
                if (_monitored.TryGetValue(tagId, out int n))
                {
                    if (n > 1) _monitored[tagId] = n - 1; else _monitored.Remove(tagId);
                }
            }
            _hub.Unpin(tagId);
            _pushSub.Remove(tagId);
        }
    }

    // 须持 Lock:该节点是否仍是地址空间里此 tagId 的当前变量
    private bool IsLiveVariable(string tagId, NodeState? node) =>
        node is BaseDataVariableState && _vars.TryGetValue(tagId, out var v) && ReferenceEquals(v, node);

    /// <summary>把缓存值推到对应节点(由 DataHub 订阅回调触发)。</summary>
    public void PushValue(string tagId, in IGW.Abstractions.DataValue dv)
    {
        lock (Lock)
        {
            if (!_vars.TryGetValue(tagId, out var v)) return;   // 查表在锁内:子树替换会在运行期增删 _vars
            ApplyValue(v, dv);
            v.ClearChangeMasks(SystemContext, false);
        }
    }

    private static void ApplyValue(BaseDataVariableState v, in IGW.Abstractions.DataValue dv)
    {
        v.Value = dv.Value is ArrayRuntimeValue array ? IgwOpcUaArrayCodec.Encode(array) : dv.Value;
        v.StatusCode = (Opc.Ua.StatusCode)dv.Quality.Code;
        v.Timestamp = dv.SourceTimestamp == default ? DateTime.UtcNow : dv.SourceTimestamp;
    }

    /// <summary>
    /// 服务启动后把缓存里的当前值填入全部节点。此时还没有客户端订阅，不必逐点加锁和发变化通知：
    /// 整体加一次锁直接写值(百万点从近 1 秒降到约 0.2 秒)。
    /// </summary>
    public void PrimeAll()
    {
        var now = DateTime.UtcNow;
        lock (Lock)
        {
            foreach (var (tagId, variable) in _vars)
            {
                if (!_hub.TryGet(tagId, out var dv)) continue;
                variable.Value = dv.Value is ArrayRuntimeValue array ? IgwOpcUaArrayCodec.Encode(array) : dv.Value;
                variable.StatusCode = (Opc.Ua.StatusCode)dv.Quality.Code;
                variable.Timestamp = dv.SourceTimestamp == default ? now : dv.SourceTimestamp;
            }
        }
    }

    public void RefreshDescriptions()
    {
        lock (Lock)
        {
            foreach (var (tagId, variable) in _vars)
            {
                if (!_hub.TryGetDef(tagId, out var definition)) continue;
                string description = definition.Description ?? string.Empty;
                if (variable.Description?.Text == description) continue;
                variable.Description = new LocalizedText(description);
                variable.ClearChangeMasks(SystemContext, false);
            }
        }
    }

    private static byte MapAccess(TagAccess a) => a switch
    {
        TagAccess.Read => AccessLevels.CurrentRead,
        TagAccess.Write => AccessLevels.CurrentWrite,
        _ => AccessLevels.CurrentReadOrWrite,
    };

    public static int ResolveValueRank(TagDef def) =>
        def.Array?.Shape.Rank ?? (def.IsArray ? ValueRanks.OneDimension : ValueRanks.Scalar);

    private static NodeId MapDataType(TagDef def)
    {
        if (def.Scaling is { Enabled: true }) return DataTypeIds.Double; // 缩放后 eng 值为 double
        return def.DataType switch
        {
            var semantic when SiemensValueCodec.IsText(semantic) => DataTypeIds.String,
            IgwDataType.SInt or IgwDataType.Char => DataTypeIds.SByte,
            IgwDataType.Word or IgwDataType.Timer or IgwDataType.Counter => DataTypeIds.UInt16,
            IgwDataType.DWord => DataTypeIds.UInt32,
            IgwDataType.LWord => DataTypeIds.UInt64,
            IgwDataType.Bool => DataTypeIds.Boolean,
            IgwDataType.Byte => DataTypeIds.Byte,
            IgwDataType.Int16 => DataTypeIds.Int16,
            IgwDataType.UInt16 => DataTypeIds.UInt16,
            IgwDataType.Int32 => DataTypeIds.Int32,
            IgwDataType.UInt32 => DataTypeIds.UInt32,
            IgwDataType.Int64 => DataTypeIds.Int64,
            IgwDataType.UInt64 => DataTypeIds.UInt64,
            IgwDataType.Float => DataTypeIds.Float,
            IgwDataType.Double => DataTypeIds.Double,
            IgwDataType.String or IgwDataType.WString => DataTypeIds.String,
            _ => DataTypeIds.BaseDataType,
        };
    }
}
