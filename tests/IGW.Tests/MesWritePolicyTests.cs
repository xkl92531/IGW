using System.Collections.Concurrent;
using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using IGW.Northbound.Mes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

/// <summary>
/// 回写节点上的「写后确认」和「重试次数」要真的传到引擎。
///
/// 这两项在界面上一直摆着，但历史实现只把它们记进日志、从不往下传，
/// 引擎固定按「回读验证 + 最多重写 10 次」执行。改了不生效，比没有这个选项更糟：
/// 现场按界面理解去调，结果与实际行为对不上，排查时没人会怀疑到配置项本身。
/// </summary>
public sealed class MesWritePolicyTests
{
    [Fact]
    public async Task Write_back_node_settings_reach_the_engine()
    {
        var policies = new ConcurrentQueue<(string TagId, WritePolicy Policy)>();
        await RunWriteBackAsync(new MesWriteBack
        {
            Confirm = true,
            Retries = 3,
            Manual = [new MesManualWrite { TagId = Tag, Value = "7" }],
        }, policies);

        var captured = Captured(policies);
        Assert.True(captured.Verify);
        Assert.Equal(3, captured.MaxRewrites);
    }

    /// <summary>关掉确认要真的关掉，不能还在背地里回读重写。</summary>
    [Fact]
    public async Task Turning_confirmation_off_reaches_the_engine()
    {
        var policies = new ConcurrentQueue<(string TagId, WritePolicy Policy)>();
        await RunWriteBackAsync(new MesWriteBack
        {
            Confirm = false,
            Retries = 3,
            Manual = [new MesManualWrite { TagId = Tag, Value = "7" }],
        }, policies);

        Assert.False(Captured(policies).Verify);
    }

    /// <summary>
    /// 任务级总开关关掉时，逐节点配的确认也要一起失效——
    /// 引擎里的判定是 step.WriteBack.Confirm &amp;&amp; task.ConfirmRewrite，两者都要成立。
    /// </summary>
    [Fact]
    public async Task The_task_level_switch_still_overrides_the_node()
    {
        var policies = new ConcurrentQueue<(string TagId, WritePolicy Policy)>();
        await RunWriteBackAsync(new MesWriteBack
        {
            Confirm = true,
            Retries = 3,
            Manual = [new MesManualWrite { TagId = Tag, Value = "7" }],
        }, policies, confirmRewrite: false);

        Assert.False(Captured(policies).Verify);
    }

    /// <summary>
    /// 默认值要和引擎历来的行为对齐。这个字段过去从未生效，存量工程里存着的旧默认值
    /// 不代表谁的配置意图；若照搬旧的 2，现场重写上限会从 10 掉到 2，
    /// 写入成功率下降而且极难归因到这次改动。
    /// </summary>
    [Fact]
    public void The_default_matches_what_the_engine_has_always_done()
    {
        var fresh = new MesWriteBack();

        Assert.True(fresh.Confirm);
        Assert.Equal(WritePolicy.Default.MaxRewrites, fresh.Retries);
    }

    /// <summary>
    /// 存量工程加载回来也必须是 10。
    ///
    /// 只改 C# 默认值救不了它们:JSON 里存着 "retries": 2 就用 2,轮不到默认值。
    /// 而那个 2 没人配过——界面上根本没有入口,是序列化自己写进去的
    /// (int 不是 null,WhenWritingNull 拦不住,每次保存都把当时的默认值固化)。
    /// 漏掉这条,现场重写上限就会从 10 悄悄退到 2。
    /// </summary>
    [Fact]
    public void Existing_projects_that_stored_the_old_default_still_get_ten()
    {
        var stored = JsonSerializer.Deserialize<MesWriteBack>(
            """{"fromResponse":[],"manual":[],"confirm":true,"retries":2}""", ProjectStore.Options)!;

        Assert.Equal(MesWriteBack.DefaultRetries, stored.Retries);
    }

    /// <summary>界面上配的其它值要原样留着,归一只针对那个旧默认值。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(20)]
    public void A_value_someone_actually_configured_is_kept(int configured)
    {
        var stored = JsonSerializer.Deserialize<MesWriteBack>(
            $$"""{"fromResponse":[],"manual":[],"confirm":true,"retries":{{configured}}}""",
            ProjectStore.Options)!;

        Assert.Equal(configured, stored.Retries);
    }

    /// <summary>新建的节点存库时写出的就是 10,不再把旧默认值传下去。</summary>
    [Fact]
    public void A_new_node_persists_ten()
    {
        string json = JsonSerializer.Serialize(new MesWriteBack(), ProjectStore.Options);

        Assert.Contains($"\"retries\": {MesWriteBack.DefaultRetries}", json);
    }

    /// <summary>
    /// 任务是 20ms 一轮的，机器忙一点就会多跑一轮、多捕获一条。
    /// 断言的是策略内容而不是调用次数，所以要求每条都一致、至少有一条，
    /// 不能用 Assert.Single——那样测试的成败取决于机器当时有多闲。
    /// </summary>
    private static WritePolicy Captured(ConcurrentQueue<(string TagId, WritePolicy Policy)> policies)
    {
        var mine = policies.ToArray().Where(item => item.TagId == Tag).Select(item => item.Policy).ToArray();
        Assert.NotEmpty(mine);
        Assert.All(mine, policy => Assert.Equal(mine[0], policy));
        return mine[0];
    }

    private const string Tag = "Channel.Device.DB1.Flag";

    private static async Task RunWriteBackAsync(
        MesWriteBack writeBack,
        ConcurrentQueue<(string TagId, WritePolicy Policy)> policies,
        bool confirmRewrite = true)
    {
        await using var hub = new DataHub();
        hub.Register(Tag, new TagDef { Name = "Flag", DataType = IgwDataType.Int16, Access = TagAccess.ReadWrite });
        hub.Update(Tag, DataValue.GoodAt((short)0, DateTime.UtcNow));

        var task = new MesTask
        {
            Id = "policy", Name = "policy", Enabled = true,
            CollectIntervalMs = 20, ConfirmRewrite = confirmRewrite,
            Flow = new MesFlow
            {
                Nodes =
                [
                    new MesNode { Id = "trigger", Type = "Trigger" },
                    new MesNode { Id = "write", Type = "ResponseWriteback", WriteBack = writeBack },
                ],
                Edges = [new MesEdge { From = "trigger", To = "write" }],
            },
        };

        await using var engine = new MesUploadEngine(
            hub, new MesUploadSettings { Enabled = true, Tasks = [task] },
            (id, _, policy, _) =>
            {
                policies.Enqueue((id, policy));
                return Task.FromResult(StatusCode.Good);
            },
            NullLogger.Instance);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline && !policies.Any(item => item.TagId == Tag))
            await Task.Delay(20);
    }
}
