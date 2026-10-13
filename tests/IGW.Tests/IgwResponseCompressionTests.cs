using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using IGW.Northbound.RestApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace IGW.Tests;

public sealed class IgwResponseCompressionTests
{
    [Fact]
    public async Task Json_responses_are_compressed_for_clients_that_accept_it()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/big.json");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        using var response = await client.SendAsync(request);

        Assert.Equal("gzip", Assert.Single(response.Content.Headers.ContentEncoding));
        byte[] compressed = await response.Content.ReadAsByteArrayAsync();
        using var gzip = new GZipStream(new MemoryStream(compressed), CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        string json = await reader.ReadToEndAsync();
        Assert.Equal(BigJson, json);
        Assert.True(compressed.Length * 5 <Encoding.UTF8.GetByteCount(json), $"compressed={compressed.Length}");
    }

    [Fact]
    public async Task Server_sent_events_are_not_compressed_so_each_frame_arrives_immediately()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/events");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

        Assert.Empty(response.Content.Headers.ContentEncoding);
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var reader = new StreamReader(stream);
        // 服务器在写完第一帧后一直不结束响应；压缩层若缓冲，这里会等到超时。
        Assert.Equal("data: first", await reader.ReadLineAsync(timeout.Token));
    }

    [Fact]
    public void Push_stream_media_types_are_never_in_the_compressed_set()
    {
        Assert.DoesNotContain("text/event-stream", IgwResponseCompression.CompressedMimeTypes);
        Assert.DoesNotContain("text/plain", IgwResponseCompression.CompressedMimeTypes);
        Assert.DoesNotContain("application/octet-stream", IgwResponseCompression.CompressedMimeTypes);
    }

    private static readonly string BigJson = "[" + string.Join(",", Enumerable.Range(0, 20_000)
        .Select(i => $"{{\"name\":\"Value{i}\",\"address\":\"ns=3;s=\\\"DB_Station\\\".\\\"Data\\\"[{i}]\"}}")) + "]";

    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddIgwResponseCompression();
        var app = builder.Build();
        app.UseIgwResponseCompression();
        app.MapGet("/big.json", () => Results.Text(BigJson, "application/json", Encoding.UTF8));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.MapGet("/events", async (HttpResponse response, CancellationToken ct) =>
        {
            response.ContentType = "text/event-stream";
            await response.WriteAsync("data: first\n\n", ct);
            await response.Body.FlushAsync(ct);
            try { await release.Task.WaitAsync(ct); } catch (OperationCanceledException) { }
        });
        await app.StartAsync();
        return app;
    }
}
