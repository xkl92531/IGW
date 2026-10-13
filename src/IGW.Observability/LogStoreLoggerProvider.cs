using System.Globalization;
using Microsoft.Extensions.Logging;

namespace IGW.Observability;

/// <summary>
/// 把日志交给非阻塞存储队列，PLC 调用线程不等待磁盘或数据库。
/// 上下文字段来自 <see cref="LogContextLoggerProvider"/> 补充的默认值、日志参数以及作用域(如 MES 任务)。
/// </summary>
public sealed class LogStoreLoggerProvider(ILogRecordSink store, TimeProvider? time = null) : ILoggerProvider, ISupportExternalScope
{
    private const int MaxExceptionLength = 32 * 1024;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private IExternalScopeProvider? _scopes;

    public ILogger CreateLogger(string categoryName) => new StoreLogger(this);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose() { }

    private sealed class StoreLogger(LogStoreLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            owner._scopes?.Push(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            string message;
            try { message = formatter(state, exception); }
            catch (Exception error) { message = "(日志格式化失败：" + error.Message + ")"; }

            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
                foreach (var (key, value) in values) Add(fields, key, value, overwrite: true);
            // 作用域(如 MES 任务号)只补充缺失或为空的字段，日志自身带的值优先。
            owner._scopes?.ForEachScope((scope, target) =>
            {
                if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
                    foreach (var (key, value) in pairs) Add(target, key, value, overwrite: false);
            }, fields);
            if (exception is not null)
            {
                fields["exception_type"] = exception.GetType().FullName;
                fields["exception_message"] = exception.Message;
                string detail = exception.ToString();
                fields["exception_stacktrace"] = detail.Length > MaxExceptionLength ? detail[..MaxExceptionLength] : detail;
            }
            string category = fields.TryGetValue("igw_category", out object? raw) && raw is string text && text.Length > 0
                ? text.ToLowerInvariant() : "system";
            fields.Remove("igw_category");
            owner.Append(new LogRecord(
                LogRecordCodec.ToMicros(owner._time.GetUtcNow()),
                LogRecordCodec.FromLogLevel(logLevel),
                category,
                message,
                fields));
        }

        private static void Add(Dictionary<string, object?> fields, string key, object? value, bool overwrite)
        {
            if (key == "{OriginalFormat}" || value is null) return;
            object? normalized = value switch
            {
                string text => text,
                bool or int or long or short or byte or uint or ulong or ushort or sbyte or double or float or decimal => value,
                DateTime time => time.ToString("O", CultureInfo.InvariantCulture),
                DateTimeOffset time => time.ToString("O", CultureInfo.InvariantCulture),
                Enum => value.ToString(),
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString()
            };
            if (normalized is string empty && empty.Length == 0) return;
            if (overwrite || !fields.TryGetValue(key, out object? existing) || existing is null or "")
                fields[key] = normalized;
        }
    }

    private void Append(LogRecord record) => store.Append(record);
}
