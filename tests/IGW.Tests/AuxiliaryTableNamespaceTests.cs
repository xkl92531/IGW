using IGW.Abstractions;
using IGW.Core;
using IGW.Northbound.Mes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class AuxiliaryTableNamespaceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "igw-aux-prefix-" + Guid.NewGuid().ToString("N"));
    private string Database(string name, string sql)
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, name + ".db");
        using var db = Open(path);
        using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        return path;
    }
    private static SqliteConnection Open(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open(); return db;
    }
    private static void Names(string path, params string[] expected)
    {
        using var db = Open(path);
        using var command = db.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        using var reader = command.ExecuteReader(); var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        Assert.Equal(expected.Order(StringComparer.Ordinal), names.Order(StringComparer.Ordinal));
    }
    [Fact]
    public void Existing_prefixed_foreign_schema_fails_safely_without_creating_other_tables()
    {
        string path = Database("foreign-ui", "CREATE TABLE IGW_ui_preferences(customer TEXT); INSERT INTO IGW_ui_preferences VALUES('keep');");
        Assert.Throws<InvalidDataException>(() => { using var store = new UiPreferenceStore(path); });
        Names(path, "IGW_ui_preferences");
    }
    [Fact]
    public void An_older_inbox_without_later_optional_tables_migrates_and_preserves_pending_messages()
    {
        string path = Database("old-inbox", """
            CREATE TABLE interface_inbox(sequence INTEGER PRIMARY KEY AUTOINCREMENT,scope TEXT NOT NULL,id TEXT NOT NULL,json TEXT NOT NULL,completed INTEGER NOT NULL DEFAULT 0,ack TEXT,UNIQUE(scope,id));
            INSERT INTO interface_inbox VALUES(51,'task','pending','{}',0,NULL);
            """);
        using (var inbox = new MesInterfaceInbox(path))
        {
            Assert.Equal(51, inbox.Pending("task")!.Sequence);
            Assert.Equal(52, inbox.Append("task", "new", "{}", null, 3).Sequence);
        }
        Names(path, "IGW_interface_inbox", "IGW_interface_state", "IGW_interface_bodies", "IGW_interface_configuration");
    }
    [Fact]
    public void Legacy_ui_preferences_keep_operator_values_and_unowned_tables()
    {
        string path = Database("ui", """
            CREATE TABLE ui_preferences(key TEXT PRIMARY KEY,value TEXT NOT NULL,updated_utc TEXT NOT NULL);
            INSERT INTO ui_preferences VALUES('locale','en-US','2026-10-01'),('projectAutoSave','false','2026-10-01'),('projectAutoSaveSeconds','120','2026-10-01');
            CREATE TABLE customer_note(value TEXT); INSERT INTO customer_note VALUES('keep');
            """);
        for (int i = 0; i < 2; i++)
        {
            using var store = new UiPreferenceStore(path);
            Assert.Equal("en-US", store.GetLocale()); Assert.False(store.GetProjectAutoSave());
            Assert.Equal(120, store.GetProjectAutoSaveSeconds()); store.SetProjectAutoSave(false, 120);
        }
        Names(path, "IGW_ui_preferences", "customer_note");
    }
    [Fact]
    public async Task Legacy_history_remains_readable_and_mes_logs_continue_the_sequence()
    {
        string history = Database("history", """
            CREATE TABLE samples(id INTEGER PRIMARY KEY AUTOINCREMENT,tag TEXT NOT NULL,val TEXT,q INTEGER,ts TEXT);
            INSERT INTO samples VALUES(42,'PLC.Tag','123',192,'2026-10-01T01:02:03.0000000Z');
            """);
        await using (var hub = new DataHub())
        await using (var logger = new DataLogger(hub, new HistorySettings { DbPath = history, FlushIntervalMs = 250 }, _directory, NullLogger.Instance))
        {
            Assert.Equal(1, logger.Count());
            HistoryRow old = Assert.Single(logger.Query("PLC.Tag", 5));
            Assert.Equal("123", old.Value); Assert.Equal(192u, old.Quality);
            Assert.Equal(new DateTime(2026, 10, 1, 1, 2, 3, DateTimeKind.Utc), old.Ts);
            hub.Register("PLC.Tag", new TagDef { Name = "Tag", DataType = IgwDataType.Int32 });
            // History is a passive observer; an active consumer causes hub change publication.
            using var consumer = hub.SubscribeAll(_ => ValueTask.CompletedTask, "history-test-consumer");
            hub.Update("PLC.Tag", DataValue.GoodAt(456, DateTime.UtcNow));
            for (int attempt = 0; attempt < 100 && logger.Count() < 2; attempt++) await Task.Delay(25);
            Assert.Equal("456", logger.Query("PLC.Tag", 5)[0].Value);
        }
        Names(history, "IGW_samples");
        using (var historyDb = Open(history))
        using (var sequence = historyDb.CreateCommand())
        {
            sequence.CommandText = "SELECT id FROM IGW_samples WHERE val='456'";
            Assert.Equal(43L, sequence.ExecuteScalar());
        }
        string mes = Database("mes", """
            CREATE TABLE mes_log(id INTEGER PRIMARY KEY AUTOINCREMENT,ts TEXT,task TEXT,step TEXT,ok INTEGER,http INTEGER,msg TEXT);
            INSERT INTO mes_log VALUES(77,'2026-10-01','task','upload',1,200,'legacy');
            """);
        await using (var log = new MesLog(mes, 1000))
        {
            Assert.Equal("legacy", Assert.Single(log.Query(10)).Msg); log.Add("task", "upload", true, 201, "new");
        }
        Names(mes, "IGW_mes_log");
        using var db = Open(mes); using var command = db.CreateCommand();
        command.CommandText = "SELECT id FROM IGW_mes_log WHERE msg='new'";
        Assert.Equal(78L, command.ExecuteScalar());
    }
    [Fact]
    public void Legacy_mes_inbox_preserves_pending_dedup_ack_frozen_bodies_and_resume_state()
    {
        string path = Database("inbox", """
            CREATE TABLE interface_inbox(sequence INTEGER PRIMARY KEY AUTOINCREMENT,scope TEXT NOT NULL,id TEXT NOT NULL,json TEXT NOT NULL,completed INTEGER NOT NULL DEFAULT 0,ack TEXT,UNIQUE(scope,id));
            CREATE INDEX ix_interface_pending ON interface_inbox(scope,completed,sequence);
            CREATE TABLE interface_state(scope TEXT PRIMARY KEY,baseline TEXT,last_event_id TEXT);
            CREATE TABLE interface_bodies(sequence INTEGER NOT NULL,step TEXT NOT NULL,body TEXT NOT NULL,PRIMARY KEY(sequence,step));
            CREATE TABLE interface_configuration(scope TEXT PRIMARY KEY,signature TEXT NOT NULL);
            INSERT INTO interface_inbox VALUES(80,'task','done','{}',1,'ack-old'),(81,'task','pending','{}',0,NULL);
            INSERT INTO interface_state VALUES('task','baseline-old','event-81');
            INSERT INTO interface_bodies VALUES(81,'upload','frozen-old');
            INSERT INTO interface_configuration VALUES('task','original');
            """);
        for (int i = 0; i < 2; i++)
        {
            using var inbox = new MesInterfaceInbox(path);
            Assert.Equal(81, inbox.Pending("task")!.Sequence);
            Assert.Equal("ack-old", inbox.Append("task", "done", "{}", null, 5).Ack);
            Assert.Equal("frozen-old", inbox.FreezeBody(81, "upload", "changed"));
            Assert.Equal("baseline-old", inbox.Baseline("task")); Assert.Equal("event-81", inbox.LastEventId("task"));
            Assert.Throws<InvalidOperationException>(() => inbox.Configure("task", "changed"));
        }
        using (var inbox = new MesInterfaceInbox(path))
        {
            Assert.Equal(82, inbox.Append("task", "new", "{}", "event-82", 5).Sequence);
            inbox.Commit(81, "baseline-new", "ack-new");
            Assert.Equal("ack-new", inbox.Append("task", "pending", "{}", null, 5).Ack);
        }
        Names(path, "IGW_interface_inbox", "IGW_interface_state", "IGW_interface_bodies", "IGW_interface_configuration");
    }
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
