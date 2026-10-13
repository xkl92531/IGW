using IGW.Abstractions;
using IGW.Core;
using Xunit;

namespace IGW.Tests;

public sealed class AcquisitionPerformanceAttributionTests
{
    private static readonly DateTimeOffset CapturedAt = new(2026, 9, 7, 8, 0, 0, TimeSpan.Zero);

    public static IEnumerable<object[]> AttributionCases()
    {
        yield return Case(
            "local processing is direct evidence",
            Batch(totalMs: 200, driverMs: 140, localMs: 60, protocol: Protocol()),
            null,
            100,
            "igw-local-processing",
            [],
            ["local=60 ms", "total=200 ms"]);

        yield return Case(
            "shared queue is direct evidence",
            Batch(totalMs: 320, driverMs: 250, localMs: 20, protocol: Protocol(queueMs: 110)),
            null,
            100,
            "igw-shared-queue",
            [],
            ["queue=110 ms", "driver=250 ms"]);

        yield return Case(
            "loss and degraded protocol suspect the network",
            Batch(totalMs: 260, driverMs: 250, localMs: 10, protocol: Protocol(maxMs: 170)),
            Window(count: 6, successes: 5, failures: 1, latestRttMs: 25, baselineP95RttMs: 10, lossPercent: 16.67),
            100,
            "network-suspected",
            [],
            ["loss=16.67%", "request-max=170 ms", "protocol-baseline-p95=100 ms"]);

        yield return Case(
            "rtt surge and degraded protocol suspect the network",
            Batch(totalMs: 260, driverMs: 250, localMs: 10, protocol: Protocol(maxMs: 170)),
            Window(count: 6, successes: 6, failures: 0, latestRttMs: 50, baselineP95RttMs: 10, lossPercent: 0),
            100,
            "network-suspected",
            [],
            ["latest-rtt=50 ms", "network-baseline-p95=10 ms"]);

        yield return Case(
            "stable network and degraded dominant protocol request suspect the server",
            Batch(totalMs: 200, driverMs: 190, localMs: 10, protocol: Protocol(maxMs: 150)),
            Window(count: 6, successes: 6, failures: 0, latestRttMs: 12, baselineP95RttMs: 10, lossPercent: 0),
            100,
            "plc-opc-server-suspected",
            [],
            ["request-max=150 ms", "total=200 ms", "latest-rtt=12 ms"]);

        yield return Case(
            "split and failed requests remain a factor",
            Batch(totalMs: 240, driverMs: 230, localMs: 10, protocol: Protocol(requests: 2, failedRequests: 1, cancelledRequests: 0, maxMs: 170)),
            Window(count: 6, successes: 5, failures: 1, latestRttMs: 25, baselineP95RttMs: 10, lossPercent: 16.67),
            100,
            "network-suspected",
            ["request-split-or-retry"],
            ["requests=2", "failed=1"]);

        yield return Case(
            "missing protocol timing is insufficient evidence",
            Batch(totalMs: 200, driverMs: 190, localMs: 10, protocol: null),
            Window(count: 6, successes: 6, failures: 0, latestRttMs: 12, baselineP95RttMs: 10, lossPercent: 0),
            100,
            "insufficient-evidence",
            [],
            ["protocol timing unavailable"]);

        yield return Case(
            "small network window is insufficient evidence",
            Batch(totalMs: 200, driverMs: 190, localMs: 10, protocol: Protocol(maxMs: 170)),
            Window(count: 5, successes: 5, failures: 0, latestRttMs: 12, baselineP95RttMs: 10, lossPercent: 0),
            100,
            "insufficient-evidence",
            [],
            ["network samples=5"]);

        yield return Case(
            "unavailable network does not support a server suspicion",
            Batch(totalMs: 200, driverMs: 190, localMs: 10, protocol: Protocol(maxMs: 170)),
            Window(count: 6, successes: 6, failures: 0, latestRttMs: 12, baselineP95RttMs: 10, lossPercent: 0,
                status: "unavailable", error: "ICMP blocked"),
            100,
            "insufficient-evidence",
            [],
            ["network status=unavailable", "network error=ICMP blocked"]);

        yield return Case(
            "unverified route does not support a server suspicion",
            Batch(totalMs: 200, driverMs: 190, localMs: 10, protocol: Protocol(maxMs: 170)),
            Window(count: 6, successes: 6, failures: 0, latestRttMs: 12, baselineP95RttMs: 10, lossPercent: 0,
                routeVerified: false),
            100,
            "insufficient-evidence",
            [],
            ["network route unverified"]);

        yield return Case(
            "missing rtt measurements do not establish a stable network",
            Batch(totalMs: 200, driverMs: 190, localMs: 10, protocol: Protocol(maxMs: 170)),
            Window(count: 6, successes: 6, failures: 0, latestRttMs: null, baselineP95RttMs: null, lossPercent: 0),
            100,
            "insufficient-evidence",
            [],
            ["network stability metrics unavailable"]);

        yield return Case(
            "missing loss measurement does not establish a stable network",
            Batch(totalMs: 200, driverMs: 190, localMs: 10, protocol: Protocol(maxMs: 170)),
            Window(count: 6, successes: 6, failures: 0, latestRttMs: 12, baselineP95RttMs: 10, lossPercent: null),
            100,
            "insufficient-evidence",
            [],
            ["network stability metrics unavailable"]);

        yield return Case(
            "inconsistent usable sample accounting does not establish a stable network",
            Batch(totalMs: 200, driverMs: 190, localMs: 10, protocol: Protocol(maxMs: 170)),
            Window(count: 6, successes: 6, failures: 1, latestRttMs: 12, baselineP95RttMs: 10, lossPercent: 0),
            100,
            "insufficient-evidence",
            [],
            ["network sample accounting unavailable"]);

        yield return Case(
            "explicit same session retry remains a request shape factor",
            Batch(totalMs: 200, driverMs: 190, localMs: 10, protocol: Protocol()),
            Window(count: 6, successes: 6, failures: 0, latestRttMs: 12, baselineP95RttMs: 10, lossPercent: 0),
            100,
            "insufficient-evidence",
            ["request-split-or-retry"],
            ["same-session-retries=1"],
            sameSessionRetries: 1);

        yield return Case(
            "explicit batch downshift remains a request shape factor",
            Batch(totalMs: 200, driverMs: 190, localMs: 10, protocol: Protocol()),
            Window(count: 6, successes: 6, failures: 0, latestRttMs: 12, baselineP95RttMs: 10, lossPercent: 0),
            100,
            "insufficient-evidence",
            ["request-split-or-retry"],
            ["batch-downshifts=1"],
            batchDownshifts: 1);

        yield return Case(
            "absent protocol baseline cannot suspect an adverse network",
            Batch(totalMs: 260, driverMs: 250, localMs: 10, protocol: Protocol(maxMs: 170)),
            Window(count: 6, successes: 5, failures: 1, latestRttMs: 25, baselineP95RttMs: 10, lossPercent: 16.67),
            null,
            "insufficient-evidence",
            [],
            ["protocol baseline p95 unavailable"]);

        yield return Case(
            "absent protocol baseline cannot suspect a stable network server",
            Batch(totalMs: 200, driverMs: 190, localMs: 10, protocol: Protocol(maxMs: 170)),
            Window(count: 6, successes: 6, failures: 0, latestRttMs: 12, baselineP95RttMs: 10, lossPercent: 0),
            null,
            "insufficient-evidence",
            [],
            ["protocol baseline p95 unavailable"]);

        yield return Case(
            "direct local evidence takes priority while retaining network and split factors",
            Batch(totalMs: 300, driverMs: 240, localMs: 60, protocol: Protocol(requests: 2, maxMs: 170)),
            Window(count: 6, successes: 5, failures: 1, latestRttMs: 25, baselineP95RttMs: 10, lossPercent: 16.67),
            100,
            "igw-local-processing",
            ["network-suspected", "request-split-or-retry"],
            ["local=60 ms", "loss=16.67%", "requests=2"]);
    }

    [Theory]
    [MemberData(nameof(AttributionCases))]
    public void Classify_returns_stable_code_factors_and_measured_evidence(
        string _, AcquisitionAttributionContext context, string expectedCode,
        string[] expectedFactors, string[] evidenceFragments)
    {
        var result = AcquisitionAttributionClassifier.Classify(context);

        Assert.Equal(expectedCode, result.Code);
        foreach (var expectedFactor in expectedFactors)
            Assert.Contains(expectedFactor, result.Factors);
        foreach (var fragment in evidenceFragments)
            Assert.Contains(result.Evidence, evidence => evidence.Contains(fragment, StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_timing_record_keeps_optional_network_and_attribution_without_replacing_missing_metrics()
    {
        var network = Window(count: 0, successes: 0, failures: 0, latestRttMs: null, baselineP95RttMs: null, lossPercent: null,
            status: "unavailable", error: "ICMP blocked");
        var attribution = new AcquisitionAttribution("insufficient-evidence", "证据不足", "low",
            ["protocol timing unavailable", "network status=unavailable"], []);
        var record = Batch(totalMs: 20, driverMs: 20, localMs: 0, protocol: null, network: network, attribution: attribution);

        Assert.Same(network, record.Network);
        Assert.Same(attribution, record.Attribution);
        Assert.Null(record.Protocol);
        Assert.Null(record.Network!.LatestRttMs);
        Assert.Null(record.Network.LossPercent);
    }

    private static object[] Case(string name, ScanTimingRecord batch, NetworkProbeWindow? network,
        double? protocolBaselineP95Ms, string expectedCode, string[] expectedFactors, string[] evidenceFragments,
        long sameSessionRetries = 0, long batchDownshifts = 0) =>
        [name, new AcquisitionAttributionContext(batch, network, protocolBaselineP95Ms, sameSessionRetries, batchDownshifts), expectedCode, expectedFactors, evidenceFragments];

    private static ProtocolTimingSnapshot Protocol(int requests = 1, int failedRequests = 0, int cancelledRequests = 0,
        double maxMs = 20, double queueMs = 0) => new(requests, failedRequests, cancelledRequests, maxMs, maxMs, queueMs);

    private static ScanTimingRecord Batch(int totalMs, int driverMs, int localMs, ProtocolTimingSnapshot? protocol,
        NetworkProbeWindow? network = null, AcquisitionAttribution? attribution = null) =>
        new(1, CapturedAt, 1, 100, null, totalMs, driverMs, localMs, 1, 1, 0, "good", null, 0,
            ["ns=2;s=PLC.DB.Value"], protocol, network, attribution);

    private static NetworkProbeWindow Window(int count, int successes, int failures, double? latestRttMs,
        double? baselineP95RttMs, double? lossPercent, string status = "healthy", string? error = null,
        bool routeVerified = true) =>
        new(CapturedAt.AddSeconds(-10), CapturedAt.AddSeconds(10), count, successes, failures, latestRttMs,
            latestRttMs, baselineP95RttMs, lossPercent, status, error, routeVerified);
}
