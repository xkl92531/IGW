using IGW.Abstractions;

namespace IGW.Northbound.Mes;

/// <summary>
/// 每任务的节点流转事件环形缓冲(固定容量,默认 256):数组复用,追加零堆分配(MesFlowEvent 是结构体)。
/// 写者=任务循环(单写),读者=REST 快照;锁只护住数组下标操作,微秒级,不阻塞任务循环。
/// 内存态:服务重启即清空(界面注明"最近 N 条(自启动)")。
/// </summary>
public sealed class FlowEventBuffer
{
    private readonly MesFlowEvent[] _buf;
    private readonly object _lock = new();
    private int _next;    // 下一个写位
    private int _count;   // 已有条数(≤容量)

    public FlowEventBuffer(int capacity = 256) => _buf = new MesFlowEvent[Math.Max(1, capacity)];

    public int Capacity => _buf.Length;

    public void Add(in MesFlowEvent e)
    {
        lock (_lock)
        {
            _buf[_next] = e;
            _next = (_next + 1) % _buf.Length;
            if (_count < _buf.Length) _count++;
        }
    }

    /// <summary>时间正序导出当前全部事件(最旧→最新)。</summary>
    public IReadOnlyList<MesFlowEvent> Snapshot()
    {
        lock (_lock)
        {
            var r = new MesFlowEvent[_count];
            int start = (_next - _count + _buf.Length) % _buf.Length;
            for (int i = 0; i < _count; i++) r[i] = _buf[(start + i) % _buf.Length];
            return r;
        }
    }
}
