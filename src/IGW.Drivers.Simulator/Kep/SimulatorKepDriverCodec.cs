using IGW.Abstractions;

namespace IGW.Drivers.Simulator.Kep;

public sealed class SimulatorKepDriverCodec : KepDriverCodecBase
{
    public override string IgwDriverId => SimulatorDriverFactory.Id;
    public override IReadOnlyCollection<string> KepDriverNames { get; } = ["Simulator"];
    protected override string ExportDriverName => "Simulator";
}
