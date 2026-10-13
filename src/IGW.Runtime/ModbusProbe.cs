using System.Net;
using FluentModbus;

namespace IGW.Runtime;

/// <summary>诊断:直接连模拟器,dump 原始寄存器字节 + FluentModbus 泛型解释,定位字节序。</summary>
public static class ModbusProbe
{
    public static async Task RunAsync()
    {
        var c = new ModbusTcpClient { ReadTimeout = 2000, ConnectTimeout = 2000 };
        c.Connect(new IPEndPoint(IPAddress.Loopback, 1502), ModbusEndianness.BigEndian);
        var raw = await c.ReadHoldingRegistersAsync((byte)1, (ushort)0, (ushort)11);
        Console.WriteLine("RAW bytes 0..10: " + BitConverter.ToString(raw.ToArray()));
        var shorts = (await c.ReadHoldingRegistersAsync<short>(1, 0, 11)).ToArray();
        Console.WriteLine("short[]      : " + string.Join(",", shorts));
        var floats = (await c.ReadHoldingRegistersAsync<float>(1, 2, 1)).ToArray();
        Console.WriteLine("float@reg2   : " + string.Join(",", floats));
        c.Disconnect();
    }
}
