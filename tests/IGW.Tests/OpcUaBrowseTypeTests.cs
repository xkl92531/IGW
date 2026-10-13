using System.Reflection;
using IGW.Abstractions;
using IGW.Drivers.OpcUaClient;
using Opc.Ua;
using Xunit;

namespace IGW.Tests;

public sealed class OpcUaBrowseTypeTests
{
    [Fact]
    public void Partial_type_batch_keeps_valid_nodes_and_reports_exact_unsupported_node()
    {
        var result = OpcUaClientDeviceDriver.ReadBrowseTypeBatch(["ns=3;s=Good", "ns=3;s=Bad", "ns=3;s=After"], batch =>
            new DataValueCollection(batch.Select(r => r.AttributeId == Attributes.Value
                ? new Opc.Ua.DataValue()
                : r.AttributeId == Attributes.BrowseName
                ? new Opc.Ua.DataValue(new Variant(new QualifiedName("POINTER")))
                : Attribute(r, r.NodeId.ToString() == "ns=3;s=Bad" ? new NodeId(3000u, 3) : DataTypeIds.Int16, -1, []))));
        Assert.Equal(2, result.Types.Count);
        Assert.Equal(IgwDataType.Int16, result.Types["ns=3;s=After"].DataType);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("ns=3;s=Bad", issue.NodeId);
        Assert.Contains("POINTER", issue.Reason);
    }

    [Fact]
    public void Partial_type_batch_propagates_transport_failure_and_cancellation()
    {
        Assert.Throws<IOException>(() => OpcUaClientDeviceDriver.ReadBrowseTypeBatch(["ns=3;s=Good"], _ => throw new IOException("disconnected")));
        Assert.Throws<OperationCanceledException>(() => OpcUaClientDeviceDriver.ReadBrowseTypeBatch(["ns=3;s=Good"], _ => throw new OperationCanceledException()));
    }

    [Theory]
    [InlineData(StatusCodes.BadTimeout)]
    [InlineData(StatusCodes.BadCommunicationError)]
    [InlineData(StatusCodes.BadSessionIdInvalid)]
    public void Partial_type_batch_does_not_turn_transport_status_into_node_issue(uint status)
    {
        var error = Assert.Throws<ServiceResultException>(() => OpcUaClientDeviceDriver.ReadBrowseTypeBatch(
            ["ns=3;s=Good"], batch => new DataValueCollection(batch.Select(_ =>
                new Opc.Ua.DataValue { StatusCode = status }))));
        Assert.Equal(status, error.StatusCode);
    }

    [Theory]
    [InlineData("PackGlue", false)]
    [InlineData("DTL", true)]
    public void Atomic_parent_probe_uses_metadata_only_and_never_reads_UDT_value(string typeName, bool atomic)
    {
        var result = OpcUaClientDeviceDriver.ReadBrowseAtomicTypes(["ns=3;s=PackGlue"], batch =>
            new DataValueCollection(batch.Select(request =>
            {
                Assert.NotEqual(Attributes.Value, request.AttributeId);
                return request.AttributeId == Attributes.BrowseName
                    ? new Opc.Ua.DataValue(new Variant(new QualifiedName(typeName, 3)))
                    : Attribute(request, new NodeId(3000u, 3), ValueRanks.Scalar, []);
            })));
        Assert.Equal(atomic, result.ContainsKey("ns=3;s=PackGlue"));
    }

    [Fact]
    public void Mixed_user_permissions_remain_attached_to_each_node_across_attribute_batches()
    {
        var ids = Enumerable.Range(0, 256).Select(i => $"ns=3;s=Value{i}").ToArray();
        var permissions = new Dictionary<string, byte>(StringComparer.Ordinal);
        for (int i = 0; i < ids.Length; i++) permissions[ids[i]] = (byte)(i % 3 + 1);
        var result = OpcUaClientDeviceDriver.ReadBrowseTypes(ids, batch =>
            new DataValueCollection(batch.Select(request => request.AttributeId == Attributes.UserAccessLevel
                ? new Opc.Ua.DataValue(new Variant(permissions[request.NodeId.ToString()]))
                : Attribute(request, DataTypeIds.Int16, ValueRanks.Scalar, []))));

        foreach (string id in ids)
            Assert.Equal(permissions[id] switch
            {
                1 => TagAccess.Read,
                2 => TagAccess.Write,
                _ => TagAccess.ReadWrite,
            }, result[id].Access);
    }

    [Fact]
    public void Large_scalar_import_reads_metadata_in_bounded_batches_without_sampling_values()
    {
        int calls = 0;
        var ids = Enumerable.Range(0, 256).Select(i => $"ns=3;s=Value{i}").ToArray();
        var result = OpcUaClientDeviceDriver.ReadBrowseTypes(ids, batch =>
        {
            calls++;
            Assert.InRange(batch.Count, 1, 512);
            Assert.DoesNotContain(batch, r => r.AttributeId == Attributes.Value);
            return new DataValueCollection(batch.Select(r => Attribute(r, DataTypeIds.Float, -1, [])));
        });
        Assert.Equal(256, result.Count);
        Assert.Equal(3, calls);
        Assert.All(result.Values, value => Assert.Equal(IgwDataType.Float, value.DataType));
    }

    [Fact]
    public void Incomplete_array_shape_and_custom_type_are_sampled_without_sampling_known_scalars()
    {
        var sampled = new List<string>();
        var result = OpcUaClientDeviceDriver.ReadBrowseTypes(["ns=3;s=Scalar", "ns=3;s=Bytes", "ns=3;s=Custom"], batch =>
            new DataValueCollection(batch.Select(r =>
            {
                if (r.AttributeId == Attributes.BrowseName)
                    return new Opc.Ua.DataValue(new Variant(new QualifiedName("Custom")));
                string name = (string)r.NodeId.Identifier;
                if (r.AttributeId == Attributes.Value)
                {
                    sampled.Add(name);
                    return new Opc.Ua.DataValue(new Variant(name == "Bytes" ? (object)new byte[8] : 2.5f));
                }
                return name switch
                {
                    "Bytes" => Attribute(r, DataTypeIds.Byte, 1, [0]),
                    "Custom" => Attribute(r, new NodeId(3000u, 3), -1, []),
                    _ => Attribute(r, DataTypeIds.UInt32, -1, []),
                };
            })));
        Assert.Equal(new[] { "Bytes", "Custom" }, sampled);
        Assert.Equal(IgwDataType.UInt32, result["ns=3;s=Scalar"].DataType);
        Assert.Equal(IgwDataType.Float, result["ns=3;s=Custom"].DataType);
        Assert.Equal(IgwDataType.Byte, result["ns=3;s=Bytes"].DataType);
        Assert.Equal(new[] { 8 }, result["ns=3;s=Bytes"].Dimensions);
        Assert.All(result.Values, type => Assert.Equal(TagAccess.Read, type.Access));
    }

    private static Opc.Ua.DataValue Attribute(ReadValueId request, NodeId type, int rank, uint[] dimensions) =>
        new(new Variant(request.AttributeId switch
        {
            Attributes.DataType => (object)type,
            Attributes.ValueRank => rank,
            Attributes.AccessLevel => (byte)3,
            Attributes.UserAccessLevel => (byte)1,
            Attributes.ArrayDimensions => dimensions,
            _ => throw new InvalidOperationException("Unexpected value read"),
        }));

    [Fact]
    public void Browsing_standard_scalars_does_not_request_live_values()
    {
        var requested = new List<uint>();
        var result = OpcUaClientDeviceDriver.ReadBrowseTypes(["ns=3;s=Amount"], batch =>
            new DataValueCollection(batch.Select(request =>
            {
                requested.Add(request.AttributeId);
                return new Opc.Ua.DataValue(new Variant(request.AttributeId switch
                {
                    Attributes.DataType => (object)DataTypeIds.Float,
                    Attributes.ValueRank => ValueRanks.Scalar,
                    Attributes.AccessLevel or Attributes.UserAccessLevel => (byte)3,
                    Attributes.ArrayDimensions => Array.Empty<uint>(),
                    _ => 1.5f,
                }));
            })));
        Assert.Equal(IgwDataType.Float, result["ns=3;s=Amount"].DataType);
        Assert.DoesNotContain(Attributes.Value, requested);
    }
    private static OpcUaBrowseType Describe(object? value, string? type, int rank = -1, uint status = StatusCodes.Good)
    {
        var source = typeof(OpcUaClientDeviceDriver).GetNestedType("SessionBrowseSource", BindingFlags.NonPublic)!;
        var method = source.GetMethod("DescribeType", BindingFlags.Static | BindingFlags.NonPublic)!;
        return (OpcUaBrowseType)method.Invoke(null,
            [new Opc.Ua.DataValue(new Variant(value)) { StatusCode = status }, "ns=3;s=test", rank, null, type])!;
    }

    [Theory]
    [InlineData("i=10", IgwDataType.Float)]
    [InlineData("i=4", IgwDataType.Int16)]
    [InlineData("i=7", IgwDataType.UInt32)]
    public void Declared_type_survives_an_unavailable_current_value(string type, IgwDataType expected)
    {
        var result = Describe(null, type);
        Assert.Equal(expected, result.DataType);
        Assert.False(result.IsArray);
    }

    [Fact]
    public void Declared_float_is_not_replaced_by_a_double_sample()
        => Assert.Equal(IgwDataType.Float, Describe(0d, "i=10").DataType);

    [Fact]
    public void ByteString_is_a_byte_buffer_not_an_array_of_doubles()
    {
        var result = Describe(new byte[] { 144, 1, 0, 0 }, "i=15");
        Assert.Equal(IgwDataType.ByteArray, result.DataType);
        Assert.False(result.IsArray);
    }

    [Fact]
    public void Byte_array_preserves_its_element_type_and_shape()
    {
        var result = Describe(new byte[] { 1, 2, 3 }, "i=3", 1);
        Assert.Equal(IgwDataType.Byte, result.DataType);
        Assert.True(result.IsArray);
        Assert.Equal(new[] { 3 }, result.Dimensions);
    }

    [Fact]
    public void Custom_type_uses_the_typed_sample()
        => Assert.Equal(IgwDataType.Float, Describe(1.25f, "ns=3;i=3000").DataType);

    [Fact]
    public void Bad_value_quality_does_not_hide_readable_type_metadata()
        => Assert.Equal(IgwDataType.Int16, Describe(null, "i=4", status: StatusCodes.BadNotReadable).DataType);

    [Fact]
    public void Array_of_byte_strings_keeps_byte_buffers_as_elements()
    {
        var result = Describe(new byte[][] { [1, 2], [3, 4] }, "i=15", 1);
        Assert.Equal(IgwDataType.ByteArray, result.DataType);
        Assert.True(result.IsArray);
        Assert.Equal(new[] { 2 }, result.Dimensions);
    }

    [Fact]
    public void Unresolved_type_is_reported_instead_of_inventing_double()
    {
        var error = Assert.Throws<TargetInvocationException>(() => Describe(null, "ns=3;i=3000"));
        Assert.IsType<InvalidOperationException>(error.InnerException);
    }

    [Fact]
    public void Attribute_reads_reduce_capacity_and_preserve_response_order()
    {
        var requests = new ReadValueIdCollection(Enumerable.Range(0, 18)
            .Select(i => new ReadValueId { NodeId = new NodeId((uint)i), AttributeId = Attributes.Value }));
        var attempts = new List<int>();
        Func<ReadValueIdCollection, DataValueCollection> read = batch =>
        {
            attempts.Add(batch.Count);
            if (batch.Count > 2) throw new ServiceResultException(StatusCodes.BadTooManyOperations);
            return new DataValueCollection(batch.Select(r => new Opc.Ua.DataValue(new Variant(r.NodeId.Identifier))));
        };
        var result = OpcUaClientDeviceDriver.ReadBrowseAttributes(requests, read);
        Assert.Equal(Enumerable.Range(0, 18).Select(i => (uint)i), result.Select(v => (uint)v.Value));
        Assert.Contains(attempts, count => count > 2);
    }

    [Fact]
    public void Single_attribute_capacity_failure_is_not_retried_forever()
    {
        var requests = new ReadValueIdCollection { new() { NodeId = new NodeId(1u) } };
        int calls = 0;
        Assert.Throws<ServiceResultException>(() => OpcUaClientDeviceDriver.ReadBrowseAttributes(requests, _ =>
        {
            calls++;
            throw new ServiceResultException(StatusCodes.BadTooManyOperations);
        }));
        Assert.Equal(1, calls);
    }
}
