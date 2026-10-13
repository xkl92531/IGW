using IGW.Observability;
using Xunit;

namespace IGW.Tests;

public sealed class LogPayloadStoreTests
{
    [Fact]
    public void Exchange_split_keeps_only_sent_and_returned_content_for_new_and_old_formats()
    {
        LogPayloadExchange current = LogPayloadStore.SplitExchange("a",
            "===== 发送内容 =====\n{\"sn\":\"A1\"}\n===== 返回内容 =====\n{\"code\":0}", 100);
        Assert.True(current.Split);
        Assert.Equal("{\"sn\":\"A1\"}", current.Request);
        Assert.Equal("{\"code\":0}", current.Response);

        LogPayloadExchange oldMes = LogPayloadStore.SplitExchange("b",
            "===== IGW MES 实际发送内容 =====\r\nContent-Type: application/json; charset=utf-8\r\n\r\n{\"sn\":\"A1\"}\r\n\r\n" +
            "===== MES 实际响应内容 =====\r\nHTTP 200\r\n\r\n{\"code\":0}", 100);
        Assert.Equal("{\"sn\":\"A1\"}", oldMes.Request);
        Assert.Equal("{\"code\":0}", oldMes.Response);

        LogPayloadExchange oldRest = LogPayloadStore.SplitExchange("c",
            "===== 客户端 → IGW 实际请求 =====\nPOST /api/v1/tags/write HTTP/1.1\nContent-Type: application/json\nContent-Length: 9\n\n{\"v\":1}\n\n" +
            "===== IGW → 客户端实际响应 =====\nHTTP 200\nContent-Type: application/json\nContent-Length: 11\n\n{\"ok\":true}", 100);
        Assert.Equal("{\"v\":1}", oldRest.Request);
        Assert.Equal("{\"ok\":true}", oldRest.Response);

        LogPayloadExchange timeout = LogPayloadStore.SplitExchange("d",
            "===== 发送内容 =====\n{}\n===== 返回内容 =====\n等待响应超时（3000 ms）", 100);
        Assert.Equal("等待响应超时（3000 ms）", timeout.Response);

        LogPayloadExchange unknown = LogPayloadStore.SplitExchange("e", "纯文本报文", 3);
        Assert.False(unknown.Split);
        Assert.Equal("纯文本", unknown.Request);
        Assert.True(unknown.RequestTruncated);
    }

    [Fact]
    public async Task Cleanup_removes_expired_payloads_then_oldest_until_under_the_size_cap()
    {
        string root = NewDirectory();
        try
        {
            var store = new LogPayloadStore(root);
            var ids = new List<string>();
            var now = DateTime.UtcNow;
            for (int i = 0; i < 10; i++)
            {
                LogPayloadReference saved = await store.SaveTextAsync("text/plain", new string('x', 10_000), CancellationToken.None);
                ids.Add(saved.Id);
                // 第 0 个过期；其余按新旧排列，第 1 个最旧
                DateTime written = i == 0 ? now.AddDays(-40) : now.AddMinutes(-100 + i);
                File.SetLastWriteTimeUtc(saved.Path, written);
                File.SetLastWriteTimeUtc(Path.ChangeExtension(saved.Path, ".json"), written);
            }

            // 9 个未过期的约 90KB；上限 50KB → 删到 40KB 以内，只留最新的几个
            LogPayloadCleanupResult result = store.Cleanup(TimeSpan.FromDays(30), 50_000, DateTimeOffset.UtcNow);

            Assert.Equal(1, result.Expired);
            Assert.True(result.RemainingBytes <= 40_000);
            Assert.False(File.Exists(Path.Combine(root, ids[0] + ".payload")));
            Assert.False(File.Exists(Path.Combine(root, ids[1] + ".payload")));
            Assert.True(File.Exists(Path.Combine(root, ids[9] + ".payload")));
            await store.ReadPageAsync(ids[9], 0, CancellationToken.None);
            Assert.Equal(0, store.Failed);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Payload_is_redacted_before_persistence_and_read_in_bounded_pages()
    {
        string root = NewDirectory();
        try
        {
            var store = new LogPayloadStore(root);
            string payload = "Authorization: Bearer top-secret\npassword=hidden&token=also-hidden\n"
                + "GET /api/v1/items?access_token=query-secret&station=OP140 HTTP/1.1\n"
                + "{\"password\":\"json-password\",\"apiKey\":\"json-key\",\"value\":123}\n"
                + new string('中', 40_000);

            LogPayloadReference saved = await store.SaveTextAsync("text/plain", payload, CancellationToken.None);
            LogPayloadPage first = await store.ReadPageAsync(saved.Id, 0, CancellationToken.None);
            LogPayloadPage second = await store.ReadPageAsync(saved.Id, 1, CancellationToken.None);

            Assert.Equal(32_768, first.Content.Length);
            Assert.NotEmpty(second.Content);
            Assert.True(first.HasMore);
            Assert.Equal(2, first.TotalPages);
            Assert.Equal(File.ReadAllText(saved.Path).Length, first.TotalLength);
            Assert.DoesNotContain("top-secret", first.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("hidden", first.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("json-password", first.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("json-key", first.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("query-secret", first.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("top-secret", File.ReadAllText(saved.Path), StringComparison.Ordinal);
            Assert.DoesNotContain("hidden", File.ReadAllText(saved.Path), StringComparison.Ordinal);
            Assert.Equal(0, store.Failed);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Payload_ids_cannot_escape_the_storage_directory()
    {
        string root = NewDirectory();
        try
        {
            var store = new LogPayloadStore(root);
            await Assert.ThrowsAsync<ArgumentException>(() =>
                store.ReadPageAsync("../outside", 0, CancellationToken.None));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string NewDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "igw-log-payload", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
