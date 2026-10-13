using System.Net;
using System.Text;
using System.Text.Json;
using IGW.Observability;
using Xunit;

namespace IGW.Observability.Tests;

public sealed class QuestDbTableNamespaceTests
{
    [Fact]
    public void Log_and_payload_measurements_use_the_physical_prefix_but_keep_attribute_names()
    {
        string log = QuestDbLogCodec.Encode(new LogRecord(1791500000000000, 2, "system", "test", new Dictionary<string, object?> { ["igw_device"] = "PLC" }), Guid.NewGuid());
        Assert.StartsWith("IGW_logs ", log); Assert.Contains("igw_device=", log);
        Assert.StartsWith("IGW_payloads ", QuestDbLogCodec.Payload(Guid.NewGuid(), 1791500000000000, "text/plain", "body"));
    }

    [Theory]
    [InlineData("igw_logs", "igw_payloads")]
    [InlineData("IGW_logs", "IGW_payloads")]
    [InlineData("IGW_logs_namespace_migration", "IGW_payloads_namespace_migration")]
    public async Task Initialization_renames_legacy_tables_or_recovers_interrupted_rename_without_copying(string logs, string payloads)
    {
        var handler = new CatalogHandler([logs, payloads, "customer"]);
        using var client = Client(handler);
        await client.InitializeAsync(30, default);
        await client.InitializeAsync(30, default);
        Assert.Contains("IGW_logs", handler.Names); Assert.Contains("IGW_payloads", handler.Names); Assert.Contains("customer", handler.Names);
        Assert.Equal(3, handler.Names.Count);
        Assert.DoesNotContain(handler.Sql, sql => sql.Contains("insert into", StringComparison.OrdinalIgnoreCase) || sql.Contains("drop table", StringComparison.OrdinalIgnoreCase));
        if (logs == "igw_logs") Assert.Contains("rename table igw_logs to IGW_logs_namespace_migration", handler.Sql);
        if (logs != "IGW_logs") Assert.Contains("rename table IGW_logs_namespace_migration to IGW_logs", handler.Sql);
    }

    [Fact]
    public async Task A_temporary_name_collision_or_foreign_schema_fails_before_any_rename_or_create()
    {
        foreach (var handler in new[]
        {
            new CatalogHandler(["igw_logs", "IGW_logs_namespace_migration", "igw_payloads"]),
            new CatalogHandler(["igw_logs", "igw_payloads", "IGW_payloads_namespace_migration"]),
            new CatalogHandler(["igw_logs", "igw_payloads"], invalidSchema: true),
            new CatalogHandler(["igw_logs", "igw_payloads"], invalidPayloadSchema: true)
        })
        {
            using var client = Client(handler);
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.InitializeAsync(30, default));
            Assert.DoesNotContain(handler.Sql, sql => sql.StartsWith("rename ") || sql.StartsWith("create ") || sql.StartsWith("alter "));
        }
    }

    [Fact]
    public async Task Failed_second_rename_keeps_recoverable_table_and_retry_completes()
    {
        var handler = new CatalogHandler(["igw_logs", "igw_payloads"]) { FailFinalRenameOnce = true };
        using var client = Client(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.InitializeAsync(30, default));
        Assert.Contains("IGW_logs_namespace_migration", handler.Names);
        await client.InitializeAsync(30, default);
        Assert.Contains("IGW_logs", handler.Names); Assert.Contains("IGW_payloads", handler.Names);
    }

    [Fact]
    public async Task Durable_legacy_batch_uses_new_measurements_without_changing_fields_or_values()
    {
        string? sent = null;
        using var client = Client(new CaptureHandler(async request => { sent = await request.Content!.ReadAsStringAsync(); return Response("{}", HttpStatusCode.NoContent); }));
        await client.WriteAsync(Encoding.UTF8.GetBytes("igw_logs id=\"id\",raw=\"igw_logs value\",igw_channel=\"PLC\" 1791500000000000\nigw_payloads id=\"id\",body=\"igw_payloads value\" 1791500000000000\n"), default);
        Assert.Equal("IGW_logs id=\"id\",raw=\"igw_logs value\",igw_channel=\"PLC\" 1791500000000000\nIGW_payloads id=\"id\",body=\"igw_payloads value\" 1791500000000000\n", sent);
    }

    private static QuestDbClient Client(HttpMessageHandler handler) => new(new Uri("http://127.0.0.1:28922"), "test", "secret", handler);
    private static HttpResponseMessage Response(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json) };
    private sealed class CaptureHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request);
    }
    private sealed class CatalogHandler(IEnumerable<string> names, bool invalidSchema = false, bool invalidPayloadSchema = false) : HttpMessageHandler
    {
        public HashSet<string> Names { get; } = new(names, StringComparer.Ordinal);
        public List<string> Sql { get; } = [];
        public bool FailFinalRenameOnce { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string sql = Uri.UnescapeDataString(request.RequestUri!.Query[7..]); Sql.Add(sql);
            if (sql.Contains("from tables()")) return Task.FromResult(Response(JsonSerializer.Serialize(new { dataset = Names.Select(name => new object[] { name, "ts", "HOUR", true }) })));
            if (sql.Contains("table_columns("))
            {
                string[] columns = sql.Contains("logs", StringComparison.Ordinal) ? ["ts", "id", "level", "category", "raw", .. QuestDbLogCodec.Fields] : ["ts", "id", "content_type", "body"];
                object[][] rows = columns.Select(name => new object[] { name, name == "ts" ? "TIMESTAMP" : name == "id" ? "UUID" : name == "level" ? "INT" : name == "category" || QuestDbLogCodec.IndexedFields.Contains(name) ? "SYMBOL" : "VARCHAR", name is "ts" or "id" }).ToArray();
                bool invalid = invalidSchema || invalidPayloadSchema && sql.Contains("payloads", StringComparison.Ordinal);
                return Task.FromResult(Response(JsonSerializer.Serialize(new { dataset = invalid ? rows.Take(1) : rows.AsEnumerable() })));
            }
            if (sql.StartsWith("rename table "))
            {
                string[] words = sql.Split(' '); string from = words[2], to = words[4];
                if (FailFinalRenameOnce && to == "IGW_logs") { FailFinalRenameOnce = false; return Task.FromResult(Response("{\"error\":\"busy\"}")); }
                if (!Names.Remove(from) || Names.Any(n => n.Equals(to, StringComparison.OrdinalIgnoreCase))) return Task.FromResult(Response("{\"error\":\"exists or missing\"}"));
                Names.Add(to);
            }
            return Task.FromResult(Response("{\"ddl\":\"OK\"}"));
        }
    }
}
