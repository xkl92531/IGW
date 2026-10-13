using System.Collections.Concurrent;
using IGW.Abstractions;

namespace IGW.Drivers.Simulator;

/// <summary>
/// Simulator 设备驱动:无真实连接,就地按点位「类型 + 名字」生成生动仿真值。
/// 导入真实 DB 后给通道设「模拟模式」即可直接出数据,无需外部模拟器、无需导两次。
/// 数值=斜坡/正弦/随机(按名字哈希分配,各点不同);Bool=翻转;String=NK+流水号;WString=中文报警。
/// 关键字地址仍生效:RAMP/SINE/RANDOM/BOOL/RUN;R&lt;n&gt; 为可读写用户寄存器;写任意点会粘滞(写后读回)。
/// </summary>
public sealed class SimulatorDeviceDriver : IDeviceDriver, IArrayCapableDriver, IDriverCapabilityProvider
{
    private static readonly string[] ZhMsgs = { "正常运行", "缺料报警", "卡膜故障", "温度过高", "气压不足", "急停触发", "封口异常", "计数偏差" };
    private readonly ConcurrentDictionary<string, double> _numOverride = new();   // 写过的数值(粘滞)/ R<n> 用户寄存器
    private readonly ConcurrentDictionary<string, string> _strOverride = new();   // 写过的字符串(粘滞)
    private readonly Random _rng = new();
    private ConnectionState _state = ConnectionState.Disconnected;
    private readonly double _speed;       // 变化速度倍率(>1 更快,<1 更慢)
    private readonly int _delayMs;        // 读取延迟 ms(模拟慢 PLC/高延迟网络现场)
    private readonly Dictionary<string, ArrayRuntimeValue> _arrayValues = new(StringComparer.Ordinal);
    private readonly object _arrayGate = new();

    public SimulatorDeviceDriver(double speed = 1.0, int delayMs = 0)
    { _speed = speed <= 0 ? 1.0 : speed; _delayMs = Math.Clamp(delayMs, 0, 60000); }

    public string DriverId => SimulatorDriverFactory.Id;
    public IAddressParser AddressParser { get; } = new SimulatorAddressParser();
    public DriverLimits Limits { get; } = new DriverLimits { MaxReadUnits = 1000, MaxGap = 0, MaxTagsPerRead = 4096, MaxTagsPerWrite = 4096 };
    public DriverCapabilitySnapshot Capabilities => DriverCapabilitySnapshot.FromLimits(Limits) with
    {
        MaxInFlight = 4,
        PartitionsReadRequests = true,
    };
    public ConnectionState State => _state;
    public event Action<ConnectionState>? StateChanged;
    public ArrayDriverCapabilities ArrayCapabilities { get; } = new(
        NativeArrays: true, StructuredArrays: true, MultiDimensional: true,
        SliceRead: true, SliceWrite: true, AtomicWholeWrite: true,
        MaxElementsPerRequest: int.MaxValue, MaxBytesPerRequest: int.MaxValue);

    private void SetState(ConnectionState s) { if (_state == s) return; _state = s; StateChanged?.Invoke(s); }

    public Task ConnectAsync(CancellationToken ct) { SetState(ConnectionState.Connected); return Task.CompletedTask; }
    public Task DisconnectAsync() { SetState(ConnectionState.Disconnected); return Task.CompletedTask; }

    public async Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
    {
        if (_delayMs > 0) await Task.Delay(_delayMs, ct);   // 模拟现场读取延迟
        var now = DateTime.UtcNow;
        var samples = new List<TagSample>(tags.Count);
        foreach (var t in tags)
        {
            object value;
            if (t.Def.Array is null)
            {
                value = Generate(t.Def, now);
            }
            else
            {
                lock (_arrayGate)
                {
                    if (!_arrayValues.TryGetValue(t.TagId, out var array))
                        _arrayValues[t.TagId] = array = SimulatorArrayCodec.Create(t.Def);
                    value = array;
                }
            }
            samples.Add(new TagSample(t.TagId, DataValue.GoodAt(value, now)));
        }
        return samples;
    }

    private object Generate(TagDef def, DateTime now)
    {
        string addr = (def.Address ?? "").Trim();
        string A = addr.ToUpperInvariant();
        double secs = now.TimeOfDay.TotalSeconds * _speed;   // 变化速度倍率
        int seed = StableHash(string.IsNullOrEmpty(def.Name) ? addr : def.Name);

        // 写过的值优先(可写、粘滞)
        if (_strOverride.TryGetValue(addr, out var sov)) return sov;
        if (_numOverride.TryGetValue(addr, out var nov)) return Coerce(nov, def.DataType);

        // 字符串 / 宽字符串
        if (def.DataType == IgwDataType.String) return "NK" + (((long)secs + seed) % 1000000).ToString("D6");
        if (def.DataType == IgwDataType.WString) return ZhMsgs[(int)(((long)(secs / 3) + seed) % ZhMsgs.Length)];

        // 布尔(或 BOOL/RUN 关键字地址)
        if (def.DataType == IgwDataType.Bool || A.StartsWith("BOOL") || A.StartsWith("RUN"))
            return ((int)(secs / 2) + seed) % 2 == 0;

        // R<n> 用户寄存器:未写时为 0(写后走上面的粘滞)
        if (A.Length > 0 && A[0] == 'R' && !A.StartsWith("RANDOM") && !A.StartsWith("RAMP") && !A.StartsWith("REAL"))
            return Coerce(0, def.DataType);

        // 数值:关键字优先,否则按名字哈希分配 斜坡/正弦/随机(各点不同)
        double d;
        if (A.StartsWith("SINE")) d = 100 * Math.Sin(secs * 2 * Math.PI / 10);
        else if (A.StartsWith("RANDOM")) d = _rng.Next(0, 1001);
        else if (A.StartsWith("RAMP")) d = (int)(secs % 100) * 10;
        else d = (seed % 3) switch
        {
            0 => ((long)(secs * 5) + seed % 50) % 1000,            // 斜坡 0..999
            1 => 50 + 45 * Math.Sin(secs * 0.3 + seed % 10),       // 正弦 ~5..95
            _ => _rng.Next(0, 1000),                               // 随机 0..999
        };
        return Coerce(d, def.DataType);
    }

    private static int StableHash(string s)
    {
        int h = 17;
        foreach (char c in s) h = h * 31 + c;
        return h & 0x7FFFFFFF;
    }

    private static object Coerce(double d, IgwDataType type) => type switch
    {
        IgwDataType.Bool => Math.Abs(d) >= 0.5,   // 写过的 Bool 粘滞值:原来掉进默认分支返回 Int16,北向类型不匹配
        IgwDataType.Float => (float)d,
        IgwDataType.Double => d,
        IgwDataType.Byte => (byte)((uint)Math.Abs(d) & 0xFF),
        IgwDataType.Char => (sbyte)(int)d,
        IgwDataType.UInt16 => (ushort)((uint)Math.Abs(d) & 0xFFFF),
        IgwDataType.Int16 => (short)(int)d,
        IgwDataType.UInt32 => (uint)Math.Abs(d),
        IgwDataType.Int32 => (int)d,
        IgwDataType.Int64 => (long)d,
        IgwDataType.UInt64 => (ulong)Math.Abs(d),
        IgwDataType.String or IgwDataType.WString => d.ToString("0"),
        _ => (short)(int)d,
    };

    public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct)
    {
        var results = new List<WriteResult>(writes.Count);
        foreach (var w in writes)
        {
            try
            {
                string addr = (w.Def.Address ?? "").Trim();
                if (w.Def.DataType is IgwDataType.String or IgwDataType.WString)
                    _strOverride[addr] = Convert.ToString(w.Value) ?? "";
                else
                    _numOverride[addr] = Convert.ToDouble(w.Value);
                results.Add(new WriteResult { TagId = w.TagId, Status = StatusCode.Good });
            }
            catch { results.Add(new WriteResult { TagId = w.TagId, Status = StatusCode.Bad }); }
        }
        return Task.FromResult<IReadOnlyList<WriteResult>>(results);
    }

    public Task<ArrayWriteResult> WriteArrayAsync(ArrayWriteRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            lock (_arrayGate)
            {
                if (!_arrayValues.TryGetValue(request.Binding.TagId, out var whole))
                    whole = SimulatorArrayCodec.Create(request.Binding.Def);
                _arrayValues[request.Binding.TagId] =
                    SimulatorArrayCodec.ReplaceSlice(whole, request.Slice, request.Value);
            }
            return Task.FromResult(new ArrayWriteResult(StatusCode.Good, request.Value.Count, []));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Task.FromResult(ArrayWriteResult.Failed(StatusCode.BadDecodeError, ex.Message));
        }
    }

    public ValueTask DisposeAsync() { SetState(ConnectionState.Disconnected); return ValueTask.CompletedTask; }
}
