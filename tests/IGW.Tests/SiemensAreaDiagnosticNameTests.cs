using IGW.Drivers.IoTClient;
using Xunit;

namespace IGW.Tests;

/// <summary>
/// 设备诊断要说得出是哪块数据区出的问题。
/// 过程映像区(I/Q/M/V)没有 DB 的概念，原先在诊断里只能笼统说「配置的地址」，
/// 还会一并建议去查「DB 是否存在、是否为优化 DB」——对 I/Q 点位纯属误导。
/// </summary>
public sealed class SiemensAreaDiagnosticNameTests
{
    [Theory]
    [InlineData("DB436.DBX305.0", "DB436")]
    [InlineData("DB190.DBB12", "DB190")]
    [InlineData("Q204.2", "输出区(Q)")]
    [InlineData("A1666.7", "输出区(Q)")]      // 德文助记符
    [InlineData("I104.1", "输入区(I)")]
    [InlineData("E4057.4", "输入区(I)")]
    [InlineData("M10.0", "位存储区(M)")]
    [InlineData("V50.3", "变量区(V)")]
    [InlineData("MW100", "位存储区(M)")]
    public void An_address_reports_the_data_area_it_belongs_to(string address, string expected) =>
        Assert.Equal(expected, SiemensHybridDeviceDriver.DataAreaName(address));

    [Theory]
    [InlineData("SomeSymbol.Member")]
    [InlineData("")]
    public void A_symbolic_or_empty_address_has_no_area_name(string address) =>
        Assert.Equal("", SiemensHybridDeviceDriver.DataAreaName(address));
}
