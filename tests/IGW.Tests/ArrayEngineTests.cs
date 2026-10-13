using IGW.Abstractions;
using IGW.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class ArrayEngineTests
{
    [Fact]
    public async Task Logical_array_registers_one_slot_and_element_alias_does_not_add_reads()
    {
        var driver = new RecordingArrayDriver();
        await using var engine = CreateEngine(driver);
        await engine.StartAsync();
        var tagId = "Line.Plc.DB.Values";
        engine.Touch(tagId);
        await WaitUntilAsync(() => engine.Hub.TryGet(tagId, out var data) &&
            data.Value is ArrayRuntimeValue, TimeSpan.FromSeconds(2));

        var element = engine.ReadArrayElement("Line.Plc.DB.Values[3]");
        _ = engine.ReadArrayElement("Line.Plc.DB.Values[4]");

        Assert.Single(engine.Hub.SortedTagIds);
        Assert.Equal((short)3, element.Value);
        Assert.Equal(1, driver.ReadCalls);
    }

    [Fact]
    public async Task Array_write_uses_device_serial_queue_and_returns_partial_failures()
    {
        var driver = new RecordingArrayDriver
        {
            NextWriteResult = new ArrayWriteResult(
                StatusCode.Uncertain, 1,
                [new ArrayElementFailure([2], StatusCode.BadCommFailure, "第二个元素失败")]),
        };
        await using var engine = CreateEngine(driver);
        await engine.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var value = PrimitiveArrayValue<short>.Create(ArrayShape.Create([2], [1]), [7, 8]);

        var result = await engine.WriteArrayAsync(
            "Line.Plc.DB.Values", new ArraySlice([1], [2]), value, timeout.Token);

        Assert.Equal(11, driver.ArrayWriteCalls);
        Assert.Equal(StatusCode.BadNotVerified, result.Status);
        Assert.Equal(0, result.Succeeded);
        Assert.Single(result.Failures);
    }

    [Fact]
    public async Task Array_value_failure_does_not_disconnect_a_still_connected_device()
    {
        var driver = new RecordingArrayDriver
        {
            ArrayWriteError = new InvalidDataException("array slice does not match the target type"),
        };
        await using var engine = CreateEngine(driver);
        await engine.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var value = PrimitiveArrayValue<short>.Create(ArrayShape.Create([2], [1]), [7, 8]);

        var result = await engine.WriteArrayAsync(
            "Line.Plc.DB.Values", new ArraySlice([1], [2]), value, timeout.Token);

        Assert.Equal(StatusCode.BadDecodeError, result.Status);
        Assert.Equal(0, driver.DisconnectCalls);
        Assert.Equal(ConnectionState.Connected, driver.State);
    }

    [Fact]
    public async Task Array_verification_rewrites_only_the_mismatched_contiguous_slice()
    {
        var driver = new RecordingArrayDriver { PersistentMismatchIndex = 2 };
        await using var engine = CreateEngine(driver);
        await engine.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var value = PrimitiveArrayValue<short>.Create(ArrayShape.Create([4], [1]), [7, 8, 9, 10]);

        var result = await engine.WriteArrayAsync(
            "Line.Plc.DB.Values", new ArraySlice([1], [4]), value, timeout.Token);

        Assert.Equal(StatusCode.BadNotVerified, result.Status);
        Assert.Equal(11, driver.ArrayWriteCalls);
        Assert.Equal(4, driver.WrittenElementCounts[0]);
        Assert.All(driver.WrittenElementCounts.Skip(1), count => Assert.Equal(1, count));
        Assert.All(driver.WrittenStarts.Skip(1), start => Assert.Equal(3, start));
    }

    [Fact]
    public async Task Newer_array_generation_stops_an_older_write_from_verifying_or_rewriting()
    {
        var driver = new RecordingArrayDriver { BlockFirstArrayWrite = true };
        await using var engine = CreateEngine(driver);
        await engine.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var oldValue = PrimitiveArrayValue<short>.Create(ArrayShape.Create([1], [1]), [7]);
        var newValue = PrimitiveArrayValue<short>.Create(ArrayShape.Create([1], [1]), [9]);

        Task<ArrayWriteResult> oldWrite = engine.WriteArrayAsync(
            "Line.Plc.DB.Values", new ArraySlice([1], [1]), oldValue, timeout.Token);
        await driver.FirstArrayWriteStarted.Task.WaitAsync(timeout.Token);
        Task<ArrayWriteResult> newWrite = engine.WriteArrayAsync(
            "Line.Plc.DB.Values", new ArraySlice([1], [1]), newValue, timeout.Token);
        driver.ReleaseFirstArrayWrite.TrySetResult(true);

        var oldResult = await oldWrite;
        var newResult = await newWrite;

        Assert.Equal(StatusCode.BadWriteSuperseded, oldResult.Status);
        Assert.Equal(StatusCode.Good, newResult.Status);
        Assert.Equal((short)9, driver.CurrentValues[0]);
        Assert.Equal(2, driver.ArrayWriteCalls);
        Assert.True(engine.Hub.TryGet("Line.Plc.DB.Values", out var cached));
        var cachedArray = Assert.IsAssignableFrom<ArrayRuntimeValue>(cached.Value);
        Assert.Equal((short)9, cachedArray.GetBoxedValue(0));
    }

    [Fact]
    public async Task Scalar_alias_write_is_forwarded_as_one_element_array_slice()
    {
        var driver = new RecordingArrayDriver();
        await using var engine = CreateEngine(driver);
        await engine.StartAsync();
        engine.Touch("Line.Plc.DB.Values");
        await WaitUntilAsync(() => engine.Hub.TryGet("Line.Plc.DB.Values", out var data) &&
            data.Value is ArrayRuntimeValue, TimeSpan.FromSeconds(2));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var status = await engine.WriteAsync("Line.Plc.DB.Values[3]", "27", timeout.Token);

        Assert.Equal(StatusCode.Good, status);
        Assert.NotNull(driver.LastArrayWrite);
        Assert.Equal(new[] { 3 }, driver.LastArrayWrite!.Slice.Start);
        Assert.Equal(new[] { 1 }, driver.LastArrayWrite.Slice.Lengths);
        Assert.Equal((short)27, driver.LastArrayWrite.Value.GetBoxedValue(0));
    }

    [Fact]
    public async Task Invalid_scalar_numeric_text_returns_decode_error_without_reaching_driver()
    {
        var driver = new RecordingArrayDriver();
        var registry = new DriverRegistry();
        registry.Register(new Factory(driver));
        var project = Project();
        project.Channels[0].Devices[0].Groups[0].Tags =
        [
            new TagDef
            {
                Name = "MESFlag",
                Address = "DB1.DBW0",
                DataType = IgwDataType.Int16,
                Access = TagAccess.ReadWrite,
                ScanRateMs = 30_000,
            },
        ];
        await using var engine = new RuntimeEngine(project, registry, new DataHub(), NullLoggerFactory.Instance);
        await engine.StartAsync();

        StatusCode status = await engine.WriteAsync("Line.Plc.DB.MESFlag", "not-a-number", CancellationToken.None);

        Assert.Equal(StatusCode.BadDecodeError, status);
        Assert.Equal(0, driver.WriteCalls);
    }

    [Fact]
    public void Struct_member_alias_builds_a_single_element_replacement_without_losing_other_members()
    {
        var shape = ArrayShape.Create([2], [1]);
        var schema = new StructDef
        {
            Name = "CellData",
            Fields =
            [
                new StructFieldDef { Name = "Code", DataType = IgwDataType.String, StringLength = 4 },
                new StructFieldDef { Name = "IsNG", DataType = IgwDataType.Int16 },
            ],
        };
        var value = StructArrayValue.Create(shape, schema,
            new Dictionary<string, ArrayRuntimeValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["Code"] = PrimitiveArrayValue<string>.Create(shape, ["A", "B"], elementType: IgwDataType.String),
                ["IsNG"] = PrimitiveArrayValue<short>.Create(shape, [0, 0]),
            });

        var replacement = ArrayValueEditor.ReplaceSingle(value, [2], ["Code"], "ABCDEFG");

        Assert.Equal("ABCD", replacement.GetBoxedValue(0) is Dictionary<string, object?> row ? row["Code"] : null);
        Assert.Equal((short)0, replacement.GetBoxedValue(0) is Dictionary<string, object?> sameRow ? sameRow["IsNG"] : null);
    }

    [Theory]
    [InlineData("Line.Plc.DB.Cells[1,2].IsNG", "Line.Plc.DB.Cells", new int[] { 1, 2 }, "IsNG")]
    [InlineData("Line.Plc.DB.Cells[1][2].Nested.Code", "Line.Plc.DB.Cells", new int[] { 1, 2 }, "Nested.Code")]
    public void Alias_parser_preserves_parent_multidimensional_indices_and_member(
        string path, string parent, int[] indices, string member)
    {
        Assert.True(ArrayAliasResolver.TryParse(path, out var alias));
        Assert.Equal(parent, alias.ParentTagId);
        Assert.Equal(indices, alias.Indices);
        Assert.Equal(member.Split('.'), alias.MemberPath);
    }

    private static RuntimeEngine CreateEngine(RecordingArrayDriver driver)
    {
        var registry = new DriverRegistry();
        registry.Register(new Factory(driver));
        return new RuntimeEngine(Project(), registry, new DataHub(), NullLoggerFactory.Instance);
    }

    private static IgwProject Project() => new()
    {
        Channels =
        [
            new ChannelDef
            {
                Name = "Line",
                DriverId = "array-test",
                Devices =
                [
                    new DeviceDef
                    {
                        Name = "Plc",
                        Groups =
                        [
                            new TagGroupDef
                            {
                                Name = "DB",
                                Tags =
                                [
                                    new TagDef
                                    {
                                        Name = "Values",
                                        Address = "DB1.DBW0",
                                        DataType = IgwDataType.Int16,
                                        ScanRateMs = 30_000,
                                        Access = TagAccess.ReadWrite,
                                        Array = new ArrayDef
                                        {
                                            ElementType = IgwDataType.Int16,
                                            Dimensions = [4],
                                            LowerBounds = [1],
                                            ElementStrideBytes = 2,
                                        },
                                    },
                                ],
                            },
                        ],
                    },
                ],
            },
        ],
    };

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= until) throw new TimeoutException("等待测试条件超时。");
            await Task.Delay(10);
        }
    }

    private sealed class Factory(RecordingArrayDriver driver) : IDriverFactory
    {
        public string DriverId => "array-test";
        public DriverDescriptor Describe() => new() { DriverId = DriverId, DisplayName = DriverId };
        public IDeviceDriver CreateDevice(ChannelDef channel, DeviceDef device) => driver;
    }

    private sealed class RecordingArrayDriver : IDeviceDriver, IArrayCapableDriver
    {
        private readonly short[] _values = [1, 2, 3, 4];
        public int ReadCalls { get; private set; }
        public int WriteCalls { get; private set; }
        public int ArrayWriteCalls { get; private set; }
        public ArrayWriteRequest? LastArrayWrite { get; private set; }
        public Exception? ArrayWriteError { get; init; }
        public int DisconnectCalls { get; private set; }
        public ArrayWriteResult NextWriteResult { get; set; } =
            new(StatusCode.Good, 0, []);
        public int? PersistentMismatchIndex { get; init; }
        public bool BlockFirstArrayWrite { get; init; }
        public TaskCompletionSource<bool> FirstArrayWriteStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseFirstArrayWrite { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<short> CurrentValues => _values;
        public List<int> WrittenElementCounts { get; } = [];
        public List<int> WrittenStarts { get; } = [];
        public string DriverId => "array-test";
        public ConnectionState State { get; private set; }
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser { get; } = new Parser();
        public DriverLimits Limits { get; } = new();
        public ArrayDriverCapabilities ArrayCapabilities { get; } = new(
            NativeArrays: true, StructuredArrays: true, MultiDimensional: true,
            SliceRead: true, SliceWrite: true, AtomicWholeWrite: false,
            MaxElementsPerRequest: 1024, MaxBytesPerRequest: 4096);

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
            return Task.FromResult<IReadOnlyList<TagSample>>(
                tags.Select(tag => new TagSample(tag.TagId, new DataValue(
                    PrimitiveArrayValue<short>.Create(ArrayShape.Create([4], [1]), _values),
                    StatusCode.Good, now, now))).ToArray());
        }

        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct)
        {
            WriteCalls++;
            return Task.FromResult<IReadOnlyList<WriteResult>>([]);
        }

        public async Task<ArrayWriteResult> WriteArrayAsync(ArrayWriteRequest request, CancellationToken ct)
        {
            ArrayWriteCalls++;
            if (BlockFirstArrayWrite && ArrayWriteCalls == 1)
            {
                FirstArrayWriteStarted.TrySetResult(true);
                await ReleaseFirstArrayWrite.Task.WaitAsync(ct);
            }
            LastArrayWrite = request;
            WrittenElementCounts.Add(request.Value.Count);
            WrittenStarts.Add(request.Slice.Start[0]);
            if (ArrayWriteError is not null) throw ArrayWriteError;
            if (NextWriteResult.Status.IsGood && NextWriteResult.Failures.Count == 0)
            {
                var targetShape = ArrayShape.Create([4], [1]);
                for (int i = 0; i < request.Value.Count; i++)
                {
                    int target = checked((int)targetShape.GetLinearOffset(request.Value.Shape.GetIndices(i)));
                    if (PersistentMismatchIndex == target) continue;
                    _values[target] = Convert.ToInt16(request.Value.GetBoxedValue(i));
                }
            }
            return NextWriteResult with
            {
                Succeeded = NextWriteResult.Succeeded == 0 ? request.Value.Count : NextWriteResult.Succeeded,
            };
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Parser : IAddressParser
    {
        public bool TryParse(string address, IgwDataType type, out TagAddress result, out string? error)
        {
            result = new TagAddress(1, 0, 8);
            error = null;
            return true;
        }
    }
}
