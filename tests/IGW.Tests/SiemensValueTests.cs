using IGW.Abstractions;
using IGW.Core;
using Xunit;

namespace IGW.Tests;

public class SiemensValueTests
{
    [Theory]
    [InlineData(17, "DTL#2026-09-05-13:40:44.123456789", "2026-09-05 13:40:44.123456789")]
    [InlineData(18, "D#2026-09-05", "2026-09-05")]
    [InlineData(19, "TOD#13:40:44.123", "13:40:44.123")]
    [InlineData(20, "LTOD#13:40:44.123456789", "13:40:44.123456789")]
    [InlineData(21, "T#1h2m3s4ms", "T#3723004ms")]
    [InlineData(22, "LT#1s23ns", "LT#1000000023ns")]
    [InlineData(23, "DT#2026-09-05-13:40:44.123", "2026-09-05 13:40:44.123")]
    [InlineData(24, "LDT#2026-09-05-13:40:44.123456700", "2026-09-05 13:40:44.123456700")]
    [InlineData(25, "S5T#1s", "S5T#1000ms")]
    public void Normalize_siemens_literals_without_losing_precision(int type, string input, string expected)
        => Assert.Equal(expected, PlcWriteValueNormalizer.Normalize(new TagDef { DataType = (IgwDataType)type }, input));

    [Theory]
    [InlineData(17, "2026-02-29 12:00:00.000000000")]
    [InlineData(17, "2262-04-11 23:47:16.854775808")]
    [InlineData(17, "2026-09-05 12:00:00.1234567890")]
    [InlineData(18, "1989-12-31")]
    [InlineData(19, "24:00:00.000")]
    [InlineData(23, "2090-01-01 00:00:00.000")]
    [InlineData(25, "S5T#11ms")]
    public void Reject_invalid_or_unrepresentable_siemens_values(int type, string input)
        => Assert.ThrowsAny<Exception>(() => PlcWriteValueNormalizer.Normalize(new TagDef { DataType = (IgwDataType)type }, input));

    [Fact]
    public void Dtl_known_binary_vectors_use_distinct_endianness_and_keep_nanoseconds()
    {
        const string timestamp = "2026-09-05 13:40:44.123456789";
        byte[] plc = [0x07, 0xea, 9, 5, 7, 13, 40, 44, 0x07, 0x5b, 0xcd, 0x15];
        byte[] ua = [0xea, 0x07, 9, 5, 7, 13, 40, 44, 0x15, 0xcd, 0x5b, 0x07];
        Assert.Equal(timestamp, SiemensValueCodec.DecodeDtl(plc));
        Assert.Equal(timestamp, SiemensValueCodec.DecodeDtl(ua, true));
        Assert.Equal(plc, SiemensValueCodec.EncodeDtl(timestamp));
        Assert.Equal(ua, SiemensValueCodec.EncodeDtl(timestamp, true));
        Assert.ThrowsAny<Exception>(() => SiemensValueCodec.DecodeDtl(new byte[12]));
    }

    [Fact]
    public void Dt_bcd_and_date_unsigned_day_count_cover_the_documented_endpoints()
    {
        byte[] bytes = [0x26, 0x09, 0x05, 0x13, 0x40, 0x44, 0x12, 0x37];
        Assert.Equal("2026-09-05 13:40:44.123", SiemensValueCodec.Normalize(IgwDataType.Dt, bytes));
        Assert.Equal(bytes, SiemensValueCodec.ToWire(IgwDataType.Dt, "2026-09-05 13:40:44.123"));
        Assert.Equal("2169-06-06", SiemensValueCodec.Normalize(IgwDataType.Date, ushort.MaxValue));
        Assert.Equal(ushort.MaxValue, SiemensValueCodec.ToWire(IgwDataType.Date, "2169-06-06"));
        Assert.Equal("2089-12-31 23:59:59.999", SiemensValueCodec.Normalize(IgwDataType.Dt,
            SiemensValueCodec.ToWire(IgwDataType.Dt, "2089-12-31 23:59:59.999")));
    }

    [Fact]
    public void Numeric_and_time_types_write_exact_wire_widths()
    {
        Assert.Equal(sbyte.MinValue, Assert.IsType<sbyte>(SiemensValueCodec.ToWire(IgwDataType.SInt, "-128")));
        Assert.Equal(ulong.MaxValue, Assert.IsType<ulong>(SiemensValueCodec.ToWire(IgwDataType.LWord, "18446744073709551615")));
        Assert.Equal(ushort.MaxValue, Assert.IsType<ushort>(SiemensValueCodec.ToWire(IgwDataType.Word, "W#16#FFFF")));
        Assert.Equal(int.MinValue, Assert.IsType<int>(SiemensValueCodec.ToWire(IgwDataType.Time, "T#-2147483648ms")));
        Assert.Equal(long.MinValue, Assert.IsType<long>(SiemensValueCodec.ToWire(IgwDataType.Ltime, "LT#-9223372036854775808ns")));
        Assert.Equal(86_399_999_999_999UL, Assert.IsType<ulong>(SiemensValueCodec.ToWire(IgwDataType.Ltod, "23:59:59.999999999")));
        Assert.Equal("S5T#9990000ms", SiemensValueCodec.Normalize(IgwDataType.S5Time, (ushort)0x3999));
        Assert.Equal((ushort)0x3999, SiemensValueCodec.ToWire(IgwDataType.S5Time, "S5T#9990000ms"));
        Assert.Throws<FormatException>(() => SiemensValueCodec.Normalize(IgwDataType.SInt, 12.5));
    }

    [Fact]
    public void Character_encodings_are_distinct_from_signed_kep_char()
    {
        Assert.Equal("€", SiemensValueCodec.Normalize(IgwDataType.S7Char, (byte)0x80));
        Assert.Equal((byte)0x80, SiemensValueCodec.ToWire(IgwDataType.S7Char, "€"));
        Assert.Equal("中", SiemensValueCodec.Normalize(IgwDataType.WChar, (ushort)'中'));
        Assert.Equal((ushort)'中', SiemensValueCodec.ToWire(IgwDataType.WChar, "中"));
        Assert.ThrowsAny<Exception>(() => SiemensValueCodec.ToWire(IgwDataType.S7Char, "中"));
        Assert.ThrowsAny<Exception>(() => SiemensValueCodec.ToWire(IgwDataType.WChar, "😀"));
    }

    [Fact]
    public void Ldt_rejects_lossy_ua_writes_and_does_not_apply_local_timezone()
    {
        Assert.Throws<FormatException>(() => SiemensValueCodec.ToWire(IgwDataType.Ldt, "2026-09-05 13:40:44.123456789"));
        var wire = Assert.IsType<DateTime>(SiemensValueCodec.ToWire(IgwDataType.Ldt, "2026-09-05 13:40:44.123456700"));
        Assert.Equal(13, wire.Hour);
        Assert.Equal(DateTimeKind.Utc, wire.Kind);
        Assert.Equal("2026-09-05 13:40:44.123456700", SiemensValueCodec.Normalize(IgwDataType.Ldt, wire));
        Assert.Equal("2026-09-05T05:40:44.1234567Z", SiemensValueCodec.Normalize(IgwDataType.DateTime, "2026-09-05T13:40:44.1234567+08:00"));
    }
}
