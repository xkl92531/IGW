using System.Globalization;

namespace IGW.Abstractions;

/// <summary>驱动当前单个读请求能容纳的点数。共享连接据此把一次大读取拆成多次准入。</summary>
public interface IReadChunkHint
{
    int ReadChunkSize { get; }
    /// <summary>当前读批量、耗时预算与实测开销，供设备状态诊断显示。</summary>
    string ReadWindowSummary => "";

    /// <summary>
    /// 把一次逻辑读取拆成若干“准入单元”，每个单元单独排队下发。默认按读批量切分；
    /// 驱动可以把能一次读回的点(如同一个结构体数组的成员)放进同一个单元。
    /// </summary>
    IReadOnlyList<IReadOnlyList<TagBinding>> PlanReadUnits(IReadOnlyList<TagBinding> tags)
    {
        int chunk = Math.Max(1, ReadChunkSize);
        if (tags.Count <= chunk) return [tags];
        var units = new List<IReadOnlyList<TagBinding>>();
        for (int offset = 0; offset < tags.Count; offset += chunk)
            units.Add(tags.Skip(offset).Take(chunk).ToArray());
        return units;
    }
}

public enum RequestBudgetMode { Auto, Off, Fixed }

/// <summary>
/// 单个物理请求的耗时预算。串行处理请求的 PLC(如 S7-1500 的 OPC UA 服务器)上，一个大请求会让同一 PLC 的
/// 写入和其他读取全部排在它后面，预算限制的就是这段等待。
/// Auto：按实测的每请求固定开销和每点耗时自动推算；Off：只追求吞吐；Fixed：使用配置的毫秒数。
/// </summary>
public readonly record struct RequestBudget(RequestBudgetMode Mode, double FixedMs = 0)
{
    public static RequestBudget Auto => new(RequestBudgetMode.Auto);
    public static RequestBudget Off => new(RequestBudgetMode.Off);

    /// <summary>空或 auto = 自动；0 = 不限制；正数 = 固定毫秒。无法识别时按自动处理。</summary>
    public static RequestBudget Parse(string? text)
    {
        string value = text?.Trim() ?? "";
        if (value.Length == 0 || value.Equals("auto", StringComparison.OrdinalIgnoreCase) || value == "自动") return Auto;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double ms) || !double.IsFinite(ms) || ms < 0)
            return Auto;
        return ms == 0 ? Off : new(RequestBudgetMode.Fixed, ms);
    }

    public override string ToString() => Mode switch
    {
        RequestBudgetMode.Off => "不限",
        RequestBudgetMode.Fixed => $"{FixedMs:0}ms",
        _ => "自动",
    };
}

/// <summary>A bounded, session-local request window trained only by normal completed requests.</summary>
public sealed class AdaptiveRequestWindow
{
    // 自动预算：固定开销在单请求耗时中的占比目标。占比 10% 时预算 = 10 × 固定开销
    // (S7-1500 实测固定开销 15–35 ms，预算约 150–350 ms)，吞吐损失不超过 10%；
    // 缩窗下限取占比 25% 的点数，窗口再小固定开销就会吞掉吞吐。
    private const double AutoBudgetOverheadShare = 0.10;
    private const double MinimumWindowOverheadShare = 0.25;
    private const double AutoBudgetFloorMs = 100;
    private const double AutoBudgetCeilingMs = 2000;
    private const int AbsoluteMinimumWindow = 32;
    // 服务器忙时小请求也会排队，拟合出的固定开销偏大；超过该值按该值计，避免预算被放宽、窗口被压得过小。
    private const double MaxLearnedFixedCostMs = 50;

    private readonly object _sync = new();
    private readonly Func<long> _clock;
    private readonly (int Units, double Ms)[] _recent = new (int, double)[128];
    private int _current, _successes, _prior, _evaluationSamples, _recentCount, _recentNext;
    private long _version, _nextGrowth;
    private double _cost, _baseline, _evaluationCost;
    private double _fixedCostMs = double.NaN, _unitCostMs = double.NaN;

    public AdaptiveRequestWindow(int initial, int ceiling, Func<long>? monotonicMilliseconds = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initial);
        if (ceiling < initial) throw new ArgumentOutOfRangeException(nameof(ceiling));
        _current = initial;
        Ceiling = ceiling;
        _clock = monotonicMilliseconds ?? (() => Environment.TickCount64);
    }
    public int Current { get { lock (_sync) return _current; } }
    public int Ceiling { get; }
    public RequestBudget Budget { get; init; } = RequestBudget.Off;
    public long Version { get { lock (_sync) return _version; } }
    public bool Declared { get; private init; }
    public (int Current, long Version) Capture()
    {
        lock (_sync) return (_current, _version);
    }
    public static AdaptiveRequestWindow Undeclared(int initial, int ceiling) => new(initial, ceiling);
    public static AdaptiveRequestWindow Undeclared(int initial, int ceiling, RequestBudget budget) =>
        new(initial, ceiling) { Budget = budget };
    public static AdaptiveRequestWindow ForDeclaredLimit(int limit) => new(limit, limit) { Declared = true };
    public static AdaptiveRequestWindow ForDeclaredLimit(int limit, RequestBudget budget) =>
        new(limit, limit) { Declared = true, Budget = budget };

    /// <summary>当前生效的单请求耗时预算(毫秒)；0 表示不限制或自动模式尚未学到开销。</summary>
    public double EffectiveBudgetMs { get { lock (_sync) return BudgetMsLocked(); } }

    /// <summary>预算缩窗时的最小点数。</summary>
    public int MinimumWindow { get { lock (_sync) return MinimumWindowLocked(); } }

    /// <summary>实测的每请求固定开销与每点耗时(毫秒)，未学到时为 NaN。</summary>
    public (double FixedMs, double PerUnitMs) Costs { get { lock (_sync) return (_fixedCostMs, _unitCostMs); } }

    public int OnCapacityFailure(int attempted)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attempted);
        lock (_sync)
        {
            SetCurrent(Math.Max(1, Math.Min(_current, attempted / 2)));
            ResetSamples();
            _nextGrowth = _clock() + 60_000;
            return _current;
        }
    }

    public bool OnSuccess(int workUnits, double requestMs) => OnSuccessCore(workUnits, requestMs, null);

    public bool OnSuccess(int workUnits, double requestMs, (int Current, long Version) plan) =>
        OnSuccessCore(workUnits, requestMs, plan);

    private bool OnSuccessCore(int workUnits, double requestMs, (int Current, long Version)? plan)
    {
        if (workUnits <= 0 || !double.IsFinite(requestMs) || requestMs <= 0) return false;
        lock (_sync)
        {
            LearnCosts(workUnits, requestMs);
            if (plan is { } captured && (captured.Version != _version || captured.Current != _current)) return false;
            double budget = BudgetMsLocked();
            int minimum = MinimumWindowLocked();
            if (budget > 0 && requestMs > budget * 1.2 && workUnits > minimum && _current > minimum)
            {
                // 按预算比例缩小，每次最多减半，避免一次抖动把窗口压到底。
                long proportional = (long)(workUnits * budget / requestMs);
                SetCurrent((int)Math.Max(minimum, Math.Max(_current / 2, Math.Min(_current - 1, proportional))));
                ResetSamples();
                _nextGrowth = _clock() + 60_000;
                return true;
            }
            // A short tail does not measure the performance of the grown request size.
            if (_prior > 0 && workUnits != _current) return false;
            double normalized = requestMs / workUnits;
            if (_prior > 0)
            {
                _evaluationCost += normalized;
                if (++_evaluationSamples < 5) return false;
                bool regression = _evaluationCost / 5 > _baseline * 1.2;
                if (regression)
                {
                    SetCurrent(_prior);
                    _nextGrowth = _clock() + 60_000;
                }
                ResetSamples();
                return regression;
            }
            // Keep a bounded aggregate, even during a long cooldown.
            if (_successes < 20) { _cost += normalized; _successes++; }
            else _cost += normalized - _cost / 20;
            if (_successes < 20 || _clock() < _nextGrowth || _current >= Ceiling) return false;
            int next = (int)Math.Min(Ceiling, Math.Max((long)_current + 1, (long)_current + _current / 4));
            // 增长后的请求按实测开销估算会超出预算，就停在当前大小。
            if (budget > 0 && EstimatedMsLocked(next, normalized) > budget) return false;
            _prior = _current;
            _baseline = _cost / _successes;
            SetCurrent(next);
            _evaluationSamples = 0;
            _evaluationCost = 0;
            return true;
        }
    }

    /// <summary>
    /// 用近期请求估算“耗时 = 固定开销 + 点数 × 每点耗时”。按点数的 2 的幂分桶，每桶取最小耗时：
    /// 在串行服务器上排在别的请求后面的样本会偏大，下包络最接近服务器真实的处理开销。
    /// </summary>
    private void LearnCosts(int workUnits, double requestMs)
    {
        _recent[_recentNext] = (workUnits, requestMs);
        _recentNext = (_recentNext + 1) % _recent.Length;
        _recentCount = Math.Min(_recentCount + 1, _recent.Length);
        if (_recentCount < 8) return;

        Span<double> bucketUnits = stackalloc double[32];
        Span<double> bucketMs = stackalloc double[32];
        Span<int> bucketCount = stackalloc int[32];
        bucketMs.Fill(double.MaxValue);
        for (int i = 0; i < _recentCount; i++)
        {
            var (units, ms) = _recent[i];
            int bucket = Math.Min(31, System.Numerics.BitOperations.Log2((uint)units));
            bucketCount[bucket]++;
            if (ms < bucketMs[bucket]) { bucketMs[bucket] = ms; bucketUnits[bucket] = units; }
        }
        int n = 0;
        double sumX = 0, sumY = 0, sumXX = 0, sumXY = 0, minX = double.MaxValue, maxX = 0;
        for (int b = 0; b < 32; b++)
        {
            // 单个样本的区间不可靠(可能恰好排队)。
            if (bucketMs[b] == double.MaxValue || bucketCount[b] < 2) continue;
            double x = bucketUnits[b], y = bucketMs[b];
            n++; sumX += x; sumY += y; sumXX += x * x; sumXY += x * y;
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
        }
        // 至少要有两种相差 4 倍以上、且含 32 点以上的请求大小，才能区分固定开销和每点耗时；
        // 只有 1–3 点的写入拟合出的每点耗时没有意义，不据此限制批量。
        if (n < 2 || maxX < minX * 4 || maxX < 32) return;
        double denominator = n * sumXX - sumX * sumX;
        if (denominator <= 0) return;
        double slope = (n * sumXY - sumX * sumY) / denominator;
        double intercept = (sumY - slope * sumX) / n;
        _unitCostMs = Math.Max(0, slope);
        _fixedCostMs = Math.Clamp(intercept, 0.1, MaxLearnedFixedCostMs);
    }

    private double BudgetMsLocked()
    {
        switch (Budget.Mode)
        {
            case RequestBudgetMode.Off: return 0;
            case RequestBudgetMode.Fixed: return Budget.FixedMs;
        }
        if (double.IsNaN(_fixedCostMs) || double.IsNaN(_unitCostMs)) return 0;
        return Math.Clamp(_fixedCostMs / AutoBudgetOverheadShare, AutoBudgetFloorMs, AutoBudgetCeilingMs);
    }

    private int MinimumWindowLocked()
    {
        if (double.IsNaN(_fixedCostMs) || double.IsNaN(_unitCostMs) || _unitCostMs <= 0) return Math.Min(Ceiling, 64);
        // 固定开销占单请求耗时 10% 时的点数。
        double units = _fixedCostMs * (1 - MinimumWindowOverheadShare) / MinimumWindowOverheadShare / _unitCostMs;
        return (int)Math.Clamp(Math.Ceiling(units), AbsoluteMinimumWindow, Math.Max(AbsoluteMinimumWindow, Ceiling));
    }

    private double EstimatedMsLocked(int units, double normalizedFallback)
    {
        return double.IsNaN(_fixedCostMs) || double.IsNaN(_unitCostMs)
            ? units * normalizedFallback
            : _fixedCostMs + units * _unitCostMs;
    }

    public override string ToString()
    {
        lock (_sync)
        {
            double budget = BudgetMsLocked();
            string costs = double.IsNaN(_fixedCostMs) ? "开销学习中" : $"实测 {_fixedCostMs:0.#}ms+{_unitCostMs:0.##}ms/点";
            return $"批量={_current}点，预算={Budget}{(budget > 0 ? $"({budget:0}ms)" : "")}，{costs}";
        }
    }

    private void SetCurrent(int value) { if (value != _current) { _current = value; _version++; } }
    private void ResetSamples() { _successes = _prior = _evaluationSamples = 0; _cost = _baseline = _evaluationCost = 0; }
}
