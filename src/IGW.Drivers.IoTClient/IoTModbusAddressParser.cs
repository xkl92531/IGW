using IGW.Abstractions;

namespace IGW.Drivers.IoTClient;

/// <summary>
/// Modbus 地址解析(供 IoTClient 驱动用):区号(首位)+ 寄存器/线圈号。
/// 0xxxx 线圈 / 1xxxx 离散输入 / 3xxxx 输入寄存器 / 4xxxx 保持寄存器;寄存器位 40001.3。
/// TagAddress.Area=0/1/3/4;Offset=0 基寄存器/线圈号;BitOffset=寄存器位(无则 -1)。
/// 解析出的 Offset 直接作为 IoTClient 的地址串,Area→功能码由驱动映射。
/// </summary>
public sealed class IoTModbusAddressParser : IAddressParser
{
    private readonly bool _zeroBased;
    public IoTModbusAddressParser(bool zeroBased = false) => _zeroBased = zeroBased;

    public bool TryParse(string address, IgwDataType type, out TagAddress result, out string? error)
    {
        result = default; error = null;
        if (string.IsNullOrWhiteSpace(address)) { error = "空地址"; return false; }

        var s = address.Trim();
        int bit = -1;
        int dot = s.IndexOf('.');
        if (dot >= 0)
        {
            if (!int.TryParse(s.AsSpan(dot + 1), out bit) || bit < 0) { error = "位偏移非法"; return false; }
            s = s[..dot];
        }
        int colon = s.IndexOf(':');
        int area;
        int offset;
        if (colon >= 0)
        {
            area = s[..colon].ToUpperInvariant() switch { "C" => 0, "DI" => 1, "IR" => 3, "HR" => 4, _ => -1 };
            var number = s[(colon + 1)..];
            if (area < 0 || number.Length == 0 || !number.All(char.IsAsciiDigit) || !int.TryParse(number, out offset))
            { error = "地址格式应为 C:0 / DI:0 / IR:0 / HR:0"; return false; }
        }
        else
        {
            if (s.Length < 2 || !s.All(char.IsAsciiDigit)) { error = "地址格式应为区号+寄存器号,如 40001"; return false; }
            char areaCh = s[0];
            int minNum = _zeroBased ? 0 : 1;
            if (!int.TryParse(s.AsSpan(1), out int num1) || num1 < minNum)
            { error = _zeroBased ? "寄存器/线圈号应 ≥ 0(零基址)" : "寄存器/线圈号应 ≥ 1"; return false; }
            offset = _zeroBased ? num1 : num1 - 1;

            area = areaCh switch { '0' => 0, '1' => 1, '3' => 3, '4' => 4, _ => -1 };
            if (area < 0) { error = $"未知区号 {areaCh}(支持 0/1/3/4)"; return false; }
        }
        if (offset is < 0 or > 65535) { error = "Modbus 偏移范围 0-65535"; return false; }
        if (bit >= 0 && (area is 0 or 1 || type != IgwDataType.Bool))
        { error = "位后缀仅适用于寄存器 Bool 点位"; return false; }

        bool isBitArea = area is 0 or 1;
        if (isBitArea)
        {
            if (type != IgwDataType.Bool) { error = "线圈/离散输入只能映射 Bool"; return false; }
            result = new TagAddress(area, offset, 1, 0);
            return true;
        }
        if (type == IgwDataType.Bool)
        {
            if (bit < 0) { error = "寄存器位访问需指定位,如 40001.3"; return false; }
            if (bit > 15) { error = "位偏移范围 0-15"; return false; }
            result = new TagAddress(area, offset, 1, bit);
            return true;
        }
        int regs = type switch
        {
            IgwDataType.Int16 or IgwDataType.UInt16 or IgwDataType.Byte or IgwDataType.Char or IgwDataType.Bcd => 1,
            IgwDataType.Int32 or IgwDataType.UInt32 or IgwDataType.Float or IgwDataType.LBcd => 2,
            IgwDataType.Int64 or IgwDataType.UInt64 or IgwDataType.Double => 4,
            _ => 0,
        };
        if (regs == 0) { error = $"Modbus 暂不支持数据类型 {type}"; return false; }
        if ((long)offset + regs > 65536) { error = "数据类型跨度超出 Modbus 地址范围"; return false; }
        result = new TagAddress(area, offset, regs, -1);
        return true;
    }
}
