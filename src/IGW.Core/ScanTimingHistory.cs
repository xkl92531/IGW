using IGW.Abstractions;

namespace IGW.Core;

public sealed record ScanTimingRecord(
    long Sequence, DateTimeOffset StartedAt, int BlockId, int ConfiguredPeriodMs,
    long? ActualIntervalMs, int TotalMs, int DriverMs, int LocalMs,
    int TagCount, int SampleCount, int BadCount, string Status, string? Error,
    long MissedPeriods, IReadOnlyList<string> TagIds, ProtocolTimingSnapshot? Protocol = null,
    NetworkProbeWindow? Network = null, AcquisitionAttribution? Attribution = null,
    long SameSessionRetries = 0, long BatchDownshifts = 0,
    int? LogicalBatchCount = null, int? LatestProtocolRequestCount = null,
    IReadOnlyList<ProtocolRequestSample>? ProtocolRequestSamples = null, int? OmittedProtocolRequestSamples = null,
    string? DeviceSessionFingerprint = null, long? SessionGeneration = null);

/// <summary>
/// ProtocolRequestP95Ms is the P95 of each retained batch's RequestMaxMs (批次请求最大耗时 P95),
/// not an individual-request P95. Network metrics come only from the newest batch's window; null stays missing.
/// </summary>
public sealed record ScanTimingDeviceSummary(
    string Name, string Mode, int ConfiguredMinPeriodMs, int ConfiguredMaxPeriodMs,
    int PollingTags, int SubscribedTags, int PlannedBlocks, int LastScanMs,
    double ActualAveragePeriodMs, int ActualP95PeriodMs, long OverduePeriods,
    double SamplesPerSecond, DateTimeOffset? LastScanAt,
    int? LastDriverMs, int? LastLocalMs, int? LastTagCount,
    double? QueueP95Ms = null, double? ProtocolRequestP95Ms = null, double? ProtocolRequestMaxMs = null,
    double? LocalP95Ms = null, double? NetworkP95RttMs = null, double? NetworkLossPercent = null,
    string? LatestAttribution = null, string? LatestAttributionConfidence = null,
    int? LogicalBatchCount = null, int? LatestProtocolRequestCount = null,
    IReadOnlyList<ProtocolRequestSample>? ProtocolRequestSamples = null, int? OmittedProtocolRequestSamples = null,
    string? DeviceSessionFingerprint = null, long? SessionGeneration = null,
    int? SameEndpointDeviceCount = null, int? SameEndpointSessionCount = null,
    int? KnownSessionLimit = null, string? SessionLimitSource = null);

public sealed record ScanTimingSnapshot(
    IReadOnlyList<ScanTimingDeviceSummary> Devices, string? Device,
    IReadOnlyList<ScanTimingRecord> Records, int Capacity);

public sealed record TagScanTimingBatch(long Sequence, DateTimeOffset StartedAt, int ConfiguredPeriodMs,
    long? ActualIntervalMs, int TotalMs, int DriverMs, int LocalMs, int TagCount, string Status)
{
    internal static TagScanTimingBatch From(ScanTimingRecord record) => new(record.Sequence, record.StartedAt,
        record.ConfiguredPeriodMs, record.ActualIntervalMs, record.TotalMs, record.DriverMs, record.LocalMs,
        record.TagCount, record.Status);
}

public sealed record TagScanTiming(string TagId, string Device, string Mode, TagScanTimingBatch? Batch);

/// <summary>
/// Only completed scheduled driver calls; no extra PLC reads and no per-sample log writes.
/// Members are cached on the compiled block, so retaining a batch does not copy tag IDs each scan.
/// </summary>
internal sealed class ScanTimingHistory
{
    public const int Capacity = 200;
    private readonly object _gate = new();
    private readonly ScanTimingRecord?[] _records = new ScanTimingRecord[Capacity];
    private long _sequence;
    private int _next;
    private int _count;

    public ScanTimingRecord Record(CompiledScanBlock block, DateTimeOffset startedAt, long startedMs,
        long driverCompletedMs, long completedMs, int samples, int badSamples,
        string? error, long missedPeriods, ProtocolTimingSnapshot? protocol = null,
        IReadOnlyList<TagBinding>? requested = null, NetworkProbeWindow? network = null,
        long sameSessionRetries = 0, long batchDownshifts = 0,
        string? deviceSessionFingerprint = null, long? sessionGeneration = null)
    {
        long? interval = block.LastScanStartedMs is long previous ? Math.Max(0, startedMs - previous) : null;
        block.LastScanStartedMs = startedMs;
        requested ??= block.Tags;
        var tags = block.TimingTagIds;
        if (tags is null || !SameMembers(tags, requested))
            tags = block.TimingTagIds = Array.AsReadOnly(requested.Select(tag => tag.TagId).ToArray());
        int bad = Math.Clamp(badSamples + Math.Max(0, tags.Count - samples), 0, tags.Count);
        string status = error is not null ? "error" : bad == 0 ? "good" : bad >= tags.Count ? "bad" : "partial";
        // Keep bounded diagnostic text too: a driver exception can otherwise contain a whole response.
        if (error?.Length > 2048) error = error[..2048];
        lock (_gate)
        {
            // Prior records only; this baseline deliberately remains the per-batch maximum.
            double? baseline = Percentile(_records.Where(record => record?.Protocol is { Requests: > 0 })
                .Select(record => (double?)record!.Protocol!.RequestMaxMs));
            var batch = new ScanTimingRecord(++_sequence, startedAt, block.Id, block.PeriodMs,
                interval, Milliseconds(completedMs - startedMs), Milliseconds(driverCompletedMs - startedMs),
                Milliseconds(completedMs - driverCompletedMs), tags.Count, samples, bad, status, error,
                missedPeriods, tags, protocol, network, SameSessionRetries: sameSessionRetries, BatchDownshifts: batchDownshifts,
                LogicalBatchCount: 1, LatestProtocolRequestCount: protocol?.PhysicalRequests,
                ProtocolRequestSamples: protocol?.Samples, OmittedProtocolRequestSamples: protocol?.OmittedSamples,
                DeviceSessionFingerprint: deviceSessionFingerprint, SessionGeneration: sessionGeneration);
            var final = batch with { Attribution = AcquisitionAttributionClassifier.Classify(
                new(batch, network, baseline, sameSessionRetries, batchDownshifts)) };
            _records[_next] = final;
            _next = (_next + 1) % Capacity;
            _count = Math.Min(Capacity, _count + 1);
            return final;
        }
    }

    internal static double? Percentile(IEnumerable<double?> measurements)
    {
        var values = measurements.Where(value => value is double number && double.IsFinite(number) && number >= 0)
            .Select(value => value!.Value).Order().ToArray();
        return values.Length == 0 ? null : values[(int)Math.Ceiling(values.Length * .95) - 1];
    }

    public ScanTimingRecord? Latest
    {
        get { lock (_gate) return _count == 0 ? null : _records[(_next + Capacity - 1) % Capacity]; }
    }

    private static bool SameMembers(IReadOnlyList<string> cached, IReadOnlyList<TagBinding> requested)
    {
        if (cached.Count != requested.Count) return false;
        for (int i = 0; i < cached.Count; i++)
            if (!string.Equals(cached[i], requested[i].TagId, StringComparison.Ordinal)) return false;
        return true;
    }

    public IReadOnlyList<ScanTimingRecord> Snapshot(int limit, string? tag)
    {
        limit = Math.Clamp(limit, 1, Capacity);
        tag = tag?.Trim();
        lock (_gate)
        {
            var result = new List<ScanTimingRecord>(Math.Min(limit, _count));
            for (int i = 0; i < _count && result.Count < limit; i++)
            {
                var record = _records[(_next + Capacity - 1 - i) % Capacity]!;
                if (string.IsNullOrEmpty(tag) || record.TagIds.Any(id => id.Contains(tag, StringComparison.OrdinalIgnoreCase)))
                    result.Add(record);
            }
            return result;
        }
    }

    internal IReadOnlyDictionary<string, ScanTimingRecord> LatestForTags(IReadOnlySet<string> requested)
    {
        var remaining = new HashSet<string>(requested, StringComparer.Ordinal);
        var result = new Dictionary<string, ScanTimingRecord>(StringComparer.Ordinal);
        if (remaining.Count == 0) return result;
        // Copy at most 200 immutable record references under the lock, then search outside it.
        // A QC query must never hold the acquisition lock while walking large batch memberships.
        foreach (var record in Snapshot(Capacity, null))
        {
            foreach (var tag in record.TagIds)
                if (remaining.Remove(tag)) result[tag] = record;
            if (remaining.Count == 0) break;
        }
        return result;
    }

    internal static int Milliseconds(long value) => (int)Math.Clamp(value, 0, int.MaxValue);
}
