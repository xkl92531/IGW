using System.Text;
using IGW.Abstractions;
using IGW.Drivers.OpcUaClient;
using Xunit;

namespace IGW.Tests;

public sealed class OpcUaStringWriteFallbackTests
{
    [Fact]
    public void Utf8_capacity_truncation_does_not_split_a_chinese_character()
    {
        string actual = OpcUaStringWriteFallback.TruncateToCapacity(
            "ABC中文测试", 10, IgwDataType.String, PlcTextEncoding.Utf8);

        Assert.Equal("ABC中文", actual);
        Assert.True(Encoding.UTF8.GetByteCount(actual) <= 10);
    }

    [Fact]
    public async Task BadOutOfRange_finds_the_largest_writable_prefix_within_ten_attempts()
    {
        const string original = "无序列号【3130186|0093130186K8T1001X|V1028740】信息";
        const int plcCapacityBytes = 50;
        var attempts = new List<string>();

        var result = await OpcUaStringWriteFallback.WriteAsync(
            original,
            IgwDataType.String,
            PlcTextEncoding.Utf8,
            knownCapacity: null,
            maxAttempts: 10,
            (value, _) =>
            {
                attempts.Add(value);
                return Task.FromResult(Encoding.UTF8.GetByteCount(value) <= plcCapacityBytes
                    ? StatusCode.Good
                    : new StatusCode(0x803C0000));
            },
            CancellationToken.None);

        Assert.Equal(StatusCode.Good, result.Status);
        Assert.InRange(result.Attempts, 2, 10);
        Assert.Equal(plcCapacityBytes, Encoding.UTF8.GetByteCount(result.AppliedValue));
        Assert.Equal(result.Attempts, attempts.Count);
    }

    [Fact]
    public async Task Very_long_old_config_value_still_finds_a_non_empty_writable_prefix_within_ten_attempts()
    {
        string original = new('A', 65_536);

        var result = await OpcUaStringWriteFallback.WriteAsync(
            original,
            IgwDataType.String,
            PlcTextEncoding.Utf8,
            knownCapacity: null,
            maxAttempts: 10,
            (value, _) => Task.FromResult(value.Length <= 50
                ? StatusCode.Good
                : new StatusCode(0x803C0000)),
            CancellationToken.None);

        Assert.Equal(StatusCode.Good, result.Status);
        Assert.InRange(result.Attempts, 2, 10);
        Assert.InRange(result.AppliedValue.Length, 1, 50);
    }

    [Fact]
    public void Wstring_capacity_and_measurement_use_the_same_unicode_scalar_unit()
    {
        Assert.Equal("A😀", OpcUaStringWriteFallback.TruncateToCapacity(
            "A😀B", 2, IgwDataType.WString, null));
        Assert.Equal(2, OpcUaStringWriteFallback.EncodedLength("A😀", IgwDataType.WString, null));
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(100, null, 100)]
    [InlineData(null, 50, 50)]
    [InlineData(100, 50, 50)]
    [InlineData(50, 100, 50)]
    public void Verified_session_capacity_never_loses_to_a_larger_declared_capacity(
        int? declared, int? verified, int? expected)
    {
        Assert.Equal(expected, OpcUaStringWriteFallback.EffectiveCapacity(declared, verified));
    }

    [Fact]
    public async Task Non_range_rejection_is_returned_without_string_retries()
    {
        var result = await OpcUaStringWriteFallback.WriteAsync(
            "message",
            IgwDataType.String,
            PlcTextEncoding.Utf8,
            knownCapacity: null,
            maxAttempts: 10,
            (_, _) => Task.FromResult(StatusCode.BadNotWritable),
            CancellationToken.None);

        Assert.Equal(StatusCode.BadNotWritable, result.Status);
        Assert.Equal(1, result.Attempts);
        Assert.Equal("message", result.AppliedValue);
    }

    [Theory]
    [InlineData("ns=3;s=\"DB8411\".\"StationEntrance\".\"Message\"", "ns=3;s=\"DB8411\".\"StationEntrance\"", "Message")]
    [InlineData("ns=3;s=Root.Child.Message", "ns=3;s=Root.Child", "Message")]
    public void Siemens_symbolic_child_node_is_split_into_parent_and_field(
        string child, string expectedParent, string expectedField)
    {
        Assert.True(OpcUaStringLengthResolver.TrySplitParentNode(
            Opc.Ua.NodeId.Parse(child), out var parent, out var field));
        Assert.Equal(expectedParent, parent.ToString());
        Assert.Equal(expectedField, field);
    }
}
