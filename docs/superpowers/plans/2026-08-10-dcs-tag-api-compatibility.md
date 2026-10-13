# DCS Tag API Compatibility Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a complete `/Tag/*` compatibility API so the inspected DCS callers can switch to IGW by changing only the server IP and port, while preserving the native `/api/v1/plc/*` API.

**Architecture:** Add a thin DCS HTTP adapter over an internal testable runtime abstraction. Alias resolution, type conversion, reads/writes, and `AddTags` project mutation remain separate focused units; all PLC writes still pass through `RuntimeEngine.WriteAsync`, and all project changes pass through `ProjectStore.Validate` plus `IGatewayHost.ReloadAsync`.

**Tech Stack:** .NET 10, ASP.NET Core Minimal APIs, `System.Text.Json`, xUnit 2.9.3, `Microsoft.AspNetCore.TestHost` 10.0.9, existing IGW `DataHub`, `RuntimeEngine`, and `ProjectStore`.

## Global Constraints

- Keep every existing `/api/v1/plc/*` route and response unchanged.
- Expose exactly the eleven DCS routes and their original HTTP methods under `/Tag/*`.
- Use camel-case JSON and the common fields `httpStatusCode`, `isSuccess`, `backResult`, `errorMessage`, and `exceptionMessage`.
- Return HTTP 400 only for null, empty, or malformed requests; operational failures use HTTP 200 with `isSuccess=false`.
- Resolve full tag names only; never use fuzzy or leaf-name fallback.
- Validate every batch write item before the first PLC write.
- Route every real write through `RuntimeEngine.WriteAsync`; do not call a driver directly.
- Keep `AddTags` add/update-only, idempotent, and atomic; never delete unrelated project content.
- Preserve non-zero ports; use protocol defaults only when the request port is zero.
- Treat `Inovance_H5U` as an explicitly unsupported communication type in this slice.
- When security is disabled, `/Tag/*` is anonymous. When enabled, use existing HTTP Basic authentication, `ReadBranches`, and `CanWrite`.
- Do not stage or commit the existing unrelated MES and installer changes in the dirty worktree.

---

## File Map

- `src/IGW.Abstractions/ProjectModel.cs`: add optional per-tag metadata needed to preserve DCS-only type identity such as `Date` versus `String` without bloating existing project files.
- `src/IGW.Northbound.RestApi/DcsTagContracts.cs`: DCS enums, request models, response envelope, and JSON options.
- `src/IGW.Northbound.RestApi/DcsTagValueConverter.cs`: automatic scalar, array, date, and type-name conversion.
- `src/IGW.Northbound.RestApi/DcsTagAliasResolver.cs`: deterministic logical DCS path to physical IGW path resolution.
- `src/IGW.Northbound.RestApi/DcsTagRuntime.cs`: narrow runtime interface and production adapter over `IGatewayHost`.
- `src/IGW.Northbound.RestApi/DcsTagOperations.cs`: read, validation, write, retry, confirmation, and result shaping.
- `src/IGW.Northbound.RestApi/DcsTagProjectUpdater.cs`: pure `AddTags` validation and project-clone mutation.
- `src/IGW.Northbound.RestApi/DcsTagCompatibilityApi.cs`: eleven endpoint registrations and Swagger metadata.
- `src/IGW.Northbound.RestApi/DcsTagRoutePolicy.cs`: route classification shared by Runtime security and Swagger filtering.
- `src/IGW.Northbound.RestApi/IGW.Northbound.RestApi.csproj`: expose internals to the test assembly.
- `src/IGW.Runtime/Program.cs`: map the compatibility API and include/protect it correctly.
- `tests/IGW.Tests/IGW.Tests.csproj`: restore the missing test project already referenced by `IGW.slnx`.
- `tests/IGW.Tests/DcsTagTestRuntime.cs`: deterministic in-memory runtime for API tests.
- `tests/IGW.Tests/DcsTagContractsTests.cs`: contract and JSON compatibility tests.
- `tests/IGW.Tests/DcsTagValueConverterTests.cs`: complete type-conversion tests.
- `tests/IGW.Tests/DcsTagAliasResolverTests.cs`: physical/logical/ambiguous-name tests.
- `tests/IGW.Tests/DcsTagOperationsTests.cs`: read/write/error/atomicity tests.
- `tests/IGW.Tests/DcsTagProjectUpdaterTests.cs`: communication mapping and idempotence tests.
- `tests/IGW.Tests/DcsTagCompatibilityApiTests.cs`: real in-memory HTTP and route metadata tests.
- `tests/IGW.Tests/DcsTagRoutePolicyTests.cs`: security and Swagger classification tests.
- `tests/IGW.Tests/PlcApiRegressionTests.cs`: locks the existing native PLC paging and conversion behavior while the parallel adapter is added.

---

### Task 1: Restore the test harness and lock the DCS wire contracts

**Files:**
- Create: `tests/IGW.Tests/IGW.Tests.csproj`
- Create: `tests/IGW.Tests/DcsTagContractsTests.cs`
- Create: `src/IGW.Northbound.RestApi/DcsTagContracts.cs`
- Modify: `src/IGW.Northbound.RestApi/IGW.Northbound.RestApi.csproj`
- Modify: `src/IGW.Abstractions/ProjectModel.cs`

**Interfaces:**
- Produces: `DcsCommunicationType`, `DcsTagDataType`, `DcsReadTagRequest`, `DcsWriteTagRequest`, `DcsWriteTagsRequest`, `DcsTagUpdateModel`, `DcsTagValue`, `DcsKeyValue<T>`, `DcsResponse<T>`, and `DcsJson.Options`.
- Produces: nullable `TagDef.Metadata` and metadata key `DcsTagMetadata.DataType`.

- [ ] **Step 1: Restore the xUnit project and test references**

Create `tests/IGW.Tests/IGW.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="Microsoft.AspNetCore.TestHost" Version="10.0.9" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
  </ItemGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <ProjectReference Include="..\..\src\IGW.Abstractions\IGW.Abstractions.csproj" />
    <ProjectReference Include="..\..\src\IGW.Core\IGW.Core.csproj" />
    <ProjectReference Include="..\..\src\IGW.Northbound.RestApi\IGW.Northbound.RestApi.csproj" />
  </ItemGroup>
</Project>
```

Add this item to `src/IGW.Northbound.RestApi/IGW.Northbound.RestApi.csproj`:

```xml
<ItemGroup>
  <InternalsVisibleTo Include="IGW.Tests" />
</ItemGroup>
```

- [ ] **Step 2: Write failing contract tests**

Create `tests/IGW.Tests/DcsTagContractsTests.cs` with tests that assert the exact enum numbers and wire names:

```csharp
using System.Text.Json;
using IGW.Northbound.RestApi;

namespace IGW.Tests;

public sealed class DcsTagContractsTests
{
    [Fact]
    public void Communication_type_numbers_match_dcs()
    {
        Assert.Equal(0, (int)DcsCommunicationType.OpcUa);
        Assert.Equal(4, (int)DcsCommunicationType.S1500);
        Assert.Equal(8, (int)DcsCommunicationType.Inovance_H5U);
        Assert.Equal(11, (int)DcsCommunicationType.MemoryCache);
    }

    [Fact]
    public void Data_type_numbers_match_dcs()
    {
        Assert.Equal(-1, (int)DcsTagDataType.None);
        Assert.Equal(0, (int)DcsTagDataType.String);
        Assert.Equal(12, (int)DcsTagDataType.Date);
        Assert.Equal(20, (int)DcsTagDataType.StringArray);
        Assert.Equal(28, (int)DcsTagDataType.FloatArray);
    }

    [Fact]
    public void Response_envelope_is_camel_case_and_complete()
    {
        var json = JsonSerializer.Serialize(
            DcsResponse<bool>.Success(true, 200), DcsJson.Options);

        Assert.Equal(
            "{\"httpStatusCode\":200,\"isSuccess\":true,\"backResult\":true,\"errorMessage\":null,\"exceptionMessage\":null}",
            json);
    }

    [Fact]
    public void Write_request_accepts_original_dcs_shape()
    {
        var model = JsonSerializer.Deserialize<DcsWriteTagRequest>(
            "{\"tag\":{\"key\":\"通用.S1500_10.0.0.1.DB1.Value\",\"value\":\"12\"}}",
            DcsJson.Options);

        Assert.Equal("通用.S1500_10.0.0.1.DB1.Value", model!.Tag.Key);
        Assert.Equal("12", model.Tag.Value.GetString());
    }
}
```

- [ ] **Step 3: Run the tests and verify the contracts do not exist yet**

Run:

```powershell
dotnet test tests\IGW.Tests\IGW.Tests.csproj --filter FullyQualifiedName~DcsTagContractsTests
```

Expected: compilation fails because the DCS contract types are not defined.

- [ ] **Step 4: Implement the exact contracts and optional tag metadata**

Create `DcsTagContracts.cs` with the complete numeric enums:

```csharp
internal enum DcsCommunicationType
{
    None = -1, OpcUa = 0, S1200 = 1, S300 = 2, S400 = 3, S1500 = 4,
    S200Smart = 5, S200 = 6, FX5U = 7, Inovance_H5U = 8,
    Omr_NJ = 9, Omr_Fins = 10, MemoryCache = 11,
}

internal enum DcsTagDataType
{
    None = -1, String = 0, Boolean = 1, Char = 2, Byte = 3, Short = 4,
    Word = 5, Long = 6, DWord = 7, Float = 8, Double = 9, Date = 12,
    StringArray = 20, BooleanArray = 21, ByteArray = 23, ShortArray = 24,
    WordArray = 25, LongArray = 26, DWordArray = 27, FloatArray = 28,
}

internal sealed record DcsReadTagRequest(string Tag);
internal sealed record DcsTagValue(string Key, JsonElement Value);
internal sealed record DcsKeyValue<T>(string Key, T? Value);
internal sealed record DcsWriteTagRequest(DcsTagValue Tag);
internal sealed record DcsWriteTagsRequest(List<DcsTagValue> Tags);

internal sealed record DcsTagUpdateModel
{
    public string Ip { get; set; } = "";
    public int Port { get; set; }
    public DcsCommunicationType CommunicationType { get; set; }
    public string DbAddress { get; set; } = "";
    public DcsTagDataType DataType { get; set; }
    public string TagName { get; set; } = "";
}

internal sealed record DcsResponse<T>(
    int HttpStatusCode,
    bool IsSuccess,
    T? BackResult,
    string? ErrorMessage,
    string? ExceptionMessage)
{
    public static DcsResponse<T> Success(T? value, int status = 200) =>
        new(status, true, value, null, null);

    public static DcsResponse<T> Failure(string error, int status = 200, string? exception = null) =>
        new(status, false, default, error, exception);
}

internal static class DcsJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };
}

internal static class DcsTagMetadata
{
    public const string DataType = "dcsDataType";
}
```

Add this nullable property to `TagDef`; nullable is required so existing million-point projects do not serialize an empty dictionary for every point:

```csharp
public Dictionary<string, string>? Metadata { get; set; }
```

- [ ] **Step 5: Run the focused tests**

Run:

```powershell
dotnet test tests\IGW.Tests\IGW.Tests.csproj --filter FullyQualifiedName~DcsTagContractsTests
```

Expected: 4 tests pass.

- [ ] **Step 6: Commit only the contract slice**

```powershell
git add tests/IGW.Tests/IGW.Tests.csproj tests/IGW.Tests/DcsTagContractsTests.cs src/IGW.Northbound.RestApi/IGW.Northbound.RestApi.csproj src/IGW.Northbound.RestApi/DcsTagContracts.cs src/IGW.Abstractions/ProjectModel.cs
git commit -m "test: lock DCS tag API contracts"
```

---

### Task 2: Implement automatic DCS and IGW type conversion

**Files:**
- Create: `tests/IGW.Tests/DcsTagValueConverterTests.cs`
- Create: `src/IGW.Northbound.RestApi/DcsTagValueConverter.cs`

**Interfaces:**
- Consumes: `DcsTagDataType`, `DcsTagMetadata.DataType`, `TagDef`, `IgwDataType`, and `JsonElement`.
- Produces: `TryMapDefinition`, `TryConvertWriteValue`, `GetDcsTypeName`, and `ToWireValue`.

- [ ] **Step 1: Write failing scalar, array, date, and overflow tests**

Create tests containing this matrix and explicit edge cases:

```csharp
[Theory]
[InlineData(DcsTagDataType.String, IgwDataType.String, false, "String")]
[InlineData(DcsTagDataType.Boolean, IgwDataType.Bool, false, "Boolean")]
[InlineData(DcsTagDataType.Char, IgwDataType.Char, false, "Char")]
[InlineData(DcsTagDataType.Byte, IgwDataType.Byte, false, "Byte")]
[InlineData(DcsTagDataType.Short, IgwDataType.Int16, false, "Short")]
[InlineData(DcsTagDataType.Word, IgwDataType.UInt16, false, "Word")]
[InlineData(DcsTagDataType.Long, IgwDataType.Int32, false, "Long")]
[InlineData(DcsTagDataType.DWord, IgwDataType.UInt32, false, "DWord")]
[InlineData(DcsTagDataType.Float, IgwDataType.Float, false, "Float")]
[InlineData(DcsTagDataType.Double, IgwDataType.Double, false, "Double")]
[InlineData(DcsTagDataType.Date, IgwDataType.String, false, "Date")]
[InlineData(DcsTagDataType.StringArray, IgwDataType.String, true, "StringArray")]
[InlineData(DcsTagDataType.BooleanArray, IgwDataType.Bool, true, "BooleanArray")]
[InlineData(DcsTagDataType.ByteArray, IgwDataType.ByteArray, false, "ByteArray")]
[InlineData(DcsTagDataType.ShortArray, IgwDataType.Int16, true, "ShortArray")]
[InlineData(DcsTagDataType.WordArray, IgwDataType.UInt16, true, "WordArray")]
[InlineData(DcsTagDataType.LongArray, IgwDataType.Int32, true, "LongArray")]
[InlineData(DcsTagDataType.DWordArray, IgwDataType.UInt32, true, "DWordArray")]
[InlineData(DcsTagDataType.FloatArray, IgwDataType.Float, true, "FloatArray")]
public void Maps_dcs_type_without_user_configuration(
    DcsTagDataType source, IgwDataType target, bool isArray, string returnedName)
{
    Assert.True(DcsTagValueConverter.TryMapDefinition(source, out var mapped, out var error), error);
    Assert.Equal(target, mapped.DataType);
    Assert.Equal(isArray, mapped.IsArray);
    Assert.Equal(returnedName, DcsTagValueConverter.GetDcsTypeName(mapped));
}
```

Also assert:

```csharp
[Fact]
public void Converts_string_number_to_configured_short()
{
    using var doc = JsonDocument.Parse("\"123\"");
    var tag = new TagDef { DataType = IgwDataType.Int16 };
    Assert.True(DcsTagValueConverter.TryConvertWriteValue(doc.RootElement, tag, out var value, out var error), error);
    Assert.Equal((short)123, value);
}

[Fact]
public void Rejects_byte_overflow_before_write()
{
    using var doc = JsonDocument.Parse("256");
    var tag = new TagDef { DataType = IgwDataType.Byte };
    Assert.False(DcsTagValueConverter.TryConvertWriteValue(doc.RootElement, tag, out _, out var error));
    Assert.Contains("Byte", error);
}

[Fact]
public void Rejects_one_bad_array_element()
{
    using var doc = JsonDocument.Parse("[1,70000,2]");
    var tag = new TagDef { DataType = IgwDataType.UInt16, IsArray = true };
    Assert.False(DcsTagValueConverter.TryConvertWriteValue(doc.RootElement, tag, out _, out var error));
    Assert.Contains("索引 1", error);
}

[Fact]
public void Normalizes_date_to_iso_8601_and_preserves_date_identity()
{
    using var doc = JsonDocument.Parse("\"2026-08-10 12:30:00+08:00\"");
    Assert.True(DcsTagValueConverter.TryMapDefinition(DcsTagDataType.Date, out var tag, out _));
    Assert.True(DcsTagValueConverter.TryConvertWriteValue(doc.RootElement, tag, out var value, out _));
    Assert.Equal("Date", DcsTagValueConverter.GetDcsTypeName(tag));
    Assert.Equal("2026-08-10T12:30:00.0000000+08:00", value);
}
```

- [ ] **Step 2: Run the tests and verify converter symbols are missing**

```powershell
dotnet test tests\IGW.Tests\IGW.Tests.csproj --filter FullyQualifiedName~DcsTagValueConverterTests
```

Expected: compilation fails because `DcsTagValueConverter` is missing.

- [ ] **Step 3: Implement the converter with checked invariant conversions**

Create `DcsTagValueConverter.cs` with these exact public-to-the-assembly entry points:

```csharp
internal static bool TryMapDefinition(
    DcsTagDataType source, out TagDef definition, out string error);

internal static bool TryConvertWriteValue(
    JsonElement source, TagDef target, out object? value, out string error);

internal static string GetDcsTypeName(TagDef definition);

internal static object? ToWireValue(DataValue value);
```

Implementation rules:

```csharp
private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

private static string ScalarText(JsonElement value) => value.ValueKind switch
{
    JsonValueKind.String => value.GetString() ?? "",
    JsonValueKind.Number => value.GetRawText(),
    JsonValueKind.True => "true",
    JsonValueKind.False => "false",
    _ => throw new FormatException("值必须是标量"),
};
```

- Use `checked` and `TryParse(..., NumberStyles.Any, Invariant, ...)` for every integer and floating type.
- For `Bool`, accept JSON booleans, `true`/`false`, and numeric `0`/non-zero.
- For `Char`, produce `sbyte`; do not produce a one-character .NET string.
- For arrays, require `JsonValueKind.Array`, convert each element through the scalar converter, and report the failing index.
- For `ByteArray`, accept either a base64 string or a JSON byte array.
- For `Date`, require a string accepted by `DateTimeOffset.TryParse`, normalize with `ToString("O", Invariant)`, and persist metadata value `Date`.
- For DCS array types, persist the original enum name in `TagDef.Metadata[DcsTagMetadata.DataType]` so `GetTagValueTypeByTagName` remains exact.
- Reject `null`, objects, non-finite numbers, overflow, and unsupported IGW types with a stable Chinese error.

- [ ] **Step 4: Run converter and contract tests**

```powershell
dotnet test tests\IGW.Tests\IGW.Tests.csproj --filter "FullyQualifiedName~DcsTagValueConverterTests|FullyQualifiedName~DcsTagContractsTests"
```

Expected: all focused tests pass.

- [ ] **Step 5: Commit the converter**

```powershell
git add tests/IGW.Tests/DcsTagValueConverterTests.cs src/IGW.Northbound.RestApi/DcsTagValueConverter.cs
git commit -m "feat: add automatic DCS tag type conversion"
```

---

### Task 3: Add deterministic DCS logical alias resolution

**Files:**
- Create: `tests/IGW.Tests/DcsTagAliasResolverTests.cs`
- Create: `src/IGW.Northbound.RestApi/DcsTagAliasResolver.cs`

**Interfaces:**
- Consumes: `IgwProject` and the current physical `DataHub.SortedTagIds`.
- Produces: `DcsTagAliasResolver.TryResolve(string requested, out string physical, out string error)`.
- Produces metadata keys `DcsTagMetadata.LogicalDevice` and `DcsTagMetadata.CommunicationType` for device settings.

- [ ] **Step 1: Write failing resolution tests**

Cover native paths, aliases split across driver-specific channels, case-insensitive matching, missing paths, and ambiguous aliases:

```csharp
[Fact]
public void Resolves_dcs_alias_across_internal_driver_channel()
{
    var project = ProjectWithTag(
        channel: "通用_S1500", device: "S1500_10.1.2.3", tag: "DB1.Value",
        settings: new() { [DcsTagMetadata.LogicalDevice] = "S1500_10.1.2.3" });
    var resolver = new DcsTagAliasResolver(
        project, ["通用_S1500.S1500_10.1.2.3.DB1.Value"]);

    Assert.True(resolver.TryResolve(
        "通用.S1500_10.1.2.3.DB1.Value", out var physical, out var error), error);
    Assert.Equal("通用_S1500.S1500_10.1.2.3.DB1.Value", physical);
}

[Fact]
public void Rejects_ambiguous_alias_instead_of_guessing()
{
    var project = ProjectWithDuplicateLogicalAlias();
    var resolver = new DcsTagAliasResolver(project, AllPhysicalIds(project));

    Assert.False(resolver.TryResolve("通用.S1500_10.1.2.3.DB1.Value", out _, out var error));
    Assert.Contains("不唯一", error);
}
```

- [ ] **Step 2: Run the tests and verify the resolver is missing**

```powershell
dotnet test tests\IGW.Tests\IGW.Tests.csproj --filter FullyQualifiedName~DcsTagAliasResolverTests
```

Expected: compilation fails for missing resolver types.

- [ ] **Step 3: Implement a precomputed, case-insensitive resolver**

The constructor must enumerate physical tag IDs once and build:

```csharp
private readonly Dictionary<string, string> _physical;
private readonly Dictionary<string, string> _aliases;
private readonly HashSet<string> _ambiguous;
```

Use these rules:

```text
physical key = exact IGW full path
logical device = device.Settings["dcsLogicalDevice"]
logical key = "通用." + logicalDevice + "." + relativeTagPath
```

`TryResolve` first accepts an exact physical path, then a DCS alias. It trims surrounding whitespace, compares ordinal-ignore-case, rejects empty names, rejects aliases listed in `_ambiguous`, and returns `点位不存在:<name>` for a miss.

Enumerate root tags and recursively nested group tags so aliases also work for CSV/DB-created structures. Use the physical ID list as the final existence boundary so deleted project nodes cannot remain resolvable from stale configuration.

- [ ] **Step 4: Run focused tests**

```powershell
dotnet test tests\IGW.Tests\IGW.Tests.csproj --filter FullyQualifiedName~DcsTagAliasResolverTests
```

Expected: all alias tests pass.

- [ ] **Step 5: Commit the alias resolver**

```powershell
git add tests/IGW.Tests/DcsTagAliasResolverTests.cs src/IGW.Northbound.RestApi/DcsTagAliasResolver.cs src/IGW.Northbound.RestApi/DcsTagContracts.cs
git commit -m "feat: resolve DCS logical tag aliases"
```

---

### Task 4: Implement DCS read operations over the live cache

**Files:**
- Create: `tests/IGW.Tests/DcsTagTestRuntime.cs`
- Create: `tests/IGW.Tests/DcsTagOperationsTests.cs`
- Create: `src/IGW.Northbound.RestApi/DcsTagRuntime.cs`
- Create: `src/IGW.Northbound.RestApi/DcsTagOperations.cs`

**Interfaces:**
- Produces: `IDcsTagRuntime`, `GatewayDcsTagRuntime`, `DcsApiOutcome<T>`, and `DcsTagOperations`.
- Consumes: converter and alias resolver from Tasks 2-3.

- [ ] **Step 1: Define tests through a narrow fake runtime**

Create `DcsTagTestRuntime` implementing:

```csharp
internal interface IDcsTagRuntime
{
    IgwProject Project { get; }
    bool Reloading { get; }
    IReadOnlyList<string> SortedTagIds { get; }
    bool TryGet(string tagId, out DataValue value);
    bool TryGetDef(string tagId, out TagDef definition);
    void Touch(string tagId);
    Task<StatusCode> WriteAsync(string tagId, object value, CancellationToken cancellationToken);
    Task ReloadAsync(IgwProject project);
}
```

The fake stores definitions and current `DataValue` objects in case-insensitive dictionaries, records `Touched`, `Writes`, and `Reloads`, and lets each test inject write status/read-back behavior.

Add read tests:

```csharp
[Fact]
public void Read_tag_touches_and_returns_good_cached_value()
{
    var runtime = DcsTagTestRuntime.WithGoodTag("通用.Sim.DB1.Value", 42);
    var operations = new DcsTagOperations(runtime);

    var result = operations.ReadOne("通用.Sim.DB1.Value", user: null);

    Assert.Equal(200, result.StatusCode);
    Assert.True(result.Body.IsSuccess);
    Assert.Equal(42, result.Body.BackResult);
    Assert.Contains("通用.Sim.DB1.Value", runtime.Touched);
}

[Fact]
public void Batch_read_preserves_order_and_dcs_json_string_shape()
{
    var runtime = DcsTagTestRuntime.WithGoodTags(("A", 1), ("B", "x"));
    var result = new DcsTagOperations(runtime).ReadAsJsonStrings(["B", "A"], null);

    Assert.Equal(["\"x\"", "1"], result.Body.BackResult);
}

[Fact]
public void Bad_quality_is_http_200_failure()
{
    var runtime = DcsTagTestRuntime.WithBadTag("A", StatusCode.BadNotConnected);
    var result = new DcsTagOperations(runtime).ReadOne("A", null);

    Assert.Equal(200, result.StatusCode);
    Assert.False(result.Body.IsSuccess);
    Assert.Contains("质量", result.Body.ErrorMessage);
}
```

Also test empty input returns 400, unknown tags return a 200 failure, `ReadTagsAsList` returns request-ordered `DcsKeyValue<object?>`, `ReadTagsAsDictionary` uses the requested names as keys, `GetTagValueTypeByTagName` uses converter type names, branch-limited users cannot read aliases outside their resolved physical branch, and `ReadConditionCache` returns successful `null`. Duplicate names passed to a dictionary-returning method are rejected with an HTTP 200 failure envelope instead of allowing `Dictionary.Add` to throw.

- [ ] **Step 2: Run the read tests and verify runtime/operations types are missing**

```powershell
dotnet test tests\IGW.Tests\IGW.Tests.csproj --filter FullyQualifiedName~DcsTagOperationsTests
```

Expected: compilation fails because the runtime abstraction and operations are absent.

- [ ] **Step 3: Implement the production runtime adapter**

`GatewayDcsTagRuntime` delegates without copying data:

```csharp
public IgwProject Project => _host.Project;
public bool Reloading => _host.Reloading;
public IReadOnlyList<string> SortedTagIds => _host.Hub.SortedTagIds;
public bool TryGet(string id, out DataValue value) => _host.Hub.TryGet(id, out value);
public bool TryGetDef(string id, out TagDef def) => _host.Hub.TryGetDef(id, out def);
public void Touch(string id) => _host.Hub.Touch(id);
public Task<StatusCode> WriteAsync(string id, object value, CancellationToken ct) =>
    _host.Engine.WriteAsync(id, value, ct);
public Task ReloadAsync(IgwProject project) => _host.ReloadAsync(project);
```

- [ ] **Step 4: Implement read operations and permission checks**

`DcsTagOperations` rebuilds its alias resolver from `runtime.Project` plus `runtime.SortedTagIds` per request boundary, ensuring project reloads and deletions are immediately reflected. A successful read must:

1. resolve the path;
2. verify the authenticated user's `ReadBranches` against the resolved physical path;
3. call `Touch`;
4. call `TryGet`;
5. require `DataValue.Quality.IsGood`;
6. return `DcsTagValueConverter.ToWireValue`.

Use these outcome factories:

```csharp
internal sealed record DcsApiOutcome<T>(int StatusCode, DcsResponse<T> Body)
{
    public static DcsApiOutcome<T> Ok(T? value) => new(200, DcsResponse<T>.Success(value));
    public static DcsApiOutcome<T> BadRequest(string error) => new(400, DcsResponse<T>.Failure(error, 400));
    public static DcsApiOutcome<T> OperationFailure(string error) => new(200, DcsResponse<T>.Failure(error));
}
```

`ReadTags` serializes each returned value independently with `JsonSerializer.Serialize(value, DcsJson.Options)` to preserve DCS's list-of-JSON-strings behavior.

- [ ] **Step 5: Run all read-operation tests**

```powershell
dotnet test tests\IGW.Tests\IGW.Tests.csproj --filter FullyQualifiedName~DcsTagOperationsTests
```

Expected: all read tests pass and no PLC/network connection is attempted.

- [ ] **Step 6: Commit the read slice**

```powershell
git add tests/IGW.Tests/DcsTagTestRuntime.cs tests/IGW.Tests/DcsTagOperationsTests.cs src/IGW.Northbound.RestApi/DcsTagRuntime.cs src/IGW.Northbound.RestApi/DcsTagOperations.cs
git commit -m "feat: add DCS-compatible cache reads"
```

---

### Task 5: Add atomic validation, writes, retries, and read-back confirmation

**Files:**
- Modify: `tests/IGW.Tests/DcsTagOperationsTests.cs`
- Modify: `tests/IGW.Tests/DcsTagTestRuntime.cs`
- Modify: `src/IGW.Northbound.RestApi/DcsTagOperations.cs`

**Interfaces:**
- Extends: `DcsTagOperations.CheckValidity`, `WriteOneAsync`, and `WriteManyAsync`.
- Consumes: `IDcsTagRuntime.WriteAsync`, `TryGetDef`, `TryGet`, resolver, converter, and `UserDef.CanWrite`.

- [ ] **Step 1: Write failing write and validation tests**

Add these behaviors:

```csharp
[Fact]
public async Task Batch_validation_failure_prevents_every_write()
{
    var runtime = DcsTagTestRuntime.WithWritableTags(
        ("A", new TagDef { DataType = IgwDataType.Byte, Access = TagAccess.ReadWrite }),
        ("B", new TagDef { DataType = IgwDataType.Byte, Access = TagAccess.ReadWrite }));
    var request = DcsTagValues(("A", "1"), ("B", "999"));

    var result = await new DcsTagOperations(runtime).WriteManyAsync(request, WritableUser(), default);

    Assert.False(result.Body.IsSuccess);
    Assert.Empty(runtime.Writes);
}

[Fact]
public async Task Check_validity_never_writes()
{
    var runtime = DcsTagTestRuntime.WithWritableTag("A", IgwDataType.Int16);
    var result = new DcsTagOperations(runtime).CheckValidity(DcsTagValues(("A", "12")), ReadOnlyUser());

    Assert.True(result.Body.IsSuccess);
    Assert.Empty(runtime.Writes);
}

[Fact]
public async Task Write_retries_until_read_back_confirms()
{
    var runtime = DcsTagTestRuntime.WithWritableTag("A", IgwDataType.Int16);
    runtime.ConfirmOnWriteAttempt = 3;

    var result = await new DcsTagOperations(runtime).WriteOneAsync(
        DcsTagValue("A", "12"), WritableUser(), default);

    Assert.True(result.Body.IsSuccess);
    Assert.Equal(3, runtime.Writes.Count);
}
```

Also test read-only target, user without `CanWrite`, driver returning `BadNotConnected`, cancellation, alias write, multiple devices running concurrently, same-device request ordering, and failure after the initial attempt plus two retries.

- [ ] **Step 2: Run the tests and verify write methods are missing/failing**

```powershell
dotnet test tests\IGW.Tests\IGW.Tests.csproj --filter FullyQualifiedName~DcsTagOperationsTests
```

Expected: new write tests fail while read tests remain green.

- [ ] **Step 3: Implement preflight validation**

Create an internal immutable validated item:

```csharp
private sealed record ValidatedWrite(
    int Index,
    string RequestedTag,
    string PhysicalTag,
    string Device,
    object Value,
    TagDef Definition);
```

Preflight rules, in order:

1. request/items non-null and count between 1 and `PlcApi.MaxBatchSize`;
2. key non-empty;
3. alias resolves uniquely;
4. resolved path is allowed by `ReadBranches`;
5. user has `CanWrite` when security supplied a user;
6. definition exists;
7. access is `Write` or `ReadWrite`;
8. value converts to the configured target type.

Return before executing any write if one item fails. Preserve duplicate write entries and their order; do not silently deduplicate them.

- [ ] **Step 4: Implement device-grouped writes and confirmation**

Derive the device path as the first two physical tag segments. Group by device with ordinal-ignore-case, run groups through `Task.WhenAll`, and process each group's entries sequentially.

For each entry, execute at most three attempts:

```csharp
for (int attempt = 0; attempt < 3; attempt++)
{
    runtime.Touch(item.PhysicalTag);
    StatusCode status = await runtime.WriteAsync(item.PhysicalTag, item.Value, ct);
    if (status.IsGood && ReadBackMatches(runtime, item)) return null;
    if (attempt < 2)
        await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(item.Definition.ScanRateMs, 20, 1000)), ct);
}
return $"PLC 写入或回读确认失败:{item.RequestedTag}";
```

`ReadBackMatches` requires a good-quality cached value and compares using target-type-aware equality: exact integral/string/bool/array equality and `1e-6` relative tolerance for `Float`/`Double`.

- [ ] **Step 5: Run the complete operation suite**

```powershell
dotnet test tests\IGW.Tests\IGW.Tests.csproj --filter FullyQualifiedName~DcsTagOperationsTests
```

Expected: read, validation, write, retry, concurrency, and no-partial-write tests all pass.

- [ ] **Step 6: Commit the write slice**

```powershell
git add tests/IGW.Tests/DcsTagOperationsTests.cs tests/IGW.Tests/DcsTagTestRuntime.cs src/IGW.Northbound.RestApi/DcsTagOperations.cs
git commit -m "feat: add confirmed DCS PLC writes"
```

---

### Task 6: Implement idempotent and atomic `AddTags`

**Files:**
- Create: `tests/IGW.Tests/DcsTagProjectUpdaterTests.cs`
- Create: `src/IGW.Northbound.RestApi/DcsTagProjectUpdater.cs`
- Modify: `src/IGW.Northbound.RestApi/DcsTagOperations.cs`
- Modify: `tests/IGW.Tests/DcsTagOperationsTests.cs`

**Interfaces:**
- Produces: `DcsTagProjectUpdater.TryBuild(IgwProject current, IReadOnlyList<DcsTagUpdateModel> items, out IgwProject updated, out string error)`.
- Extends: `DcsTagOperations.AddTagsAsync` to validate/build then call `IDcsTagRuntime.ReloadAsync` once.

- [ ] **Step 1: Write failing project updater tests**

Cover this exact table:

| DCS communication | Driver ID | Default endpoint | Settings |
|---|---|---|---|
| `OpcUa` | `OpcUa.Client` | `opc.tcp://ip:4840` | device endpoint; channel `url` remains unset |
| `S1200` | `Siemens.S7` | `ip:102` | `model=S7-1200` |
| `S300` | `Siemens.S7` | `ip:102` | `model=S7-300` |
| `S400` | `Siemens.S7` | `ip:102` | `model=S7-400` |
| `S1500` | `Siemens.S7` | `ip:102` | `model=S7-1500` |
| `S200Smart` | `Siemens.S7` | `ip:102` | `model=S7-200Smart` |
| `S200` | `Siemens.S7` | `ip:102` | `model=S7-200` |
| `FX5U` | `Mitsubishi.Mc` | `ip:6000` | `mcVersion=Qna_3E` |
| `Omr_NJ` | `Omron.Cip` | `ip:44818` | `path=` |
| `Omr_Fins` | `Omron.Fins` | `ip:9600` | driver defaults |
| `MemoryCache` | `Simulator` | empty endpoint | simulator defaults |

Required tests:

```csharp
[Fact]
public void Non_zero_port_is_preserved()
{
    var item = Update(DcsCommunicationType.S1500, "10.0.0.8", 1102, "DB1.DBW0", "Value", DcsTagDataType.Short);
    Assert.True(DcsTagProjectUpdater.TryBuild(new IgwProject(), [item], out var updated, out var error), error);
    Assert.Equal("10.0.0.8:1102", SingleDevice(updated).Endpoint);
}

[Fact]
public void Repeating_same_add_is_idempotent_and_updates_definition()
{
    var first = Update(DcsCommunicationType.S1500, "10.0.0.8", 0, "DB1.DBW0", "DB1.Value", DcsTagDataType.Short);
    Assert.True(DcsTagProjectUpdater.TryBuild(new IgwProject(), [first], out var once, out _));
    var changed = first with { DbAddress = "DB1.DBW2", DataType = DcsTagDataType.Word };
    Assert.True(DcsTagProjectUpdater.TryBuild(once, [changed], out var twice, out _));

    Assert.Single(AllTags(twice));
    Assert.Equal("DB1.DBW2", AllTags(twice).Single().Address);
    Assert.Equal(IgwDataType.UInt16, AllTags(twice).Single().DataType);
}

[Fact]
public void Unsupported_h5u_does_not_mutate_input_project()
{
    var original = ExistingProject();
    string before = ProjectStore.ToJson(original);
    var item = Update(DcsCommunicationType.Inovance_H5U, "10.0.0.9", 0, "D0", "Value", DcsTagDataType.Short);

    Assert.False(DcsTagProjectUpdater.TryBuild(original, [item], out _, out var error));
    Assert.Contains("暂不支持", error);
    Assert.Equal(before, ProjectStore.ToJson(original));
}
```

Also test mixed Siemens/OPC UA devices create deterministic internal channels while both aliases resolve under `通用`, blank IP/address/name rejection, bad enum rejection, duplicate items in one request updating rather than duplicating, and existing unrelated channels/devices/tags remaining byte-for-byte equivalent after JSON round-trip.

- [ ] **Step 2: Run tests and verify updater is missing**

```powershell
dotnet test tests\IGW.Tests\IGW.Tests.csproj --filter FullyQualifiedName~DcsTagProjectUpdaterTests
```

Expected: compilation fails because `DcsTagProjectUpdater` is absent.

- [ ] **Step 3: Implement validation, naming, and clone mutation**

Use `ProjectStore.Clone(current)` before mutation. Validate every request item before cloning.

Persist on every DCS-managed device:

```csharp
device.Settings[DcsTagMetadata.LogicalDevice] = $"{item.CommunicationType}_{item.Ip}";
device.Settings[DcsTagMetadata.CommunicationType] = item.CommunicationType.ToString();
```

Channel selection:

1. reuse an existing DCS-managed channel with the same driver ID;
2. otherwise use `通用` if absent or already uses the same driver;
3. otherwise use `通用_<CommunicationType>`;
4. if that name is occupied by a different driver, append `_2`, `_3`, and so on deterministically.

Device selection uses the persisted logical-device key, not endpoint alone, and updates that device's endpoint when a repeated call changes the port. Tag selection matches case-insensitively by address first and name second: if one existing tag matches either identity, update it; if address and name point to two different tags, reject the request as a conflict instead of merging or duplicating them. Update `Name`, `Address`, `DataType`, `IsArray`, metadata, and default `Access=ReadWrite`, `ScanRateMs=100` without replacing unrelated tags.

Generate stable IDs for new objects using `Guid.NewGuid().ToString("N")`. Call `ProjectStore.Validate(clone)` before returning success.

- [ ] **Step 4: Add the atomic runtime operation test and implementation**

Add:

```csharp
[Fact]
public async Task Add_tags_reload_failure_returns_failure_and_never_changes_live_project()
{
    var runtime = DcsTagTestRuntime.WithProject(ExistingProject());
    runtime.ReloadFailure = new InvalidOperationException("reload failed");
    string before = ProjectStore.ToJson(runtime.Project);

    var result = await new DcsTagOperations(runtime).AddTagsAsync([ValidUpdate()], WritableUser(), default);

    Assert.False(result.Body.IsSuccess);
    Assert.Equal(before, ProjectStore.ToJson(runtime.Project));
    Assert.Single(runtime.Reloads);
}
```

`AddTagsAsync` checks `CanWrite`, calls `TryBuild`, calls `ReloadAsync` exactly once, catches validation/reload exceptions into a 200 failure envelope, and never assigns to the current project directly. On success it returns `DcsResponse<string?>.Success(null)`, matching the inspected DCS controller, which sets `isSuccess=true` but leaves its string `backResult` null.

- [ ] **Step 5: Run updater and operation tests**

```powershell
dotnet test tests\IGW.Tests\IGW.Tests.csproj --filter "FullyQualifiedName~DcsTagProjectUpdaterTests|FullyQualifiedName~DcsTagOperationsTests"
```

Expected: all updater, atomicity, and existing operation tests pass.

- [ ] **Step 6: Commit the AddTags slice**

```powershell
git add tests/IGW.Tests/DcsTagProjectUpdaterTests.cs tests/IGW.Tests/DcsTagOperationsTests.cs src/IGW.Northbound.RestApi/DcsTagProjectUpdater.cs src/IGW.Northbound.RestApi/DcsTagOperations.cs
git commit -m "feat: add atomic DCS tag project updates"
```

---

### Task 7: Register and verify all eleven HTTP endpoints

**Files:**
- Create: `tests/IGW.Tests/DcsTagCompatibilityApiTests.cs`
- Create: `src/IGW.Northbound.RestApi/DcsTagCompatibilityApi.cs`

**Interfaces:**
- Produces: `MapDcsTagCompatibilityApi(this IEndpointRouteBuilder app, IGatewayHost host)`.
- Produces internal overload: `MapDcsTagCompatibilityApi(this IEndpointRouteBuilder app, IDcsTagRuntime runtime)` for in-memory HTTP tests.
- Consumes all `DcsTagOperations` methods.

- [ ] **Step 1: Write a route metadata test for all methods and paths**

Using `WebApplication.CreateBuilder()`, `builder.WebHost.UseTestServer()`, and the internal runtime overload, assert this exact set:

```csharp
var expected = new HashSet<(string Method, string Route)>
{
    ("GET", "/Tag/ReadConditionCache"),
    ("POST", "/Tag/ReadTag"),
    ("POST", "/Tag/ReadTag1"),
    ("POST", "/Tag/ReadTags"),
    ("POST", "/Tag/ReadTagsAsList"),
    ("POST", "/Tag/ReadTagsAsDictionary"),
    ("POST", "/Tag/WriteTag"),
    ("POST", "/Tag/WriteTags"),
    ("POST", "/Tag/GetTagValueTypeByTagName"),
    ("POST", "/Tag/CheckTagValueValidity"),
    ("POST", "/Tag/AddTags"),
};
```

Read endpoint metadata from `EndpointDataSource`, including `HttpMethodMetadata`, and assert exact equality with no missing or extra `/Tag` routes.

- [ ] **Step 2: Add failing in-memory HTTP contract tests**

Test representative request and response shapes through `HttpClient`:

```csharp
[Fact]
public async Task ReadTag1_uses_dcs_request_and_response_shape()
{
    await using var app = await DcsTestApp.StartAsync(DcsTagTestRuntime.WithGoodTag("A", 7));
    var response = await app.Client.PostAsJsonAsync("/Tag/ReadTag1", new { tag = "A" });
    string json = await response.Content.ReadAsStringAsync();

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Equal(
        "{\"httpStatusCode\":200,\"isSuccess\":true,\"backResult\":7,\"errorMessage\":null,\"exceptionMessage\":null}",
        json);
}
```

Also assert:

- `ReadTag` receives `tag` from the query string.
- `ReadTags` returns `backResult:["1","\"x\""]`.
- `GetTagValueTypeByTagName` returns a direct dictionary, not an envelope.
- empty arrays and missing JSON bodies return HTTP 400 envelopes.
- write failures remain HTTP 200 failures.
- malformed JSON uses a JSON DCS envelope instead of the ASP.NET HTML error page.

- [ ] **Step 3: Run tests and verify no routes are registered**

```powershell
dotnet test tests\IGW.Tests\IGW.Tests.csproj --filter FullyQualifiedName~DcsTagCompatibilityApiTests
```

Expected: route and HTTP tests fail because the API mapper is absent.

- [ ] **Step 4: Implement the endpoint mapper**

Create one `MapGroup("/Tag").WithTags("DCS 兼容接口")`. Each endpoint:

- binds the original DCS request shape;
- extracts `UserDef?` from `HttpContext.Items[PlcApi.AuthenticatedUserItemKey]`;
- calls one operation method;
- returns `Results.Json(outcome.Body, DcsJson.Options, statusCode: outcome.StatusCode)`;
- declares `.Accepts<T>("application/json")`, `.Produces<DcsResponse<T>>()`, Chinese summary, and unique operation name.

For `GetTagValueTypeByTagName`, return the direct dictionary on success and a DCS envelope only on an error, matching the inspected DCS behavior.

Do not rely on Minimal API automatic complex-body binding because deserialization can fail before an endpoint filter runs. For every JSON-body route, accept `HttpRequest`, call a shared `ReadBodyAsync<T>` helper using `JsonSerializer.DeserializeAsync<T>(request.Body, DcsJson.Options, ct)`, and convert `JsonException`, an empty body, or a deserialized null into `DcsResponse<object>.Failure("请求 JSON 无效", 400)`. This keeps malformed requests in the DCS envelope without changing global Runtime error handling.

- [ ] **Step 5: Run route and HTTP tests**

```powershell
dotnet test tests\IGW.Tests\IGW.Tests.csproj --filter FullyQualifiedName~DcsTagCompatibilityApiTests
```

Expected: all eleven route checks and HTTP snapshots pass.

- [ ] **Step 6: Commit the HTTP adapter**

```powershell
git add tests/IGW.Tests/DcsTagCompatibilityApiTests.cs src/IGW.Northbound.RestApi/DcsTagCompatibilityApi.cs
git commit -m "feat: expose DCS-compatible Tag endpoints"
```

---

### Task 8: Integrate security and Swagger without exposing internal APIs

**Files:**
- Create: `tests/IGW.Tests/DcsTagRoutePolicyTests.cs`
- Create: `tests/IGW.Tests/PlcApiRegressionTests.cs`
- Create: `src/IGW.Northbound.RestApi/DcsTagRoutePolicy.cs`
- Modify: `src/IGW.Runtime/Program.cs`
- Modify: `tests/IGW.Tests/DcsTagCompatibilityApiTests.cs`

**Interfaces:**
- Produces public `DcsTagRoutePolicy.IsProtectedPath`, `RequiresWritePermission`, and `IsPublicSwaggerPath` so `IGW.Runtime` can consume them.
- Consumes: route paths from Task 7 and existing `PlcApi.AuthenticatedUserItemKey`.

- [ ] **Step 1: Write failing route-policy tests**

```csharp
[Theory]
[InlineData("/Tag/WriteTag", true)]
[InlineData("/Tag/WriteTags", true)]
[InlineData("/Tag/AddTags", true)]
[InlineData("/Tag/ReadTag", false)]
[InlineData("/Tag/ReadTags", false)]
[InlineData("/Tag/CheckTagValueValidity", false)]
public void Classifies_dcs_write_permission(string path, bool expected)
{
    Assert.Equal(expected, DcsTagRoutePolicy.RequiresWritePermission(path));
}

[Theory]
[InlineData("api/v1/plc/read", true)]
[InlineData("Tag/ReadTag1", true)]
[InlineData("api/v1/project", false)]
[InlineData("api/v1/license", false)]
[InlineData("api/v1/mes", false)]
public void Swagger_includes_only_public_plc_families(string relativePath, bool expected)
{
    Assert.Equal(expected, DcsTagRoutePolicy.IsPublicSwaggerPath(relativePath));
}
```

Add an HTTP test with a small authentication middleware mirroring `Program.cs` to prove disabled security allows anonymous reads, enabled security returns 401 without Basic credentials, read-only users receive 403 on the three write routes, branch-limited users are rejected after alias resolution, and writable users succeed.

Create `PlcApiRegressionTests.cs` before modifying `Program.cs` and lock the existing native helpers:

```csharp
public sealed class PlcApiRegressionTests
{
    [Fact]
    public void Native_tag_page_keeps_filter_order_and_page_shape()
    {
        var page = PlcApi.BuildTagPage(
            ["C.D.G.A", "C.D.G.B", "C.E.G.C"],
            device: "C.D", query: "G.", skip: 1, take: 1);

        Assert.Equal(2, page.Total);
        Assert.Equal(["C.D.G.B"], page.Items);
    }

    [Fact]
    public void Native_write_converter_keeps_existing_short_conversion()
    {
        using var json = JsonDocument.Parse("123");
        Assert.True(PlcApi.TryConvertValue(json.RootElement, IgwDataType.Int16, out var value, out var error), error);
        Assert.Equal((short)123, value);
    }
}
```

- [ ] **Step 2: Run the tests and verify the policy is missing**

```powershell
dotnet test tests\IGW.Tests\IGW.Tests.csproj --filter "FullyQualifiedName~DcsTagRoutePolicyTests|FullyQualifiedName~DcsTagCompatibilityApiTests|FullyQualifiedName~PlcApiRegressionTests"
```

Expected: policy tests fail for missing `DcsTagRoutePolicy`.

- [ ] **Step 3: Implement route policy and update Runtime middleware**

The policy must use exact path comparisons:

```csharp
public static bool IsProtectedPath(string path) =>
    path.StartsWith("/api/v1", StringComparison.OrdinalIgnoreCase)
    || path.Equals("/Tag", StringComparison.OrdinalIgnoreCase)
    || path.StartsWith("/Tag/", StringComparison.OrdinalIgnoreCase);

public static bool RequiresWritePermission(string path) =>
    path.Equals("/Tag/WriteTag", StringComparison.OrdinalIgnoreCase)
    || path.Equals("/Tag/WriteTags", StringComparison.OrdinalIgnoreCase)
    || path.Equals("/Tag/AddTags", StringComparison.OrdinalIgnoreCase);

public static bool IsPublicSwaggerPath(string? path) =>
    path?.StartsWith("api/v1/plc", StringComparison.OrdinalIgnoreCase) == true
    || path?.StartsWith("Tag/", StringComparison.OrdinalIgnoreCase) == true;
```

In `Program.cs`:

```csharp
if (sec is { Enabled: true } && DcsTagRoutePolicy.IsProtectedPath(path))
```

Keep the existing native API read/write classification, then OR in `DcsTagRoutePolicy.RequiresWritePermission(path)`. Do not classify DCS POST reads or `CheckTagValueValidity` as writes.

- [ ] **Step 4: Map DCS endpoints and update Swagger visibility**

Add:

```csharp
app.MapDcsTagCompatibilityApi(host);
```

Change `DocInclusionPredicate` to call `DcsTagRoutePolicy.IsPublicSwaggerPath(api.RelativePath)`. Update Swagger title/description to state that both native IGW and DCS-compatible PLC operations are available. Keep configuration, MES, authorization, and licensing routes excluded.

- [ ] **Step 5: Run policy, endpoint, and full test suites**

```powershell
dotnet test tests\IGW.Tests\IGW.Tests.csproj --filter "FullyQualifiedName~DcsTagRoutePolicyTests|FullyQualifiedName~DcsTagCompatibilityApiTests"
dotnet test tests\IGW.Tests\IGW.Tests.csproj
```

Expected: focused tests pass, then the entire restored test project passes.

- [ ] **Step 6: Commit Runtime integration**

```powershell
git add tests/IGW.Tests/DcsTagRoutePolicyTests.cs tests/IGW.Tests/DcsTagCompatibilityApiTests.cs tests/IGW.Tests/PlcApiRegressionTests.cs src/IGW.Northbound.RestApi/DcsTagRoutePolicy.cs src/IGW.Runtime/Program.cs
git commit -m "feat: integrate DCS API security and Swagger"
```

---

### Task 9: Run regression, portability, and clean-diff verification

**Files:**
- Modify only if a verified failure requires it: files introduced by Tasks 1-8.
- Do not modify or stage existing MES/UI/installer dirty files.

**Interfaces:**
- Consumes: complete DCS compatibility implementation.
- Produces: evidence that contracts, build, Swagger exposure, and existing native APIs remain intact.

- [ ] **Step 1: Restore dependencies and run all .NET tests**

```powershell
dotnet restore tests\IGW.Tests\IGW.Tests.csproj
dotnet test tests\IGW.Tests\IGW.Tests.csproj --no-restore --logger "console;verbosity=normal"
```

Expected: zero failed tests. If package restore is unavailable, report restore as blocked; do not describe skipped tests as passed.

- [ ] **Step 2: Build the production Runtime**

```powershell
dotnet build src\IGW.Runtime\IGW.Runtime.csproj -c Release --no-restore --nologo
```

Expected: build succeeds with zero errors.

- [ ] **Step 3: Verify route inventory and contract snapshots again**

```powershell
dotnet test tests\IGW.Tests\IGW.Tests.csproj --no-restore --filter "FullyQualifiedName~DcsTagContractsTests|FullyQualifiedName~DcsTagCompatibilityApiTests|FullyQualifiedName~DcsTagRoutePolicyTests" --logger "console;verbosity=detailed"
```

Expected: all eleven route/method pairs, JSON snapshots, security rules, and Swagger visibility checks pass.

- [ ] **Step 4: Run repository regression checks unaffected by this backend change**

```powershell
node --test src\IGW.WebUi\src\views\*.test.cjs
dotnet build src\IGW.Northbound.RestApi\IGW.Northbound.RestApi.csproj -c Release --no-restore --nologo
```

Expected: existing Web UI Node tests and REST API compilation pass. Record unrelated pre-existing failures separately instead of changing unrelated files.

- [ ] **Step 5: Inspect the final diff and staged-file boundary**

```powershell
git diff --check
git status --short
git diff -- src/IGW.Abstractions/ProjectModel.cs src/IGW.Northbound.RestApi src/IGW.Runtime/Program.cs tests/IGW.Tests
```

Expected: no whitespace errors; DCS changes are limited to the planned files; the pre-existing MES/UI/installer modifications remain unstaged and untouched.

- [ ] **Step 6: Commit any verification-only corrections, then show history**

If verification required corrections, stage only those exact DCS files and commit:

```powershell
git add src/IGW.Abstractions/ProjectModel.cs src/IGW.Northbound.RestApi src/IGW.Runtime/Program.cs tests/IGW.Tests
git commit -m "fix: complete DCS API compatibility verification"
git log --oneline -8
```

If no correction was needed, do not create an empty commit; run only:

```powershell
git log --oneline -8
```

Expected: the design commit and the focused implementation commits are visible, with no unrelated MES or installer files included.
