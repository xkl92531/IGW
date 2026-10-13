using System.Collections.Concurrent;
using IGW.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace IGW.Northbound.Mes;

public sealed record MesLogRow(string Ts, string Task, string Step, bool Ok, int Http, string Msg);

/// <summary>
/// MES 上报日志(SQLite):每次上报结果落库,重启不丢、可查(对标 DCS SysLog)。
/// 后台批量写 + 周期裁剪;读写共用一把锁。
/// </summary>
public sealed class MesLog : IAsyncDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _dbLock = new();
    private readonly ConcurrentQueue<(string ts, string task, string step, bool ok, int http, string msg)> _queue = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _flusher;
    private readonly int _retain;
    private readonly int _queueCap;   // 内存队列上限(盘满/被锁时丢最旧,不无界涨)
    private int _flushCount;
    private long _qCount;

    public string DbFile { get; }

    public MesLog(string dbPath, int retainRows, int queueCap = 10_000)
    {
        DbFile = Path.IsPathRooted(dbPath) ? dbPath : Path.Combine(AppContext.BaseDirectory, dbPath);
        _retain = Math.Max(1000, retainRows);
        _queueCap = Math.Max(1000, queueCap);
        _conn = new SqliteConnection($"Data Source={DbFile}");
        _conn.Open();
        try
        {
            IgwSqliteTableNamespace.Ensure(_conn, ["mes_log"], new Dictionary<string, IReadOnlyCollection<string>>
            {
                ["mes_log"] = ["id", "ts", "task", "step", "ok", "http", "msg"]
            });
        }
        catch { _conn.Dispose(); _cts.Dispose(); throw; }
        Exec("PRAGMA journal_mode=WAL;");
        Exec("CREATE TABLE IF NOT EXISTS IGW_mes_log(id INTEGER PRIMARY KEY AUTOINCREMENT, ts TEXT, task TEXT, step TEXT, ok INTEGER, http INTEGER, msg TEXT);");
        _flusher = Task.Run(FlushLoopAsync);
    }

    public void Add(string task, string step, bool ok, int http, string msg)
    {
        if (Interlocked.Read(ref _qCount) >= _queueCap && _queue.TryDequeue(out _))   // 满了丢最旧
            Interlocked.Decrement(ref _qCount);
        _queue.Enqueue((DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), task, step, ok, http, msg ?? ""));
        Interlocked.Increment(ref _qCount);
    }

    private async Task FlushLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { await Task.Delay(1000, _cts.Token); } catch { break; }
            try { Flush(); } catch { }
        }
        try { Flush(); } catch { }
    }

    private void Flush()
    {
        if (_queue.IsEmpty) return;
        lock (_dbLock)
        {
            using var tx = _conn.BeginTransaction();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT INTO IGW_mes_log(ts,task,step,ok,http,msg) VALUES($ts,$t,$s,$ok,$h,$m)";
            var pts = AddParam(cmd, "$ts"); var pt = AddParam(cmd, "$t"); var ps = AddParam(cmd, "$s");
            var pok = AddParam(cmd, "$ok"); var ph = AddParam(cmd, "$h"); var pm = AddParam(cmd, "$m");
            int n = 0;
            while (n < 5000 && _queue.TryDequeue(out var r))
            { Interlocked.Decrement(ref _qCount); pts.Value = r.ts; pt.Value = r.task; ps.Value = r.step; pok.Value = r.ok ? 1L : 0L; ph.Value = (long)r.http; pm.Value = r.msg; cmd.ExecuteNonQuery(); n++; }
            tx.Commit();
            if (++_flushCount % 10 == 0) Trim();
        }
    }

    private void Trim()
    {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM IGW_mes_log WHERE id <= (SELECT MAX(id) FROM IGW_mes_log) - $k";
            AddParam(cmd, "$k").Value = (long)_retain;
            cmd.ExecuteNonQuery();
        }
        catch { }
    }

    public IReadOnlyList<MesLogRow> Query(int limit)
    {
        lock (_dbLock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT ts,task,step,ok,http,msg FROM IGW_mes_log ORDER BY id DESC LIMIT $n";
            AddParam(cmd, "$n").Value = (long)Math.Clamp(limit, 1, 5000);
            var list = new List<MesLogRow>();
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
                list.Add(new MesLogRow(rd.GetString(0), rd.GetString(1), rd.GetString(2), rd.GetInt64(3) != 0, (int)rd.GetInt64(4), rd.IsDBNull(5) ? "" : rd.GetString(5)));
            return list;
        }
    }

    private static SqliteParameter AddParam(SqliteCommand cmd, string name) { var p = cmd.CreateParameter(); p.ParameterName = name; cmd.Parameters.Add(p); return p; }
    private void Exec(string sql) { using var c = _conn.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _flusher; } catch { }
        lock (_dbLock) { try { _conn.Close(); _conn.Dispose(); } catch { } }
        _cts.Dispose();
    }
}
