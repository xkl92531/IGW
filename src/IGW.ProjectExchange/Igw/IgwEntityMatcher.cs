namespace IGW.ProjectExchange.Igw;

public sealed record IgwMatchCandidate<T>(string Id, string Path, string Fingerprint, T Value);

public sealed class IgwExtensionMergeException : Exception
{
    public string Code { get; }
    public IReadOnlyList<string> Candidates { get; }

    public IgwExtensionMergeException(string code, string message, IReadOnlyList<string>? candidates = null)
        : base(message)
    {
        Code = code;
        Candidates = candidates ?? Array.Empty<string>();
    }
}

public static class IgwEntityMatcher
{
    public static IgwMatchCandidate<T> Match<T>(
        string? stableId,
        string canonicalPath,
        string structuralFingerprint,
        IReadOnlyList<IgwMatchCandidate<T>> candidates)
        => new IgwEntityMatchIndex<T>(candidates).Match(stableId, canonicalPath, structuralFingerprint);

    public static string NormalizePath(string path) => string.Join('.',
        path.Split('.', StringSplitOptions.TrimEntries)
            .Select(segment => segment.Trim().ToUpperInvariant()));

    internal static IgwExtensionMergeException Ambiguous<T>(
        string path,
        IReadOnlyCollection<IgwMatchCandidate<T>> candidates) => new(
            "igw_match_ambiguous",
            $"IGW extension entity '{path}' matches more than one KEP entity.",
            candidates.Select(candidate => candidate.Path).ToArray());
}

internal sealed class IgwEntityMatchIndex<T>
{
    private readonly IReadOnlyList<IgwMatchCandidate<T>> _all;
    private readonly Dictionary<string, List<IgwMatchCandidate<T>>> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<IgwMatchCandidate<T>>> _byPath = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<IgwMatchCandidate<T>>> _byFingerprint = new(StringComparer.Ordinal);

    public IgwEntityMatchIndex(IReadOnlyList<IgwMatchCandidate<T>> candidates)
    {
        _all = candidates;
        foreach (IgwMatchCandidate<T> candidate in candidates)
        {
            Add(_byId, candidate.Id, candidate);
            Add(_byPath, IgwEntityMatcher.NormalizePath(candidate.Path), candidate);
            if (!string.IsNullOrEmpty(candidate.Fingerprint))
                Add(_byFingerprint, candidate.Fingerprint, candidate);
        }
    }

    public IgwMatchCandidate<T> Match(
        string? stableId,
        string canonicalPath,
        string structuralFingerprint)
    {
        if (!string.IsNullOrWhiteSpace(stableId)
            && TryUnique(_byId, stableId, canonicalPath, out IgwMatchCandidate<T>? byId))
            return byId;
        string normalized = IgwEntityMatcher.NormalizePath(canonicalPath);
        if (TryUnique(_byPath, normalized, canonicalPath, out IgwMatchCandidate<T>? byPath))
            return byPath;
        if (!string.IsNullOrEmpty(structuralFingerprint)
            && TryUnique(_byFingerprint, structuralFingerprint, canonicalPath, out IgwMatchCandidate<T>? byFingerprint))
            return byFingerprint;
        throw new IgwExtensionMergeException(
            "igw_match_not_found",
            $"IGW extension entity '{canonicalPath}' has no unique KEP counterpart.",
            _all.Select(candidate => candidate.Path).ToArray());
    }

    private static bool TryUnique(
        IReadOnlyDictionary<string, List<IgwMatchCandidate<T>>> index,
        string key,
        string path,
        out IgwMatchCandidate<T> match)
    {
        if (!index.TryGetValue(key, out List<IgwMatchCandidate<T>>? candidates))
        {
            match = null!;
            return false;
        }
        if (candidates.Count > 1) throw IgwEntityMatcher.Ambiguous(path, candidates);
        match = candidates[0];
        return true;
    }

    private static void Add(
        IDictionary<string, List<IgwMatchCandidate<T>>> index,
        string key,
        IgwMatchCandidate<T> candidate)
    {
        if (!index.TryGetValue(key, out List<IgwMatchCandidate<T>>? values))
            index.Add(key, values = []);
        values.Add(candidate);
    }
}
