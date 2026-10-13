namespace IGW.OpcUaProbe;

public sealed class ProbeBrowseCapacityWindow
{
    private const uint BadNoContinuationPoints = 0x804B0000;

    public ProbeBrowseCapacityWindow(int initial)
    {
        if (initial < 1) throw new ArgumentOutOfRangeException(nameof(initial));
        Current = initial;
    }

    public int Current { get; private set; }

    public bool TryReduce(uint statusCode)
    {
        if (statusCode != BadNoContinuationPoints) return false;
        Current = Math.Max(1, Current / 2);
        return true;
    }
}
