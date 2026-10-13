# Unified Array Recognition Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace name-based array guessing with one backend, format-aware recognition pipeline that preserves native, flattened, multidimensional, nested, sparse, jagged, and struct arrays as logical ranges without expanding large arrays in MES/QC UI.

**Architecture:** A new Core recognition catalog parses protocol metadata and physical addresses into canonical index evidence, validates repeated element schemas, and emits immutable logical array descriptors. MES compilation, REST range discovery, and the Web UI consume those descriptors; KEP/OPC UA/Siemens imports retain source evidence so round trips reproduce the same logical shape. Existing `ArrayDef` remains the native rectangular-array contract while logical descriptors add sparse/jagged and expanded-leaf bindings without rewriting the physical project tree.

**Tech Stack:** .NET 10, C# records, xUnit, ASP.NET Core minimal APIs, Vue 3, TypeScript, Element Plus, Vitest/Node tests.

**Spec:** `docs/superpowers/specs/2026-08-28-capability-driven-plc-io-engine-design.md`

## Global Constraints

- Array recognition must use source format, explicit metadata, physical address/NodeId evidence, and schema equality; display names alone are never sufficient evidence.
- Existing native `ArrayDef` projects and scalar projects remain loadable without migration prompts.
- Rules arrays remain one logical range in MES/QC; no permanent N-element `TagDef` or N-row DOM expansion.
- Full physical TagIds and addresses remain unchanged so existing PLC reads/writes continue to target the real device.
- Partial element quality and write failures remain element-scoped and never disconnect a healthy session.
- Unknown or ambiguous shapes stay unmerged and return an explicit diagnostic instead of a silent guess.
- Current unrelated working-tree changes must not be reset or overwritten.

---

### Task 1: Canonical array address evidence

**Files:**
- Create: `src/IGW.Core/ArrayRecognitionModels.cs`
- Create: `src/IGW.Core/ArrayAddressEvidenceParser.cs`
- Test: `tests/IGW.Tests/ArrayAddressEvidenceParserTests.cs`

**Interfaces:**
- Consumes: raw physical `TagDef.Address` strings from OPC UA, S7 symbolic, KEP, and generic bracketed drivers.
- Produces: `ArrayAddressEvidenceParser.TryParse(string address, out ArrayAddressEvidence evidence)` where `ArrayAddressEvidence` contains `SourceFormat`, `CanonicalPattern`, `IndexVectors`, `MemberPath`, and `OriginalAddress`.

- [ ] **Step 1: Write parser tests for every supported index syntax**

```csharp
[Theory]
[InlineData("ns=3;s=\"Unit\".\"Bolts\"[4].\"Torque\"", "OpcUaNodeId", "ns=3;s=\"Unit\".\"Bolts\"[{0}].\"Torque\"", "Torque", 4)]
[InlineData("DB1.Cells[1,2].Value", "Symbolic", "DB1.Cells[{0},{1}].Value", "Value", 1, 2)]
[InlineData("DB1.Cells[1][2].Value", "Symbolic", "DB1.Cells[{0}][{1}].Value", "Value", 1, 2)]
[InlineData("DB1.Cells[-2].Value", "Symbolic", "DB1.Cells[{0}].Value", "Value", -2)]
public void Parse_preserves_format_pattern_member_and_indices(
    string address, string format, string pattern, string member, params int[] indices)
{
    Assert.True(ArrayAddressEvidenceParser.TryParse(address, out var actual));
    Assert.Equal(format, actual.SourceFormat);
    Assert.Equal(pattern, actual.CanonicalPattern);
    Assert.Equal(member, actual.MemberPath);
    Assert.Equal(indices, actual.FlattenedIndices);
}
```

Add negative tests for `Recipe_0.Value`, `Folder.0.Value`, and `Text[abc]`; these must return `false` because a display-looking suffix without valid physical index syntax is not evidence.

- [ ] **Step 2: Run the focused test and verify it fails**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~ArrayAddressEvidenceParserTests`

Expected: compilation fails because `ArrayAddressEvidenceParser` is not defined.

- [ ] **Step 3: Add immutable recognition contracts**

```csharp
namespace IGW.Core;

public enum LogicalArrayShapeKind { Rectangular, Sparse, Jagged }
public enum ArrayBindingKind { NativeTag, AddressPattern, ExpandedLeaves }
public enum ArrayRecognitionConfidence { Explicit, Proven, Ambiguous }

public sealed record ArrayAddressEvidence(
    string SourceFormat,
    string OriginalAddress,
    string CanonicalPattern,
    IReadOnlyList<int[]> IndexVectors,
    IReadOnlyList<int> FlattenedIndices,
    string MemberPath);

public sealed record LogicalArrayLeafBinding(
    int[] Indices,
    string MemberPath,
    string TagId,
    string Address);
```

- [ ] **Step 4: Implement a single-pass bracket tokenizer**

Implement `ArrayAddressEvidenceParser` without splitting on dots inside quoted OPC UA identifiers. Parse every `[...]` group as one axis vector, accept signed comma-separated integers, replace values with stable `{n}` placeholders, and derive the member path from quoted/symbolic property tokens after the final index. Set `SourceFormat` to `OpcUaNodeId` for `ns=...;[isg]=...`, otherwise `Symbolic`.

- [ ] **Step 5: Run focused tests**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~ArrayAddressEvidenceParserTests`

Expected: all parser cases pass.

- [ ] **Step 6: Commit the parser boundary**

```powershell
git add src/IGW.Core/ArrayRecognitionModels.cs src/IGW.Core/ArrayAddressEvidenceParser.cs tests/IGW.Tests/ArrayAddressEvidenceParserTests.cs
git commit -m "feat: parse protocol array address evidence"
```

### Task 2: Backend logical array recognizer registry

**Files:**
- Create: `src/IGW.Core/ArrayShapeRecognizer.cs`
- Create: `src/IGW.Core/ProjectArrayRecognitionCatalog.cs`
- Test: `tests/IGW.Tests/ArrayShapeRecognizerTests.cs`

**Interfaces:**
- Consumes: `IgwProject`, native `TagDef.Array`, full project group paths, and `ArrayAddressEvidence` from Task 1.
- Produces: `ProjectArrayRecognitionCatalog.Build(IgwProject project)`; `TryGet(string sourcePath, out LogicalArrayDescriptor descriptor)`; `Diagnostics` for ambiguous candidates.

- [ ] **Step 1: Write failing tests for explicit, flattened, nested, multidimensional, sparse, and false-positive cases**

```csharp
[Fact]
public void Physical_addresses_not_business_names_define_the_array()
{
    var project = ProjectWithGroups(
        Group("AnyBusinessName", Element("first", 5), Element("second", 6)));
    var catalog = ProjectArrayRecognitionCatalog.Build(project);
    var array = AssertDescriptor(catalog, "Line.Plc.DB.AnyBusinessName");
    Assert.Equal([5], array.LowerBounds);
    Assert.Equal([2], array.Dimensions);
    Assert.Equal(ArrayRecognitionConfidence.Proven, array.Confidence);
}
```

The element helper must give the leaves addresses `ns=3;s="DB"."ActualArray"[5]."Value"` and `[6]`; the group labels deliberately contain no indices. Add tests that prove:

- native `TagDef.Array` wins with `Explicit` confidence;
- `[1,2]` and `[1][2]` produce rank 2;
- `Outer[0].Inner[0]` with unequal inner lengths produces `Jagged`;
- indices `1,3,9` produce `Sparse` and preserve exactly those indices;
- identical groups named `0` and `1` with non-indexed addresses remain ordinary groups and emit an ambiguous diagnostic;
- structurally different elements do not merge;
- duplicate display names at different physical paths produce different descriptors.

- [ ] **Step 2: Run the recognizer tests and verify they fail**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~ArrayShapeRecognizerTests`

Expected: compilation fails because the recognition catalog is not defined.

- [ ] **Step 3: Define the logical descriptor**

```csharp
public sealed record LogicalArrayMember(
    string Path,
    string JsonName,
    IGW.Abstractions.IgwDataType DataType,
    LogicalArrayDescriptor? NestedArray);

public sealed record LogicalArrayDescriptor(
    string Name,
    string SourcePath,
    string DevicePath,
    LogicalArrayShapeKind ShapeKind,
    int[] Dimensions,
    int[] LowerBounds,
    IReadOnlyList<int[]> ExistingIndices,
    IReadOnlyList<LogicalArrayMember> Members,
    IReadOnlyList<LogicalArrayLeafBinding> Leaves,
    ArrayBindingKind BindingKind,
    string SourceFormat,
    ArrayRecognitionConfidence Confidence,
    IReadOnlyList<string> Evidence);
```

- [ ] **Step 4: Implement the recognizer registry**

Create an internal `IArrayFormatRecognizer` with `Collect(ProjectTreeNode root, ArrayRecognitionBuilder builder)`. Register in this order:

1. `ExplicitArrayDefRecognizer` for native `TagDef.Array`;
2. `IndexedAddressArrayRecognizer` for physical address patterns from Task 1;
3. `StrictExpandedTreeRecognizer` only when every element has matching address evidence and identical recursive schema.

For each candidate, calculate the Cartesian product of each axis. Complete products become `Rectangular`; missing tuples become `Sparse`; nested axis lengths that depend on an outer index become `Jagged`. Never require indices to start at zero or be continuous.

- [ ] **Step 5: Run the tests and verify all shape classes pass**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~ArrayShapeRecognizerTests`

Expected: all tests pass and the false-positive case has no descriptor.

- [ ] **Step 6: Commit the backend recognition catalog**

```powershell
git add src/IGW.Core/ArrayShapeRecognizer.cs src/IGW.Core/ProjectArrayRecognitionCatalog.cs tests/IGW.Tests/ArrayShapeRecognizerTests.cs
git commit -m "feat: recognize logical arrays from source evidence"
```

### Task 3: Make MES catalog and payloads consume recognized arrays

**Files:**
- Modify: `src/IGW.Core/MesDataRangeCatalog.cs`
- Modify: `src/IGW.Core/MesDataRangeValidator.cs`
- Modify: `src/IGW.Core/MesTaskTopology.cs`
- Modify: `src/IGW.Northbound.Mes/MesRangePayloadCompiler.cs`
- Modify: `src/IGW.Northbound.Mes/MesRangeJsonWriter.cs`
- Modify: `src/IGW.Northbound.Mes/MesResponseWritePlanner.cs`
- Test: `tests/IGW.Tests/MesArrayFlowTests.cs`

**Interfaces:**
- Consumes: `LogicalArrayDescriptor` and leaf bindings from Task 2.
- Produces: one `MesCatalogLogicalArray` per descriptor, JSON array/object output preserving real indices, and response write plans targeting original leaf TagIds.

- [ ] **Step 1: Add failing MES tests for non-zero, sparse, multidimensional, and nested arrays**

Extend `MesArrayFlowTests` with physical addresses that prove shape while display groups use arbitrary names. Assert:

```csharp
Assert.True(catalog.TryGetLogicalArray("Line.Plc.OP200Data.AnyBusinessName", out var logical));
Assert.Equal(LogicalArrayShapeKind.Sparse, logical.Descriptor.ShapeKind);
Assert.Equal(new[] { 1, 3, 9 }, logical.Descriptor.ExistingIndices.Select(x => x[0]));
```

Build a response containing only the existing sparse elements and assert `MesResponseWritePlanner` writes their original TagIds, not synthetic `Array[0]` paths. Add a partial Bad-value test showing one failed leaf does not replace Good siblings.

- [ ] **Step 2: Run MES array tests and verify the new cases fail**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~MesArrayFlowTests`

Expected: new logical array lookup or sparse compilation assertions fail.

- [ ] **Step 3: Replace duplicate indexed-name logic in the catalog**

Remove `ParseNumericName`, name-only `MesIndexedGroupName` registration, and schema duplication from `MesDataRangeCatalog`. Build `ProjectArrayRecognitionCatalog` once, expose:

```csharp
public bool TryGetLogicalArray(string sourcePath, out MesCatalogLogicalArray source);
```

Keep `TryGetArray` and `TryGetIndexedArray` as compatibility wrappers during this change so existing callers and saved flows continue to load.

- [ ] **Step 4: Compile JSON and reverse writes through leaf bindings**

Update compiler entries to carry `LogicalArrayDescriptor`. Rectangular arrays preserve lower bounds and requested slices. Sparse/jagged arrays enumerate `ExistingIndices` in deterministic lexicographic order. Member selection resolves through `LogicalArrayMember.Path`; reverse writes resolve `(indices, memberPath)` to `LogicalArrayLeafBinding.TagId`.

- [ ] **Step 5: Run MES and project validation regressions**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~MesArrayFlowTests|FullyQualifiedName~MesFlowTemplateOpsTests|FullyQualifiedName~ArrayProjectValidatorTests"`

Expected: all selected tests pass.

- [ ] **Step 6: Commit MES integration**

```powershell
git add src/IGW.Core/MesDataRangeCatalog.cs src/IGW.Core/MesDataRangeValidator.cs src/IGW.Core/MesTaskTopology.cs src/IGW.Northbound.Mes/MesRangePayloadCompiler.cs src/IGW.Northbound.Mes/MesRangeJsonWriter.cs src/IGW.Northbound.Mes/MesResponseWritePlanner.cs tests/IGW.Tests/MesArrayFlowTests.cs
git commit -m "feat: compile MES arrays from recognized structure"
```

### Task 4: Expose the backend range catalog to the Web UI

**Files:**
- Create: `src/IGW.Northbound.RestApi/MesRangeOptionsApi.cs`
- Modify: `src/IGW.Northbound.RestApi/IgwRestApi.cs`
- Modify: `src/IGW.WebUi/src/api.ts`
- Test: `tests/IGW.Tests/MesRangeOptionsApiTests.cs`
- Test: `src/IGW.WebUi/src/api.parameter-options.test.cjs`

**Interfaces:**
- Consumes: current server-side project and `MesDataRangeCatalog`.
- Produces: `GET /api/v1/project/mes-range-options?scopeGroup=...&q=...&cursor=...&limit=200`, returning paged `MesRangeOptionDto` records with stable source paths, shape, members, diagnostics, and `nextCursor`.

- [ ] **Step 1: Write API contract tests**

```csharp
var response = await client.GetFromJsonAsync<MesRangeOptionsResponse>(
    "/api/v1/project/mes-range-options?scopeGroup=Line.Plc.OP200Data&limit=50");
var array = Assert.Single(response!.Items.Where(x => x.Scope == "Array"));
Assert.Equal("Line.Plc.OP200Data.Bolts", array.SourcePath);
Assert.Equal(1400, array.TagCount);
Assert.Empty(array.Children);
```

Add a 10,000-element fixture and assert the JSON response contains one array DTO and at most 50 requested rows, not 10,000 element nodes.

- [ ] **Step 2: Run API tests and verify 404/failure**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~MesRangeOptionsApiTests`

Expected: endpoint returns 404 until mapped.

- [ ] **Step 3: Implement paged DTO projection and endpoint**

Define DTOs in `MesRangeOptionsApi.cs`; do not serialize `MesCatalogGroup` or physical project objects directly. Validate `limit` in `1..500`, bind cancellation, and search the catalog’s precomputed lowercase search text. Return array members separately and omit leaf bindings from the browser response.

- [ ] **Step 4: Add the typed Web API client**

```ts
export const getMesRangeOptions = (params: {
  scopeGroup: string; q?: string; cursor?: string; limit?: number
}, signal?: AbortSignal) =>
  http.get<MesRangeOptionsResponse>('/api/v1/project/mes-range-options', { params, signal })
    .then(response => response.data)
```

- [ ] **Step 5: Run backend and Web API contract tests**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~MesRangeOptionsApiTests`

Run: `cd src/IGW.WebUi; node src/api.parameter-options.test.cjs`

Expected: both pass.

- [ ] **Step 6: Commit the range API**

```powershell
git add src/IGW.Northbound.RestApi/MesRangeOptionsApi.cs src/IGW.Northbound.RestApi/IgwRestApi.cs src/IGW.WebUi/src/api.ts tests/IGW.Tests/MesRangeOptionsApiTests.cs src/IGW.WebUi/src/api.parameter-options.test.cjs
git commit -m "feat: expose paged logical array ranges"
```

### Task 5: Remove frontend array guessing and virtualize selection

**Files:**
- Modify: `src/IGW.WebUi/src/utils/mesDataRanges.ts`
- Modify: `src/IGW.WebUi/src/components/MesDataRangeDialog.vue`
- Modify: `src/IGW.WebUi/src/views/MesFlow.vue`
- Test: `src/IGW.WebUi/src/utils/mesDataRanges.spec.ts`
- Test: `src/IGW.WebUi/src/components/MesDataRangeDialog.ui.test.cjs`
- Test: `src/IGW.WebUi/src/views/MesFlow.fill-all.spec.ts`

**Interfaces:**
- Consumes: `MesRangeOptionsResponse` from Task 4.
- Produces: backend-driven paged search/tree UI and stable selected ranges; `fill all points` adds logical ranges and ordinary scalars without array-element expansion.

- [ ] **Step 1: Write failing UI tests**

Assert that the dialog:

- requests `mes-range-options` instead of calling `numericContainerArray` or `namedSiblingArrays`;
- keeps selected range chips while the user searches or opens another node;
- displays full actual path for duplicate labels;
- removes a selected range from the right pane without mutating other selections;
- renders no more than the requested page for a 10,000-element array;
- `fill all points` inserts one array range plus ordinary scalar mappings.

- [ ] **Step 2: Run focused Web tests and verify failure**

Run: `cd src/IGW.WebUi; .\node_modules\.bin\vitest.cmd run src/utils/mesDataRanges.spec.ts src/views/MesFlow.fill-all.spec.ts`

Run: `cd src/IGW.WebUi; node src/components/MesDataRangeDialog.ui.test.cjs`

Expected: tests fail because local heuristics and full in-memory project traversal are still used.

- [ ] **Step 3: Replace local recognition with DTO adapters**

Delete `pureIndex`, `namedIndex`, `numericContainerArray`, and `namedSiblingArrays` from `mesDataRanges.ts`. Keep pure validation/summary helpers, but make `MesRangeOption` deserialize from the backend DTO. Existing saved range validation still uses source paths and shape metadata.

- [ ] **Step 4: Add cancellable paged loading and stable selection**

In `MesDataRangeDialog.vue`, use one `AbortController` per query, debounce search by 200 ms, append cursor pages, and key selected items by full `sourcePath`. Render selected items in a fixed-height virtualized/scrollable pane. Do not clear `selected` when `query`, current tree node, or loaded page changes.

- [ ] **Step 5: Run UI tests, typecheck, and build**

Run: `cd src/IGW.WebUi; .\node_modules\.bin\vitest.cmd run src/utils/mesDataRanges.spec.ts src/views/MesFlow.fill-all.spec.ts`

Run: `cd src/IGW.WebUi; node src/components/MesDataRangeDialog.ui.test.cjs`

Run: `cd src/IGW.WebUi; .\node_modules\.bin\vue-tsc.cmd --noEmit`

Expected: tests and typecheck pass.

- [ ] **Step 6: Commit the frontend switch**

```powershell
git add src/IGW.WebUi/src/utils/mesDataRanges.ts src/IGW.WebUi/src/components/MesDataRangeDialog.vue src/IGW.WebUi/src/views/MesFlow.vue src/IGW.WebUi/src/utils/mesDataRanges.spec.ts src/IGW.WebUi/src/components/MesDataRangeDialog.ui.test.cjs src/IGW.WebUi/src/views/MesFlow.fill-all.spec.ts
git commit -m "feat: use backend logical arrays in MES UI"
```

### Task 6: Preserve recognition evidence across import/export formats

**Files:**
- Modify: `src/IGW.WebUi/src/interop.ts`
- Modify: `src/IGW.WebUi/src/utils/kepTagImport.ts`
- Modify: `src/IGW.WebUi/src/utils/opcUaStructuredImport.ts`
- Modify: `src/IGW.Drivers.OpcUaClient/OpcUaBrowseWalker.cs`
- Modify: `src/IGW.Core/SiemensDbTypeParser.cs`
- Test: `src/IGW.WebUi/src/interop.kep-project.test.cjs`
- Test: `src/IGW.WebUi/src/utils/kepTagImport.test.cjs`
- Test: `src/IGW.WebUi/src/utils/opcUaStructuredImport.test.cjs`
- Test: `tests/IGW.Tests/OpcUaBrowseWalkerTests.cs`
- Test: `tests/IGW.Tests/SiemensDbArrayImporterTests.cs`

**Interfaces:**
- Consumes: source-format metadata and array definitions from KEP JSON, OPC UA browse, Siemens DB, and IGW JSON.
- Produces: unchanged physical addresses plus `TagDef.Metadata` keys `array.sourceFormat`, `array.sourcePath`, `array.valueRank`, and `array.dimensions` when supplied by the source.

- [ ] **Step 1: Add round-trip fixture tests**

Use `模拟器驱动程序演示 (3).json` as a copied, test-local fixture under `src/IGW.WebUi/src/test-fixtures/kep-expanded-struct-array.json` reduced to two elements and one nested array. Test full production functions, not the external absolute path. Assert import → IGW export → reimport retains:

- the original driver/protocol;
- each physical NodeId;
- one recognized outer array and one nested array member;
- lower bounds and member types;
- JSON aliases.

Add OPC UA tests for `ValueRank=2` and `ArrayDimensions=[2,3]`, and Siemens tests for nested `ARRAY[1..2] OF ARRAY[-1..1] OF Struct`.

- [ ] **Step 2: Run import tests and verify missing evidence fails**

Run: `cd src/IGW.WebUi; node src/interop.kep-project.test.cjs; node src/utils/kepTagImport.test.cjs; node src/utils/opcUaStructuredImport.test.cjs`

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~OpcUaBrowseWalkerTests|FullyQualifiedName~SiemensDbArrayImporterTests"`

Expected: new metadata/round-trip assertions fail.

- [ ] **Step 3: Retain source evidence without changing addresses**

Populate metadata only when the source supplies evidence; do not synthesize array flags from names. OPC UA browsing must copy `ValueRank`, `ArrayDimensions`, DataType NodeId, and current access into the imported tag. Siemens DB parsing continues to generate native `ArrayDef`, including combined nested dimensions. KEP expanded elements keep physical addresses and source tree; the backend recognizer derives logical shape from those addresses.

- [ ] **Step 4: Run round-trip tests**

Run the same commands from Step 2.

Expected: all round-trip and importer tests pass.

- [ ] **Step 5: Commit format evidence preservation**

```powershell
git add src/IGW.WebUi/src/interop.ts src/IGW.WebUi/src/utils/kepTagImport.ts src/IGW.WebUi/src/utils/opcUaStructuredImport.ts src/IGW.Drivers.OpcUaClient/OpcUaBrowseWalker.cs src/IGW.Core/SiemensDbTypeParser.cs src/IGW.WebUi/src/test-fixtures/kep-expanded-struct-array.json src/IGW.WebUi/src/interop.kep-project.test.cjs src/IGW.WebUi/src/utils/kepTagImport.test.cjs src/IGW.WebUi/src/utils/opcUaStructuredImport.test.cjs tests/IGW.Tests/OpcUaBrowseWalkerTests.cs tests/IGW.Tests/SiemensDbArrayImporterTests.cs
git commit -m "feat: preserve array source evidence across formats"
```

### Task 7: Large-array regression and performance acceptance

**Files:**
- Create: `tests/IGW.Tests/ArrayRecognitionPerformanceTests.cs`
- Modify: `tests/IGW.ArrayStress/Program.cs`
- Modify: `src/IGW.WebUi/src/components/MesDataRangeDialog.ui.test.cjs`
- Modify: `docs/superpowers/specs/2026-08-28-capability-driven-plc-io-engine-design.md`

**Interfaces:**
- Consumes: completed recognition catalog, MES compilation, REST paging, and frontend virtualized range selection.
- Produces: repeatable timing/allocation evidence and documented observed limits; no hard-coded pass claim without measured output.

- [ ] **Step 1: Add deterministic scale tests**

Generate 10,000 and 100,000 expanded physical leaves with address patterns and assert:

```csharp
Assert.Single(catalog.LogicalArrays);
Assert.Equal(100_000, catalog.LogicalArrays[0].TagCount);
Assert.DoesNotContain(catalog.Diagnostics, d => d.Severity == "Error");
```

Measure recognition and catalog build with `Stopwatch` and `GC.GetAllocatedBytesForCurrentThread`; log values through `ITestOutputHelper`. Use generous correctness guards (30 seconds and 1.5 GB) so CI variability does not masquerade as product certification.

- [ ] **Step 2: Run scale tests and capture evidence**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~ArrayRecognitionPerformanceTests --logger "console;verbosity=detailed"`

Expected: one descriptor per logical array, bounded completion, no stack overflow, and measured values in output.

- [ ] **Step 3: Extend the stress executable**

Add command-line options `--shape rectangular|sparse|jagged`, `--elements`, `--members`, and `--iterations`. Print recognition time, catalog time, payload compile time, allocated bytes, descriptor count, physical leaf count, and logical range count as JSON.

- [ ] **Step 4: Run 100,000-element stress evidence**

Run: `dotnet run --project tests/IGW.ArrayStress/IGW.ArrayStress.csproj -c Release -- --shape rectangular --elements 100000 --members 7 --iterations 5`

Run: `dotnet run --project tests/IGW.ArrayStress/IGW.ArrayStress.csproj -c Release -- --shape jagged --elements 10000 --members 7 --iterations 5`

Expected: both complete and report one outer logical range without generating N permanent logical configuration rows.

- [ ] **Step 5: Run complete regression gates**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj`

Run: `cd src/IGW.WebUi; .\node_modules\.bin\vitest.cmd run`

Run: `cd src/IGW.WebUi; .\node_modules\.bin\vue-tsc.cmd --noEmit`

Run: `cd src/IGW.WebUi; .\node_modules\.bin\vite.cmd build`

Expected: all commands exit 0. Existing unrelated failures, if any, must be recorded with exact pre-existing evidence and cannot be reported as passing.

- [ ] **Step 6: Record measured evidence and commit**

Append the exact command outputs and environment boundary to the spec’s acceptance section, clearly separating synthetic results from real PLC certification.

```powershell
git add tests/IGW.Tests/ArrayRecognitionPerformanceTests.cs tests/IGW.ArrayStress/Program.cs src/IGW.WebUi/src/components/MesDataRangeDialog.ui.test.cjs docs/superpowers/specs/2026-08-28-capability-driven-plc-io-engine-design.md
git commit -m "test: verify logical arrays at production scale"
```

## Follow-on implementation plans

After this array track is green, create and execute separate plans for:

1. dynamic protocol/field capability discovery and adaptive batch sizing;
2. the per-device read/write scheduler and mandatory write-readback verification with at most ten selective rewrites;
3. protocol-native OPC UA, S7, Modbus, Omron, EtherNet/IP, Mitsubishi, MQTT execution plans and production-scale certification.

These tracks consume the logical array descriptors from Tasks 1–7 and must not reintroduce element expansion or name-based semantics.
