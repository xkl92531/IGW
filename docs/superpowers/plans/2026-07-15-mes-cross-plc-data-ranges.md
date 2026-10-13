# MES 跨 PLC / 跨 DB 数据范围 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在不改变现有流程画布结构的前提下，让一个上报节点能用“选择整个 DB/结构体”的方式读取两台 PLC 的多个数据范围，并在来源离线时继续上报、将对应范围写为 `null`。

**Architecture:** 工程保存时由 `IGW.Core` 把工程树（含模板实例展开结果）编译成不可变的 MES 数据目录并完成范围校验；`IGW.Northbound.Mes` 在引擎构造/热重载时把每个上报节点预编译成稳定的输出树，运行周期只读取缓存与实时设备状态，再用 `Utf8JsonWriter` 流式生成 JSON。Web 端只保存范围引用、输出路径和排除项，数据源树按组懒展开，字段排除列表使用固定行高虚拟滚动；旧 `Fields`/`Structs` 配置保持旧版模式，不自动迁移。

**Tech Stack:** .NET 10、C#、xUnit、System.Text.Json、Vue 3、TypeScript、Element Plus、Node `node:test`、Vite。

## Global Constraints

- 工作区已有大量用户改动；每个任务只暂存本任务列出的文件，不得使用 `git add .`，不得回滚或覆盖无关修改。
- 严格测试先行：先添加失败测试并确认失败原因，再写最小实现，再运行聚焦测试。
- 旧工程没有 `dataRanges` 字段时必须按现有报文逻辑运行；自动保存、打开和再次保存都不得静默转换旧配置。
- 新数据范围与旧 `fields`/`structs` 在同一个 `MesPayload` 内互斥；自定义 `bodyTemplate` 与数据范围模式也互斥，避免界面显示已选范围但运行时被模板忽略。
- `groupRef` 仅用于工程树导航、默认定位和旧任务兼容，不限制数据范围来源。
- 设备离线是数据质量状态，不是 HTTP 传输失败：离线来源整块输出 `null`，其他来源继续生成并发送；单点质量异常只输出该字段 `null`。
- 运行周期不得调用 `IDataHub.ListTagIds()` 做范围发现；点位结构、数组分组、排除集合和输出路径都在热重载时预编译。
- 默认输出路径为 `设备名.DB.结构体`（去掉通道名）；用户可改为自定义点分路径。自动路径随来源重命名，自定义路径不跟随。
- 数组识别规则保持稳定：同一父组下名字为 `Name[整数]` 的子组折叠为 JSON 数组 `Name`，按数值下标排序；其他子组输出对象。不同范围独立解析，不要求同构。
- 不追求两台 PLC 原子同步快照；每次构建报文时每个点从当前缓存只取一次，形成该轮只读值集合。

---

### Task 1: 增加紧凑的数据范围配置模型并锁定旧工程兼容

**Files:**
- Modify: `src/IGW.Abstractions/MesModel.cs`
- Create: `tests/IGW.Tests/MesDataRangeModelTests.cs`

- [ ] **Step 1: 写旧工程兼容和新模型序列化失败测试**

在 `MesDataRangeModelTests.cs` 覆盖：

```csharp
[Fact]
public void 旧工程没有数据范围字段_反序列化后仍是空列表()
{
    const string json = """{"fields":[{"tagId":"Line.PLC.DB.Value","fieldName":"value"}],"structs":[],"append":{}}""";
    var payload = JsonSerializer.Deserialize<MesPayload>(json, ProjectStore.Options)!;
    Assert.Empty(payload.DataRanges);
    Assert.Single(payload.Fields);
}

[Fact]
public void 数据范围只保存来源输出和排除项_不展开全部点位()
{
    var payload = new MesPayload { DataRanges = { new MesDataRange {
        SourcePath = "Line.PLC_A.DB1604.Tray",
        Scope = "Struct",
        OutputPath = "PLC_A.AutoStacking.Tray",
        OutputPathMode = "Auto",
        Excludes = { "Debug", "Items[3].Reserved" }
    } } };
    string json = JsonSerializer.Serialize(payload, ProjectStore.Options);
    Assert.Contains("dataRanges", json);
    Assert.DoesNotContain("Line.PLC_A.DB1604.Tray.Debug\"", json); // 排除项必须保持相对路径
}
```

- [ ] **Step 2: 运行测试确认因模型缺失而失败**

Run:

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~MesDataRangeModelTests
```

Expected: 编译失败，提示 `MesPayload.DataRanges` / `MesDataRange` 不存在。

- [ ] **Step 3: 添加模型，保持字符串枚举兼容**

在 `MesPayload` 添加：

```csharp
public List<MesDataRange> DataRanges { get; set; } = new();
```

紧接 `MesPayload` 定义：

```csharp
public sealed class MesDataRange
{
    public string SourcePath { get; set; } = "";       // 通道.设备.DB[.结构体]
    public string Scope { get; set; } = "Group";       // Group / Struct
    public string OutputPath { get; set; } = "";       // 点分 JSON 路径；空且 Auto 时运行时推导
    public string OutputPathMode { get; set; } = "Auto"; // Auto / Custom
    public string IncludeMode { get; set; } = "All";   // 首版固定 All
    public List<string> Excludes { get; set; } = new(); // 相对 SourcePath 的点路径
    public string OfflineMode { get; set; } = "Null";  // 首版固定 Null
}
```

不修改 `Fields`、`Structs`、`Append` 的默认值和序列化名称。

- [ ] **Step 4: 运行聚焦测试**

Run:

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~MesDataRangeModelTests
```

Expected: PASS。

- [ ] **Step 5: 提交模型和测试**

```powershell
git add src/IGW.Abstractions/MesModel.cs tests/IGW.Tests/MesDataRangeModelTests.cs
git commit -m "feat: add MES data range model"
```

---

### Task 2: 建立工程数据目录并在保存前阻断非法范围

**Files:**
- Create: `src/IGW.Core/MesDataRangeCatalog.cs`
- Modify: `src/IGW.Core/ProjectStore.cs`
- Create: `tests/IGW.Tests/MesDataRangeValidationTests.cs`

- [ ] **Step 1: 写目录解析和保存校验失败测试**

测试工程至少含 `Line.PLC_A.DB1604.Tray`、`Line.PLC_A.DBRecipe`、`Line.PLC_B.DBQuality`，并覆盖模板实例通过 `TemplateExpander.ExpandInProject` 后也能被目录找到。添加这些断言：

```csharp
Assert.True(MesDataRangeCatalog.Build(project).TryGet("Line.PLC_A.DB1604", out var source));
Assert.Equal("Line.PLC_A", source.DevicePath);
Assert.Equal("PLC_A.DB1604", source.DefaultOutputPath);
Assert.Equal(3000, source.TagCount);
```

以及 `ProjectStore.Check(project)` 的失败场景：

- 来源路径不存在，错误含完整 `SourcePath`。
- 已选整个 DB 后又选其子结构体，错误含“已被上级范围包含”。
- 两个 `OutputPath` 相同或互为祖先/后代，错误列出两个路径。
- `Append` 固定字段名与任一范围输出路径的顶层段冲突，错误列出冲突字段；非冲突 `Append` 允许与范围一起使用。
- 同一 payload 同时存在 `DataRanges` 与 `Fields`/`Structs`。
- Flow 上报节点同时存在 `DataRanges` 与非空 `Http.BodyTemplate`。
- `Scope` 不是 `Group/Struct`、`IncludeMode` 不是 `All`、`OfflineMode` 不是 `Null`。
- 排除项不在来源范围内或指向组而不是叶子点。

- [ ] **Step 2: 运行测试确认失败**

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~MesDataRangeValidationTests
```

Expected: 编译失败，提示 `MesDataRangeCatalog` 不存在，或断言失败。

- [ ] **Step 3: 实现不可变目录**

`MesDataRangeCatalog.cs` 提供以下只读形状：

```csharp
public sealed record MesCatalogTag(string Name, string RelativePath, string TagId, IgwDataType DataType, int Order);
public sealed record MesCatalogGroup(
    string Name, string SourcePath, string RelativePath,
    string DevicePath, string DefaultOutputPath,
    IReadOnlyList<MesCatalogTag> Tags,
    IReadOnlyList<MesCatalogGroup> Children,
    int TagCount);

public sealed class MesDataRangeCatalog
{
    public static MesDataRangeCatalog Build(IgwProject project);
    public bool TryGet(string sourcePath, out MesCatalogGroup source);
    public IReadOnlyList<MesCatalogGroup> Roots { get; }
}
```

构建时：

1. 遍历 `project.Channels` / `Devices`。
2. 对模板实例调用 `TemplateExpander.ExpandInProject(project, device)`。
3. 以工程树顺序赋 `Order`，完整 TagId 为 `通道.设备.组…点`。
4. 每个组计算后代 `TagCount`；`DefaultOutputPath` 为完整来源去掉通道段。
5. 字典使用 `StringComparer.OrdinalIgnoreCase` 查找，但保存和输出沿用工程中的原始大小写。

- [ ] **Step 4: 把范围校验接入 `ProjectStore.Check`**

新增私有 `CheckMesDataRanges(IgwProject, MesDataRangeCatalog, List<string>)`，同时检查 `MesTask.Payload` 与每个 Flow `Upload` 节点的 `Payload`。路径重叠使用“段”比较，不允许用普通字符串前缀误判 `DB1` 与 `DB10`。输出路径以 `.` 拆段并阻断相同或祖先冲突。

`ProjectStore.Check` 在通道/模板基础校验后构建一次目录并调用该方法；如果通道/模板已有致命错误，目录构建应容错跳过坏设备，不得让 `Check` 自身抛出。

- [ ] **Step 5: 运行目录与校验测试，再跑现有工程测试**

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~MesDataRangeValidationTests|FullyQualifiedName~InitialProjectTests|FullyQualifiedName~MesFlowTemplateTests"
```

Expected: PASS；旧工程和流程模板测试保持通过。

- [ ] **Step 6: 提交目录与校验**

```powershell
git add src/IGW.Core/MesDataRangeCatalog.cs src/IGW.Core/ProjectStore.cs tests/IGW.Tests/MesDataRangeValidationTests.cs
git commit -m "feat: validate MES data ranges"
```

---

### Task 3: 预编译范围并用流式 JSON 生成跨 PLC 报文

**Files:**
- Modify: `src/IGW.Northbound.Mes/IGW.Northbound.Mes.csproj`
- Create: `src/IGW.Northbound.Mes/MesRangePayloadCompiler.cs`
- Create: `src/IGW.Northbound.Mes/MesRangeJsonWriter.cs`
- Modify: `src/IGW.Northbound.Mes/MesUploadEngine.cs`
- Create: `tests/IGW.Tests/MesRangePayloadTests.cs`

- [ ] **Step 1: 写跨 PLC、不同结构和质量异常的失败测试**

为测试创建 `IgwProject`、`DataHub` 和目录，注册并更新这些点：

```text
Line.PLC_A.DB1604.Tray[0].Code
Line.PLC_A.DB1604.Tray[0].Ok
Line.PLC_A.DBRecipe.Name
Line.PLC_B.DBQuality.Result.Value
```

测试通过新增的 internal 测试入口构建 JSON，断言：

```json
{
  "PLC_A": {
    "DB1604": { "Tray": [ { "Code": "A01", "Ok": true } ] },
    "DBRecipe": { "Name": "R1" }
  },
  "PLC_B": { "DBQuality": { "Result": { "Value": 12 } } }
}
```

另测：

- 两个不同结构体成员完全不同，输出互不串字段。
- `Excludes = ["Tray[0].Ok"]` 后字段消失。
- `Append = { ["WorkStationCode"] = "S01" }` 与范围数据一起写到根对象，值保持现有字符串语义。
- 设备在线但 `DataValue.IsGood == false` 时单字段为 `null`。
- 同一个点在一次 `Build` 中只调用一次 `TryGet`。

- [ ] **Step 2: 运行测试确认失败**

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~MesRangePayloadTests
```

Expected: 编译失败，缺少编译器、写入器和测试入口。

- [ ] **Step 3: 增加对 Core 数据目录的项目引用**

在 `IGW.Northbound.Mes.csproj` 添加：

```xml
<ProjectReference Include="..\IGW.Core\IGW.Core.csproj" />
```

依赖方向为 `Northbound.Mes -> Core -> Abstractions`，不存在反向引用。

- [ ] **Step 4: 实现预编译输出树**

`MesRangePayloadCompiler` 输入 `MesPayload + MesDataRangeCatalog`，输出不可变 `CompiledMesPayload`：

```csharp
internal sealed record CompiledMesPayload(IReadOnlyList<CompiledRange> Ranges, int TagCount);
internal sealed record CompiledRange(
    string SourcePath, string DevicePath,
    IReadOnlyList<string> OutputSegments,
    CompiledJsonNode Root, int TagCount);
```

编译阶段完成：

- 排除项转为 `HashSet<string>(OrdinalIgnoreCase)` 并从叶子集合剔除。
- `Name[整数]` 同名基组折叠成数组节点并按整数排序。
- 其他组、字段按工程树 `Order` 保持稳定。
- 多范围公共输出前缀合并为一棵根输出树。
- 引擎构造时为任务 payload 和 Flow 中每个 Upload payload 建立 `ReferenceEqualityComparer` 映射；运行周期不再解析路径。

- [ ] **Step 5: 实现一次取值和流式写入**

`MesRangeJsonWriter` 使用 `ArrayBufferWriter<byte>` + `Utf8JsonWriter`。对每个在线范围先把叶子值各读取一次写入本轮缓冲，再输出 JSON；不得调用 `ListTagIds()`。类型规则：

```csharp
if (!found || !value.IsGood || value.Value is null) writer.WriteNullValue();
else JsonSerializer.Serialize(writer, value.Value, value.Value.GetType());
```

所有范围输出完成后再按现有顺序写 `payload.Append` 的非冲突根字段，固定值继续输出 JSON 字符串；顶层冲突已由 Task 2 的保存校验阻断。

返回结构化构建结果：

```csharp
internal sealed record MesPayloadBuildResult(
    string Json,
    IReadOnlyList<string> OfflineSources,
    IReadOnlyList<string> BadTags,
    int TagCount)
{
    public bool Partial => OfflineSources.Count > 0 || BadTags.Count > 0;
}
```

`MesUploadEngine.BuildBody` 保留旧分支：

1. 非空 `bodyTemplate`：现有模板逻辑。
2. `DataRanges.Count > 0`：只走预编译范围写入器。
3. 否则：现有 `Fields/Structs/Append` 逻辑完全不改。

为 xUnit 暴露 `internal MesPayloadBuildResult BuildRangeBodyForTest(MesPayload payload)`，不扩大公共 API。

- [ ] **Step 6: 运行聚焦测试和旧 MES 测试**

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~MesRangePayloadTests|FullyQualifiedName~MesResponseTimeoutTests|FullyQualifiedName~MesWholeCycleStateTests"
```

Expected: PASS；旧超时、流程状态和报文路径无回归。

- [ ] **Step 7: 提交编译器和流式写入器**

```powershell
git add src/IGW.Northbound.Mes/IGW.Northbound.Mes.csproj src/IGW.Northbound.Mes/MesRangePayloadCompiler.cs src/IGW.Northbound.Mes/MesRangeJsonWriter.cs src/IGW.Northbound.Mes/MesUploadEngine.cs tests/IGW.Tests/MesRangePayloadTests.cs
git commit -m "feat: stream MES range payloads"
```

---

### Task 4: 接入实时设备状态并显示“部分数据”而不是传输失败

**Files:**
- Modify: `src/IGW.Abstractions/MesStatus.cs`
- Modify: `src/IGW.Core/Engine.cs`
- Modify: `src/IGW.Runtime/GatewayHost.cs`
- Modify: `src/IGW.Northbound.Mes/MesUploadEngine.cs`
- Modify: `src/IGW.WebUi/src/components/MesNodeDetail.vue`
- Modify: `tests/IGW.Tests/MesRangePayloadTests.cs`
- Modify: `tests/IGW.Tests/ObservabilityTests.cs`

- [ ] **Step 1: 写设备离线和部分数据状态失败测试**

覆盖：

- PLC-A 在线、PLC-B 离线：PLC-A 正常有值，`PLC_B.DBQuality` 整棵为 `null`。
- PLC-B 缓存仍存有断线前好值：离线后仍必须为 `null`。
- 两台 PLC 都离线：仍生成结构稳定、两个范围均为 `null` 的报文。
- HTTP 返回 2xx 时节点状态仍为 `ok`，但 `Info` 含“部分数据”，`Detail.Kind == "data-quality"` 并列出离线来源。
- HTTP 超时或非 2xx 时仍为传输失败，不能被“部分数据”覆盖。

- [ ] **Step 2: 运行测试确认失败**

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~MesRangePayloadTests|FullyQualifiedName~MesWholeCycleStateTests|FullyQualifiedName~DetailSnapshotTests"
```

Expected: 新增断言失败。

- [ ] **Step 3: 提供无分配列表的设备状态查询**

在 `RuntimeEngine` 添加：

```csharp
public ConnectionState? DeviceState(string fullName)
{
    var runtime = _devices.FirstOrDefault(d => string.Equals(d.Name, fullName, StringComparison.OrdinalIgnoreCase));
    return runtime?.Driver.State;
}
```

该入口不构造 `DeviceStatuses()` 列表。MES 引擎构造参数增加可选 `Func<string, ConnectionState?> deviceState`，旧测试/调用方不传时仅用于旧 payload；如果使用 DataRanges 却未提供状态，构造时明确抛出配置错误，不得把未知状态当在线。

- [ ] **Step 4: 在 GatewayHost 热重载边界注入目录和状态**

`StartNorthboundAsync` 中：

```csharp
var mesCatalog = MesDataRangeCatalog.Build(Project);
_mes = new MesUploadEngine(
    Hub, Project.MesUpload, writeBack, logger, _dataDir,
    mesCatalog, Engine.DeviceState);
```

`MesTestAsync` 的临时引擎使用当前 `Project` 构建同一目录并注入 `Engine.DeviceState`，保证“测试上报”和正式运行一致。

- [ ] **Step 5: 增加数据质量详情并接入节点状态**

在 `MesStatus.cs` 添加：

```csharp
public sealed record MesDataQualityDetail(
    IReadOnlyList<string> OfflineSources,
    IReadOnlyList<string> BadTags)
{
    public string Kind => "data-quality";
}
```

Upload 节点生成报文后保留 `MesPayloadBuildResult`：HTTP 成功时若 `Partial`，调用：

```csharp
Mark(st, node.Id, "ok",
    $"部分数据：离线来源 {build.OfflineSources.Count}，异常点 {build.BadTags.Count}，已按 null 上报",
    new MesDataQualityDetail(build.OfflineSources, build.BadTags));
```

`SnapNode` 对 `MesDataQualityDetail` 即使状态为 `ok` 也保留，其他正常详情仍清空。线性步骤把同样摘要写入 MES 日志，但不把它计为失败。

`MesNodeDetail.vue` 为 `detail.kind === 'data-quality'` 增加绿色/橙色“上报成功，数据不完整”展示，最多直接列 3 个来源/点，其余显示数量；不要套用红色故障样式。

- [ ] **Step 6: 运行测试**

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~MesRangePayloadTests|FullyQualifiedName~MesWholeCycleStateTests|FullyQualifiedName~DetailSnapshotTests"
```

Expected: PASS。

- [ ] **Step 7: 提交运行时状态接入**

```powershell
git add src/IGW.Abstractions/MesStatus.cs src/IGW.Core/Engine.cs src/IGW.Runtime/GatewayHost.cs src/IGW.Northbound.Mes/MesUploadEngine.cs src/IGW.WebUi/src/components/MesNodeDetail.vue tests/IGW.Tests/MesRangePayloadTests.cs tests/IGW.Tests/ObservabilityTests.cs
git commit -m "feat: report partial MES data quality"
```

---

### Task 5: 建立前端数据范围索引、摘要和重命名规则

**Files:**
- Create: `src/IGW.WebUi/src/utils/mesDataRanges.ts`
- Create: `src/IGW.WebUi/src/utils/mesDataRanges.test.cjs`
- Modify: `src/IGW.WebUi/src/views/Project.vue`
- Modify: `src/IGW.WebUi/src/utils/projectTopology.ts`

- [ ] **Step 1: 写纯函数失败测试**

`mesDataRanges.test.cjs` 用项目夹具覆盖：

- 跨两个设备搜索 DB/结构体，结果显示完整来源路径和后代点数。
- 模板实例先应用模板点、Mask、Override、Add，再生成可选范围。
- 默认输出路径去掉通道名。
- 选父范围后子范围返回 `{ disabled: true, reason: '已被上级范围包含' }`。
- 选择结果摘要为 `2 PLC · 3 个数据范围 · 约 3050 点`。
- 自动输出路径随来源重命名；`outputPathMode: 'Custom'` 保持用户别名不变。
- 排除项只接受来源下的叶子相对路径。

- [ ] **Step 2: 运行测试确认失败**

```powershell
node --test src/IGW.WebUi/src/utils/mesDataRanges.test.cjs
```

Expected: FAIL，模块不存在。

- [ ] **Step 3: 实现可复用索引**

导出这些稳定接口：

```ts
export interface MesRangeOption {
  sourcePath: string
  devicePath: string
  label: string
  scope: 'Group' | 'Struct'
  tagCount: number
  defaultOutputPath: string
  children: MesRangeOption[]
  leafTags: Array<{ relativePath: string; tagId: string }>
}

export function buildMesRangeIndex(project: any): MesRangeIndex
export function searchMesRangeOptions(index: MesRangeIndex, query: string): MesRangeOption[]
export function validateRangeSelection(ranges: any[], option: MesRangeOption): string | null
export function rangeSummary(ranges: any[], index: MesRangeIndex): string
export function migrateMesRangeRefs(root: any, oldPath: string, newPath: string): void
```

索引建立一次，搜索使用预先小写化的组路径/标签；`children` 只在树节点展开时交给 UI，页面初始不扁平化渲染所有点。`leafTags` 仅供打开“排除少数字段”时读取。

- [ ] **Step 4: 接入工程树重命名迁移**

`Project.vue` 的通用 `REF_KEYS` 增加 `sourcePath`，但 `outputPath` 不能加入通用键集合。完成通用引用迁移后调用 `migrateMesRangeRefs`：

- `sourcePath` 按段重写。
- `outputPathMode === 'Auto'` 时由新 `sourcePath` 重算。
- `Custom` 输出路径保持不动。

`projectTopology.ts` 统计工程形状时继续忽略 MES 范围数量，避免把范围配置误判成工程点位删除。

- [ ] **Step 5: 运行前端纯函数测试**

```powershell
node --test src/IGW.WebUi/src/utils/mesDataRanges.test.cjs src/IGW.WebUi/src/utils/projectTopology.test.cjs
```

Expected: PASS。

- [ ] **Step 6: 提交前端索引和迁移**

```powershell
git add src/IGW.WebUi/src/utils/mesDataRanges.ts src/IGW.WebUi/src/utils/mesDataRanges.test.cjs src/IGW.WebUi/src/views/Project.vue src/IGW.WebUi/src/utils/projectTopology.ts
git commit -m "feat: index MES data range sources"
```

---

### Task 6: 实现简化的数据范围选择窗口和虚拟排除列表

**Files:**
- Create: `src/IGW.WebUi/src/components/MesDataRangeDialog.vue`
- Create: `src/IGW.WebUi/src/components/MesDataRangeDialog.ui.test.cjs`
- Modify: `src/IGW.WebUi/src/views/MesFlow.vue`

- [ ] **Step 1: 写交互结构失败测试**

用 `node:test` 读取 Vue 源码，锁定这些不易被回归的交互：

- 默认区域标题“已选择的数据范围”，每行包含来源、范围、规模、输出、状态。
- 存在“添加数据范围”“排除少数字段”“移除”入口。
- 搜索输入和懒展开树存在，但树节点不渲染点位叶子。
- 排除编辑器使用 `visibleExclusions` 切片和上下占位高度，不对全部叶子直接 `v-for`。
- 离线策略展示固定“来源离线时输出 null”，没有可改成“最后值”的选项。
- 确认前调用重叠/输出路径冲突校验，错误时不关闭窗口。

- [ ] **Step 2: 运行测试确认失败**

```powershell
node --test src/IGW.WebUi/src/components/MesDataRangeDialog.ui.test.cjs
```

Expected: FAIL，组件不存在。

- [ ] **Step 3: 实现范围窗口**

组件契约：

```ts
const props = defineProps<{
  modelValue: boolean
  project: any
  ranges: any[]
}>()
const emit = defineEmits<{
  'update:modelValue': [boolean]
  apply: [ranges: any[]]
}>()
```

窗口行为：

1. 打开时深拷贝 `ranges` 为草稿，取消不污染画布节点。
2. 主视图先列已选范围；“添加数据范围”切到可搜索树。
3. 树只显示通道、设备、DB/结构体组；点击组整块添加，不显示数千个点位复选框。
4. 父范围已选时禁用子范围并在行内显示原因。
5. 输出路径默认自动模式；用户编辑后切为 `Custom`，提供“恢复自动路径”。
6. 排除编辑使用固定 `rowHeight = 36`、`viewportHeight = 360`、`overscan = 8`，根据 `scrollTop` 计算 `start/end`，DOM 行数保持在约 30 行以内。
7. “应用”前校验来源重叠、输出路径冲突和排除项，成功才 emit。

- [ ] **Step 4: 在 MesFlow 只接入对话框状态，不改右侧布局**

添加 `rangeDialogOpen`、`editingRangeNode`，并提供：

```ts
function openRangeDialog(node: any) {
  editingRangeNode.value = node
  rangeDialogOpen.value = true
}
function applyRanges(ranges: any[]) {
  editingRangeNode.value.payload.dataRanges = ranges
  markDirty()
}
```

此任务暂不删除旧字段 UI；只确保组件可打开、应用后节点 payload 更新且 30 秒静默自动保存能捕获 `markDirty()`。

- [ ] **Step 5: 运行测试、类型检查和构建**

```powershell
node --test src/IGW.WebUi/src/components/MesDataRangeDialog.ui.test.cjs src/IGW.WebUi/src/utils/mesDataRanges.test.cjs
pnpm --dir src/IGW.WebUi typecheck
pnpm --dir src/IGW.WebUi build
```

Expected: 全部 PASS，Vite 构建成功。

- [ ] **Step 6: 提交范围窗口**

```powershell
git add src/IGW.WebUi/src/components/MesDataRangeDialog.vue src/IGW.WebUi/src/components/MesDataRangeDialog.ui.test.cjs src/IGW.WebUi/src/views/MesFlow.vue
git commit -m "feat: add MES data range picker"
```

---

### Task 7: 把新模式接入画布摘要并整理上报节点右侧详情

**Files:**
- Modify: `src/IGW.WebUi/src/views/MesFlow.vue`
- Create: `src/IGW.WebUi/src/views/MesFlow.data-ranges.test.cjs`
- Modify: `src/IGW.WebUi/src/components/MesNodeDetail.vue`

- [ ] **Step 1: 写画布和右侧详情失败测试**

源码结构测试覆盖：

- 新建 Upload 节点 payload 为 `{ dataRanges: [], fields: [], structs: [], append: {} }`，不再自动把 `groupRef` 下所有点展开成字段行。
- 画布 Upload 卡片显示 `rangeSummary`，例如 `2 PLC · 3 个数据范围 · 约 3050 点`。
- 右侧始终显示标题、上报地址、等待超时、“上报数据”摘要和“配置数据”。
- 三个折叠组标题分别是“响应与成功判定”“报文高级设置”“响应变量”，并展示当前配置摘要/数量。
- `hasLegacyPayload(selNode)` 时显示“高级旧版配置”，原 `Fields/Structs/Append/BodyTemplate` 控件仍存在。
- 新范围模式不渲染旧字段长列表；旧模式不显示范围编辑造成的静默转换。
- 点击从范围模式切到自定义报文模板时必须二次确认并显式清空范围，取消则保持原配置。

- [ ] **Step 2: 运行测试确认失败**

```powershell
node --test src/IGW.WebUi/src/views/MesFlow.data-ranges.test.cjs
```

Expected: 新断言失败。

- [ ] **Step 3: 修改新节点默认行为和画布摘要**

`newNode('Upload')` 不再调用 `autoFillFromGroup`；新节点默认空范围模式。保留 `autoFillBlankUploads` 仅用于识别真正的旧节点：只有 payload 没有 `dataRanges` 属性且旧字段/结构为空时，才沿用历史自动填充，避免新节点再次展开几千点。

Upload 卡片摘要优先显示：

1. 新模式：`rangeSummary`。
2. 旧模式：`旧版 · N 字段 · M 结构体`。
3. 未配置：`尚未配置上报数据`。

运行态部分数据时，在摘要下显示橙色小字“某来源离线，已按 null 上报”，不把节点边框改成红色失败。

- [ ] **Step 4: 整理右侧详情但保持整体三栏和宽度不变**

Upload 区按顺序改为：

```text
节点标题
上报地址
等待超时
上报数据 [摘要] [配置数据]
响应与成功判定（折叠，标题含“code = 0”）
报文高级设置（折叠，标题含“附加 N · 默认/自定义模板”）
响应变量（折叠，标题含“N 个”）
```

高级旧版配置放在“报文高级设置”内，复用原输入控件和原函数，不重写其数据。Element Plus `el-collapse` 只控制展示，不改变 payload。右侧 `.cfg` 现有宽度、画布尺寸、拖动和连线逻辑不得修改。

- [ ] **Step 5: 处理模板模式的显式切换**

新增 `switchUploadMode(node, target)`：

- DataRanges → BodyTemplate/Legacy：提示会停止使用已选范围；确认后才清空 `dataRanges`。
- Legacy → DataRanges：提示旧字段/结构体不会自动转换；确认后才清空旧字段/结构体和模板。
- 已是对应模式时不做任何改动。

不得在打开页面、自动保存或普通字段编辑时自动调用该切换函数。

- [ ] **Step 6: 运行所有前端相关测试和构建**

```powershell
node --test src/IGW.WebUi/src/views src/IGW.WebUi/src/components src/IGW.WebUi/src/utils
pnpm --dir src/IGW.WebUi typecheck
pnpm --dir src/IGW.WebUi build
```

Expected: 全部 PASS；构建后的 Upload 右栏无超长字段列表，旧任务仍可编辑。

- [ ] **Step 7: 提交画布和详情接入**

```powershell
git add src/IGW.WebUi/src/views/MesFlow.vue src/IGW.WebUi/src/views/MesFlow.data-ranges.test.cjs src/IGW.WebUi/src/components/MesNodeDetail.vue
git commit -m "feat: simplify MES upload range configuration"
```

---

### Task 8: 加入 10,000 点回归、全量验证和现场验收记录

**Files:**
- Create: `tests/IGW.Tests/MesRangePerformanceTests.cs`
- Modify: `docs/superpowers/specs/2026-07-15-mes-cross-plc-data-ranges-design.md`

- [ ] **Step 1: 写 10,000 点热路径失败测试**

使用测试 `CountingDataHub`：目录从工程树编译完成后把 `ListTagIds()` 设为一旦调用就抛异常；注册 10,000 个点，预热一次后连续构建 20 次。断言：

```csharp
Assert.Equal(0, hub.ListTagIdsCalls);
Assert.Equal(10_000, result.TagCount);
Assert.All(results, json => Assert.Equal(firstLength, json.Length));
Assert.True(bytesPerBuild < 8 * 1024 * 1024, $"每轮分配过高: {bytesPerBuild:N0} bytes");
```

分配阈值只统计调用线程中“构建报文”区间，先预热 JIT；若 CI 波动，保留无全量扫描和输出稳定为硬断言，把耗时记录输出到测试日志而不设脆弱的毫秒阈值。

- [ ] **Step 2: 运行性能测试，确认旧实现不能满足**

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~MesRangePerformanceTests --logger "console;verbosity=detailed"
```

Expected: 在流式实现完成前失败；完成后 PASS，并输出每轮平均耗时/分配。

- [ ] **Step 3: 根据测量做最小优化**

只允许针对测量证据调整：

- 复用预编译输出段和排除集合。
- 使用 `ArrayPool` 保存本轮点值/有效位，`finally` 归还。
- 为 `ArrayBufferWriter<byte>` 设置接近上轮大小的初始容量。
- 避免 `string.Split`、LINQ 和路径拼接出现在单点热循环。

不得为了性能修改旧版 payload 的 JSON 语义。

- [ ] **Step 4: 跑后端全量测试**

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj
```

Expected: PASS，0 failed。

- [ ] **Step 5: 跑前端全量测试、类型检查和构建**

```powershell
node --test src/IGW.WebUi/src/views src/IGW.WebUi/src/components src/IGW.WebUi/src/utils
pnpm --dir src/IGW.WebUi typecheck
pnpm --dir src/IGW.WebUi build
```

Expected: PASS，Vite build 成功。

- [ ] **Step 6: 执行现场行为验收**

在本地工程中验证并记录结果：

1. 一个上报节点选择 PLC-A 两个 DB、PLC-B 一个 DB，不逐点配置即可保存应用。
2. 预览 JSON 层级为设备 → DB → 结构体，数组下标排序稳定。
3. 断开 PLC-B 后仍发 HTTP，请求中的 PLC-B 范围为 `null`，PLC-A 数据保持有效。
4. 恢复 PLC-B 后下一轮使用新好值，不复用离线期间残留值。
5. 旧逐字段任务打开、保存、F5 后 JSON 配置不发生变化。
6. 打开 10,000 点 DB 的排除编辑器时 DOM 行数保持在可视窗口附近，滚动和搜索可操作。
7. 画布节点数量、三栏布局和右侧宽度与修改前一致。

- [ ] **Step 7: 回填设计文档验收结果并提交**

在设计文档末尾新增“实施验收记录”，只写实际执行过的命令、版本和结果，不填写推测值。

```powershell
git add tests/IGW.Tests/MesRangePerformanceTests.cs docs/superpowers/specs/2026-07-15-mes-cross-plc-data-ranges-design.md
git commit -m "test: verify MES cross PLC data ranges"
```

---

## Final Verification Checklist

- [ ] `git diff --check` 无空白错误。
- [ ] `rg -n "TODO|TBD|待实现|临时"` 在本次新增文件中无未处理占位。
- [ ] 后端全量 xUnit 通过。
- [ ] 前端全部 `node:test`、`vue-tsc`、Vite build 通过。
- [ ] `MesUploadEngine` 新范围热路径不调用 `ListTagIds()`。
- [ ] 旧 `Fields/Structs/Append/BodyTemplate` 报文回归通过。
- [ ] 离线设备不泄漏最后好值，且不被计为 HTTP 失败。
- [ ] 新范围、旧版配置、自定义模板三种模式切换必须由用户显式确认。
- [ ] 只提交本功能涉及文件，工作区其他用户改动原样保留。
