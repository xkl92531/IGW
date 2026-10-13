using System.Globalization;
using IGW.Abstractions;

namespace IGW.Core;

public sealed record NetworkProbeSample(
    DateTimeOffset CapturedAt,
    string Status,
    double? RttMs,
    string? Error,
    string SourceAddress,
    string RemoteAddress,
    bool RouteVerified);

public sealed record NetworkProbeWindow(
    DateTimeOffset From,
    DateTimeOffset To,
    int Count,
    int SuccessCount,
    int FailedCount,
    double? LatestRttMs,
    double? P95RttMs,
    double? BaselineP95RttMs,
    double? LossPercent,
    string Status,
    string? Error,
    bool RouteVerified);

public sealed record AcquisitionAttribution(
    string Code,
    string Label,
    string Confidence,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> Factors);

public sealed record AcquisitionAttributionContext(
    ScanTimingRecord Batch,
    NetworkProbeWindow? Network,
    double? ProtocolBaselineP95Ms,
    long SameSessionRetries = 0,
    long BatchDownshifts = 0);

/// <summary>
/// Deterministically attributes a completed batch using only its supplied measurements.
/// The protocol baseline must be calculated from prior batches by the caller.
/// </summary>
public static class AcquisitionAttributionClassifier
{
    private const int LocalThresholdMs = 50;
    private const double LocalTotalShare = 0.20;
    private const int QueueThresholdMs = 100;
    private const double QueueDriverShare = 0.40;
    private const int MinimumNetworkSamples = 6;
    private const double ProtocolSlowdownMultiplier = 1.50;
    private const double ProtocolSlowdownMinimumMs = 20;
    private const double RttSurgeMultiplier = 3;
    private const double RttSurgeMinimumMs = 20;
    private const double ProtocolDominantTotalShare = 0.70;

    public static AcquisitionAttribution Classify(AcquisitionAttributionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(context.Batch);

        var batch = context.Batch;
        var protocol = batch.Protocol;
        var network = context.Network;
        var evidence = new List<string>();
        var candidates = new List<Candidate>();

        evidence.Add($"batch total={batch.TotalMs} ms; driver={batch.DriverMs} ms; local={batch.LocalMs} ms");

        bool hasProtocolTiming = protocol is { Requests: > 0 };
        bool hasProtocolBaseline = IsMeasurement(context.ProtocolBaselineP95Ms);
        bool protocolDegraded = hasProtocolTiming && hasProtocolBaseline &&
            protocol!.RequestMaxMs >= context.ProtocolBaselineP95Ms!.Value * ProtocolSlowdownMultiplier &&
            protocol.RequestMaxMs - context.ProtocolBaselineP95Ms.Value >= ProtocolSlowdownMinimumMs;

        AddProtocolEvidence(evidence, protocol, context.ProtocolBaselineP95Ms);
        AddNetworkEvidence(evidence, network);

        bool localDelayed = batch.TotalMs > 0 && batch.LocalMs >= LocalThresholdMs &&
            batch.LocalMs >= batch.TotalMs * LocalTotalShare;
        bool queueDelayed = protocol is not null && batch.DriverMs > 0 && protocol.QueueMs >= QueueThresholdMs &&
            protocol.QueueMs >= batch.DriverMs * QueueDriverShare;
        if (localDelayed)
        {
            evidence.Add($"local={batch.LocalMs} ms; total={batch.TotalMs} ms");
            candidates.Add(new("igw-local-processing", "IGW本地处理", "confirmed"));
        }
        if (queueDelayed)
        {
            evidence.Add($"queue={Format(protocol!.QueueMs)} ms; driver={batch.DriverMs} ms");
            candidates.Add(new("igw-shared-queue", "IGW共享队列", "confirmed"));
        }

        bool networkAvailable = network is not null && !string.Equals(network.Status, "unavailable", StringComparison.OrdinalIgnoreCase);
        bool networkSampleAccounting = network is not null && network.Count >= MinimumNetworkSamples &&
            network.SuccessCount >= 0 && network.FailedCount >= 0 && network.Count == network.SuccessCount + network.FailedCount;
        bool sufficientNetwork = networkAvailable && networkSampleAccounting && network!.RouteVerified;
        bool hasRttMeasurements = network is not null && IsMeasurement(network.LatestRttMs) && IsMeasurement(network.BaselineP95RttMs);
        bool hasLossMeasurement = network is not null && IsMeasurement(network.LossPercent);
        bool packetLoss = sufficientNetwork && (network!.FailedCount > 0 || hasLossMeasurement && network.LossPercent > 0);
        bool rttSurged = sufficientNetwork && IsMeasurement(network!.LatestRttMs) && IsMeasurement(network.BaselineP95RttMs) &&
            network.LatestRttMs!.Value >= network.BaselineP95RttMs!.Value * RttSurgeMultiplier &&
            network.LatestRttMs.Value - network.BaselineP95RttMs.Value >= RttSurgeMinimumMs;
        bool stableNetwork = sufficientNetwork && network!.SuccessCount >= MinimumNetworkSamples && network.FailedCount == 0 &&
            hasRttMeasurements && hasLossMeasurement && network.LossPercent == 0 && !rttSurged;

        if (protocolDegraded && (packetLoss || rttSurged))
            candidates.Add(new("network-suspected", "网络链路疑似", "suspected"));

        bool requestDominatesBatch = hasProtocolTiming && batch.TotalMs > 0 &&
            protocol!.RequestMaxMs >= batch.TotalMs * ProtocolDominantTotalShare;
        if (protocolDegraded && stableNetwork && !localDelayed && !queueDelayed && requestDominatesBatch)
            candidates.Add(new("plc-opc-server-suspected", "PLC/OPC UA服务器疑似", "suspected"));

        if (hasProtocolTiming && (protocol!.Requests > 1 || protocol.FailedRequests > 0 || protocol.CancelledRequests > 0) ||
            context.SameSessionRetries > 0 || context.BatchDownshifts > 0)
        {
            if (context.SameSessionRetries > 0 || context.BatchDownshifts > 0)
                evidence.Add($"same-session-retries={context.SameSessionRetries}; batch-downshifts={context.BatchDownshifts}");
            candidates.Add(new("request-split-or-retry", "请求拆分或重试", "medium"));
        }

        bool insufficientEvidence = !hasProtocolTiming || !hasProtocolBaseline || !sufficientNetwork ||
            !hasRttMeasurements || !hasLossMeasurement;
        if (insufficientEvidence)
        {
            if (!hasProtocolTiming) evidence.Add("protocol timing unavailable");
            if (hasProtocolTiming && !hasProtocolBaseline) evidence.Add("protocol baseline p95 unavailable");
            if (network is null) evidence.Add("network window unavailable");
            else
            {
                if (!networkAvailable) evidence.Add("network unavailable");
                if (network.Count < MinimumNetworkSamples) evidence.Add($"network samples={network.Count}; at least {MinimumNetworkSamples} required");
                if (!networkSampleAccounting) evidence.Add("network sample accounting unavailable");
                if (!network.RouteVerified) evidence.Add("network route unverified");
                if (!hasRttMeasurements || !hasLossMeasurement) evidence.Add("network stability metrics unavailable");
            }
        }

        Candidate primary = candidates.FirstOrDefault(candidate => candidate.Code is "igw-local-processing" or "igw-shared-queue")
            ?? candidates.FirstOrDefault(candidate => candidate.Code is "network-suspected" or "plc-opc-server-suspected")
            ?? new Candidate("insufficient-evidence", "证据不足", "low");

        var factors = candidates.Where(candidate => candidate.Code != primary.Code)
            .Select(candidate => candidate.Code)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new AcquisitionAttribution(primary.Code, primary.Label, primary.Confidence,
            Array.AsReadOnly(evidence.ToArray()), Array.AsReadOnly(factors));
    }

    private static void AddProtocolEvidence(List<string> evidence, ProtocolTimingSnapshot? protocol, double? baselineP95Ms)
    {
        if (protocol is null || protocol.Requests == 0) return;
        evidence.Add($"requests={protocol.Requests}; failed={protocol.FailedRequests}; cancelled={protocol.CancelledRequests}");
        evidence.Add($"request-total={Format(protocol.RequestTotalMs)} ms; request-max={Format(protocol.RequestMaxMs)} ms; queue={Format(protocol.QueueMs)} ms");
        if (IsMeasurement(baselineP95Ms)) evidence.Add($"protocol-baseline-p95={Format(baselineP95Ms!.Value)} ms");
    }

    private static void AddNetworkEvidence(List<string> evidence, NetworkProbeWindow? network)
    {
        if (network is null) return;
        evidence.Add($"network status={network.Status}; samples={network.Count}; success={network.SuccessCount}; failed={network.FailedCount}");
        if (IsMeasurement(network.LatestRttMs)) evidence.Add($"latest-rtt={Format(network.LatestRttMs!.Value)} ms");
        if (IsMeasurement(network.P95RttMs)) evidence.Add($"network-p95-rtt={Format(network.P95RttMs!.Value)} ms");
        if (IsMeasurement(network.BaselineP95RttMs)) evidence.Add($"network-baseline-p95={Format(network.BaselineP95RttMs!.Value)} ms");
        if (IsMeasurement(network.LossPercent)) evidence.Add($"loss={Format(network.LossPercent!.Value)}%");
        if (!string.IsNullOrWhiteSpace(network.Error)) evidence.Add($"network error={network.Error}");
    }

    private static bool IsMeasurement(double? value) => value is double measured && double.IsFinite(measured) && measured >= 0;

    private static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private sealed record Candidate(string Code, string Label, string Confidence);
}
