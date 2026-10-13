using IGW.Abstractions;
using IGW.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class WriteVerificationEngineTests
{
    [Fact]
    public async Task Write_only_point_can_explicitly_accept_protocol_ack_without_fake_readback()
    {
        var definition = new TagDef
        {
            Name = "Command", Address = "Command", DataType = IgwDataType.Int16,
            Access = TagAccess.Write, ScanRateMs = 30_000,
            WriteConfirmation = new WriteConfirmationDef
            {
                Mode = WriteConfirmationMode.ProtocolAcknowledged,
            },
        };
        var binding = new TagBinding
        {
            TagId = "Line.Plc.DB.Command", Def = definition, Address = new TagAddress(0, 0, 2),
        };
        await using var hub = new DataHub();
        hub.Register(binding.TagId, binding.Def);
        var driver = new VerificationDriver(failReadBacks: 0);
        var loop = new DeviceLoop(driver, [binding], hub, NullLogger.Instance, "Line.Plc", idlePollMs: 1);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        loop.Start(stop.Token);

        StatusCode status = await loop.EnqueueWriteAsync(Request(binding, 7)).WaitAsync(stop.Token);

        Assert.Equal(StatusCode.GoodUnverified, status);
        Assert.True(status.IsGood);
        Assert.Equal(1, driver.WriteCalls);
        Assert.Equal(0, driver.ReadCalls);
        stop.Cancel();
        await loop.Completion;
    }

    [Fact]
    public async Task Write_only_point_without_confirmation_strategy_is_rejected_before_driver_call()
    {
        var definition = new TagDef
        {
            Name = "Command", Address = "Command", DataType = IgwDataType.Int16,
            Access = TagAccess.Write, ScanRateMs = 30_000,
        };
        var binding = new TagBinding
        {
            TagId = "Line.Plc.DB.Command", Def = definition, Address = new TagAddress(0, 0, 2),
        };
        await using var hub = new DataHub();
        hub.Register(binding.TagId, binding.Def);
        var driver = new VerificationDriver(failReadBacks: 0);
        var loop = new DeviceLoop(driver, [binding], hub, NullLogger.Instance, "Line.Plc", idlePollMs: 1);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        loop.Start(stop.Token);

        StatusCode status = await loop.EnqueueWriteAsync(Request(binding, 7)).WaitAsync(stop.Token);

        Assert.Equal(StatusCode.BadNotReadable, status);
        Assert.Equal(0, driver.WriteCalls);
        stop.Cancel();
        await loop.Completion;
    }

    [Fact]
    public async Task Write_only_point_can_be_verified_by_an_independent_confirmation_tag()
    {
        var command = new TagDef
        {
            Name = "Command", Address = "Command", DataType = IgwDataType.Int16,
            Access = TagAccess.Write, ScanRateMs = 30_000,
            WriteConfirmation = new WriteConfirmationDef
            {
                Mode = WriteConfirmationMode.ConfirmationTag,
                ConfirmationTagId = "Line.Plc.DB.CommandAccepted",
                ExpectedValue = "true",
            },
        };
        var confirmation = new TagDef
        {
            Name = "CommandAccepted", Address = "CommandAccepted", DataType = IgwDataType.Bool,
            Access = TagAccess.Read, ScanRateMs = 30_000,
        };
        var bindings = new[]
        {
            new TagBinding { TagId = "Line.Plc.DB.Command", Def = command, Address = new TagAddress(0, 0, 2) },
            new TagBinding { TagId = "Line.Plc.DB.CommandAccepted", Def = confirmation, Address = new TagAddress(0, 2, 1) },
        };
        await using var hub = new DataHub();
        foreach (var binding in bindings) hub.Register(binding.TagId, binding.Def);
        var driver = new ConfirmationTagDriver(bindings[1].TagId);
        var loop = new DeviceLoop(driver, bindings, hub, NullLogger.Instance, "Line.Plc", idlePollMs: 1);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        loop.Start(stop.Token);

        StatusCode status = await loop.EnqueueWriteAsync(Request(bindings[0], 7)).WaitAsync(stop.Token);

        Assert.Equal(StatusCode.Good, status);
        Assert.Equal(1, driver.WriteCalls);
        Assert.Equal(new[] { bindings[1].TagId }, driver.LastReadTagIds);
        stop.Cancel();
        await loop.Completion;
    }

    [Fact]
    public async Task Rewrites_only_until_the_real_device_value_matches()
    {
        var fixture = Fixture(failReadBacks: 2);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        fixture.Loop.Start(stop.Token);

        var status = await fixture.Loop.EnqueueWriteAsync(fixture.Request(42)).WaitAsync(stop.Token);

        Assert.Equal(StatusCode.Good, status);
        Assert.Equal(3, fixture.Driver.WriteCalls);
        Assert.Equal(3, fixture.Driver.ReadCalls);
        stop.Cancel();
        await fixture.Loop.Completion;
        await fixture.Hub.DisposeAsync();
    }

    [Fact]
    public async Task Successful_write_publishes_the_verified_value_to_QC_cache_immediately()
    {
        var fixture = Fixture(failReadBacks: 0);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        fixture.Loop.Start(stop.Token);

        var status = await fixture.Loop.EnqueueWriteAsync(fixture.Request(42)).WaitAsync(stop.Token);

        Assert.Equal(StatusCode.Good, status);
        Assert.True(fixture.Hub.TryGet(fixture.Binding.TagId, out var cached));
        Assert.Equal((short)42, cached.Value);
        Assert.Equal(StatusCode.Good, cached.Quality);
        stop.Cancel();
        await fixture.Loop.Completion;
        await fixture.Hub.DisposeAsync();
    }

    [Fact]
    public async Task Stops_after_ten_rewrites_without_disconnecting_the_session()
    {
        var fixture = Fixture(failReadBacks: int.MaxValue);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        fixture.Loop.Start(stop.Token);

        var status = await fixture.Loop.EnqueueWriteAsync(fixture.Request(42)).WaitAsync(stop.Token);

        Assert.Equal(StatusCode.BadNotVerified, status);
        Assert.Equal(11, fixture.Driver.WriteCalls); // 初写 1 次 + 重写最多 10 次
        Assert.Equal(ConnectionState.Connected, fixture.Driver.State);
        Assert.Equal(0, fixture.Driver.DisconnectCalls);
        stop.Cancel();
        await fixture.Loop.Completion;
        await fixture.Hub.DisposeAsync();
    }

    [Fact]
    public async Task A_verified_field_is_removed_and_only_the_mismatched_field_is_rewritten()
    {
        var definitions = new[]
        {
            new TagDef { Name = "A", Address = "A", DataType = IgwDataType.Int16, Access = TagAccess.ReadWrite, ScanRateMs = 30_000 },
            new TagDef { Name = "B", Address = "B", DataType = IgwDataType.Int16, Access = TagAccess.ReadWrite, ScanRateMs = 30_000 },
        };
        var bindings = definitions.Select((definition, index) => new TagBinding
        {
            TagId = $"Line.Plc.DB.{definition.Name}", Def = definition,
            Address = new TagAddress(0, index * 2, 2),
        }).ToArray();
        var hub = new DataHub();
        foreach (var binding in bindings) hub.Register(binding.TagId, binding.Def);
        var driver = new SelectiveVerificationDriver("Line.Plc.DB.B");
        var loop = new DeviceLoop(driver, bindings, hub, NullLogger.Instance, "Line.Plc", idlePollMs: 10);
        var first = loop.EnqueueWriteAsync(Request(bindings[0], 11));
        var second = loop.EnqueueWriteAsync(Request(bindings[1], 22));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        loop.Start(stop.Token);

        Assert.Equal(StatusCode.Good, await first.WaitAsync(stop.Token));
        Assert.Equal(StatusCode.Good, await second.WaitAsync(stop.Token));
        Assert.Equal(new[] { "Line.Plc.DB.A", "Line.Plc.DB.B" }, driver.WriteBatches[0]);
        Assert.Equal(new[] { "Line.Plc.DB.B" }, driver.WriteBatches[1]);

        stop.Cancel();
        await loop.Completion;
        await hub.DisposeAsync();
    }

    [Fact]
    public async Task A_point_value_exception_does_not_disconnect_the_healthy_session()
    {
        var fixture = Fixture(failReadBacks: 0);
        fixture.Driver.WriteError = new InvalidDataException("PLC rejected this field type");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        fixture.Loop.Start(stop.Token);

        var status = await fixture.Loop.EnqueueWriteAsync(fixture.Request(42)).WaitAsync(stop.Token);

        Assert.Equal(StatusCode.BadDecodeError, status);
        Assert.Equal(ConnectionState.Connected, fixture.Driver.State);
        Assert.Equal(0, fixture.Driver.DisconnectCalls);
        stop.Cancel();
        await fixture.Loop.Completion;
        await fixture.Hub.DisposeAsync();
    }

    [Fact]
    public async Task Permanent_point_rejection_does_not_retry_or_block_other_good_writes()
    {
        var definitions = new[]
        {
            new TagDef { Name = "Allowed", Address = "Allowed", DataType = IgwDataType.Int16, Access = TagAccess.ReadWrite, ScanRateMs = 30_000 },
            new TagDef { Name = "ReadOnly", Address = "ReadOnly", DataType = IgwDataType.Int16, Access = TagAccess.ReadWrite, ScanRateMs = 30_000 },
        };
        var bindings = definitions.Select((definition, index) => new TagBinding
        {
            TagId = $"Line.Plc.DB.{definition.Name}", Def = definition,
            Address = new TagAddress(0, index * 2, 2),
        }).ToArray();
        await using var hub = new DataHub();
        foreach (var binding in bindings) hub.Register(binding.TagId, binding.Def);
        var driver = new PerPointRejectionDriver(bindings[1].TagId);
        var loop = new DeviceLoop(driver, bindings, hub, NullLogger.Instance, "Line.Plc", idlePollMs: 10);
        Task<StatusCode> allowed = loop.EnqueueWriteAsync(Request(bindings[0], 11));
        Task<StatusCode> rejected = loop.EnqueueWriteAsync(Request(bindings[1], 22));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        loop.Start(stop.Token);
        try
        {
            Assert.Equal(StatusCode.Good, await allowed.WaitAsync(stop.Token));
            Assert.Equal(StatusCode.BadNotWritable, await rejected.WaitAsync(stop.Token));
            Assert.Equal(1, driver.WriteCalls);
            Assert.Equal(0, driver.DisconnectCalls);
        }
        finally
        {
            stop.Cancel();
            await loop.Completion;
        }
    }

    [Theory]
    [InlineData(0x801F0000u)] // BadUserAccessDenied
    [InlineData(0x80340000u)] // BadNodeIdUnknown
    [InlineData(0x803C0000u)] // BadOutOfRange
    [InlineData(0x80740000u)] // BadTypeMismatch
    public async Task Permanent_protocol_rejections_are_not_rewritten_ten_times(uint rawStatus)
    {
        var definition = new TagDef
        {
            Name = "Rejected", Address = "Rejected", DataType = IgwDataType.Int16,
            Access = TagAccess.ReadWrite, ScanRateMs = 30_000,
        };
        var binding = new TagBinding
        {
            TagId = "Line.Plc.DB.Rejected", Def = definition, Address = new TagAddress(0, 0, 2),
        };
        await using var hub = new DataHub();
        hub.Register(binding.TagId, binding.Def);
        var driver = new PerPointRejectionDriver(binding.TagId, new StatusCode(rawStatus));
        var loop = new DeviceLoop(driver, [binding], hub, NullLogger.Instance, "Line.Plc", idlePollMs: 1);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        loop.Start(stop.Token);

        StatusCode actual = await loop.EnqueueWriteAsync(Request(binding, 7)).WaitAsync(stop.Token);

        Assert.Equal(rawStatus, actual.Code);
        Assert.Equal(1, driver.WriteCalls);
        Assert.Equal(0, driver.DisconnectCalls);
        stop.Cancel();
        await loop.Completion;
    }

    [Fact]
    public async Task Verification_reads_respect_the_negotiated_read_batch_limit()
    {
        var definitions = Enumerable.Range(0, 5).Select(index => new TagDef
        {
            Name = $"Value{index}", Address = $"Value{index}", DataType = IgwDataType.Int16,
            Access = TagAccess.ReadWrite, ScanRateMs = 30_000,
        }).ToArray();
        var bindings = definitions.Select((definition, index) => new TagBinding
        {
            TagId = $"Line.Plc.DB.{definition.Name}", Def = definition,
            Address = new TagAddress(0, index * 2, 2),
        }).ToArray();
        await using var hub = new DataHub();
        foreach (var binding in bindings) hub.Register(binding.TagId, binding.Def);
        var driver = new LimitedVerificationDriver(maxRead: 2);
        var loop = new DeviceLoop(driver, bindings, hub, NullLogger.Instance, "Line.Plc", idlePollMs: 1);
        var writes = bindings.Select((binding, index) =>
            loop.EnqueueWriteAsync(Request(binding, checked((short)(index + 1))))).ToArray();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        loop.Start(stop.Token);

        StatusCode[] statuses = await Task.WhenAll(writes).WaitAsync(stop.Token);

        Assert.All(statuses, status => Assert.Equal(StatusCode.Good, status));
        Assert.Equal(new[] { 1, 1, 1, 1, 1 }, driver.WriteBatchSizes);
        Assert.Equal(new[] { 2, 2, 1 }, driver.VerificationReadBatchSizes);
        stop.Cancel();
        await loop.Completion;
    }

    [Fact]
    public async Task Driver_applied_string_value_is_used_for_readback_verification()
    {
        var definition = new TagDef
        {
            Name = "Message", Address = "Message", DataType = IgwDataType.String,
            Access = TagAccess.ReadWrite, ScanRateMs = 30_000,
        };
        var binding = new TagBinding
        {
            TagId = "Line.Plc.DB.Message", Def = definition, Address = new TagAddress(0, 0, 1),
        };
        await using var hub = new DataHub();
        hub.Register(binding.TagId, binding.Def);
        var driver = new AppliedStringValueDriver("1234");
        var loop = new DeviceLoop(driver, [binding], hub, NullLogger.Instance, "Line.Plc", idlePollMs: 1);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        loop.Start(stop.Token);

        var status = await loop.EnqueueWriteAsync(new WriteRequest
        {
            TagId = binding.TagId,
            Def = binding.Def,
            Address = binding.Address,
            Value = "12345",
        }).WaitAsync(stop.Token);

        Assert.Equal(StatusCode.Good, status);
        Assert.Equal(1, driver.WriteCalls);
        Assert.True(hub.TryGet(binding.TagId, out var cached));
        Assert.Equal("1234", cached.Value);
        stop.Cancel();
        await loop.Completion;
    }

    [Fact]
    public async Task Successful_string_write_log_contains_endpoints_permission_capacity_length_and_status()
    {
        var definition = new TagDef
        {
            Name = "Message", Address = "ns=3;s=DB1.Message", DataType = IgwDataType.String,
            Access = TagAccess.ReadWrite, StringLength = 20, ScanRateMs = 30_000,
        };
        var binding = new TagBinding
        {
            TagId = "Line.Plc.DB.Message", Def = definition, Address = new TagAddress(0, 0, 1),
        };
        await using var hub = new DataHub();
        hub.Register(binding.TagId, binding.Def);
        var logger = new CollectingLogger();
        var driver = new AppliedStringValueDriver("现场异常");
        var loop = new DeviceLoop(driver, [binding], hub, logger, "Line.Plc", idlePollMs: 1);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        loop.Start(stop.Token);

        StatusCode status = await loop.EnqueueWriteAsync(new WriteRequest
        {
            TagId = binding.TagId,
            Def = binding.Def,
            Address = binding.Address,
            Value = "现场异常",
        }).WaitAsync(stop.Token);

        Assert.Equal(StatusCode.Good, status);
        string diagnostic = Assert.Single(logger.Messages, message => message.Contains("写入批次完成"));
        Assert.Contains("远程地址=opc.tcp://10.217.152.80:4840", diagnostic);
        Assert.Contains("本地地址=opc.tcp://127.0.0.1:52000", diagnostic);
        Assert.Contains("NodeId=ns=3;s=DB1.Message", diagnostic);
        Assert.Contains("类型=String", diagnostic);
        Assert.Contains("权限=ReadWrite", diagnostic);
        Assert.Contains("字段长度=20", diagnostic);
        Assert.Contains("写入长度=UTF16:4/UTF8:12", diagnostic);
        Assert.Contains("状态=Good", diagnostic);
        Assert.DoesNotContain("现场异常", diagnostic);
        stop.Cancel();
        await loop.Completion;
    }

    /// <summary>
    /// 回写节点上配的重试次数要真的封顶。配 2 次就写 3 遍(首发+2 次重写)后判负,
    /// 不能继续按引擎默认的 10 次硬扛——否则这个配置项等于没接通。
    /// </summary>
    [Fact]
    public async Task Configured_rewrite_limit_caps_the_retries()
    {
        var fixture = Fixture(failReadBacks: 5);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        fixture.Loop.Start(stop.Token);

        StatusCode status = await fixture.Loop
            .EnqueueWriteAsync(Request(fixture.Binding, 7, new WritePolicy(Verify: true, MaxRewrites: 2)))
            .WaitAsync(stop.Token);

        Assert.Equal(StatusCode.BadNotVerified, status);
        Assert.Equal(3, fixture.Driver.WriteCalls);
        stop.Cancel();
        await fixture.Loop.Completion;
    }

    /// <summary>同一个点、同样的坏回读，给够次数就该成功——证明上一条的失败来自配置而不是别的故障。</summary>
    [Fact]
    public async Task A_larger_rewrite_limit_still_succeeds_on_the_same_point()
    {
        var fixture = Fixture(failReadBacks: 5);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        fixture.Loop.Start(stop.Token);

        StatusCode status = await fixture.Loop
            .EnqueueWriteAsync(Request(fixture.Binding, 7, new WritePolicy(Verify: true, MaxRewrites: 10)))
            .WaitAsync(stop.Token);

        Assert.True(status.IsGood);
        Assert.Equal(6, fixture.Driver.WriteCalls);
        stop.Cancel();
        await fixture.Loop.Completion;
    }

    /// <summary>
    /// 关掉确认就不该回读。注意结果是 GoodUnverified 而不是 Good:
    /// 协议说写成功了,但 PLC 里究竟是什么值没人看过,状态要如实说出这件事。
    /// </summary>
    [Fact]
    public async Task Turning_confirmation_off_skips_the_read_back()
    {
        var fixture = Fixture(failReadBacks: 99);   // 回读必然不匹配:一旦验证就不可能通过
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        fixture.Loop.Start(stop.Token);

        StatusCode status = await fixture.Loop
            .EnqueueWriteAsync(Request(fixture.Binding, 7, new WritePolicy(Verify: false, MaxRewrites: 10)))
            .WaitAsync(stop.Token);

        Assert.Equal(StatusCode.GoodUnverified, status);
        Assert.True(status.IsGood);
        Assert.Equal(1, fixture.Driver.WriteCalls);
        stop.Cancel();
        await fixture.Loop.Completion;
    }

    /// <summary>没声明策略的调用方必须原样保持引擎历来的行为(验证 + 最多重写 10 次)。</summary>
    [Fact]
    public async Task Callers_that_declare_nothing_keep_the_engine_default()
    {
        Assert.True(WritePolicy.Default.Verify);
        Assert.Equal(10, WritePolicy.Default.MaxRewrites);

        var fixture = Fixture(failReadBacks: 5);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        fixture.Loop.Start(stop.Token);

        StatusCode status = await fixture.Loop
            .EnqueueWriteAsync(Request(fixture.Binding, 7))
            .WaitAsync(stop.Token);

        Assert.True(status.IsGood);
        Assert.Equal(6, fixture.Driver.WriteCalls);
        stop.Cancel();
        await fixture.Loop.Completion;
    }

    private static WriteRequest Request(TagBinding binding, short value, WritePolicy policy) => new()
    {
        TagId = binding.TagId, Def = binding.Def, Address = binding.Address, Value = value, Policy = policy,
    };

    private static WriteRequest Request(TagBinding binding, short value) => new()
    {
        TagId = binding.TagId, Def = binding.Def, Address = binding.Address, Value = value,
    };

    private static TestFixture Fixture(int failReadBacks)
    {
        var def = new TagDef
        {
            Name = "Value", Address = "Value", DataType = IgwDataType.Int16,
            Access = TagAccess.ReadWrite, ScanRateMs = 1000,
        };
        const string tagId = "Line.Plc.DB.Value";
        var hub = new DataHub();
        hub.Register(tagId, def);
        var binding = new TagBinding { TagId = tagId, Def = def, Address = new TagAddress(0, 0, 2) };
        var driver = new VerificationDriver(failReadBacks);
        var loop = new DeviceLoop(driver, [binding], hub, NullLogger.Instance, "Line.Plc", idlePollMs: 10);
        return new TestFixture(hub, driver, loop, binding);
    }

    private sealed record TestFixture(DataHub Hub, VerificationDriver Driver, DeviceLoop Loop, TagBinding Binding)
    {
        public WriteRequest Request(short value) => new()
        {
            TagId = Binding.TagId, Def = Binding.Def, Address = Binding.Address, Value = value,
        };
    }

    private sealed class VerificationDriver(int failReadBacks) : IDeviceDriver, IDriverCapabilityProvider
    {
        private short _value;
        public int WriteCalls { get; private set; }
        public int ReadCalls { get; private set; }
        public int DisconnectCalls { get; private set; }
        public Exception? WriteError { get; set; }
        public string DriverId => "Verification";
        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser { get; } = new TestParser();
        public DriverLimits Limits { get; } = new() { MaxTagsPerRead = 10_000, MaxTagsPerWrite = 10_000 };
        public DriverCapabilitySnapshot Capabilities => DriverCapabilitySnapshot.FromLimits(Limits) with
        {
            SuggestedVerificationDelayMs = 0,
        };

        public Task ConnectAsync(CancellationToken ct)
        {
            State = ConnectionState.Connected;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            DisconnectCalls++;
            State = ConnectionState.Disconnected;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
        {
            ReadCalls++;
            var now = DateTime.UtcNow;
            return Task.FromResult<IReadOnlyList<TagSample>>(tags.Select(tag =>
                new TagSample(tag.TagId, new DataValue(_value, StatusCode.Good, now, now))).ToArray());
        }

        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct)
        {
            if (WriteError is not null) throw WriteError;
            WriteCalls++;
            foreach (var write in writes)
                _value = WriteCalls <= failReadBacks ? (short)-1 : Convert.ToInt16(write.Value);
            return Task.FromResult<IReadOnlyList<WriteResult>>(writes.Select(write =>
                new WriteResult { TagId = write.TagId, Status = StatusCode.Good }).ToArray());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    }

    private sealed class ConfirmationTagDriver(string confirmationTagId) : IDeviceDriver, IDriverCapabilityProvider
    {
        public int WriteCalls { get; private set; }
        public string[] LastReadTagIds { get; private set; } = [];
        public string DriverId => "ConfirmationTag";
        public ConnectionState State { get; private set; }
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser { get; } = new TestParser();
        public DriverLimits Limits { get; } = new() { MaxTagsPerRead = 10_000, MaxTagsPerWrite = 10_000 };
        public DriverCapabilitySnapshot Capabilities => DriverCapabilitySnapshot.FromLimits(Limits) with
        {
            SuggestedVerificationDelayMs = 0,
        };

        public Task ConnectAsync(CancellationToken ct)
        {
            State = ConnectionState.Connected;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            State = ConnectionState.Disconnected;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
        {
            LastReadTagIds = tags.Select(tag => tag.TagId).ToArray();
            var now = DateTime.UtcNow;
            return Task.FromResult<IReadOnlyList<TagSample>>(tags.Select(tag => new TagSample(
                tag.TagId,
                new DataValue(string.Equals(tag.TagId, confirmationTagId, StringComparison.Ordinal),
                    StatusCode.Good, now, now))).ToArray());
        }

        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct)
        {
            WriteCalls++;
            return Task.FromResult<IReadOnlyList<WriteResult>>(writes.Select(write =>
                new WriteResult { TagId = write.TagId, Status = StatusCode.Good }).ToArray());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SelectiveVerificationDriver(string mismatchOnceTag) : IDeviceDriver, IDriverCapabilityProvider
    {
        private readonly Dictionary<string, short> _values = new(StringComparer.Ordinal);
        private bool _mismatchPending = true;
        public List<string[]> WriteBatches { get; } = [];
        public string DriverId => "SelectiveVerification";
        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser { get; } = new TestParser();
        public DriverLimits Limits { get; } = new() { MaxTagsPerRead = 10_000, MaxTagsPerWrite = 10_000 };
        public DriverCapabilitySnapshot Capabilities => DriverCapabilitySnapshot.FromLimits(Limits) with
        {
            SuggestedVerificationDelayMs = 0,
        };

        public Task ConnectAsync(CancellationToken ct)
        {
            State = ConnectionState.Connected;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            State = ConnectionState.Disconnected;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            return Task.FromResult<IReadOnlyList<TagSample>>(tags.Select(tag => new TagSample(tag.TagId,
                new DataValue(_values.GetValueOrDefault(tag.TagId), StatusCode.Good, now, now))).ToArray());
        }

        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct)
        {
            WriteBatches.Add(writes.Select(write => write.TagId).ToArray());
            foreach (var write in writes)
            {
                if (_mismatchPending && string.Equals(write.TagId, mismatchOnceTag, StringComparison.Ordinal))
                {
                    _values[write.TagId] = -1;
                    _mismatchPending = false;
                }
                else _values[write.TagId] = Convert.ToInt16(write.Value);
            }
            return Task.FromResult<IReadOnlyList<WriteResult>>(writes.Select(write =>
                new WriteResult { TagId = write.TagId, Status = StatusCode.Good }).ToArray());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PerPointRejectionDriver(
        string rejectedTag,
        StatusCode? rejection = null) : IDeviceDriver, IDriverCapabilityProvider
    {
        private readonly Dictionary<string, short> _values = new(StringComparer.Ordinal);
        public int WriteCalls { get; private set; }
        public int DisconnectCalls { get; private set; }
        public string DriverId => "PerPointRejection";
        public ConnectionState State { get; private set; }
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser { get; } = new TestParser();
        public DriverLimits Limits { get; } = new() { MaxTagsPerRead = 10_000, MaxTagsPerWrite = 10_000 };
        public DriverCapabilitySnapshot Capabilities => DriverCapabilitySnapshot.FromLimits(Limits) with
        {
            SuggestedVerificationDelayMs = 0,
        };

        public Task ConnectAsync(CancellationToken ct)
        {
            State = ConnectionState.Connected;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            DisconnectCalls++;
            State = ConnectionState.Disconnected;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            return Task.FromResult<IReadOnlyList<TagSample>>(tags.Select(tag => new TagSample(tag.TagId,
                new DataValue(_values.GetValueOrDefault(tag.TagId), StatusCode.Good, now, now))).ToArray());
        }

        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct)
        {
            WriteCalls++;
            return Task.FromResult<IReadOnlyList<WriteResult>>(writes.Select(write =>
            {
                if (string.Equals(write.TagId, rejectedTag, StringComparison.Ordinal))
                    return new WriteResult { TagId = write.TagId, Status = rejection ?? StatusCode.BadNotWritable };
                _values[write.TagId] = Convert.ToInt16(write.Value);
                return new WriteResult { TagId = write.TagId, Status = StatusCode.Good };
            }).ToArray());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class LimitedVerificationDriver(int maxRead) : IDeviceDriver, IDriverCapabilityProvider
    {
        private readonly Dictionary<string, short> _values = new(StringComparer.Ordinal);
        public List<int> WriteBatchSizes { get; } = [];
        public List<int> VerificationReadBatchSizes { get; } = [];
        public string DriverId => "LimitedVerification";
        public ConnectionState State { get; private set; }
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser { get; } = new TestParser();
        public DriverLimits Limits { get; } = new() { MaxTagsPerRead = 10_000, MaxTagsPerWrite = 10_000 };
        public DriverCapabilitySnapshot Capabilities => DriverCapabilitySnapshot.FromLimits(Limits) with
        {
            MaxTagsPerRead = maxRead,
            MaxTagsPerWrite = 10,
            SupportsBatchWrite = false,
            SuggestedVerificationDelayMs = 0,
        };

        public Task ConnectAsync(CancellationToken ct)
        {
            State = ConnectionState.Connected;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            State = ConnectionState.Disconnected;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
        {
            if (tags.Count > maxRead) throw new InvalidOperationException($"read batch {tags.Count} exceeds {maxRead}");
            VerificationReadBatchSizes.Add(tags.Count);
            var now = DateTime.UtcNow;
            return Task.FromResult<IReadOnlyList<TagSample>>(tags.Select(tag => new TagSample(tag.TagId,
                new DataValue(_values.GetValueOrDefault(tag.TagId), StatusCode.Good, now, now))).ToArray());
        }

        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct)
        {
            WriteBatchSizes.Add(writes.Count);
            if (writes.Count != 1) throw new InvalidOperationException("driver supports only scalar writes");
            foreach (var write in writes) _values[write.TagId] = Convert.ToInt16(write.Value);
            return Task.FromResult<IReadOnlyList<WriteResult>>(writes.Select(write =>
                new WriteResult { TagId = write.TagId, Status = StatusCode.Good }).ToArray());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class AppliedStringValueDriver(string appliedValue) : IDeviceDriver, IDriverCapabilityProvider, IRemoteEndpointDiagnostics
    {
        public int WriteCalls { get; private set; }
        public string DriverId => "AppliedStringValue";
        public ConnectionState State { get; private set; }
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser { get; } = new TestParser();
        public DriverLimits Limits { get; } = new() { MaxTagsPerRead = 10_000, MaxTagsPerWrite = 10_000 };
        public DriverCapabilitySnapshot Capabilities => DriverCapabilitySnapshot.FromLimits(Limits) with
        {
            SuggestedVerificationDelayMs = 0,
        };

        public Task ConnectAsync(CancellationToken ct)
        {
            State = ConnectionState.Connected;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            State = ConnectionState.Disconnected;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            return Task.FromResult<IReadOnlyList<TagSample>>(tags.Select(tag =>
                new TagSample(tag.TagId, new DataValue(appliedValue, StatusCode.Good, now, now))).ToArray());
        }

        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct)
        {
            WriteCalls++;
            return Task.FromResult<IReadOnlyList<WriteResult>>(writes.Select(write => new WriteResult
            {
                TagId = write.TagId,
                Status = StatusCode.Good,
                ActualValue = appliedValue,
                Attempts = 1,
            }).ToArray());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public RemoteEndpointSnapshot RemoteEndpoint() => new(
            "opc.tcp://10.217.152.80:4840",
            "opc.tcp://10.217.152.80:4840",
            "opc.tcp://127.0.0.1:52000");
    }

    private sealed class CollectingLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Messages) Messages.Add(formatter(state, exception));
        }
    }

    private sealed class TestParser : IAddressParser
    {
        public bool TryParse(string address, IgwDataType type, out TagAddress result, out string? error)
        {
            result = new TagAddress(0, 0, 2);
            error = null;
            return true;
        }
    }
}
