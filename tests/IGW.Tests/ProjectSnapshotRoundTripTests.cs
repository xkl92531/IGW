using System.Text;
using IGW.Abstractions;
using IGW.Core;
using IGW.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IGW.Tests;

public sealed class ProjectSnapshotRoundTripTests
{
    [Fact]
    public async Task Delta_snapshot_round_trip_preserves_changes_secrets_and_conflict_protection()
    {
        await using var fixture = await SnapshotDbFixture.CreateAsync();
        var original = FullProject();
        var revision = await fixture.Writer.WriteAsync(original, new ProjectRevision(0), default);
        var changed = await fixture.Reader.ReadAsync(default);
        changed.Description = "delta project";
        var channel = changed.Channels[0];
        channel.Description = "delta channel";
        channel.Settings["password"] = "changed-secret";
        var device = channel.Devices[0];
        device.Endpoint = "changed-endpoint";
        device.Groups[1].Description = "delta group";
        device.Groups[1].Tags[0].ScanRateMs = 123;
        changed.MqttPublish.Password = "changed-mqtt-secret";
        changed.MesUpload.Tasks[0].CollectIntervalMs = 731;
        changed.History.Enabled = !changed.History.Enabled;

        var updated = await fixture.Writer.WriteAsync(changed, revision, default);

        Assert.Equal(revision.Value + 1, updated.Value);
        var saved = await fixture.Reader.ReadAsync(default);
        Assert.Equal(ProjectStore.ToJson(changed), ProjectStore.ToJson(saved));
        await Assert.ThrowsAsync<ProjectRevisionConflictException>(() => fixture.Writer.WriteAsync(original, revision, default));
        Assert.Equal(ProjectStore.ToJson(changed), ProjectStore.ToJson(await fixture.Reader.ReadAsync(default)));

        // Renaming changes canonical paths and takes the complete snapshot path.
        changed.Channels[0].Devices[0].Groups[1].Tags[0].Name = "RenamedResult";
        await fixture.Writer.WriteAsync(changed, updated, default);
        Assert.Equal(ProjectStore.ToJson(changed), ProjectStore.ToJson(await fixture.Reader.ReadAsync(default)));
    }

    [Fact]
    public async Task Snapshot_round_trip_preserves_all_supported_project_fields()
    {
        await using var fixture = await SnapshotDbFixture.CreateAsync();
        IgwProject expected = FullProject();

        ProjectRevision revision = await fixture.Writer.WriteAsync(
            expected, new ProjectRevision(0), default);
        IgwProject actual = await fixture.Reader.ReadAsync(default);

        Assert.Equal(1, revision.Value);
        Assert.Equal(ProjectStore.ToJson(expected), ProjectStore.ToJson(actual));
    }

    [Fact]
    public async Task Snapshot_database_never_contains_plaintext_reversible_secrets()
    {
        await using var fixture = await SnapshotDbFixture.CreateAsync();
        IgwProject project = FullProject();

        await fixture.Writer.WriteAsync(project, new ProjectRevision(0), default);

        string databaseBytes = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(fixture.Database.FilePath));
        Assert.DoesNotContain("mqtt-plain-marker", databaseBytes);
        Assert.DoesNotContain("mes-token-marker", databaseBytes);
        Assert.DoesNotContain("opcua-password-marker", databaseBytes);
        Assert.DoesNotContain("channel-password-marker", databaseBytes);
        Assert.DoesNotContain("device-token-marker", databaseBytes);
        Assert.DoesNotContain("template-secret-marker", databaseBytes);

        IgwProject restored = await fixture.Reader.ReadAsync(default);
        Assert.Equal("mqtt-plain-marker", restored.MqttPublish.Password);
        Assert.Equal("mes-token-marker", restored.MesUpload.AuthBearerToken);
        Assert.Equal("opcua-password-marker", restored.OpcUa.Users.Single().Password);
        Assert.Equal("channel-password-marker", restored.Channels.Single().Settings["password"]);
        Assert.Equal("device-token-marker", restored.Channels.Single().Devices.Single().Settings["apiToken"]);
        Assert.Equal("template-secret-marker", restored.Templates.Single().DefaultSettings["secret"]);
    }

    [Fact]
    public async Task Snapshot_rebuilds_queryable_index_for_mes_tag_and_path_references()
    {
        await using var fixture = await SnapshotDbFixture.CreateAsync();
        await fixture.Writer.WriteAsync(FullProject(), new ProjectRevision(0), default);

        await using SqliteConnection connection = await fixture.Database.OpenAsync(default);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT node_type, referenced_tag_id, referenced_path
            FROM IGW_mes_task_index
            WHERE task_id='mes-task-1'
            ORDER BY node_type, referenced_path;
            """;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        var rows = new List<(string Type, string? TagId, string Path)>();
        while (await reader.ReadAsync())
            rows.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2)));

        Assert.Contains(("Trigger", "tag-result", "PLC8.Device8.OP200Data.ResultFlag"), rows);
        Assert.Contains(("DataRange", "tag-bolts", "PLC8.Device8.OP200Data.Bolts"), rows);
        Assert.Contains(("FlowWriteback", "tag-result", "PLC8.Device8.OP200Data.ResultFlag"), rows);
    }

    private static IgwProject FullProject()
    {
        const string devicePath = "PLC8.Device8";
        const string groupPath = devicePath + ".OP200Data";
        const string resultPath = groupPath + ".ResultFlag";
        const string arrayPath = groupPath + ".Bolts";

        var resultTag = new TagDef
        {
            Id = "tag-result",
            Name = "ResultFlag",
            Address = "DB200.DBW0",
            DataType = IgwDataType.Int16,
            Access = TagAccess.ReadWrite,
            ScanRateMs = 20,
            Scaling = new ScalingDef
            {
                Enabled = true,
                RawLow = 0,
                RawHigh = 100,
                EngLow = -10,
                EngHigh = 10,
                Clamp = true,
            },
            Deadband = 0.5,
            DeadbandType = DeadbandType.Absolute,
            Description = "结果标志",
            EngUnit = "code",
            Metadata = new Dictionary<string, string> { ["nodeId"] = "ns=3;s=ResultFlag" },
            Alarm = new AlarmDef { Enabled = true, HiHi = 90, Hi = 80, Lo = 10, LoLo = 5, Deadband = 1 },
            WriteConfirmation = new WriteConfirmationDef { Mode = WriteConfirmationMode.ReadBack },
        };
        var stringTag = new TagDef
        {
            Id = "tag-message",
            Name = "Message",
            Address = "DB200.DBB2",
            DataType = IgwDataType.String,
            Access = TagAccess.ReadWrite,
            ScanRateMs = 50,
            StringLength = 64,
            TextEncoding = PlcTextEncoding.Gb18030,
            Description = "消息",
            WriteConfirmation = new WriteConfirmationDef { Mode = WriteConfirmationMode.ReadBack },
        };
        var arrayTag = new TagDef
        {
            Id = "tag-bolts",
            Name = "Bolts",
            Address = "DB200.DBB100",
            DataType = IgwDataType.Struct,
            IsArray = true,
            Access = TagAccess.Read,
            ScanRateMs = 100,
            Description = "嵌套结构体数组",
            Array = new ArrayDef
            {
                ElementType = IgwDataType.Struct,
                Dimensions = [2, 3],
                LowerBounds = [0, 1],
                ElementStrideBytes = 16,
                SourceShape = "ARRAY[0..1,1..3] OF Bolt",
                Struct = new StructDef
                {
                    Id = "struct-bolt",
                    Name = "Bolt",
                    SizeBytes = 16,
                    Fields =
                    [
                        new StructFieldDef { Name = "Torque", DataType = IgwDataType.Float, ByteOffset = 0 },
                        new StructFieldDef
                        {
                            Name = "Samples",
                            DataType = IgwDataType.Int16,
                            ByteOffset = 4,
                            Array = new ArrayDef
                            {
                                ElementType = IgwDataType.Int16,
                                Dimensions = [4],
                                LowerBounds = [0],
                                ElementStrideBytes = 2,
                            },
                        },
                    ],
                },
            },
        };
        var group = new TagGroupDef
        {
            Id = "group-op200data",
            Name = "OP200Data",
            Description = "业务数据块",
            Tags = [resultTag, stringTag, arrayTag],
            SubGroups =
            [
                new TagGroupDef
                {
                    Id = "group-nested",
                    Name = "Nested",
                    Description = "嵌套组",
                    Tags =
                    [
                        new TagDef
                        {
                            Id = "tag-confirm",
                            Name = "Confirm",
                            Address = "DB200.DBX50.0",
                            DataType = IgwDataType.Bool,
                            Access = TagAccess.Read,
                            ScanRateMs = 20,
                        },
                    ],
                },
            ],
        };
        var channel = new ChannelDef
        {
            Id = "channel-plc8",
            Name = "PLC8",
            DriverId = "Siemens.S7",
            Description = "主通道",
            Settings = new Dictionary<string, string>
            {
                ["networkAdapter"] = "192.168.1.20",
                ["password"] = "channel-password-marker",
            },
            Devices =
            [
                new DeviceDef
                {
                    Id = "device-8",
                    Name = "Device8",
                    Endpoint = "192.168.1.8:102",
                    Description = "S7-1500",
                    Settings = new Dictionary<string, string>
                    {
                        ["model"] = "S7-1500",
                        ["rack"] = "0",
                        ["slot"] = "1",
                        ["apiToken"] = "device-token-marker",
                    },
                    Groups =
                    [
                        new TagGroupDef
                        {
                            Id = "group-z-first",
                            Name = "ZZZ_FirstByConfiguredOrder",
                            Description = "验证同级顺序不按名称重排",
                        },
                        group,
                    ],
                },
            ],
        };
        var task = new MesTask
        {
            Id = "mes-task-1",
            Name = "OP200 上报",
            FlowAvailable = true,
            Enabled = true,
            GroupRef = groupPath,
            DeviceRef = devicePath,
            CollectIntervalMs = 20,
            ConfirmRewrite = true,
            Trigger = new MesTrigger { Mode = "OnChange", TagId = resultPath, Op = "Eq", Value = "1" },
            Payload = new MesPayload
            {
                Mode = MesPayloadModes.DataRanges,
                DataRanges = [new MesDataRange { SourcePath = arrayPath, Scope = "Array", OutputPath = "bolts" }],
                Append = new Dictionary<string, string> { ["station"] = "OP200" },
            },
            Flow = new MesFlow
            {
                Nodes =
                [
                    new MesNode { Id = "trigger", Type = "Trigger", Title = "采集周期", X = 10, Y = 20 },
                    new MesNode
                    {
                        Id = "upload",
                        Type = "Upload",
                        Title = "上报",
                        X = 220,
                        Y = 20,
                        Http = new MesHttp
                        {
                            Url = "http://mes.local/report",
                            SuccessKey = "isSuccess",
                            SuccessValue = "true",
                        },
                    },
                    new MesNode
                    {
                        Id = "writeback",
                        Type = "ResponseWriteback",
                        Title = "响应直写",
                        X = 430,
                        Y = 20,
                        WriteBack = new MesWriteBack
                        {
                            Confirm = true,
                            Retries = 10,
                            FromResponse = [new MesResponseWrite { ResponseField = "resultCode", TagId = resultPath }],
                            Manual = [new MesManualWrite { TagId = resultPath, Value = "4" }],
                        },
                    },
                ],
                Edges =
                [
                    new MesEdge { From = "trigger", To = "upload" },
                    new MesEdge { From = "upload", To = "writeback" },
                ],
            },
        };

        return new IgwProject
        {
            SchemaVersion = 3,
            Name = "完整工程",
            Description = "SQLite 往返",
            Settings = new ProjectSettings
            {
                RestPort = 48020,
                OpcUaPort = 4840,
                ManagementPort = 48010,
                LogRetentionDays = 45,
                LogStorageDirectory = Path.Combine(Path.GetTempPath(), "igw-custom-logs"),
                TagStream = new TagStreamSettings
                {
                    Enabled = true,
                    MaxTagsPerStream = 10_000,
                    MaxStreams = 100,
                    HeartbeatSeconds = 10,
                    SnapshotIntervalSeconds = 60,
                    MaxConcurrentSnapshots = 4,
                    DeltaBatchWindowMs = 1,
                    CoordinatorQueueCapacity = 262_144,
                },
            },
            OpcUa = new OpcUaSettings
            {
                Enabled = true,
                Port = 4840,
                AllowAnonymous = false,
                Users = [new OpcUaUser { Username = "operator", Password = "opcua-password-marker" }],
            },
            History = new HistorySettings { Enabled = true, DbPath = "history.db", FlushIntervalMs = 500, RetentionRows = 123_456 },
            MqttPublish = new MqttPublishSettings
            {
                Enabled = true,
                BrokerHost = "mqtt.local",
                BrokerPort = 1883,
                ClientId = "igw-1",
                TopicPrefix = "factory",
                Username = "publisher",
                Password = "mqtt-plain-marker",
                Qos = 1,
                Retain = true,
                PublishIntervalMs = 100,
            },
            ComputedTags =
            [
                new ComputedTagDef
                {
                    Name = "Computed.ResultDouble",
                    Expression = $"{{{resultPath}}}*2",
                    DataType = IgwDataType.Int32,
                    Description = "结果翻倍",
                },
            ],
            Schedules =
            [
                new ScheduleJobDef { Name = "读取结果", Type = "Read", TagId = resultPath, IntervalMs = 500, Enabled = true },
            ],
            Security = new SecuritySettings
            {
                Enabled = true,
                Users =
                [
                    new UserDef
                    {
                        Username = "admin",
                        PasswordHash = "pbkdf2-sha256$210000$c2FsdHNhbHRzYWx0c2FsdA==$aGFzaGhhc2hoYXNoaGFzaGhhc2hoYXNoaGFzaGhhc2hoYXNoaA==",
                        CanWrite = true,
                        ReadBranches = ["PLC8.Device8.OP200Data"],
                    },
                ],
            },
            MesUpload = new MesUploadSettings
            {
                Enabled = true,
                DefaultMesUrl = "http://mes.local/default",
                AuthBearerToken = "mes-token-marker",
                HttpTimeoutMs = 5_000,
                QueueCapacity = 2_000,
                LogDbPath = "mesupload.db",
                Tasks = [task],
                FlowTemplates =
                [
                    new MesFlowTemplate
                    {
                        Id = "flow-template-1",
                        Name = "上报模板",
                        Description = "模板说明",
                        SourceGroupRef = groupPath,
                        Task = task,
                    },
                ],
            },
            Templates =
            [
                new DeviceTemplate
                {
                    Id = "device-template-1",
                    Name = "S7 标准模板",
                    DriverId = "Siemens.S7",
                    Description = "设备模板",
                    DefaultSettings = new Dictionary<string, string>
                    {
                        ["model"] = "S7-1500",
                        ["secret"] = "template-secret-marker",
                    },
                    Tags =
                    [
                        new TagDef
                        {
                            Id = "template-tag-1",
                            Name = "Heartbeat",
                            Address = "DB1.DBW0",
                            DataType = IgwDataType.UInt16,
                            Access = TagAccess.ReadWrite,
                            ScanRateMs = 100,
                            WriteConfirmation = new WriteConfirmationDef { Mode = WriteConfirmationMode.ReadBack },
                        },
                    ],
                },
            ],
            Channels = [channel],
        };
    }

    private sealed class SnapshotDbFixture : IAsyncDisposable
    {
        private readonly string _directory;
        public ProjectDatabase Database { get; }
        public ProjectSnapshotWriter Writer { get; }
        public ProjectSnapshotReader Reader { get; }

        private SnapshotDbFixture(
            string directory,
            ProjectDatabase database,
            ProjectSnapshotWriter writer,
            ProjectSnapshotReader reader)
        {
            _directory = directory;
            Database = database;
            Writer = writer;
            Reader = reader;
        }

        public static async Task<SnapshotDbFixture> CreateAsync()
        {
            string directory = Path.Combine(Path.GetTempPath(), "igw-snapshot-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var database = new ProjectDatabase(Path.Combine(directory, "project.db"));
            await new ProjectSchemaMigrator(database).MigrateAsync(default);
            var vault = new ProjectSecretVault(
                new InstallationSecretProtector(Path.Combine(directory, "igw.masterkey")));
            return new SnapshotDbFixture(
                directory,
                database,
                new ProjectSnapshotWriter(database, vault),
                new ProjectSnapshotReader(database, vault));
        }

        public ValueTask DisposeAsync()
        {
            Directory.Delete(_directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
