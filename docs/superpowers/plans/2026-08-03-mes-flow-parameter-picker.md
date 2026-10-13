# MES Flow Parameter Picker Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 MES 流程的条件、上报、分流和回写参数在 PLC 离线或数据面未启动时，仍能从工程配置中按当前 DB 优先、跨 DB、跨 PLC搜索并选择。

**Architecture:** 新增一个只读取 `IGatewayHost.Project` 的有界工程搜索服务和配置态 REST 接口，不经过 `DataGate()`、DataHub 或驱动。前端用共享 `MesParameterPicker.vue` 调用远程搜索接口，只保存当前最多 100 条候选，并通过防抖、取消和请求序号处理百万点工程与乱序响应。

**Tech Stack:** .NET 10 / ASP.NET Core Minimal API / xUnit / Vue 3 / TypeScript / Element Plus `el-select-v2` / Node test runner

## Global Constraints

- 配置搜索不得访问 DataHub、设备驱动或创建采集需求，也不得主动连接 PLC。
- 空查询只搜索当前任务绑定组；全工程搜索至少输入 2 个字符并延迟 250ms。
- 服务端 `limit` 强制限制为 1 到 100，默认 100；返回 `limit + 1` 命中用于判定 `truncated`，不建立全量路径缓存。
- 排序固定为当前组、当前设备其他组、其他设备；同一范围内保持工程定义顺序。
- `access=read` 允许 `Read`/`ReadWrite`，`access=write` 允许 `Write`/`ReadWrite`。
- `kind=tag` 返回点位，`kind=group` 只返回真实工程组；旧值、手工值和已删除点位值都不得被自动清空。
- 不改变现有 MES 工程 Schema、画布布局、保存、自动保存、模板导入导出和运行时执行模型。
- 响应字段、变量名、JSON 字段名、固定值、成员相对名和参数码继续使用普通文本输入。
- 不把百万点完整候选复制到浏览器；浏览器仅保留当前请求的最多 100 项。

---

## File Map

- `src/IGW.Northbound.RestApi/ProjectParameterSearch.cs`：解析查询参数、按范围遍历工程、过滤访问权限并返回有界候选。
- `src/IGW.Northbound.RestApi/IgwRestApi.cs`：暴露不经过数据面门禁的配置搜索端点。
- `tests/IGW.Tests/ProjectParameterSearchTests.cs`：覆盖离线配置搜索、排序、跨 DB/PLC、权限、组模式、模板实例、上限与取消。
- `src/IGW.WebUi/src/api.ts`：声明候选 DTO，并支持 Axios `AbortSignal` 的参数搜索方法。
- `src/IGW.WebUi/src/utils/mesParameterRequest.ts`：封装 250ms 防抖、取消旧请求和请求序号，便于无浏览器单元测试。
- `src/IGW.WebUi/src/utils/mesParameterRequest.test.cjs`：验证短查询、取消、乱序和失败保值行为。
- `src/IGW.WebUi/src/components/MesParameterPicker.vue`：共享远程虚拟下拉、范围标签、截断/错误/缺失值提示和手工输入兼容。
- `src/IGW.WebUi/src/components/MesParameterPicker.test.cjs`：检查组件契约与交互文案。
- `src/IGW.WebUi/src/views/MesFlow.vue`：移除运行时全量点位补全，将指定 PLC 参数字段接到共享选择器。
- `src/IGW.WebUi/src/views/MesFlow.parameter-picker.test.cjs`：防止应接字段漏接或把响应字段误接为 PLC 选择器。

---

### Task 1: 有界工程参数搜索服务

**Files:**
- Create: `src/IGW.Northbound.RestApi/ProjectParameterSearch.cs`
- Create: `tests/IGW.Tests/ProjectParameterSearchTests.cs`
- Modify: `tests/IGW.Tests/IGW.Tests.csproj`

**Interfaces:**
- Consumes: `IgwProject`, `ChannelDef`, `DeviceDef`, `TagGroupDef`, `TagDef`, `TagAccess`, `TemplateExpander.ExpandInProject`。
- Produces: `ProjectParameterSearch.Search(IgwProject project, string? query, string? scopeGroup, string? kind, string? access, int? limit, CancellationToken cancellationToken)`，返回 `ProjectParameterOptionsResponse`。
- Produces: `ProjectParameterOption(string Value, string Label, string FullPath, string Scope, string Kind)` 与 `ProjectParameterOptionsResponse(IReadOnlyList<ProjectParameterOption> Items, bool Truncated)`。

- [ ] **Step 1: 写排序、跨 DB/PLC 和空查询范围的失败测试**

```csharp
private static TagGroupDef Group(string path, string tagName, TagAccess access)
{
    var segments = path.Split('.');
    var root = new TagGroupDef { Name = segments[0] };
    var current = root;
    foreach (string segment in segments.Skip(1)) {
        var child = new TagGroupDef { Name = segment };
        current.SubGroups.Add(child);
        current = child;
    }
    current.Tags.Add(new TagDef { Name = tagName, Access = access });
    return root;
}

private static IgwProject SearchProject() => new() {
    Channels = [
        new ChannelDef { Name = "Line1", Devices = [
            new DeviceDef { Name = "PLC1", Groups = [
                Group("DB1.Active", "Ready", TagAccess.Read),
                Group("DB2", "Ready", TagAccess.Read)
            ]}
        ]},
        new ChannelDef { Name = "Line2", Devices = [
            new DeviceDef { Name = "PLC2", Groups = [Group("DB9", "Ready", TagAccess.Read)] }
        ]}
    ]
};

[Fact]
public void Search_RanksCurrentGroupThenCurrentDeviceThenOtherDevices()
{
    var result = ProjectParameterSearch.Search(
        SearchProject(), "Ready", "Line1.PLC1.DB1.Active", "tag", "read", 100, CancellationToken.None);

    Assert.Equal(new[] { "CurrentGroup", "CurrentDevice", "OtherDevice" }, result.Items.Select(x => x.Scope));
    Assert.Equal(new[] {
        "Line1.PLC1.DB1.Active.Ready",
        "Line1.PLC1.DB2.Ready",
        "Line2.PLC2.DB9.Ready"
    }, result.Items.Select(x => x.Value));
}

[Fact]
public void Search_WithEmptyQuery_ReturnsOnlyCurrentGroup()
{
    var result = ProjectParameterSearch.Search(SearchProject(), "", "Line1.PLC1.DB1", "tag", "read", 100, CancellationToken.None);
    Assert.NotEmpty(result.Items);
    Assert.All(result.Items, x => Assert.StartsWith("Line1.PLC1.DB1.", x.FullPath));
}
```

同时在 `tests/IGW.Tests/IGW.Tests.csproj` 加入直接引用，避免测试依赖传递引用：

```xml
<ProjectReference Include="..\..\src\IGW.Northbound.RestApi\IGW.Northbound.RestApi.csproj" />
```

- [ ] **Step 2: 运行目标测试并确认因类型不存在而失败**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~ProjectParameterSearchTests --no-restore --nologo`

Expected: FAIL，编译器报告 `ProjectParameterSearch` 或响应类型不存在。

- [ ] **Step 3: 实现查询类型、参数校验和按范围顺序遍历**

```csharp
public static class ProjectParameterSearch
{
    public static ProjectParameterOptionsResponse Search(
        IgwProject project, string? query, string? scopeGroup,
        string? kind, string? access, int? limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        string normalizedKind = ParseKind(kind);
        string normalizedAccess = ParseAccess(access);
        int take = Math.Clamp(limit ?? 100, 1, 100);
        string q = (query ?? "").Trim();
        string scope = (scopeGroup ?? "").Trim();

        var collector = new Collector(take, q, normalizedKind, normalizedAccess, cancellationToken);
        collector.VisitCurrentGroup(project, scope);
        if (q.Length >= 2) {
            collector.VisitCurrentDeviceRemainder(project, scope);
            collector.VisitOtherDevices(project, scope);
        }
        return collector.ToResponse();
    }
}
```

遍历器必须在每个通道、设备、组和每 256 个点检查一次 `cancellationToken.ThrowIfCancellationRequested()`；每个阶段收集到 `take + 1` 个匹配后停止后续遍历。点位匹配字符串为名称和完整路径，组匹配字符串为组名和完整路径，比较使用 `OrdinalIgnoreCase`。

- [ ] **Step 4: 运行目标测试并确认排序测试通过**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~ProjectParameterSearchTests --no-restore --nologo`

Expected: PASS。

- [ ] **Step 5: 写权限、组模式、模板实例、上限和取消的失败测试**

```csharp
[Theory]
[InlineData("read", TagAccess.Read, true)]
[InlineData("read", TagAccess.Write, false)]
[InlineData("write", TagAccess.ReadWrite, true)]
[InlineData("write", TagAccess.Read, false)]
public void Search_FiltersTagAccess(string access, TagAccess tagAccess, bool expected)
{
    var project = new IgwProject { Channels = [new ChannelDef { Name = "C", Devices = [
        new DeviceDef { Name = "D", Groups = [Group("DB", "Value", tagAccess)] }
    ]}]};
    var result = ProjectParameterSearch.Search(project, "Value", "C.D.DB", "tag", access, 100, CancellationToken.None);
    Assert.Equal(expected, result.Items.Count == 1);
}

[Fact]
public void Search_GroupMode_ReturnsGroupsButNeverTags()
{
    var result = ProjectParameterSearch.Search(SearchProject(), "DB", "Line1.PLC1.DB1", "group", "read", 100, CancellationToken.None);
    Assert.Contains(result.Items, x => x.FullPath == "Line1.PLC1.DB1" && x.Kind == "group");
    Assert.DoesNotContain(result.Items, x => x.FullPath.EndsWith(".Ready", StringComparison.Ordinal));
}

[Fact]
public void Search_TemplateInstance_UsesEffectiveTagsIncludingDeltaAddsAndMasks()
{
    var template = new DeviceTemplate {
        Name = "T", Groups = [new TagGroupDef { Name = "DB", Tags = [
            new TagDef { Name = "Kept", Access = TagAccess.Read },
            new TagDef { Name = "Masked", Access = TagAccess.Read }
        ]}]
    };
    var project = new IgwProject {
        Templates = [template], Channels = [new ChannelDef { Name = "C", Devices = [
            new DeviceDef { Name = "D", TemplateRef = "T", Delta = new DeviceDelta {
                Masks = ["DB.Masked"], Adds = [new TagAdd {
                    GroupPath = "DB", Tag = new TagDef { Name = "Added", Access = TagAccess.Read }
                }]
            }}
        ]}]
    };
    var result = ProjectParameterSearch.Search(project, "", "C.D.DB", "tag", "read", 100, CancellationToken.None);
    Assert.Contains(result.Items, x => x.Value == "C.D.DB.Kept");
    Assert.Contains(result.Items, x => x.Value == "C.D.DB.Added");
    Assert.DoesNotContain(result.Items, x => x.Value == "C.D.DB.Masked");
}

[Fact]
public void Search_LimitsMillionPointResultAndMarksTruncated()
{
    var group = new TagGroupDef { Name = "DB1" };
    for (int i = 0; i < 1_000_000; i++) group.Tags.Add(new TagDef { Name = $"Tag{i:D7}", Access = TagAccess.Read });
    var project = new IgwProject { Channels = [new ChannelDef { Name = "Line1", Devices = [
        new DeviceDef { Name = "PLC1", Groups = [group] }
    ]}]};
    var result = ProjectParameterSearch.Search(project, "Tag", "Line1.PLC1.DB1", "tag", "read", 100, CancellationToken.None);
    Assert.Equal(100, result.Items.Count);
    Assert.True(result.Truncated);
}

[Fact]
public void Search_ObservesCancellation()
{
    var group = new TagGroupDef { Name = "DB1", Tags = Enumerable.Range(0, 50_000)
        .Select(i => new TagDef { Name = $"Tag{i:D5}", Access = TagAccess.Read }).ToList() };
    var project = new IgwProject { Channels = [new ChannelDef { Name = "Line1", Devices = [
        new DeviceDef { Name = "PLC1", Groups = [group] }
    ]}]};
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    Assert.Throws<OperationCanceledException>(() => ProjectParameterSearch.Search(project, "missing", "Line1.PLC1.DB1", "tag", "read", 100, cts.Token));
}
```

- [ ] **Step 6: 运行新增测试并实现权限、组模式、模板实例与截断逻辑**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~ProjectParameterSearchTests --no-restore --nologo`

Expected before implementation: FAIL；实现后: PASS。模板实例通过 `TemplateExpander.ExpandInProject(project, device)` 获得与运行时一致的有效点；结果只持有最多 101 个路径，测试同时断言响应集合不会随工程规模增长。

- [ ] **Step 7: 提交独立搜索服务**

```powershell
git add src/IGW.Northbound.RestApi/ProjectParameterSearch.cs tests/IGW.Tests/ProjectParameterSearchTests.cs tests/IGW.Tests/IGW.Tests.csproj
git commit -m "feat: add bounded project parameter search"
```

---

### Task 2: 配置态 REST 端点与前端 API 合同

**Files:**
- Modify: `src/IGW.Northbound.RestApi/IgwRestApi.cs`
- Modify: `src/IGW.WebUi/src/api.ts`
- Create: `src/IGW.WebUi/src/api.parameter-options.test.cjs`

**Interfaces:**
- Consumes: Task 1 的 `ProjectParameterSearch.Search(...)`。
- Produces: `GET /api/v1/project/parameter-options?q=&scopeGroup=&kind=tag&access=read&limit=100`。
- Produces: `api.parameterOptions(params, signal)`，返回 `Promise<ProjectParameterOptionsResponse>`。

- [ ] **Step 1: 写接口合同失败测试**

```js
test('parameter options use the project configuration endpoint and support cancellation', () => {
  assert.match(apiSource, /parameterOptions/)
  assert.match(apiSource, /\/api\/v1\/project\/parameter-options/)
  assert.match(apiSource, /signal/)
  assert.match(restSource, /ProjectParameterSearch\.Search\(host\.Project/)
  const start = restSource.indexOf('app.MapGet("/api/v1/project/parameter-options"')
  const end = restSource.indexOf('});', start)
  assert.ok(start >= 0 && end > start)
  assert.doesNotMatch(restSource.slice(start, end), /DataGate/)
})
```

- [ ] **Step 2: 运行接口合同测试并确认失败**

Run: `node --test src/IGW.WebUi/src/api.parameter-options.test.cjs`

Expected: FAIL，找不到 `parameterOptions` 或新端点。

- [ ] **Step 3: 在 `DataGate()` 之外映射端点并处理无效参数**

```csharp
app.MapGet("/api/v1/project/parameter-options", (
    string? q, string? scopeGroup, string? kind, string? access, int? limit, CancellationToken ct) =>
{
    try { return Results.Ok(ProjectParameterSearch.Search(host.Project, q, scopeGroup, kind, access, limit, ct)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
```

端点必须位于项目配置 API 区域，不能写成 `DataGate() ?? ...`；`OperationCanceledException` 交给 ASP.NET Core 请求取消流程处理。

- [ ] **Step 4: 增加 TypeScript DTO 与可取消 API 方法**

```ts
export type ProjectParameterScope = 'CurrentGroup' | 'CurrentDevice' | 'OtherDevice'
export interface ProjectParameterOption {
  value: string; label: string; fullPath: string
  scope: ProjectParameterScope; kind: 'tag' | 'group'
}
export interface ProjectParameterOptionsResponse { items: ProjectParameterOption[]; truncated: boolean }

parameterOptions: (params: {
  q?: string; scopeGroup: string; kind: 'tag' | 'group'; access: 'read' | 'write'; limit?: number
}, signal?: AbortSignal) =>
  http.get<ProjectParameterOptionsResponse>('/api/v1/project/parameter-options', { params, signal }).then((r) => r.data),
```

- [ ] **Step 5: 运行接口合同、类型检查和后端目标测试**

Run: `node --test src/IGW.WebUi/src/api.parameter-options.test.cjs`

Run: `npm --prefix src/IGW.WebUi run typecheck`

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~ProjectParameterSearchTests --no-restore --nologo`

Expected: 全部 PASS。

- [ ] **Step 6: 提交配置端点合同**

```powershell
git add src/IGW.Northbound.RestApi/IgwRestApi.cs src/IGW.WebUi/src/api.ts src/IGW.WebUi/src/api.parameter-options.test.cjs
git commit -m "feat: expose project parameter options API"
```

---

### Task 3: 可取消远程请求协调器

**Files:**
- Create: `src/IGW.WebUi/src/utils/mesParameterRequest.ts`
- Create: `src/IGW.WebUi/src/utils/mesParameterRequest.test.cjs`

**Interfaces:**
- Consumes: `(query: string, signal: AbortSignal) => Promise<ProjectParameterOptionsResponse>`。
- Produces: `createMesParameterRequestCoordinator(fetcher, delayMs = 250)`，返回 `{ search, cancel }`；`search(query, onResult, onError)` 只允许最后一次请求更新 UI。

- [ ] **Step 1: 写防抖、取消和乱序失败测试**

```js
test('only the newest parameter request may publish results', async () => {
  const pending = new Map()
  const coordinator = createMesParameterRequestCoordinator((query, signal) =>
    new Promise((resolve, reject) => pending.set(query, { resolve, reject, signal })), 0)
  const published = []
  coordinator.search('old', (value) => published.push(value), () => {})
  coordinator.search('new', (value) => published.push(value), () => {})
  pending.get('new').resolve({ items: [{ value: 'new' }], truncated: false })
  pending.get('old').resolve({ items: [{ value: 'old' }], truncated: false })
  await new Promise((resolve) => setImmediate(resolve))
  assert.deepEqual(published.flatMap((x) => x.items.map((i) => i.value)), ['new'])
  assert.equal(pending.get('old').signal.aborted, true)
})
```

- [ ] **Step 2: 运行协调器测试并确认失败**

Run: `node --test src/IGW.WebUi/src/utils/mesParameterRequest.test.cjs`

Expected: FAIL，模块或导出不存在。

- [ ] **Step 3: 实现请求序号、250ms 定时器和 AbortController**

```ts
export function createMesParameterRequestCoordinator<T>(
  fetcher: (query: string, signal: AbortSignal) => Promise<T>, delayMs = 250,
) {
  let timer: ReturnType<typeof setTimeout> | undefined
  let controller: AbortController | undefined
  let sequence = 0
  const cancel = () => { sequence++; if (timer) clearTimeout(timer); timer = undefined; controller?.abort() }
  const search = (query: string, onResult: (value: T) => void, onError: (error: unknown) => void, immediate = false) => {
    cancel(); const request = sequence; const current = new AbortController(); controller = current
    timer = setTimeout(async () => {
      try { const value = await fetcher(query, current.signal); if (request === sequence) onResult(value) }
      catch (error) { if (request === sequence && !current.signal.aborted) onError(error) }
    }, immediate ? 0 : delayMs)
  }
  return { search, cancel }
}
```

- [ ] **Step 4: 增加失败不发布空结果、销毁后不发布和 250ms 防抖测试并运行**

Run: `node --test src/IGW.WebUi/src/utils/mesParameterRequest.test.cjs`

Expected: PASS；失败回调只收到错误，不会伪造 `{ items: [] }` 覆盖当前候选。

- [ ] **Step 5: 提交协调器**

```powershell
git add src/IGW.WebUi/src/utils/mesParameterRequest.ts src/IGW.WebUi/src/utils/mesParameterRequest.test.cjs
git commit -m "feat: coordinate remote MES parameter searches"
```

---

### Task 4: 共享虚拟参数选择器

**Files:**
- Create: `src/IGW.WebUi/src/components/MesParameterPicker.vue`
- Create: `src/IGW.WebUi/src/components/MesParameterPicker.test.cjs`

**Interfaces:**
- Consumes: `api.parameterOptions(...)` 与 `createMesParameterRequestCoordinator(...)`。
- Props: `modelValue: string`, `scopeGroup: string`, `kind?: 'tag' | 'group'`, `access?: 'read' | 'write'`, `placeholder?: string`, `size?: 'small' | 'default' | 'large'`。
- Emits: `update:modelValue`, `change`。

- [ ] **Step 1: 写组件合同失败测试**

```js
test('picker is remote virtualized and preserves missing legacy values', () => {
  assert.match(source, /el-select-v2/)
  assert.match(source, /remote/)
  assert.match(source, /filterable/)
  assert.match(source, /allow-create/)
  assert.match(source, /当前工程中不存在/)
  assert.match(source, /继续输入以缩小范围/)
  assert.match(source, /重新加载候选/)
  assert.match(source, /CurrentGroup/)
  assert.match(source, /CurrentDevice/)
  assert.match(source, /OtherDevice/)
})
```

- [ ] **Step 2: 运行组件测试并确认失败**

Run: `node --test src/IGW.WebUi/src/components/MesParameterPicker.test.cjs`

Expected: FAIL，组件文件不存在。

- [ ] **Step 3: 实现组件状态与远程搜索**

组件打开时以空查询立即加载当前组；输入 0 到 1 个字符时只请求当前组，不触发全局搜索；输入至少 2 个字符时使用 250ms 防抖。组件卸载、`scopeGroup`/`kind`/`access` 改变时调用 `cancel()`，清理旧候选并重新加载当前范围。

```ts
type PickerOption = ProjectParameterOption & { missing?: boolean }
const options = ref<PickerOption[]>([])
const loading = ref(false)
const error = ref('')
const truncated = ref(false)
const coordinator = createMesParameterRequestCoordinator((q, signal) =>
  api.parameterOptions({ q, scopeGroup: props.scopeGroup, kind: props.kind, access: props.access, limit: 100 }, signal))

function leafName(value: string) { return value.split('.').filter(Boolean).at(-1) || value }
function withCurrentValue(items: ProjectParameterOption[]): PickerOption[] {
  if (!props.modelValue || items.some((item) => item.value === props.modelValue)) return items
  return [{
    value: props.modelValue, label: leafName(props.modelValue), fullPath: props.modelValue,
    scope: 'CurrentGroup' as const, kind: props.kind, missing: true,
  }, ...items]
}
function apiErrorMessage(reason: any) {
  return reason?.response?.data?.error || reason?.message || '加载工程点位失败'
}

function remoteSearch(query: string, immediate = false) {
  loading.value = true; error.value = ''
  coordinator.search(query, (result) => {
    options.value = withCurrentValue(result.items)
    truncated.value = result.truncated
    loading.value = false
  }, (reason) => {
    error.value = apiErrorMessage(reason)
    loading.value = false
  }, immediate)
}
```

`withCurrentValue` 在 `modelValue` 非空且不在结果中时加入 `{ value: modelValue, label: leafName(modelValue), fullPath: modelValue, missing: true }` 展示项，但不改变 `modelValue`。下拉项用叶子名作主行、完整路径作副行，并把范围显示为“当前 DB”“当前 PLC”“其他 PLC”。

- [ ] **Step 4: 实现错误、空结果、截断和样式状态**

错误区域提供“重新加载候选”按钮；空结果显示“未找到工程点位”；`truncated` 显示“结果较多，继续输入以缩小范围”。所有状态只影响下拉，不阻塞手工输入或清空操作。

- [ ] **Step 5: 运行组件测试与类型检查**

Run: `node --test src/IGW.WebUi/src/components/MesParameterPicker.test.cjs src/IGW.WebUi/src/utils/mesParameterRequest.test.cjs`

Run: `npm --prefix src/IGW.WebUi run typecheck`

Expected: 全部 PASS。

- [ ] **Step 6: 提交共享选择器**

```powershell
git add src/IGW.WebUi/src/components/MesParameterPicker.vue src/IGW.WebUi/src/components/MesParameterPicker.test.cjs
git commit -m "feat: add MES parameter picker"
```

---

### Task 5: 将 MES 流程中的 PLC 参数字段接入共享选择器

**Files:**
- Modify: `src/IGW.WebUi/src/views/MesFlow.vue`
- Create: `src/IGW.WebUi/src/views/MesFlow.parameter-picker.test.cjs`

**Interfaces:**
- Consumes: Task 4 的 `<MesParameterPicker v-model scope-group kind access>`。
- Produces: 原字段 `tagId`、`baseGroup`、`countTagId`、`switchTag`、`copyFromTagId` 的值保持不变，仅替换输入交互。

- [ ] **Step 1: 写字段覆盖失败测试**

```js
test('all PLC parameter fields use the shared picker with correct access', () => {
  assert.match(source, /import MesParameterPicker/)
  assert.equal((source.match(/<MesParameterPicker/g) || []).length, 9)
  assert.match(source, /v-model="st\.baseGroup"[^>]*kind="group"/)
  assert.match(source, /v-model="w\.tagId"[^>]*access="write"/)
  assert.match(source, /v-model="w\.copyFromTagId"[^>]*access="read"/)
  assert.doesNotMatch(source, /tagSuggest/)
  assert.doesNotMatch(source, /api\.tags\(\)/)
})

test('response fields and structure member names remain plain inputs', () => {
  assert.match(source, /<el-input v-model="c\.responseField"/)
  assert.match(source, /<el-input v-model="c\.field"/)
  assert.match(source, /<el-input v-model="m\.tagId"/)
  assert.match(source, /<el-input v-model="f\.fieldName"/)
  assert.match(source, /<el-input v-model="f\.parameterCode"/)
})
```

- [ ] **Step 2: 运行覆盖测试并确认失败**

Run: `node --test src/IGW.WebUi/src/views/MesFlow.parameter-picker.test.cjs`

Expected: FAIL，仍存在 `tagSuggest` 和 `el-autocomplete`。

- [ ] **Step 3: 导入组件并移除运行时全量点位加载**

删除 `allTags`、`api.tags()` 的 `onMounted` 调用和 `tagSuggest`；新增：

```ts
import MesParameterPicker from '../components/MesParameterPicker.vue'
```

- [ ] **Step 4: 替换九类 PLC 参数控件**

所有选择器传入 `:scope-group="task?.groupRef || ''"`。触发条件、条件分支、上报字段、数量点、分流判值点和复制来源使用 `kind="tag" access="read"`；结构基准组使用 `kind="group" access="read"`；两个回写目标使用 `kind="tag" access="write"`。所有控件的 `@change="md"` 保留脏标记语义。

```vue
<MesParameterPicker v-model="c.tagId" :scope-group="task?.groupRef || ''"
  kind="tag" access="read" placeholder="点位完整路径" size="small" @change="md" />
```

- [ ] **Step 5: 运行流程覆盖测试、已有流程测试和类型检查**

Run: `node --test src/IGW.WebUi/src/views/MesFlow.parameter-picker.test.cjs src/IGW.WebUi/src/views/MesFlow.data-ranges.test.cjs src/IGW.WebUi/src/views/MesFlow.runtime-state.test.cjs`

Run: `npm --prefix src/IGW.WebUi run typecheck`

Expected: 全部 PASS。

- [ ] **Step 6: 提交流程接入**

```powershell
git add src/IGW.WebUi/src/views/MesFlow.vue src/IGW.WebUi/src/views/MesFlow.parameter-picker.test.cjs
git commit -m "feat: connect MES fields to project parameters"
```

---

### Task 6: 全量回归与真实浏览器验收

**Files:**
- Modify only if verification exposes a defect: files owned by Tasks 1-5

**Interfaces:**
- Consumes: 完整后端端点、共享组件和流程字段接入。
- Produces: 可复现的构建、测试与浏览器验收证据。

- [ ] **Step 1: 运行全部 .NET 测试**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --nologo`

Expected: 0 failed，包含 `ProjectParameterSearchTests`。

- [ ] **Step 2: 运行全部 Web 单元测试、类型检查和生产构建**

Run: `Get-ChildItem src/IGW.WebUi/src -Recurse -Filter '*.test.cjs' | ForEach-Object { node --test $_.FullName; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE } }`

Run: `npm --prefix src/IGW.WebUi run typecheck`

Run: `npm --prefix src/IGW.WebUi run build`

Expected: 全部命令退出码 0。

- [ ] **Step 3: 用未连接 PLC 的工程启动运行时并打开真实浏览器**

启动现有开发/运行配置后，在浏览器打开工程配置中的真实 DB 流程画布。确认下拉打开时立即显示当前 DB 候选，且界面没有依赖 PLC 在线提示。

- [ ] **Step 4: 验证跨范围、写权限和旧值兼容**

依次搜索当前 PLC 其他 DB 的点、其他 PLC 的点；确认范围标签正确。检查回写目标不出现只读点。手工输入不存在路径，保存并刷新，确认原值保留且显示“当前工程中不存在”。

- [ ] **Step 5: 验证百万点有界行为与请求取消**

使用合成百万点工程或等价压力夹具，打开下拉和连续快速输入查询；在浏览器 Network 面板确认每个响应最多 100 项、旧请求被取消，页面不下载或渲染全量点位。

- [ ] **Step 6: 检查工作区差异并只提交本功能文件**

Run: `git diff --check`

Run: `git status --short`

Expected: `git diff --check` 无输出；不得暂存或覆盖用户原有的无关修改。若 Tasks 1-5 的文件在开始前已有混合修改，保留工作区改动并在交付说明中列出，不执行宽泛的 `git add .`。
