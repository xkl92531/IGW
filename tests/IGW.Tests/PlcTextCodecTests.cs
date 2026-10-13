using System.Text;
using IGW.Abstractions;
using IGW.Drivers.IoTClient;
using Xunit;

namespace IGW.Tests;

public sealed class PlcTextCodecTests
{
    private static readonly Encoding Gb18030;

    static PlcTextCodecTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Gb18030 = Encoding.GetEncoding(54936);
    }

    [Fact]
    public void Auto_detects_utf8_and_reuses_it_for_write()
    {
        var codec = new PlcTextCodec();
        const string value = "设备异常";

        Assert.Equal(value, codec.Decode("tag-utf8", Encoding.UTF8.GetBytes(value), null));
        Assert.Equal(PlcTextEncoding.Utf8, codec.ResolvedEncoding("tag-utf8", null));
        Assert.Equal(Encoding.UTF8.GetBytes(value), codec.Encode("tag-utf8", value, null, 64));
    }

    [Fact]
    public void Auto_detects_gb18030_and_reuses_it_for_write()
    {
        var codec = new PlcTextCodec();
        const string value = "设备异常";

        Assert.Equal(value, codec.Decode("tag-gb", Gb18030.GetBytes(value), null));
        Assert.Equal(PlcTextEncoding.Gb18030, codec.ResolvedEncoding("tag-gb", null));
        Assert.Equal(Gb18030.GetBytes(value), codec.Encode("tag-gb", value, null, 64));
    }

    [Fact]
    public void Ascii_read_does_not_lock_auto_mode_to_utf8()
    {
        var codec = new PlcTextCodec();

        Assert.Equal("READY", codec.Decode("tag", Encoding.ASCII.GetBytes("READY"), null));
        Assert.Equal("中文", codec.Decode("tag", Gb18030.GetBytes("中文"), null));
        Assert.Equal(PlcTextEncoding.Gb18030, codec.ResolvedEncoding("tag", null));
    }

    [Theory]
    [InlineData(PlcTextEncoding.Utf8)]
    [InlineData(PlcTextEncoding.Gb18030)]
    public void Explicit_mode_overrides_auto_detection(PlcTextEncoding mode)
    {
        var codec = new PlcTextCodec();
        const string value = "中文";
        var expected = mode == PlcTextEncoding.Gb18030 ? Gb18030 : Encoding.UTF8;

        Assert.Equal(value, codec.Decode("tag", expected.GetBytes(value), mode));
        Assert.Equal(expected.GetBytes(value), codec.Encode("tag", value, mode, 32));
    }

    [Fact]
    public void Encode_truncates_without_splitting_gb18030_character()
    {
        var codec = new PlcTextCodec();

        byte[] encoded = codec.Encode("tag", "中文A", PlcTextEncoding.Gb18030, 3);

        Assert.Equal("中", Gb18030.GetString(encoded));
        Assert.Equal(2, encoded.Length);
    }
}
