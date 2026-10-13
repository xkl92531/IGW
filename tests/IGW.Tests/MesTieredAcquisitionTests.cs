using System.Net;
using System.Text;
using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using IGW.Northbound.Mes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

/// <summary>
/// 分层采集：只有判定链路(触发/条件/握手/长度点)常驻轮询，上报点位在触发条件满足后按需批量读取。
/// </summary>
public sealed class MesTieredAcquisitionTests
{
    private const string Device = "Line.Plc";
    private const string TriggerTag = "Line.Plc.DB.Trig";
    private const string ConditionTag = "Line.Plc.DB.Ready";
    private const string HandshakeTag = "Line.Plc.DB.Ack";
    private const string PayloadTag = "Line.Plc.DB.Value";
    private const string PayloadTag2 = "Line.Plc.DB.Serial";

    [Fact]
    public async Task Tiered_acquisition_polls_only_the_decision_points()
    {
        await using var hub = NewHub();
        var demands = new ScanDemandRegistry(hub);
        await using var engine = new MesUploadEngine(hub, Settings(Task()), NoWrite,
            NullLogger.Instance, scanDemands: demands);

        var polled = demands.Snapshot(Device).EffectivePeriods.Select(item => item.TagId).ToHashSet(
            StringComparer.OrdinalIgnoreCase);

        Assert.Contains(TriggerTag, polled);
        Assert.Contains(ConditionTag, polled);
        Assert.Contains(HandshakeTag, polled);
        // 上报点位不再按采集周期常态轮询——这正是现场 924 点负载的来源。
        Assert.DoesNotContain(PayloadTag, polled);
        Assert.DoesNotContain(PayloadTag2, polled);
    }

    [Fact]
    public async Task Task_level_opt_out_restores_full_polling()
    {
        await using var hub = NewHub();
        var demands = new ScanDemandRegistry(hub);
        var task = Task();
        task.TieredAcquisition = false;
        await using var engine = new MesUploadEngine(hub, Settings(task), NoWrite,
            NullLogger.Instance, scanDemands: demands);

        var polled = demands.Snapshot(Device).EffectivePeriods.Select(item => item.TagId).ToHashSet(
            StringComparer.OrdinalIgnoreCase);

        Assert.Contains(TriggerTag, polled);
        Assert.Contains(PayloadTag, polled);
        Assert.Contains(PayloadTag2, polled);
    }

    [Fact]
    public async Task Global_switch_off_restores_full_polling()
    {
        await using var hub = NewHub();
        var demands = new ScanDemandRegistry(hub);
        var settings = Settings(Task());
        settings.TieredAcquisition = false;
        await using var engine = new MesUploadEngine(hub, settings, NoWrite,
            NullLogger.Instance, scanDemands: demands);

        var polled = demands.Snapshot(Device).EffectivePeriods.Select(item => item.TagId).ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        Assert.Contains(PayloadTag, polled);
    }

    [Fact]
    public async Task Trigger_pulls_payload_points_before_building_the_upload_body()
    {
        await using var hub = NewHub();
        var handler = new CapturingHandler();
        var requested = new List<string>();
        var conditionReads = new List<string>();

        Task<OnDemandReadResult> ReadNow(IReadOnlyCollection<string> tagIds, CancellationToken ct)
        {
            // 每个周期判断前先现读判定点；上报点另外按需拉取，两次读取互不混在一起
            if (!tagIds.Contains(PayloadTag))
            {
                lock (conditionReads) conditionReads.AddRange(tagIds);
                return System.Threading.Tasks.Task.FromResult(new OnDemandReadResult(tagIds.Count, tagIds.Count, null));
            }
            lock (requested) requested.AddRange(tagIds);
            // 模拟一次真实 PLC 读取：触发时刻 PLC 上的值已经变成 99。
            hub.Update(PayloadTag, new DataValue((short)99, StatusCode.Good, DateTime.UtcNow, DateTime.UtcNow));
            return System.Threading.Tasks.Task.FromResult(new OnDemandReadResult(tagIds.Count, tagIds.Count, null));
        }

        await using var engine = new MesUploadEngine(hub, Settings(Task()), NoWrite,
            NullLogger.Instance, httpMessageHandler: handler, readNow: ReadNow);

        await Until(() => handler.LastRequestBody is not null);

        lock (requested)
        {
            Assert.Contains(PayloadTag, requested);
            Assert.Contains(PayloadTag2, requested);
            // 判定点在判断前已经现读过，上报点的按需拉取不重复读它们。
            Assert.DoesNotContain(TriggerTag, requested);
            Assert.DoesNotContain(ConditionTag, requested);
        }
        lock (conditionReads) Assert.Contains(ConditionTag, conditionReads);
        using var json = JsonDocument.Parse(handler.LastRequestBody!);
        // 上报的是按需读取拿到的触发时刻真实值，而不是上一轮缓存里的 1。
        Assert.Equal(99, json.RootElement.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task Failed_pull_keeps_the_flow_running_on_cached_values()
    {
        await using var hub = NewHub();
        var handler = new CapturingHandler();
        await using var engine = new MesUploadEngine(hub, Settings(Task()), NoWrite,
            NullLogger.Instance, httpMessageHandler: handler,
            readNow: (_, _) => throw new IOException("PLC 断线"));

        hub.Update(TriggerTag, new DataValue(true, StatusCode.Good, DateTime.UtcNow, DateTime.UtcNow));
        await Until(() => handler.LastRequestBody is not null);

        using var json = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal(1, json.RootElement.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task Flow_without_a_condition_keeps_full_polling_and_never_pulls()
    {
        await using var hub = NewHub();
        var demands = new ScanDemandRegistry(hub);
        var task = Task();
        // 触发节点直接接上报：每个周期都要上报，分层没有意义。
        task.Flow!.Nodes.RemoveAll(node => node.Type == "Condition");
        task.Flow.Edges = [new MesEdge { From = "trigger", To = "upload" }];
        var handler = new CapturingHandler();
        int pulls = 0;
        await using var engine = new MesUploadEngine(hub, Settings(task), NoWrite, NullLogger.Instance,
            scanDemands: demands, httpMessageHandler: handler, readNow: (ids, _) =>
            {
                Interlocked.Increment(ref pulls);
                return System.Threading.Tasks.Task.FromResult(new OnDemandReadResult(ids.Count, ids.Count, null));
            });

        var polled = demands.Snapshot(Device).EffectivePeriods.Select(item => item.TagId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains(PayloadTag, polled);
        Assert.Contains(PayloadTag2, polled);
        await Until(() => handler.LastRequestBody is not null);
        Assert.Equal(0, Volatile.Read(ref pulls));
    }

    [Fact]
    public async Task Step_task_with_conditions_pulls_payload_before_building_the_body()
    {
        await using var hub = NewHub();
        var demands = new ScanDemandRegistry(hub);
        var handler = new CapturingHandler();
        var requested = new List<string>();
        var task = StepTask(withCondition: true);
        await using var engine = new MesUploadEngine(hub, Settings(task), NoWrite, NullLogger.Instance,
            scanDemands: demands, httpMessageHandler: handler, readNow: (ids, _) =>
            {
                lock (requested) requested.AddRange(ids);
                hub.Update(PayloadTag, new DataValue((short)99, StatusCode.Good, DateTime.UtcNow, DateTime.UtcNow));
                return System.Threading.Tasks.Task.FromResult(new OnDemandReadResult(ids.Count, ids.Count, null));
            });

        var polled = demands.Snapshot(Device).EffectivePeriods.Select(item => item.TagId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains(ConditionTag, polled);
        Assert.DoesNotContain(PayloadTag, polled);

        await Until(() => handler.LastRequestBody is not null);
        lock (requested) Assert.Contains(PayloadTag, requested);
        using var json = JsonDocument.Parse(handler.LastRequestBody!);
        // 步骤模式同样必须上报触发时刻的真实值，而不是一个不再被轮询的旧缓存。
        Assert.Equal(99, json.RootElement.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task Step_task_with_an_unconditional_step_keeps_full_polling()
    {
        await using var hub = NewHub();
        var demands = new ScanDemandRegistry(hub);
        await using var engine = new MesUploadEngine(hub, Settings(StepTask(withCondition: false)), NoWrite,
            NullLogger.Instance, scanDemands: demands);

        var polled = demands.Snapshot(Device).EffectivePeriods.Select(item => item.TagId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains(PayloadTag, polled);
    }

    [Fact]
    public async Task Failed_upload_retries_reuse_the_recent_pull_instead_of_rereading_every_cycle()
    {
        await using var hub = NewHub();
        var handler = new CapturingHandler { Fail = true };
        int pulls = 0;
        await using var engine = new MesUploadEngine(hub, Settings(Task()), NoWrite, NullLogger.Instance,
            httpMessageHandler: handler, readNow: (ids, _) =>
            {
                if (ids.Contains(PayloadTag)) Interlocked.Increment(ref pulls);   // 只数上报点的拉取
                return System.Threading.Tasks.Task.FromResult(new OnDemandReadResult(ids.Count, ids.Count, null));
            });

        // 周期 20ms，1 秒内会重试几十次；握手未完成期间只允许拉取一次。
        await Until(() => handler.Requests >= 10);
        Assert.Equal(1, Volatile.Read(ref pulls));

        // 复用窗口过后的重试重新读取，避免 PLC 数据在长时间失败后已经变化。
        await System.Threading.Tasks.Task.Delay(MesUploadEngine.RetryPullReuseMs + 300);
        await Until(() => Volatile.Read(ref pulls) >= 2);
    }

    [Fact]
    public async Task Fixed_value_writeback_branch_does_not_pull_the_payload()
    {
        await using var hub = NewHub();
        var task = Task();
        // 握手确认分支：条件满足后只写固定值，不需要任何 PLC 当前值。
        task.Flow!.Nodes.RemoveAll(node => node.Type == "Upload");
        task.Flow.Nodes.Add(new MesNode
        {
            Id = "ack",
            Type = "Writeback",
            WriteBack = new MesWriteBack { Manual = [new MesManualWrite { TagId = HandshakeTag, Value = "true" }] },
        });
        task.Flow.Edges = [new MesEdge { From = "trigger", To = "condition" }, new MesEdge { From = "condition", FromPort = "b0", To = "ack" }];
        int writes = 0, pulls = 0;
        await using var engine = new MesUploadEngine(hub, Settings(task),
            (_, _, _) => { Interlocked.Increment(ref writes); return System.Threading.Tasks.Task.FromResult(true); },
            NullLogger.Instance, readNow: (ids, _) =>
            {
                if (ids.Contains(PayloadTag)) Interlocked.Increment(ref pulls);   // 只数上报点的拉取
                return System.Threading.Tasks.Task.FromResult(new OnDemandReadResult(ids.Count, ids.Count, null));
            });

        await Until(() => Volatile.Read(ref writes) > 0);
        Assert.Equal(0, Volatile.Read(ref pulls));
    }

    [Fact]
    public async Task Writeback_copying_a_plc_value_pulls_the_payload_first()
    {
        await using var hub = NewHub();
        var task = Task();
        task.Flow!.Nodes.RemoveAll(node => node.Type == "Upload");
        task.Flow.Nodes.Add(new MesNode
        {
            Id = "copy",
            Type = "Writeback",
            WriteBack = new MesWriteBack { Manual = [new MesManualWrite { TagId = HandshakeTag, CopyFromTagId = PayloadTag }] },
        });
        task.Flow.Edges = [new MesEdge { From = "trigger", To = "condition" }, new MesEdge { From = "condition", FromPort = "b0", To = "copy" }];
        var written = new List<object>();
        var requested = new List<string>();
        await using var engine = new MesUploadEngine(hub, Settings(task),
            (_, value, _) => { lock (written) written.Add(value); return System.Threading.Tasks.Task.FromResult(true); },
            NullLogger.Instance, readNow: (ids, _) =>
            {
                lock (requested) requested.AddRange(ids);
                hub.Update(PayloadTag, new DataValue((short)77, StatusCode.Good, DateTime.UtcNow, DateTime.UtcNow));
                return System.Threading.Tasks.Task.FromResult(new OnDemandReadResult(ids.Count, ids.Count, null));
            });

        await Until(() => { lock (written) return written.Count > 0; });
        lock (requested) Assert.Contains(PayloadTag, requested);
        lock (written) Assert.Equal("77", Convert.ToString(written[0], System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Tiered_pull_reads_only_the_uploaded_fields_not_the_whole_bound_group()
    {
        const string unused = "Line.Plc.DB.Unused";
        await using var hub = NewHub();
        hub.Register(unused, new TagDef { Name = unused, Address = "ns=2;s=" + unused, DataType = IgwDataType.Float }, Device);
        var task = Task();
        task.GroupRef = "Line.Plc.DB";
        var demands = new ScanDemandRegistry(hub);
        var handler = new CapturingHandler();
        var requested = new List<string>();
        await using var engine = new MesUploadEngine(hub, Settings(task), NoWrite, NullLogger.Instance,
            scanDemands: demands, httpMessageHandler: handler, readNow: (ids, _) =>
            {
                lock (requested) requested.AddRange(ids);
                return System.Threading.Tasks.Task.FromResult(new OnDemandReadResult(ids.Count, ids.Count, null));
            });

        await Until(() => handler.LastRequestBody is not null);
        lock (requested)
        {
            Assert.Contains(PayloadTag, requested);
            // 报文只由字段拼成：整组其他点不参与上报，触发时不读。
            Assert.DoesNotContain(unused, requested);
        }
        var polled = demands.Snapshot(Device).EffectivePeriods.Select(item => item.TagId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(unused, polled);
    }

    [Fact]
    public async Task Tiered_pull_reads_points_of_nested_child_structs()
    {
        // UDT 数组：外层结构体自己没有成员，成员都在子结构体里(Cells[i].Glue.Len)
        const string len0 = "Line.Plc.DB.Cells[0].Glue.Len", len1 = "Line.Plc.DB.Cells[1].Glue.Len";
        await using var hub = NewHub();
        foreach (var (tagId, value) in new[] { (len0, 1.5f), (len1, 2.5f) })
        {
            hub.Register(tagId, new TagDef { Name = "Len", Address = "ns=2;s=" + tagId, DataType = IgwDataType.Float }, Device);
            hub.Update(tagId, new DataValue(0f, StatusCode.Good, DateTime.UtcNow, DateTime.UtcNow));
        }
        var task = Task();
        var payload = task.Flow.Nodes.Single(node => node.Type == "Upload").Payload!;
        payload.Structs.Add(new MesStruct
        {
            Kind = "Array", CountMode = "Enum", BaseGroup = "Line.Plc.DB.Cells", AliasName = "cells", StartIndex = 0, FixedCount = 2,
            Structs = [new MesStruct { Kind = "Object", BaseGroup = "Glue", AliasName = "glue", Members = [new MesField { TagId = "Len", FieldName = "len" }] }],
        });
        var demands = new ScanDemandRegistry(hub);
        var handler = new CapturingHandler();
        var requested = new List<string>();
        await using var engine = new MesUploadEngine(hub, Settings(task), NoWrite, NullLogger.Instance,
            scanDemands: demands, httpMessageHandler: handler, readNow: (ids, _) =>
            {
                lock (requested) requested.AddRange(ids);
                hub.Update(len0, new DataValue(1.5f, StatusCode.Good, DateTime.UtcNow, DateTime.UtcNow));
                hub.Update(len1, new DataValue(2.5f, StatusCode.Good, DateTime.UtcNow, DateTime.UtcNow));
                return System.Threading.Tasks.Task.FromResult(new OnDemandReadResult(ids.Count, ids.Count, null));
            });

        await Until(() => handler.LastRequestBody is not null);
        lock (requested)
        {
            Assert.Contains(len0, requested);
            Assert.Contains(len1, requested);
        }
        Assert.Contains("\"cells\":[{\"glue\":{\"len\":1.5}},{\"glue\":{\"len\":2.5}}]", handler.LastRequestBody!);
    }

    [Fact]
    public async Task Untiered_task_polls_only_the_configured_points_not_its_whole_bound_group()
    {
        const string unused = "Line.Plc.DB.Unused";
        await using var hub = NewHub();
        hub.Register(unused, new TagDef { Name = unused, Address = "ns=2;s=" + unused, DataType = IgwDataType.Float }, Device);
        var task = Task();
        task.GroupRef = "Line.Plc.DB";
        task.TieredAcquisition = false;
        var demands = new ScanDemandRegistry(hub);
        await using var engine = new MesUploadEngine(hub, Settings(task), NoWrite, NullLogger.Instance, scanDemands: demands);

        var polled = demands.Snapshot(Device).EffectivePeriods.Select(item => item.TagId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains(PayloadTag, polled);
        Assert.Contains(TriggerTag, polled);
        Assert.DoesNotContain(unused, polled);
    }

    [Theory]
    [InlineData("Upload", false, true)]
    [InlineData("Writeback", false, false)]
    [InlineData("ResponseWriteback", false, false)]
    [InlineData("Writeback", true, true)]
    [InlineData("Branch", false, false)]
    [InlineData("Delay", false, false)]
    public void Only_nodes_that_consume_plc_values_trigger_the_pull(string type, bool copies, bool expected)
    {
        var node = new MesNode { Type = type };
        node.WriteBack.Manual.Add(new MesManualWrite { TagId = HandshakeTag, Value = "1", CopyFromTagId = copies ? PayloadTag : "" });
        Assert.Equal(expected, MesUploadEngine.NeedsPayloadValues(node));
    }

    private static MesTask StepTask(bool withCondition) => new()
    {
        Id = "steps",
        Name = "Steps",
        CollectIntervalMs = 20,
        GroupRef = "",
        Payload = new MesPayload
        {
            Mode = MesPayloadModes.FieldMapping,
            Fields =
            [
                new MesField { TagId = PayloadTag, FieldName = "value" },
                new MesField { TagId = PayloadTag2, FieldName = "serial" },
            ],
        },
        Steps =
        [
            new MesStep
            {
                Name = "report",
                Http = new MesHttp { Url = "http://local/report" },
                Conditions = withCondition
                    ? [new MesCondition { Source = "Tag", TagId = ConditionTag, Op = "Eq", Value = "true" }]
                    : [],
            },
        ],
    };

    private static MesUploadSettings Settings(MesTask task) => new() { Tasks = [task], LogDbPath = "" };

    private static Task<bool> NoWrite(string tagId, object value, CancellationToken ct) =>
        System.Threading.Tasks.Task.FromResult(true);

    private static MesTask Task() => new()
    {
        Id = "tiered",
        Name = "Tiered",
        CollectIntervalMs = 20,
        GroupRef = "",
        Trigger = new MesTrigger { Mode = "OnChange", TagId = TriggerTag, Op = "Eq", Value = "true" },
        Handshake = new MesHandshake { PlcFlagTagId = HandshakeTag },
        Flow = new MesFlow
        {
            Nodes =
            [
                new MesNode { Id = "trigger", Type = "Trigger" },
                new MesNode
                {
                    Id = "condition",
                    Type = "Condition",
                    Branches = [new MesCondBranch { Conditions = [new MesCondition
                    {
                        Source = "Tag", TagId = ConditionTag, Op = "Eq", Value = "true",
                    }] }],
                },
                new MesNode
                {
                    Id = "upload",
                    Type = "Upload",
                    Http = new MesHttp { Url = "http://local/report" },
                    Payload = new MesPayload
                    {
                        Mode = MesPayloadModes.FieldMapping,
                        Fields =
                        [
                            new MesField { TagId = PayloadTag, FieldName = "value" },
                            new MesField { TagId = PayloadTag2, FieldName = "serial" },
                        ],
                    },
                },
            ],
            Edges =
            [
                new MesEdge { From = "trigger", To = "condition" },
                new MesEdge { From = "condition", FromPort = "b0", To = "upload" },
            ],
        },
    };

    private static DataHub NewHub()
    {
        var hub = new DataHub();
        void Add(string tagId, IgwDataType type, object value)
        {
            hub.Register(tagId, new TagDef { Name = tagId, Address = "ns=2;s=" + tagId, DataType = type }, Device);
            hub.Update(tagId, new DataValue(value, StatusCode.Good, DateTime.UtcNow, DateTime.UtcNow));
        }
        Add(TriggerTag, IgwDataType.Bool, false);
        Add(ConditionTag, IgwDataType.Bool, true);
        Add(HandshakeTag, IgwDataType.Bool, false);
        Add(PayloadTag, IgwDataType.Int16, (short)1);
        Add(PayloadTag2, IgwDataType.String, "SN-1");
        return hub;
    }

    private static async Task Until(Func<bool> done)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        while (!done()) await System.Threading.Tasks.Task.Delay(10, timeout.Token);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public volatile string? LastRequestBody;
        public bool Fail { get; init; }
        private int _requests;
        public int Requests => Volatile.Read(ref _requests);
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            LastRequestBody = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(Fail ? HttpStatusCode.InternalServerError : HttpStatusCode.OK)
            {
                Content = new StringContent(Fail ? "{\"ok\":false}" : "{\"ok\":true}", Encoding.UTF8, "application/json"),
            };
        }
    }
}
