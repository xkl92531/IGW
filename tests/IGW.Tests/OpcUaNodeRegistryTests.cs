using IGW.Drivers.OpcUaClient;
using Opc.Ua;
using Xunit;

namespace IGW.Tests;

public sealed class OpcUaNodeRegistryTests
{
    private static NodeId Symbolic(int i) => new($"\"DB_Station\".\"Data\"[{i}].\"Value\"", 3);

    [Fact]
    public async Task Registers_once_and_reuses_handles_in_later_requests()
    {
        var calls = new List<int>();
        var registry = new OpcUaNodeRegistry((nodes, _) =>
        {
            calls.Add(nodes.Count);
            return Task.FromResult(new NodeIdCollection(nodes.Select((_, i) => new NodeId((uint)(1000 + calls.Count * 100 + i), 3))));
        }, batchSize: 1000, report: _ => { });
        NodeId[] nodes = [Symbolic(1), Symbolic(2)];

        NodeId[] first = await registry.MapAsync(nodes, default);
        NodeId[] second = await registry.MapAsync(nodes, default);

        Assert.Equal([2], calls);
        Assert.All(first, node => Assert.Equal(IdType.Numeric, node.IdType));
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Splits_registration_by_the_server_limit_and_keeps_order()
    {
        var calls = new List<int>();
        var registry = new OpcUaNodeRegistry((nodes, _) =>
        {
            calls.Add(nodes.Count);
            return Task.FromResult(new NodeIdCollection(nodes.Select(node =>
                new NodeId((uint)(10_000 + int.Parse(((string)node.Identifier).Split('[', ']')[1])), 3))));
        }, batchSize: 3, report: _ => { });
        var nodes = Enumerable.Range(0, 7).Select(Symbolic).ToArray();

        NodeId[] mapped = await registry.MapAsync(nodes, default);

        Assert.Equal([3, 3, 1], calls);
        Assert.Equal(Enumerable.Range(0, 7).Select(i => (uint)(10_000 + i)), mapped.Select(node => (uint)node.Identifier));
    }

    [Fact]
    public async Task Unsupported_service_disables_registration_for_the_session_without_failing_requests()
    {
        int calls = 0;
        var reports = new List<string>();
        var registry = new OpcUaNodeRegistry((_, _) =>
        {
            calls++;
            throw new ServiceResultException(StatusCodes.BadServiceUnsupported);
        }, batchSize: 100, report: reports.Add);
        NodeId[] nodes = [Symbolic(1)];

        Assert.Equal(nodes, await registry.MapAsync(nodes, default));
        Assert.Equal(nodes, await registry.MapAsync(nodes, default));

        Assert.Equal(1, calls);
        Assert.True(registry.Disabled);
        Assert.Single(reports);
    }

    [Fact]
    public async Task Transient_failure_backs_off_then_retries()
    {
        long now = 0;
        int calls = 0;
        var registry = new OpcUaNodeRegistry((nodes, _) =>
        {
            if (++calls == 1) throw new ServiceResultException(StatusCodes.BadTimeout);
            return Task.FromResult(new NodeIdCollection(nodes.Select((_, i) => new NodeId((uint)(50 + i), 3))));
        }, batchSize: 100, report: _ => { }, clock: () => now);
        NodeId[] nodes = [Symbolic(1)];

        Assert.Equal(nodes, await registry.MapAsync(nodes, default));
        now += OpcUaNodeRegistry.RetryAfterFailureMs - 1;
        Assert.Equal(nodes, await registry.MapAsync(nodes, default));
        Assert.Equal(1, calls);

        now += 1;
        NodeId[] mapped = await registry.MapAsync(nodes, default);
        Assert.Equal(2, calls);
        Assert.Equal(IdType.Numeric, Assert.Single(mapped).IdType);
        Assert.False(registry.Disabled);
    }

    [Fact]
    public async Task Malformed_response_disables_registration_instead_of_mapping_to_wrong_nodes()
    {
        var registry = new OpcUaNodeRegistry((nodes, _) =>
            Task.FromResult(new NodeIdCollection([new NodeId(1u, 3)])), batchSize: 100, report: _ => { });
        NodeId[] nodes = [Symbolic(1), Symbolic(2)];

        Assert.Equal(nodes, await registry.MapAsync(nodes, default));
        Assert.True(registry.Disabled);
        Assert.Equal(0, registry.RegisteredCount);
    }

    [Fact]
    public async Task Stops_registering_at_the_cap_and_leaves_the_rest_unmapped()
    {
        var registry = new OpcUaNodeRegistry((nodes, _) =>
            Task.FromResult(new NodeIdCollection(nodes.Select((_, i) => new NodeId((uint)(700 + i), 3)))),
            batchSize: 100, report: _ => { }, maxRegistered: 2);
        var nodes = Enumerable.Range(0, 3).Select(Symbolic).ToArray();

        NodeId[] mapped = await registry.MapAsync(nodes, default);

        Assert.Equal(2, registry.RegisteredCount);
        Assert.Equal(IdType.Numeric, mapped[0].IdType);
        Assert.Equal(IdType.Numeric, mapped[1].IdType);
        Assert.Equal(nodes[2], mapped[2]);
    }

    [Fact]
    public async Task Concurrent_requests_register_each_node_once()
    {
        int registered = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new OpcUaNodeRegistry(async (nodes, _) =>
        {
            Interlocked.Add(ref registered, nodes.Count);
            await release.Task;
            return new NodeIdCollection(nodes.Select((_, i) => new NodeId((uint)(900 + i), 3)));
        }, batchSize: 100, report: _ => { });
        NodeId[] nodes = [Symbolic(1), Symbolic(2)];

        var a = registry.MapAsync(nodes, default);
        var b = registry.MapAsync(nodes, default);
        release.SetResult();
        await Task.WhenAll(a, b);

        Assert.Equal(2, registered);
        Assert.Equal(await a, await b);
    }

    [Fact]
    public void NodeId_cache_parses_once_and_remembers_invalid_addresses()
    {
        var cache = new OpcUaNodeIdCache();
        Assert.True(cache.TryGet("ns=3;s=\"DB\".\"A\"", out var first));
        Assert.True(cache.TryGet("ns=3;s=\"DB\".\"A\"", out var second));
        Assert.Equal(first, second);
        Assert.False(cache.TryGet("not a node id ;;;", out var invalid));
        Assert.True(NodeId.IsNull(invalid));
        Assert.False(cache.TryGet("not a node id ;;;", out _));
    }
}
