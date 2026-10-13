namespace IGW.Northbound.Mes;

/// <summary>
/// P² 分位数在线估计(Jain &amp; Chlamtac 1985):5 个标记 O(1) 内存跟踪单一分位数(默认 P95),无需保存样本。
/// 单写者(任务循环)Add;Value 经 Interlocked 位存取,可被快照线程并发读(不撕裂)。
/// </summary>
public sealed class P2Quantile
{
    /// <summary>判定生效所需的最小样本数(冷启动保护:不足不判卡住)。</summary>
    public const int MinSamples = 20;

    private readonly double _p;
    private readonly double[] _q = new double[5];   // 标记高度
    private readonly int[] _n = new int[5];         // 标记实际位置(1 基)
    private readonly double[] _np = new double[5];  // 标记期望位置
    private readonly double[] _dn = new double[5];  // 期望位置每样本增量
    private int _count;
    private long _valueBits;                        // 当前估计值(双精度位),Interlocked 读写防撕裂

    public P2Quantile(double p = 0.95)
    {
        _p = p;
        _dn[0] = 0; _dn[1] = p / 2; _dn[2] = p; _dn[3] = (1 + p) / 2; _dn[4] = 1;
    }

    public int Count => Volatile.Read(ref _count);

    /// <summary>样本是否足够(≥ MinSamples),不足时估计值不可用于判定。</summary>
    public bool Ready => Count >= MinSamples;

    /// <summary>当前分位数估计(毫秒);样本不足 5 个返回 0。</summary>
    public double Value => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _valueBits));

    public void Add(double x)
    {
        if (_count < 5)
        {
            _q[_count] = x;
            _count++;
            if (_count == 5)
            {
                Array.Sort(_q);
                for (int i = 0; i < 5; i++) { _n[i] = i + 1; _np[i] = 1 + 4 * _dn[i]; }
                Publish();
            }
            else Volatile.Write(ref _count, _count);   // Count 对外可见
            return;
        }

        // 找样本落入的分段 k(标记 k 与 k+1 之间),并更新极值
        int k;
        if (x < _q[0]) { _q[0] = x; k = 0; }
        else if (x >= _q[4]) { _q[4] = x; k = 3; }
        else { k = 3; for (int i = 1; i < 5; i++) if (x < _q[i]) { k = i - 1; break; } }

        for (int i = k + 1; i < 5; i++) _n[i]++;
        for (int i = 0; i < 5; i++) _np[i] += _dn[i];

        // 内部标记按需抛物线(退化线性)调整
        for (int i = 1; i <= 3; i++)
        {
            double d = _np[i] - _n[i];
            if ((d >= 1 && _n[i + 1] - _n[i] > 1) || (d <= -1 && _n[i - 1] - _n[i] < -1))
            {
                int s = Math.Sign(d);
                double qp = Parabolic(i, s);
                _q[i] = qp > _q[i - 1] && qp < _q[i + 1] ? qp : Linear(i, s);
                _n[i] += s;
            }
        }
        _count++;
        Publish();
    }

    private double Parabolic(int i, int s) =>
        _q[i] + s / (double)(_n[i + 1] - _n[i - 1]) *
        ((_n[i] - _n[i - 1] + s) * (_q[i + 1] - _q[i]) / (_n[i + 1] - _n[i]) +
         (_n[i + 1] - _n[i] - s) * (_q[i] - _q[i - 1]) / (_n[i] - _n[i - 1]));

    private double Linear(int i, int s) => _q[i] + s * (_q[i + s] - _q[i]) / (_n[i + s] - _n[i]);

    private void Publish()
    {
        Interlocked.Exchange(ref _valueBits, BitConverter.DoubleToInt64Bits(_q[2]));
        Volatile.Write(ref _count, _count);
    }
}

/// <summary>
/// 卡住判定(纯函数,独立可测):只有 run(执行中停滞)参与判定,阈值 = 手工优先,否则 P95×3(样本未就绪不判)。
/// wait(等触发/等复位)是产线常态——"在等活干"不是异常,永不判卡、不给基线(现场反馈钉死的语义)。
/// </summary>
public static class StuckMath
{
    /// <summary>返回 BaselineMs = 所用基线(手工值或 P95;非 run / 未就绪且无手工 = 0,同时也是"慢于基线"预警的分界)。</summary>
    public static (bool Stuck, long BaselineMs) Evaluate(string state, long ageMs, int manualMs, bool p95Ready, double p95Ms)
    {
        if (state != "run") return (false, 0);
        long baseline = manualMs > 0 ? manualMs : p95Ready ? (long)p95Ms : 0;
        if (baseline <= 0) return (false, baseline);
        long threshold = manualMs > 0 ? manualMs : (long)(p95Ms * 3);
        return (ageMs > threshold, baseline);
    }
}
