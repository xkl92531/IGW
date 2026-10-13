using IGW.Abstractions;

namespace IGW.Drivers.OpcUaClient;

public sealed record OpcUaBrowseChild(string Id, string Name, bool IsVariable, bool IsProperty = false);
public sealed record OpcUaBrowseType(IgwDataType DataType, bool IsArray,
    IReadOnlyList<int>? Dimensions = null, IReadOnlyList<int>? LowerBounds = null,
    StructDef? Structure = null, TagAccess Access = TagAccess.Read,
    int? ValueRank = null, string? DataTypeNodeId = null, int? StringLength = null, string? DataTypeName = null, int? PhysicalValueRank = null, IReadOnlyList<int>? PhysicalDimensions = null);

public enum OpcUaBrowseProgressStage
{
    Starting,
    Scanning,
    Completed,
    Failed,
}

public sealed record OpcUaBrowseProgress(
    OpcUaBrowseProgressStage Stage,
    string CurrentNodeId,
    string Path,
    int Depth,
    int ScannedNodeCount,
    int DiscoveredTagCount,
    long ElapsedMilliseconds,
    string? Error = null);

public interface IOpcUaBrowseSource
{
    IReadOnlyList<OpcUaBrowseChild> Children(string nodeId);
    IReadOnlyDictionary<string, IReadOnlyList<OpcUaBrowseChild>> ChildrenOf(IReadOnlyList<string> nodeIds);
    OpcUaBrowseType TypeOf(string nodeId);
    IReadOnlyDictionary<string, OpcUaBrowseType> TypesOf(IReadOnlyList<string> nodeIds);
}

/// <summary>Optional metadata-only probe for logical scalar variables that expose components.</summary>
public interface IOpcUaAtomicBrowseSource
{
    IReadOnlyDictionary<string, OpcUaBrowseType> AtomicTypesOf(IReadOnlyList<string> nodeIds);
}

public sealed record OpcUaBrowseTypeBatch(IReadOnlyDictionary<string, OpcUaBrowseType> Types,
    IReadOnlyList<OpcUaBrowseIssue> Issues);

public interface IOpcUaPartialBrowseSource
{
    OpcUaBrowseTypeBatch TypeBatch(IReadOnlyList<string> nodeIds);
}

public sealed class OpcUaBrowseNodeException(string nodeId, Exception innerException)
    : Exception($"OPC UA 节点 {nodeId} 浏览失败：{innerException.Message}", innerException)
{
    public string NodeId { get; } = nodeId;
}

public static class OpcUaBrowseWalker
{
    private sealed record PendingTag(string Name, string Address, IReadOnlyList<string> PathSegments);
    private sealed record PendingNode(
        string Id,
        string Name,
        bool IsVariable,
        IReadOnlyList<string> PathSegments,
        int Depth,
        bool IncludeSelf);

    /// <summary>只读取当前节点的直接子节点；不会为判断叶子节点而继续递归。</summary>
    public static IReadOnlyList<OpcUaBrowseNode> Children(
        IOpcUaBrowseSource source,
        string root,
        IReadOnlyList<string> parentPathSegments)
    {
        return source.Children(root)
            .Where(child => !child.IsProperty)
            .Select(child => new OpcUaBrowseNode(
                child.Id,
                child.Name,
                child.IsVariable,
                MayHaveChildren: true,
                parentPathSegments.Append(child.Name).ToArray()))
            .ToArray();
    }

    public static BrowseResult Walk(
        IOpcUaBrowseSource source,
        string root,
        int limit,
        CancellationToken ct,
        Action<OpcUaBrowseProgress>? progress = null,
        int progressEvery = 250)
        => WalkCore(source, root, null, Array.Empty<string>(), limit, ct, progress, progressEvery);

    /// <summary>只遍历用户选中的节点及其后代；所选叶子变量也能直接导入。</summary>
    public static BrowseResult WalkBranch(
        IOpcUaBrowseSource source,
        OpcUaBrowseNode selectedRoot,
        int limit,
        CancellationToken ct,
        Action<OpcUaBrowseProgress>? progress = null,
        int progressEvery = 250)
        => WalkCore(source, selectedRoot.NodeId, selectedRoot, selectedRoot.PathSegments,
            limit, ct, progress, progressEvery);

    private static BrowseResult WalkCore(
        IOpcUaBrowseSource source,
        string root,
        OpcUaBrowseNode? selectedRoot,
        IReadOnlyList<string> initialPath,
        int limit,
        CancellationToken ct,
        Action<OpcUaBrowseProgress>? progress,
        int progressEvery)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        if (progressEvery <= 0) throw new ArgumentOutOfRangeException(nameof(progressEvery));

        var tags = new List<BrowsedTag>(Math.Min(limit, 1024));
        var issues = new List<OpcUaBrowseIssue>();
        var pending = new List<PendingTag>(Math.Min(limit, 1024));
        var knownTypes = new Dictionary<string, OpcUaBrowseType>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        int scannedNodes = 0;
        bool truncated = false;
        bool failureReported = false;

        void Notify(OpcUaBrowseProgressStage stage, string nodeId, IReadOnlyList<string> path, int depth, string? error = null)
        {
            if (progress is null) return;
            try
            {
                progress(new OpcUaBrowseProgress(
                    stage,
                    nodeId,
                    string.Join('.', path),
                    depth,
                    scannedNodes,
                    pending.Count,
                    elapsed.ElapsedMilliseconds,
                    error));
            }
            catch
            {
                // 诊断消费者不能中断真实的 OPC UA 浏览。
            }
        }

        Notify(OpcUaBrowseProgressStage.Starting, root, initialPath, 0);
        try
        {
            var queue = new Queue<PendingNode>();
            queue.Enqueue(selectedRoot is null
                ? new PendingNode(root, "", false, initialPath.ToArray(), 0, IncludeSelf: false)
                : new PendingNode(selectedRoot.NodeId, selectedRoot.Name, selectedRoot.IsVariable,
                    selectedRoot.PathSegments.ToArray(), 0, IncludeSelf: true));

            while (queue.Count > 0 && !truncated)
            {
                ct.ThrowIfCancellationRequested();
                var batch = new List<PendingNode>(64);
                while (queue.Count > 0 && batch.Count < 64)
                {
                    var item = queue.Dequeue();
                    if (item.Depth > 32)
                    {
                        truncated = true;
                        break;
                    }
                    if (visited.Add(item.Id)) batch.Add(item);
                }
                if (batch.Count == 0) continue;

                IReadOnlyDictionary<string, IReadOnlyList<OpcUaBrowseChild>> childrenByNode;
                try
                {
                    childrenByNode = source.ChildrenOf(batch.Select(item => item.Id).ToArray());
                }
                catch (OpcUaBrowseNodeException ex)
                {
                    var failed = batch.FirstOrDefault(item => item.Id == ex.NodeId) ?? batch[0];
                    failureReported = true;
                    Notify(OpcUaBrowseProgressStage.Failed, ex.NodeId, failed.PathSegments, failed.Depth,
                        ex.InnerException?.Message ?? ex.Message);
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException ?? ex).Throw();
                    throw;
                }
                catch (Exception ex)
                {
                    var failed = batch[0];
                    failureReported = true;
                    Notify(OpcUaBrowseProgressStage.Failed, failed.Id, failed.PathSegments, failed.Depth, ex.Message);
                    throw;
                }

                // UDT parents can expose readable components while their aggregate Value is unreadable.
                // Only probe their datatype contract for atomic DTL; resolve full types for selected leaves below.
                var componentVariables = batch.Where(item => item.IncludeSelf && item.IsVariable &&
                    childrenByNode.TryGetValue(item.Id, out var children) && children.Any(child => !child.IsProperty))
                    .Select(item => item.Id).ToArray();
                var atomicTypes = source is IOpcUaAtomicBrowseSource atomicSource && componentVariables.Length > 0
                    ? atomicSource.AtomicTypesOf(componentVariables)
                    : new Dictionary<string, OpcUaBrowseType>();
                foreach (var item in batch)
                {
                    ct.ThrowIfCancellationRequested();
                    var descendants = childrenByNode.TryGetValue(item.Id, out var found)
                        ? found.Where(child => !child.IsProperty).ToArray()
                        : Array.Empty<OpcUaBrowseChild>();
                    if (item.IncludeSelf && item.IsVariable && (descendants.Length == 0 || atomicTypes.TryGetValue(item.Id, out var logicalType) && logicalType.DataType == IgwDataType.Dtl))
                    {
                        if (pending.Count >= limit)
                        {
                            truncated = true;
                            break;
                        }
                        var parentPath = item.PathSegments.Count > 0
                            ? item.PathSegments.Take(item.PathSegments.Count - 1).ToArray()
                            : Array.Empty<string>();
                        pending.Add(new PendingTag(item.Name, item.Id, parentPath));
                        continue;
                    }

                    foreach (var child in descendants)
                    {
                        scannedNodes++;
                        var childPath = item.PathSegments.Append(child.Name).ToArray();
                        if (scannedNodes <= 20 || scannedNodes % progressEvery == 0)
                            Notify(OpcUaBrowseProgressStage.Scanning, child.Id,
                                child.IsVariable ? item.PathSegments : childPath, item.Depth);
                        queue.Enqueue(new PendingNode(
                            child.Id,
                            child.Name,
                            child.IsVariable,
                            childPath,
                            item.Depth + 1,
                            IncludeSelf: true));
                    }
                }
            }

            foreach (var batch in pending.Chunk(256))
            {
                ct.ThrowIfCancellationRequested();
                var ids = batch.Select(item => item.Address).ToArray();
                var missing = ids.Where(id => !knownTypes.ContainsKey(id)).ToArray();
                var failedTypes = new Dictionary<string, OpcUaBrowseIssue>(StringComparer.Ordinal);
                if (missing.Length > 0)
                {
                    var resolved = source is IOpcUaPartialBrowseSource partial
                        ? partial.TypeBatch(missing)
                        : new OpcUaBrowseTypeBatch(source.TypesOf(missing), Array.Empty<OpcUaBrowseIssue>());
                    foreach (var pair in resolved.Types) knownTypes[pair.Key] = pair.Value;
                    foreach (var issue in resolved.Issues) failedTypes[issue.NodeId] = issue;
                }
                var types = knownTypes;
                foreach (var item in batch)
                {
                    ct.ThrowIfCancellationRequested();
                    if (failedTypes.TryGetValue(item.Address, out var issue))
                    {
                        issues.Add(issue with { Name = item.Name, PathSegments = item.PathSegments.Append(item.Name).ToArray() });
                        continue;
                    }
                    var type = types.TryGetValue(item.Address, out var found)
                        ? found
                        : throw new InvalidOperationException($"缺少 OPC UA 点位 {item.Address} 的数据类型");
                    tags.Add(new BrowsedTag
                    {
                        Name = item.Name,
                        Address = item.Address,
                        Path = string.Join('.', item.PathSegments),
                        PathSegments = item.PathSegments.ToArray(),
                        DataType = type.DataType,
                        IsArray = type.IsArray,
                        StringLength = type.StringLength,
                        Access = type.Access,
                        Metadata = BuildMetadata(item.Address, type),
                        Array = type.IsArray && type.Dimensions is { Count: > 0 }
                            ? new ArrayDef
                            {
                                ElementType = type.DataType,
                                Dimensions = type.Dimensions.ToArray(),
                                LowerBounds = type.LowerBounds?.ToArray() ?? new int[type.Dimensions.Count],
                                ElementStrideBytes = type.Structure?.SizeBytes ?? 1,
                                Struct = type.Structure,
                                SourceShape = BuildSourceShape(type),
                            }
                            : null,
                    });
                }
            }

            Notify(OpcUaBrowseProgressStage.Completed, root, initialPath, 0);
            return new BrowseResult(tags, truncated, scannedNodes, elapsed.ElapsedMilliseconds, issues);
        }
        catch
        {
            // 子节点失败已经包含最后一个 NodeId；根节点失败需在此补齐。
            if (!failureReported)
                Notify(OpcUaBrowseProgressStage.Failed, root, initialPath, 0, "根节点浏览失败");
            throw;
        }
    }

    private static IReadOnlyDictionary<string, string>? BuildMetadata(string address, OpcUaBrowseType type)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["array.sourceFormat"] = "OPC UA",
            ["array.sourceAddress"] = address,
        };
        if (type.PhysicalValueRank is not null) metadata["opcua.physicalValueRank"] = type.PhysicalValueRank.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (type.PhysicalDimensions is { Count: > 0 }) metadata["opcua.physicalArrayDimensions"] = string.Join(',', type.PhysicalDimensions);
        if (type.DataTypeName is not null) metadata["opcua.dataTypeName"] = type.DataTypeName;
        if (type.ValueRank is not null) metadata["opcua.valueRank"] = type.ValueRank.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (type.Dimensions is { Count: > 0 }) metadata["opcua.arrayDimensions"] = string.Join(',', type.Dimensions);
        if (type.LowerBounds is { Count: > 0 }) metadata["array.lowerBounds"] = string.Join(',', type.LowerBounds);
        if (!string.IsNullOrWhiteSpace(type.DataTypeNodeId)) metadata["opcua.dataTypeNodeId"] = type.DataTypeNodeId;
        return metadata;
    }

    private static string? BuildSourceShape(OpcUaBrowseType type)
    {
        if (!type.IsArray || type.Dimensions is not { Count: > 0 }) return null;
        int rank = type.ValueRank ?? type.Dimensions.Count;
        var lowerBounds = type.LowerBounds ?? Enumerable.Repeat(0, type.Dimensions.Count).ToArray();
        return $"OPC UA ValueRank={rank}; Dimensions=[{string.Join(',', type.Dimensions)}]; LowerBounds=[{string.Join(',', lowerBounds)}]";
    }
}
