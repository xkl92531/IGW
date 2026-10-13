#nullable enable
namespace PlcSimulator.Core;

public readonly record struct SimulationValue<T>(T Value, long Version, DateTimeOffset Timestamp);
public readonly record struct SimulationUntypedValue(object? Value, long Version, DateTimeOffset Timestamp);
public sealed record SimulationValueChanged(Guid TagId, object? Value, long Version, DateTimeOffset Timestamp, SimulationWriteSource Source);
