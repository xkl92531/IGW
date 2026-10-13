using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IGW.Abstractions;
using IGW.Core;

namespace IGW.Northbound.RestApi;

/// <summary>Whitelisted diagnostic contracts only. Temporary archive ownership transfers to the HTTP result.</summary>
public static class AcquisitionDiagnosticsExport
{
    public const long MaximumBytes = 32 * 1024 * 1024;
    private const string P95Definition = "ProtocolRequestP95Ms is the P95 of per-batch RequestMaxMs; it is not an individual-request P95.";
    private const string NetworkDefinition = "network.csv contains currently retained active-probe history (120 samples per endpoint); released endpoint workers may be omitted. Missing or latest-null network metrics remain unavailable.";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    // Unknown structured bodies cannot be safely diagnosed as free text. Retain the preceding error/quality code.
    private static readonly Regex SensitiveTail = new(
        "(?is)(?:\\b(?:username|password|passwd|pwd|credential(?:s)?|authorization|proxy[- ]authorization|cookie|set-cookie|secret|project[_ -]?secret|key|api[_ -]?key|token|id[_ -]?token|access[_ -]?token|refresh[_ -]?token|signature|client[_ -]?secret|request[_ -]?body|response[_ -]?body|MES\\s+(?:request|response))\\b\\s*[\\\"']?\\s*[:=]|\\b(?:(?:proxy[- ]authorization|authorization)\\s*[:=]?\\s*)?(?:Basic|Bearer)\\s+).*",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
    private static readonly Regex UserInfo = new(@"(\w+://)[^\s/@]+@", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));

    public static async Task<Stream> CreateAsync(AcquisitionDiagnosticsSnapshot snapshot, string projectRevision,
        CancellationToken cancellationToken, long maximumBytes = MaximumBytes)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (maximumBytes <= 0 || maximumBytes > MaximumBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        snapshot = Prepare(snapshot, cancellationToken);
        var file = new FileStream(Path.Combine(Path.GetTempPath(), "igw-diagnostics-" + Guid.NewGuid().ToString("N") + ".zip"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        try
        {
            var budget = new Budget(maximumBytes);
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
            {
                var batches = snapshot.Devices.SelectMany(device => device.Records).ToArray();
                var missing = new List<string>();
                if (snapshot.Devices.Count == 0) missing.Add("devices");
                if (batches.Length == 0) missing.Add("batches");
                if (snapshot.Network.Count == 0) missing.Add("network");
                if (snapshot.Connections.Active.Count == 0 && snapshot.Connections.Recovered.Count == 0) missing.Add("connection-diagnostics (no retained incidents)");
                if (snapshot.Devices.Any(device => device.Addresses.Any(address => address.Address is null))) missing.Add("point-addresses");
                await WriteJson("summary.json", new
                {
                    snapshot.CapturedAt, snapshot.Build, snapshot.ProjectName, snapshot.ProjectSchemaVersion, projectRevision,
                    from = batches.Length == 0 ? (DateTimeOffset?)null : batches.Min(batch => batch.StartedAt),
                    to = batches.Length == 0 ? (DateTimeOffset?)null : batches.Max(batch => batch.StartedAt),
                    devices = snapshot.Devices.Select(device => device.Summary).ToArray(),
                    snapshot.Truncated, missingSources = missing, maximumUncompressedBytes = maximumBytes,
                    limits = new { devices = 128, batchesPerDevice = 200, tagReferences = 100000, tagReferencesPerBatch = 4096, networkSamplesPerEndpoint = 120, activeIncidents = 128, recoveredIncidents = 200, evidenceItems = 64, factorItems = 64, freeTextCharacters = 8192 },
                    protocolRequestP95Definition = P95Definition, networkDefinition = NetworkDefinition
                });
                await WriteCsv("batches.csv", BatchRows(snapshot));
                await WriteCsv("requests.csv", RequestRows(snapshot));
                await WriteCsv("network.csv", NetworkRows(snapshot));
                await WriteJson("connection-diagnostics.json", ConnectionDiagnosticsApi.ToDto(snapshot.Connections));
                await using var readme = Entry("README.txt");
                await readme.WriteAsync(Encoding.UTF8.GetBytes(Readme), cancellationToken);

                Stream Entry(string name) => new BudgetStream(archive.CreateEntry(name, CompressionLevel.Optimal).Open(), budget, cancellationToken);
                async Task WriteJson<T>(string name, T value)
                {
                    await using var stream = Entry(name);
                    await JsonSerializer.SerializeAsync(stream, value, Json, cancellationToken);
                }
                async Task WriteCsv(string name, IEnumerable<object?[]> rows)
                {
                    await using var stream = Entry(name);
                    await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true) { NewLine = "\r\n" };
                    foreach (var row in rows)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await writer.WriteLineAsync(string.Join(',', row.Select(Csv)).AsMemory(), cancellationToken);
                    }
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            file.Position = 0;
            return file;
        }
        catch { await file.DisposeAsync(); throw; }
    }

    private static IEnumerable<object?[]> BatchRows(AcquisitionDiagnosticsSnapshot snapshot)
    {
        yield return ["Device", "Sequence", "StartedAt", "BlockId", "ConfiguredPeriodMs", "ActualIntervalMs", "TotalMs", "DriverMs", "LocalMs", "TagCount", "SampleCount", "BadCount", "Status", "Error", "MissedPeriods", "TagIds", "Addresses", "AddressStatus", "Requests", "FailedRequests", "CancelledRequests", "RequestTotalMs", "RequestMaxMs", "QueueMs", "SameSessionRetries", "BatchDownshifts", "Network", "Attribution", "AttributionLabel", "Confidence", "Evidence", "Factors", "LogicalBatchCount", "LatestProtocolRequestCount", "OmittedProtocolRequestSamples", "DeviceSessionFingerprint", "SessionGeneration"];
        foreach (var device in snapshot.Devices)
        {
            var addresses = device.Addresses.ToDictionary(address => address.TagId, address => address.Address);
            foreach (var batch in device.Records)
            {
                var points = batch.TagIds.Select(id => new { tagId = id, address = addresses.GetValueOrDefault(id) }).ToArray();
                yield return [device.Summary.Name, batch.Sequence, batch.StartedAt, batch.BlockId, batch.ConfiguredPeriodMs,
                    batch.ActualIntervalMs, batch.TotalMs, batch.DriverMs, batch.LocalMs, batch.TagCount, batch.SampleCount,
                    batch.BadCount, batch.Status, batch.Error, batch.MissedPeriods, string.Join('\n', batch.TagIds),
                    string.Join('\n', points.Select(point => point.address ?? "[missing]")), points.Any(point => point.address is null) ? "missing" : "available",
                    batch.Protocol?.Requests, batch.Protocol?.FailedRequests, batch.Protocol?.CancelledRequests,
                    batch.Protocol?.RequestTotalMs, batch.Protocol?.RequestMaxMs, batch.Protocol?.QueueMs,
                    batch.SameSessionRetries, batch.BatchDownshifts,
                    batch.Network is null ? null : JsonSerializer.Serialize(batch.Network, Json),
                    batch.Attribution?.Code, batch.Attribution?.Label, batch.Attribution?.Confidence,
                    batch.Attribution is null ? null : string.Join('\n', batch.Attribution.Evidence),
                    batch.Attribution is null ? null : string.Join('\n', batch.Attribution.Factors),
                    batch.LogicalBatchCount, batch.LatestProtocolRequestCount, batch.OmittedProtocolRequestSamples,
                    batch.DeviceSessionFingerprint, batch.SessionGeneration];
            }
        }
    }

    private static IEnumerable<object?[]> RequestRows(AcquisitionDiagnosticsSnapshot snapshot)
    {
        yield return ["Device", "Sequence", "RequestIndex", "Operation", "Scope", "TagCount", "PayloadBytes", "GapBytes", "ElapsedMs", "Failed", "Cancelled", "DeviceSessionFingerprint", "SessionGeneration"];
        static int? Known(int? value) => value >= 0 ? value : null;
        foreach (var device in snapshot.Devices)
            foreach (var batch in device.Records)
            {
                int index = 0;
                foreach (var request in batch.ProtocolRequestSamples ?? [])
                    yield return [device.Summary.Name, batch.Sequence, ++index, request.Descriptor?.Operation,
                        request.Descriptor?.Scope, Known(request.Descriptor?.TagCount), Known(request.Descriptor?.PayloadBytes),
                        Known(request.Descriptor?.GapBytes), request.ElapsedMs, request.Failed, request.Cancelled,
                        batch.DeviceSessionFingerprint, batch.SessionGeneration];
            }
    }

    private static IEnumerable<object?[]> NetworkRows(AcquisitionDiagnosticsSnapshot snapshot)
    {
        yield return ["CapturedAt", "Status", "RttMs", "Error", "SourceAddress", "RemoteAddress", "RouteVerified"];
        foreach (var sample in snapshot.Network)
            yield return [sample.CapturedAt, sample.Status, sample.RttMs, sample.Error, sample.SourceAddress, sample.RemoteAddress, sample.RouteVerified];
    }

    private static string Csv(object? value)
    {
        string text = value switch
        {
            null => "", DateTimeOffset date => date.ToString("O", CultureInfo.InvariantCulture),
            string str => str, IFormattable number => number.ToString(null, CultureInfo.InvariantCulture), _ => value.ToString() ?? ""
        };
        if (value is string)
        {
            int first = 0;
            while (first < text.Length && (char.IsWhiteSpace(text[first]) || char.IsControl(text[first]))) first++;
            if (text.StartsWith("'", StringComparison.Ordinal)
                || (first < text.Length && text[first] is '=' or '+' or '-' or '@')) text = "'" + text;
        }
        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? '"' + text.Replace("\"", "\"\"") + '"' : text;
    }

    // Prepare every untrusted free-text field before the summary is written. Typed identities are never redacted
    // or shortened: JSON escapes them normally, CSV applies only the documented reversible formula prefix.
    private static AcquisitionDiagnosticsSnapshot Prepare(AcquisitionDiagnosticsSnapshot snapshot, CancellationToken ct)
    {
        bool truncated = snapshot.Truncated;
        string? FreeText(string? value)
        {
            ct.ThrowIfCancellationRequested();
            if (value is null || value == "[truncated]") return value;
            bool clipped = value.Length > 8192;
            if (clipped) { truncated = true; value = value[..8192]; }
            // Inspect source text before inserting our own markers, so [truncated] and [redacted] stay distinct.
            int bodyStart = value.IndexOfAny(['{', '<', '[']);
            if (bodyStart >= 0) value = value[..bodyStart] + "[redacted]";
            value = SensitiveTail.Replace(UserInfo.Replace(value, "$1[redacted]@"), "[redacted]");
            return clipped ? value + " [truncated]" : value;
        }
        IReadOnlyList<string> TextList(IReadOnlyList<string> values)
        {
            bool clipped = values.Count > 64;
            if (clipped) truncated = true;
            var result = values.Take(clipped ? 63 : 64).Select(value => FreeText(value)!).ToList();
            if (clipped) result.Add("[truncated]");
            return result.AsReadOnly();
        }
        ConnectionIncident Incident(ConnectionIncident incident) => incident with
        {
            CategoryLabel = FreeText(incident.CategoryLabel)!, Cause = FreeText(incident.Cause)!,
            Suggestion = FreeText(incident.Suggestion)!, RawError = FreeText(incident.RawError)!
        };
        IReadOnlyList<ProtocolRequestSample>? Requests(IReadOnlyList<ProtocolRequestSample>? samples)
        {
            if (samples is null) return null;
            if (samples.Count > 64) truncated = true;
            return Array.AsReadOnly(samples.Take(64).Select(sample => sample with
            {
                Descriptor = sample.Descriptor is { } descriptor ? descriptor with
                { Operation = FreeText(descriptor.Operation)!, Scope = FreeText(descriptor.Scope)! } : null
            }).ToArray());
        }
        foreach (var device in snapshot.Devices) if (device.Records.Count > 200) truncated = true;
        var devices = snapshot.Devices.Select(device => device with
        {
            Summary = device.Summary with { LatestAttribution = FreeText(device.Summary.LatestAttribution),
                SessionLimitSource = FreeText(device.Summary.SessionLimitSource),
                ProtocolRequestSamples = Requests(device.Summary.ProtocolRequestSamples),
                OmittedProtocolRequestSamples = device.Summary.OmittedProtocolRequestSamples is { } omitted
                    ? omitted + Math.Max(0, (device.Summary.ProtocolRequestSamples?.Count ?? 0) - 64) : null },
            Records = Array.AsReadOnly(device.Records.Take(200).Select(batch => batch with
            {
                Error = FreeText(batch.Error),
                ProtocolRequestSamples = Requests(batch.ProtocolRequestSamples),
                OmittedProtocolRequestSamples = batch.OmittedProtocolRequestSamples is { } omitted
                    ? omitted + Math.Max(0, (batch.ProtocolRequestSamples?.Count ?? 0) - 64) : null,
                Protocol = batch.Protocol is { } protocol ? protocol with { Samples = Requests(protocol.Samples) } : null,
                Network = batch.Network is { } window ? window with { Error = FreeText(window.Error) } : null,
                Attribution = batch.Attribution is { } attribution ? attribution with
                {
                    Label = FreeText(attribution.Label)!, Evidence = TextList(attribution.Evidence), Factors = TextList(attribution.Factors)
                } : null
            }).ToArray())
        }).ToArray();
        var network = snapshot.Network.Select(sample => sample with { Error = FreeText(sample.Error) }).ToArray();
        var connections = new ConnectionIncidentSnapshot(Array.AsReadOnly(snapshot.Connections.Active.Select(Incident).ToArray()),
            Array.AsReadOnly(snapshot.Connections.Recovered.Select(Incident).ToArray()));
        return snapshot with { Devices = Array.AsReadOnly(devices), Network = Array.AsReadOnly(network), Connections = connections, Truncated = truncated };
    }

    private sealed class Budget(long maximum)
    {
        private long _used;
        public void Add(int count)
        {
            if (count > maximum - _used) throw new InvalidDataException("Diagnostic export size limit exceeded.");
            _used += count;
        }
    }

    private sealed class BudgetStream(Stream inner, Budget budget, CancellationToken cancellationToken) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override void Write(byte[] buffer, int offset, int count) { cancellationToken.ThrowIfCancellationRequested(); budget.Add(count); inner.Write(buffer, offset, count); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        { cancellationToken.ThrowIfCancellationRequested(); budget.Add(buffer.Length); await inner.WriteAsync(buffer, ct); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await inner.DisposeAsync(); GC.SuppressFinalize(this); }
    }

    private const string Readme = "IGW acquisition diagnostics\r\n" + P95Definition + "\r\n" + NetworkDefinition + "\r\n" +
        "PollingTags counts points; LogicalBatchCount counts compiled device/period/priority batches. LatestProtocolRequestCount counts observed physical requests and is null for opaque operations. requests.csv retains at most 64 request samples per batch within the 200-batch/device window; omitted counts remain on batches. Descriptor negative counts/sizes are unavailable (empty CSV cells). DeviceSessionFingerprint is an anonymous logical device/path identity and SessionGeneration tracks connections within its runtime lifetime. SameEndpointDeviceCount is configured runtime devices; SameEndpointSessionCount is the distinct currently Connected physical sessions observed by IGW after endpoint pooling, not other clients. KnownSessionLimit and SessionLimitSource remain null unless reliable southbound server evidence exists; no default server limit is assumed.\r\n" +
        "Batch TotalMs = DriverMs + LocalMs. QueueMs is shared scheduler wait; protocol request time includes network and server work. All timings are milliseconds. ActualIntervalMs/null means no previous retained interval.\r\n" +
        "CSV uses UTF-8 without BOM, RFC4180 quoting, CRLF records and invariant decimal points. Empty numeric cells and JSON null mean unavailable, never zero. Exactly one apostrophe is prefixed to a text cell when its original first character is an apostrophe OR its first non-whitespace/control character is =, +, -, or @. This happens before CSV escaping, prevents formula evaluation and distinguishes literal leading apostrophes from protection prefixes. After parsing RFC4180, decode a textual cell by removing exactly its first character if that character is an apostrophe; otherwise leave it unchanged. Multiple original leading apostrophes therefore round-trip without ambiguity. Numeric and date cells are not transformed. TagIds and Addresses are newline-separated lists in the same order, identifying actual IGW batch members and their compiled PLC address/NodeId; missing addresses are explicitly marked. Network is a JSON window per batch; Evidence and Factors retain attribution support.\r\n" +
        "ICMP RTT is auxiliary network evidence, not OPC UA TCP RTT. Stable network can only support suspected PLC/OPC UA server attribution; server execution time is not measured. Missing network/protocol evidence cannot establish a PLC or network cause.\r\n" +
        "Read-only retained memory snapshot. No PLC reads/writes, session creation, shared scheduler acquisition or additional probes. Collection sources may advance independently during capture. Limits: 128 devices, 200 batches/device, 100000 total/4096 per-batch tag references, 120 samples/active endpoint, 128 active/200 recovered incidents, 32 MiB total uncompressed content. Truncation and missing sources are recorded in summary.json.\r\n" +
        "Only diagnostic fields are exported. Options, credentials, headers, cookies, MES bodies and arbitrary project objects are excluded. Typed project/device/IGW path/NodeId/source/remote identities remain verbatim in JSON and retain all original characters in CSV apart from its reversible formula prefix. The total archive byte budget also applies to identities. Only untrusted free-text errors, messages and evidence are credential/body-redacted. Free text is capped at 8192 source characters, evidence/factor lists at 64 items including any clipping marker. Preparation runs before summary.json so every length/list clip sets truncated=true. [truncated] denotes omitted bounded data; [redacted] denotes removed sensitive text.\r\n";
}
