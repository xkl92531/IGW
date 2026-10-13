using System.Buffers.Binary;
using System.Text;
using IGW.Abstractions;
using IGW.Drivers.IoTClient;
using Xunit;

namespace IGW.Tests;

public sealed partial class SiemensClassicReadPlannerTests
{
    [Fact]
    public async Task Raw_range_cost_regression_restores_payload_only_for_later_batches()
    {
        var window = new AdaptiveRequestWindow(20, 40, () => 0);
        for (int i = 0; i < 20; i++) window.OnSuccess(20, 0.000001);
        Assert.Equal(25, window.Current);
        var transport = new RecordingBytes(40, (_, _, count) => SiemensByteReadResult.Good(new byte[count]));
        var tags = Enumerable.Range(0, 25).Select(i => Binding($"DB1.DBB{i}", IgwDataType.Byte)).ToArray();
        for (int i = 0; i < 5; i++)
            Assert.Equal(25, (await SiemensClassicBlockCodec.ReadAsync(transport, tags, default, window: window)).Count);
        Assert.Equal(20, window.Current);
        Assert.Equal(Enumerable.Repeat(25, 5), transport.Reads.Select(r => r.Count));
        await SiemensClassicBlockCodec.ReadAsync(transport, tags, default, window: window);
        Assert.Equal(new[] {20, 5}, transport.Reads.Skip(5).Select(r => r.Count));
    }

    [Fact]
    public async Task Capacity_downshift_does_not_learn_retried_ranges_or_transport_failures()
    {
        long clock = 0;
        var window = new AdaptiveRequestWindow(40, 40, () => clock);
        var transport = new RecordingBytes(40, (_, _, count) => count > 20
            ? SiemensByteReadResult.Failed(StatusCode.BadDeviceFailure, "PDU size limit")
            : SiemensByteReadResult.Good(new byte[count]));
        var tags = Enumerable.Range(0, 40).Select(i => Binding($"DB1.DBB{i}", IgwDataType.Byte)).ToArray();
        Assert.All(await SiemensClassicBlockCodec.ReadAsync(transport, tags, default, window: window), s => Assert.True(s.Value.IsGood));
        Assert.Equal(20, window.Current);
        clock = 60_000;
        for (int i = 0; i < 19; i++) Assert.False(window.OnSuccess(20, 1));
        Assert.True(window.OnSuccess(20, 1));
        var failed = new RecordingBytes(40, (_, _, _) => SiemensByteReadResult.Failed(StatusCode.BadTimeout, "socket timeout"));
        long version = window.Version;
        await Assert.ThrowsAsync<IOException>(() => SiemensClassicBlockCodec.ReadAsync(failed, tags, default, window: window));
        Assert.Equal(version, window.Version);
    }

    [Fact]
    public async Task Capacity_failure_splits_a_single_wide_field_without_losing_its_sample()
    {
        var window = new AdaptiveRequestWindow(8, 8);
        var transport = new RecordingBytes(8, (_, _, count) => count > 4
            ? SiemensByteReadResult.Failed(StatusCode.BadDeviceFailure, "PDU size limit")
            : SiemensByteReadResult.Good(new byte[count]));
        var samples = await SiemensClassicBlockCodec.ReadAsync(transport,
            [Binding("DB1.DBB0", IgwDataType.UInt64)], default, window: window);
        Assert.True(Assert.Single(samples).Value.IsGood);
        Assert.Equal(new[] { 8, 4, 4 }, transport.Reads.Select(r => r.Count));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Oversized_known_field_recovers_below_initial_payload_without_training_recovery(bool isArray)
    {
        long clock = 0;
        var window = new AdaptiveRequestWindow(40, 40, () => clock);
        var bytes = new byte[100];
        bytes[0] = 98; bytes[1] = 3;
        Encoding.ASCII.GetBytes("ABC").CopyTo(bytes, 2);
        var transport = new RecordingBytes(40, (_, start, count) => count > 20
            ? SiemensByteReadResult.Failed(StatusCode.BadDeviceFailure, "PDU size limit")
            : SiemensByteReadResult.Good(bytes.AsSpan(start - 12, count).ToArray()));
        var tag = isArray
            ? Binding("DB1.DBB12", IgwDataType.Byte, array: new ArrayDef {
                ElementType = IgwDataType.Byte, ElementStrideBytes = 1, Dimensions = [100], LowerBounds = [0] })
            : Binding("DB1.DBB12", IgwDataType.String, 98);

        var sample = Assert.Single(await SiemensClassicBlockCodec.ReadAsync(transport, [tag], default, window: window));

        Assert.True(sample.Value.IsGood);
        if (isArray) Assert.Equal(100, Assert.IsAssignableFrom<ArrayRuntimeValue>(sample.Value.Value).Count);
        else Assert.Equal("ABC", sample.Value.Value);
        Assert.Equal(new[] { 40, 20, 20, 20, 20, 20 }, transport.Reads.Select(r => r.Count));
        Assert.All(transport.Reads, r => Assert.InRange(r.Offset + r.Count, 13, 112));
        Assert.Equal(20, window.Current);
        clock = 60_000;
        for (int i = 0; i < 19; i++) Assert.False(window.OnSuccess(20, 1));
        Assert.True(window.OnSuccess(20, 1));
    }

    [Fact]
    public async Task Growth_mid_batch_waits_for_actual_grown_requests_before_evaluating_regression()
    {
        var window = new AdaptiveRequestWindow(20, 40, () => 0);
        for (int i = 0; i < 19; i++) window.OnSuccess(20, 1);
        var transport = new RecordingBytes(40, (_, _, count) =>
        {
            if (count == 25) Thread.Sleep(10);
            return SiemensByteReadResult.Good(new byte[count]);
        });
        var oldTags = Enumerable.Range(0, 120).Select(i => Binding($"DB1.DBB{i}", IgwDataType.Byte)).ToArray();
        await SiemensClassicBlockCodec.ReadAsync(transport, oldTags, default, window: window);
        Assert.Equal(25, window.Current);
        Assert.Equal(Enumerable.Repeat(20, 6), transport.Reads.Select(r => r.Count));

        var grownTags = Enumerable.Range(0, 125).Select(i => Binding($"DB1.DBB{i}", IgwDataType.Byte)).ToArray();
        await SiemensClassicBlockCodec.ReadAsync(transport, grownTags, default, window: window);
        Assert.Equal(Enumerable.Repeat(25, 5), transport.Reads.Skip(6).Select(r => r.Count));
        Assert.Equal(20, window.Current);
    }

    [Theory]
    [InlineData(IgwDataType.String, 50, 52)]
    [InlineData(IgwDataType.String, 100, 102)]
    [InlineData(IgwDataType.WString, 50, 104)]
    public void Definition_parser_accounts_for_string_headers(IgwDataType type, int capacity, int expected)
    {
        var parser = Assert.IsAssignableFrom<ITagDefinitionAddressParser>(new SiemensScanAddressParser());
        Assert.True(parser.TryParse(new TagDef { Address = "DB2100.DBB6", DataType = type, StringLength = capacity },
            out var address, out var error), error);
        Assert.Equal(expected, address.Length);
    }

    [Fact]
    public void Plc3_layout_preserves_all_72_points_in_five_bounded_ranges()
    {
        var tags = Plc3();
        Assert.Equal(52, tags.Single(t => t.Def.Address == "DB2100.DBB6").Address.Length);
        Assert.Equal(102, tags.Single(t => t.Def.Address == "DB2100.DBB66").Address.Length);
        var ranges = SiemensClassicReadPlanner.Plan(tags, 880);
        Assert.Equal(5, ranges.Count);
        Assert.All(ranges, r => Assert.InRange(r.Count, 1, 880));
        Assert.Equal(72, ranges.SelectMany(r => r.Tags).DistinctBy(t => t.TagId).Count());
        Assert.Equal((2100, 0, 864), (ranges[0].DbNumber, ranges[0].Start, ranges[0].Count));
        Assert.Equal((2101, 0, 106), (ranges[4].DbNumber, ranges[4].Start, ranges[4].Count));
    }

    [Fact]
    public void Planner_merges_overlap_and_small_gaps_but_never_crosses_db_or_payload()
    {
        TagBinding[] tags = [Binding("DB2.DBW0", IgwDataType.UInt16), Binding("DB1.DBW42", IgwDataType.UInt16),
            Binding("DB1.DBW24", IgwDataType.UInt16), Binding("DB1.DBW4", IgwDataType.UInt16),
            Binding("DB1.DBX4.3", IgwDataType.Bool), Binding("DB1.DBW8", IgwDataType.UInt16)];
        var ranges = SiemensClassicReadPlanner.Plan(tags, 64);
        Assert.Equal(new[] { (1, 4, 6, 2), (1, 24, 2, 0), (1, 42, 2, 0), (2, 0, 2, 0) },
            ranges.Select(r => (r.DbNumber, r.Start, r.Count, r.GapBytes)).ToArray());
        Assert.Equal(ranges.Select(r => (r.DbNumber, r.Start, r.Count)),
            SiemensClassicReadPlanner.Plan(tags.Reverse().ToArray(), 64).Select(r => (r.DbNumber, r.Start, r.Count)));
    }

    [Fact]
    public void Planner_excludes_symbolic_unproven_arrays_and_oversized_members()
    {
        var unknownArray = Binding("DB1.DBW0", IgwDataType.Int16, array: new ArrayDef
            { ElementType = IgwDataType.Int16, Dimensions = [4], LowerBounds = [0] });
        Assert.Empty(SiemensClassicReadPlanner.Plan([unknownArray,
            Binding("PLC.DB1.Name", IgwDataType.String, 10), Binding("DB1.DBB20", IgwDataType.String, 254)], 200));
    }

    [Theory]
    [MemberData(nameof(ScalarCases))]
    public void Block_decodes_configured_scalar_at_nonzero_response_offset(IgwDataType type, byte[] bytes, object expected)
    {
        var tag = Binding("DB1.DBB24", type);
        if (type == IgwDataType.Bool) tag = Binding("DB1.DBX24.3", type);
        var image = new byte[4 + bytes.Length];
        bytes.CopyTo(image, 4);
        var sample = Assert.Single(SiemensClassicBlockCodec.Decode(new(1, 20, image.Length, 0, [tag]), image, DateTime.UtcNow));
        Assert.True(sample.Value.IsGood);
        Assert.Equal(expected, sample.Value.Value);
    }

    public static TheoryData<IgwDataType, byte[], object> ScalarCases => new()
    {
        { IgwDataType.Bool, [8], true }, { IgwDataType.Byte, [250], (byte)250 },
        { IgwDataType.Char, [254], (sbyte)-2 }, { IgwDataType.Int16, [255, 254], (short)-2 },
        { IgwDataType.UInt16, [255, 254], (ushort)65534 },
        { IgwDataType.Int32, [255, 255, 255, 254], -2 },
        { IgwDataType.UInt32, [255, 255, 255, 254], 4294967294u },
        { IgwDataType.Int64, [255, 255, 255, 255, 255, 255, 255, 254], -2L },
        { IgwDataType.UInt64, [255, 255, 255, 255, 255, 255, 255, 254], 18446744073709551614UL },
        { IgwDataType.Float, [63, 192, 0, 0], 1.5f },
        { IgwDataType.Double, [63, 248, 0, 0, 0, 0, 0, 0], 1.5d },
    };

    [Fact]
    public void Block_decodes_strings_and_marks_only_malformed_or_short_members_bad()
    {
        TagBinding[] tags = [Binding("DB1.DBW20", IgwDataType.UInt16),
            Binding("DB1.DBB22", IgwDataType.String, 3), Binding("DB1.DBB28", IgwDataType.WString, 2),
            Binding("DB1.DBW36", IgwDataType.Int16)];
        byte[] image = [0, 7, 3, 2, 65, 66, 0, 0, 0, 2, 0, 2, 0, 65, 0x4f, 0x60];
        var range = new SiemensClassicReadRange(1, 20, 18, 1, tags);
        var values = SiemensClassicBlockCodec.Decode(range, image, DateTime.UtcNow);
        Assert.Equal((ushort)7, values[0].Value.Value);
        Assert.Equal("AB", values[1].Value.Value);
        Assert.Equal("A你", values[2].Value.Value);
        Assert.False(values[3].Value.IsGood);
        image[3] = 4;
        image[11] = 3;
        values = SiemensClassicBlockCodec.Decode(range, image, DateTime.UtcNow);
        Assert.True(values[0].Value.IsGood);
        Assert.All(values.Skip(1), v => Assert.False(v.Value.IsGood));
    }

    [Fact]
    public async Task One_response_decodes_many_points_and_failed_range_splits_only_its_members()
    {
        TagBinding[] tags = [Binding("DB1.DBW0", IgwDataType.UInt16), Binding("DB1.DBW2", IgwDataType.UInt16),
            Binding("DB2.DBW0", IgwDataType.UInt16)];
        var transport = new RecordingBytes(200, (db, offset, count) =>
            db == 1 && count > 2 ? SiemensByteReadResult.Failed(StatusCode.Bad, "range rejected") :
            SiemensByteReadResult.Good([0, (byte)(db * 10 + offset)]));
        var samples = await SiemensClassicBlockCodec.ReadAsync(transport, tags, default);
        Assert.Equal(new[] { (1, 0, 4), (1, 0, 2), (1, 2, 2), (2, 0, 2) }, transport.Reads);
        Assert.Equal(new object[] { (ushort)10, (ushort)12, (ushort)20 }, samples.Select(s => s.Value.Value));
        Assert.All(samples, s => Assert.True(s.Value.IsGood));
    }

    [Fact]
    public async Task Plc3_read_uses_five_responses_for_72_values()
    {
        var tags = Plc3();
        var transport = new RecordingBytes(880, (_, _, count) => SiemensByteReadResult.Good(new byte[count]));
        var samples = await SiemensClassicBlockCodec.ReadAsync(transport, tags, default);
        Assert.Equal(5, transport.Reads.Count);
        Assert.Equal(72, samples.Count);
        Assert.All(samples, s => Assert.True(s.Value.IsGood));
    }

    [Fact]
    public async Task Oversized_string_uses_only_bounded_configured_point_image()
    {
        byte[] bytes = new byte[256];
        bytes[0] = 254; bytes[1] = 3;
        Encoding.ASCII.GetBytes("ABC").CopyTo(bytes, 2);
        var transport = new RecordingBytes(200, (_, start, count) =>
            SiemensByteReadResult.Good(bytes.AsSpan(start - 20, count).ToArray()));
        var samples = await SiemensClassicBlockCodec.ReadAsync(transport, [Binding("DB1.DBB20", IgwDataType.String, 254)], default);
        Assert.Equal(new[] { (1, 20, 200), (1, 220, 56) }, transport.Reads);
        Assert.Equal("ABC", Assert.Single(samples).Value.Value);
    }

    [Fact]
    public async Task Disconnection_aborts_without_range_fallback_and_priority_yields_between_requests()
    {
        var failed = new RecordingBytes(200, (_, _, _) => throw new IOException("连接断开"));
        TagBinding[] tags = [Binding("DB1.DBW0", IgwDataType.UInt16), Binding("DB2.DBW0", IgwDataType.UInt16)];
        await Assert.ThrowsAsync<IOException>(() => SiemensClassicBlockCodec.ReadAsync(failed, tags, default));
        Assert.Single(failed.Reads);
        var good = new RecordingBytes(200, (_, _, count) => SiemensByteReadResult.Good(new byte[count]));
        await Assert.ThrowsAsync<DriverReadYieldException>(() => SiemensClassicBlockCodec.ReadAsync(good, tags, default,
            () => good.Reads.Count > 0));
        Assert.Single(good.Reads);
    }

    [Fact]
    public async Task Network_failure_response_is_not_retried_as_a_capacity_failure()
    {
        var transport = new RecordingBytes(200, (_, _, _) => SiemensByteReadResult.Failed(StatusCode.BadTimeout, "socket timeout"));
        TagBinding[] tags = [Binding("DB1.DBW0", IgwDataType.UInt16), Binding("DB1.DBW2", IgwDataType.UInt16)];
        await Assert.ThrowsAsync<IOException>(() => SiemensClassicBlockCodec.ReadAsync(transport, tags, default));
        Assert.Single(transport.Reads);
    }

    [Fact]
    public async Task Unreadable_single_member_does_not_poison_good_neighbours_or_other_db()
    {
        var transport = new RecordingBytes(200, (db, offset, count) => db == 1 && (count > 2 || offset == 2)
            ? SiemensByteReadResult.Failed(StatusCode.Bad, "item not available")
            : SiemensByteReadResult.Good([0, 7]));
        TagBinding[] tags = [Binding("DB1.DBW0", IgwDataType.UInt16), Binding("DB1.DBW2", IgwDataType.UInt16),
            Binding("DB2.DBW0", IgwDataType.UInt16)];
        var samples = await SiemensClassicBlockCodec.ReadAsync(transport, tags, default);
        Assert.True(samples[0].Value.IsGood);
        Assert.False(samples[1].Value.IsGood);
        Assert.True(samples[2].Value.IsGood);
        Assert.Equal(4, transport.Reads.Count);
    }

    internal static TagBinding Binding(string address, IgwDataType type, int? capacity = null, ArrayDef? array = null)
    {
        var def = new TagDef { Address = address, DataType = type, StringLength = capacity, Array = array };
        var parser = (ITagDefinitionAddressParser)new SiemensScanAddressParser();
        Assert.True(parser.TryParse(def, out var parsed, out var error), error);
        return new TagBinding { TagId = address + ":" + type, Def = def, Address = parsed };
    }

    private static List<TagBinding> Plc3()
    {
        List<TagBinding> tags = [Binding("DB2100.DBX0.0", IgwDataType.Bool),
            Binding("DB2100.DBW2", IgwDataType.UInt16), Binding("DB2100.DBW4", IgwDataType.UInt16)];
        for (int i = 0; i < 11; i++)
        {
            int offset = 6 + i * 266;
            tags.AddRange([Binding($"DB2100.DBB{offset}", IgwDataType.String, 50),
                Binding($"DB2100.DBD{offset + 52}", IgwDataType.Float), Binding($"DB2100.DBD{offset + 56}", IgwDataType.Float),
                Binding($"DB2100.DBB{offset + 60}", IgwDataType.String, 100),
                Binding($"DB2100.DBW{offset + 162}", IgwDataType.UInt16), Binding($"DB2100.DBB{offset + 164}", IgwDataType.String, 100)]);
        }
        tags.AddRange([Binding("DB2101.DBW0", IgwDataType.UInt16), Binding("DB2101.DBW2", IgwDataType.UInt16),
            Binding("DB2101.DBB4", IgwDataType.String, 100)]);
        return tags;
    }

    private sealed class RecordingBytes(int payload, Func<int, int, int, SiemensByteReadResult> read) : ISiemensByteTransport
    {
        public int MaxPayloadBytes => payload;
        public List<(int Db, int Offset, int Count)> Reads { get; } = [];
        public ValueTask<SiemensByteReadResult> ReadAsync(int db, int offset, int count, CancellationToken ct)
        {
            Reads.Add((db, offset, count));
            return ValueTask.FromResult(read(db, offset, count));
        }
        public ValueTask<StatusCode> WriteAsync(int db, int offset, ReadOnlyMemory<byte> data, CancellationToken ct) => throw new NotSupportedException();
    }
}
