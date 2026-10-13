using System.Collections;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace IGW.Observability;

public static class LogSourceContext
{
    public static IReadOnlyDictionary<string, object?> FieldsFor(string categoryName)
    {
        string logger = string.IsNullOrWhiteSpace(categoryName) ? "Unknown" : categoryName.Trim();
        (string module, string feature) = logger switch
        {
            _ when logger.StartsWith("Device:", StringComparison.OrdinalIgnoreCase) => ("acquisition", "device_loop"),
            "RuntimeEngine" => ("acquisition", "runtime_engine"),
            "Computed" => ("acquisition", "computed_tags"),
            "Scheduler" => ("automation", "scheduler"),
            "DataLogger" => ("history", "data_logger"),
            "MesUpload" => ("mes", "flow_engine"),
            "MqttPublish" => ("integration", "mqtt_publish"),
            "GatewayHost" => ("runtime", "gateway_host"),
            _ when logger.Contains("NetworkPerformanceProbe", StringComparison.OrdinalIgnoreCase) => ("acquisition", "network_probe"),
            _ when logger.Contains("Realtime", StringComparison.OrdinalIgnoreCase) => ("northbound", "realtime_stream"),
            _ when logger.Contains("SignalR", StringComparison.OrdinalIgnoreCase) => ("northbound", "signalr"),
            _ when logger.Contains("LogCenter", StringComparison.OrdinalIgnoreCase) => ("observability", "log_center"),
            _ when logger.Contains("OpenObserve", StringComparison.OrdinalIgnoreCase) => ("observability", "openobserve"),
            _ => ("runtime", "general")
        };

        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["igw_category"] = "system",
            ["igw_module"] = module,
            ["igw_feature"] = feature,
            ["igw_event_kind"] = "runtime_log",
            ["igw_logger"] = logger,
            // OpenObserve schemas are inferred from received records. Keep every
            // user-queryable field present so optional MES/interface context does
            // not make the whole log query fail on a sparse installation.
            ["igw_channel"] = "",
            ["igw_device"] = "",
            ["igw_db"] = "",
            ["igw_task_id"] = "",
            ["igw_task_name"] = "",
            ["igw_task_ids"] = "",
            ["igw_task_names"] = "",
            ["igw_interface_name"] = "",
            ["igw_operation"] = "",
            ["igw_route"] = "",
            ["igw_target"] = "",
            ["igw_protocol"] = "",
            ["igw_trace_id"] = ""
        };
        if (logger.StartsWith("Device:", StringComparison.OrdinalIgnoreCase))
            fields["igw_device"] = logger["Device:".Length..];
        return fields;
    }
}

public sealed class LogContextLoggerProvider(ILoggerFactory destination) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) =>
        new ContextLogger(categoryName, destination.CreateLogger(categoryName), LogSourceContext.FieldsFor(categoryName));

    public void Dispose() { }

    private sealed class ContextLogger(
        string categoryName,
        ILogger inner,
        IReadOnlyDictionary<string, object?> defaults) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!inner.IsEnabled(logLevel)) return;
            // SignalR logs every deliberate HubException a second time through its dispatcher.
            // IGW already writes one structured request/stream summary with the operation and
            // outcome, so exporting this framework duplicate floods the operator log and can
            // expose the encoded response body carried by the protocol exception.
            if (categoryName.Equals("Microsoft.AspNetCore.SignalR.Internal.DefaultHubDispatcher", StringComparison.Ordinal)
                && exception?.GetType().FullName == "Microsoft.AspNetCore.SignalR.HubException"
                && exception.Message.StartsWith("igw:", StringComparison.Ordinal)) return;
            var values = new Dictionary<string, object?>(defaults, StringComparer.Ordinal);
            if (Activity.Current is { } activity)
                values["igw_trace_id"] = activity.TraceId.ToString();
            if (state is IEnumerable<KeyValuePair<string, object?>> structured)
                foreach (var pair in structured) values[pair.Key] = pair.Value;
            var enriched = new EnrichedState(values);
            inner.Log(logLevel, eventId, enriched, exception, (_, error) => formatter(state, error));
        }
    }

    private sealed class EnrichedState : IReadOnlyList<KeyValuePair<string, object?>>
    {
        private readonly KeyValuePair<string, object?>[] _values;
        public EnrichedState(IReadOnlyDictionary<string, object?> values) => _values = values.ToArray();
        public int Count => _values.Length;
        public KeyValuePair<string, object?> this[int index] => _values[index];
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() =>
            ((IEnumerable<KeyValuePair<string, object?>>)_values).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _values.GetEnumerator();
    }
}
