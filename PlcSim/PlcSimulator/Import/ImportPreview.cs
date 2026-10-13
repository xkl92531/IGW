#nullable enable
using PlcSimulator.Core;

namespace PlcSimulator.Import;

public sealed class SimulationImportResult
{
    public required SimulationProject Project { get; init; }
    public int ChannelCount { get; init; }
    public List<string> Warnings { get; } = new();
}
