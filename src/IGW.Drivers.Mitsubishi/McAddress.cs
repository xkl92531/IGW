using System.Globalization;
using System.Text.RegularExpressions;
using IGW.Abstractions;

namespace IGW.Drivers.Mitsubishi;

/// <summary>解析后的 MELSEC 设备地址。Code=MC 设备代码;IsBit=位设备;Number=设备号。</summary>
internal readonly record struct McLoc(byte Code, bool IsBit, int Number);

/// <summary>三菱 MC(MELSEC Q/L/iQ-R)地址解析。D/R/ZR/SD 十进制字;W/B/X/Y 十六进制;M/L/SM 十进制位。</summary>
internal static class McAddress
{
    // device -> (MC 设备代码, 是否位设备, 是否按十六进制解析设备号)
    private static readonly Dictionary<string, (byte code, bool bit, bool hex)> Devices = new()
    {
        ["D"] = (0xA8, false, false),  // 数据寄存器
        ["R"] = (0xAF, false, false),  // 文件寄存器
        ["ZR"] = (0xB0, false, false),
        ["SD"] = (0xA9, false, false),
        ["W"] = (0xB4, false, true),   // 链接寄存器(hex)
        ["M"] = (0x90, true, false),   // 内部继电器
        ["L"] = (0x92, true, false),   // 锁存继电器
        ["SM"] = (0x91, true, false),
        ["X"] = (0x9C, true, true),    // 输入(hex)
        ["Y"] = (0x9D, true, true),    // 输出(hex)
        ["B"] = (0xA0, true, true),    // 链接继电器(hex)
    };

    private static readonly Regex Re = new(@"^(ZR|SD|SM|D|R|W|M|L|X|Y|B)([0-9A-F]+)$", RegexOptions.Compiled);

    public static bool TryParse(string address, out McLoc loc, out string? error)
    {
        loc = default;
        error = null;
        var s = (address ?? "").Trim().ToUpperInvariant();
        var m = Re.Match(s);
        if (!m.Success || !Devices.TryGetValue(m.Groups[1].Value, out var dev))
        { error = "MC 地址格式错误(示例:D100 / W1A / R200 / M50 / X10 / Y20 / B1F)"; return false; }

        string numStr = m.Groups[2].Value;
        int num;
        try { num = dev.hex ? int.Parse(numStr, NumberStyles.HexNumber) : int.Parse(numStr); }
        catch { error = "设备号解析失败"; return false; }

        loc = new McLoc(dev.code, dev.bit, num);
        return true;
    }

    /// <summary>该数据类型占的字数(16 位=1 字)。</summary>
    public static int Words(IgwDataType t) => t switch
    {
        IgwDataType.Int32 or IgwDataType.UInt32 or IgwDataType.Float => 2,
        IgwDataType.Int64 or IgwDataType.UInt64 or IgwDataType.Double => 4,
        _ => 1,
    };
}

/// <summary>IAddressParser:校验 MC 地址。</summary>
public sealed class McAddressParser : IAddressParser
{
    public bool TryParse(string address, IgwDataType type, out TagAddress result, out string? error)
    {
        result = new TagAddress(0, 0, 1);
        return McAddress.TryParse(address, out _, out error);
    }
}
