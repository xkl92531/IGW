using IGW.Abstractions;
using IGW.Drivers.OpcUaClient;
using Opc.Ua;
using Xunit;
using UaValue = Opc.Ua.DataValue;

namespace IGW.Tests;

/// <summary>订阅分组与注册句柄在请求上的体现。</summary>
public sealed class OpcUaRequestShapingTests
{
    private static TagBinding Tag(string id, int? periodMs, int scanRateMs = 1000) => new()
    {
        TagId = id,
        Def = new TagDef { Address = "ns=3;s=" + id, DataType = IgwDataType.Int16, ScanRateMs = scanRateMs },
        Address = new(0, 0, 2),
        RequestedSamplingIntervalMs = periodMs,
    };

    [Fact]
    public void Subscriptions_never_mix_sampling_periods()
    {
        TagBinding[] tags =
        [
            Tag("fast-1", 200), Tag("slow-1", 5000), Tag("fast-2", 200),
            Tag("default", null, scanRateMs: 1000), Tag("slow-2", 5000),
        ];

        var plan = OpcUaClientDeviceDriver.PlanSubscriptions(tags, 5000);

        Assert.Equal([200, 1000, 5000], plan.Select(item => item.PeriodMs));
        Assert.Equal(["fast-1", "fast-2"], plan[0].Tags.Select(tag => tag.TagId));
        Assert.Equal(["default"], plan[1].Tags.Select(tag => tag.TagId));
        Assert.Equal(["slow-1", "slow-2"], plan[2].Tags.Select(tag => tag.TagId));
        // 慢组不再被快点拖着按 200ms 发布。
        Assert.Equal(1000, OpcUaClientDeviceDriver.PublishingIntervalFor(plan[2].PeriodMs));
        Assert.Equal(200, OpcUaClientDeviceDriver.PublishingIntervalFor(plan[0].PeriodMs));
    }

    [Fact]
    public void Large_period_groups_are_split_by_subscription_capacity()
    {
        var tags = Enumerable.Range(0, 11).Select(i => Tag("t" + i, 500)).ToArray();

        var plan = OpcUaClientDeviceDriver.PlanSubscriptions(tags, 4);

        Assert.Equal([4, 4, 3], plan.Select(item => item.Tags.Count));
        Assert.All(plan, item => Assert.Equal(500, item.PeriodMs));
        Assert.Equal(tags.Select(tag => tag.TagId), plan.SelectMany(item => item.Tags).Select(tag => tag.TagId));
    }

    [Fact]
    public async Task Read_requests_carry_registered_handles_while_samples_keep_tag_identity()
    {
        await using var driver = new OpcUaClientDeviceDriver("opc.tcp://shaping-test.invalid:4840", 100, true, "", "", "None");
        var tags = Enumerable.Range(0, 3).Select(i => Tag("L.P.T" + i, null)).ToArray();
        var sent = new List<NodeId>();
        int mapperCalls = 0;

        var values = await driver.ReadCoreAsync(tags, (requests, _) =>
        {
            sent.AddRange(requests.Select(request => request.NodeId));
            return Task.FromResult(new DataValueCollection(requests.Select((_, i) => new UaValue((short)(10 + i)))));
        }, () => true, default, (nodes, _) =>
        {
            mapperCalls++;
            return Task.FromResult(nodes.Select((_, i) => new NodeId((uint)(500 + i), 3)).ToArray());
        });

        Assert.Equal(1, mapperCalls);
        Assert.Equal([500u, 501u, 502u], sent.Select(node => (uint)node.Identifier));
        Assert.Equal(tags.Select(tag => tag.TagId), values.Select(sample => sample.TagId));
        Assert.Equal([(short)10, (short)11, (short)12], values.Select(sample => (short)sample.Value.Value!));
    }

    [Fact]
    public async Task Registration_time_is_not_counted_as_read_latency()
    {
        await using var driver = new OpcUaClientDeviceDriver("opc.tcp://shaping-test.invalid:4840", 100, true, "", "", "None");
        var tags = new[] { Tag("L.P.A", null) };
        using var timing = ProtocolOperationTiming.Begin();

        await driver.ReadCoreAsync(tags, (requests, _) =>
            Task.FromResult(new DataValueCollection(requests.Select(_ => new UaValue((short)1)))),
            () => true, default, async (nodes, _) =>
            {
                await Task.Delay(200);
                return nodes.ToArray();
            });

        var snapshot = timing.Snapshot();
        Assert.Equal(1, snapshot.Requests);
        Assert.True(snapshot.RequestMaxMs < 150, $"读取计时混入了注册耗时：{snapshot.RequestMaxMs}ms");
    }
}
