using System.Collections.Concurrent;
using System.Globalization;
using IGW.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace IGW.Core;

public sealed record HistoryRow(string Value, uint Quality, DateTime Ts);

/// <summary>
/// 历史记录 / 数据记录(对标 KEP「Data Logger」)。订阅 DataHub 变化 → 批量写入 SQLite。
/// 单写者(后台 flush 线程)+ 读写共用一把锁;按 RetentionRows 周期裁剪旧数据。
/// </summary>
public sealed class DataLogger : IAsyncDisposable
{
    private readonly HistorySettings _cfg;
    private readonly ILogger _log;
    private readonly IDisposable _sub;
    private readonly SqliteConnection _conn;
    private readonly object _dbLock = new();
    private readonly ConcurrentQueue<(string tag, string val, uint q, DateTime ts)> _queue = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _flusher;
    private int _flushCount;
    private const int MaxQueue = 200_000;   // 内存队列上限:写不动时丢最旧(有丢弃计数),绝不无界涨到 OOM
    private long _qCount;                    // 近似队列长度(Interlocked)
    private long _dropped;                   // 累计丢弃(供日志/诊断)
    private int _failStreak;                 // 连续落盘失败次数(升级告警)

    public string DbFile { get; }

    public DataLogger(DataHub hub, HistorySettings cfg, string baseDir, ILogger log)
    {
        _cfg = cfg; _log = log;
        DbFile = Path.IsPathRooted(cfg.DbPath) ? cfg.DbPath : Path.Combine(baseDir, cfg.DbPath);
        _conn = new SqliteConnection($"Data Source={DbFile}");
        _conn.Open();
        try
        {
            IgwSqliteTableNamespace.Ensure(_conn, ["samples"], new Dictionary<string, IReadOnlyCollection<string>>
            {
                ["samples"] = ["id", "tag", "val", "q", "ts"]
            });
        }
        catch { _conn.Dispose(); _cts.Dispose(); throw; }
        Exec("PRAGMA journal_mode=WAL;");
        Exec("CREATE TABLE IF NOT EXISTS IGW_samples(id INTEGER PRIMARY KEY AUTOINCREMENT, tag TEXT NOT NULL, val TEXT, q INTEGER, ts TEXT);");
        Exec("CREATE INDEX IF NOT EXISTS ix_tag ON IGW_samples(tag, id);");
        // 被动记录:每点变化都记，但本身不构成采集需求。历史记录一旦登记通配需求，
        // active 模式下几十万点会全部变成活动点持续轮询，分层采集与按需扫描都会失效。
        _sub = hub.SubscribeAll(OnChange, "history", createsDemand: false);
        _flusher = Task.Run(FlushLoopAsync);
        _log.LogInformation("数据记录已启用 → {path}(每 {ms}ms 落盘)", DbFile, cfg.FlushIntervalMs);
    }

    private ValueTask OnChange(TagChange c)
    {
        if (Interlocked.Read(ref _qCount) >= MaxQueue)   // 满了 → 丢最旧,保新数据;每 10 万条丢弃报一次
        {
            if (_queue.TryDequeue(out _))
            {
                Interlocked.Decrement(ref _qCount);
                long d = Interlocked.Increment(ref _dropped);
                if (d % 100_000 == 1) _log.LogError("历史记录写入跟不上,内存队列已满,累计丢弃 {n} 条(检查磁盘/降低记录点数/加死区)", d);
            }
        }
        var v = c.Value;
        _queue.Enqueue((c.TagId, v.Value?.ToString() ?? "", v.Quality.Code,
            v.SourceTimestamp == default ? DateTime.UtcNow : v.SourceTimestamp));
        Interlocked.Increment(ref _qCount);
        return ValueTask.CompletedTask;
    }

    private async Task FlushLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { await Task.Delay(Math.Max(250, _cfg.FlushIntervalMs), _cts.Token); }
            catch (OperationCanceledException) { break; }
            try
            {
                Flush();
                _failStreak = 0;
            }
            catch (Exception ex)
            {
                // 持续失败(磁盘满/文件被锁)要越来越响,不能永远只是一条 warning
                if (++_failStreak % 30 == 5)
                    _log.LogError(ex, "历史落盘已连续失败 {n} 次(队列积压 {q} 条,累计丢弃 {d}):检查磁盘空间/history.db 是否被占用", _failStreak, Interlocked.Read(ref _qCount), Interlocked.Read(ref _dropped));
                else
                    _log.LogWarning("历史落盘失败:{m}", ex.Message);
            }
        }
        try { Flush(); } catch { }   // 退出前最后一次
    }

    private void Flush()
    {
        // 循环排空(每事务 1 万行,最多 20 轮/周期):排水能力跟上入队速率,不再固定 1 万/周期
        for (int round = 0; round < 20 && !_queue.IsEmpty; round++)
        {
            lock (_dbLock)
            {
                using var tx = _conn.BeginTransaction();
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "INSERT INTO IGW_samples(tag,val,q,ts) VALUES($t,$v,$q,$ts)";
                var pt = AddParam(cmd, "$t"); var pv = AddParam(cmd, "$v"); var pq = AddParam(cmd, "$q"); var pts = AddParam(cmd, "$ts");
                int n = 0;
                while (n < 10000 && _queue.TryDequeue(out var r))
                {
                    Interlocked.Decrement(ref _qCount);
                    pt.Value = r.tag; pv.Value = r.val; pq.Value = (long)r.q; pts.Value = r.ts.ToString("o", CultureInfo.InvariantCulture);
                    cmd.ExecuteNonQuery(); n++;
                }
                tx.Commit();
                if (++_flushCount % 10 == 0) Trim();
            }
        }
    }

    private void Trim()
    {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM IGW_samples WHERE id <= (SELECT MAX(id) FROM IGW_samples) - $keep";
            AddParam(cmd, "$keep").Value = (long)Math.Max(1000, _cfg.RetentionRows);
            cmd.ExecuteNonQuery();
        }
        catch { }
    }

    /// <summary>查询某点最近 limit 条历史(最新在前)。</summary>
    public IReadOnlyList<HistoryRow> Query(string tagId, int limit)
    {
        lock (_dbLock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT val,q,ts FROM IGW_samples WHERE tag=$t ORDER BY id DESC LIMIT $n";
            AddParam(cmd, "$t").Value = tagId;
            AddParam(cmd, "$n").Value = (long)Math.Clamp(limit, 1, 10000);
            var list = new List<HistoryRow>();
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
                list.Add(new HistoryRow(
                    rd.IsDBNull(0) ? "" : rd.GetString(0),
                    (uint)rd.GetInt64(1),
                    DateTime.Parse(rd.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
            return list;
        }
    }

    /// <summary>历史总行数(诊断用)。</summary>
    public long Count()
    {
        lock (_dbLock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM IGW_samples";
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
    }

    private static SqliteParameter AddParam(SqliteCommand cmd, string name)
    {
        var p = cmd.CreateParameter(); p.ParameterName = name; cmd.Parameters.Add(p); return p;
    }
    private void Exec(string sql) { using var c = _conn.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }

    public async ValueTask DisposeAsync()
    {
        try { _sub.Dispose(); } catch { }
        _cts.Cancel();
        try { await _flusher; } catch { }
        lock (_dbLock) { try { _conn.Close(); _conn.Dispose(); } catch { } }
        _cts.Dispose();
    }
}
