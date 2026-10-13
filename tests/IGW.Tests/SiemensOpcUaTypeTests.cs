using IGW.Abstractions;
using IGW.Drivers.OpcUaClient;
using Opc.Ua;
using Xunit;

namespace IGW.Tests;

public class SiemensOpcUaTypeTests
{
    [Fact]
    public void Dtl_parent_with_member_nodes_imports_one_logical_value()
    {
        var result = OpcUaBrowseWalker.WalkBranch(new DtlSource(),
            new OpcUaBrowseNode("dtl", "Timestamp", true, true, ["Timestamp"]), 100, CancellationToken.None);
        var tag = Assert.Single(result.Tags);
        Assert.Equal("dtl", tag.Address);
        Assert.Equal(IgwDataType.Dtl, tag.DataType);
        Assert.Equal("ns=3;i=100", tag.Metadata!["opcua.dataTypeNodeId"]);
    }

    private sealed class DtlSource : IOpcUaBrowseSource, IOpcUaAtomicBrowseSource
    {
        public IReadOnlyList<OpcUaBrowseChild> Children(string id) => id == "dtl" ? [new("year", "YEAR", true)] : [];
        public IReadOnlyDictionary<string, IReadOnlyList<OpcUaBrowseChild>> ChildrenOf(IReadOnlyList<string> ids) => ids.ToDictionary(id => id, Children);
        public OpcUaBrowseType TypeOf(string id) => new(IgwDataType.Dtl, false, DataTypeNodeId: "ns=3;i=100", DataTypeName: "DTL");
        public IReadOnlyDictionary<string, OpcUaBrowseType> TypesOf(IReadOnlyList<string> ids) => ids.ToDictionary(id => id, TypeOf);
        public IReadOnlyDictionary<string, OpcUaBrowseType> AtomicTypesOf(IReadOnlyList<string> ids) => TypesOf(ids);
    }

    [Theory]
    [InlineData("DTL", 17, -1, false)]
    [InlineData("DATE", 18, -1, false)]
    [InlineData("DT", 23, 1, false)]
    [InlineData("DT", 23, 2, true)]
    [InlineData("LTIME", 22, -1, false)]
    [InlineData("CHAR", 26, -1, false)]
    [InlineData("WCHAR", 27, -1, false)]
    [InlineData("WORD", 28, -1, false)]
    [InlineData("DWORD", 29, -1, false)]
    [InlineData("LWORD", 30, -1, false)]
    [InlineData("TIMER", 31, -1, false)]
    [InlineData("COUNTER", 32, -1, false)]
    [InlineData("SINT", 33, -1, false)]
    [InlineData("LDT", 24, -1, false)]
    [InlineData("S5TIME", 25, -1, false)]
    [InlineData("TIME", 21, -1, false)]
    [InlineData("TOD", 19, -1, false)]
    [InlineData("LTOD", 20, -1, false)]
    public void Source_type_contract_controls_semantics_and_physical_DT_rank(string name, int expected, int rank, bool array)
    {
        int valueReads = 0, typeReads = 0;
        var ids = new[] { "ns=3;s=A", "ns=3;s=B" };
        var result = OpcUaClientDeviceDriver.ReadBrowseTypes(ids, requests => new DataValueCollection(requests.Select(r =>
        {
            if (r.AttributeId == Attributes.Value) valueReads++;
            if (r.AttributeId == Attributes.BrowseName) typeReads++;
            return new Opc.Ua.DataValue(new Variant(r.AttributeId switch
            {
                Attributes.DataType => (object)new NodeId(100u, 3),
                Attributes.BrowseName => new QualifiedName(name, 3),
                Attributes.ValueRank => rank,
                Attributes.ArrayDimensions => rank == 2 ? new uint[] { 4, 8 } : rank == 1 ? new uint[] { 8 } : [],
                Attributes.AccessLevel => (byte)3,
                Attributes.UserAccessLevel => (byte)1,
                _ => 123,
            }));
        })));
        Assert.All(result.Values, type => { Assert.Equal((IgwDataType)expected, type.DataType); Assert.Equal(array, type.IsArray); Assert.Equal(TagAccess.Read, type.Access); });
        if (array) Assert.Equal(new[] { 4 }, result[ids[0]].Dimensions);
        Assert.Equal(0, valueReads);
        Assert.Equal(1, typeReads);
    }
}
