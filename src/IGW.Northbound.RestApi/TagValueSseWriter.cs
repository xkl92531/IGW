using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace IGW.Northbound.RestApi;

// Kept only for legacy contract tests and optional external integrations. The browser hub
// uses TagValueFrameWriter directly and never constructs or parses SSE wire text.
internal sealed class TagValueSseWriter : TagValueFrameWriter
{
    protected override string TimeoutErrorCode => "sse_write_timeout";

    public TagValueSseWriter(Stream output, string streamId, TimeSpan? writeTimeout = null)
        : base(CreateSender(output), streamId, writeTimeout)
    {
        if (streamId.Contains('\r') || streamId.Contains('\n'))
            throw new ArgumentException("invalid_stream_id", nameof(streamId));
    }

    private static Func<RealtimeFrame, CancellationToken, Task<int>> CreateSender(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite) throw new ArgumentException("output_must_be_writable", nameof(output));
        return async (frame, ct) =>
        {
            byte[] bytes = Encoding.UTF8.GetBytes($"event: {frame.Event}\nid: {frame.Id}\ndata: {frame.Data}\n\n");
            if (bytes.Length > MaxEventBytes) throw new InvalidOperationException("event_too_large");
            await output.WriteAsync(bytes, ct);
            await output.FlushAsync(ct);
            return bytes.Length;
        };
    }

    public static void ConfigureResponse(HttpResponse response)
    {
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream; charset=utf-8";
        response.Headers.CacheControl = "no-store";
        response.Headers["X-Accel-Buffering"] = "no";
        response.HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        var compression = response.HttpContext.Features.Get<IHttpsCompressionFeature>();
        if (compression is not null) compression.Mode = HttpsCompressionMode.DoNotCompress;
    }
}
