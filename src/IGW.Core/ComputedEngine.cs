using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using IGW.Abstractions;
using Microsoft.Extensions.Logging;

namespace IGW.Core;

/// <summary>
/// 计算 / 派生标记(对标 KEP「Advanced Tags」)。表达式用 {通道.设备.组.点} 引用输入;
/// 任一输入变化即重算(System.Data 表达式求值),结果写回 DataHub(变化才写,天然断环)。
/// </summary>
public sealed class ComputedEngine : IDisposable
{
    private readonly DataHub _hub;
    private readonly ILogger _log;
    private readonly IDisposable? _sub;
    private readonly List<Computed> _computed = new();
    private readonly Dictionary<string, List<Computed>> _inputMap = new();
    private static readonly Regex RefRx = new(@"\{([^}]+)\}", RegexOptions.Compiled);

    private sealed class Computed
    {
        public required ComputedTagDef Def;
        public required string[] Inputs;
        public readonly Queue<double> Window = new();   // MovingAverage 样本窗
    }

    public ComputedEngine(DataHub hub, IReadOnlyList<ComputedTagDef> defs, ILogger log)
    {
        _hub = hub; _log = log;
        foreach (var d in defs)
        {
            if (string.IsNullOrWhiteSpace(d.Name) || string.IsNullOrWhiteSpace(d.Expression)) continue;
            var inputs = new List<string>();
            foreach (Match m in RefRx.Matches(d.Expression))
            {
                var id = m.Groups[1].Value.Trim();
                if (id.Length > 0 && !inputs.Contains(id)) inputs.Add(id);
            }
            var c = new Computed { Def = d, Inputs = inputs.ToArray() };
            _computed.Add(c);
            _hub.Register(d.Name, new TagDef
            {
                Name = d.Name, Address = d.Expression, DataType = d.DataType,
                Access = TagAccess.Read, Description = d.Description, EngUnit = d.EngUnit,
            });
            foreach (var inp in c.Inputs)
            {
                if (!_inputMap.TryGetValue(inp, out var list)) _inputMap[inp] = list = new();
                list.Add(c);
            }
        }
        if (_computed.Count == 0) return;
        foreach (var c in _computed) Recompute(c);   // 初值:先算完再订阅——否则订阅 pump 与本线程会同时写同一计算点,违反 seqlock 单写者前提
        // 订阅倒转:只按表达式输入点注册;输入不存在 → Add=false(与旧"回调过滤永不命中"一致)。链式计算的输入(其他计算点)已在上方注册,可解析。
        var sub = hub.SubscribePoints(OnChange, "computed");
        foreach (var inp in _inputMap.Keys) sub.Add(inp);
        _sub = sub;
        _log.LogInformation("计算标记已启用:{n} 个", _computed.Count);
    }

    public IReadOnlyList<string> ComputedTagIds => _computed.Select(c => c.Def.Name).ToList();

    private ValueTask OnChange(TagChange c)
    {
        if (_inputMap.TryGetValue(c.TagId, out var deps))
            foreach (var comp in deps) Recompute(comp);
        return ValueTask.CompletedTask;
    }

    private void Recompute(Computed c)
    {
        var ts = DateTime.UtcNow;
        // 收集输入当前值(任一缺失/坏 → 结果坏)
        var vals = new List<double>(c.Inputs.Length);
        foreach (var inp in c.Inputs)
        {
            if (_hub.TryGet(inp, out var dv) && dv.IsGood && TryNum(dv.Value, out double num)) vals.Add(num);
            else { WriteBadIfChanged(c.Def.Name, ts); return; }
        }

        double? r;
        switch (c.Def.Operation)
        {
            case "Average": r = vals.Count > 0 ? vals.Average() : null; break;
            case "Sum": r = vals.Sum(); break;
            case "Max": r = vals.Count > 0 ? vals.Max() : (double?)null; break;
            case "Min": r = vals.Count > 0 ? vals.Min() : (double?)null; break;
            case "MovingAverage": r = MovingAvg(c, vals); break;
            default:   // Expression
                string expr = c.Def.Expression;
                for (int i = 0; i < c.Inputs.Length; i++)
                    expr = expr.Replace("{" + c.Inputs[i] + "}", "(" + vals[i].ToString("R", CultureInfo.InvariantCulture) + ")");
                try { using var dt = new DataTable(); r = Convert.ToDouble(dt.Compute(expr, "")); }
                catch (Exception ex) { _log.LogDebug("计算 {n} 失败:{m}", c.Def.Name, ex.Message); WriteBadIfChanged(c.Def.Name, ts); return; }
                break;
        }
        if (r is null) { WriteBadIfChanged(c.Def.Name, ts); return; }

        object typed = CastTo(r.Value, c.Def.DataType);
        if (_hub.TryGet(c.Def.Name, out var old) && old.IsGood && Equals(old.Value, typed)) return;  // 未变不写(断环)
        _hub.Update(c.Def.Name, DataValue.GoodAt(typed, ts));
    }

    // 移动平均:把第一个输入的值压入窗口,返回窗口均值
    private static double MovingAvg(Computed c, List<double> vals)
    {
        c.Window.Enqueue(vals.Count > 0 ? vals[0] : 0);
        int max = Math.Max(1, c.Def.WindowSize);
        while (c.Window.Count > max) c.Window.Dequeue();
        return c.Window.Average();
    }

    private void WriteBadIfChanged(string id, DateTime ts)
    {
        if (_hub.TryGet(id, out var old) && !old.IsGood) return;
        _hub.Update(id, DataValue.BadAt(StatusCode.Bad, ts));
    }

    private static bool TryNum(object? v, out double d)
    {
        d = 0; if (v is null) return false;
        try { d = Convert.ToDouble(v); return true; } catch { return false; }
    }

    private static object CastTo(object? r, IgwDataType t)
    {
        double d; try { d = Convert.ToDouble(r); } catch { return 0.0; }
        return t switch
        {
            IgwDataType.Bool => d != 0,
            IgwDataType.Int16 => (short)d, IgwDataType.UInt16 => (ushort)d,
            IgwDataType.Int32 => (int)d, IgwDataType.UInt32 => (uint)d,
            IgwDataType.Int64 => (long)d, IgwDataType.UInt64 => (ulong)d,
            IgwDataType.Float => (float)d, IgwDataType.Double => d,
            _ => d,
        };
    }

    public void Dispose()
    {
        try { _sub?.Dispose(); } catch { }
        // 注销本引擎登记的计算点:改名/删除后不在 DataHub 留幽灵点(北向重建时会按新配置重新登记)
        foreach (var c in _computed) { try { _hub.Unregister(c.Def.Name); } catch { } }
    }
}
