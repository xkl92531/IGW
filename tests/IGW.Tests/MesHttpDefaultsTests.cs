using IGW.Abstractions;
using IGW.Core;
using IGW.Northbound.Mes;
using IGW.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class MesHttpDefaultsTests
{
    [Fact]
    public void Individual_task_controls_runtime_without_legacy_global_gate()
    {
        var settings = new MesUploadSettings
        {
            Enabled = false,
            Tasks = [new MesTask { Id = "op090-1", Name = "OP090_1", Enabled = true }],
        };

        Assert.True(MesRuntimePolicy.ShouldRun(settings));
    }

    [Fact]
    public void Runtime_stops_when_all_individual_tasks_are_disabled()
    {
        var settings = new MesUploadSettings
        {
            Enabled = true,
            Tasks =
            [
                new MesTask { Id = "first", Enabled = false },
                new MesTask { Id = "second", Enabled = false },
            ],
        };

        Assert.False(MesRuntimePolicy.ShouldRun(settings));
    }

    [Fact]
    public void Closed_collection_flow_feature_cannot_start_runtime_even_if_legacy_running_flag_is_true()
    {
        var task = new MesTask { Id = "closed-flow", Enabled = true };
        var property = typeof(MesTask).GetProperty("FlowAvailable");
        Assert.NotNull(property);
        property!.SetValue(task, false);

        Assert.False(MesRuntimePolicy.ShouldRun(new MesUploadSettings { Tasks = [task] }));
    }

    [Fact]
    public void Validation_does_not_rewrite_the_legacy_global_switch_from_an_individual_task()
    {
        var project = new IgwProject
        {
            MesUpload = new MesUploadSettings
            {
                Enabled = false,
                Tasks = [new MesTask { Id = "op090-1", Name = "OP090_1", Enabled = true }],
            },
        };

        ProjectStore.Validate(project);

        Assert.False(project.MesUpload.Enabled);
    }

    [Fact]
    public void New_upload_uses_http_status_200_as_default_success_rule()
    {
        var http = new MesHttp();

        Assert.Equal("httpStatusCode", http.SuccessKey);
        Assert.Equal("200", http.SuccessValue);
    }

    [Fact]
    public async Task Waiting_trigger_age_tracks_the_latest_collection_cycle()
    {
        const string tagId = "Line.Plc.DB.Request";
        await using var hub = new DataHub();
        hub.Register(tagId, new TagDef
        {
            Name = "Request",
            Address = "DB1.DBW0",
            DataType = IgwDataType.Int16,
            ScanRateMs = 50,
        });
        hub.Update(tagId, DataValue.GoodAt((short)0, DateTime.UtcNow));

        var task = new MesTask
        {
            Id = "trigger-runtime",
            Name = "trigger-runtime",
            Enabled = true,
            CollectIntervalMs = 50,
            Flow = new MesFlow
            {
                Nodes =
                [
                    new MesNode { Id = "trigger", Type = "Trigger", Title = "采集周期" },
                    new MesNode
                    {
                        Id = "condition",
                        Type = "Condition",
                        Branches =
                        [
                            new MesCondBranch
                            {
                                Label = "有请求",
                                Conditions = [new MesCondition { TagId = tagId, Op = "Eq", Value = "1" }],
                            },
                        ],
                    },
                ],
                Edges = [new MesEdge { From = "trigger", To = "condition" }],
            },
        };

        await using var engine = new MesUploadEngine(
            hub,
            new MesUploadSettings { Enabled = true, Tasks = [task] },
            (_, _, _) => Task.FromResult(true),
            NullLogger.Instance);

        await WaitUntilAsync(() => TryTrigger(engine)?.Hits >= 10, TimeSpan.FromSeconds(2));
        long priorHits = Trigger(engine).Hits;
        await WaitUntilAsync(() => TryTrigger(engine)?.Hits > priorHits, TimeSpan.FromSeconds(2));

        var trigger = Trigger(engine);
        Assert.Equal("wait", trigger.State);
        Assert.InRange(trigger.AgeMs, 0, 150);
    }

    [Fact]
    public async Task Timeline_resolves_duplicate_task_names_by_stable_task_id()
    {
        await using var hub = new DataHub();
        var first = WaitingTask("first", "OP090_1", "trigger-first", "condition-first");
        var second = WaitingTask("second", "OP090_1", "trigger-second", "condition-second");
        await using var engine = new MesUploadEngine(
            hub,
            new MesUploadSettings { Enabled = true, Tasks = [first, second] },
            (_, _, _) => Task.FromResult(true),
            NullLogger.Instance);

        await WaitUntilAsync(() => engine.Snapshot()
            .Single(task => task.Id == "second").Nodes.Any(node => node.Id == "condition-second"),
            TimeSpan.FromSeconds(2));

        Assert.Contains(engine.Timeline("second"), item => item.NodeId == "condition-second");
        Assert.Empty(engine.Timeline("OP090_1"));
    }

    private static MesTask WaitingTask(string id, string name, string triggerId, string conditionId) => new()
    {
        Id = id,
        Name = name,
        Enabled = true,
        CollectIntervalMs = 20,
        Flow = new MesFlow
        {
            Nodes =
            [
                new MesNode { Id = triggerId, Type = "Trigger" },
                new MesNode
                {
                    Id = conditionId,
                    Type = "Condition",
                    Branches = [new MesCondBranch { Label = "never", Conditions = [new MesCondition()] }],
                },
            ],
            Edges = [new MesEdge { From = triggerId, To = conditionId }],
        },
    };

    private static MesNodeStatus Trigger(MesUploadEngine engine) =>
        engine.Snapshot().Single().Nodes.Single(node => node.Id == "trigger");

    private static MesNodeStatus? TryTrigger(MesUploadEngine engine) =>
        engine.Snapshot().Single().Nodes.SingleOrDefault(node => node.Id == "trigger");

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException();
            await Task.Delay(5);
        }
    }
}
