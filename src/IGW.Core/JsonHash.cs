using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using IGW.Abstractions;

namespace IGW.Core;

/// <summary>
/// 对象 → JSON → SHA256 十六进制串,全程流式:不落中间大字符串。
/// 用于热重载配置签名比较(10万点工程的 Channels 序列化成字符串会进 LOH、拖长停服窗口)。
/// </summary>
public static class JsonHash
{
    private static readonly JsonSerializerOptions RuntimeOptions = CreateRuntimeOptions();

    // Descriptions are editable documentation; all communication and interpretation fields remain significant.
    private static JsonSerializerOptions CreateRuntimeOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type != typeof(ChannelDef) && info.Type != typeof(DeviceDef) &&
                info.Type != typeof(TagGroupDef) && info.Type != typeof(TagDef) &&
                info.Type != typeof(DeviceTemplate)) return;
            var description = info.Properties.FirstOrDefault(p => p.Name == "Description");
            if (description is not null) info.Properties.Remove(description);
        });
        return new JsonSerializerOptions { TypeInfoResolver = resolver };
    }

    public static string Of<T>(T obj) => Compute(obj, null);
    public static string OfRuntime<T>(T obj) => Compute(obj, RuntimeOptions);

    /// <summary>
    /// 通道列表的运行态签名：各通道并行序列化求哈希，再按顺序合并。
    /// 百万点工程整体串行序列化要 4–5 秒，按通道并行后接近核数倍提速；结果只在同一版本内比较。
    /// </summary>
    public static string OfRuntimeChannels(IReadOnlyList<ChannelDef> channels)
    {
        var parts = new string[channels.Count];
        Parallel.For(0, channels.Count, index => parts[index] = OfRuntime(channels[index]));
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData(BitConverter.GetBytes(channels.Count));
        foreach (string part in parts) h.AppendData(System.Text.Encoding.ASCII.GetBytes(part));
        return Convert.ToHexString(h.GetHashAndReset());
    }

    private static string Compute<T>(T obj, JsonSerializerOptions? options)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var s = new HashOnlyStream(h))
        using (var w = new Utf8JsonWriter(s))
            JsonSerializer.Serialize(w, obj, options);
        return Convert.ToHexString(h.GetHashAndReset());
    }

    private sealed class HashOnlyStream : Stream
    {
        private readonly IncrementalHash _h;
        public HashOnlyStream(IncrementalHash h) => _h = h;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => _h.AppendData(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => _h.AppendData(buffer);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
