using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace IGW.Tests;

internal class TelemetryTestLogger : ILogger
{
    private readonly ConcurrentQueue<IReadOnlyDictionary<string, object?>> _records = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (state is IEnumerable<KeyValuePair<string, object?>> values)
            _records.Enqueue(values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
    }

    public IReadOnlyList<IReadOnlyDictionary<string, object?>> ByCategory(string category) =>
        _records.Where(record => record.TryGetValue("igw_category", out object? value)
            && string.Equals(value?.ToString(), category, StringComparison.OrdinalIgnoreCase)).ToArray();
}

internal sealed class TelemetryTestLogger<T> : TelemetryTestLogger, ILogger<T>;
