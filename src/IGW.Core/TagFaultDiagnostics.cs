using System.Collections.Concurrent;
using IGW.Abstractions;

namespace IGW.Core;

/// <summary>
/// 一个点位当前的故障记录：完整路径、为什么不对、该怎么处理。
/// 质量码只说明「坏」，现场要定位到具体点位、知道改哪里，靠的是 <see cref="Cause"/> 与 <see cref="Suggestion"/>。
/// </summary>
public sealed record TagFault(
    string TagId,
    string Channel,
    string Device,
    string Address,
    string DataType,
    string Quality,
    string QualityHex,
    string Category,
    string Cause,
    string Suggestion,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? RecoveredAt,
    long OccurrenceCount,
    bool Isolated)
{
    public bool Active => RecoveredAt is null;
}

public sealed record TagFaultSnapshot(
    IReadOnlyList<TagFault> Active,
    IReadOnlyList<TagFault> Recovered,
    int TrackedCount,
    int OverflowCount);

/// <summary>按质量码(必要时结合驱动给出的具体原因)推导出中文的「原因 + 怎么做」。</summary>
public static class TagFaultAdvice
{
    public static (string Category, string Cause, string Suggestion) For(
        StatusCode status, TagDef def, string driverId, string? driverReason)
    {
        // 驱动能说清楚的以驱动为准：它知道地址形式、型号、协议通道这些网关看不到的上下文。
        if (!string.IsNullOrWhiteSpace(driverReason))
            return ("AddressNotRoutable", driverReason!,
                "按 PLC 实际型号与程序修正该点位的地址形式：绝对地址（如 DB1.DBW0）走 S7 Classic，" +
                "符号地址（如 DB名.成员）需要 S7-1200/1500；同一台设备内不能混用两种写法，请拆成两台设备。");

        uint code = status.Code;
        return code switch
        {
            0x80340000 => ("SymbolNotFound",
                $"PLC 程序里找不到这个地址：{def.Address}",
                "核对点位地址与 PLC 程序中的名称（大小写、数组下标、结构体层级都要一致）；" +
                "PLC 程序改过就重新导入点表。该点已被隔离，每 30 秒复探一次，不影响同设备其它点位。"),
            0x80070000 => ("DecodeMismatch",
                $"PLC 返回的值无法按配置的数据类型 {def.DataType} 解析：{def.Address}",
                $"核对该点位的数据类型与 PLC 程序中的实际类型(现在配的是 {def.DataType})；" +
                "结构体成员要逐个成员配点，不能整体当标量读。" +
                "重读不会改变结果，该点已停止读取，每 30 秒复探一次；改对类型后下一次复探就会自动恢复。"),
            0x80740000 => ("TypeMismatch",
                $"PLC 侧的实际类型与点位声明的 {def.DataType} 不一致：{def.Address}",
                $"把该点位的数据类型改成 PLC 程序里的实际类型(现在配的是 {def.DataType})；" +
                "点表是从旧版 PLC 程序导的就重新导一次。" +
                "重读不会改变结果，该点已停止读取，每 30 秒复探一次；改对类型后下一次复探就会自动恢复。"),
            0x80050000 => ("CommFailure",
                $"与 PLC 的通信中断，未取到该点位的值：{def.Address}",
                "先看这台设备的连接诊断：网络、PLC 负载、连接数限制都可能导致；连接恢复后该点位会自动恢复。"),
            0x800A0000 => ("Timeout",
                $"读取该点位超时：{def.Address}",
                "大工程的 S7 Plus 符号解析本身较慢，可适当调大设备的请求超时；若只有个别点位超时，检查这些点位是否落在很大的结构体/数组里。"),
            0x80210000 => ("OutOfRange",
                $"地址越界或长度超出 PLC 实际数据区：{def.Address}",
                "核对 DB 长度、数组上下界与字符串长度；导入点表后 PLC 程序又改过的话要重新导入。"),
            0x80180000 or 0x801F0000 => ("PermissionDenied",
                $"PLC 拒绝访问该点位：{def.Address}",
                "检查 PLC 的保护等级与账户权限；受保护的块需要在设备上配置凭据后才能读写。"),
            _ => ("BadQuality",
                $"点位质量异常（{status.Class}，0x{code:X8}）：{def.Address}",
                "先看这台设备的连接诊断确认链路是否正常；链路正常时核对该点位的地址与数据类型。"),
        };
    }
}

/// <summary>
/// 设备级的点位故障跟踪：按点位聚合，记录首次/最近出现、次数与恢复时间。
/// 有上限，超出部分只计数不展开，避免 32 万点的设备在批量坏值时把内存吃光。
/// </summary>
public sealed class TagFaultTracker(string channel, string device, string driverId, int capacity = 500)
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private int _overflow;

    private sealed class Entry
    {
        public required TagDef Def { get; init; }
        public StatusCode Status;
        public string? DriverReason;
        public DateTimeOffset FirstSeenAt;
        public DateTimeOffset LastSeenAt;
        public DateTimeOffset? RecoveredAt;
        public long Count;
        public bool Isolated;
    }

    /// <summary>记录一次坏值。同一点位重复出现只累加次数与时间，不重复分配。</summary>
    public void RecordBad(TagBinding binding, StatusCode status, string? driverReason, DateTimeOffset now, bool isolated = false)
    {
        if (_entries.TryGetValue(binding.TagId, out var existing))
        {
            existing.Status = status;
            existing.DriverReason = driverReason ?? existing.DriverReason;
            existing.LastSeenAt = now;
            existing.RecoveredAt = null;
            existing.Count++;
            existing.Isolated = isolated || existing.Isolated;
            return;
        }
        if (_entries.Count >= capacity)
        {
            Interlocked.Increment(ref _overflow);
            return;
        }
        _entries.TryAdd(binding.TagId, new Entry
        {
            Def = binding.Def,
            Status = status,
            DriverReason = driverReason,
            FirstSeenAt = now,
            LastSeenAt = now,
            Count = 1,
            Isolated = isolated,
        });
    }

    /// <summary>点位恢复成好值：保留记录并标记恢复时间，方便现场回看「刚才是什么问题」。</summary>
    public void RecordGood(string tagId, DateTimeOffset now)
    {
        if (_entries.TryGetValue(tagId, out var entry) && entry.RecoveredAt is null)
            entry.RecoveredAt = now;
    }

    public void Clear() { _entries.Clear(); Interlocked.Exchange(ref _overflow, 0); }

    public TagFaultSnapshot Snapshot()
    {
        var active = new List<TagFault>();
        var recovered = new List<TagFault>();
        foreach (var (tagId, entry) in _entries)
        {
            var (category, cause, suggestion) = TagFaultAdvice.For(entry.Status, entry.Def, driverId, entry.DriverReason);
            var fault = new TagFault(
                tagId, channel, device, entry.Def.Address, entry.Def.DataType.ToString(),
                entry.Status.Class.ToString(), $"0x{entry.Status.Code:X8}",
                category, cause, suggestion,
                entry.FirstSeenAt, entry.LastSeenAt, entry.RecoveredAt, entry.Count, entry.Isolated);
            (fault.Active ? active : recovered).Add(fault);
        }
        active.Sort(static (left, right) => right.LastSeenAt.CompareTo(left.LastSeenAt));
        recovered.Sort(static (left, right) => right.LastSeenAt.CompareTo(left.LastSeenAt));
        return new TagFaultSnapshot(active, recovered, _entries.Count, Volatile.Read(ref _overflow));
    }
}
