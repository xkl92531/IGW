# IGW Unified Array Tag Support Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make every built-in IGW protocol expose scalar, multidimensional, string, and structured PLC arrays as one logical tag with complete read, write, QC, MES, import/export, quality, and diagnostic behavior.

**Architecture:** Add a backward-compatible array/structure schema to `TagDef`, store immutable typed array snapshots in the existing `DataValue` reference path, and let each driver compile that schema into native-array or contiguous-block operations. Keep one DataHub slot per logical array, materialize JSON/UI windows only at boundaries, and preserve existing expanded scalar tags without silently migrating them.

**Tech Stack:** .NET 10/C#, ASP.NET Core Minimal APIs, xUnit, Vue 3/TypeScript/Element Plus, Vitest, OPC Foundation UA .NET, Rust S7 Plus FFI, IoTClient, libplctag, MQTTnet, Inno Setup.

**Spec:** `docs/superpowers/specs/2026-08-24-igw-unified-array-support-design.md`

## Global Constraints

- One logical array uses one `TagDef`, one DataHub slot, and protocol-sized blocks; never register one runtime tag per element.
- Preserve all scalar and already-expanded array tags without automatic migration or TagId changes.
- Support all current built-in drivers: Siemens S7 Classic/Plus, OPC UA Client/Server, Omron CIP/FINS, Allen-Bradley EtherNet/IP, Mitsubishi MC, Modbus TCP, MQTT, and Simulator.
- Preserve per-element quality; mixed quality maps to overall `Uncertain`, Bad strings serialize as `"Bad"`, and other Bad elements serialize as `null`.
- Do not claim atomic whole-array writes when a protocol splits a write; return exact partial results.
- Do not deep-render or deep-clone million-element arrays in Vue; all large-array UI and REST paths use slices.
- Do not publish an installer until the complete protocol matrix, Web build, full .NET regression, performance benchmark, payload scan, and installer smoke tests pass.
- Keep all new JSON fields optional so older project files continue to load.
- Test helper names shown in snippets (`ProjectWithArray`, `CellArrayTag`, `FakeS7Bytes`, and equivalent fixture builders) are private members created in the listed test file; they construct only the exact public contracts introduced by that task and never become production APIs.

## File Structure Map

- `src/IGW.Abstractions/ArrayModel.cs`: persisted array shape and reusable recursive structure schemas.
- `src/IGW.Abstractions/ArrayRuntimeValue.cs`: immutable typed primitive/structured array snapshots, element quality, and slicing.
- `src/IGW.Abstractions/ArrayDrivers.cs`: driver capability and partial-write contracts shared by all drivers.
- `src/IGW.Core/ArrayProjectValidator.cs`: project schema validation and legacy `IsArray` normalization.
- `src/IGW.Core/ArrayValueCodec.cs`: protocol-independent JSON/value conversion and Bad-element projection.
- `src/IGW.Core/ArrayAliasResolver.cs`: virtual element/member paths backed by one array slot.
- `src/IGW.Core/Engine.cs`: binding, read publication, element/slice write routing, and array demand integration.
- `src/IGW.Drivers.Simulator/SimulatorArrayCodec.cs`: deterministic all-type array simulator.
- `src/IGW.Northbound.RestApi/ArrayValueApi.cs`: shape, slice, element-quality, and slice-write endpoints.
- `src/IGW.Configurator/SiemensDbTypeParser.cs`: reusable Siemens type/UDT parser with recursive arrays.
- `src/IGW.Drivers.IoTClient/SiemensArrayCodec.cs`: S7 absolute array block layout and codecs.
- `native/igw-s7plus/src/array_value.rs`: S7 Plus browse/read/write array FFI payloads.
- `src/IGW.Drivers.OpcUaClient/OpcUaArrayCodec.cs`: UA Variant/ExtensionObject array conversion.
- `src/IGW.Northbound.OpcUaServer/IgwArrayNodeFactory.cs`: UA ValueRank/ArrayDimensions/DataTypeDefinition nodes.
- `src/IGW.Drivers.EtherNetIP/EtherNetIpArrayCodec.cs`: AB/Omron symbolic array reads and writes.
- `src/IGW.Drivers.IoTClient/RegisterArrayCodec.cs`: FINS/Modbus register and bit-array conversion.
- `src/IGW.Drivers.Mitsubishi/McArrayCodec.cs`: MC word/bit-array conversion.
- `src/IGW.Drivers.Mqtt/MqttArrayCodec.cs`: JSON array/object-array payload conversion.
- `src/IGW.WebUi/src/utils/arrayTags.ts`: array shape, index, quality, and display helpers.
- `src/IGW.WebUi/src/components/ArrayTagEditor.vue`: array schema editing.
- `src/IGW.WebUi/src/components/ArrayValueViewer.vue`: virtual/paged QC array viewer and editor.
- `src/IGW.Northbound.Mes/MesArrayProjection.cs`: whole-array, slice, member projection, and response write-back.
- `tests/IGW.Tests/*Array*Tests.cs`: model, runtime, protocol, API, MES, and import/export tests.
- `src/IGW.WebUi/src/**/*.spec.ts`: pure utility and component behavior tests under Vitest.

---

### Task 1: Persisted Array and Recursive Structure Model

**Files:**
- Create: `src/IGW.Abstractions/ArrayModel.cs`
- Modify: `src/IGW.Abstractions/Enums.cs`
- Modify: `src/IGW.Abstractions/ProjectModel.cs`
- Create: `tests/IGW.Tests/ArrayModelTests.cs`

**Interfaces:**
- Produces: `ArrayDef`, `StructDef`, `StructFieldDef`, `ArrayShape`, `IgwDataType.Struct`, and `TagDef.Array`.
- Consumes: existing `IgwDataType`, `TagDef`, and JSON project serialization.

- [ ] **Step 1: Write failing model/JSON round-trip tests**

```csharp
[Fact]
public void Array_shape_preserves_rank_lower_bounds_and_struct_layout()
{
    var tag = new TagDef {
        Name = "CellData", DataType = IgwDataType.Struct, IsArray = true,
        Array = new ArrayDef {
            ElementType = IgwDataType.Struct,
            Dimensions = [2, 3], LowerBounds = [1, -2], ElementStrideBytes = 172,
            Struct = new StructDef { Id = "Cell", Name = "Cell", SizeBytes = 172,
                Fields = [new StructFieldDef { Name = "IsNG", JsonName = "isNg", DataType = IgwDataType.Int16, ByteOffset = 168 }] }
        }
    };
    var copy = JsonSerializer.Deserialize<TagDef>(JsonSerializer.Serialize(tag))!;
    Assert.Equal(6, copy.Array!.Shape.ElementCount);
    Assert.Equal([1, -2], copy.Array.LowerBounds);
    Assert.Equal("isNg", copy.Array.Struct!.Fields.Single().JsonName);
}
```

- [ ] **Step 2: Run the focused test and verify it fails**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~ArrayModelTests`

Expected: compile failure because `ArrayDef`, `StructDef`, `StructFieldDef`, `TagDef.Array`, and `IgwDataType.Struct` do not exist.

- [ ] **Step 3: Add the exact persisted contracts**

```csharp
public sealed class ArrayDef
{
    public IgwDataType ElementType { get; set; }
    public int[] Dimensions { get; set; } = [];
    public int[] LowerBounds { get; set; } = [];
    public int ElementStrideBytes { get; set; }
    public int? StringLength { get; set; }
    public StructDef? Struct { get; set; }
    public string? SourceShape { get; set; }
    [JsonIgnore] public ArrayShape Shape => ArrayShape.Create(Dimensions, LowerBounds);
}

public sealed class StructDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int SizeBytes { get; set; }
    public List<StructFieldDef> Fields { get; set; } = [];
}

public sealed class StructFieldDef
{
    public string Name { get; set; } = "";
    public string? JsonName { get; set; }
    public IgwDataType DataType { get; set; }
    public int ByteOffset { get; set; }
    public int BitOffset { get; set; } = -1;
    public int? StringLength { get; set; }
    public ArrayDef? Array { get; set; }
    public StructDef? Struct { get; set; }
}
```

Add `public ArrayDef? Array { get; set; }` to `TagDef`, keep `IsArray`, and add `Struct` to `IgwDataType` without reordering existing enum values.

- [ ] **Step 4: Run tests and solution build**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~ArrayModelTests`

Expected: PASS.

Run: `dotnet build IGW.slnx --no-restore`

Expected: PASS without enum-switch compile regressions.

- [ ] **Step 5: Commit**

```powershell
git add src/IGW.Abstractions/ArrayModel.cs src/IGW.Abstractions/Enums.cs src/IGW.Abstractions/ProjectModel.cs tests/IGW.Tests/ArrayModelTests.cs
git commit -m "feat: define persisted array tag schemas"
```

### Task 2: Schema Validation and Legacy Normalization

**Files:**
- Create: `src/IGW.Core/ArrayProjectValidator.cs`
- Modify: `src/IGW.Core/ProjectStore.cs`
- Create: `tests/IGW.Tests/ArrayProjectValidatorTests.cs`

**Interfaces:**
- Consumes: `ArrayDef`, `StructDef`, and `TagDef.Array` from Task 1.
- Produces: `ArrayProjectValidator.NormalizeAndValidate(IgwProject)` and stable error messages used by REST/UI.

- [ ] **Step 1: Write failing validation tests**

```csharp
[Theory]
[InlineData(new int[] { 2, 3 }, new int[] { 0 }, "维度与下界数量不一致")]
[InlineData(new int[] { 2, 0 }, new int[] { 0, 0 }, "数组维度必须大于零")]
public void Rejects_invalid_shapes(int[] dimensions, int[] lowerBounds, string message)
{
    var project = ProjectWithArray(dimensions, lowerBounds);
    var error = Assert.Throws<InvalidDataException>(() => ArrayProjectValidator.NormalizeAndValidate(project));
    Assert.Contains(message, error.Message);
}

[Fact]
public void Legacy_is_array_without_shape_is_not_silently_read_as_scalar()
{
    var project = ProjectWithLegacyArray(IgwDataType.Int16, "DB1.DBW0");
    var error = Assert.Throws<InvalidDataException>(() => ArrayProjectValidator.NormalizeAndValidate(project));
    Assert.Contains("数组长度缺失", error.Message);
}
```

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~ArrayProjectValidatorTests`

Expected: FAIL because the validator does not exist.

- [ ] **Step 3: Implement normalization and recursive validation**

Implement `NormalizeAndValidate` to walk every channel/device/group/tag, validate checked dimension products, lower-bound counts, stride, string capacity, field overlap, bit offsets, and recursive schema cycles. Normalize `IsArray = tag.Array is not null`; retain a compatibility error for old `IsArray=true` definitions that have neither discoverable metadata nor dimensions.

```csharp
public static void NormalizeAndValidate(IgwProject project)
{
    foreach (var tag in EnumerateTags(project))
    {
        if (tag.Array is null) {
            if (tag.IsArray) throw Error(tag, "数组长度缺失，不能按标量运行");
            continue;
        }
        tag.IsArray = true;
        ValidateArray(tag.Array, new HashSet<string>(StringComparer.Ordinal));
        if (tag.DataType != tag.Array.ElementType)
            throw Error(tag, "TagDef.DataType 与 Array.ElementType 不一致");
    }
}
```

Add a private recursive `EnumerateTags(IgwProject)` in the same validator file that walks device tags and every nested `TagGroupDef`; do not introduce another project-tree service solely for validation.

- [ ] **Step 4: Call validation from project load/save and run regressions**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~ArrayProjectValidatorTests|FullyQualifiedName~PlcTextEncodingProjectTests|FullyQualifiedName~DcsTagProjectUpdaterTests"`

Expected: PASS; old scalar projects remain valid.

- [ ] **Step 5: Commit**

```powershell
git add src/IGW.Core/ArrayProjectValidator.cs src/IGW.Core/ProjectStore.cs tests/IGW.Tests/ArrayProjectValidatorTests.cs
git commit -m "feat: validate array project schemas"
```

### Task 3: Immutable Array Runtime Values and Element Quality

**Files:**
- Create: `src/IGW.Abstractions/ArrayRuntimeValue.cs`
- Modify: `src/IGW.Abstractions/RuntimeValue.cs`
- Create: `src/IGW.Core/ArrayValueCodec.cs`
- Create: `tests/IGW.Tests/ArrayRuntimeValueTests.cs`

**Interfaces:**
- Consumes: `ArrayShape` and structure schemas from Task 1.
- Produces: `ArrayRuntimeValue`, `PrimitiveArrayValue<T>`, `StructArrayValue`, `ArraySlice`, and `ArrayValueCodec.ToWireValue`.

- [ ] **Step 1: Write failing quality/slice tests**

```csharp
[Fact]
public void Mixed_element_quality_is_uncertain_and_bad_values_project_by_type()
{
    var value = PrimitiveArrayValue<short>.Create(
        ArrayShape.Create([3], [1]), [10, 20, 30],
        [StatusCode.Good, StatusCode.BadTimeout, StatusCode.Good]);
    Assert.Equal(QualityClass.Uncertain, value.OverallQuality.Class);
    Assert.Equal(new object?[] { 10, null, 30 }, ArrayValueCodec.ToWireValues(value));
    var slice = Assert.IsType<PrimitiveArrayValue<short>>(value.Slice(new ArraySlice([2], [2])));
    Assert.Equal(new short[] { 20, 30 }, slice.Values.ToArray());
}
```

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~ArrayRuntimeValueTests`

Expected: compile failure for the new runtime types.

- [ ] **Step 3: Implement immutable typed snapshots**

```csharp
public abstract class ArrayRuntimeValue
{
    public required ArrayShape Shape { get; init; }
    public required StatusCode[] ElementQuality { get; init; }
    public abstract IgwDataType ElementType { get; }
    public abstract int Count { get; }
    public abstract ArrayRuntimeValue Slice(ArraySlice slice);
    public StatusCode OverallQuality => ArrayQuality.Aggregate(ElementQuality);
}

public sealed class PrimitiveArrayValue<T> : ArrayRuntimeValue where T : notnull
{
    private readonly T[] _values;
    public ReadOnlyMemory<T> Values => _values;
    public override int Count => _values.Length;
}
```

Use immutable owned arrays for the first correct implementation. Add segmented/pool-backed ownership only in Task 16 after lifetime tests prove no published buffer is reused.

- [ ] **Step 4: Make `DataValue.ValueEquals` compare array versions/content hashes without scalar conversion**

Add a stable `Version` and segment hashes to `ArrayRuntimeValue`; compare reference/version in the hot path. Verify scalar `DataValue` behavior remains unchanged.

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~ArrayRuntimeValueTests|FullyQualifiedName~MesBadQualityPayloadTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/IGW.Abstractions/ArrayRuntimeValue.cs src/IGW.Abstractions/RuntimeValue.cs src/IGW.Core/ArrayValueCodec.cs tests/IGW.Tests/ArrayRuntimeValueTests.cs
git commit -m "feat: add immutable array runtime values"
```

### Task 4: Driver Array Capabilities and Engine Routing

**Files:**
- Create: `src/IGW.Abstractions/ArrayDrivers.cs`
- Create: `src/IGW.Core/ArrayAliasResolver.cs`
- Modify: `src/IGW.Core/Engine.cs`
- Modify: `src/IGW.Core/DataHub.cs`
- Create: `tests/IGW.Tests/ArrayEngineTests.cs`

**Interfaces:**
- Consumes: `ArrayRuntimeValue`, `ArraySlice`, `TagDef.Array`.
- Produces: `ArrayDriverCapabilities`, `IArrayCapableDriver`, `ArrayWriteRequest`, `ArrayWriteResult`, `Engine.ReadArraySlice`, `Engine.ReadArrayElement`, and `Engine.WriteArrayAsync`.

- [ ] **Step 1: Write failing one-slot and partial-write routing tests**

```csharp
[Fact]
public async Task Logical_array_registers_one_slot_and_element_alias_does_not_add_reads()
{
    var driver = new RecordingArrayDriver(Int16Array([4]));
    await using var engine = await Start(ProjectWithArray([4]), driver);
    Assert.Single(engine.Hub.SortedTagIds);
    Assert.Equal((short)3, engine.ReadArrayElement("Line.Plc.DB.Values[2]").Value);
    Assert.Equal(1, driver.ReadCalls);
}
```

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~ArrayEngineTests`

Expected: compile failure for array driver/engine contracts.

- [ ] **Step 3: Add capability and result contracts**

```csharp
public sealed record ArrayDriverCapabilities(
    bool NativeArrays, bool StructuredArrays, bool MultiDimensional,
    bool SliceRead, bool SliceWrite, bool AtomicWholeWrite,
    int MaxElementsPerRequest, int MaxBytesPerRequest);

public interface IArrayCapableDriver
{
    ArrayDriverCapabilities ArrayCapabilities { get; }
}

public sealed record ArrayWriteRequest(TagBinding Binding, ArraySlice Slice, ArrayRuntimeValue Value);
public sealed record ArrayWriteResult(StatusCode Status, int Succeeded, IReadOnlyList<ArrayElementFailure> Failures);
```

- [ ] **Step 4: Route arrays through existing device loops**

Keep `IDeviceDriver.ReadAsync` returning `TagSample`; drivers return `ArrayRuntimeValue` in `DataValue.Value`. Add explicit array-write queue items to the same per-device executor so scalar and array writes cannot race the connection. Resolve `Tag[index].member` as a virtual handle into the parent slot.

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~ArrayEngineTests|FullyQualifiedName~DeviceLoopScanDemandTests|FullyQualifiedName~RuntimeEngineReconnectTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/IGW.Abstractions/ArrayDrivers.cs src/IGW.Core/ArrayAliasResolver.cs src/IGW.Core/Engine.cs src/IGW.Core/DataHub.cs tests/IGW.Tests/ArrayEngineTests.cs
git commit -m "feat: route logical arrays through the engine"
```

### Task 5: Simulator End-to-End Array Slice

**Files:**
- Create: `src/IGW.Drivers.Simulator/SimulatorArrayCodec.cs`
- Modify: `src/IGW.Drivers.Simulator/SimulatorDeviceDriver.cs`
- Modify: `tests/IGW.Tests/IGW.Tests.csproj`
- Create: `tests/IGW.Tests/SimulatorArrayTests.cs`

**Interfaces:**
- Consumes: engine and runtime contracts from Tasks 3-4.
- Produces: first runnable whole/slice/element array read-write path for every element type and nested struct schema.

- [ ] **Step 1: Add the Simulator project reference and failing integration tests**

```csharp
[Fact]
public async Task Simulator_round_trips_multidimensional_struct_array_slice()
{
    await using var driver = SimulatorFor(CellStructArray([2, 3], [1, -2]));
    var initial = await driver.ReadArray("CellData", new ArraySlice([1, -1], [1, 2]));
    var changed = SetMember(initial, [1, 0], "IsNG", (short)2);
    Assert.True((await driver.WriteArray(changed)).Status.IsGood);
    Assert.Equal((short)2, Member(await driver.ReadWhole("CellData"), [1, 0], "IsNG"));
}
```

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~SimulatorArrayTests`

Expected: FAIL because Simulator only generates scalar values.

- [ ] **Step 3: Implement deterministic generation and mutable backing storage**

Use row-major linear indices and schema field columns. Initialize Bool/number/string/WString/BCD values deterministically from the linear index; writes replace only validated slices under the existing device lock.

- [ ] **Step 4: Run simulator, engine, and scalar regressions**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~SimulatorArrayTests|FullyQualifiedName~ArrayEngineTests|FullyQualifiedName~RuntimeEngineIdleConnectionTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/IGW.Drivers.Simulator/SimulatorArrayCodec.cs src/IGW.Drivers.Simulator/SimulatorDeviceDriver.cs tests/IGW.Tests/IGW.Tests.csproj tests/IGW.Tests/SimulatorArrayTests.cs
git commit -m "feat: simulate complete array tag behavior"
```

### Task 6: REST, DCS, Swagger, and Streaming Slice APIs

**Files:**
- Create: `src/IGW.Northbound.RestApi/ArrayValueApi.cs`
- Modify: `src/IGW.Northbound.RestApi/IgwRestApi.cs`
- Modify: `src/IGW.Northbound.RestApi/DcsTagContracts.cs`
- Modify: `src/IGW.Northbound.RestApi/DcsTagValueConverter.cs`
- Modify: `src/IGW.Northbound.RestApi/DcsTagProjectUpdater.cs`
- Modify: `src/IGW.Northbound.RestApi/PublicPlcApiPolicy.cs`
- Create: `tests/IGW.Tests/ArrayValueApiTests.cs`
- Modify: `tests/IGW.Tests/DcsTagValueConverterTests.cs`

**Interfaces:**
- Produces: `GET /api/v1/tags/{id}/array`, `GET /api/v1/tags/{id}/array/slice`, `PUT /api/v1/tags/{id}/array/slice`, array DCS contracts, and Swagger metadata.
- Consumes: `Engine.ReadArraySlice` and `Engine.WriteArrayAsync` from Task 4.

- [ ] **Step 1: Write failing API contract tests**

```csharp
[Fact]
public async Task Slice_endpoint_returns_shape_values_and_element_quality()
{
    var response = await Client.GetFromJsonAsync<ArraySliceResponse>(
        "/api/v1/tags/Line.Plc.Values/array/slice?start=1&count=2");
    Assert.Equal([4], response!.Dimensions);
    Assert.Equal([20, null], response.Values);
    Assert.Equal([0x00000000u, 0x800A0000u], response.QualityCodes);
}
```

- [ ] **Step 2: Run and verify 404/failure**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~ArrayValueApiTests|FullyQualifiedName~DcsTagValueConverterTests"`

Expected: new endpoint returns 404 and DCS shape fields are absent.

- [ ] **Step 3: Implement exact request/response contracts**

```csharp
public sealed record ArraySliceRequest(int[] Start, int[] Length, JsonElement? Values);
public sealed record ArraySliceResponse(
    string TagId, int[] Dimensions, int[] LowerBounds, int[] Start, int[] Length,
    object?[] Values, uint[] QualityCodes, string OverallQuality);
```

Extend `DcsTagUpdateModel` with optional `int[]? Dimensions`, `int[]? LowerBounds`, `StructDef? Struct`; require explicit dimensions for absolute-address protocols. Use async streaming serialization for explicit whole-array reads.

- [ ] **Step 4: Verify API authorization and scalar compatibility**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~ArrayValueApiTests|FullyQualifiedName~DcsTagCompatibilityApiTests|FullyQualifiedName~PublicPlcApiPolicyTests"`

Expected: PASS; public tag APIs remain permission-free according to existing policy, configuration endpoints retain existing protection.

- [ ] **Step 5: Commit**

```powershell
git add src/IGW.Northbound.RestApi/ArrayValueApi.cs src/IGW.Northbound.RestApi/IgwRestApi.cs src/IGW.Northbound.RestApi/DcsTagContracts.cs src/IGW.Northbound.RestApi/DcsTagValueConverter.cs src/IGW.Northbound.RestApi/DcsTagProjectUpdater.cs src/IGW.Northbound.RestApi/PublicPlcApiPolicy.cs tests/IGW.Tests/ArrayValueApiTests.cs tests/IGW.Tests/DcsTagValueConverterTests.cs
git commit -m "feat: expose array tag APIs"
```

### Task 7: Web Array Editor and Virtual QC Viewer

**Files:**
- Modify: `src/IGW.WebUi/package.json`
- Create: `src/IGW.WebUi/vitest.config.ts`
- Create: `src/IGW.WebUi/src/utils/arrayTags.ts`
- Create: `src/IGW.WebUi/src/utils/arrayTags.spec.ts`
- Create: `src/IGW.WebUi/src/components/ArrayTagEditor.vue`
- Create: `src/IGW.WebUi/src/components/ArrayValueViewer.vue`
- Modify: `src/IGW.WebUi/src/views/Project.vue`
- Modify: `src/IGW.WebUi/src/views/Points.vue`
- Modify: `src/IGW.WebUi/src/api.ts`
- Modify: `src/IGW.WebUi/src/i18n/catalog.ts`

**Interfaces:**
- Consumes: shape/slice APIs from Task 6.
- Produces: complete array property editing and paged QC display/write UI.

- [ ] **Step 1: Add Vitest dependencies and failing pure utility tests**

```ts
it('maps non-zero multidimensional indices to row-major offsets', () => {
  expect(linearOffset([2, 3], [1, -2], [2, 0])).toBe(5)
  expect(indexAt(5, [2, 3], [1, -2])).toEqual([2, 0])
})
```

Add `vitest`, `@vue/test-utils`, and `jsdom` as dev dependencies plus `"test": "vitest run"`.

- [ ] **Step 2: Run and verify failure**

Run: `pnpm --dir src/IGW.WebUi test`

Expected: FAIL because `arrayTags.ts` does not exist.

- [ ] **Step 3: Implement array helpers and editor**

`ArrayTagEditor.vue` edits scalar/array mode, element type, dimensions, lower bounds, String/WString capacity, and read-only imported structure layout. It emits a cloned shallow schema rather than deep-cloning live million-element values.

- [ ] **Step 4: Implement paged QC viewer**

`ArrayValueViewer.vue` requests only a configurable 100-row window, supports multidimensional index jump, structure member columns, element/slice writes, and displays Good/Bad/Uncertain counts. `Points.vue` renders it when `tag.array` exists and retains the scalar table otherwise.

- [ ] **Step 5: Run Web verification**

Run: `pnpm --dir src/IGW.WebUi test`

Expected: PASS.

Run: `pnpm --dir src/IGW.WebUi typecheck`

Expected: PASS.

Run: `pnpm --dir src/IGW.WebUi build`

Expected: PASS; output updates `src/IGW.Runtime/wwwroot` only through the existing build pipeline.

- [ ] **Step 6: Commit**

```powershell
git add src/IGW.WebUi/package.json src/IGW.WebUi/pnpm-lock.yaml src/IGW.WebUi/vitest.config.ts src/IGW.WebUi/src/utils/arrayTags.ts src/IGW.WebUi/src/utils/arrayTags.spec.ts src/IGW.WebUi/src/components/ArrayTagEditor.vue src/IGW.WebUi/src/components/ArrayValueViewer.vue src/IGW.WebUi/src/views/Project.vue src/IGW.WebUi/src/views/Points.vue src/IGW.WebUi/src/api.ts src/IGW.WebUi/src/i18n/catalog.ts src/IGW.Runtime/wwwroot
git commit -m "feat: add array tag configuration and QC"
```

### Task 8: Siemens DB/UDT Import Without Array Flattening

**Files:**
- Create: `src/IGW.Configurator/SiemensDbTypeParser.cs`
- Modify: `src/IGW.Configurator/SiemensDbImporter.cs`
- Modify: `src/IGW.Configurator/MainViewModel.cs`
- Modify: `src/IGW.WebUi/src/views/Project.vue`
- Create: `tests/IGW.Tests/SiemensDbArrayImporterTests.cs`
- Modify: `tests/IGW.Tests/IGW.Tests.csproj`

**Interfaces:**
- Produces: recursive DB/UDT declarations mapped to one `TagDef` per PLC array with exact offsets/stride.
- Consumes: array schemas from Task 1 and project validation from Task 2.

- [ ] **Step 1: Add Configurator project reference and failing parser tests**

```csharp
[Fact]
public void Imports_multidimensional_struct_and_string_arrays_as_single_tags()
{
    var result = SiemensDbImporter.Parse(DbSourceWith(
        "CellData : Array[1..10] of CellType;", "Codes : Array[-2..2,0..1] of String[50];"), Udt("CellType", CellFields));
    var cell = Assert.Single(result.Root.Tags, x => x.Name == "CellData");
    Assert.Equal([10], cell.Array!.Dimensions);
    Assert.Equal([1], cell.Array.LowerBounds);
    Assert.Equal(IgwDataType.Struct, cell.DataType);
    Assert.Empty(result.Root.SubGroups.Where(x => x.Name.StartsWith("CellData_")));
}
```

- [ ] **Step 2: Run and verify current flatten/skip failure**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~SiemensDbArrayImporterTests`

Expected: FAIL because basic arrays expand and structure arrays are skipped.

- [ ] **Step 3: Implement tokenizer/parser and layout compiler**

Parse comments without discarding Chinese descriptions, quoted identifiers, multi-line declarations, `ARRAY` dimensions, inline `STRUCT`, named UDTs, String/WString, initialization values, and optimized/non-optimized attributes. Compile non-optimized offsets with S7 alignment and preserve symbolic source shape for optimized blocks.

- [ ] **Step 4: Replace duplicate Web import logic with the backend/import contract**

Expose a preview endpoint returning the same `TagDef`/schema as desktop import; `Project.vue` applies the preview rather than maintaining a second TypeScript S7 offset parser.

- [ ] **Step 5: Run importer and project tests**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~SiemensDbArrayImporterTests|FullyQualifiedName~ArrayProjectValidatorTests|FullyQualifiedName~ProjectParameterSearchTests"`

Expected: PASS with comments and every supported array retained.

- [ ] **Step 6: Commit**

```powershell
git add src/IGW.Configurator/SiemensDbTypeParser.cs src/IGW.Configurator/SiemensDbImporter.cs src/IGW.Configurator/MainViewModel.cs src/IGW.WebUi/src/views/Project.vue tests/IGW.Tests/SiemensDbArrayImporterTests.cs tests/IGW.Tests/IGW.Tests.csproj
git commit -m "feat: preserve arrays in Siemens DB imports"
```

### Task 9: S7 Classic Array Block Read and Write

**Files:**
- Create: `src/IGW.Drivers.IoTClient/SiemensArrayCodec.cs`
- Modify: `src/IGW.Drivers.IoTClient/IoTPlcDriver.cs`
- Modify: `src/IGW.Drivers.IoTClient/SiemensHybridDeviceDriver.cs`
- Modify: `src/IGW.Drivers.IoTClient/SiemensRoutePlanner.cs`
- Create: `tests/IGW.Tests/SiemensClassicArrayTests.cs`

**Interfaces:**
- Consumes: exact absolute layouts from Task 8 and engine contracts from Task 4.
- Produces: PDU-sized S7 block operations for all base/string/structured arrays and element quality.

- [ ] **Step 1: Write failing codec and chunking tests against a fake Siemens byte transport**

```csharp
[Fact]
public async Task Reads_bool_string_and_struct_arrays_in_pdu_sized_blocks()
{
    var transport = new FakeS7Bytes(maxPayload: 32, Db2100Image());
    var value = await SiemensArrayCodec.ReadAsync(transport, CellDataTag(), CancellationToken.None);
    Assert.Equal(10, value.Count);
    Assert.Equal("A55", value.Member<string>([1], "DMCCode"));
    Assert.True(transport.Requests.All(x => x.Length <= 32));
}
```

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~SiemensClassicArrayTests`

Expected: FAIL because `IoTPlcDriver` uses scalar APIs and has no byte-array plan.

- [ ] **Step 3: Implement S7 layout codecs**

Decode/encode S7 big-endian integers/floats, bit-packed Bool, String two-byte headers, WString four-byte headers, BCD/LBCD, multi-dimensional row-major shapes, and recursive structure fields. Split requests by negotiated/configured maximum payload and map failed blocks only to overlapping elements.

- [ ] **Step 4: Route optimized arrays to Plus and reject unsafe Classic guesses**

Use `SiemensAddressDescriptor` and imported metadata. Absolute addresses route Classic; symbolic/optimized addresses route Plus. A missing Plus path returns `DbAddressMismatch` with the exact optimized-block guidance.

- [ ] **Step 5: Run S7 and reconnect regressions**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~SiemensClassicArrayTests|FullyQualifiedName~IoTPlcRetryTests|FullyQualifiedName~SiemensHybridDiagnosticsTests|FullyQualifiedName~DeviceLoopTransientReadTests"`

Expected: PASS; a bad array/DB does not disconnect other DB plans.

- [ ] **Step 6: Commit**

```powershell
git add src/IGW.Drivers.IoTClient/SiemensArrayCodec.cs src/IGW.Drivers.IoTClient/IoTPlcDriver.cs src/IGW.Drivers.IoTClient/SiemensHybridDeviceDriver.cs src/IGW.Drivers.IoTClient/SiemensRoutePlanner.cs tests/IGW.Tests/SiemensClassicArrayTests.cs
git commit -m "feat: read and write S7 Classic arrays"
```

### Task 10: S7 Plus Native Array and UDT Support

**Files:**
- Create: `native/igw-s7plus/src/array_value.rs`
- Modify: `native/igw-s7plus/src/lib.rs`
- Modify: `native/igw-s7plus/tests/abi.rs`
- Modify: `native/igw-s7plus/tests/ffi.rs`
- Modify: `src/IGW.Drivers.IoTClient/SiemensPlusNative.cs`
- Modify: `src/IGW.Drivers.IoTClient/SiemensPlusTransport.cs`
- Modify: `src/IGW.Drivers.IoTClient/SiemensHybridDeviceDriver.cs`
- Create: `tests/IGW.Tests/SiemensPlusArrayTests.cs`

**Interfaces:**
- Produces: ABI v2 browse metadata and binary typed array/structure payload envelopes.
- Consumes: `ArrayRuntimeValue` and schemas from Tasks 1-3.

- [ ] **Step 1: Write failing Rust ABI/FFI tests**

```rust
#[test]
fn browse_payload_contains_array_shape_and_udt_fields() {
    let row = encode_browse_tag(&fixture_struct_array());
    assert!(row.contains("\"dimensions\":[10]"));
    assert!(row.contains("\"lowerBounds\":[1]"));
    assert!(row.contains("\"fields\""));
}
```

- [ ] **Step 2: Run Rust tests and verify failure**

Run: `cargo test --manifest-path native/igw-s7plus/Cargo.toml`

Expected: FAIL because ABI v1 browse rows carry only symbol/type/string length.

- [ ] **Step 3: Implement a length-prefixed JSON browse schema and binary value envelope**

Increment the ABI version to 2 in Rust and C#. Browse returns UTF-8 JSON records containing symbol/type/shape/schema; read values use a header with kind, rank, dimensions, lower bounds, element count, and payload length. Structure arrays encode field columns to avoid object-per-element payloads.

- [ ] **Step 4: Extend C# decode/encode without scalar regressions**

`SiemensPlusNativeBrowseTag` gains `ArrayDef? Array`; `Decode` recognizes array envelopes and constructs runtime values; `Encode` accepts `ArrayRuntimeValue`. Keep v2 version checking explicit so an old DLL fails with a clear installation mismatch.

- [ ] **Step 5: Run Rust and .NET S7 Plus tests**

Run: `cargo test --manifest-path native/igw-s7plus/Cargo.toml`

Expected: PASS.

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~SiemensPlusArrayTests|FullyQualifiedName~SiemensHybridDiagnosticsTests"`

Expected: PASS, including anonymous/authenticated connections and array browse `IsArray=true`.

- [ ] **Step 6: Commit**

```powershell
git add native/igw-s7plus/src/array_value.rs native/igw-s7plus/src/lib.rs native/igw-s7plus/tests/abi.rs native/igw-s7plus/tests/ffi.rs src/IGW.Drivers.IoTClient/SiemensPlusNative.cs src/IGW.Drivers.IoTClient/SiemensPlusTransport.cs src/IGW.Drivers.IoTClient/SiemensHybridDeviceDriver.cs tests/IGW.Tests/SiemensPlusArrayTests.cs
git commit -m "feat: support arrays through S7 Plus native bridge"
```

### Task 11: OPC UA Client and Server Array/Structure Semantics

**Files:**
- Create: `src/IGW.Drivers.OpcUaClient/OpcUaArrayCodec.cs`
- Modify: `src/IGW.Drivers.OpcUaClient/OpcUaClientDeviceDriver.cs`
- Modify: `src/IGW.Drivers.OpcUaClient/OpcUaBrowseWalker.cs`
- Create: `src/IGW.Northbound.OpcUaServer/IgwArrayNodeFactory.cs`
- Modify: `src/IGW.Northbound.OpcUaServer/IgwNodeManager.cs`
- Modify: `tests/IGW.Tests/IGW.Tests.csproj`
- Modify: `tests/IGW.Tests/OpcUaBrowseWalkerTests.cs`
- Create: `tests/IGW.Tests/OpcUaArrayCodecTests.cs`

**Interfaces:**
- Produces: ValueRank/ArrayDimensions-preserving browse, exact Variant writes, ExtensionObject structures, and northbound array nodes.
- Consumes: array runtime/schema contracts.

- [ ] **Step 1: Write failing UA array/ExtensionObject tests**

```csharp
[Fact]
public void Converts_extension_object_array_using_data_type_definition()
{
    var schema = CellSchema();
    var ua = OpcUaFixture.ExtensionObjects(schema, count: 2);
    var value = OpcUaArrayCodec.Decode(ua, CellArrayTag());
    Assert.Equal("A55", value.Member<string>([0], "DMCCode"));
    Assert.Equal(ValueRanks.OneDimension, OpcUaArrayCodec.ValueRank(value.Shape));
}
```

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~OpcUaArrayCodecTests|FullyQualifiedName~OpcUaBrowseWalkerTests"`

Expected: FAIL because browse only records `IsArray` and runtime passes raw `dv.Value`.

- [ ] **Step 3: Implement client browse/read/write conversion**

Read ValueRank, ArrayDimensions, DataType and DataTypeDefinition. Map primitive CLR arrays directly; decode/encode ExtensionObjects through the persisted `StructDef`. Validate shape before write and preserve the server status across all elements.

- [ ] **Step 4: Implement server nodes**

Create variable nodes with exact DataType, ValueRank and ArrayDimensions. Register deterministic custom DataTypeDefinition nodes for structure schemas and accept typed array/ExtensionObject writes through Engine array routing.

- [ ] **Step 5: Run OPC UA and idle-session regressions**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~OpcUaArrayCodecTests|FullyQualifiedName~OpcUaBrowseWalkerTests|FullyQualifiedName~RuntimeEngineIdleConnectionTests"`

Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add src/IGW.Drivers.OpcUaClient/OpcUaArrayCodec.cs src/IGW.Drivers.OpcUaClient/OpcUaClientDeviceDriver.cs src/IGW.Drivers.OpcUaClient/OpcUaBrowseWalker.cs src/IGW.Northbound.OpcUaServer/IgwArrayNodeFactory.cs src/IGW.Northbound.OpcUaServer/IgwNodeManager.cs tests/IGW.Tests/IGW.Tests.csproj tests/IGW.Tests/OpcUaBrowseWalkerTests.cs tests/IGW.Tests/OpcUaArrayCodecTests.cs
git commit -m "feat: support OPC UA arrays and structures"
```

### Task 12: EtherNet/IP Arrays for AB and Omron CIP

**Files:**
- Create: `src/IGW.Drivers.EtherNetIP/EtherNetIpArrayCodec.cs`
- Modify: `src/IGW.Drivers.EtherNetIP/EtherNetIpDriver.cs`
- Modify: `src/IGW.Drivers.EtherNetIP/EtherNetIpFactories.cs`
- Modify: `tests/IGW.Tests/IGW.Tests.csproj`
- Create: `tests/IGW.Tests/EtherNetIpArrayTests.cs`

**Interfaces:**
- Produces: symbolic element-range/UDT array operations for `AB.Eip` and `Omron.Cip`.
- Consumes: engine array and structure contracts.

- [ ] **Step 1: Add project reference and failing fake-tag tests**

```csharp
[Theory]
[InlineData("AB.Eip")]
[InlineData("Omron.Cip")]
public async Task Symbolic_array_is_chunked_by_controller_element_limit(string driverId)
{
    var gateway = new FakePlcTagGateway(maxElements: 4);
    var value = await Driver(driverId, gateway).ReadWhole(Int32SymbolArray("Values", 10));
    Assert.Equal(10, value.Count);
    Assert.Equal([4, 4, 2], gateway.ReadElementCounts);
}
```

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~EtherNetIpArrayTests`

Expected: FAIL because `EnsureTag` creates scalar libplctag tags.

- [ ] **Step 3: Implement symbolic array plans**

Use libplctag element count/index parameters, controller-specific type widths, and UDT/member metadata. Chunk by driver/controller limits, map multidimensional indices through the imported shape, and preserve per-range failures.

- [ ] **Step 4: Run tests and relay regressions**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~EtherNetIpArrayTests|FullyQualifiedName~BoundTcpRelayTests|FullyQualifiedName~NetworkBindingPlanTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/IGW.Drivers.EtherNetIP/EtherNetIpArrayCodec.cs src/IGW.Drivers.EtherNetIP/EtherNetIpDriver.cs src/IGW.Drivers.EtherNetIP/EtherNetIpFactories.cs tests/IGW.Tests/IGW.Tests.csproj tests/IGW.Tests/EtherNetIpArrayTests.cs
git commit -m "feat: support AB and Omron CIP arrays"
```

### Task 13: FINS, Modbus, Mitsubishi, and MQTT Logical Arrays

**Files:**
- Create: `src/IGW.Drivers.IoTClient/RegisterArrayCodec.cs`
- Modify: `src/IGW.Drivers.IoTClient/IoTPlcDriver.cs`
- Modify: `src/IGW.Drivers.IoTClient/IoTModbusDriver.cs`
- Create: `src/IGW.Drivers.Mitsubishi/McArrayCodec.cs`
- Modify: `src/IGW.Drivers.Mitsubishi/McTcpDeviceDriver.cs`
- Create: `src/IGW.Drivers.Mqtt/MqttArrayCodec.cs`
- Modify: `src/IGW.Drivers.Mqtt/MqttDriver.cs`
- Modify: `tests/IGW.Tests/IGW.Tests.csproj`
- Create: `tests/IGW.Tests/RegisterAndMqttArrayTests.cs`

**Interfaces:**
- Produces: contiguous bit/register arrays and MQTT JSON arrays/object arrays.
- Consumes: schema, codecs, and driver capabilities from earlier tasks.

- [ ] **Step 1: Write failing protocol-neutral register codec and MQTT JSON tests**

```csharp
[Fact]
public void Register_codec_round_trips_word_order_and_partial_quality()
{
    var bytes = Hex("00 01 00 02 00 03 00 04");
    var value = RegisterArrayCodec.Decode(bytes, UInt32Array([2]), WordOrder.HighLow, StatusCode.Good);
    Assert.Equal(new uint[] { 0x00010002, 0x00030004 }, value.Values);
}

[Fact]
public void Mqtt_struct_array_round_trips_json_objects() =>
    Assert.Equal(CellJson, MqttArrayCodec.Encode(MqttArrayCodec.Decode(CellJson, CellArrayTag())));
```

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~RegisterAndMqttArrayTests`

Expected: FAIL because only scalar register/MQTT parsing exists.

- [ ] **Step 3: Implement FINS/Modbus register and bit plans**

Compile start address plus element span; read coils/bits or registers in protocol-sized ranges; decode all numeric widths, BCD/LBCD, configured String/WString and structures. Preserve existing zero-based and byte/word order device settings.

- [ ] **Step 4: Implement Mitsubishi and MQTT arrays**

MC uses X/Y/M bit blocks and D/W register blocks with existing address rules. MQTT parses a JSON Pointer suffix from tag metadata, accepts JSON arrays/object arrays, and publishes the same JSON structure on writes.

- [ ] **Step 5: Run protocol and network-binding regressions**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~RegisterAndMqttArrayTests|FullyQualifiedName~NetworkBindingPlanTests|FullyQualifiedName~IoTPlcRetryTests"`

Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add src/IGW.Drivers.IoTClient/RegisterArrayCodec.cs src/IGW.Drivers.IoTClient/IoTPlcDriver.cs src/IGW.Drivers.IoTClient/IoTModbusDriver.cs src/IGW.Drivers.Mitsubishi/McArrayCodec.cs src/IGW.Drivers.Mitsubishi/McTcpDeviceDriver.cs src/IGW.Drivers.Mqtt/MqttArrayCodec.cs src/IGW.Drivers.Mqtt/MqttDriver.cs tests/IGW.Tests/IGW.Tests.csproj tests/IGW.Tests/RegisterAndMqttArrayTests.cs
git commit -m "feat: support register and MQTT arrays"
```

### Task 14: KEP/IGW JSON Round-Trip and Explicit Legacy Merge Tool

**Files:**
- Modify: `src/IGW.WebUi/src/interop.ts`
- Modify: `src/IGW.WebUi/src/utils/kepTagImport.ts`
- Modify: `src/IGW.Configurator/KepJsonImporter.cs`
- Modify: `src/IGW.Configurator/DcsConfigExporter.cs`
- Create: `src/IGW.Core/ExpandedArrayMigration.cs`
- Create: `tests/IGW.Tests/ArrayProjectExchangeTests.cs`
- Create: `tests/IGW.Tests/ExpandedArrayMigrationTests.cs`

**Interfaces:**
- Produces: lossless KEP+`igw` array definitions and preview-only explicit merge migration.
- Consumes: schemas and alias resolver.

- [ ] **Step 1: Write failing round-trip tests**

```csharp
[Fact]
public void Export_import_preserves_array_shape_schema_mes_and_driver()
{
    var source = FullProjectWithTcpSiemensStructArrayAndMes();
    var imported = Import(Export(source));
    AssertEquivalent(source.Channels, imported.Channels);
    AssertEquivalent(source.MesTasks, imported.MesTasks);
    Assert.Equal("Siemens.S7", imported.Channels.Single().DriverId);
}
```

- [ ] **Step 2: Run and verify shape loss**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~ArrayProjectExchangeTests|FullyQualifiedName~ExpandedArrayMigrationTests"`

Expected: FAIL because KEP array mapping only preserves `IsArray` and no dimensions/schema.

- [ ] **Step 3: Add standard KEP mappings plus lossless `igw.arrayDefinition`**

Preserve KEP type codes and address fields; serialize lower bounds, dimensions, structure IDs/layout, aliases, and quality policy under the existing IGW extension. Import must not replace an explicit driver ID with generic Siemens.

- [ ] **Step 4: Implement previewed expanded-tag merge**

Recognize only contiguous, same-type indexed siblings; calculate the proposed shape and affected TagIds/MES references; return a preview. Apply only after explicit confirmation, create virtual compatibility aliases, and leave noncontiguous/inconsistent groups unchanged with reasons.

- [ ] **Step 5: Run project/MES template regressions**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~ArrayProjectExchangeTests|FullyQualifiedName~ExpandedArrayMigrationTests|FullyQualifiedName~MesFlowTemplateOpsTests|FullyQualifiedName~ProjectParameterSearchTests"`

Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add src/IGW.WebUi/src/interop.ts src/IGW.WebUi/src/utils/kepTagImport.ts src/IGW.Configurator/KepJsonImporter.cs src/IGW.Configurator/DcsConfigExporter.cs src/IGW.Core/ExpandedArrayMigration.cs tests/IGW.Tests/ArrayProjectExchangeTests.cs tests/IGW.Tests/ExpandedArrayMigrationTests.cs
git commit -m "feat: preserve arrays in project exchange"
```

### Task 15: MES Array Conditions, Upload Projection, and Response Write-Back

**Files:**
- Create: `src/IGW.Northbound.Mes/MesArrayProjection.cs`
- Modify: `src/IGW.Abstractions/MesModel.cs`
- Modify: `src/IGW.Northbound.Mes/MesUploadEngine.cs`
- Modify: `src/IGW.Northbound.Mes/MesResponseWritePlanner.cs`
- Modify: `src/IGW.Northbound.Mes/MesRangePayloadCompiler.cs`
- Modify: `src/IGW.WebUi/src/views/MesFlow.vue`
- Modify: `src/IGW.WebUi/src/components/MesDataRangeDialog.vue`
- Modify: `src/IGW.WebUi/src/utils/mesStructMapping.ts`
- Create: `tests/IGW.Tests/MesArrayFlowTests.cs`

**Interfaces:**
- Produces: whole/slice/member projections, Any/All conditions, lower-bound-aware start/count, and upload-mapping-derived response writes.
- Consumes: runtime arrays and element-quality wire rules.

- [ ] **Step 1: Write failing MES payload/write-back tests**

```csharp
[Fact]
public async Task Upload_slice_and_response_writeback_use_plc_lower_bound_and_json_aliases()
{
    var task = ArrayTask(start: 1, count: 2, memberAlias: ("ErrorMessage", "ngReason"));
    var payload = BuildPayload(task, CellArray(lowerBound: 1));
    Assert.Equal(2, payload.GetProperty("structures").GetArrayLength());
    var writes = MesResponseWritePlanner.Plan(task.Payload, ResponseWithNgReason(), Compiled(task));
    Assert.Contains(writes, x => x.TagId.EndsWith("CellData[1].ErrorMessage"));
}
```

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~MesArrayFlowTests`

Expected: FAIL because MES currently discovers arrays from expanded indexed groups.

- [ ] **Step 3: Add direct array selectors and projection**

Extend `MesDataRange`/conditions with optional array slice and member projection. Compile selectors to parent array slot plus row-major ranges. Implement Any/All over element/member values without materializing full JSON.

- [ ] **Step 4: Reuse upload mapping for response write-back**

Map response aliases case-insensitively back through `StructFieldDef.JsonName`; truncate strings to PLC capacity; record member-level failures while continuing other fields. Business failure/non-2xx stays on Upload and never schedules ResponseWriteback.

- [ ] **Step 5: Update Web selector and run MES regressions**

The dialog chooses whole array, start/count, multidimensional slice, or member projection and previews real PLC indices. Preserve template replacement against the current DB schema.

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~MesArrayFlowTests|FullyQualifiedName~MesBadQualityPayloadTests|FullyQualifiedName~MesResponseWritebackTests|FullyQualifiedName~MesHttpDefaultsTests|FullyQualifiedName~MesFlowTemplateOpsTests"`

Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add src/IGW.Northbound.Mes/MesArrayProjection.cs src/IGW.Abstractions/MesModel.cs src/IGW.Northbound.Mes/MesUploadEngine.cs src/IGW.Northbound.Mes/MesResponseWritePlanner.cs src/IGW.Northbound.Mes/MesRangePayloadCompiler.cs src/IGW.WebUi/src/views/MesFlow.vue src/IGW.WebUi/src/components/MesDataRangeDialog.vue src/IGW.WebUi/src/utils/mesStructMapping.ts tests/IGW.Tests/MesArrayFlowTests.cs
git commit -m "feat: support arrays throughout MES flows"
```

### Task 16: Diagnostics, Million-Element Performance, Full Verification, and Installer

**Files:**
- Modify: `src/IGW.Core/ConnectionDiagnostics.cs`
- Modify: `src/IGW.Core/Engine.cs`
- Modify: `src/IGW.Northbound.RestApi/ConnectionDiagnosticsApi.cs`
- Modify: `src/IGW.WebUi/src/views/Monitor.vue`
- Create: `tests/IGW.Tests/ArrayDiagnosticsTests.cs`
- Create: `tests/IGW.Tests/ArrayPerformanceTests.cs`
- Modify: `installer/version.txt`
- Generated: `installer/out/IGW-Setup-<version>.exe`

**Interfaces:**
- Consumes: all previous tasks.
- Produces: observable capacity/partial failure behavior and the only releasable installer artifact.

- [ ] **Step 1: Write failing diagnostic and performance-budget tests**

```csharp
[Fact]
public void Partial_array_failure_reports_ranges_without_marking_device_offline()
{
    var snapshot = Diagnose(ArrayWithFailedRange(100, 119));
    Assert.Equal("PartialOnline", snapshot.State);
    Assert.Contains("[100..119]", snapshot.Message);
    Assert.Equal(ConnectionState.Connected, snapshot.ConnectionState);
}

[Fact]
public void Million_primitive_elements_use_one_slot_and_bounded_allocations()
{
    var result = ArrayBenchmark.Run(elementCount: 1_000_000, iterations: 20);
    Assert.Equal(1, result.HubSlots);
    Assert.True(result.AllocatedBytesPerIteration < 1_000_000, result.ToString());
}
```

- [ ] **Step 2: Run and verify the new diagnostics/performance tests fail**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~ArrayDiagnosticsTests|FullyQualifiedName~ArrayPerformanceTests"`

Expected: FAIL until range metrics and buffer reuse are implemented.

- [ ] **Step 3: Add array metrics and safe pooled/segmented buffers**

Report total/Good/Bad/Uncertain elements, failed ranges, block count, bytes, last duration, P95/P99 and deadline misses. Replace Task 3 owned-array allocations with pooled/segmented snapshots only after ownership/version tests prove published buffers cannot be reused early.

- [ ] **Step 4: Run every automated suite**

Run: `cargo test --manifest-path native/igw-s7plus/Cargo.toml`

Expected: PASS.

Run: `dotnet test IGW.slnx --no-restore --verbosity minimal`

Expected: PASS with no skipped array protocol tests except tests explicitly requiring unavailable physical PLC hardware; each such hardware certification is listed separately and not called passed.

Run: `pnpm --dir src/IGW.WebUi test && pnpm --dir src/IGW.WebUi typecheck && pnpm --dir src/IGW.WebUi build`

Expected: all commands PASS.

- [ ] **Step 5: Run sustained simulator capacity verification**

Run the dedicated benchmark for at least 30 minutes with one million total elements, 50 tasks at 200 ms, several 500-element tasks at 10 ms, concurrent QC slice reads, and injected partial device failures. Save CPU, memory, GC, request, P95/P99 and UI latency output under `artifacts/verification/array-support/`; do not add artifacts to Git.

Expected: no unbounded queue, stable memory, one slot per logical array, no N-request-per-element behavior, and unrelated devices remain on schedule. Report any missed spec budget instead of suppressing it.

- [ ] **Step 6: Perform live protocol certification where hardware/server is available**

Verify read/write and failure behavior against the available S7 Classic/Plus simulator or PLC, IGW OPC UA simulator, Omron/AB server/device, Modbus/Mitsubishi/FINS endpoints, and MQTT broker. Record unavailable hardware as `not run`, never `pass`.

- [ ] **Step 7: Build and verify the installer**

Run: `installer\build.bat`

Expected: version increments once, Runtime/Web/native payloads are rebuilt from current commit, Inno Setup succeeds, runtime/firewall tests pass, and a new `installer\out\IGW-Setup-<version>.exe` exists.

Run the existing installer payload scan and verify no private keys, project data, keygen outputs, tests, `.codex`, package stores, `bin/obj`, or node_modules are included. Install in a disposable location, start Runtime, open the Web UI, exercise Simulator array import/QC/MES write-back, uninstall, and verify service/process cleanup.

- [ ] **Step 8: Commit source verification changes only**

```powershell
git add src/IGW.Core/ConnectionDiagnostics.cs src/IGW.Core/Engine.cs src/IGW.Northbound.RestApi/ConnectionDiagnosticsApi.cs src/IGW.WebUi/src/views/Monitor.vue tests/IGW.Tests/ArrayDiagnosticsTests.cs tests/IGW.Tests/ArrayPerformanceTests.cs installer/version.txt
git commit -m "feat: complete and verify unified array support"
```

Do not add `artifacts/` or `installer/out/` to Git. Final handoff reports the installer absolute path, display version, size, SHA-256, completed tests, unavailable hardware tests, and any measured capacity limits.
