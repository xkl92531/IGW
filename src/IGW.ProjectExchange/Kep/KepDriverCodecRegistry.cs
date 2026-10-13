using IGW.Abstractions;

namespace IGW.ProjectExchange.Kep;

public sealed class KepDriverCodecRegistry
{
    public IReadOnlyList<IKepDriverCodec> Codecs { get; }

    public KepDriverCodecRegistry(IEnumerable<IKepDriverCodec> codecs)
    {
        Codecs = codecs.ToArray();
        EnsureUnique(Codecs.Select(codec => codec.IgwDriverId), "igw_driver_id");
        EnsureUnique(Codecs.SelectMany(codec => codec.KepDriverNames), "kep_driver_name");
    }

    private static void EnsureUnique(IEnumerable<string> values, string kind)
    {
        string? duplicate = values.GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicate is not null) throw new InvalidOperationException($"duplicate_{kind}:{duplicate}");
    }
}
