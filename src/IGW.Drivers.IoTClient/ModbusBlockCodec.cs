using System.Buffers.Binary;
using IGW.Abstractions;

namespace IGW.Drivers.IoTClient;

/// <summary>多寄存器字序。Modbus 线上每个寄存器为大端;多寄存器类型的字/字节顺序按设备而定。</summary>
public enum WordOrder { ABCD, CDAB, BADC, DCBA }

/// <summary>
/// Modbus 块读切片解码(取自原生驱动 ModbusCodec,已对内置模拟器端到端验证过):
/// 整块原始线上字节 → 按各点偏移/类型/字序切片解码。块读一次取回整段,请求数从 N 点降到 ⌈N/块⌉。
/// </summary>
public static class ModbusBlockCodec
{
    public static WordOrder ParseOrder(string? s) => (s?.Trim().ToUpperInvariant()) switch
    {
        "CDAB" => WordOrder.CDAB,
        "BADC" => WordOrder.BADC,
        "DCBA" => WordOrder.DCBA,
        _ => WordOrder.ABCD,
    };

    /// <summary>在“线上字节序”与“规范大端”之间转换(自反:同一函数双向通用)。</summary>
    public static byte[] ApplyOrder(ReadOnlySpan<byte> src, WordOrder order)
    {
        int len = src.Length;
        var outb = new byte[len];
        bool wordSwap = order is WordOrder.CDAB or WordOrder.DCBA;
        bool byteSwap = order is WordOrder.BADC or WordOrder.DCBA;
        int regs = len / 2;
        for (int k = 0; k < regs; k++)
        {
            int srcReg = wordSwap ? (regs - 1 - k) : k;
            byte hi = src[srcReg * 2];
            byte lo = src[srcReg * 2 + 1];
            if (byteSwap) (hi, lo) = (lo, hi);
            outb[k * 2] = hi;
            outb[k * 2 + 1] = lo;
        }
        return outb;
    }

    /// <summary>从“线上字节”解码一个寄存器区值。wire 长度 = 寄存器数 × 2。</summary>
    public static object Decode(ReadOnlySpan<byte> wire, IgwDataType type, WordOrder order)
    {
        byte[] b = ApplyOrder(wire, order); // 规范大端
        return type switch
        {
            IgwDataType.Int16 => (object)BinaryPrimitives.ReadInt16BigEndian(b),
            IgwDataType.UInt16 => BinaryPrimitives.ReadUInt16BigEndian(b),
            IgwDataType.Int32 => BinaryPrimitives.ReadInt32BigEndian(b),
            IgwDataType.UInt32 => BinaryPrimitives.ReadUInt32BigEndian(b),
            IgwDataType.Int64 => BinaryPrimitives.ReadInt64BigEndian(b),
            IgwDataType.UInt64 => BinaryPrimitives.ReadUInt64BigEndian(b),
            IgwDataType.Float => BinaryPrimitives.ReadSingleBigEndian(b),
            IgwDataType.Double => BinaryPrimitives.ReadDoubleBigEndian(b),
            IgwDataType.Byte => b[^1], // 取低字节
            IgwDataType.Char => (sbyte)b[^1], // 有符号低字节
            IgwDataType.Bcd => (ushort)FromBcd16(BinaryPrimitives.ReadUInt16BigEndian(b)),
            IgwDataType.LBcd => (uint)FromBcd32(BinaryPrimitives.ReadUInt32BigEndian(b)),
            _ => (ushort)0,
        };
    }

    /// <summary>把标量编码为 Modbus 线上字节；与 <see cref="Decode"/> 的字序处理严格互逆。</summary>
    public static byte[] Encode(object value, IgwDataType type, WordOrder order)
    {
        int length = type switch
        {
            IgwDataType.Int32 or IgwDataType.UInt32 or IgwDataType.Float or IgwDataType.LBcd => 4,
            IgwDataType.Int64 or IgwDataType.UInt64 or IgwDataType.Double => 8,
            _ => 2,
        };
        var canonical = new byte[length];
        switch (type)
        {
            case IgwDataType.Int16: BinaryPrimitives.WriteInt16BigEndian(canonical, Convert.ToInt16(value)); break;
            case IgwDataType.UInt16: BinaryPrimitives.WriteUInt16BigEndian(canonical, Convert.ToUInt16(value)); break;
            case IgwDataType.Int32: BinaryPrimitives.WriteInt32BigEndian(canonical, Convert.ToInt32(value)); break;
            case IgwDataType.UInt32: BinaryPrimitives.WriteUInt32BigEndian(canonical, Convert.ToUInt32(value)); break;
            case IgwDataType.Int64: BinaryPrimitives.WriteInt64BigEndian(canonical, Convert.ToInt64(value)); break;
            case IgwDataType.UInt64: BinaryPrimitives.WriteUInt64BigEndian(canonical, Convert.ToUInt64(value)); break;
            case IgwDataType.Float: BinaryPrimitives.WriteSingleBigEndian(canonical, Convert.ToSingle(value)); break;
            case IgwDataType.Double: BinaryPrimitives.WriteDoubleBigEndian(canonical, Convert.ToDouble(value)); break;
            case IgwDataType.Byte: BinaryPrimitives.WriteUInt16BigEndian(canonical, Convert.ToByte(value)); break;
            case IgwDataType.Char: BinaryPrimitives.WriteUInt16BigEndian(canonical, (ushort)(byte)Convert.ToSByte(value)); break;
            case IgwDataType.Bcd: BinaryPrimitives.WriteUInt16BigEndian(canonical, ToBcd16(Convert.ToUInt16(value))); break;
            case IgwDataType.LBcd: BinaryPrimitives.WriteUInt32BigEndian(canonical, ToBcd32(Convert.ToUInt32(value))); break;
            default: BinaryPrimitives.WriteInt16BigEndian(canonical, Convert.ToInt16(value)); break;
        }
        return ApplyOrder(canonical, order);
    }

    // BCD 解码:每 4 位(nibble)一位十进制数
    private static int FromBcd16(ushort r) => ((r >> 12) & 0xF) * 1000 + ((r >> 8) & 0xF) * 100 + ((r >> 4) & 0xF) * 10 + (r & 0xF);
    private static long FromBcd32(uint r) { long o = 0, m = 1; for (int i = 0; i < 8; i++) { o += ((r >> (i * 4)) & 0xF) * m; m *= 10; } return o; }
    private static ushort ToBcd16(ushort value)
    {
        if (value > 9999) throw new ArgumentOutOfRangeException(nameof(value), "BCD16 最大为 9999");
        ushort result = 0;
        for (int shift = 0; shift < 16; shift += 4) { result |= (ushort)((value % 10) << shift); value /= 10; }
        return result;
    }

    private static uint ToBcd32(uint value)
    {
        if (value > 99_999_999) throw new ArgumentOutOfRangeException(nameof(value), "BCD32 最大为 99999999");
        uint result = 0;
        for (int shift = 0; shift < 32; shift += 4) { result |= (value % 10) << shift; value /= 10; }
        return result;
    }
}
