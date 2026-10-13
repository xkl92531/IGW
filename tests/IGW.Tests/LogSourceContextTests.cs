using System.Diagnostics;
using IGW.Core;
using IGW.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.SignalR;
using Xunit;

namespace IGW.Tests;

public sealed class LogSourceContextTests
{
    [Fact]
    public void Ordinary_device_logger_record_receives_readable_source_context()
    {
        var sink = new CapturingProvider();
        using var destination = LoggerFactory.Create(logging => logging.AddProvider(sink));
        using var provider = new LogContextLoggerProvider(destination);

        provider.CreateLogger("Device:PLC8.OP140").LogWarning("读取失败：{reason}", "timeout");

        IReadOnlyDictionary<string, object?> record = Assert.Single(sink.Records);
        Assert.Equal("system", record["igw_category"]);
        Assert.Equal("acquisition", record["igw_module"]);
        Assert.Equal("device_loop", record["igw_feature"]);
        Assert.Equal("runtime_log", record["igw_event_kind"]);
        Assert.Equal("Device:PLC8.OP140", record["igw_logger"]);
        Assert.Equal("PLC8.OP140", record["igw_device"]);
        Assert.Equal("", record["igw_channel"]);
        Assert.Equal("", record["igw_db"]);
        Assert.Equal("", record["igw_task_id"]);
        Assert.Equal("", record["igw_task_name"]);
        Assert.Equal("", record["igw_task_ids"]);
        Assert.Equal("", record["igw_task_names"]);
        Assert.Equal("", record["igw_interface_name"]);
        Assert.Equal("", record["igw_operation"]);
        Assert.Equal("", record["igw_route"]);
        Assert.Equal("", record["igw_target"]);
        Assert.Equal("", record["igw_protocol"]);
    }

    [Fact]
    public void Logger_record_carries_the_current_trace_id_in_a_stable_query_field()
    {
        var sink = new CapturingProvider();
        using var destination = LoggerFactory.Create(logging => logging.AddProvider(sink));
        using var provider = new LogContextLoggerProvider(destination);
        using var activity = new Activity("log-test").Start();

        provider.CreateLogger("GatewayHost").LogInformation("启动完成");

        IReadOnlyDictionary<string, object?> record = Assert.Single(sink.Records);
        Assert.Equal(activity.TraceId.ToString(), record["igw_trace_id"]);
    }

    [Fact]
    public void Explicit_business_context_overrides_logger_defaults()
    {
        var sink = new CapturingProvider();
        using var destination = LoggerFactory.Create(logging => logging.AddProvider(sink));
        using var provider = new LogContextLoggerProvider(destination);
        ILogger logger = provider.CreateLogger("MesUpload");

        GatewayTelemetry.Log(logger, LogLevel.Information, "interface", "MES 请求完成",
            new Dictionary<string, object?>
            {
                ["module"] = "mes",
                ["feature"] = "http_upload",
                ["eventKind"] = "mes_transport"
            });

        IReadOnlyDictionary<string, object?> record = Assert.Single(sink.Records);
        Assert.Equal("interface", record["igw_category"]);
        Assert.Equal("mes", record["igw_module"]);
        Assert.Equal("http_upload", record["igw_feature"]);
        Assert.Equal("mes_transport", record["igw_event_kind"]);
        Assert.Equal("MesUpload", record["igw_logger"]);
    }

    [Fact]
    public void Deliberate_SignalR_protocol_errors_are_not_exported_as_duplicate_framework_exceptions()
    {
        var sink = new CapturingProvider();
        using var destination = LoggerFactory.Create(logging => logging.AddProvider(sink));
        using var provider = new LogContextLoggerProvider(destination);
        ILogger dispatcher = provider.CreateLogger("Microsoft.AspNetCore.SignalR.Internal.DefaultHubDispatcher");

        dispatcher.LogError(new HubException("igw:503:realtime_stream_unavailable"), "Failed while sending stream items");
        dispatcher.LogError(new InvalidOperationException("unexpected"), "Unexpected dispatcher failure");

        IReadOnlyDictionary<string, object?> record = Assert.Single(sink.Records);
        Assert.Equal("Microsoft.AspNetCore.SignalR.Internal.DefaultHubDispatcher", record["igw_logger"]);
    }

    private sealed class CapturingProvider : ILoggerProvider
    {
        public List<IReadOnlyDictionary<string, object?>> Records { get; } = [];
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Records);
        public void Dispose() { }
    }

    private sealed class CapturingLogger(List<IReadOnlyDictionary<string, object?>> records) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
                records.Add(values.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal));
        }
    }
}
