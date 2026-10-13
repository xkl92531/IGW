using IGW.Abstractions;
using IGW.Drivers.OpcUaClient;
using Xunit;

namespace IGW.Tests;

public sealed class OpcUaBrowseWalkerTests
{
    [Fact]
    public void Unresolved_leaf_type_still_fails_instead_of_importing_a_guessed_type()
    {
        var source = new FakeBrowseSource().At("PackGlue");
        source.UnreadableParents.Add("PackGlue");
        Assert.Throws<InvalidOperationException>(() => OpcUaBrowseWalker.WalkBranch(source,
            new OpcUaBrowseNode("PackGlue", "PackGlue", true, false, ["PackGlue"]), 100, CancellationToken.None));
    }

    [Fact]
    public void Unreadable_UDT_parent_still_imports_readable_components()
    {
        var source = new FakeBrowseSource().At("PackGlue", Var("enabled", "Enabled"));
        source.UnreadableParents.Add("PackGlue");
        source.Types["enabled"] = new OpcUaBrowseType(IgwDataType.Bool, false);
        var result = OpcUaBrowseWalker.WalkBranch(source,
            new OpcUaBrowseNode("PackGlue", "PackGlue", true, true, ["DB", "PackGlue"]), 100, CancellationToken.None);
        Assert.Equal("enabled", Assert.Single(result.Tags).Address);
        Assert.Equal(1, result.ScannedNodeCount);
        Assert.NotNull(result.ElapsedMilliseconds);
        Assert.DoesNotContain(source.TypeBatchRequests.SelectMany(ids => ids), id => id == "PackGlue");
    }

    [Fact]
    public void Direct_children_never_walk_grandchildren_and_preserve_the_parent_path()
    {
        var source = new FakeBrowseSource()
            .At("root", Obj("db1", "DB1"), Var("state", "State"))
            .ThrowChildrenAt("db1", new InvalidOperationException("direct browse must not recurse"));

        var result = OpcUaBrowseWalker.Children(source, "root", ["PLC1"]);

        Assert.Collection(result,
            db =>
            {
                Assert.Equal("db1", db.NodeId);
                Assert.Equal("DB1", db.Name);
                Assert.False(db.IsVariable);
                Assert.Equal(new[] { "PLC1", "DB1" }, db.PathSegments);
            },
            state =>
            {
                Assert.Equal("state", state.NodeId);
                Assert.True(state.IsVariable);
                Assert.Equal(new[] { "PLC1", "State" }, state.PathSegments);
            });
        Assert.Equal(new[] { "root" }, source.ChildrenRequests);
    }

    [Fact]
    public void Selected_branch_scans_only_that_branch_and_keeps_its_full_server_path()
    {
        var source = new FakeBrowseSource()
            .At("db1", Var("flag", "MESFlag"))
            .At("flag")
            .ThrowChildrenAt("other-db", new InvalidOperationException("another DB must not be scanned"));
        source.Types["flag"] = new OpcUaBrowseType(IgwDataType.Int16, false);
        var selected = new OpcUaBrowseNode("db1", "DB1", false, true, ["PLC1", "DB1"]);

        var result = OpcUaBrowseWalker.WalkBranch(source, selected, 100, CancellationToken.None);

        var tag = Assert.Single(result.Tags);
        Assert.Equal("MESFlag", tag.Name);
        Assert.Equal(new[] { "PLC1", "DB1" }, tag.PathSegments);
        Assert.DoesNotContain("other-db", source.ChildrenRequests);
    }

    [Fact]
    public void Selected_leaf_is_imported_without_scanning_the_whole_server()
    {
        var source = new FakeBrowseSource().At("flag");
        source.Types["flag"] = new OpcUaBrowseType(IgwDataType.Int16, false);
        var selected = new OpcUaBrowseNode("flag", "MESFlag", true, true, ["PLC1", "DB1", "MESFlag"]);

        var tag = Assert.Single(OpcUaBrowseWalker.WalkBranch(
            source, selected, 100, CancellationToken.None).Tags);

        Assert.Equal("flag", tag.Address);
        Assert.Equal(new[] { "PLC1", "DB1" }, tag.PathSegments);
    }

    [Fact]
    public void Leaf_types_are_requested_in_a_batch_instead_of_one_read_per_tag()
    {
        var source = new FakeBrowseSource()
            .At("root", Var("a", "A"), Var("b", "B"))
            .At("a")
            .At("b");
        source.Types["a"] = new OpcUaBrowseType(IgwDataType.Int16, false);
        source.Types["b"] = new OpcUaBrowseType(IgwDataType.String, false);

        var result = OpcUaBrowseWalker.Walk(source, "root", 100, CancellationToken.None);

        Assert.Equal(2, result.Tags.Count);
        var batch = Assert.Single(source.TypeBatchRequests);
        Assert.Equal(new[] { "a", "b" }, batch);
        Assert.Equal(0, source.SingleTypeRequests);
    }

    [Fact]
    public void Current_session_access_is_preserved_in_the_browse_result()
    {
        var source = new FakeBrowseSource().At("root", Var("flag", "DataAccept")).At("flag");
        source.Types["flag"] = new OpcUaBrowseType(IgwDataType.Int16, false, Access: TagAccess.ReadWrite);

        var tag = Assert.Single(OpcUaBrowseWalker.Walk(source, "root", 100, CancellationToken.None).Tags);

        Assert.Equal(TagAccess.ReadWrite, tag.Access);
    }

    [Fact]
    public void Server_published_string_capacity_is_preserved_in_the_browse_result()
    {
        var source = new FakeBrowseSource().At("root", Var("message", "Message")).At("message");
        source.Types["message"] = new OpcUaBrowseType(
            IgwDataType.String, false, Access: TagAccess.ReadWrite, StringLength: 50);

        var tag = Assert.Single(OpcUaBrowseWalker.Walk(source, "root", 100, CancellationToken.None).Tags);

        Assert.Equal(50, tag.StringLength);
    }

    [Fact]
    public void Current_user_access_overrides_the_server_default_access()
    {
        Assert.Equal(TagAccess.Read, OpcUaClientDeviceDriver.MapBrowseAccess(3, 1));
        Assert.Equal(TagAccess.ReadWrite, OpcUaClientDeviceDriver.MapBrowseAccess(1, 3));
    }

    [Fact]
    public void Sibling_nodes_are_browsed_in_batches_instead_of_one_service_roundtrip_per_node()
    {
        var source = new FakeBrowseSource()
            .At("root", Obj("a", "A"), Obj("b", "B"))
            .At("a", Var("a1", "ValueA"))
            .At("b", Var("b1", "ValueB"))
            .At("a1")
            .At("b1");
        source.Types["a1"] = new OpcUaBrowseType(IgwDataType.Int16, false);
        source.Types["b1"] = new OpcUaBrowseType(IgwDataType.Int16, false);

        var result = OpcUaBrowseWalker.Walk(source, "root", 100, CancellationToken.None);

        Assert.Equal(2, result.Tags.Count);
        Assert.Contains(source.ChildrenBatchRequests, batch => batch.SequenceEqual(new[] { "a", "b" }));
        Assert.Contains(source.ChildrenBatchRequests, batch => batch.SequenceEqual(new[] { "a1", "b1" }));
    }

    [Fact]
    public void Variable_with_components_becomes_nested_groups_and_properties_are_ignored()
    {
        var source = new FakeBrowseSource()
            .At("root", Obj("recipe", "Recipe"))
            .At("recipe", Var("header", "Header"), Prop("units", "EngineeringUnits"))
            .At("header", Var("batch", "BatchId"))
            .At("batch")
            .At("units");
        source.Types["batch"] = new OpcUaBrowseType(IgwDataType.String, false);

        var result = OpcUaBrowseWalker.Walk(source, "root", 100, CancellationToken.None);

        var tag = Assert.Single(result.Tags);
        Assert.Equal("BatchId", tag.Name);
        Assert.Equal("batch", tag.Address);
        Assert.Equal(new[] { "Recipe", "Header" }, tag.PathSegments);
        Assert.Equal("Recipe.Header", tag.Path);
        Assert.DoesNotContain(result.Tags, item => item.Name == "EngineeringUnits");
    }

    [Fact]
    public void Explicit_path_segments_preserve_dots_inside_browse_names()
    {
        var source = new FakeBrowseSource()
            .At("root", Obj("cell", "Cell.Data"))
            .At("cell", Var("value", "Voltage"))
            .At("value");
        source.Types["value"] = new OpcUaBrowseType(IgwDataType.Float, false);

        var tag = Assert.Single(OpcUaBrowseWalker.Walk(source, "root", 100, CancellationToken.None).Tags);

        Assert.Equal(new[] { "Cell.Data" }, tag.PathSegments);
        Assert.Equal("Cell.Data", tag.Path);
    }

    [Fact]
    public void Array_dimensions_and_lower_bounds_are_preserved_in_browse_result()
    {
        var source = new FakeBrowseSource().At("root", Var("matrix", "Matrix")).At("matrix");
        source.Types["matrix"] = new OpcUaBrowseType(IgwDataType.Int16, true, [2, 3], [0, 0]);

        var tag = Assert.Single(OpcUaBrowseWalker.Walk(source, "root", 100, CancellationToken.None).Tags);

        Assert.Equal([2, 3], tag.Array!.Dimensions);
        Assert.Equal([0, 0], tag.Array.LowerBounds);
    }

    [Fact]
    public void Array_protocol_evidence_is_preserved_for_project_round_trip()
    {
        var source = new FakeBrowseSource().At("root", Var("matrix", "Matrix")).At("matrix");
        source.Types["matrix"] = new OpcUaBrowseType(
            IgwDataType.Int16,
            true,
            [2, 3],
            [-1, 5],
            ValueRank: 2,
            DataTypeNodeId: "i=4");

        var tag = Assert.Single(OpcUaBrowseWalker.Walk(source, "root", 100, CancellationToken.None).Tags);

        Assert.Equal("OPC UA", tag.Metadata!["array.sourceFormat"]);
        Assert.Equal("2", tag.Metadata["opcua.valueRank"]);
        Assert.Equal("2,3", tag.Metadata["opcua.arrayDimensions"]);
        Assert.Equal("-1,5", tag.Metadata["array.lowerBounds"]);
        Assert.Equal("i=4", tag.Metadata["opcua.dataTypeNodeId"]);
        Assert.Equal("matrix", tag.Metadata["array.sourceAddress"]);
        Assert.Equal("OPC UA ValueRank=2; Dimensions=[2,3]; LowerBounds=[-1,5]", tag.Array!.SourceShape);
    }

    [Fact]
    public void Browse_progress_reports_current_node_and_literal_scanned_tag_counts()
    {
        var source = new FakeBrowseSource()
            .At("root", Obj("recipe", "Recipe"))
            .At("recipe", Var("header", "Header"))
            .At("header", Var("batch", "BatchId"))
            .At("batch");
        source.Types["batch"] = new OpcUaBrowseType(IgwDataType.String, false);
        var events = new List<OpcUaBrowseProgress>();

        var result = OpcUaBrowseWalker.Walk(
            source,
            "root",
            100,
            CancellationToken.None,
            events.Add,
            progressEvery: 1);

        Assert.Single(result.Tags);
        Assert.Equal(OpcUaBrowseProgressStage.Starting, events[0].Stage);
        Assert.Contains(events, item =>
            item.Stage == OpcUaBrowseProgressStage.Scanning &&
            item.CurrentNodeId == "header" &&
            item.ScannedNodeCount == 2 &&
            item.DiscoveredTagCount == 0 &&
            item.Depth == 1);
        var completed = Assert.Single(events, item => item.Stage == OpcUaBrowseProgressStage.Completed);
        Assert.Equal("root", completed.CurrentNodeId);
        Assert.Equal(3, completed.ScannedNodeCount);
        Assert.Equal(1, completed.DiscoveredTagCount);
    }

    [Fact]
    public void Browse_failure_reports_the_exact_node_that_failed_before_rethrowing()
    {
        var source = new FakeBrowseSource()
            .At("root", Obj("machine", "Machine"))
            .ThrowChildrenAt("machine", new IOException("remote browse timed out"));
        var events = new List<OpcUaBrowseProgress>();

        var error = Assert.Throws<IOException>(() => OpcUaBrowseWalker.Walk(
            source,
            "root",
            100,
            CancellationToken.None,
            events.Add,
            progressEvery: 250));

        Assert.Equal("remote browse timed out", error.Message);
        var failed = Assert.Single(events, item => item.Stage == OpcUaBrowseProgressStage.Failed);
        Assert.Equal("machine", failed.CurrentNodeId);
        Assert.Equal(1, failed.ScannedNodeCount);
        Assert.Equal(0, failed.DiscoveredTagCount);
        Assert.Contains("remote browse timed out", failed.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Deep_failure_is_reported_once_instead_of_once_per_parent_frame()
    {
        var source = new FakeBrowseSource()
            .At("root", Obj("machine", "Machine"))
            .At("machine", Obj("db", "DB2100"))
            .ThrowChildrenAt("db", new IOException("remote browse timed out"));
        var events = new List<OpcUaBrowseProgress>();

        Assert.Throws<IOException>(() => OpcUaBrowseWalker.Walk(
            source, "root", 100, CancellationToken.None, events.Add));

        var failed = Assert.Single(events, item => item.Stage == OpcUaBrowseProgressStage.Failed);
        Assert.Equal("db", failed.CurrentNodeId);
    }

    [Fact]
    public void Partial_type_issue_keeps_original_node_path_and_other_tags()
    {
        var source = new FakeBrowseSource().At("root", Var("ns=3;s=Good", "Good"), Var("ns=3;s=Bad", "Bad"));
        source.TypeIssues.Add(new OpcUaBrowseIssue("ns=3;s=Bad", "wrong", [], "unsupported POINTER"));
        var result = OpcUaBrowseWalker.WalkBranch(source,
            new OpcUaBrowseNode("root", "DB", false, true, ["PLC", "DB"]), 100, CancellationToken.None);
        Assert.Equal("ns=3;s=Good", Assert.Single(result.Tags).Address);
        var issue = Assert.Single(result.Issues!);
        Assert.Equal("ns=3;s=Bad", issue.NodeId);
        Assert.Equal("Bad", issue.Name);
        Assert.Equal(new[] { "PLC", "DB", "Bad" }, issue.PathSegments);
        Assert.Equal("unsupported POINTER", issue.Reason);
        Assert.False(result.Truncated);
    }

    private static OpcUaBrowseChild Obj(string id, string name) => new(id, name, false, false);
    private static OpcUaBrowseChild Var(string id, string name) => new(id, name, true, false);
    private static OpcUaBrowseChild Prop(string id, string name) => new(id, name, true, true);

    private sealed class FakeBrowseSource : IOpcUaBrowseSource, IOpcUaPartialBrowseSource
    {
        public List<OpcUaBrowseIssue> TypeIssues { get; } = [];
        public OpcUaBrowseTypeBatch TypeBatch(IReadOnlyList<string> nodeIds) => new(
            TypesOf(nodeIds.Where(id => TypeIssues.All(issue => issue.NodeId != id)).ToArray()),
            TypeIssues.Where(issue => nodeIds.Contains(issue.NodeId)).ToArray());
        private readonly Dictionary<string, IReadOnlyList<OpcUaBrowseChild>> _children = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Exception> _childrenErrors = new(StringComparer.Ordinal);
        public Dictionary<string, OpcUaBrowseType> Types { get; } = new(StringComparer.Ordinal);
        public List<string> ChildrenRequests { get; } = [];
        public List<IReadOnlyList<string>> ChildrenBatchRequests { get; } = [];
        public List<IReadOnlyList<string>> TypeBatchRequests { get; } = [];
        public int SingleTypeRequests { get; private set; }
        public HashSet<string> UnreadableParents { get; } = [];

        public FakeBrowseSource At(string id, params OpcUaBrowseChild[] children)
        {
            _children[id] = children;
            return this;
        }

        public FakeBrowseSource ThrowChildrenAt(string id, Exception error)
        {
            _childrenErrors[id] = error;
            return this;
        }

        public IReadOnlyList<OpcUaBrowseChild> Children(string nodeId)
        {
            ChildrenRequests.Add(nodeId);
            if (_childrenErrors.TryGetValue(nodeId, out var error)) throw error;
            return _children.TryGetValue(nodeId, out var children) ? children : Array.Empty<OpcUaBrowseChild>();
        }

        public IReadOnlyDictionary<string, IReadOnlyList<OpcUaBrowseChild>> ChildrenOf(IReadOnlyList<string> nodeIds)
        {
            ChildrenBatchRequests.Add(nodeIds.ToArray());
            var result = new Dictionary<string, IReadOnlyList<OpcUaBrowseChild>>(StringComparer.Ordinal);
            foreach (var id in nodeIds)
            {
                try { result[id] = Children(id); }
                catch (Exception ex) { throw new OpcUaBrowseNodeException(id, ex); }
            }
            return result;
        }

        public OpcUaBrowseType TypeOf(string nodeId)
        {
            SingleTypeRequests++;
            return Types.TryGetValue(nodeId, out var type) ? type : new OpcUaBrowseType(IgwDataType.Double, false);
        }

        public IReadOnlyDictionary<string, OpcUaBrowseType> TypesOf(IReadOnlyList<string> nodeIds)
        {
            if (nodeIds.Any(UnreadableParents.Contains))
                throw new InvalidOperationException("无法确定 OPC UA 点位 ns=3;s=PackGlue 的数据类型");
            TypeBatchRequests.Add(nodeIds.ToArray());
            return nodeIds.ToDictionary(
                id => id,
                id => Types.TryGetValue(id, out var type) ? type : new OpcUaBrowseType(IgwDataType.Double, false),
                StringComparer.Ordinal);
        }
    }
}
