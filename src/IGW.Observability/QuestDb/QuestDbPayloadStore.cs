using System.Globalization;
using System.Text.Json;

namespace IGW.Observability;

public sealed record QuestDbPayload(string ContentType, string Content);

public sealed class QuestDbPayloadStore(QuestDbClient client, QuestDbLogStore store)
{
    public string Save(string contentType, string content)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid id = Guid.CreateVersion7(now);
        if (!store.AppendPayload(id, LogRecordCodec.ToMicros(now), contentType, content))
            throw new IOException("报文未保存：日志缓存已满、正在关闭或报文超过 16M 字符。");
        return id.ToString("N");
    }

    public async Task<QuestDbPayload?> ReadAsync(string id, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(id, "N", out Guid guid) || id[12] != '7') return null; // legacy v4 IDs remain file-backed
        long milliseconds = long.Parse(id[..12], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        long from = (milliseconds - 1000) * 1000, to = (milliseconds + 1000) * 1000;
        string sql = $"select content_type,body from IGW_payloads where ts >= cast({from} as timestamp) and ts < cast({to} as timestamp) and id='{guid}' limit 1";
        for (int attempt = 0; attempt < 20; attempt++)
        {
            using JsonDocument result = await client.QueryAsync(sql, cancellationToken).ConfigureAwait(false);
            JsonElement rows = result.RootElement.GetProperty("dataset");
            if (rows.GetArrayLength() > 0)
                return new QuestDbPayload(JsonSerializer.Deserialize<string>(rows[0][0].GetString()!)!, JsonSerializer.Deserialize<string>(rows[0][1].GetString()!)!);
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    public async Task CleanupCapacityAsync(long maxBytes, CancellationToken cancellationToken)
    {
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        using JsonDocument partitions = await client.QueryAsync("select minTimestamp,diskSize from table_partitions('IGW_payloads') where minTimestamp != null order by minTimestamp", cancellationToken);
        JsonElement[] rows = partitions.RootElement.GetProperty("dataset").EnumerateArray().ToArray();
        long remaining = rows.Sum(row => row[1].GetInt64());
        if (remaining <= maxBytes || rows.Length < 2) return;
        DateTimeOffset? cutoff = null;
        for (int index = 0; index < rows.Length - 1; index++)
        {
            remaining -= rows[index][1].GetInt64();
            DateTimeOffset next = rows[index + 1][0].GetDateTimeOffset().ToUniversalTime();
            cutoff = new DateTimeOffset(next.Year, next.Month, next.Day, next.Hour, 0, 0, TimeSpan.Zero);
            if (remaining <= maxBytes / 10 * 8) break;
        }
        if (cutoff is not null)
        {
            using var dropped = await client.QueryAsync("alter table IGW_payloads drop partition where ts < " + QuestDbLogQuery.Literal(cutoff.Value.ToString("O")), cancellationToken);
        }
    }
}
