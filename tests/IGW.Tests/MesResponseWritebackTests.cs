using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using IGW.Northbound.Mes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class MesResponseWritebackTests
{
    [Fact]
    public async Task ResponseWriteback_WritesActualUnderscoreStructTagsAndConfiguredMesFlag()
    {
        const string root = "S7Sim.Sim.DB2100_电芯OCV_DataDB";
        string mesFlag = $"{root}.MESFlag";
        string kValue = $"{root}.CellData_0.KValue";
        string isNg = $"{root}.CellData_0.IsNG";
        string errorMessage = $"{root}.CellData_0.ErrorMessage";
        string errorInfo = $"{root}.CellData_0.Errorinfo";
        string explicitResult = $"{root}.ExplicitResult";
        await using var hub = new DataHub();
        foreach (var item in new[]
                 {
                     (Id: mesFlag, Type: IgwDataType.Int16, Value: (object)(short)0),
                     (Id: kValue, Type: IgwDataType.Float, Value: (object)0f),
                     (Id: isNg, Type: IgwDataType.Int16, Value: (object)(short)0),
                     (Id: errorMessage, Type: IgwDataType.String, Value: (object)""),
                     (Id: errorInfo, Type: IgwDataType.Int16, Value: (object)(short)0),
                     (Id: explicitResult, Type: IgwDataType.String, Value: (object)""),
                 })
        {
            hub.Register(item.Id, new TagDef { Name = item.Id.Split('.').Last(), DataType = item.Type, Access = TagAccess.ReadWrite });
            hub.Update(item.Id, DataValue.GoodAt(item.Value, DateTime.UtcNow));
        }

        var handler = new StaticResponseHandler("""
            {
              "value": {
                "httpStatusCode": 200,
                "isSuccess": true,
                "backResult": {
                  "mesFlag": 4,
                  "plcFlag": 1,
                  "temperature": 25,
                  "diagnostic": "accepted",
                  "structures": [
                    {
                      "dmcCode": "A55BAE5050126V1861072460103603",
                      "kValue": 0.00088,
                      "voltage": 3580,
                      "isNg": 1,
                      "ngReason": "工序跳站！",
                      "errorInfo": ""
                    }
                  ]
                },
                "errorMessage": null,
                "exceptionMessage": null
              },
              "formatters": [],
              "contentTypes": [],
              "declaredType": null,
              "statusCode": 200
            }
            """);
        var writes = new ConcurrentQueue<(string TagId, object Value)>();
        var task = new MesTask
        {
            Id = "actual-db2100-response",
            Name = "actual-db2100-response",
            Enabled = true,
            CollectIntervalMs = 20,
            ConfirmRewrite = false,
            Flow = new MesFlow
            {
                Nodes =
                [
                    new MesNode { Id = "trigger", Type = "Trigger" },
                    new MesNode
                    {
                        Id = "upload", Type = "Upload",
                        Http = new MesHttp { Url = "http://localhost/judge" },
                        Payload = new MesPayload
                        {
                            Mode = MesPayloadModes.FieldMapping,
                            Fields = [new MesField { TagId = mesFlag, FieldName = "MESFlag" }],
                            Structs =
                            [
                                new MesStruct
                                {
                                    Kind = "Array", BaseGroup = $"{root}.CellData", AliasName = "Structures",
                                    CountMode = "Enum", StartIndex = 0, FixedCount = 1,
                                    Members =
                                    [
                                        new MesField { TagId = "KValue", FieldName = "KValue" },
                                        new MesField { TagId = "IsNG", FieldName = "IsNg" },
                                        new MesField { TagId = "ErrorMessage", FieldName = "NgReason" },
                                        new MesField { TagId = "Errorinfo", FieldName = "ErrorInfo" },
                                    ],
                                },
                            ],
                        },
                    },
                    new MesNode
                    {
                        Id = "write", Type = "ResponseWriteback",
                        WriteBack = new MesWriteBack
                        {
                            Confirm = false,
                            FromResponse = [new MesResponseWrite { TagId = explicitResult, ResponseField = "diagnostic" }],
                            Manual = [new MesManualWrite { TagId = mesFlag, Value = "2" }],
                        },
                    },
                ],
                Edges =
                [
                    new MesEdge { From = "trigger", To = "upload" },
                    new MesEdge { From = "upload", To = "write" },
                ],
            },
        };

        await using var engine = new MesUploadEngine(
            hub,
            new MesUploadSettings { Enabled = true, Tasks = [task] },
            (id, value, _) =>
            {
                writes.Enqueue((id, value));
                // 真实 PLC 中 Errorinfo 是 Int16，接口返回空字符串时该点必然失败。
                // 这个局部失败不得阻断同一结构体其他点位下发。
                return Task.FromResult(id != errorInfo);
            },
            NullLogger.Instance,
            httpMessageHandler: handler);

        await WaitUntilAsync(() => writes.Any(item => item.TagId == mesFlag), TimeSpan.FromSeconds(2));
        var actual = writes.ToArray();
        Assert.Contains(actual, item => item.TagId == kValue && Convert.ToString(item.Value, CultureInfo.InvariantCulture) == "0.00088");
        Assert.Contains(actual, item => item.TagId == isNg && Convert.ToString(item.Value) == "1");
        Assert.Contains(actual, item => item.TagId == errorMessage && Convert.ToString(item.Value) == "工序跳站！");
        Assert.Contains(actual, item => item.TagId == errorInfo && Convert.ToString(item.Value) == "");
        Assert.Contains(actual, item => item.TagId == explicitResult && Convert.ToString(item.Value) == "accepted");
        Assert.Contains(actual, item => item.TagId == mesFlag && Convert.ToString(item.Value) == "2");
    }

    [Fact]
    public async Task ResponseWriteback_DoesNotReuseEarlierUploadResponse_WhenLatest2xxBodyIsNotJson()
    {
        const string tagId = "Channel.Device.DB.Value";
        await using var hub = new DataHub();
        hub.Register(tagId, new TagDef
        {
            Name = "Value",
            DataType = IgwDataType.Int16,
            Access = TagAccess.ReadWrite,
        });
        hub.Update(tagId, DataValue.GoodAt((short)0, DateTime.UtcNow));

        var handler = new SequentialResponseHandler();
        var writes = new ConcurrentQueue<(string TagId, object Value)>();
        var task = new MesTask
        {
            Id = "response-context",
            Name = "response-context",
            Enabled = true,
            CollectIntervalMs = 20,
            ConfirmRewrite = false,
            Flow = new MesFlow
            {
                Nodes =
                [
                    new MesNode { Id = "trigger", Type = "Trigger" },
                    new MesNode
                    {
                        Id = "upload-1",
                        Type = "Upload",
                        Http = new MesHttp { Url = "http://localhost/first" },
                        Payload = new MesPayload
                        {
                            Mode = MesPayloadModes.FieldMapping,
                            Fields = [new MesField { TagId = tagId, FieldName = "Value" }],
                        },
                    },
                    new MesNode
                    {
                        Id = "upload-2",
                        Type = "Upload",
                        Http = new MesHttp { Url = "http://localhost/second" },
                    },
                    new MesNode
                    {
                        Id = "response-writeback",
                        Type = "ResponseWriteback",
                        WriteBack = new MesWriteBack { Confirm = false },
                    },
                ],
                Edges =
                [
                    new MesEdge { From = "trigger", To = "upload-1" },
                    new MesEdge { From = "upload-1", To = "upload-2" },
                    new MesEdge { From = "upload-2", To = "response-writeback" },
                ],
            },
        };
        var settings = new MesUploadSettings { Enabled = true, Tasks = [task] };

        await using var engine = new MesUploadEngine(
            hub,
            settings,
            (id, value, _) =>
            {
                writes.Enqueue((id, value));
                return Task.FromResult(true);
            },
            NullLogger.Instance,
            httpMessageHandler: handler);

        await WaitUntilAsync(() => handler.RequestCount >= 2, TimeSpan.FromSeconds(2));
        await Task.Delay(100);

        Assert.Empty(writes);
    }

    [Fact]
    public async Task ResponseWriteback_TruncatesLongErrorInfoToPlcStringLength_AndKeepsOtherStructWrites()
    {
        const string root = "PLC3.PLC3.DB2100_电芯OCV_DataDB";
        string[] isNgTags = Enumerable.Range(0, 10).Select(i => $"{root}.CellData_{i}.IsNG").ToArray();
        string[] errorInfoTags = Enumerable.Range(0, 10).Select(i => $"{root}.CellData_{i}.Errorinfo").ToArray();
        var errorInfoSet = errorInfoTags.ToHashSet(StringComparer.OrdinalIgnoreCase);
        await using var hub = new DataHub();
        foreach (string isNg in isNgTags)
        {
            hub.Register(isNg, new TagDef
            {
                Name = "IsNG", DataType = IgwDataType.Int16, Access = TagAccess.ReadWrite,
            });
            hub.Update(isNg, DataValue.GoodAt((short)0, DateTime.UtcNow));
        }
        foreach (string errorInfo in errorInfoTags)
        {
            hub.Register(errorInfo, new TagDef
            {
                Name = "Errorinfo", DataType = IgwDataType.String, Access = TagAccess.ReadWrite, StringLength = 100,
            });
            hub.Update(errorInfo, DataValue.GoodAt("", DateTime.UtcNow));
        }

        string serviceError = "远景进站失败：" + new string('X', 180);
        var handler = new StaticResponseHandler(JsonSerializer.Serialize(new
        {
            httpStatusCode = 200,
            isSuccess = true,
            backResult = new
            {
                structures = Enumerable.Range(0, 10).Select(_ => new { isNg = 1, errorInfo = serviceError }).ToArray(),
            },
            errorMessage = (string?)null,
        }));
        var writes = new ConcurrentQueue<(string TagId, string Value)>();
        var task = new MesTask
        {
            Id = "long-error-info", Name = "long-error-info", Enabled = true,
            CollectIntervalMs = 100, ConfirmRewrite = false,
            Flow = new MesFlow
            {
                Nodes =
                [
                    new MesNode { Id = "trigger", Type = "Trigger" },
                    new MesNode
                    {
                        Id = "upload", Type = "Upload", Http = new MesHttp { Url = "http://localhost/judge" },
                        Payload = new MesPayload
                        {
                            Mode = MesPayloadModes.FieldMapping,
                            Structs =
                            [
                                new MesStruct
                                {
                                    Kind = "Array", BaseGroup = $"{root}.CellData", AliasName = "Structures",
                                    CountMode = "Enum", StartIndex = 0, FixedCount = 10,
                                    Members =
                                    [
                                        new MesField { TagId = "IsNG" },
                                        new MesField { TagId = "Errorinfo" },
                                    ],
                                },
                            ],
                        },
                    },
                    new MesNode { Id = "write", Type = "ResponseWriteback", WriteBack = new MesWriteBack { Confirm = false } },
                ],
                Edges =
                [
                    new MesEdge { From = "trigger", To = "upload" },
                    new MesEdge { From = "upload", To = "write" },
                ],
            },
        };

        await using var engine = new MesUploadEngine(
            hub, new MesUploadSettings { Enabled = true, Tasks = [task] },
            (id, value, _) =>
            {
                string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                writes.Enqueue((id, text));
                return Task.FromResult(!errorInfoSet.Contains(id) || Encoding.UTF8.GetByteCount(text) <= 100);
            },
            NullLogger.Instance, httpMessageHandler: handler);

        await WaitUntilAsync(() => writes.Where(item => errorInfoSet.Contains(item.TagId))
            .Select(item => item.TagId).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 10, TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() =>
        {
            var current = Assert.Single(engine.Snapshot());
            return Assert.Single(current.Nodes, node => node.Id == "upload").State == "ok" &&
                   Assert.Single(current.Nodes, node => node.Id == "write").State == "ok";
        }, TimeSpan.FromSeconds(2));
        Assert.All(isNgTags, tag => Assert.Contains(writes, item => item.TagId == tag && item.Value == "1"));
        var errorWrites = writes.Where(item => errorInfoSet.Contains(item.TagId)).ToArray();
        Assert.NotEmpty(errorWrites);
        Assert.All(errorWrites, item =>
        {
            Assert.StartsWith("远景进站失败：", item.Value);
            Assert.True(Encoding.UTF8.GetByteCount(item.Value) <= 100);
        });
    }

    [Fact]
    /// <summary>
    /// 上报不再拦路：配置的成功判定(httpStatusCode=200)不满足时上报仍判失败，
    /// 但流程照样走到回写节点。要在失败时不写 PLC，请自己插一个「判成败」节点分流。
    /// </summary>
    public async Task Failed_success_rule_marks_upload_fail_but_flow_continues_to_writeback()
    {
        const string tagId = "Channel.Device.DB.MESFlag";
        await using var hub = new DataHub();
        hub.Register(tagId, new TagDef { Name = "MESFlag", DataType = IgwDataType.Int16, Access = TagAccess.ReadWrite });
        hub.Update(tagId, DataValue.GoodAt((short)0, DateTime.UtcNow));
        var writes = new ConcurrentQueue<(string TagId, object Value)>();
        var task = FlowTask(tagId, new StaticResponseHandler("""
            { "value": { "httpStatusCode": 400, "isSuccess": false, "backResult": null,
              "errorMessage": "IsNG cannot be converted to Byte" }, "statusCode": 200 }
            """), out var handler);
        task.CollectIntervalMs = 1000;

        await using var engine = new MesUploadEngine(
            hub, new MesUploadSettings { Enabled = true, Tasks = [task] },
            (id, value, _) => { writes.Enqueue((id, value)); return Task.FromResult(true); },
            NullLogger.Instance, httpMessageHandler: handler);

        var staticHandler = Assert.IsType<StaticResponseHandler>(handler);
        await WaitUntilAsync(() => staticHandler.RequestCount >= 2, TimeSpan.FromSeconds(4));
        await Task.Delay(50);
        var status = Assert.Single(engine.Snapshot());
        var upload = Assert.Single(status.Nodes, node => node.Id == "upload");
        var writeback = Assert.Single(status.Nodes, node => node.Id == "write");
        Assert.Equal("fail", upload.State);   // 成功判定保留：httpStatusCode=400 不等于 200
        Assert.Contains("IsNG cannot be converted", upload.Info);
        var detail = Assert.IsType<MesHttpFailDetail>(upload.Detail);
        Assert.Equal(400, detail.StatusCode);
        // 判失败不再阻断流程：回写节点照常执行，固定值 2 写进 PLC
        Assert.Contains(writes, item => item.TagId == tagId && Convert.ToInt32(item.Value) == 2);
        Assert.NotEqual("idle", writeback.State);
    }

    [Fact]
    public async Task Configured_success_rule_marks_upload_OK_and_continues_writeback_when_isSuccess_is_not_intercepted()
    {
        const string tagId = "Channel.Device.DB.MESFlag";
        await using var hub = new DataHub();
        hub.Register(tagId, new TagDef { Name = "MESFlag", DataType = IgwDataType.Int16, Access = TagAccess.ReadWrite });
        hub.Update(tagId, DataValue.GoodAt((short)0, DateTime.UtcNow));
        var writes = new ConcurrentQueue<(string TagId, object Value)>();
        var task = FlowTask(tagId, new StaticResponseHandler("""
            { "httpStatusCode": 200, "isSuccess": false, "backResult": null,
              "errorMessage": "business rejected" }
            """), out var handler);

        await using var engine = new MesUploadEngine(
            hub, new MesUploadSettings { Enabled = true, Tasks = [task] },
            (id, value, _) => { writes.Enqueue((id, value)); return Task.FromResult(true); },
            NullLogger.Instance, httpMessageHandler: handler);

        await WaitUntilAsync(() =>
        {
            var snapshot = engine.Snapshot().SingleOrDefault();
            return writes.Any(item => item.TagId == tagId)
                && snapshot?.Nodes.SingleOrDefault(node => node.Id == "upload")?.State == "ok"
                && snapshot.Nodes.SingleOrDefault(node => node.Id == "write")?.State == "ok";
        }, TimeSpan.FromSeconds(2));
        var status = Assert.Single(engine.Snapshot());
        var upload = Assert.Single(status.Nodes, node => node.Id == "upload");
        Assert.Equal("ok", upload.State);
        Assert.Null(upload.Detail);
        Assert.Equal(0, status.Err);
        Assert.True(status.Ok > 0);
        Assert.Equal("ok", Assert.Single(status.Nodes, node => node.Id == "write").State);
        Assert.Contains(writes, item => item.TagId == tagId && Convert.ToString(item.Value) == "2");
    }

    [Fact]
    public async Task Non_intercepted_business_response_still_writes_fixed_value_when_automatic_field_write_fails()
    {
        const string root = "Channel.Device.DB2100";
        string mesFlag = $"{root}.MESFlag";
        string errorInfo = $"{root}.CellData_0.Errorinfo";
        await using var hub = new DataHub();
        hub.Register(mesFlag, new TagDef { Name = "MESFlag", DataType = IgwDataType.Int16, Access = TagAccess.ReadWrite });
        hub.Register(errorInfo, new TagDef
        {
            Name = "Errorinfo", DataType = IgwDataType.String, Access = TagAccess.ReadWrite, StringLength = 20,
        });
        hub.Update(mesFlag, DataValue.GoodAt((short)0, DateTime.UtcNow));
        hub.Update(errorInfo, DataValue.GoodAt("", DateTime.UtcNow));
        var writes = new ConcurrentQueue<(string TagId, object Value)>();
        var handler = new StaticResponseHandler("""
            { "httpStatusCode": 200, "isSuccess": false,
              "backResult": { "structures": [ { "errorInfo": "business rejected" } ] },
              "errorMessage": "business rejected" }
            """);
        var task = new MesTask
        {
            Id = "continue-fixed", Name = "continue-fixed", Enabled = true,
            CollectIntervalMs = 20, ConfirmRewrite = false,
            Flow = new MesFlow
            {
                Nodes =
                [
                    new MesNode { Id = "trigger", Type = "Trigger" },
                    new MesNode
                    {
                        Id = "upload", Type = "Upload",
                        Http = new MesHttp
                        {
                            Url = "http://localhost/mes", SuccessKey = "httpStatusCode", SuccessValue = "200",
                        },
                        Payload = new MesPayload
                        {
                            Mode = MesPayloadModes.FieldMapping,
                            Fields = [new MesField { TagId = mesFlag, FieldName = "MESFlag" }],
                            Structs =
                            [
                                new MesStruct
                                {
                                    Kind = "Array", BaseGroup = $"{root}.CellData", AliasName = "Structures",
                                    CountMode = "Enum", StartIndex = 0, FixedCount = 1,
                                    Members = [new MesField { TagId = "Errorinfo", FieldName = "ErrorInfo" }],
                                },
                            ],
                        },
                    },
                    new MesNode
                    {
                        Id = "write", Type = "ResponseWriteback",
                        WriteBack = new MesWriteBack
                        {
                            Confirm = false,
                            Manual = [new MesManualWrite { TagId = mesFlag, Value = "4" }],
                        },
                    },
                ],
                Edges =
                [
                    new MesEdge { From = "trigger", To = "upload" },
                    new MesEdge { From = "upload", To = "write" },
                ],
            },
        };

        await using var engine = new MesUploadEngine(
            hub, new MesUploadSettings { Enabled = true, Tasks = [task] },
            (id, value, _, _) =>
            {
                writes.Enqueue((id, value));
                return Task.FromResult(id == errorInfo
                    ? new StatusCode(0x803C0000)
                    : StatusCode.Good);
            },
            NullLogger.Instance, httpMessageHandler: handler);

        MesTaskStatus? completed = null;
        await WaitUntilAsync(() =>
        {
            var snapshot = engine.Snapshot().SingleOrDefault();
            if (!writes.Any(item => item.TagId == mesFlag) ||
                snapshot?.Nodes.SingleOrDefault(node => node.Id == "upload")?.State != "ok") return false;
            completed = snapshot;
            return true;
        }, TimeSpan.FromSeconds(2));
        var actual = writes.ToArray();
        Assert.Contains(actual, item => item.TagId == errorInfo);
        Assert.Contains(actual, item => item.TagId == mesFlag && Convert.ToString(item.Value) == "4");
        var status = Assert.IsType<MesTaskStatus>(completed);
        Assert.Equal("ok", Assert.Single(status.Nodes, node => node.Id == "upload").State);
        Assert.Equal(0, status.Err);
    }

    [Fact]
    public async Task Failed_plc_write_logs_status_expected_actual_attempts_and_stage()
    {
        const string tagId = "Channel.Device.DB.MESFlag";
        await using var hub = new DataHub();
        hub.Register(tagId, new TagDef
        {
            Name = "MESFlag", DataType = IgwDataType.Int16, Access = TagAccess.ReadWrite,
        });
        hub.Update(tagId, DataValue.GoodAt((short)0, DateTime.UtcNow));
        var task = FlowTask(tagId, new StaticResponseHandler("""
            { "httpStatusCode": 200, "isSuccess": false, "backResult": null,
              "errorMessage": "business rejected" }
            """), out var handler);
        var logger = new CollectingLogger();

        await using var engine = new MesUploadEngine(
            hub, new MesUploadSettings { Enabled = true, Tasks = [task] },
            (_, _, _, _) => Task.FromResult(new StatusCode(0x80740000)),
            logger, httpMessageHandler: handler);

        await WaitUntilAsync(() => logger.Messages.Any(message => message.Contains("MES 回写失败")),
            TimeSpan.FromSeconds(2));
        string failure = logger.Messages.Last(message => message.Contains("MES 回写失败"));
        Assert.Contains("状态=BadTypeMismatch(0x80740000)", failure);
        Assert.Contains("期望=2", failure);
        Assert.Contains("回读=0", failure);
        Assert.Contains("阶段=协议写入", failure);
        Assert.Contains("尝试=1", failure);
    }

    [Fact]
    /// <summary>
    /// isSuccess 不再做隐式的二次判定：响应里 httpStatusCode=200 满足配置的成功规则，
    /// 就算 isSuccess=false 上报也判成功，回写节点完整执行(含固定写 MESFlag)。
    /// </summary>
    public async Task IsSuccess_false_no_longer_overrides_the_configured_success_rule()
    {
        const string root = "Channel.Device.DB2100";
        string mesFlag = $"{root}.MESFlag";
        string errorInfo = $"{root}.CellData_0.Errorinfo";
        await using var hub = new DataHub();
        hub.Register(mesFlag, new TagDef { Name = "MESFlag", DataType = IgwDataType.Int16, Access = TagAccess.ReadWrite });
        hub.Register(errorInfo, new TagDef
        {
            Name = "Errorinfo", DataType = IgwDataType.String, Access = TagAccess.ReadWrite, StringLength = 100,
        });
        hub.Update(mesFlag, DataValue.GoodAt((short)0, DateTime.UtcNow));
        hub.Update(errorInfo, DataValue.GoodAt("", DateTime.UtcNow));
        string returnedError = "远景进站失败：" + new string('错', 80);
        var handler = new StaticResponseHandler(JsonSerializer.Serialize(new
        {
            httpStatusCode = 200,
            isSuccess = false,
            backResult = new
            {
                mesFlag = 0,
                structures = new[] { new { errorInfo = returnedError } },
            },
            errorMessage = "远景进站失败",
        }));
        var writes = new ConcurrentQueue<(string TagId, object Value)>();
        var task = new MesTask
        {
            Id = "business-failure-writeback", Name = "business-failure-writeback", Enabled = true,
            CollectIntervalMs = 20, ConfirmRewrite = false,
            Flow = new MesFlow
            {
                Nodes =
                [
                    new MesNode { Id = "trigger", Type = "Trigger" },
                    new MesNode
                    {
                        Id = "upload", Type = "Upload",
                        Http = new MesHttp { Url = "http://localhost/mes", SuccessKey = "httpStatusCode", SuccessValue = "200" },
                        Payload = new MesPayload
                        {
                            Mode = MesPayloadModes.FieldMapping,
                            Fields = [new MesField { TagId = mesFlag, FieldName = "MESFlag" }],
                            Structs =
                            [
                                new MesStruct
                                {
                                    Kind = "Array", BaseGroup = $"{root}.CellData", AliasName = "Structures",
                                    CountMode = "Enum", StartIndex = 0, FixedCount = 1,
                                    Members = [new MesField { TagId = "Errorinfo", FieldName = "ErrorInfo" }],
                                },
                            ],
                        },
                    },
                    new MesNode
                    {
                        Id = "write", Type = "ResponseWriteback",
                        WriteBack = new MesWriteBack
                        {
                            Confirm = false,
                            Manual = [new MesManualWrite { TagId = mesFlag, Value = "2" }],
                        },
                    },
                ],
                Edges =
                [
                    new MesEdge { From = "trigger", To = "upload" },
                    new MesEdge { From = "upload", To = "write" },
                ],
            },
        };

        await using var engine = new MesUploadEngine(
            hub, new MesUploadSettings { Enabled = true, Tasks = [task] },
            (id, value, _) =>
            {
                writes.Enqueue((id, value));
                if (id == errorInfo) hub.Update(id, DataValue.GoodAt(value, DateTime.UtcNow));
                return Task.FromResult(true);
            },
            NullLogger.Instance, httpMessageHandler: handler);

        await WaitUntilAsync(() => writes.Any(item => item.TagId == errorInfo)
            && engine.Snapshot().Single().Nodes.Single(node => node.Id == "upload").State == "ok",
            TimeSpan.FromSeconds(2));
        var actual = writes.ToArray();
        var errorWrite = Assert.Single(actual, item => item.TagId == errorInfo);
        Assert.StartsWith("远景进站失败：", Convert.ToString(errorWrite.Value));
        Assert.True(Encoding.UTF8.GetByteCount(Convert.ToString(errorWrite.Value)!) <= 100);
        var status = Assert.Single(engine.Snapshot());
        Assert.Equal("ok", Assert.Single(status.Nodes, node => node.Id == "upload").State);
        var writeback = Assert.Single(status.Nodes, node => node.Id == "write");
        Assert.Equal("ok", writeback.State);
    }

    [Fact]
    public async Task Transport_failure_clears_stale_response_writeback_failure()
    {
        const string tagId = "Channel.Device.DB.MESFlag";
        await using var hub = new DataHub();
        hub.Register(tagId, new TagDef { Name = "MESFlag", DataType = IgwDataType.Int16, Access = TagAccess.ReadWrite });
        hub.Update(tagId, DataValue.GoodAt((short)0, DateTime.UtcNow));
        var handler = new SuccessThenServerErrorHandler();
        var task = FlowTask(tagId, handler, out _);

        await using var engine = new MesUploadEngine(
            hub, new MesUploadSettings { Enabled = true, Tasks = [task] },
            (_, _, _) => Task.FromResult(false),
            NullLogger.Instance, httpMessageHandler: handler);

        await WaitUntilAsync(() => handler.RequestCount >= 2, TimeSpan.FromSeconds(2));
        await Task.Delay(50);
        var status = Assert.Single(engine.Snapshot());
        var upload = Assert.Single(status.Nodes, node => node.Id == "upload");
        var writeback = Assert.Single(status.Nodes, node => node.Id == "write");
        Assert.Equal("fail", upload.State);
        Assert.Equal("idle", writeback.State);
        Assert.Contains("上报", writeback.Info);
    }

    [Fact]
    public void Planner_MapsAliasesAndStructArrayMembersBackToOriginalPlcTags()
    {
        var payload = new MesPayload
        {
            Mode = MesPayloadModes.FieldMapping,
            Fields =
            [
                new MesField { TagId = "Channel.Device.DB.MESFlag", FieldName = "mes_flag" },
            ],
            Structs =
            [
                new MesStruct
                {
                    Kind = "Array",
                    BaseGroup = "Channel.Device.DB.CellData",
                    AliasName = "Structures",
                    StartIndex = 0,
                    FixedCount = 2,
                    Members = [new MesField { TagId = "IsNG", FieldName = "is_ng" }],
                },
            ],
        };
        using var response = JsonDocument.Parse("""
            {
              "backResult": {
                "mes_flag": 7,
                "Structures": [
                  { "is_ng": 1 },
                  { "is_ng": null }
                ]
              }
            }
            """);

        var plan = MesResponseWritePlanner.Plan(payload, response.RootElement, "parameterDic", []);

        Assert.Equal(
            [
                new MesResponseWritePlanItem("Channel.Device.DB.MESFlag", "7"),
                new MesResponseWritePlanItem("Channel.Device.DB.CellData[0].IsNG", "1"),
            ],
            plan);
    }

    [Fact]
    public void Planner_ItemId_uses_literal_keys_to_select_response_object_and_not_old_aliases()
    {
        var payload = JsonSerializer.Deserialize<MesPayload>("""
            {"mode":"FieldMapping","fieldKeyMode":"ItemId","fields":[
              {"tagId":"Channel.Device.DB.MESFlag","fieldName":"oldAlias","parameterCode":"Channel.Device.DB.MESFlag","scale":2}
            ]}
            """, ProjectStore.Options)!;
        using var response = JsonDocument.Parse("""
            {"backResult":{"oldAlias":90},
             "value":{"Channel.Device.DB.MESFlag":8},
             "Channel":{"Device":{"DB":{"MESFlag":100}}}}
            """);

        var plan = MesResponseWritePlanner.Plan(payload, response.RootElement, "parameterDic", []);

        Assert.Equal([new MesResponseWritePlanItem("Channel.Device.DB.MESFlag", "4")], plan);
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("backResult")]
    [InlineData("data")]
    [InlineData("nested")]
    [InlineData("legacy")]
    public async Task ResponseWriteback_ItemId_writes_dictionary_and_outer_structures_without_losing_either(string shape)
    {
        const string flag = "Channel.Device.DB.MESFlag";
        const string ready = "Channel.Device.DB.Ready";
        const string code = "Channel.Device.DB.Code";
        const string count = "Channel.Device.DB.Count";
        const string product = "Channel.Device.DB.Product.Code";
        const string listValue = "Channel.Device.DB.Values.A";
        const string member = "Channel.Device.DB.CellData[0].IsNG";
        const string nested = "Channel.Device.DB.CellData[0].Inner.Code";
        await using var hub = new DataHub();
        foreach (string id in new[] { flag, ready, code, count, product, listValue, member, nested })
        {
            hub.Register(id, new() { DataType = IgwDataType.Int16, Access = TagAccess.ReadWrite });
            hub.Update(id, DataValue.GoodAt((short)0, DateTime.UtcNow));
        }
        var payload = JsonSerializer.Deserialize<MesPayload>("""
            {"mode":"FieldMapping","fieldKeyMode":"ItemId","fields":[
              {"tagId":"Channel.Device.DB.MESFlag","fieldName":"oldAlias","parameterCode":"Channel.Device.DB.MESFlag","scale":2},
              {"tagId":"Channel.Device.DB.Ready"},{"tagId":"Channel.Device.DB.Code"},{"tagId":"Channel.Device.DB.Count"},
              {"tagId":"Channel.Device.DB.CellData[0].IsNG","fieldName":"covered"}],
             "structs":[{"kind":"Array","baseGroup":"Channel.Device.DB.CellData","aliasName":"Structures","fixedCount":1,
               "members":[{"tagId":"IsNG","fieldName":"is_ng"}],
               "structs":[{"kind":"Object","baseGroup":"Inner","aliasName":"inner","members":[{"tagId":"Code","fieldName":"code"}]}]},
               {"kind":"Object","baseGroup":"Channel.Device.DB.Product","aliasName":"Product","members":[{"tagId":"Code","fieldName":"code"}]},
               {"kind":"List","baseGroup":"Channel.Device.DB.Values","aliasName":"Values","members":[{"tagId":"A"}]}]}
            """, ProjectStore.Options)!;
        const string ordinary = """
            "Channel.Device.DB.MESFlag":8,"Channel.Device.DB.Ready":true,"Channel.Device.DB.Code":"A","Channel.Device.DB.Count":5,
            "oldAlias":90,"Channel.Device.DB.CellData[0].IsNG":99
            """;
        const string structures = """
            "Structures":[{"is_ng":1,"inner":{"code":6}}],"Product":{"code":"P"},"Values":[12]
            """;
        string responsePayload = shape == "legacy" ? "{" + ordinary + "," + structures + "}"
            : "{\"parameterDic\":{" + ordinary + "}," + structures + "}";
        string response = shape switch
        {
            "direct" => responsePayload,
            "data" => "{\"data\":" + responsePayload + "}",
            "nested" => "{\"value\":{\"backResult\":{\"data\":" + responsePayload + "}}}",
            _ => "{\"httpStatusCode\":200,\"backResult\":" + responsePayload + "}",
        };
        var handler = new StaticResponseHandler(response);
        var writes = new ConcurrentQueue<(string Id, string Value)>();
        var task = new MesTask
        {
            Id = "item-id-writeback", Name = "item-id-writeback", CollectIntervalMs = 1000, ConfirmRewrite = false,
            Flow = new()
            {
                Nodes =
                [
                    new() { Id = "trigger", Type = "Trigger" },
                    new() { Id = "upload", Type = "Upload", Payload = payload, Http = new() { Url = "http://localhost/mes" } },
                    new() { Id = "write", Type = "ResponseWriteback", WriteBack = new() { Confirm = false } },
                ],
                Edges = [new() { From = "trigger", To = "upload" }, new() { From = "upload", To = "write" }],
            },
        };
        await using var engine = new MesUploadEngine(hub, new() { Enabled = true, Tasks = [task] },
            (id, value, _) => { writes.Enqueue((id, Convert.ToString(value, CultureInfo.InvariantCulture)!)); return Task.FromResult(true); },
            NullLogger.Instance, httpMessageHandler: handler);
        await WaitUntilAsync(() => engine.Snapshot().Single().Nodes.SingleOrDefault(n => n.Id == "write")?.State == "ok", TimeSpan.FromSeconds(2));

        Assert.Contains((flag, "4"), writes);
        Assert.Contains((ready, "true"), writes);
        Assert.Contains((code, "A"), writes);
        Assert.Contains((count, "5"), writes);
        Assert.Contains((product, "P"), writes);
        Assert.Contains((listValue, "12"), writes);
        Assert.Contains((member, "1"), writes);
        Assert.Contains((nested, "6"), writes);
        Assert.DoesNotContain(writes, item => item.Value is "90" or "99");
    }

    [Fact]
    public void Planner_ItemId_prefers_dictionary_values_and_keeps_legacy_flat_fallback_without_writing_null_or_arrays()
    {
        var payload = new MesPayload
        {
            FieldKeyMode = "ItemId",
            Fields = [new() { TagId = "L.P.DB.Value", Scale = 2 }, new() { TagId = "L.P.DB.Flat" },
                new() { TagId = "L.P.DB.Null" }, new() { TagId = "L.P.DB.Array" }, new() { TagId = "L.P.DB.parameterDic" }],
        };
        using var response = JsonDocument.Parse("""
            {"data":{"ParameterDic":{"L.P.DB.Value":8,"L.P.DB.Null":null,"L.P.DB.Array":[1,2],"L.P.DB.parameterDic":6},
              "L.P.DB.Value":100,"L.P.DB.Flat":3,"L.P.DB.Null":99}}
            """);

        var plan = MesResponseWritePlanner.Plan(payload, response.RootElement, "legacyResponseName", []);

        Assert.Equal([
            new MesResponseWritePlanItem("L.P.DB.Value", "4"),
            new MesResponseWritePlanItem("L.P.DB.parameterDic", "6"),
            new MesResponseWritePlanItem("L.P.DB.Flat", "3"),
        ], plan);
    }

    [Fact]
    public void Planner_ItemId_empty_dictionary_keeps_outer_structure_writeback()
    {
        var payload = new MesPayload
        {
            FieldKeyMode = "ItemId",
            Structs = [new() { Kind = "Object", BaseGroup = "L.P.DB.Product", AliasName = "Product", Members = [new() { TagId = "Code" }] }],
        };
        using var response = JsonDocument.Parse("""{"backResult":{"parameterDic":{},"Product":{"Code":"A"}}}""");

        var plan = MesResponseWritePlanner.Plan(payload, response.RootElement, "parameterDic", []);

        Assert.Equal([new MesResponseWritePlanItem("L.P.DB.Product.Code", "A")], plan);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Planner_ItemId_does_not_mistake_nested_struct_alias_for_the_ordinary_dictionary(bool hasUnreturnedField)
    {
        var payload = new MesPayload
        {
            FieldKeyMode = "ItemId",
            Fields = hasUnreturnedField ? [new() { TagId = "L.P.DB.Unreturned" }] : [],
            Structs = [new() { Kind = "Object", BaseGroup = "L.P.DB.Product", AliasName = "Product", Structs =
                [new() { Kind = "Object", BaseGroup = "Inner", AliasName = "parameterDic", Members = [new() { TagId = "Code" }] }] }],
        };
        using var response = JsonDocument.Parse("""{"Product":{"parameterDic":{"Code":1}}}""");

        var plan = MesResponseWritePlanner.Plan(payload, response.RootElement, "parameterDic", []);

        Assert.Equal([new MesResponseWritePlanItem("L.P.DB.Product.Inner.Code", "1")], plan);
    }

    [Theory]
    [InlineData("Body", "Alias", "10")]
    [InlineData("BodyDefault", "DefaultName", "10")]
    [InlineData("Json", "Alias", "10")]
    [InlineData("Both", "WrappedAlias", "10")]
    [InlineData("Body", "Dictionary", "10")]
    [InlineData("Json", "DictionaryAlias", "10")]
    [InlineData("Body", "BothKeys", "10")]
    [InlineData("Json", "LegacyFlat", "10")]
    [InlineData("Automatic", "Alias", "10")]
    [InlineData("Whitespace", "Alias", "10")]
    [InlineData("Renamed", "Unconfigured", null)]
    [InlineData("RangeBody", "Alias", null)]
    public async Task ResponseWriteback_ItemId_template_aliases_require_an_effective_template_and_configured_mapping(
        string template, string responseShape, string? expected)
    {
        const string tagId = "L.P.DB.X";
        await using var hub = new DataHub();
        var tag = new TagDef { Name = "X", DataType = IgwDataType.Int32, Access = TagAccess.ReadWrite };
        hub.Register(tagId, tag);
        hub.Update(tagId, DataValue.GoodAt(5, DateTime.UtcNow));
        var project = new IgwProject { Channels = [new() { Name = "L", Devices = [new() { Name = "P", Groups = [new() { Name = "DB", Tags = [tag] }] }] }] };
        var payload = new MesPayload
        {
            Mode = template == "RangeBody" ? "DataRanges" : "FieldMapping", FieldKeyMode = "ItemId",
            Fields = [new() { TagId = tagId, FieldName = template == "BodyDefault" ? "" : "x", Scale = 2 }],
            DataRanges = template == "RangeBody" ? [new() { SourcePath = tagId, Scope = "Tag" }] : [],
        };
        var http = new MesHttp
        {
            Url = "http://localhost/template",
            BodyTemplate = template switch
            {
                "Body" or "Both" or "RangeBody" => "{\"x\":{x}}",
                "BodyDefault" => "{\"X\":{X}}",
                "Renamed" => "{\"renamed\":{x}}",
                "Whitespace" => "  ",
                _ => "",
            },
            JsonTemplate = template switch { "Json" => "{\"x\":10}", "Both" => "{\"x\":30}", _ => "" },
        };
        string response = responseShape switch
        {
            "Dictionary" => "{\"data\":{\"parameterDic\":{\"L.P.DB.X\":20}}}",
            "DictionaryAlias" => "{\"data\":{\"parameterDic\":{\"x\":20}}}",
            "BothKeys" => "{\"parameterDic\":{\"L.P.DB.X\":20},\"x\":90}",
            "LegacyFlat" => "{\"backResult\":{\"L.P.DB.X\":20}}",
            "WrappedAlias" => "{\"backResult\":{\"x\":20}}",
            "Unconfigured" => "{\"renamed\":20}",
            "DefaultName" => "{\"X\":20}",
            _ => "{\"x\":20}",
        };
        var handler = new StaticResponseHandler(response);
        var writes = new ConcurrentQueue<(string TagId, string Value)>();
        var task = new MesTask
        {
            Id = "template-writeback", Name = "template-writeback", CollectIntervalMs = 1000, ConfirmRewrite = false,
            Flow = new()
            {
                Nodes = [new() { Id = "trigger", Type = "Trigger" }, new() { Id = "upload", Type = "Upload", Payload = payload, Http = http },
                    new() { Id = "write", Type = "ResponseWriteback", WriteBack = new() { Confirm = false } }],
                Edges = [new() { From = "trigger", To = "upload" }, new() { From = "upload", To = "write" }],
            },
        };
        await using var engine = new MesUploadEngine(hub, new() { Enabled = true, Tasks = [task] },
            (id, value, _) => { writes.Enqueue((id, Convert.ToString(value, CultureInfo.InvariantCulture)!)); return Task.FromResult(true); },
            NullLogger.Instance, httpMessageHandler: handler, catalog: MesDataRangeCatalog.Build(project), deviceState: _ => ConnectionState.Connected);
        await WaitUntilAsync(() => engine.Snapshot().Single().Nodes.SingleOrDefault(n => n.Id == "write")?.State == "ok", TimeSpan.FromSeconds(2));

        using var request = JsonDocument.Parse(handler.RequestBody!);
        if (template is "Automatic" or "Whitespace") Assert.Equal(10, request.RootElement.GetProperty("x").GetInt32());
        else if (template == "RangeBody") Assert.Equal(5, request.RootElement.GetProperty("P").GetProperty("DB").GetProperty("X").GetInt32());
        else Assert.Equal(template == "Both" ? 30 : 10, request.RootElement.GetProperty(
            template switch { "Renamed" => "renamed", "BodyDefault" => "X", _ => "x" }).GetInt32());
        if (expected is null) Assert.Empty(writes);
        else Assert.Contains((tagId, expected), writes);
    }

    [Fact]
    public void Planner_MapsNumericChildGroupArrayBackToActualElementPaths()
    {
        var payload = new MesPayload
        {
            Mode = MesPayloadModes.FieldMapping,
            Structs =
            [
                new MesStruct
                {
                    Kind = "Array",
                    BaseGroup = "Channel.Device.DB.Bolts",
                    AliasName = "bolts",
                    CountMode = "Enum",
                    StartIndex = 0,
                    FixedCount = 2,
                    Members = [new MesField { TagId = "Result", FieldName = "result" }],
                },
            ],
        };
        using var response = JsonDocument.Parse("""
            {"bolts":[{"result":3},{"result":4}]}
            """);

        var plan = MesResponseWritePlanner.Plan(payload, response.RootElement, null,
        [
            "Channel.Device.DB.Bolts.0.Result",
            "Channel.Device.DB.Bolts.1.Result",
        ]);

        Assert.Equal(
        [
            new MesResponseWritePlanItem("Channel.Device.DB.Bolts.0.Result", "3"),
            new MesResponseWritePlanItem("Channel.Device.DB.Bolts.1.Result", "4"),
        ], plan);
    }

    /// <summary>
    /// 自动匹配出的字段要一起交给引擎，由引擎合批下发、统一回读。现场 SendParam 把 79 条 × 6 个字段的
    /// 参数表写回 PLC，原先逐个 await，474 次串行往返，一次响应直写 3～4 秒。
    /// 显式配置的写入(常见是 MESFlag 握手位)仍排在所有自动字段写完之后。
    /// </summary>
    [Fact]
    public async Task Automatic_response_fields_are_dispatched_together_and_the_configured_flag_still_goes_last()
    {
        const string root = "Line.Plc.SendParam";
        const int rows = 40;
        string mesFlag = $"{root}.MESFlag";
        await using var hub = new DataHub();
        hub.Register(mesFlag, new TagDef { Name = "MESFlag", DataType = IgwDataType.Int16, Access = TagAccess.ReadWrite });
        hub.Update(mesFlag, DataValue.GoodAt((short)0, DateTime.UtcNow));
        for (int i = 0; i < rows; i++)
            foreach (string member in new[] { "ParamCode", "UpperLimit" })
            {
                string id = $"{root}.ParamData.{i}.{member}";
                hub.Register(id, new TagDef { Name = member, DataType = IgwDataType.String, Access = TagAccess.ReadWrite });
                hub.Update(id, DataValue.GoodAt("", DateTime.UtcNow));
            }
        string rowsJson = string.Join(",", Enumerable.Range(0, rows).Select(i => $$"""{"paramCode":"P{{i}}","upperLimit":"{{i}}"}"""));
        var handler = new StaticResponseHandler($$$"""{"isSuccess":true,"backResult":{"paramData":[{{{rowsJson}}}]}}""");
        var task = new MesTask
        {
            Id = "send-param", Name = "send-param", Enabled = true, CollectIntervalMs = 20, ConfirmRewrite = false,
            Flow = new MesFlow
            {
                Nodes =
                [
                    new MesNode { Id = "trigger", Type = "Trigger" },
                    new MesNode
                    {
                        Id = "upload", Type = "Upload", Http = new MesHttp { Url = "http://localhost/mes" },
                        Payload = new MesPayload
                        {
                            Mode = MesPayloadModes.FieldMapping,
                            Fields = [new MesField { TagId = mesFlag, FieldName = "MESFlag" }],
                            Structs =
                            [
                                new MesStruct
                                {
                                    Kind = "Array", BaseGroup = $"{root}.ParamData", AliasName = "ParamData",
                                    CountMode = "Enum", StartIndex = 0, FixedCount = rows,
                                    Members =
                                    [
                                        new MesField { TagId = "ParamCode", FieldName = "ParamCode" },
                                        new MesField { TagId = "UpperLimit", FieldName = "UpperLimit" },
                                    ],
                                },
                            ],
                        },
                    },
                    new MesNode
                    {
                        Id = "write", Type = "ResponseWriteback",
                        WriteBack = new MesWriteBack { Confirm = false, Manual = [new MesManualWrite { TagId = mesFlag, Value = "2" }] },
                    },
                ],
                Edges = [new MesEdge { From = "trigger", To = "upload" }, new MesEdge { From = "upload", To = "write" }],
            },
        };
        int inFlight = 0, maxInFlight = 0, dataWrites = 0, dataWritesBeforeFlag = -1;
        await using var engine = new MesUploadEngine(hub, new MesUploadSettings { Enabled = true, Tasks = [task] },
            async (id, value, _) =>
            {
                if (id == mesFlag)
                {
                    if (dataWritesBeforeFlag < 0) dataWritesBeforeFlag = Volatile.Read(ref dataWrites);
                    return true;
                }
                int now = Interlocked.Increment(ref inFlight);
                InterlockedMax(ref maxInFlight, now);
                await Task.Delay(20);   // 模拟一次 PLC 写入往返
                Interlocked.Decrement(ref inFlight);
                Interlocked.Increment(ref dataWrites);
                return true;
            },
            NullLogger.Instance, httpMessageHandler: handler);

        await WaitUntilAsync(() => Volatile.Read(ref dataWritesBeforeFlag) >= 0, TimeSpan.FromSeconds(5));

        Assert.Equal(rows * 2, dataWritesBeforeFlag);
        Assert.True(maxInFlight > 1, $"自动回写仍是逐个串行下发(最大并发 {maxInFlight})");

        static void InterlockedMax(ref int target, int value)
        {
            int current;
            while ((current = Volatile.Read(ref target)) < value &&
                   Interlocked.CompareExchange(ref target, value, current) != current) { }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Timed out waiting for MES requests.");
            await Task.Delay(10);
        }
    }

    private static MesTask FlowTask(string tagId, HttpMessageHandler handler, out HttpMessageHandler returnedHandler)
    {
        returnedHandler = handler;
        return new MesTask
        {
            Id = "error-owner", Name = "error-owner", Enabled = true, CollectIntervalMs = 20, ConfirmRewrite = false,
            Flow = new MesFlow
            {
                Nodes =
                [
                    new MesNode { Id = "trigger", Type = "Trigger" },
                    new MesNode
                    {
                        Id = "upload", Type = "Upload",
                        Http = new MesHttp { Url = "http://localhost/mes" },
                        Payload = new MesPayload
                        {
                            Mode = MesPayloadModes.FieldMapping,
                            Fields = [new MesField { TagId = tagId, FieldName = "MESFlag" }],
                        },
                    },
                    new MesNode
                    {
                        Id = "write", Type = "ResponseWriteback",
                        WriteBack = new MesWriteBack
                        {
                            Confirm = false,
                            Manual = [new MesManualWrite { TagId = tagId, Value = "2" }],
                        },
                    },
                ],
                Edges =
                [
                    new MesEdge { From = "trigger", To = "upload" },
                    new MesEdge { From = "upload", To = "write" },
                ],
            },
        };
    }

    private sealed class SequentialResponseHandler : HttpMessageHandler
    {
        private int _requestCount;
        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int requestNumber = Interlocked.Increment(ref _requestCount);
            string body = requestNumber == 1
                ? "{\"code\":0,\"backResult\":{\"Value\":42}}"
                : "OK";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class StaticResponseHandler(string body) : HttpMessageHandler
    {
        private int _requestCount;
        public int RequestCount => Volatile.Read(ref _requestCount);
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Interlocked.Increment(ref _requestCount);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class CollectingLogger : ILogger
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Enqueue(formatter(state, exception));
    }

    private sealed class SuccessThenServerErrorHandler : HttpMessageHandler
    {
        private int _requestCount;
        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage requestMessage, CancellationToken cancellationToken)
        {
            int requestNumber = Interlocked.Increment(ref _requestCount);
            var response = requestNumber == 1
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"code\":0,\"backResult\":{\"MESFlag\":4}}", Encoding.UTF8, "application/json"),
                }
                : new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("backend unavailable", Encoding.UTF8, "text/plain"),
                };
            return Task.FromResult(response);
        }
    }
}
