# OPC UA Channel Browse and KEP CSV Import Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make an OPC UA channel own its server connection, preserve KEP CSV hierarchy, and let users browse and selectively import OPC UA variables into a device.

**Architecture:** Keep the existing `ChannelDef -> DeviceDef -> TagGroupDef -> TagDef` model. Extend browse results with hierarchy and array metadata, add a temporary preview connection through `IGatewayHost`, and isolate CSV/tree merging in tested TypeScript utilities so `Project.vue` only coordinates dialogs and project mutations.

**Tech Stack:** .NET 10, OPC Foundation UA .NET Standard, ASP.NET Core minimal APIs, Vue 3, TypeScript, Element Plus, Node test runner, xUnit.

## Global Constraints

- One new OPC UA channel represents exactly one upstream OPC UA server.
- `channel.settings.url` is authoritative; `device.endpoint` remains only as a backward-compatible fallback and must not be deleted from serialized projects.
- Existing channels with different device endpoints must not be silently rewritten.
- Browse preview must not persist credentials or mutate the project.
- Browse results are capped at 10,000 variables and must report truncation explicitly.
- CSV parse failures, unsupported types, and path conflicts must never silently degrade or partially mutate the project.
- Preserve unrelated dirty-worktree changes and stage only task-owned files or hunks.

---

### Task 1: OPC UA connection ownership and array metadata

**Files:**
- Modify: `src/IGW.Abstractions/ProjectModel.cs`
- Modify: `src/IGW.Abstractions/Drivers.cs`
- Modify: `src/IGW.Drivers.OpcUaClient/OpcUaClientDriverFactory.cs`
- Modify: `src/IGW.Northbound.OpcUaServer/IgwNodeManager.cs`
- Modify: `tests/IGW.Tests/IGW.Tests.csproj`
- Create: `tests/IGW.Tests/OpcUaConfigurationTests.cs`

**Interfaces:**
- Produces: `TagDef.IsArray : bool`.
- Produces: `BrowsedTag.Path : string`, `BrowsedTag.IsArray : bool`.
- Produces: `OpcUaClientDriverFactory.ResolveUrl(ChannelDef, DeviceDef) : string`.

- [ ] **Step 1: Write failing configuration and array tests**

```csharp
[Fact]
public void 通道URL优先于设备旧端点()
{
    var channel = new ChannelDef { Settings = { ["url"] = "opc.tcp://new:4840" } };
    var device = new DeviceDef { Endpoint = "opc.tcp://legacy:4840" };
    Assert.Equal("opc.tcp://new:4840", OpcUaClientDriverFactory.ResolveUrl(channel, device));
}

[Fact]
public void 通道URL为空时保留旧设备端点回退()
{
    var channel = new ChannelDef();
    var device = new DeviceDef { Endpoint = "legacy:49320" };
    Assert.Equal("opc.tcp://legacy:49320", OpcUaClientDriverFactory.ResolveUrl(channel, device));
}

[Fact]
public void 数组标记JSON往返保留IsArray()
{
    var json = JsonSerializer.Serialize(new TagDef { Name = "Serials", DataType = IgwDataType.String, IsArray = true });
    Assert.True(JsonSerializer.Deserialize<TagDef>(json)!.IsArray);
}
```

- [ ] **Step 2: Run the focused tests and verify RED**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~OpcUaConfigurationTests`

Expected: FAIL because `IsArray` and `ResolveUrl` do not exist and the test project does not yet reference the OPC UA Client driver.

- [ ] **Step 3: Add the minimal model and resolution code**

```csharp
public sealed class BrowsedTag
{
    public required string Name { get; init; }
    public required string Address { get; init; }
    public string Path { get; init; } = "";
    public IgwDataType DataType { get; init; } = IgwDataType.Int16;
    public bool IsArray { get; init; }
}

public sealed class TagDef
{
    public bool IsArray { get; set; }
}

public static string ResolveUrl(ChannelDef channel, DeviceDef device)
{
    string url = channel.Settings.GetValueOrDefault("url", "").Trim();
    if (url.Length == 0) url = device.Endpoint.Trim();
    if (url.Length == 0) url = "opc.tcp://localhost:4840";
    return url.Contains("://", StringComparison.Ordinal) ? url : "opc.tcp://" + url;
}
```

Update `CreateDevice` to call `ResolveUrl`. In `IgwNodeManager.CreateTag`, set `ValueRank = def.IsArray ? ValueRanks.OneDimension : ValueRanks.Scalar`.

- [ ] **Step 4: Run focused tests and build affected projects**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~OpcUaConfigurationTests`

Expected: PASS.

Run: `dotnet build src/IGW.Runtime/IGW.Runtime.csproj --nologo`

Expected: exit 0.

- [ ] **Step 5: Commit Task 1**

```powershell
git add -- src/IGW.Abstractions/ProjectModel.cs src/IGW.Abstractions/Drivers.cs src/IGW.Drivers.OpcUaClient/OpcUaClientDriverFactory.cs src/IGW.Northbound.OpcUaServer/IgwNodeManager.cs tests/IGW.Tests/IGW.Tests.csproj tests/IGW.Tests/OpcUaConfigurationTests.cs
git commit -m "feat: move OPC UA server URL to channel"
```

### Task 2: Hierarchical OPC UA browse and temporary preview API

**Files:**
- Modify: `src/IGW.Abstractions/Drivers.cs`
- Modify: `src/IGW.Core/IGatewayHost.cs`
- Modify: `src/IGW.Drivers.OpcUaClient/OpcUaClientDeviceDriver.cs`
- Modify: `src/IGW.Runtime/GatewayHost.cs`
- Modify: `src/IGW.Northbound.RestApi/IgwRestApi.cs`
- Create: `tests/IGW.Tests/OpcUaBrowseTreeTests.cs`

**Interfaces:**
- Produces: `BrowseResult(IReadOnlyList<BrowsedTag> Tags, bool Truncated)`.
- Produces: `BrowsePreviewRequest(string Url, string SecurityPolicy, bool Anonymous, string Username, string Password, int TimeoutMs)`.
- Produces: `IOpcUaBrowseSource.Children(string)` and `IOpcUaBrowseSource.TypeOf(string)` for a testable traversal boundary.
- Produces: `IGatewayHost.BrowsePreviewAsync(BrowsePreviewRequest, CancellationToken)`.
- HTTP: `POST /api/v1/opcua/browse-preview` returns `{ tags, truncated }`.

- [ ] **Step 1: Write failing hierarchy, array, and limit tests**

```csharp
[Fact]
public void 浏览树保留父路径和数组属性()
{
    var source = FakeBrowseSource.Tree(
        Obj("PLC4", Obj("LogOff1", Var("Serials", "ns=3;s=Serials", IgwDataType.String, isArray: true))));
    var result = OpcUaBrowseWalker.Walk(source, "Objects", 10_000, CancellationToken.None);
    var tag = Assert.Single(result.Tags);
    Assert.Equal("PLC4.LogOff1", tag.Path);
    Assert.True(tag.IsArray);
}

[Fact]
public void 超过上限明确标记截断()
{
    var source = FakeBrowseSource.Variables(10_001);
    var result = OpcUaBrowseWalker.Walk(source, "Objects", 10_000, CancellationToken.None);
    Assert.Equal(10_000, result.Tags.Count);
    Assert.True(result.Truncated);
}
```

- [ ] **Step 2: Run browse tests and verify RED**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~OpcUaBrowseTreeTests`

Expected: FAIL because `BrowseResult` and `OpcUaBrowseWalker` do not exist.

- [ ] **Step 3: Implement a testable walker and adapt the OPC UA session**

```csharp
public sealed record BrowseResult(IReadOnlyList<BrowsedTag> Tags, bool Truncated);
public sealed record OpcUaBrowseChild(string Id, string Name, bool IsVariable);
public sealed record OpcUaBrowseType(IgwDataType DataType, bool IsArray);
public interface IOpcUaBrowseSource
{
    IReadOnlyList<OpcUaBrowseChild> Children(string nodeId);
    OpcUaBrowseType TypeOf(string nodeId);
}

public static BrowseResult Walk(IOpcUaBrowseSource source, string root, int limit, CancellationToken ct)
{
    var tags = new List<BrowsedTag>(Math.Min(limit, 1024));
    bool truncated = false;
    void Visit(string id, string path, int depth)
    {
        ct.ThrowIfCancellationRequested();
        if (depth > 32 || truncated) return;
        foreach (var child in source.Children(id))
        {
            if (child.IsVariable)
            {
                if (tags.Count >= limit) { truncated = true; return; }
                var type = source.TypeOf(child.Id);
                tags.Add(new BrowsedTag { Name = child.Name, Address = child.Id, Path = path, DataType = type.DataType, IsArray = type.IsArray });
            }
            else Visit(child.Id, path.Length == 0 ? child.Name : path + "." + child.Name, depth + 1);
        }
    }
    Visit(root, "", 0);
    return new BrowseResult(tags, truncated);
}
```

Use the walker from `OpcUaClientDeviceDriver.BrowseAsync`. Determine arrays with `value is Array`; map the first non-null element to the base `IgwDataType`.

- [ ] **Step 4: Add temporary preview connection and REST endpoint**

```csharp
public async Task<BrowseResult> BrowsePreviewAsync(BrowsePreviewRequest request, CancellationToken ct)
{
    if (!_registry.TryGet("OpcUa.Client", out var factory)) throw new NotSupportedException("OPC UA Client 驱动未注册");
    var channel = new ChannelDef { DriverId = "OpcUa.Client" };
    channel.Settings["url"] = request.Url;
    channel.Settings["securityPolicy"] = request.SecurityPolicy;
    channel.Settings["anonymous"] = request.Anonymous ? "true" : "false";
    channel.Settings["username"] = request.Username;
    channel.Settings["password"] = request.Password;
    channel.Settings["timeoutMs"] = request.TimeoutMs.ToString(CultureInfo.InvariantCulture);
    var device = new DeviceDef();
    await using var driver = factory.CreateDevice(channel, device);
    await driver.ConnectAsync(ct);
    try { return await ((IBrowsableDriver)driver).BrowseAsync(ct); }
    finally { await driver.DisconnectAsync(); }
}
```

Validate URL, timeout range `1_000..120_000`, and driver capability before connecting. Map `IOException`, OPC service errors, and authentication errors to HTTP 400 with their message; cancellation maps to the existing request cancellation behavior.

- [ ] **Step 5: Run focused tests and Runtime build**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~OpcUaBrowseTreeTests|FullyQualifiedName~OpcUaConfigurationTests"`

Expected: PASS.

Run: `dotnet build src/IGW.Runtime/IGW.Runtime.csproj --nologo`

Expected: exit 0.

- [ ] **Step 6: Commit Task 2**

```powershell
git add -- src/IGW.Abstractions/Drivers.cs src/IGW.Core/IGatewayHost.cs src/IGW.Drivers.OpcUaClient/OpcUaClientDeviceDriver.cs src/IGW.Runtime/GatewayHost.cs src/IGW.Northbound.RestApi/IgwRestApi.cs tests/IGW.Tests/OpcUaBrowseTreeTests.cs
git commit -m "feat: browse OPC UA hierarchy for selective import"
```

### Task 3: Robust KEP CSV parser and hierarchy merger

**Files:**
- Create: `src/IGW.WebUi/src/utils/kepTagImport.ts`
- Create: `src/IGW.WebUi/src/utils/kepTagImport.test.cjs`

**Interfaces:**
- Produces: `parseKepTagCsv(text: string): KepCsvParseResult`.
- Produces: `buildImportPreview(target, tags): TagImportPreview`.
- Produces: `applyTagImport(target, preview, overwriteConflicts): TagImportResult`.

- [ ] **Step 1: Write failing tests using an embedded equivalent of `PLC4.csv`**

```javascript
test('PLC4样本恢复44点和嵌套组并保留NodeId引号', () => {
  const result = imp.parseKepTagCsv(plc4Fixture)
  assert.equal(result.errors.length, 0)
  assert.equal(result.tags.length, 44)
  assert.deepEqual(result.tags.find(x => x.name === 'ProductNo').groupPath, ['LogOff1', 'AESCM2E_1'])
  assert.equal(result.tags[0].address, 'ns=3;s="AESCE2M_1"."EACEQPTINIT"."EquipmentInfo_ResultFlag"')
})

test('String Array保留字符串数组属性', () => {
  const row = result.tags.find(x => x.name.endsWith('SerialNoArray'))
  assert.equal(row.dataType, 'string')
  assert.equal(row.isArray, true)
})

test('解析错误和冲突不会修改目标树', () => {
  const before = structuredClone(target)
  const preview = imp.buildImportPreview(target, [{ groupPath: ['G'], name: 'T', address: 'new' }])
  assert.equal(preview.conflicts.length, 1)
  assert.deepEqual(target, before)
})
```

- [ ] **Step 2: Run parser tests and verify RED**

Run: `node --test src/IGW.WebUi/src/utils/kepTagImport.test.cjs`

Expected: FAIL because `kepTagImport.ts` does not exist.

- [ ] **Step 3: Implement record parsing, type mapping, preview, and atomic merge**

```ts
export interface ImportedTag {
  groupPath: string[]
  name: string
  address: string
  dataType: string
  isArray: boolean
  access: string
  scanRateMs: number
  description: string
  row: number
}

const TYPE_MAP: Record<string, { dataType: string; isArray: boolean }> = {
  boolean: { dataType: 'bool', isArray: false },
  short: { dataType: 'int16', isArray: false },
  float: { dataType: 'float', isArray: false },
  string: { dataType: 'string', isArray: false },
  'string array': { dataType: 'string', isArray: true },
}
```

The CSV state machine treats `"` as a field terminator only when followed by comma, CR/LF, or end of input; otherwise it is preserved as a literal NodeId quote. Build a cloned target tree first and replace the live target only after every row and conflict policy succeeds.

- [ ] **Step 4: Run parser tests and frontend typecheck**

Run: `node --test src/IGW.WebUi/src/utils/kepTagImport.test.cjs`

Expected: PASS.

Run: `pnpm --dir src/IGW.WebUi typecheck`

Expected: exit 0.

- [ ] **Step 5: Commit Task 3**

```powershell
git add -- src/IGW.WebUi/src/utils/kepTagImport.ts src/IGW.WebUi/src/utils/kepTagImport.test.cjs
git commit -m "fix: preserve KEP CSV tag hierarchy"
```

### Task 4: OPC UA browse selection dialog

**Files:**
- Create: `src/IGW.WebUi/src/components/OpcUaTagBrowserDialog.vue`
- Create: `src/IGW.WebUi/src/components/OpcUaTagBrowserDialog.ui.test.cjs`
- Modify: `src/IGW.WebUi/src/api.ts`

**Interfaces:**
- Consumes: `POST /api/v1/opcua/browse-preview`.
- Consumes: `buildImportPreview` from Task 3.
- Emits: `apply` with `{ tags: ImportedTag[], overwriteConflicts: boolean }`.

- [ ] **Step 1: Write a failing UI contract test**

```javascript
test('浏览器提供树形多选搜索清空和冲突确认', () => {
  assert.match(source, /el-tree/)
  assert.match(source, /show-checkbox/)
  assert.match(source, /已选.*个/)
  assert.match(source, /清空选择/)
  assert.match(source, /覆盖同路径标记/)
  assert.match(source, /结果已截断/)
})
```

- [ ] **Step 2: Run the UI test and verify RED**

Run: `node --test src/IGW.WebUi/src/components/OpcUaTagBrowserDialog.ui.test.cjs`

Expected: FAIL because the component does not exist.

- [ ] **Step 3: Implement the dialog and API client**

```ts
browseOpcUaPreview: (body: OpcUaBrowsePreviewBody) =>
  http.post('/api/v1/opcua/browse-preview', body, { timeout: 120000 }).then(r => r.data)
```

The dialog builds folder nodes from each result's `path`, uses variable `address` as the stable tree key, supports case-insensitive name/NodeId filtering, and converts checked variable nodes only. Folder selection uses Element Plus cascading checks. Disable “导入所选” when zero variables are checked or the response is truncated.

- [ ] **Step 4: Run UI test and typecheck**

Run: `node --test src/IGW.WebUi/src/components/OpcUaTagBrowserDialog.ui.test.cjs`

Expected: PASS.

Run: `pnpm --dir src/IGW.WebUi typecheck`

Expected: exit 0.

- [ ] **Step 5: Commit Task 4**

```powershell
git add -- src/IGW.WebUi/src/api.ts src/IGW.WebUi/src/components/OpcUaTagBrowserDialog.vue src/IGW.WebUi/src/components/OpcUaTagBrowserDialog.ui.test.cjs
git commit -m "feat: add selective OPC UA tag browser"
```

### Task 5: Integrate channel configuration, CSV preview, and selective merge

**Files:**
- Modify: `src/IGW.WebUi/src/views/Project.vue`
- Create: `src/IGW.WebUi/src/views/Project.opcua-import.test.cjs`

**Interfaces:**
- Consumes: `OpcUaTagBrowserDialog` and Task 3 merge functions.
- Produces: channel-owned OPC UA settings and the user-facing import workflows.

- [ ] **Step 1: Write failing integration contract tests**

```javascript
test('OPC UA端点属于通道且设备不再要求端点', () => {
  assert.match(source, /UA 服务器/)
  assert.match(source, /chForm\.settings\.url/)
  assert.match(source, /devPDriverId === 'OpcUa\.Client'/)
  assert.match(source, /旧设备端点/)
})

test('设备入口打开选择浏览器而不是直接全量导入', () => {
  assert.match(source, /自动生成标记…/)
  assert.match(source, /OpcUaTagBrowserDialog/)
  assert.doesNotMatch(source, /for \(const bt of tags\)[\s\S]*dev\.tags/)
})

test('CSV先预览后原子应用层级结构', () => {
  assert.match(source, /parseKepTagCsv/)
  assert.match(source, /buildImportPreview/)
  assert.match(source, /applyTagImport/)
})
```

- [ ] **Step 2: Run integration contract tests and verify RED**

Run: `node --test src/IGW.WebUi/src/views/Project.opcua-import.test.cjs`

Expected: FAIL because the new channel fields and dialogs are absent.

- [ ] **Step 3: Implement OPC UA channel and device UI rules**

For `OpcUa.Client`, add a channel wizard step and channel property category with URL, security policy, anonymous switch, username, password, and timeout. Seed `url` from the single unique legacy `device.endpoint` only in the edit form; write it to the channel only after user confirms. If legacy device endpoints contain more than one unique value, show a non-destructive warning and do not seed.

Hide the endpoint field in new/edit OPC UA device dialogs. Display a read-only legacy endpoint warning when `device.endpoint` is non-empty and channel URL is empty.

- [ ] **Step 4: Replace direct browse and CSV mutation with preview dialogs**

Open `OpcUaTagBrowserDialog` from the device menu. On apply, call `applyTagImport`, replace `device.groups/device.tags` with the returned clone, then call `markDirty()` and `bump()` once.

For CSV, parse and preview row errors, group count, tag count, and conflicts before applying. The supplied `PLC4.csv` must produce 44 tags and nested groups `LogCheck1`, `LogOff1`, and `LogOff1.AESCM2E_1`.

- [ ] **Step 5: Run all frontend tests, typecheck, and production build**

Run: `$tests = Get-ChildItem src/IGW.WebUi/src -Recurse -Filter *.test.cjs | Select-Object -ExpandProperty FullName; node --test $tests`

Expected: all tests pass.

Run: `pnpm --dir src/IGW.WebUi typecheck`

Expected: exit 0.

Run: `pnpm --dir src/IGW.WebUi build`

Expected: exit 0.

- [ ] **Step 6: Commit Task 5**

```powershell
git add -- src/IGW.WebUi/src/views/Project.vue src/IGW.WebUi/src/views/Project.opcua-import.test.cjs
git commit -m "feat: integrate OPC UA selective tag import"
```

### Task 6: Full regression and supplied-file acceptance

**Files:**
- Modify only if verification exposes a task-owned defect.

**Interfaces:**
- Verifies all interfaces produced by Tasks 1-5.

- [ ] **Step 1: Run the supplied CSV acceptance check**

Run a Node script that imports `kepTagImport.ts`, reads `C:/Users/Administrator/Desktop/PLC4.csv`, and asserts:

```js
assert.equal(result.errors.length, 0)
assert.equal(result.tags.length, 44)
assert.equal(result.tags.filter(x => x.isArray).length, 2)
assert.ok(result.tags.some(x => x.groupPath.join('.') === 'LogOff1.AESCM2E_1'))
```

Expected: all assertions pass.

- [ ] **Step 2: Run the full backend suite**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --nologo`

Expected: zero failed tests.

- [ ] **Step 3: Build the complete Runtime**

Run: `dotnet build src/IGW.Runtime/IGW.Runtime.csproj --nologo`

Expected: zero errors.

- [ ] **Step 4: Run final diff checks**

Run: `git diff --check`

Expected: no whitespace errors.

Run: `git status --short`

Expected: task files are committed; pre-existing unrelated workspace changes remain unstaged and unchanged.

- [ ] **Step 5: Record acceptance evidence**

Append exact backend/frontend test totals, build status, and the `PLC4.csv` 44-tag/2-array result to `docs/superpowers/specs/2026-07-16-opcua-channel-browse-csv-design.md`, then commit only that file:

```powershell
git add -- docs/superpowers/specs/2026-07-16-opcua-channel-browse-csv-design.md
git commit -m "docs: record OPC UA import acceptance"
```
