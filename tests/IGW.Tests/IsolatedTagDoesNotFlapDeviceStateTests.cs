using IGW.Abstractions;
using IGW.Core;
using Xunit;

namespace IGW.Tests;

/// <summary>
/// 一个配错的点位不能让整台设备在「在线」和「部分在线」之间来回跳。
///
/// 现场表现：某个符号 PLC 里不存在 → 连续 3 次后被隔离、每 30 秒复探一次。
/// 隔离期间它不参与读取，那一轮全是好值 → 设备「在线」；复探那一轮读到坏值 → 设备「部分在线」。
/// 于是设备状态按 30 秒周期闪烁，而问题本身早就识别清楚、也在监控中心的点位异常里逐点记着了。
/// 已隔离 = 已处理，不该再计入设备级的部分在线判定。
/// </summary>
public sealed class IsolatedTagDoesNotFlapDeviceStateTests
{
    private static TagBinding Binding(string tagId) => new()
    {
        TagId = tagId,
        Def = new TagDef { Name = tagId, Address = "DB1." + tagId, DataType = IgwDataType.Bool },
        Address = new TagAddress(0, 0, 1),
    };

    private static TagSample Bad(string tagId, StatusCode status) =>
        new(tagId, DataValue.BadAt(status, DateTime.UtcNow));

    private static TagSample Good(string tagId) =>
        new(tagId, new DataValue(true, StatusCode.Good, DateTime.UtcNow, DateTime.UtcNow));

    /// <summary>连续 3 次 BadNodeIdUnknown 后该点被隔离，其余点位不受影响。</summary>
    [Fact]
    public void A_missing_symbol_is_isolated_after_three_reads_and_others_stay_clear()
    {
        var bindings = new[] { Binding("Missing"), Binding("Fine") };
        var policy = new InvalidNodeReadPolicy(
            bindings.ToDictionary(binding => binding.TagId, binding => binding, StringComparer.Ordinal));

        for (int round = 1; round <= 3; round++)
        {
            policy.Record(bindings,
                [Bad("Missing", StatusCode.BadNodeIdUnknown), Good("Fine")], round * 1000);
            Assert.Equal(round == 3, policy.IsIsolated("Missing"));
            Assert.False(policy.IsIsolated("Fine"));
        }
        Assert.Equal(1, policy.IsolatedCount);
    }

    /// <summary>隔离期该点不再被选进读取计划，其余点位照常读。</summary>
    [Fact]
    public void An_isolated_tag_is_skipped_until_its_probe_is_due()
    {
        var bindings = new[] { Binding("Missing"), Binding("Fine") };
        var policy = new InvalidNodeReadPolicy(
            bindings.ToDictionary(binding => binding.TagId, binding => binding, StringComparer.Ordinal));
        for (int round = 1; round <= 3; round++)
            policy.Record(bindings, [Bad("Missing", StatusCode.BadNodeIdUnknown), Good("Fine")], round * 1000);

        var due = policy.SelectDue(bindings, 4000, out int nextProbeMs);

        Assert.Equal(["Fine"], due.Select(binding => binding.TagId));
        Assert.InRange(nextProbeMs, 1, 30_000);

        // 复探时刻到了就放回读取计划（问题没修好的话会继续隔离，但设备状态不受影响）
        var afterProbe = policy.SelectDue(bindings, 3000 + 30_000 + 1, out _);
        Assert.Equal(2, afterProbe.Count);
    }

    /// <summary>恢复成好值后隔离解除。</summary>
    [Fact]
    public void Fixing_the_tag_clears_the_isolation()
    {
        var bindings = new[] { Binding("Missing") };
        var policy = new InvalidNodeReadPolicy(
            bindings.ToDictionary(binding => binding.TagId, binding => binding, StringComparer.Ordinal));
        for (int round = 1; round <= 3; round++)
            policy.Record(bindings, [Bad("Missing", StatusCode.BadNodeIdUnknown)], round * 1000);
        Assert.True(policy.IsIsolated("Missing"));

        policy.Record(bindings, [Good("Missing")], 40_000);

        Assert.False(policy.IsIsolated("Missing"));
        Assert.Equal(0, policy.IsolatedCount);
    }
}
