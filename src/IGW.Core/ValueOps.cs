using IGW.Abstractions;

namespace IGW.Core;

/// <summary>缩放(raw↔eng)与死区运算。</summary>
public static class ValueOps
{
    /// <summary>raw 样本 → eng 值(发布热路径,变体零装箱):未启用/非数值原样(仅重打 serverTs);数值走缩放后以 double 承载。</summary>
    public static DataValue ApplyScaling(in DataValue raw, ScalingDef? s, DateTime serverTs)
    {
        if (s is null || !s.Enabled || raw.Kind == ValueKind.Empty) return raw.WithServer(serverTs);
        if (!raw.TryGetNumeric(out double r)) return raw.WithServer(serverTs);
        double span = s.RawHigh - s.RawLow;
        if (Math.Abs(span) < 1e-12) return raw.WithServer(serverTs);
        double frac = (r - s.RawLow) / span;
        if (s.SquareRoot) frac = frac <= 0 ? 0 : Math.Sqrt(frac);
        double eng = s.EngLow + frac * (s.EngHigh - s.EngLow);
        if (s.Clamp)
        {
            double lo = Math.Min(s.EngLow, s.EngHigh), hi = Math.Max(s.EngLow, s.EngHigh);
            eng = Math.Clamp(eng, lo, hi);
        }
        return DataValue.FromDouble(eng, raw.Quality, raw.SourceTimestamp, serverTs);
    }

    /// <summary>新旧值差异是否超过死区(变体路径,零装箱)。语义与 object 版一致:无死区=值等判断;数值差比较;非数值回退值等。</summary>
    public static bool ExceedsDeadband(in DataValue oldVal, in DataValue newVal, TagDef def)
    {
        if (def.DeadbandType == DeadbandType.None || def.Deadband <= 0)
            return !newVal.ValueEquals(in oldVal);
        if (!oldVal.TryGetNumeric(out var o) || !newVal.TryGetNumeric(out var n))
            return !newVal.ValueEquals(in oldVal);
        double diff = Math.Abs(n - o);
        return def.DeadbandType switch
        {
            DeadbandType.Absolute => diff > def.Deadband,
            DeadbandType.PercentOfRange => def.Scaling is { Enabled: true } sc
                ? diff > def.Deadband / 100.0 * Math.Abs(sc.EngHigh - sc.EngLow)
                : diff > def.Deadband,
            _ => diff > def.Deadband,
        };
    }

    /// <summary>raw → eng(线性缩放)。未启用或非数值则原样返回。</summary>
    public static object? ApplyScaling(object? raw, ScalingDef? s)
    {
        if (s is null || !s.Enabled || raw is null) return raw;
        if (!TryToDouble(raw, out var r)) return raw;
        double span = s.RawHigh - s.RawLow;
        if (Math.Abs(span) < 1e-12) return raw;
        double frac = (r - s.RawLow) / span;
        if (s.SquareRoot) frac = frac <= 0 ? 0 : Math.Sqrt(frac); // 平方根缩放
        double eng = s.EngLow + frac * (s.EngHigh - s.EngLow);
        if (s.Clamp)
        {
            double lo = Math.Min(s.EngLow, s.EngHigh), hi = Math.Max(s.EngLow, s.EngHigh);
            eng = Math.Clamp(eng, lo, hi);
        }
        return eng;
    }

    /// <summary>eng → raw(写入时反向缩放)。</summary>
    public static object? ReverseScaling(object? eng, ScalingDef? s)
    {
        if (s is null || !s.Enabled || eng is null) return eng;
        if (!TryToDouble(eng, out var e)) return eng;
        double span = s.EngHigh - s.EngLow;
        if (Math.Abs(span) < 1e-12) return eng;
        double frac = (e - s.EngLow) / span;
        if (s.SquareRoot) frac = frac <= 0 ? 0 : frac * frac; // 平方根的逆(与正向对称:负值截 0)
        double raw = s.RawLow + frac * (s.RawHigh - s.RawLow);
        if (s.Clamp)   // 与正向对称:钳到原始量程,避免写回越界/非法 raw
        {
            double lo = Math.Min(s.RawLow, s.RawHigh), hi = Math.Max(s.RawLow, s.RawHigh);
            raw = Math.Clamp(raw, lo, hi);
        }
        return raw;
    }

    /// <summary>新旧值差异是否超过死区(超过=需上报)。</summary>
    public static bool ExceedsDeadband(object? oldVal, object? newVal, TagDef def)
    {
        if (def.DeadbandType == DeadbandType.None || def.Deadband <= 0)
            return !Equals(oldVal, newVal);
        if (!TryToDouble(oldVal, out var o) || !TryToDouble(newVal, out var n))
            return !Equals(oldVal, newVal);
        double diff = Math.Abs(n - o);
        return def.DeadbandType switch
        {
            DeadbandType.Absolute => diff > def.Deadband,
            DeadbandType.PercentOfRange => def.Scaling is { Enabled: true } sc
                ? diff > def.Deadband / 100.0 * Math.Abs(sc.EngHigh - sc.EngLow)
                : diff > def.Deadband,
            _ => diff > def.Deadband,
        };
    }

    public static bool TryToDouble(object? v, out double d)
    {
        switch (v)
        {
            case null: d = 0; return false;
            case double x: d = x; return true;
            case float x: d = x; return true;
            case int x: d = x; return true;
            case uint x: d = x; return true;
            case short x: d = x; return true;
            case ushort x: d = x; return true;
            case long x: d = x; return true;
            case ulong x: d = x; return true;
            case byte x: d = x; return true;
            case sbyte x: d = x; return true;
            case bool x: d = x ? 1 : 0; return true;
            default: return double.TryParse(v.ToString(), out d);
        }
    }
}
