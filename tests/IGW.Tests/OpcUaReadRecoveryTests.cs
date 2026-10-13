using System.Reflection;
using IGW.Abstractions;
using IGW.Drivers.OpcUaClient;
using Opc.Ua;
using Xunit;
using UaValue = Opc.Ua.DataValue;

namespace IGW.Tests;

public sealed class OpcUaReadRecoveryTests
{
    [Fact]
    public async Task Continuous_512_node_service_timeout_is_bounded_to_one_physical_request()
    {
        await using var driver = new OpcUaClientDeviceDriver("opc.tcp://recovery-test.invalid:4840", 100, true, "", "", "None");
        var tags = Enumerable.Range(0, 512).Select(i => new TagBinding
        {
            TagId = $"L.P.T{i}",
            Def = new TagDef { Address = $"ns=2;s=T{i}", DataType = IgwDataType.Int16 },
            Address = new(0, 0, 2),
        }).ToArray();
        int calls = 0;

        using var timing = ProtocolOperationTiming.Begin();
        // 整批第一个请求就超时：没有任何可用数据，按批次级失败抛出，交回调度器按周期重试。
        var failure = await Assert.ThrowsAsync<DriverOperationException>(() => driver.ReadCoreAsync(tags, (_, _) =>
        {
            calls++;
            throw new TimeoutException("offline injected service timeout");
        }, () => true, default));

        Assert.Equal(1, calls);
        Assert.Equal(1, timing.Snapshot().PhysicalRequests);
        Assert.Equal(DriverFailureScope.Block, failure.Scope);
        Assert.Equal(DriverRetryDisposition.RetrySameConnection, failure.Retry);
        // 超时不是容量不足：批量窗口必须保持，否则同样的点位下次要拆成更多物理请求，
        // 反而加重 PLC 负载并引发更多超时。
        Assert.Equal(512, driver.Limits.MaxTagsPerRead);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Later_service_timeout_preserves_good_prefix_and_marks_unread_remainder_with_actual_quality(bool clrTimeout)
    {
        var window = new AdaptiveRequestWindow(4, 8);
        await using var driver = new OpcUaClientDeviceDriver("opc.tcp://recovery-test.invalid:4840", 100, true, "", "", "None");
        typeof(OpcUaClientDeviceDriver).GetField("_readWindow", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(driver, window);
        var tags = Enumerable.Range(0, 10).Select(i => new TagBinding
        {
            TagId = $"L.P.T{i}",
            Def = new TagDef { Address = $"ns=2;s=T{i}", DataType = IgwDataType.Int16 },
            Address = new(0, 0, 2),
        }).ToArray();
        var calls = new List<int[]>();
        Task<DataValueCollection> Read(ReadValueIdCollection requests, CancellationToken ct)
        {
            int[] ids = requests.Select(r => int.Parse(((string)r.NodeId.Identifier)[1..])).ToArray();
            calls.Add(ids);
            if (calls.Count == 1)
                return Task.FromResult(new DataValueCollection(ids.Select(i => new UaValue((short)(100 + i)))));
            return Task.FromException<DataValueCollection>(clrTimeout
                ? new TimeoutException("offline injected service timeout")
                : new ServiceResultException(StatusCodes.BadRequestTimeout));
        }

        var values = await driver.ReadCoreAsync(tags, Read, () => true, default);

        Assert.Equal(2, calls.Count);
        Assert.Equal(new[] { 0, 1, 2, 3 }, calls[0]);
        Assert.Equal(new[] { 4, 5, 6, 7 }, calls[1]);
        for (int i = 0; i < 4; i++)
        {
            Assert.True(values[i].Value.IsGood);
            Assert.Equal((short)(100 + i), values[i].Value.Value);
        }
        var expectedTimeout = clrTimeout
            ? IGW.Abstractions.StatusCode.BadTimeout
            : new IGW.Abstractions.StatusCode(StatusCodes.BadRequestTimeout);
        Assert.All(values.Skip(4), value => Assert.Equal(expectedTimeout, value.Value.Quality));
        // 后续分片超时不丢弃已成功读到的前缀，同时保持批量窗口不变。
        Assert.Equal(4, driver.Limits.MaxTagsPerRead);
    }

    [Fact]
    public async Task Non_timeout_node_error_keeps_peer_values_and_exact_node_quality()
    {
        await using var driver = new OpcUaClientDeviceDriver("opc.tcp://recovery-test.invalid:4840", 100, true, "", "", "None");
        var tags = Enumerable.Range(0, 3).Select(i => new TagBinding
        {
            TagId = $"L.P.T{i}",
            Def = new TagDef { Address = $"ns=2;s=T{i}", DataType = IgwDataType.Int16 },
            Address = new(0, 0, 2),
        }).ToArray();
        int calls = 0;

        var values = await driver.ReadCoreAsync(tags, (_, _) =>
        {
            calls++;
            return Task.FromResult(new DataValueCollection
            {
                new UaValue((short)100),
                new UaValue { StatusCode = StatusCodes.BadNodeIdUnknown },
                new UaValue((short)102),
            });
        }, () => true, default);

        Assert.Equal(1, calls);
        Assert.Equal((short)100, values[0].Value.Value);
        Assert.True(values[0].Value.IsGood);
        Assert.Equal(new IGW.Abstractions.StatusCode(StatusCodes.BadNodeIdUnknown), values[1].Value.Quality);
        Assert.Equal((short)102, values[2].Value.Value);
        Assert.True(values[2].Value.IsGood);
    }

    [Theory]
    [InlineData("request")]
    [InlineData("timeout")]
    [InlineData("wrapped")]
    [InlineData("service")]
    public async Task Timeout_aborts_batch_without_splitting_retrying_or_reducing_capacity(string kind)
    {
        await using var driver = new OpcUaClientDeviceDriver("opc.tcp://recovery-test.invalid:4840", 100, true, "", "", "None");
        var window = new AdaptiveRequestWindow(8, 8);
        typeof(OpcUaClientDeviceDriver).GetField("_readWindow", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(driver, window);
        var tags = Enumerable.Range(0, 8).Select(i => new TagBinding { TagId = $"T{i}",
            Def = new TagDef { Address = $"ns=2;s=T{i}" }, Address = new(0, 0, 2) }).ToArray();
        Exception error = kind switch
        {
            "timeout" => new TimeoutException("no reply"),
            "wrapped" => new IOException("request failed", new TimeoutException("no reply")),
            "service" => new ServiceResultException(StatusCodes.BadTimeout),
            _ => new ServiceResultException(StatusCodes.BadRequestTimeout),
        };
        int calls = 0;
        var failure = await Assert.ThrowsAsync<DriverOperationException>(() => driver.ReadCoreAsync(tags,
            (_, _) => { calls++; throw error; }, () => true, default));
        Assert.Equal(1, calls);
        Assert.Equal(8, window.Current);
        Assert.Equal(DriverFailureScope.Block, failure.Scope);
        Assert.Equal(DriverRetryDisposition.RetrySameConnection, failure.Retry);
        Assert.False(DriverOperationFailure.FromException(failure, ConnectionState.Connected).ConnectionLost);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Later_capacity_failure_preserves_prefix_and_trailing_points_without_learning_recovery(bool singletonFails)
    {
        long clock = 0;
        var window = new AdaptiveRequestWindow(4, 8, () => clock);
        await using var driver = new OpcUaClientDeviceDriver("opc.tcp://recovery-test.invalid:4840", 100, true, "", "", "None");
        typeof(OpcUaClientDeviceDriver).GetField("_readWindow", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(driver, window);
        var tags = Enumerable.Range(0, 10).Select(i => new TagBinding { TagId = $"L.P.T{i}",
            Def = new TagDef { Address = $"ns=2;s=T{i}", DataType = IgwDataType.Int16 }, Address = new(0, 0, 2) }).ToArray();
        var calls = new List<int[]>();
        Task<DataValueCollection> Read(ReadValueIdCollection requests, CancellationToken ct)
        {
            int[] ids = requests.Select(r => int.Parse(((string)r.NodeId.Identifier)[1..])).ToArray();
            calls.Add(ids);
            if (ids.Contains(5) && (ids.Length > (singletonFails ? 0 : 2)))
                throw new ServiceResultException(StatusCodes.BadTooManyOperations);
            return Task.FromResult(new DataValueCollection(ids.Select(i => new UaValue((short)(100 + i)))));
        }
        using var timing = ProtocolOperationTiming.Begin();
        var values = await driver.ReadCoreAsync(tags, Read, () => true, default);
        Assert.Equal(tags.Select(t => t.TagId), values.Select(v => v.TagId));
        Assert.Equal(new[] { 0, 1, 2, 3 }, calls[0]);
        Assert.DoesNotContain(calls.Skip(1), call => call.Any(i => i < 4));
        Assert.Contains(calls, call => call.Contains(9));
        for (int i = 0; i < values.Count; i++)
        {
            if (singletonFails && i == 5) Assert.Equal(new IGW.Abstractions.StatusCode(StatusCodes.BadTooManyOperations), values[i].Value.Quality);
            else { Assert.True(values[i].Value.IsGood); Assert.Equal((short)(100 + i), values[i].Value.Value); }
        }
        Assert.Equal(calls.Count, timing.Snapshot().PhysicalRequests);
        if (!singletonFails)
        {
            Assert.Equal(new[] { 4, 4, 2, 2, 2 }, calls.Select(c => c.Length));
            clock = 60_000;
            for (int i = 0; i < 18; i++) Assert.False(window.OnSuccess(2, 1));
            Assert.True(window.OnSuccess(2, 1)); // Only the independent tail was learned.
        }
    }

    [Fact]
    public async Task Transport_error_aborts_without_reducing_or_retrying_the_window()
    {
        await using var driver = new OpcUaClientDeviceDriver("opc.tcp://recovery-test.invalid:4840", 100, true, "", "", "None");
        int calls = 0;
        var tag = new TagBinding { TagId = "T", Def = new TagDef { Address = "ns=2;s=T" }, Address = new(0, 0, 2) };
        var error = await Assert.ThrowsAsync<DriverOperationException>(() => driver.ReadCoreAsync([tag],
            (_, _) => { calls++; throw new ServiceResultException(StatusCodes.BadConnectionClosed); }, () => true, default));
        Assert.Equal(DriverFailureScope.Transport, error.Scope);
        Assert.Equal(1, calls); Assert.Equal(512, driver.Limits.MaxTagsPerRead);
    }
}
