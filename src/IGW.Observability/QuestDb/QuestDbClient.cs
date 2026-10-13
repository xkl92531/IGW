using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace IGW.Observability;

/// <summary>Only talks to the private local database. Never used on PLC caller threads.</summary>
public sealed class QuestDbClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _queries = new(3, 3);
    private readonly SemaphoreSlim _operations = new(6, 6);
    private readonly SemaphoreSlim _initialization = new(1, 1);

    public QuestDbClient(Uri endpoint, string username, string password, HttpMessageHandler? handler = null)
    {
        if (endpoint.Scheme != "http" || endpoint.Host != "127.0.0.1")
            throw new ArgumentException("QuestDB 必须使用本机回环 HTTP 地址。", nameof(endpoint));
        _http = handler is null ? new HttpClient(new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 6, ConnectTimeout = TimeSpan.FromSeconds(2),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5), UseProxy = false
        }) : new HttpClient(handler);
        _http.BaseAddress = endpoint;
        _http.Timeout = TimeSpan.FromSeconds(10);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password)));
    }

    public async Task<JsonDocument> QueryAsync(string sql, CancellationToken cancellationToken)
    {
        await _queries.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool acquired = false;
        try
        {
            await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            using var response = await _http.GetAsync("exec?query=" + Uri.EscapeDataString(sql),
                HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"QuestDB 查询失败 (HTTP {(int)response.StatusCode})。");
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (document.RootElement.TryGetProperty("error", out _))
            {
                document.Dispose();
                throw new InvalidOperationException("QuestDB SQL 执行失败。");
            }
            return document;
        }
        finally { if (acquired) _operations.Release(); _queries.Release(); }
    }

    public async Task WriteAsync(byte[] batch, CancellationToken cancellationToken)
    {
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
        using var body = new ByteArrayContent(PhysicalMeasurements(batch));
        body.Headers.ContentType = new MediaTypeHeaderValue("text/plain") { CharSet = "utf-8" };
        using var response = await _http.PostAsync("write?precision=us", body, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new QuestDbWriteException((int)response.StatusCode);
        }
        finally { _operations.Release(); }
    }

    public async Task InitializeAsync(int retentionDays, CancellationToken cancellationToken)
    {
        ValidateRetention(retentionDays);
        await _initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureTableNamespaceAsync(cancellationToken).ConfigureAwait(false);
            string fields = string.Join(",", QuestDbLogCodec.Fields.Select(field => field +
                (QuestDbLogCodec.IndexedFields.Contains(field) ? " SYMBOL NOCACHE INDEX" : " VARCHAR")));
            using var logs = await QueryAsync($"create table if not exists IGW_logs (ts TIMESTAMP,id UUID,level INT,category SYMBOL INDEX,raw VARCHAR,{fields}) timestamp(ts) partition by HOUR ttl {retentionDays} DAYS WAL DEDUP UPSERT KEYS(ts,id)", cancellationToken);
            using var payloads = await QueryAsync($"create table if not exists IGW_payloads (ts TIMESTAMP,id UUID,content_type VARCHAR,body VARCHAR) timestamp(ts) partition by HOUR ttl {retentionDays} DAYS WAL DEDUP UPSERT KEYS(ts,id)", cancellationToken);
            await SetRetentionAsync(retentionDays, cancellationToken).ConfigureAwait(false);
        }
        finally { _initialization.Release(); }
    }

    private async Task EnsureTableNamespaceAsync(CancellationToken cancellationToken)
    {
        using JsonDocument catalog = await QueryAsync("select table_name,designatedTimestamp,partitionBy,walEnabled from tables()", cancellationToken).ConfigureAwait(false);
        var plans = new List<(string Current, string Target, string Temporary)>();
        foreach (string target in new[] { "IGW_logs", "IGW_payloads" })
        {
            string temporary = target + "_namespace_migration";
            JsonElement[] matches = catalog.RootElement.GetProperty("dataset").EnumerateArray().Where(row =>
                string.Equals(row[0].GetString(), target, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(row[0].GetString(), temporary, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length > 1) throw new InvalidOperationException($"QuestDB 表名迁移冲突: {target}。");
            if (matches.Length == 0) continue;
            JsonElement table = matches[0];
            string current = table[0].GetString()!;
            if (table[1].GetString() != "ts" || table[2].GetString() != "HOUR" || !table[3].GetBoolean())
                throw new InvalidOperationException($"QuestDB 表结构与 IGW 日志不兼容: {current}。");
            using JsonDocument schema = await QueryAsync($"select \"column\",type,upsertKey from table_columns('{current}')", cancellationToken).ConfigureAwait(false);
            var columns = schema.RootElement.GetProperty("dataset").EnumerateArray().ToDictionary(row => row[0].GetString()!, row => (Type: row[1].GetString(), Key: row[2].GetBoolean()), StringComparer.Ordinal);
            var expected = new Dictionary<string, string> { ["ts"] = "TIMESTAMP", ["id"] = "UUID" };
            if (target == "IGW_logs")
            {
                expected["level"] = "INT"; expected["category"] = "SYMBOL"; expected["raw"] = "VARCHAR";
                foreach (string field in QuestDbLogCodec.Fields) expected[field] = QuestDbLogCodec.IndexedFields.Contains(field) ? "SYMBOL" : "VARCHAR";
            }
            else { expected["content_type"] = "VARCHAR"; expected["body"] = "VARCHAR"; }
            if (expected.Any(column => !columns.TryGetValue(column.Key, out var actual) || actual.Type != column.Value || actual.Key != (column.Key is "ts" or "id")) ||
                columns.Any(column => column.Value.Key && column.Key is not ("ts" or "id")))
                throw new InvalidOperationException($"QuestDB 表字段或去重键与 IGW 日志不兼容: {current}。");
            plans.Add((current, target, temporary));
        }
        // Validate both stores before any mutation. QuestDB names are case insensitive;
        // a two-step metadata rename keeps the table ID, partitions, WAL and UUID references.
        // A stopped second step is resumed from the whitelisted temporary name on startup.
        foreach (var plan in plans)
        {
            if (plan.Current == plan.Target) continue;
            if (!plan.Current.Equals(plan.Temporary, StringComparison.OrdinalIgnoreCase))
            {
                using var renamed = await QueryAsync($"rename table {plan.Current} to {plan.Temporary}", cancellationToken).ConfigureAwait(false);
            }
            using var completed = await QueryAsync($"rename table {plan.Temporary} to {plan.Target}", cancellationToken).ConfigureAwait(false);
        }
    }

    private static byte[] PhysicalMeasurements(byte[] batch)
    {
        byte[] result = batch;
        for (int offset = 0; offset < batch.Length;)
        {
            ReadOnlySpan<byte> line = batch.AsSpan(offset);
            if (line.StartsWith("igw_logs "u8) || line.StartsWith("igw_payloads "u8))
            {
                if (ReferenceEquals(result, batch)) result = (byte[])batch.Clone();
                result[offset] = (byte)'I'; result[offset + 1] = (byte)'G'; result[offset + 2] = (byte)'W';
            }
            int end = Array.IndexOf(batch, (byte)'\n', offset);
            if (end < 0) break;
            offset = end + 1;
        }
        return result;
    }

    public async Task SetRetentionAsync(int days, CancellationToken cancellationToken, int? previousDays = null)
    {
        ValidateRetention(days);
        try
        {
        using var logs = await QueryAsync($"alter table IGW_logs set ttl {days} DAYS", cancellationToken);
        using var payloads = await QueryAsync($"alter table IGW_payloads set ttl {days} DAYS", cancellationToken);
        }
        catch when (previousDays is int previous && previous != days)
        {
            try { await SetRetentionAsync(previousDays.Value, CancellationToken.None).ConfigureAwait(false); }
            catch { throw new IOException("日志保留期修改失败，数据库保留期恢复也失败，请检查数据库状态。"); }
            throw;
        }
    }

    private static void ValidateRetention(int days)
    {
        if (days is < 1 or > 3650) throw new ArgumentOutOfRangeException(nameof(days));
    }

    public async Task<IDisposable> PauseAsync(CancellationToken cancellationToken)
    {
        int count = 0;
        try
        {
            while (count < 6) { await _operations.WaitAsync(cancellationToken).ConfigureAwait(false); count++; }
            return new Lease(_operations);
        }
        catch { if (count > 0) _operations.Release(count); throw; }
    }

    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) semaphore.Release(6); }
    }
    public void Dispose() { _http.Dispose(); _queries.Dispose(); _operations.Dispose(); _initialization.Dispose(); }
}

public sealed class QuestDbWriteException(int statusCode) : Exception($"QuestDB 批量写入失败 (HTTP {statusCode})。")
{
    public int StatusCode { get; } = statusCode;
    public bool Permanent => StatusCode is 400 or 413 or 422;
}
