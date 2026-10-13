using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using FluentModbus;

namespace IGW.Runtime;

/// <summary>进程内 Modbus TCP 模拟从站(测试用):周期性更新保持寄存器,模拟一台 PLC。</summary>
public sealed class ModbusSimulator : IDisposable
{
    private readonly ModbusTcpServer _server = new();
    private readonly System.Timers.Timer _timer = new(200);
    private readonly ILogger _log;
    private readonly int _port;
    private int _tick;

    public ModbusSimulator(ILogger log, int port = 1502)
    {
        _log = log;
        _port = port;
        _timer.Elapsed += (_, _) => Tick();
    }

    public void Start()
    {
        _server.Start(new IPEndPoint(IPAddress.Loopback, _port));
        _server.AddUnit(1);
        _timer.Start();
        _log.LogInformation("Modbus 模拟器已启动 127.0.0.1:{port} (unit 1)", _port);
    }

    private void Tick()
    {
        _tick++;
        try
        {
            // 真实 PLC 线上为大端;直接按大端写寄存器字节(规避 Span<short> 的主机字节序)
            var bytes = MemoryMarshal.AsBytes(_server.GetHoldingRegisters(1));
            BinaryPrimitives.WriteInt16BigEndian(bytes[(0 * 2)..], (short)(_tick % 1000));                       // 40001 Ramp
            BinaryPrimitives.WriteInt16BigEndian(bytes[(1 * 2)..], (short)(Math.Sin(_tick / 10.0) * 1000));      // 40002 Sine(raw -1000..1000)
            BinaryPrimitives.WriteSingleBigEndian(bytes[(2 * 2)..], 25.0f + (float)Math.Sin(_tick / 20.0) * 5f); // 40003/40004 Temp(ABCD)
            BinaryPrimitives.WriteInt16BigEndian(bytes[(4 * 2)..], (short)(_tick % 2 == 0 ? 1 : 0));             // 40005.0 Run 位
            // 40010(reg9)= SetPoint,模拟器不写,供写测试回读
        }
        catch (Exception ex) { _log.LogDebug("模拟器 tick 异常:{m}", ex.Message); }
    }

    public void Stop()
    {
        _timer.Stop();
        try { _server.Stop(); } catch { }
    }

    public void Dispose()
    {
        Stop();
        _timer.Dispose();
        (_server as IDisposable)?.Dispose();
    }
}
