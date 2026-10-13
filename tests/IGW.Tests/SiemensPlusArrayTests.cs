using IGW.Abstractions;
using IGW.Drivers.IoTClient;
using Xunit;

namespace IGW.Tests;

public sealed class SiemensPlusArrayTests
{
    [Fact]
    public async Task Expands_symbolic_struct_array_in_one_logical_tag_and_rebuilds_columns()
    {
        var native = new FakeNative
        {
            Values =
            {
                ["ProductionDb.Cells[1].Code"] = "A1",
                ["ProductionDb.Cells[1].Value"] = (short)11,
                ["ProductionDb.Cells[2].Code"] = "A2",
                ["ProductionDb.Cells[2].Value"] = (short)22,
            },
        };
        await using var transport = new SiemensPlusTransport(native);
        await transport.ConnectAsync("127.0.0.1", 102, TimeSpan.FromSeconds(1), false, CancellationToken.None);
        var binding = Binding();

        var sample = Assert.Single(await transport.ReadAsync([binding], CancellationToken.None));

        var value = Assert.IsType<StructArrayValue>(sample.Value.Value);
        Assert.Equal("A1", value.Members["Code"].GetBoxedValue(0));
        Assert.Equal((short)22, value.Members["Value"].GetBoxedValue(1));
        Assert.Equal(4, native.ReadSymbols.Count);
        Assert.DoesNotContain("ProductionDb.Cells", native.ReadSymbols);
    }

    [Fact]
    public async Task Writes_symbolic_array_slice_by_element_and_reports_success()
    {
        var native = new FakeNative();
        await using var transport = new SiemensPlusTransport(native);
        await transport.ConnectAsync("127.0.0.1", 102, TimeSpan.FromSeconds(1), false, CancellationToken.None);
        var binding = Binding();
        var shape = ArrayShape.Create([1], [2]);
        var value = StructArrayValue.Create(shape, binding.Def.Array!.Struct!, new Dictionary<string, ArrayRuntimeValue>
        {
            ["Code"] = PrimitiveArrayValue<string>.Create(shape, ["ZX"]),
            ["Value"] = PrimitiveArrayValue<short>.Create(shape, [(short)77]),
        });

        var result = await transport.WriteArrayAsync(
            new ArrayWriteRequest(binding, new ArraySlice([2], [1]), value), CancellationToken.None);

        Assert.Equal(StatusCode.Good, result.Status);
        Assert.Equal(1, result.Succeeded);
        Assert.Contains(native.Writes, item => item.Symbol == "ProductionDb.Cells[2].Code" && Equals(item.Value, "ZX"));
        Assert.Contains(native.Writes, item => item.Symbol == "ProductionDb.Cells[2].Value" && Equals(item.Value, (short)77));
    }

    private static TagBinding Binding() => new()
    {
        TagId = "S7.PLC.ProductionDb.Cells",
        Address = new TagAddress(0, 0, 1),
        Def = new TagDef
        {
            Name = "Cells", Address = "ProductionDb.Cells", DataType = IgwDataType.Struct,
            Array = new ArrayDef
            {
                ElementType = IgwDataType.Struct,
                Dimensions = [2], LowerBounds = [1], ElementStrideBytes = 8,
                Struct = new StructDef
                {
                    Id = "cell", Name = "Cell", SizeBytes = 8,
                    Fields =
                    [
                        new StructFieldDef { Name = "Code", DataType = IgwDataType.String, StringLength = 3 },
                        new StructFieldDef { Name = "Value", DataType = IgwDataType.Int16 },
                    ],
                },
            },
        },
    };

    private sealed class FakeNative : ISiemensPlusNativeApi
    {
        public Dictionary<string, object?> Values { get; } = new(StringComparer.Ordinal);
        public List<string> ReadSymbols { get; } = [];
        public List<SiemensPlusNativeWrite> Writes { get; } = [];
        public SiemensPlusResultCode Connect(string host, ushort port, uint timeoutMs, bool legacy) => SiemensPlusResultCode.Ok;
        public SiemensPlusResultCode Authenticate(string username, string password) => SiemensPlusResultCode.Ok;
        public SiemensPlusResultCode Probe() => SiemensPlusResultCode.Ok;
        public SiemensPlusNativeBrowseResult Browse() => new(SiemensPlusResultCode.Ok, []);
        public SiemensPlusNativeReadResult Read(IReadOnlyList<string> symbols)
        {
            ReadSymbols.AddRange(symbols);
            return new(SiemensPlusResultCode.Ok, symbols.Select(symbol => Values.TryGetValue(symbol, out var value)
                ? new SiemensPlusNativeValue(SiemensPlusResultCode.Ok, value)
                : new SiemensPlusNativeValue(SiemensPlusResultCode.ProtocolFailure, null)).ToArray());
        }
        public SiemensPlusResultCode Write(IReadOnlyList<SiemensPlusNativeWrite> writes)
        {
            Writes.AddRange(writes);
            return SiemensPlusResultCode.Ok;
        }
        public SiemensPlusResultCode Disconnect() => SiemensPlusResultCode.Ok;
        public void Dispose() { }
    }
}
