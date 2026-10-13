using IGW.Abstractions;
using IGW.Core;
using Xunit;

namespace IGW.Tests;

public sealed class ReadFailureLogGateTests
{
    [Fact]
    public void Repeated_failures_are_counted_and_a_changed_status_is_reported_immediately()
    {
        var gate = new ReadFailureLogGate();
        TagSample[] bad = [new("A", DataValue.BadAt(new StatusCode(0x80340000), DateTime.UtcNow))];
        Assert.True(gate.TryEmit(bad, "partial", 0, out _));
        for (int i = 1; i < 150; i++) Assert.False(gate.TryEmit(bad, "partial", i * 200, out _));
        Assert.True(gate.TryEmit(bad, "partial", 30_000, out int suppressed));
        Assert.Equal(149, suppressed);
        bad = [new("A", DataValue.BadAt(new StatusCode(0x803A0000), DateTime.UtcNow))];
        Assert.True(gate.TryEmit(bad, "partial", 30_001, out _));
        Assert.True(gate.Recover(out _));
        Assert.True(gate.TryEmit(bad, "partial", 30_002, out _));
    }
}
