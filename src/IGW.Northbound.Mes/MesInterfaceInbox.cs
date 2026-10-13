using IGW.Core;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace IGW.Northbound.Mes;

internal sealed record MesInboxMessage(long Sequence, string Id, string Json, bool Completed, string? Ack);
internal sealed class MesInboxFullException() : InvalidOperationException("监听待处理队列已满，暂停接收，未丢弃已有消息。");

/// <summary>Listener journal is separate from controller results; flow failures never discard incoming messages.</summary>
internal sealed class MesInterfaceInbox : IDisposable
{
    private readonly object _gate = new();
    private readonly SqliteConnection _db;
    internal MesInterfaceInbox(string path)
    {
        if (path != ":memory:") Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        try
        {
            IgwSqliteTableNamespace.Ensure(_db,
                ["interface_inbox", "interface_state", "interface_bodies", "interface_configuration"],
                new Dictionary<string, IReadOnlyCollection<string>>
                {
                    ["interface_inbox"] = ["sequence", "scope", "id", "json", "completed", "ack"],
                    ["interface_state"] = ["scope", "baseline", "last_event_id"],
                    ["interface_bodies"] = ["sequence", "step", "body"],
                    ["interface_configuration"] = ["scope", "signature"]
                });
        }
        catch { _db.Dispose(); throw; }
        using var command = _db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS IGW_interface_inbox (
              sequence INTEGER PRIMARY KEY AUTOINCREMENT, scope TEXT NOT NULL, id TEXT NOT NULL,
              json TEXT NOT NULL, completed INTEGER NOT NULL DEFAULT 0, ack TEXT, UNIQUE(scope,id));
            CREATE INDEX IF NOT EXISTS ix_interface_pending ON IGW_interface_inbox(scope,completed,sequence);
            CREATE TABLE IF NOT EXISTS IGW_interface_state (scope TEXT PRIMARY KEY, baseline TEXT, last_event_id TEXT);
            CREATE TABLE IF NOT EXISTS IGW_interface_bodies (sequence INTEGER NOT NULL, step TEXT NOT NULL, body TEXT NOT NULL,
              PRIMARY KEY(sequence,step));
            CREATE TABLE IF NOT EXISTS IGW_interface_configuration (scope TEXT PRIMARY KEY, signature TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
    }
    internal MesInboxMessage Append(string scope, string id, string json, string? eventId, int capacity)
    {
        using var parsed = JsonDocument.Parse(json);
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128) throw new InvalidDataException("监听消息必须有 1–128 字符的唯一编号。");
        lock (_gate)
        {
            using var find = _db.CreateCommand();
            find.CommandText = "SELECT sequence,id,json,completed,ack FROM IGW_interface_inbox WHERE scope=$s AND id=$id";
            find.Parameters.AddWithValue("$s", scope); find.Parameters.AddWithValue("$id", id);
            using (var reader = find.ExecuteReader())
                if (reader.Read())
                {
                    var existing = Read(reader);
                    using var previous = JsonDocument.Parse(existing.Json);
                    if (!JsonElement.DeepEquals(parsed.RootElement, previous.RootElement))
                        throw new InvalidDataException("相同监听消息编号的内容发生冲突，拒绝覆盖。");
                    return existing;
                }
            using var count = _db.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM IGW_interface_inbox WHERE scope=$s AND completed=0";
            count.Parameters.AddWithValue("$s", scope);
            if ((long)count.ExecuteScalar()! >= Math.Max(1, capacity)) throw new MesInboxFullException();
            using var transaction = _db.BeginTransaction();
            using var insert = _db.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO IGW_interface_inbox(scope,id,json) VALUES($s,$id,$json);
                SELECT last_insert_rowid();
                """;
            insert.Parameters.AddWithValue("$s", scope); insert.Parameters.AddWithValue("$id", id); insert.Parameters.AddWithValue("$json", json);
            long sequence = (long)insert.ExecuteScalar()!;
            using var state = _db.CreateCommand(); state.Transaction = transaction;
            state.CommandText = """
                INSERT INTO IGW_interface_state(scope,last_event_id) VALUES($s,$event)
                ON CONFLICT(scope) DO UPDATE SET last_event_id=COALESCE($event,last_event_id);
                """;
            state.Parameters.AddWithValue("$s", scope); state.Parameters.AddWithValue("$event", (object?)eventId ?? DBNull.Value);
            state.ExecuteNonQuery(); transaction.Commit();
            return new(sequence, id, json, false, null);
        }
    }
    internal MesInboxMessage? Pending(string scope)
    {
        lock (_gate)
        {
            using var command = _db.CreateCommand();
            command.CommandText = "SELECT sequence,id,json,completed,ack FROM IGW_interface_inbox WHERE scope=$s AND completed=0 ORDER BY sequence LIMIT 1";
            command.Parameters.AddWithValue("$s", scope);
            using var reader = command.ExecuteReader(); return reader.Read() ? Read(reader) : null;
        }
    }
    internal MesInboxMessage? LastCommitted(string scope)
    {
        lock (_gate)
        {
            using var command = _db.CreateCommand();
            command.CommandText = "SELECT sequence,id,json,completed,ack FROM IGW_interface_inbox WHERE scope=$s AND completed=1 ORDER BY sequence DESC LIMIT 1";
            command.Parameters.AddWithValue("$s", scope);
            using var reader = command.ExecuteReader(); return reader.Read() ? Read(reader) : null;
        }
    }
    internal string? Baseline(string scope) => State(scope, "baseline");
    internal void Configure(string scope, string signature)
    {
        lock (_gate)
        {
            using var read = _db.CreateCommand();
            read.CommandText = "SELECT signature FROM IGW_interface_configuration WHERE scope=$s";
            read.Parameters.AddWithValue("$s", scope);
            string? old = read.ExecuteScalar() as string;
            if (old == signature) return;
            if (old is not null && Pending(scope) is not null)
                throw new InvalidOperationException("监听队列仍有待处理消息，不能更改来源、业务映射或流程；请恢复原配置并处理积压。");
            using var write = _db.CreateCommand();
            write.CommandText = "INSERT INTO IGW_interface_configuration(scope,signature) VALUES($s,$sig) ON CONFLICT(scope) DO UPDATE SET signature=$sig";
            write.Parameters.AddWithValue("$s", scope); write.Parameters.AddWithValue("$sig", signature); write.ExecuteNonQuery();
        }
    }
    internal string? LastEventId(string scope) => State(scope, "last_event_id");
    private string? State(string scope, string column)
    {
        lock (_gate)
        {
            using var command = _db.CreateCommand(); command.CommandText = $"SELECT {column} FROM IGW_interface_state WHERE scope=$s";
            command.Parameters.AddWithValue("$s", scope); return command.ExecuteScalar() as string;
        }
    }
    internal void Commit(long sequence, string baseline, string? ack)
    {
        lock (_gate)
        {
            using var transaction = _db.BeginTransaction();
            using var command = _db.CreateCommand(); command.Transaction = transaction;
            command.CommandText = """
                UPDATE IGW_interface_state SET baseline=$json WHERE scope=(SELECT scope FROM IGW_interface_inbox WHERE sequence=$seq);
                UPDATE IGW_interface_inbox SET completed=1,ack=$ack WHERE sequence=$seq;
                """;
            command.Parameters.AddWithValue("$seq", sequence); command.Parameters.AddWithValue("$json", baseline);
            command.Parameters.AddWithValue("$ack", (object?)ack ?? DBNull.Value); command.ExecuteNonQuery(); transaction.Commit();
        }
    }
    internal string FreezeBody(long sequence, string step, string body)
    {
        lock (_gate)
        {
            using var command = _db.CreateCommand();
            command.CommandText = """
                INSERT INTO IGW_interface_bodies(sequence,step,body) VALUES($seq,$step,$body) ON CONFLICT DO NOTHING;
                SELECT body FROM IGW_interface_bodies WHERE sequence=$seq AND step=$step;
                """;
            command.Parameters.AddWithValue("$seq", sequence); command.Parameters.AddWithValue("$step", step);
            command.Parameters.AddWithValue("$body", body); return (string)command.ExecuteScalar()!;
        }
    }
    private static MesInboxMessage Read(SqliteDataReader reader) => new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
        reader.GetInt64(3) != 0, reader.IsDBNull(4) ? null : reader.GetString(4));
    public void Dispose() { lock (_gate) _db.Dispose(); }
}
