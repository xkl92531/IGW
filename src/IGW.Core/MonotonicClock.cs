namespace IGW.Core;

public interface IMonotonicClock
{
    long NowMilliseconds { get; }
}

internal sealed class SystemMonotonicClock : IMonotonicClock
{
    public static readonly SystemMonotonicClock Instance = new();
    private static readonly long OriginTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
    private static readonly long OriginMilliseconds = Environment.TickCount64;

    private SystemMonotonicClock() { }

    public long NowMilliseconds => OriginMilliseconds +
        (System.Diagnostics.Stopwatch.GetTimestamp() - OriginTimestamp) * 1000L /
        System.Diagnostics.Stopwatch.Frequency;
}
