using System.Collections;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace IGW.Core;

public static class GatewayTelemetry
{
    public const string ActivitySourceName = "IGW.Runtime";
    public const string MeterName = "IGW.Runtime";
    private static readonly ActivitySource Activities = new(ActivitySourceName);
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> EventCounter = Meter.CreateCounter<long>("igw.events");
    private static readonly Histogram<double> DurationHistogram = Meter.CreateHistogram<double>("igw.operation.duration", "ms");
    private static readonly HashSet<string> AllowedCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "collection", "interface", "state", "performance"
    };

    public static IReadOnlyDictionary<string, object?> Build(
        string category,
        string message,
        IReadOnlyDictionary<string, object?>? fields = null)
    {
        string normalizedCategory = AllowedCategories.Contains(category) ? category.ToLowerInvariant() : "state";
        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["igw_category"] = normalizedCategory,
            ["igw_module"] = normalizedCategory switch
            {
                "collection" or "state" or "performance" => "acquisition",
                "interface" => "integration",
                _ => "runtime"
            },
            ["igw_feature"] = normalizedCategory switch
            {
                "collection" => "plc_scan",
                "state" => "device_state",
                "performance" => "scan_performance",
                "interface" => "interface_call",
                _ => "general"
            },
            ["igw_event_kind"] = normalizedCategory switch
            {
                "collection" => "collection_batch",
                "state" => "state_change",
                "performance" => "performance_sample",
                "interface" => "interface_event",
                _ => "runtime_log"
            },
            ["{OriginalFormat}"] = RedactText(message)
        };
        if (fields is null) return result;
        foreach (var pair in fields)
        {
            if (SensitiveKey(pair.Key)) continue;
            string key = StableKey(pair.Key);
            object? value = pair.Value;
            if (value is string text)
                value = key == "igw_target" ? SanitizeTarget(text) : RedactText(text);
            result[key] = value;
        }
        return result;
    }

    public static void Log(
        ILogger logger,
        LogLevel level,
        string category,
        string message,
        IReadOnlyDictionary<string, object?>? fields = null,
        Exception? exception = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (!logger.IsEnabled(level)) return;
        IReadOnlyDictionary<string, object?> values = Build(category, message, fields);
        try
        {
            logger.Log(level, new EventId(7400, "IGW.Structured"), new TelemetryState(values), exception,
                static (state, _) => state.Message);
            TagList tags = new() { { "igw.category", values["igw_category"]?.ToString() ?? "state" } };
            EventCounter.Add(1, tags);
            if (values.TryGetValue("igw_duration_ms", out object? duration)
                && double.TryParse(Convert.ToString(duration, System.Globalization.CultureInfo.InvariantCulture), out double milliseconds)
                && double.IsFinite(milliseconds) && milliseconds >= 0)
                DurationHistogram.Record(milliseconds, tags);
        }
        catch
        {
            // Telemetry providers and metric listeners must never alter PLC, MES, or API behavior.
        }
    }

    public static Activity? StartActivity(
        string name,
        IReadOnlyDictionary<string, object?>? fields = null,
        ActivityKind kind = ActivityKind.Internal)
    {
        Activity? activity = Activities.StartActivity(name, kind);
        if (activity is null || fields is null) return activity;
        foreach (var pair in fields)
        {
            if (SensitiveKey(pair.Key)) continue;
            string key = StableKey(pair.Key);
            object? value = pair.Value is string text
                ? key == "igw_target" ? SanitizeTarget(text) : RedactText(text)
                : pair.Value;
            activity.SetTag(key, value);
        }
        return activity;
    }

    private static string StableKey(string key)
    {
        if (key.StartsWith("igw_", StringComparison.Ordinal)) return key;
        return key switch
        {
            "channel" => "igw_channel",
            "device" => "igw_device",
            "database" or "db" or "area" => "igw_db",
            "task" or "taskId" => "igw_task_id",
            "taskName" => "igw_task_name",
            "taskIds" => "igw_task_ids",
            "taskNames" => "igw_task_names",
            "node" or "nodeId" or "stepId" => "igw_node_id",
            "nodeName" or "stepName" => "igw_node_name",
            "duration" or "durationMs" => "igw_duration_ms",
            "status" => "igw_status",
            "target" or "url" => "igw_target",
            "tagCount" => "igw_tag_count",
            "sampleCount" => "igw_sample_count",
            "badCount" => "igw_bad_count",
            "configuredPeriodMs" => "igw_configured_period_ms",
            "actualIntervalMs" => "igw_actual_interval_ms",
            "driverMs" => "igw_driver_ms",
            "localMs" => "igw_local_ms",
            "missedPeriods" => "igw_missed_periods",
            "blockId" => "igw_block_id",
            "protocol" => "igw_protocol",
            "module" => "igw_module",
            "feature" => "igw_feature",
            "eventKind" => "igw_event_kind",
            "operation" => "igw_operation",
            "direction" => "igw_direction",
            "route" => "igw_route",
            "interfaceName" => "igw_interface_name",
            "collectionSource" => "igw_collection_source",
            "collectionSources" => "igw_collection_sources",
            "scope" => "igw_scope",
            "method" => "igw_method",
            "httpStatusCode" => "igw_http_status_code",
            "transportStatus" => "igw_transport_status",
            "businessStatus" => "igw_business_status",
            "payloadId" => "igw_payload_id",
            "requestBytes" => "igw_request_bytes",
            "responseBytes" => "igw_response_bytes",
            _ => "igw_" + ToSnakeCase(key)
        };
    }

    private static string ToSnakeCase(string value)
    {
        string normalized = Regex.Replace(value, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant();
        normalized = Regex.Replace(normalized, "[^a-z0-9_]+", "_").Trim('_');
        return string.IsNullOrEmpty(normalized) ? "field" : normalized;
    }

    private static bool SensitiveKey(string key) =>
        key.Contains("authorization", StringComparison.OrdinalIgnoreCase)
        || key.Contains("password", StringComparison.OrdinalIgnoreCase)
        || key.Contains("passwd", StringComparison.OrdinalIgnoreCase)
        || key.Contains("token", StringComparison.OrdinalIgnoreCase)
        || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || key.Contains("cookie", StringComparison.OrdinalIgnoreCase)
        || key.Contains("apiKey", StringComparison.OrdinalIgnoreCase)
        || key.Contains("api_key", StringComparison.OrdinalIgnoreCase);

    private static string SanitizeTarget(string target)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out Uri? uri)) return RedactText(target.Split('?', '#')[0]);
        var builder = new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" };
        return builder.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    internal static string RedactText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        string value = Regex.Replace(text,
            "(?i)(\\b(?:authorization|password|passwd|token|secret|api[_-]?key)\\b\\s*[:=]\\s*)([^&\\s;,\"'}]+)",
            "$1***");
        value = Regex.Replace(value, "(?i)(://[^:/\\s]+:)[^@/\\s]+@", "$1***@");
        return value.Length <= 2048 ? value : value[..2048];
    }

    private sealed class TelemetryState : IReadOnlyList<KeyValuePair<string, object?>>
    {
        private readonly KeyValuePair<string, object?>[] _values;
        public TelemetryState(IReadOnlyDictionary<string, object?> values)
        {
            _values = values.ToArray();
            Message = values.TryGetValue("{OriginalFormat}", out object? message) ? message?.ToString() ?? "" : "";
        }

        public string Message { get; }
        public int Count => _values.Length;
        public KeyValuePair<string, object?> this[int index] => _values[index];
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => ((IEnumerable<KeyValuePair<string, object?>>)_values).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _values.GetEnumerator();
    }
}
