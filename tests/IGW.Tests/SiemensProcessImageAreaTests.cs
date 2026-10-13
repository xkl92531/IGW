using IGW.Abstractions;
using IGW.Drivers.IoTClient;
using Xunit;

namespace IGW.Tests;

/// <summary>
/// I/Q/M/V 过程映像区要和 DB 区走同一条块读路径。
///
/// 现场 1.6.85(产线1_PLC2.S7)：1000 个 I/Q 点整批 Good=0、Bad=1000，全天没有一个成功过，
/// 而同一台设备的 DB 区点位一直正常。原因是 SiemensAddressDescriptor 对非 DB 地址不给
/// 字节偏移(DbNumber/ByteOffset 都是 null)，于是这些点位被读取规划器整批过滤掉，
/// 落到逐点路径；逐点读里任何一个点失败并带断 Socket，剩下的点就被 AddUnreadAsBad
/// 直接盖成笼统 Bad——排在最前面的那个点一失败，后面 999 个点连试都没试过。
///
/// IoTClient 本身完全支持按字节块寻址这些区(Q204→TypeCode 130/Begin 1632)，
/// 和 DB 区共用同一个 Read(address, length, isBit) 入口，所以纳入块读只是地址串的差别。
/// </summary>
public sealed class SiemensProcessImageAreaTests
{
    [Theory]
    [InlineData("Q204.2", SiemensAreaCode.Output, 204, 2)]
    [InlineData("I104.1", SiemensAreaCode.Input, 104, 1)]
    [InlineData("M10.0", SiemensAreaCode.Memory, 10, 0)]
    [InlineData("E4057.4", SiemensAreaCode.Input, 4057, 4)]     // 德文 E = 输入
    [InlineData("A1666.7", SiemensAreaCode.Output, 1666, 7)]    // 德文 A = 输出
    [InlineData("QB204", SiemensAreaCode.Output, 204, null)]
    [InlineData("QW100", SiemensAreaCode.Output, 100, null)]
    [InlineData("V50.3", SiemensAreaCode.Variable, 50, 3)]
    public void A_process_image_address_carries_its_area_and_byte_offset(
        string address, int area, int byteOffset, int? bit)
    {
        var descriptor = SiemensAddressDescriptor.Parse(address);

        Assert.Equal(SiemensAddressKind.Classic, descriptor.Kind);
        Assert.Equal(area, descriptor.DbNumber);
        Assert.Equal(byteOffset, descriptor.ByteOffset);
        Assert.Equal(bit, descriptor.BitOffset);
    }

    /// <summary>DB 区的解析一个字都不能变。</summary>
    [Theory]
    [InlineData("DB436.DBX305.0", 436, 305, 0)]
    [InlineData("DB190.DBB12", 190, 12, null)]
    [InlineData("DB410.DBW1856", 410, 1856, null)]
    public void A_db_address_still_parses_exactly_as_before(string address, int db, int offset, int? bit)
    {
        var descriptor = SiemensAddressDescriptor.Parse(address);

        Assert.Equal(db, descriptor.DbNumber);
        Assert.Equal(offset, descriptor.ByteOffset);
        Assert.Equal(bit, descriptor.BitOffset);
    }

    [Theory]
    [InlineData(SiemensAreaCode.Output, 204, "Q204")]
    [InlineData(SiemensAreaCode.Input, 104, "I104")]
    [InlineData(SiemensAreaCode.Memory, 10, "M10")]
    [InlineData(SiemensAreaCode.Variable, 50, "V50")]
    [InlineData(436, 0, "DB436.DBB0")]
    public void The_wire_address_matches_what_the_library_expects(int area, int offset, string expected) =>
        Assert.Equal(expected, SiemensAreaCode.ToAddress(area, offset));

    /// <summary>这是现场故障的直接回归：这些点位必须真的被规划成块读请求。</summary>
    [Fact]
    public void Process_image_tags_are_planned_into_read_ranges()
    {
        var tags = new[] { Bind("Q204.0"), Bind("Q204.1"), Bind("Q204.2"), Bind("I104.1") };

        var ranges = SiemensClassicReadPlanner.Plan(tags, 222, null);

        Assert.NotEmpty(ranges);
        Assert.Equal(tags.Length, ranges.Sum(r => r.Tags.Count));
        // 同一个字节上的三个位只需要一次请求，不该退化成逐点
        var output = Assert.Single(ranges, r => r.DbNumber == SiemensAreaCode.Output);
        Assert.Equal(3, output.Tags.Count);
        Assert.Equal(204, output.Start);
    }

    /// <summary>一个读不了的点位不能再让同批其它点位连请求都发不出去。</summary>
    [Fact]
    public async Task A_failing_point_no_longer_stops_the_rest_of_the_batch()
    {
        var tags = new[] { Bind("Q4055.7"), Bind("Q204.2"), Bind("I104.1") };
        var transport = new RecordingTransport { FailArea = SiemensAreaCode.Output, FailStart = 4055 };

        var samples = await SiemensClassicBlockCodec.ReadAsync(transport, tags, default);

        Assert.Equal(3, samples.Count);
        Assert.False(samples.Single(s => s.TagId.EndsWith("Q4055.7")).Value.IsGood);
        Assert.True(samples.Single(s => s.TagId.EndsWith("Q204.2")).Value.IsGood);
        Assert.True(samples.Single(s => s.TagId.EndsWith("I104.1")).Value.IsGood);
    }

    private static TagBinding Bind(string address)
    {
        var def = new TagDef { Name = address, Address = address, DataType = IgwDataType.Bool };
        var parser = new SiemensScanAddressParser();
        Assert.True(parser.TryParse(def, out var parsed, out string? error), error);
        return new TagBinding { TagId = "Line.Plc." + address, Def = def, Address = parsed };
    }

    private sealed class RecordingTransport : ISiemensByteTransport
    {
        public List<(int Area, int Offset, int Length)> Reads { get; } = [];
        public int? FailArea { get; init; }
        public int? FailStart { get; init; }
        public int MaxPayloadBytes => 222;

        public ValueTask<SiemensByteReadResult> ReadAsync(int area, int offset, int length, CancellationToken ct)
        {
            Reads.Add((area, offset, length));
            if (FailArea == area && FailStart is int bad && offset <= bad && bad < offset + length)
                return ValueTask.FromResult(SiemensByteReadResult.Failed(
                    new StatusCode(0x803C0000), "地址越界"));
            return ValueTask.FromResult(SiemensByteReadResult.Good(new byte[length]));
        }

        public ValueTask<StatusCode> WriteAsync(int area, int offset, ReadOnlyMemory<byte> data, CancellationToken ct) =>
            ValueTask.FromResult(StatusCode.Good);
    }
}
