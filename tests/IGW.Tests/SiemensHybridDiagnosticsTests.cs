using IGW.Abstractions;
using IGW.Core;
using IGW.Drivers.IoTClient;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class SiemensHybridDiagnosticsTests
{
    [Fact]
    public void Factory_rejects_mixed_effective_device_paths_before_creating_transports()
    {
        var device = new DeviceDef { Name = "Plc", Endpoint = "127.0.0.1:102",
            Tags = [new TagDef { Name = "Absolute", Address = "DB1.DBW0" }],
            Groups = [new TagGroupDef { Name = "Process", Tags = [new TagDef { Name = "Symbol", Address = "PLC.Process.Value" }] }] };
        var error = Assert.Throws<InvalidDataException>(() => new IoTSiemensDriverFactory().CreateDevice(new ChannelDef { Name = "Line" }, device));
        Assert.Contains("Line.Plc.Absolute", error.Message);
        Assert.Contains("Line.Plc.Process.Symbol", error.Message);
        Assert.Contains("DB1.DBW0", error.Message);
        Assert.Contains("PLC.Process.Value", error.Message);
    }

    [Fact]
    public async Task Driver_rejects_cross_batch_protocol_mix_with_exact_paths()
    {
        var classic = new RecoveringClassic(false);
        var plus = new WriteTimeoutPlus();
        await using var driver = new SiemensHybridDeviceDriver("plc", 102, TimeSpan.FromSeconds(1), true, classic, plus);
        await driver.ReadAsync([Binding("Line.Plc.Absolute", "DB1.DBW0")], default);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => driver.ReadAsync([Binding("Line.Plc.Symbol", "PLC.Value")], default));
        Assert.Contains("Line.Plc.Absolute", error.Message);
        Assert.Contains("Line.Plc.Symbol", error.Message);
        Assert.False(plus.IsConnected);
    }

    [Fact]
    public async Task Reset_does_not_create_a_new_session_when_old_transport_disposal_fails()
    {
        int created = 0;
        var plus = new WriteTimeoutPlus { FailDispose = true };
        var driver = new SiemensHybridDeviceDriver("plc", 102, TimeSpan.FromSeconds(1), true,
            new RecoveringClassic(false), plus,
            classicFactory: () => new RecoveringClassic(false),
            plusFactory: () => { created++; return new UnusedPlus(); });
        await driver.ConnectAsync(default);
        await Assert.ThrowsAsync<IOException>(() => driver.ResetConnectionAsync(default));
        Assert.Equal(0, created);
    }

    [Theory]
    [InlineData(240, 222)]
    [InlineData(479, 461)]
    [InlineData(480, 462)]
    [InlineData(959, 941)]
    [InlineData(960, 942)]
    public async Task Factory_derives_payload_from_pdu_and_exposes_internal_partitioning(int pdu, int expected)
    {
        var factory = new IoTSiemensDriverFactory();
        var channel = new ChannelDef { Settings = new() { ["pdu"] = pdu.ToString() } };
        var device = new DeviceDef { Endpoint = "127.0.0.1:102", Settings = new() { ["model"] = "S7-300" } };
        await using var driver = factory.CreateDevice(channel, device);
        var capabilities = ((IDriverCapabilityProvider)driver).Capabilities;
        Assert.True(capabilities.PartitionsReadRequests);
        Assert.Equal(expected, capabilities.MaxReadBytes);
        Assert.Equal(expected, ((IArrayCapableDriver)driver).ArrayCapabilities.MaxBytesPerRequest);
    }

    [Fact]
    public async Task Device_pdu_overrides_channel_pdu()
    {
        await using var driver = new IoTSiemensDriverFactory().CreateDevice(
            new ChannelDef { Settings = new() { ["pdu"] = "240" } },
            new DeviceDef { Endpoint = "127.0.0.1:102", Settings = new() { ["model"] = "S7-300", ["pdu"] = "960" } });
        Assert.Equal(942, ((IDriverCapabilityProvider)driver).Capabilities.MaxReadBytes);
    }

    [Fact]
    public async Task Engine_passes_the_complete_tag_definition_to_a_definition_aware_parser()
    {
        await using var hub = new DataHub();
        var driver = new DefinitionAwareRecordingDriver();
        var registry = new DriverRegistry();
        registry.Register(new DefinitionAwareFactory(driver));
        var project = new IgwProject
        {
            Channels =
            [
                new ChannelDef
                {
                    Name = "Line",
                    DriverId = DefinitionAwareFactory.Id,
                    Devices =
                    [
                        new DeviceDef
                        {
                            Name = "Plc",
                            Tags =
                            [
                                new TagDef
                                {
                                    Name = "Description",
                                    Address = "PLC1.Description",
                                    DataType = IgwDataType.String,
                                    StringLength = 42,
                                },
                            ],
                        },
                    ],
                },
            ],
        };
        await using var engine = new RuntimeEngine(project, registry, hub, NullLoggerFactory.Instance);

        await engine.StartAsync();
        engine.Touch("Line.Plc.Description");
        await driver.ReadObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(42, driver.ParsedStringLength);
        Assert.Equal(42, driver.ReadAddress.Length);
        Assert.Equal(777, driver.ReadAddress.Area);
    }

    [Fact]
    public void Scan_parser_keeps_different_dbs_in_different_read_areas()
    {
        var parser = new SiemensScanAddressParser();

        Assert.True(parser.TryParse("DB2100.DBW0", IgwDataType.Int16, out var db2100a, out _));
        Assert.True(parser.TryParse("DB2100.DBD20", IgwDataType.Float, out var db2100b, out _));
        Assert.True(parser.TryParse("DB2101.DBW0", IgwDataType.Int16, out var db2101, out _));
        Assert.True(parser.TryParse("PLC1.DB2102_Data.Cell_0.Value", IgwDataType.Float, out var symbolic2102, out _));

        Assert.Equal(db2100a.Area, db2100b.Area);
        Assert.NotEqual(db2100a.Area, db2101.Area);
        Assert.NotEqual(db2101.Area, symbolic2102.Area);
        Assert.Equal(0, db2100a.Offset);
        Assert.Equal(20, db2100b.Offset);
    }

    [Fact]
    public async Task Classic_bad_points_report_the_affected_db_instead_of_online()
    {
        var classic = new ClassicWithBadRead();
        var driver = new SiemensHybridDeviceDriver("192.168.1.2", 102, TimeSpan.FromSeconds(2), false,
            classic, new UnusedPlus());
        await driver.ConnectAsync(default);
        var def = new TagDef { Name = "Value", Address = "DB2100.DBW0", DataType = IgwDataType.Int16 };
        await driver.ReadAsync([new TagBinding { TagId = "Line.Plc.DB2100.Value", Def = def, Address = new TagAddress(2100, 0, 2) }], default);

        var detail = driver.ConnectionDetail();
        Assert.Equal("DbAddressMismatch", detail.Code);
        Assert.Contains("DB2100", detail.Message);
        Assert.Contains("item not available", detail.Message);
    }

    [Fact]
    public async Task Classic_partial_bad_points_keep_good_points_usable_and_report_partial_online()
    {
        var classic = new ClassicWithBadRead();
        var driver = new SiemensHybridDeviceDriver("192.168.1.2", 102, TimeSpan.FromSeconds(2), false,
            classic, new UnusedPlus());
        await driver.ConnectAsync(default);
        var tags = new[]
        {
            Binding("Line.Plc.DB2100.Good", "DB2100.DBW0"),
            Binding("Line.Plc.DB2100.Bad", "DB2100.DBW2"),
        };
        classic.GoodTagId = tags[0].TagId;

        var samples = await driver.ReadAsync(tags, default);

        Assert.True(samples.Single(item => item.TagId == tags[0].TagId).Value.IsGood);
        Assert.False(samples.Single(item => item.TagId == tags[1].TagId).Value.IsGood);
        var detail = driver.ConnectionDetail();
        Assert.Equal("PartialOnline", detail.Code);
        Assert.Contains("1/2", detail.Message);
    }

    [Fact]
    public async Task Transport_failure_is_surfaced_and_hard_reset_recreates_the_protocol_sessions()
    {
        int classicCreated = 0;
        int plusCreated = 0;
        ISiemensClassicTransport CreateClassic() =>
            new RecoveringClassic(++classicCreated == 1);
        ISiemensPlusTransport CreatePlus()
        {
            plusCreated++;
            return new UnusedPlus();
        }

        var driver = new SiemensHybridDeviceDriver("192.168.1.2", 102, TimeSpan.FromSeconds(2), false,
            CreateClassic(), CreatePlus(), classicFactory: CreateClassic, plusFactory: CreatePlus);
        await driver.ConnectAsync(default);
        var tag = Binding("Line.Plc.DB2100.Value", "DB2100.DBW0");

        await Assert.ThrowsAsync<IOException>(() => driver.ReadAsync([tag], default));

        await driver.ResetConnectionAsync(default);
        await driver.ConnectAsync(default);
        var recovered = await driver.ReadAsync([tag], default);

        Assert.Equal(2, classicCreated);
        Assert.Equal(2, plusCreated);
        Assert.True(Assert.Single(recovered).Value.IsGood);
    }

    [Fact]
    public async Task Plus_failure_is_disconnected_before_classic_fallback()
    {
        var classic = new RecoveringClassic(false);
        var plus = new FailingPlus();
        var driver = new SiemensHybridDeviceDriver("192.168.1.2", 102, TimeSpan.FromSeconds(2), true,
            classic, plus);

        await driver.ConnectAsync(default);

        Assert.Equal(1, plus.DisconnectCalls);
        Assert.True(classic.IsConnected);
        Assert.Equal(ConnectionState.Connected, driver.State);
    }

    [Fact]
    public async Task Plus_write_timeout_keeps_the_existing_session_connected()
    {
        var classic = new RecoveringClassic(false);
        var plus = new WriteTimeoutPlus();
        var driver = new SiemensHybridDeviceDriver("192.168.1.2", 102, TimeSpan.FromSeconds(2), true,
            classic, plus);
        await driver.ConnectAsync(default);
        var definition = new TagDef
        {
            Name = "Flag", Address = "PLC.DB2100.Flag", DataType = IgwDataType.Int16,
            Access = TagAccess.ReadWrite,
        };

        var results = await driver.WriteAsync([new WriteRequest
        {
            TagId = "Line.PLC.DB2100.Flag",
            Def = definition,
            Address = new TagAddress(2100, 0, 2),
            Value = (short)1,
        }], default);

        Assert.Equal(StatusCode.BadTimeout, Assert.Single(results).Status);
        Assert.Equal(0, plus.DisconnectCalls);
        Assert.True(plus.IsConnected);
        Assert.Equal(ConnectionState.Connected, driver.State);
    }

    [Fact]
    public async Task Classic_write_timeout_keeps_the_existing_session_connected()
    {
        var classic = new WriteTimeoutClassic();
        var driver = new SiemensHybridDeviceDriver("192.168.1.2", 102, TimeSpan.FromSeconds(2), false,
            classic, new UnusedPlus());
        await driver.ConnectAsync(default);
        var definition = new TagDef
        {
            Name = "Flag", Address = "DB2100.DBW0", DataType = IgwDataType.Int16,
            Access = TagAccess.ReadWrite,
        };

        var results = await driver.WriteAsync([new WriteRequest
        {
            TagId = "Line.PLC.DB2100.Flag", Def = definition,
            Address = new TagAddress(2100, 0, 2), Value = (short)1,
        }], default);

        Assert.Equal(StatusCode.BadTimeout, Assert.Single(results).Status);
        Assert.Equal(0, classic.DisconnectCalls);
        Assert.True(classic.IsConnected);
        Assert.Equal(ConnectionState.Connected, driver.State);
    }

    [Fact]
    public async Task Classic_read_timeout_keeps_the_existing_session_connected()
    {
        var classic = new ReadTimeoutClassic();
        var driver = new SiemensHybridDeviceDriver("192.168.1.2", 102, TimeSpan.FromSeconds(2), false,
            classic, new UnusedPlus());
        await driver.ConnectAsync(default);

        var samples = await driver.ReadAsync(
            [Binding("Line.PLC.DB2100.Flag", "DB2100.DBW0")], default);

        Assert.Equal(StatusCode.BadTimeout, Assert.Single(samples).Value.Quality);
        Assert.Equal(0, classic.DisconnectCalls);
        Assert.True(classic.IsConnected);
        Assert.Equal(ConnectionState.Connected, driver.State);
    }

    [Fact]
    public async Task Plus_read_timeout_keeps_the_existing_session_connected()
    {
        var plus = new ReadTimeoutPlus();
        var driver = new SiemensHybridDeviceDriver("192.168.1.2", 102, TimeSpan.FromSeconds(2), true,
            new RecoveringClassic(false), plus);
        await driver.ConnectAsync(default);

        var samples = await driver.ReadAsync(
            [Binding("Line.PLC.DB2100.Flag", "PLC.DB2100.Flag")], default);

        Assert.Equal(StatusCode.BadTimeout, Assert.Single(samples).Value.Quality);
        Assert.Equal(0, plus.DisconnectCalls);
        Assert.True(plus.IsConnected);
        Assert.Equal(ConnectionState.Connected, driver.State);
    }

    private static TagBinding Binding(string id, string address) => new()
    {
        TagId = id,
        Def = new TagDef { Name = id.Split('.').Last(), Address = address, DataType = IgwDataType.Int16 },
        Address = new TagAddress(2100, 0, 2),
    };

    private sealed class ClassicWithBadRead : ISiemensClassicTransport
    {
        public string? GoodTagId { get; set; }
        public string LastError => "item not available";
        public bool IsConnected { get; private set; }
        public Task ConnectAsync(CancellationToken ct) { IsConnected = true; return Task.CompletedTask; }
        public Task DisconnectAsync() { IsConnected = false; return Task.CompletedTask; }
        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TagSample>>(tags.Select(t => new TagSample(t.TagId,
                string.Equals(t.TagId, GoodTagId, StringComparison.Ordinal)
                    ? DataValue.GoodAt((short)7, DateTime.UtcNow)
                    : DataValue.BadAt(StatusCode.Bad, DateTime.UtcNow))).ToArray());
        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) => Task.FromResult<IReadOnlyList<WriteResult>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecoveringClassic(bool failReads) : ISiemensClassicTransport
    {
        public bool IsConnected { get; private set; }
        public string LastError => failReads ? "socket connection reset" : "";
        public Task ConnectAsync(CancellationToken ct) { IsConnected = true; return Task.CompletedTask; }
        public Task DisconnectAsync() { IsConnected = false; return Task.CompletedTask; }
        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
        {
            if (failReads) throw new IOException("socket connection reset");
            return Task.FromResult<IReadOnlyList<TagSample>>(tags.Select(tag =>
                new TagSample(tag.TagId, DataValue.GoodAt((short)9, DateTime.UtcNow))).ToArray());
        }
        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WriteResult>>([]);
        public ValueTask DisposeAsync() { IsConnected = false; return ValueTask.CompletedTask; }
    }

    private sealed class UnusedPlus : ISiemensPlusTransport
    {
        public bool IsConnected => false;
        public Task ConnectAsync(string host, ushort port, TimeSpan timeout, bool legacy, CancellationToken ct) => throw new NotSupportedException();
        public Task AuthenticateAsync(string username, string password, CancellationToken ct) => throw new NotSupportedException();
        public Task ProbeAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SiemensPlusNativeBrowseTag>> BrowseAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) => throw new NotSupportedException();
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailingPlus : ISiemensPlusTransport
    {
        public int DisconnectCalls { get; private set; }
        public bool IsConnected => false;
        public Task ConnectAsync(string host, ushort port, TimeSpan timeout, bool legacy, CancellationToken ct) =>
            throw new SiemensPlusException(SiemensPlusResultCode.TransportFailure);
        public Task DisconnectAsync() { DisconnectCalls++; return Task.CompletedTask; }
        public Task AuthenticateAsync(string username, string password, CancellationToken ct) => throw new NotSupportedException();
        public Task ProbeAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SiemensPlusNativeBrowseTag>> BrowseAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class WriteTimeoutPlus : ISiemensPlusTransport
    {
        public bool FailDispose;
        public int DisconnectCalls { get; private set; }
        public bool IsConnected { get; private set; }
        public Task ConnectAsync(string host, ushort port, TimeSpan timeout, bool legacy, CancellationToken ct)
        {
            IsConnected = true;
            return Task.CompletedTask;
        }
        public Task AuthenticateAsync(string username, string password, CancellationToken ct) => Task.CompletedTask;
        public Task ProbeAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<SiemensPlusNativeBrowseTag>> BrowseAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SiemensPlusNativeBrowseTag>>([]);
        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TagSample>>([]);
        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) =>
            throw new SiemensPlusException(SiemensPlusResultCode.Timeout);
        public Task DisconnectAsync()
        {
            DisconnectCalls++;
            IsConnected = false;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => FailDispose ? throw new IOException("dispose failed") : ValueTask.CompletedTask;
    }

    private sealed class DefinitionAwareFactory(DefinitionAwareRecordingDriver driver) : IDriverFactory
    {
        public const string Id = "DefinitionAware";
        public string DriverId => Id;
        public DriverDescriptor Describe() => new() { DriverId = Id, DisplayName = Id };
        public IDeviceDriver CreateDevice(ChannelDef channel, DeviceDef device) => driver;
    }

    private sealed class DefinitionAwareRecordingDriver : IDeviceDriver
    {
        public string DriverId => DefinitionAwareFactory.Id;
        public ConnectionState State { get; private set; }
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser { get; } = new DefinitionAwareParser();
        public DriverLimits Limits { get; } = new();
        public TaskCompletionSource ReadObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int? ParsedStringLength => ((DefinitionAwareParser)AddressParser).ParsedStringLength;
        public TagAddress ReadAddress { get; private set; }

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
            ReadAddress = Assert.Single(tags).Address;
            ReadObserved.TrySetResult();
            return Task.FromResult<IReadOnlyList<TagSample>>(
                [new TagSample(tags[0].TagId, DataValue.GoodAt("ok", DateTime.UtcNow))]);
        }

        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WriteResult>>([]);

        public async ValueTask DisposeAsync() => await DisconnectAsync();
    }

    private sealed class DefinitionAwareParser : ITagDefinitionAddressParser
    {
        public int? ParsedStringLength { get; private set; }

        public bool TryParse(TagDef definition, out TagAddress result, out string? error)
        {
            ParsedStringLength = definition.StringLength;
            result = new TagAddress(777, 0, definition.StringLength ?? 0);
            error = null;
            return true;
        }

        public bool TryParse(string address, IgwDataType type, out TagAddress result, out string? error)
        {
            result = default;
            error = "Definition-aware parser must receive TagDef.";
            return false;
        }
    }

    private sealed class WriteTimeoutClassic : ISiemensClassicTransport
    {
        public int DisconnectCalls { get; private set; }
        public bool IsConnected { get; private set; }
        public string LastError => "write timeout";
        public Task ConnectAsync(CancellationToken ct)
        {
            IsConnected = true;
            return Task.CompletedTask;
        }
        public Task DisconnectAsync()
        {
            DisconnectCalls++;
            IsConnected = false;
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TagSample>>([]);
        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) =>
            throw new TimeoutException("classic write timed out");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ReadTimeoutClassic : ISiemensClassicTransport
    {
        public int DisconnectCalls { get; private set; }
        public bool IsConnected { get; private set; }
        public string LastError => "read timeout";
        public Task ConnectAsync(CancellationToken ct)
        {
            IsConnected = true;
            return Task.CompletedTask;
        }
        public Task DisconnectAsync()
        {
            DisconnectCalls++;
            IsConnected = false;
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct) =>
            throw new TimeoutException("classic read timed out");
        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WriteResult>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ReadTimeoutPlus : ISiemensPlusTransport
    {
        public int DisconnectCalls { get; private set; }
        public bool IsConnected { get; private set; }
        public Task ConnectAsync(string host, ushort port, TimeSpan timeout, bool legacy, CancellationToken ct)
        {
            IsConnected = true;
            return Task.CompletedTask;
        }
        public Task AuthenticateAsync(string username, string password, CancellationToken ct) => Task.CompletedTask;
        public Task ProbeAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<SiemensPlusNativeBrowseTag>> BrowseAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SiemensPlusNativeBrowseTag>>([]);
        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct) =>
            throw new SiemensPlusException(SiemensPlusResultCode.Timeout);
        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WriteResult>>([]);
        public Task DisconnectAsync()
        {
            DisconnectCalls++;
            IsConnected = false;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
