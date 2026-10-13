using IGW.Abstractions;
using IGW.Core;
using IGW.Runtime;
using Xunit;

namespace IGW.Tests;

public sealed class ScopedApplyPolicyTests
{
    private static DeviceDef Plain() => new() { Name = "D", Tags = [new TagDef { Name = "T", Address = "DB1.X" }] };

    private static IgwProject With(DeviceDef device, string channel = "C") =>
        new() { Channels = [new ChannelDef { Name = channel, Devices = [device] }] };

    private static ProjectPatchSummary Changed(string device)
    {
        var summary = new ProjectPatchSummary();
        summary.ChangedDevices.Add(device);
        return summary;
    }

    [Fact]
    public void Plain_device_with_unrelated_project_needs_neither_full_apply_nor_components()
    {
        var project = With(Plain());
        Assert.False(ScopedApplyPolicy.RequiresFullApply(project, project, Changed("C.D")));
        Assert.Equal(NorthboundComponents.None, ScopedApplyPolicy.ComponentsToRestart(project, project, Changed("C.D")));
        Assert.Equal(NorthboundComponents.None,
            ScopedApplyPolicy.ComponentsReferencingTags(project, ["C.D.T"], ["C.D.T2"], ["C.D"]));
    }

    [Fact]
    public void Alarmed_tag_restarts_alarms_only()
    {
        var device = Plain();
        device.Tags[0].Alarm = new AlarmDef();
        var project = With(device);
        Assert.False(ScopedApplyPolicy.RequiresFullApply(project, project, Changed("C.D")));
        Assert.Equal(NorthboundComponents.Alarms, ScopedApplyPolicy.ComponentsToRestart(project, project, Changed("C.D")));
    }

    [Fact]
    public void Template_instance_needs_full_apply()
    {
        var device = Plain();
        device.TemplateRef = "tpl";
        var project = With(device);
        Assert.True(ScopedApplyPolicy.RequiresFullApply(project, project, Changed("C.D")));
    }

    [Fact]
    public void Template_ref_on_old_device_needs_full_apply()
    {
        var oldDevice = Plain();
        oldDevice.TemplateRef = "tpl";
        Assert.True(ScopedApplyPolicy.RequiresFullApply(With(oldDevice), With(Plain()), Changed("C.D")));
    }

    [Fact]
    public void Mes_reference_to_removed_or_added_tag_restarts_mes()
    {
        var project = With(Plain());
        project.MesUpload.Tasks.Add(new MesTask { Id = "m", Name = "m", DeviceRef = "C.D.T" });
        Assert.Equal(NorthboundComponents.Mes, ScopedApplyPolicy.ComponentsReferencingTags(project, ["C.D.T"], [], ["C.D"]));
        Assert.Equal(NorthboundComponents.Mes, ScopedApplyPolicy.ComponentsReferencingTags(project, [], ["C.D.T"], ["C.D"]));
        // 同路径点位复用条目(未注销、未新登记)时 MES 订阅照常有效，不重建
        Assert.Equal(NorthboundComponents.None, ScopedApplyPolicy.ComponentsReferencingTags(project, [], [], ["C.D"]));
    }

    [Fact]
    public void Disabled_mes_task_does_not_restart_mes()
    {
        var project = With(Plain());
        project.MesUpload.Tasks.Add(new MesTask { Id = "m", Name = "m", DeviceRef = "C.D.T", Enabled = false });
        Assert.Equal(NorthboundComponents.None, ScopedApplyPolicy.ComponentsReferencingTags(project, ["C.D.T"], [], ["C.D"]));
    }

    [Fact]
    public void Removed_path_referenced_by_mes_restarts_mes_before_device_apply()
    {
        var project = With(Plain());
        project.MesUpload.Tasks.Add(new MesTask { Id = "m", Name = "m", DeviceRef = "C.D.G.T" });
        var summary = Changed("C.D");
        summary.RemovedPaths.Add("C.D.G");
        Assert.Equal(NorthboundComponents.Mes, ScopedApplyPolicy.ComponentsToRestart(project, project, summary));
    }

    [Fact]
    public void Removed_device_referenced_by_computed_tag_restarts_computed()
    {
        var project = new IgwProject();
        project.ComputedTags.Add(new ComputedTagDef { Name = "Computed.X", Expression = "{C.Old.T} + 1" });
        var summary = new ProjectPatchSummary();
        summary.RemovedDevices.Add("C.Old");
        Assert.Equal(NorthboundComponents.Computed, ScopedApplyPolicy.ComponentsToRestart(project, project, summary));
    }

    // --- 中文设备名 + 各段 + 精确匹配 + 旧设备报警 + 分组内嵌套点报警 ---

    private const string ChineseDevice = "线体.电芯上料";

    [Fact]
    public void Chinese_tag_referenced_from_mes_task_restarts_mes()
    {
        var project = new IgwProject();
        project.MesUpload.Tasks.Add(new MesTask { Id = "m", Name = "m", DeviceRef = ChineseDevice + ".T1" });
        Assert.Equal(NorthboundComponents.Mes,
            ScopedApplyPolicy.ComponentsReferencingTags(project, [ChineseDevice + ".T1"], [], [ChineseDevice]));
    }

    [Fact]
    public void Chinese_tag_referenced_from_computed_tags_restarts_computed()
    {
        var project = new IgwProject();
        project.ComputedTags.Add(new ComputedTagDef { Name = "Computed.X", Expression = "{" + ChineseDevice + ".Temp}" });
        Assert.Equal(NorthboundComponents.Computed,
            ScopedApplyPolicy.ComponentsReferencingTags(project, [], [ChineseDevice + ".Temp"], [ChineseDevice]));
    }

    [Fact]
    public void Chinese_tag_referenced_from_schedules_restarts_scheduler()
    {
        var project = new IgwProject();
        project.Schedules.Add(new ScheduleJobDef { Name = "s", TagId = ChineseDevice + ".T1" });
        Assert.Equal(NorthboundComponents.Scheduler,
            ScopedApplyPolicy.ComponentsReferencingTags(project, [ChineseDevice + ".T1"], [], [ChineseDevice]));
    }

    [Fact]
    public void Many_changed_tags_fall_back_to_device_prefix_references()
    {
        var project = new IgwProject();
        project.MesUpload.Tasks.Add(new MesTask { Id = "m", Name = "m", DeviceRef = "C.D" });
        string[] added = Enumerable.Range(0, ScopedApplyPolicy.MaxExactTagProbes + 1).Select(i => $"C.D.N{i}").ToArray();
        Assert.Equal(NorthboundComponents.Mes, ScopedApplyPolicy.ComponentsReferencingTags(project, [], added, ["C.D"]));
    }

    [Fact]
    public void Alarmed_tag_on_old_device_only_restarts_alarms()
    {
        var oldDevice = Plain();
        oldDevice.Tags[0].Alarm = new AlarmDef();
        Assert.Equal(NorthboundComponents.Alarms,
            ScopedApplyPolicy.ComponentsToRestart(With(oldDevice), With(Plain()), Changed("C.D")));
    }

    [Fact]
    public void Alarmed_tag_in_nested_group_restarts_alarms()
    {
        var device = Plain();
        device.Groups.Add(new TagGroupDef
        {
            Name = "G1",
            SubGroups =
            [
                new TagGroupDef
                {
                    Name = "G2",
                    Tags = [new TagDef { Name = "T2", Address = "DB1.Y", Alarm = new AlarmDef() }],
                },
            ],
        });
        var project = With(device);
        Assert.Equal(NorthboundComponents.Alarms, ScopedApplyPolicy.ComponentsToRestart(project, project, Changed("C.D")));
    }
}
