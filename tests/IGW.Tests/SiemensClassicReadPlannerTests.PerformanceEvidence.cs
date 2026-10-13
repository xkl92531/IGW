using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using IGW.Abstractions;
using IGW.Drivers.IoTClient;
using Xunit;
using Xunit.Abstractions;

namespace IGW.Tests;

public sealed partial class SiemensClassicReadPlannerTests
{
    private readonly ITestOutputHelper _output;
    public SiemensClassicReadPlannerTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Plc3_block_and_scalar_reads_have_exact_value_quality_and_membership_parity()
    {
        var tags = Plc3();
        var images = new Dictionary<int, byte[]> { [2100] = new byte[3000], [2101] = new byte[106] };
        var expected = new Dictionary<string, object>();
        for (int i = 0; i < tags.Count; i++)
        {
            var tag = tags[i];
            var db = SiemensAddressDescriptor.Parse(tag.Def.Address).DbNumber!.Value;
            var bytes = images[db].AsSpan(tag.Address.Offset);
            switch (tag.Def.DataType)
            {
                case IgwDataType.Bool: bytes[0] = 1; expected[tag.TagId] = true; break;
                case IgwDataType.UInt16:
                    BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)(100 + i));
                    expected[tag.TagId] = (ushort)(100 + i); break;
                case IgwDataType.Float:
                    BinaryPrimitives.WriteSingleBigEndian(bytes, i + 0.25f);
                    expected[tag.TagId] = i + 0.25f; break;
                case IgwDataType.String:
                    var value = $"PLC3-{i:D2}";
                    bytes[0] = (byte)tag.Def.StringLength!.Value; bytes[1] = (byte)value.Length;
                    Encoding.ASCII.GetBytes(value, bytes[2..]); expected[tag.TagId] = value; break;
                default: throw new InvalidOperationException();
            }
        }
        RecordingBytes Transport() => new(880, (db, offset, count) =>
            SiemensByteReadResult.Good(images[db].AsSpan(offset, count).ToArray()));
        var batched = Transport();
        var samples = await SiemensClassicBlockCodec.ReadAsync(batched, tags, default);
        var scalar = Transport();
        var scalarSamples = new List<TagSample>();
        foreach (var tag in tags) scalarSamples.AddRange(await SiemensClassicBlockCodec.ReadAsync(scalar, [tag], default));
        Assert.Equal(72, samples.Count);
        Assert.Equal(72, samples.Select(s => s.TagId).Distinct().Count());
        Assert.Equal(tags.Select(t => t.TagId).Order(), samples.Select(s => s.TagId).Order());
        foreach (var sample in samples)
        {
            var single = scalarSamples.Single(s => s.TagId == sample.TagId);
            Assert.Equal(expected[sample.TagId], sample.Value.Value);
            Assert.Equal(single.Value.Value, sample.Value.Value);
            Assert.Equal(StatusCode.Good, sample.Value.Quality);
            Assert.Equal(single.Value.Quality, sample.Value.Quality);
        }
        Assert.Equal(72, scalar.Reads.Count); Assert.Equal(5, batched.Reads.Count);
        _output.WriteLine("PLC3 protocol stub: points=72; logical codec calls=1; scalar requests=72; block requests=5; reduction=93.06%; missing=0; duplicates=0; exact expected/scalar/block value+quality parity=72/72.");
        _output.WriteLine("Ranges (DB,offset,count): " + string.Join("; ", batched.Reads));
    }

    [Fact]
    public async Task Latency_shaped_stub_rolls_back_to_more_requests_with_lower_batch_completion_p95()
    {
        var window = new AdaptiveRequestWindow(20, 25, () => 0);
        var transport = new LatencyBytes();
        var training = Enumerable.Range(0, 20).Select(i => Binding($"DB1.DBB{i}", IgwDataType.Byte)).ToArray();
        for (int i = 0; i < 20; i++) await SiemensClassicBlockCodec.ReadAsync(transport, training, default, window: window);
        Assert.Equal(25, window.Current);
        var probe = Enumerable.Range(0, 25).Select(i => Binding($"DB1.DBB{i}", IgwDataType.Byte)).ToArray();
        for (int i = 0; i < 5; i++) await SiemensClassicBlockCodec.ReadAsync(transport, probe, default, window: window);
        Assert.Equal(20, window.Current);
        var tags = Enumerable.Range(0, 100).Select(i => Binding($"DB1.DBB{i}", IgwDataType.Byte)).ToArray();
        async Task<(double P95, int Requests)> Measure(AdaptiveRequestWindow measured)
        {
            var times = new List<double>(); int before = transport.Requests;
            for (int i = 0; i < 20; i++)
            {
                var watch = Stopwatch.StartNew();
                var values = await SiemensClassicBlockCodec.ReadAsync(transport, tags, default, window: measured);
                times.Add(watch.Elapsed.TotalMilliseconds);
                Assert.Equal(tags.Select(t => t.TagId), values.Select(v => v.TagId));
                Assert.All(values, v => Assert.True(v.Value.IsGood));
            }
            times.Sort();
            return (times[(int)Math.Ceiling(times.Count * .95) - 1], (transport.Requests - before) / 20);
        }
        var large = await Measure(AdaptiveRequestWindow.ForDeclaredLimit(25));
        var restored = await Measure(window);
        Assert.Equal(4, large.Requests); Assert.Equal(5, restored.Requests);
        Assert.True(restored.P95 < large.P95, $"Restored P95 {restored.P95} >= large {large.P95}");
        Assert.Equal(20, window.Current);
        _output.WriteLine($"Latency-shaped protocol stub only: 100 points, 20 batches/window; request delay 100ms for 25 bytes, 1ms otherwise; grown window=25 requests/batch={large.Requests} completion P95={large.P95:F3}ms; rollback window=20 requests/batch={restored.Requests} completion P95={restored.P95:F3}ms. Nearest-rank P95; real elapsed time; not PLC/KEP evidence.");
    }

    private sealed class LatencyBytes : ISiemensByteTransport
    {
        public int MaxPayloadBytes => 25;
        public int Requests;
        public async ValueTask<SiemensByteReadResult> ReadAsync(int db, int offset, int count, CancellationToken ct)
        {
            Requests++;
            await Task.Delay(count == 25 ? 100 : 1, ct);
            return SiemensByteReadResult.Good(new byte[count]);
        }
        public ValueTask<StatusCode> WriteAsync(int db, int offset, ReadOnlyMemory<byte> data, CancellationToken ct) => throw new NotSupportedException();
    }
}
