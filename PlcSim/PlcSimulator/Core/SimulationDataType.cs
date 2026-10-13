#nullable enable
namespace PlcSimulator.Core;

public enum SimulationDataType
{
    Boolean, Byte, SByte, Int16, UInt16, Int32, UInt32, Int64, UInt64,
    Float32, Float64, String, DateTime, TimeSpan, Bytes, Unknown
}

public enum SimulationWriteSource { Initialization, UserInterface, Generator, Protocol }

public sealed class SimulationValueException : Exception
{
    public SimulationValueException(string message, Exception? inner = null) : base(message, inner) { }
}
