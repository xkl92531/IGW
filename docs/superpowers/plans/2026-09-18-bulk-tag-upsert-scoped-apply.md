# 点位批量注册 + 按设备范围应用 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 新增 `POST /api/v1/project/bulk/tags`，一次请求把一批点位写进指定通道/设备/嵌套标记组，行级批量落库、只重建受影响的一台设备，百万点注册期间其它设备与读接口不中断。

**Architecture:** 契约与名称校验放 `IGW.Abstractions`；批量写库放 `IGW.Persistence.Sqlite` 新文件 `ProjectBulkTagWriter.cs`（复用 `ProjectMutationExecutor` 的通道/设备 upsert 以保持密钥处理一致），单设备读取在 `ProjectSnapshotReader` 加过滤；协调器新增 `ExecuteBulkAsync`（同一把锁、写时复制工程）；运行态在 `GatewayHost` 新增按设备应用（引擎 `ApplyDevicesAsync` + OPC UA 子树替换 + 条件实时流退役 + 北向保守回退）；REST 端点放 `IGW.Northbound.RestApi` 新文件 `ProjectBulkTagApi.cs`。

**Tech Stack:** .NET 10、ASP.NET Core Minimal API、Microsoft.Data.Sqlite、xUnit、OPC Foundation UA .NET Standard（`CustomNodeManager2`）。

**Spec:** `docs/superpowers/specs/2026-09-18-bulk-tag-upsert-scoped-apply-design.md`

## Global Constraints

- 工作目录：`C:\项目\IGW\.worktrees\bulk-tag-upsert`，分支 `feat/bulk-tag-upsert`。不得修改 `C:\项目\IGW` 主工作区。
- 不修改 `ProjectSnapshotWriter.cs` / `ProjectSnapshotWriter.Delta.cs`（主干正在迭代增量保存）。
- 不改变 `/Tag/AddTags`、`/api/v1/project/mutations`、工程导入的现有行为；全量回归测试必须保持全绿（基线 1799 通过）。
- 名称规则：组名与点名非空、不含 `.`、首尾无空白；违反整请求 400 `invalid_items`。
- 同一请求只针对一个设备；请求体上限 512MB；建议调用方每请求 ≤ 10 万项。
- 性能目标：单请求 10 万点服务端 ≤ 5 秒（不含设备重连）；已有 100 万点工程上追加 10 万点耗时与空工程相当（±30%）；100 万点分 10 次请求端到端 ≤ 90 秒；写入期间其它设备 DCS/REST 读取无 503。
- 测试命令：`dotnet test tests/IGW.Tests/IGW.Tests.csproj -c Release --filter <名称>`；全量：`dotnet test tests/IGW.Tests/IGW.Tests.csproj -c Release`。
- 代码风格：沿用所在文件的中文注释密度与命名；新文件用文件作用域命名空间。
- 提交信息结尾：`Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`。

---

## File Structure

| 文件 | 动作 | 职责 |
|---|---|---|
| `src/IGW.Abstractions/ProjectBulkTagContracts.cs` | 新建 | 请求/响应记录类型 + 纯函数校验 `ProjectBulkTagValidator` |
| `src/IGW.Abstractions/ProjectStorageContracts.cs` | 修改 | `IProjectRepository` 增加两个带默认实现的方法 |
| `src/IGW.Persistence.Sqlite/ProjectBulkTagWriter.cs` | 新建 | 单事务批量写库 |
| `src/IGW.Persistence.Sqlite/ProjectMutationExecutor.cs` | 修改 | `UpsertChannelAsync/UpsertDeviceAsync` 改 `internal` 供复用 |
| `src/IGW.Persistence.Sqlite/ProjectSnapshotReader.cs` | 修改 | 新增 `ReadDeviceAsync(deviceId)` |
| `src/IGW.Persistence.Sqlite/SqliteProjectRepository.cs` | 修改 | 接出两个新仓储方法 |
| `src/IGW.Core/ProjectConfigurationCoordinator.cs` | 修改 | `ExecuteBulkAsync`、写时复制、按设备应用委托、降级重试 |
| `src/IGW.Core/ProjectBulkProjection.cs` | 新建 | 写时复制：把一台设备投影进工程对象（纯函数） |
| `src/IGW.Core/Engine.cs` | 修改 | `ApplyDevicesAsync`、按设备的元数据刷新 |
| `src/IGW.Core/RealtimeStreamCoordinator.cs` | 修改 | `HasRoutesForTags` |
| `src/IGW.Northbound.OpcUaServer/IgwNodeManager.cs` + `OpcUaServerHost.cs` | 修改 | `ReplaceDeviceSubtree` |
| `src/IGW.Runtime/SiemensAddressModeGuard.cs` | 新建 | 设备内 Classic/Symbolic 混用判定 |
| `src/IGW.Runtime/ScopedApplyPolicy.cs` | 新建 | 判定按设备应用是否必须回退整工程应用 |
| `src/IGW.Runtime/GatewayHost.cs` | 修改 | `ApplyBulkTagsAsync`、按设备运行态应用 |
| `src/IGW.Core/IGatewayHost.cs` | 修改 | 接口新增 `ApplyBulkTagsAsync` |
| `src/IGW.Northbound.RestApi/DcsTagOperations.cs` | 修改 | 别名解析器后台重建、读请求不阻塞 |
| `src/IGW.Northbound.RestApi/ProjectBulkTagApi.cs` | 新建 | REST 端点 |
| `src/IGW.Runtime/Program.cs`（映射 API 的位置，按实际文件） | 修改 | 注册端点 |
| `benchmarks/IGW.ProjectStorage.Performance/*` | 修改 | 批量场景基准 |
| `tests/IGW.Tests/ProjectBulkTag*.cs` 等 | 新建 | 各任务测试 |

---

### Task 1: 批量请求契约与名称校验

**Files:**
- Create: `src/IGW.Abstractions/ProjectBulkTagContracts.cs`
- Test: `tests/IGW.Tests/ProjectBulkTagValidatorTests.cs`

**Interfaces:**
- Produces:
  - `record ProjectBulkChannelSpec(string Name, string DriverId, Dictionary<string,string>? Settings)`
  - `record ProjectBulkDeviceSpec(string Name, string Endpoint, Dictionary<string,string>? Settings)`
  - `sealed class ProjectBulkTagItem { List<string> GroupPath; string Name; string Address; IgwDataType DataType; TagAccess Access; int ScanRateMs; string? Description; ArrayDef? Array; Dictionary<string,string>? Metadata; int? StringLength; string? EngUnit }`
  - `sealed class ProjectBulkTagRequest { ProjectBulkChannelSpec Channel; ProjectBulkDeviceSpec Device; List<List<string>> ReplaceGroups; List<ProjectBulkTagItem> Items }`
  - `record ProjectBulkTagResult(bool Applied, bool Conflict, ProjectRevision Revision, string? ChannelId, string? DeviceId, int Created, int Updated, int Deleted, string? ErrorCode, string? Error)`
  - `record ProjectBulkValidationError(int Index, string Reason)`
  - `static class ProjectBulkTagValidator { IReadOnlyList<ProjectBulkValidationError> Validate(ProjectBulkTagRequest request, int maxErrors = 50); bool IsValidName(string? name) }`（`Index = -1` 表示通道/设备/replaceGroups 层面的错误）

- [ ] **Step 1: 写失败测试**

```csharp
using IGW.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class ProjectBulkTagValidatorTests
{
    private static ProjectBulkTagRequest Request(params ProjectBulkTagItem[] items) => new()
    {
        Channel = new ProjectBulkChannelSpec("PLC1", "Siemens.S7", null),
        Device = new ProjectBulkDeviceSpec("S7Plus", "10.0.0.1:102", null),
        Items = [.. items],
    };

    private static ProjectBulkTagItem Item(string name, params string[] groups) => new()
    {
        GroupPath = [.. groups], Name = name, Address = "DB1.X." + name, DataType = IgwDataType.Bool,
        Access = TagAccess.ReadWrite, ScanRateMs = 100,
    };

    [Fact]
    public void Valid_request_has_no_errors() =>
        Assert.Empty(ProjectBulkTagValidator.Validate(Request(Item("A", "DB1", "Sub"), Item("A", "DB2"))));

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a.b")]
    [InlineData(" lead")]
    [InlineData("trail ")]
    public void Invalid_tag_name_is_rejected(string name)
    {
        var errors = ProjectBulkTagValidator.Validate(Request(Item(name, "DB1")));
        Assert.Equal(0, Assert.Single(errors).Index);
    }

    [Fact]
    public void Dotted_group_segment_is_rejected() =>
        Assert.Equal(0, Assert.Single(ProjectBulkTagValidator.Validate(Request(Item("A", "DB1.Type")))).Index);

    [Fact]
    public void Duplicate_name_in_same_group_is_rejected_case_insensitively()
    {
        var errors = ProjectBulkTagValidator.Validate(Request(Item("Run", "DB1"), Item("RUN", "db1")));
        Assert.Equal(1, Assert.Single(errors).Index);
    }

    [Fact]
    public void Channel_device_and_replace_group_names_are_checked()
    {
        var request = Request(Item("A"));
        request.Channel = request.Channel with { Name = "a.b" };
        request.Device = request.Device with { Name = "" };
        request.ReplaceGroups = [[], ["x.y"]];
        var errors = ProjectBulkTagValidator.Validate(request);
        Assert.Equal(4, errors.Count);
        Assert.All(errors, error => Assert.Equal(-1, error.Index));
    }

    [Fact]
    public void Errors_are_capped()
    {
        var items = Enumerable.Range(0, 200).Select(_ => Item("")).ToArray();
        Assert.Equal(50, ProjectBulkTagValidator.Validate(Request(items)).Count);
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj -c Release --filter ProjectBulkTagValidatorTests`
Expected: 编译失败（`ProjectBulkTagRequest` 不存在）。

- [ ] **Step 3: 实现**

```csharp
namespace IGW.Abstractions;

/// <summary>批量注册的目标通道：不存在时按此定义创建；已存在时 settings 不覆盖。</summary>
public sealed record ProjectBulkChannelSpec(string Name, string DriverId, Dictionary<string, string>? Settings);

/// <summary>批量注册的目标设备：不存在时创建；已存在时只更新 endpoint 并补齐缺失的 settings 键。</summary>
public sealed record ProjectBulkDeviceSpec(string Name, string Endpoint, Dictionary<string, string>? Settings);

/// <summary>一条待写入的点位。GroupPath 每段一个标记组，空 = 设备根层。</summary>
public sealed class ProjectBulkTagItem
{
    public List<string> GroupPath { get; set; } = [];
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public IgwDataType DataType { get; set; } = IgwDataType.Int16;
    public TagAccess Access { get; set; } = TagAccess.Read;
    public int ScanRateMs { get; set; } = 100;
    public string? Description { get; set; }
    public ArrayDef? Array { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
    public int? StringLength { get; set; }
    public string? EngUnit { get; set; }
}

public sealed class ProjectBulkTagRequest
{
    public ProjectBulkChannelSpec Channel { get; set; } = new("", "", null);
    public ProjectBulkDeviceSpec Device { get; set; } = new("", "", null);
    /// <summary>写入前整棵删除的根组路径（相对设备根）。用于清理 PLC 中已删除的变量。</summary>
    public List<List<string>> ReplaceGroups { get; set; } = [];
    public List<ProjectBulkTagItem> Items { get; set; } = [];
}

public sealed record ProjectBulkTagResult(
    bool Applied,
    bool Conflict,
    ProjectRevision Revision,
    string? ChannelId,
    string? DeviceId,
    int Created,
    int Updated,
    int Deleted,
    string? ErrorCode,
    string? Error);

/// <summary>Index=-1 表示通道/设备/replaceGroups 层面的错误；否则为 Items 下标。</summary>
public sealed record ProjectBulkValidationError(int Index, string Reason);

public static class ProjectBulkTagValidator
{
    /// <summary>与 IGW 界面一致：非空、不含 "."、首尾无空白（canonical_path 与点位 id 均以 "." 拼接）。</summary>
    public static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && !name.Contains('.') && name.Trim().Length == name.Length;

    public static IReadOnlyList<ProjectBulkValidationError> Validate(ProjectBulkTagRequest request, int maxErrors = 50)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new List<ProjectBulkValidationError>();
        void Add(int index, string reason)
        {
            if (errors.Count < maxErrors) errors.Add(new ProjectBulkValidationError(index, reason));
        }

        if (!IsValidName(request.Channel?.Name)) Add(-1, "通道名无效（不能为空、不能含 \".\"、首尾不能有空白）");
        if (string.IsNullOrWhiteSpace(request.Channel?.DriverId)) Add(-1, "通道 driverId 不能为空");
        if (!IsValidName(request.Device?.Name)) Add(-1, "设备名无效（不能为空、不能含 \".\"、首尾不能有空白）");
        foreach (var path in request.ReplaceGroups ?? [])
            if (path is null || path.Count == 0 || !path.All(IsValidName))
                Add(-1, "replaceGroups 含无效组路径（每项至少一段，每段须为有效名称）");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = request.Items ?? [];
        for (int i = 0; i < items.Count && errors.Count < maxErrors; i++)
        {
            var item = items[i];
            if (item is null) { Add(i, "点位为空"); continue; }
            if (!(item.GroupPath ?? []).All(IsValidName)) { Add(i, "组路径含无效名称"); continue; }
            if (!IsValidName(item.Name)) { Add(i, "点名无效（不能为空、不能含 \".\"、首尾不能有空白）"); continue; }
            if (string.IsNullOrWhiteSpace(item.Address)) { Add(i, "地址不能为空"); continue; }
            string key = string.Join('\u001f', (item.GroupPath ?? []).Append(item.Name));
            if (!seen.Add(key)) Add(i, "同一组内点名重复：" + item.Name);
        }
        return errors;
    }
}
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj -c Release --filter ProjectBulkTagValidatorTests`
Expected: PASS（7 项）

- [ ] **Step 5: 提交**

```bash
git add src/IGW.Abstractions/ProjectBulkTagContracts.cs tests/IGW.Tests/ProjectBulkTagValidatorTests.cs
git commit -m "feat: bulk tag request contracts and name validation"
```

---

### Task 2: 批量写库 `ProjectBulkTagWriter`

**Files:**
- Create: `src/IGW.Persistence.Sqlite/ProjectBulkTagWriter.cs`
- Modify: `src/IGW.Persistence.Sqlite/ProjectMutationExecutor.cs`（`UpsertChannelAsync`、`UpsertDeviceAsync` 由 `private` 改为 `internal`，其余不动）
- Modify: `src/IGW.Abstractions/ProjectStorageContracts.cs`（`IProjectRepository` 追加默认实现方法）
- Modify: `src/IGW.Persistence.Sqlite/SqliteProjectRepository.cs`
- Test: `tests/IGW.Tests/ProjectBulkTagWriterTests.cs`

**Interfaces:**
- Consumes: Task 1 全部类型。
- Produces:
  - `IProjectRepository.BulkUpsertTagsAsync(ProjectBulkTagRequest request, ProjectRevision? expected, CancellationToken ct) : Task<ProjectBulkTagResult>`（默认实现抛 `NotSupportedException`）
  - `ProjectBulkTagWriter(ProjectDatabase database, ProjectMutationExecutor executor)`，`WriteAsync(request, expected, ct)`
  - 结果语义：`Created/Updated` 按点位计；`Deleted` = `replaceGroups` 删除的点位数；未被请求覆盖且未被替换的旧点保持不动。

**实现要点（必须全部满足）：**

1. 一个连接、一个事务：`BeginTransaction(deferred: false)`（`BEGIN IMMEDIATE`），事务内 `PRAGMA defer_foreign_keys=ON;`。
2. 修订号：`expected` 非空且不等于当前 → 回滚返回 `Conflict=true`。
3. 通道：`SELECT id, driver_id FROM channels WHERE name=$name COLLATE NOCASE`。不存在 → 用 `executor.UpsertChannelAsync` 创建（`new ChannelDef{ Id="", Name, DriverId, Settings = spec.Settings ?? new() }`）；存在但 driver 不同 → 抛 `InvalidDataException("channel_driver_mismatch: …")`；存在则不改 settings。
4. 设备：`SELECT id, endpoint, settings_json FROM devices WHERE channel_id=$c AND name=$n COLLATE NOCASE`。不存在 → `executor.UpsertDeviceAsync(connection, tx, channelId, new DeviceDef{…})`；存在 → 读回完整 `DeviceDef`（`SqliteProjectRepository.ReadDeviceDocumentAsync`），endpoint 不同则改、`spec.Settings` 中设备没有的键补上，有任何变化才调用 `UpsertDeviceAsync` 回写（保持密钥 vault 处理一致）。
5. 读目标设备现有组：`SELECT id, parent_group_id, name, canonical_path, sort_order FROM tag_groups WHERE device_id=$d`，建 `Dictionary<(string? parentId, string nameUpper), GroupRow>` 与每个父节点的 `nextSort`。
6. `replaceGroups`：按路径找到根组，收集其所有后代组 id（内存遍历），先 `DELETE FROM tags WHERE group_id IN (…)`（统计删除数），再自底向上 `DELETE FROM tag_groups WHERE id=$id`；同时从内存字典移除。
7. 读目标设备现有点：`SELECT id, group_id, name FROM tags WHERE device_id=$d`，建 `Dictionary<(string? groupId, string nameUpper), string tagId>` 与每组 `nextSort`（`MAX(sort_order)+1` 一次性查询：`SELECT group_id, MAX(sort_order) FROM tags WHERE device_id=$d GROUP BY group_id`）。
8. 组路径逐段确保存在：新组用预编译的 `INSERT INTO tag_groups(id,device_id,parent_group_id,name,description,canonical_path,flow_enabled,sort_order,row_version) VALUES($id,$d,$p,$n,NULL,$path,0,$sort,1)`；`canonical_path = 设备 canonical（"{channel}.{device}"）+ "." + 各段`。
9. 点：两条预编译命令（tags、tag_options），SQL 与 `ProjectMutationExecutor.UpsertTagAsync` 相同但 `sort_order` 用参数（新点取 `nextSort++`，已有点在 `ON CONFLICT` 中不改 sort_order）；已有点复用原 id，新点 `Guid.NewGuid().ToString("N")`。`canonical_path` = 组 canonical + "." + 点名。参数对象建一次，每行只改 `.Value`。
10. 修订号 +1（SQL 同 `ProjectMutationExecutor.ExecuteAsync` 内的 `UPDATE project_meta …`），提交。
11. `SqliteException`/`InvalidDataException`/`InvalidOperationException` → 回滚，返回 `Applied=false, ErrorCode="persistence_failed"`（driver 不符时 `ErrorCode="channel_driver_mismatch"`）。

- [ ] **Step 1: 写失败测试**

```csharp
using IGW.Abstractions;
using IGW.Core;
using IGW.Core.Security;
using IGW.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IGW.Tests;

public sealed class ProjectBulkTagWriterTests
{
    [Fact]
    public async Task Creates_channel_device_nested_groups_and_tags_in_one_revision()
    {
        await using var fx = await Fixture.CreateAsync(new IgwProject());
        var result = await fx.Repository.BulkUpsertTagsAsync(Request(
            Item("Error1", "DB490", "Type", "Errors"), Item("Error2", "DB490", "Type", "Errors"), Item("Run")), null, default);

        Assert.True(result.Applied);
        Assert.Equal(3, result.Created);
        Assert.Equal(1, result.Revision.Value);
        var project = await fx.Repository.LoadRuntimeProjectAsync(default);
        var device = Assert.Single(Assert.Single(project.Channels).Devices);
        Assert.Equal("S7Plus", device.Name);
        Assert.Equal("S7-1500", device.Settings["model"]);
        var errors = device.Groups.Single(g => g.Name == "DB490").SubGroups.Single(g => g.Name == "Type").SubGroups.Single();
        Assert.Equal(["Error1", "Error2"], errors.Tags.Select(t => t.Name));
        Assert.Equal("Run", Assert.Single(device.Tags).Name);
        Assert.Equal("PLC1.S7Plus.DB490.Type.Errors.Error1",
            await fx.TextAsync("SELECT canonical_path FROM tags WHERE name='Error1'"));
    }

    [Fact]
    public async Task Existing_channel_and_device_settings_are_kept_and_missing_keys_added()
    {
        await using var fx = await Fixture.CreateAsync(new IgwProject());
        await fx.Repository.BulkUpsertTagsAsync(Request(Item("A")), null, default);
        await fx.ExecAsync("UPDATE devices SET settings_json='{\"model\":\"S7-1500\",\"slot\":\"3\"}'");

        var request = Request(Item("B"));
        request.Device = request.Device with
        {
            Endpoint = "10.0.0.9:102",
            Settings = new() { ["model"] = "S7-1200", ["slot"] = "1", ["dcsLogicalDevice"] = "S1500_10.0.0.9" },
        };
        await fx.Repository.BulkUpsertTagsAsync(request, null, default);

        var device = (await fx.Repository.LoadRuntimeProjectAsync(default)).Channels[0].Devices[0];
        Assert.Equal("10.0.0.9:102", device.Endpoint);
        Assert.Equal("S7-1500", device.Settings["model"]);
        Assert.Equal("3", device.Settings["slot"]);
        Assert.Equal("S1500_10.0.0.9", device.Settings["dcsLogicalDevice"]);
    }

    [Fact]
    public async Task Existing_tags_are_updated_in_place_and_keep_their_id()
    {
        await using var fx = await Fixture.CreateAsync(new IgwProject());
        await fx.Repository.BulkUpsertTagsAsync(Request(Item("A", "DB1")), null, default);
        string id = await fx.TextAsync("SELECT id FROM tags WHERE name='A'");

        var changed = Item("A", "DB1");
        changed.Address = "DB1.Changed";
        var result = await fx.Repository.BulkUpsertTagsAsync(Request(changed), null, default);

        Assert.Equal((0, 1), (result.Created, result.Updated));
        Assert.Equal(id, await fx.TextAsync("SELECT id FROM tags WHERE name='A'"));
        Assert.Equal("DB1.Changed", await fx.TextAsync("SELECT address FROM tags WHERE name='A'"));
    }

    [Fact]
    public async Task Replace_groups_removes_stale_subtree_before_writing()
    {
        await using var fx = await Fixture.CreateAsync(new IgwProject());
        await fx.Repository.BulkUpsertTagsAsync(Request(
            Item("Old", "DB1", "Sub"), Item("Keep", "DB1"), Item("Other", "DB2")), null, default);

        var request = Request(Item("Keep", "DB1"));
        request.ReplaceGroups = [["DB1"]];
        var result = await fx.Repository.BulkUpsertTagsAsync(request, null, default);

        Assert.Equal(2, result.Deleted);
        Assert.Equal(1, result.Created);
        Assert.Equal(2, await fx.CountAsync("tags"));
        Assert.Equal(0, await fx.CountAsync("tag_groups WHERE name='Sub'"));
        Assert.Equal(1, await fx.CountAsync("tags WHERE name='Other'"));
    }

    [Fact]
    public async Task Driver_mismatch_rolls_back_everything()
    {
        await using var fx = await Fixture.CreateAsync(new IgwProject());
        await fx.Repository.BulkUpsertTagsAsync(Request(Item("A")), null, default);
        var request = Request(Item("B"));
        request.Channel = request.Channel with { DriverId = "Modbus.Tcp" };

        var result = await fx.Repository.BulkUpsertTagsAsync(request, null, default);

        Assert.False(result.Applied);
        Assert.Equal("channel_driver_mismatch", result.ErrorCode);
        Assert.Equal(1, (await fx.Repository.GetRevisionAsync(default)).Value);
        Assert.Equal(1, await fx.CountAsync("tags"));
    }

    [Fact]
    public async Task Stale_expected_revision_is_a_conflict()
    {
        await using var fx = await Fixture.CreateAsync(new IgwProject());
        await fx.Repository.BulkUpsertTagsAsync(Request(Item("A")), null, default);
        var result = await fx.Repository.BulkUpsertTagsAsync(Request(Item("B")), new ProjectRevision(0), default);
        Assert.True(result.Conflict);
        Assert.Equal(1, await fx.CountAsync("tags"));
    }

    [Fact]
    public async Task Hundred_thousand_tags_are_written_quickly()
    {
        await using var fx = await Fixture.CreateAsync(new IgwProject());
        var items = Enumerable.Range(0, 100_000).Select(i => Item("T" + i, "G" + (i / 1000))).ToArray();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await fx.Repository.BulkUpsertTagsAsync(Request(items), null, default);
        Assert.True(result.Applied);
        Assert.Equal(100_000, result.Created);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"写入 10 万点耗时 {watch.Elapsed}");
    }

    internal static ProjectBulkTagRequest Request(params ProjectBulkTagItem[] items) => new()
    {
        Channel = new ProjectBulkChannelSpec("PLC1", "Siemens.S7", new() { ["timeoutMs"] = "2000" }),
        Device = new ProjectBulkDeviceSpec("S7Plus", "10.0.0.1:102", new() { ["model"] = "S7-1500" }),
        Items = [.. items],
    };

    internal static ProjectBulkTagItem Item(string name, params string[] groups) => new()
    {
        GroupPath = [.. groups], Name = name, Address = "DB1." + name, DataType = IgwDataType.Bool,
        Access = TagAccess.ReadWrite, ScanRateMs = 100,
    };

    internal sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly ProjectDatabase _database;
        public SqliteProjectRepository Repository { get; }

        private Fixture(string directory, ProjectDatabase database, SqliteProjectRepository repository)
        { _directory = directory; _database = database; Repository = repository; }

        public static async Task<Fixture> CreateAsync(IgwProject project)
        {
            string directory = Path.Combine(Path.GetTempPath(), "igw-bulk-tags", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var database = new ProjectDatabase(Path.Combine(directory, "project.db"));
            var repository = new SqliteProjectRepository(database,
                new ProjectSecretVault(new InstallationSecretProtector(Path.Combine(directory, "igw.masterkey"))));
            await repository.InitializeAsync(default);
            await repository.ReplaceFromSnapshotAsync(project, new ProjectRevision(0), default);
            return new Fixture(directory, database, repository);
        }

        public async Task<long> CountAsync(string tableAndWhere) =>
            Convert.ToInt64(await ScalarAsync($"SELECT COUNT(*) FROM {tableAndWhere}"));
        public async Task<string> TextAsync(string sql) => Convert.ToString(await ScalarAsync(sql))!;
        public async Task ExecAsync(string sql)
        {
            await using SqliteConnection connection = await _database.OpenAsync(default);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
        private async Task<object?> ScalarAsync(string sql)
        {
            await using SqliteConnection connection = await _database.OpenAsync(default);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return await command.ExecuteScalarAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Repository.DisposeAsync();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_directory, recursive: true); } catch { }
        }
    }
}
```

> 注：`ReplaceFromSnapshotAsync(new IgwProject(), rev0)` 之后修订号的初值以实际为准；若初始化后已是 1，则把断言里的 `1`/`0` 相应调整为「初值+1」/「初值」，并在断言前先读取初值。实现者先跑一次看初值再定。

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj -c Release --filter ProjectBulkTagWriterTests`
Expected: 编译失败（`BulkUpsertTagsAsync` 不存在）。

- [ ] **Step 3: 接口与仓储接线**

`ProjectStorageContracts.cs` 的 `IProjectRepository` 末尾追加：

```csharp
    Task<ProjectBulkTagResult> BulkUpsertTagsAsync(
        ProjectBulkTagRequest request,
        ProjectRevision? expected,
        CancellationToken cancellationToken) =>
        Task.FromException<ProjectBulkTagResult>(new NotSupportedException("当前工程仓储不支持批量注册点位。"));
```

`SqliteProjectRepository` 构造函数里创建 `_bulkTags = new ProjectBulkTagWriter(database, _mutations);`，并实现：

```csharp
    public Task<ProjectBulkTagResult> BulkUpsertTagsAsync(
        ProjectBulkTagRequest request,
        ProjectRevision? expected,
        CancellationToken cancellationToken) =>
        _bulkTags.WriteAsync(request, expected, cancellationToken);
```

- [ ] **Step 4: 实现 `ProjectBulkTagWriter`**（按上面「实现要点」1–11 编写；参数化 SQL，禁止字符串拼接用户输入；`IN (…)` 用临时表 `CREATE TEMP TABLE bulk_ids(id TEXT PRIMARY KEY)` + 批量插入，与 `ProjectMutationExecutor` 的 `mutation_ids` 手法一致）

- [ ] **Step 5: 运行确认通过**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj -c Release --filter ProjectBulkTagWriterTests`
Expected: PASS（7 项），10 万点用例耗时明显低于 15 秒（记录实际值到提交信息）。

- [ ] **Step 6: 回归相关测试**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj -c Release --filter "FullyQualifiedName~Project"`
Expected: 全部 PASS。

- [ ] **Step 7: 提交**

```bash
git add src/IGW.Abstractions/ProjectStorageContracts.cs src/IGW.Persistence.Sqlite tests/IGW.Tests/ProjectBulkTagWriterTests.cs
git commit -m "feat: bulk tag upsert writer with single-transaction row-level SQL"
```

---

### Task 3: 单设备读取 `LoadDeviceAsync`

**Files:**
- Modify: `src/IGW.Persistence.Sqlite/ProjectSnapshotReader.cs`
- Modify: `src/IGW.Abstractions/ProjectStorageContracts.cs`、`src/IGW.Persistence.Sqlite/SqliteProjectRepository.cs`
- Test: `tests/IGW.Tests/ProjectDeviceLoadTests.cs`

**Interfaces:**
- Produces: `IProjectRepository.LoadDeviceAsync(string deviceId, CancellationToken ct) : Task<ProjectDeviceSnapshot>`（默认抛 `NotSupportedException`）；`record ProjectDeviceSnapshot(ChannelDef Channel, DeviceDef Device)`——`Channel` 为**不含 Devices 的**通道定义（只有 Id/Name/DriverId/Settings/Description/KepProperties），`Device` 为完整设备（组、点、已还原密钥）。`ProjectDeviceSnapshot` 定义放在 `ProjectBulkTagContracts.cs`。

**实现要点：**
- `ReadDevicesAsync/ReadGroupsAsync/ReadTagsAsync` 各增加可选参数 `string? deviceId`，非空时 SQL 加 `WHERE device_id=$deviceId`（设备表为 `WHERE id=$deviceId`）；通道只读该设备所属一行。组装逻辑复用现有代码，不复制。
- 密钥：读 `secret_entries WHERE (owner_type='channel' AND owner_id=$channelId) OR (owner_type='device' AND owner_id=$deviceId)`，构造只含这一通道一设备的临时 `IgwProject` 调 `_vault.Restore`，再取出。
- 设备不存在抛 `KeyNotFoundException`。

- [ ] **Step 1: 写失败测试**

```csharp
using IGW.Abstractions;
using IGW.Core;
using Xunit;

namespace IGW.Tests;

public sealed class ProjectDeviceLoadTests
{
    [Fact]
    public async Task Loaded_device_equals_the_same_device_from_full_load()
    {
        await using var fx = await ProjectBulkTagWriterTests.Fixture.CreateAsync(new IgwProject());
        await fx.Repository.BulkUpsertTagsAsync(ProjectBulkTagWriterTests.Request(
            ProjectBulkTagWriterTests.Item("A", "G1", "G2"), ProjectBulkTagWriterTests.Item("B")), null, default);
        var other = ProjectBulkTagWriterTests.Request(ProjectBulkTagWriterTests.Item("X", "G9"));
        other.Device = other.Device with { Name = "S7" };
        await fx.Repository.BulkUpsertTagsAsync(other, null, default);

        IgwProject full = await fx.Repository.LoadRuntimeProjectAsync(default);
        DeviceDef expected = full.Channels[0].Devices.Single(d => d.Name == "S7Plus");
        ProjectDeviceSnapshot loaded = await fx.Repository.LoadDeviceAsync(expected.Id, default);

        Assert.Equal(ProjectStore.ToJson(new IgwProject { Channels = [new ChannelDef { Id = "c", Name = "c", DriverId = "d", Devices = [expected] }] }),
                     ProjectStore.ToJson(new IgwProject { Channels = [new ChannelDef { Id = "c", Name = "c", DriverId = "d", Devices = [loaded.Device] }] }));
        Assert.Equal(full.Channels[0].Id, loaded.Channel.Id);
        Assert.Empty(loaded.Channel.Devices);
    }

    [Fact]
    public async Task Missing_device_throws()
    {
        await using var fx = await ProjectBulkTagWriterTests.Fixture.CreateAsync(new IgwProject());
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fx.Repository.LoadDeviceAsync("nope", default));
    }
}
```

- [ ] **Step 2: 运行确认失败**：`--filter ProjectDeviceLoadTests`，编译失败。
- [ ] **Step 3: 实现**（按要点）。
- [ ] **Step 4: 运行确认通过**：`--filter "ProjectDeviceLoadTests|ProjectSnapshotRoundTripTests|IncrementalProjectSaveTests"` 全 PASS。
- [ ] **Step 5: 提交** `git commit -m "feat: load a single device subtree from the project database"`

---

### Task 4: 写时复制投影 + 协调器 `ExecuteBulkAsync`

**Files:**
- Create: `src/IGW.Core/ProjectBulkProjection.cs`
- Modify: `src/IGW.Core/ProjectConfigurationCoordinator.cs`
- Test: `tests/IGW.Tests/ProjectBulkProjectionTests.cs`、`tests/IGW.Tests/ProjectConfigurationCoordinatorTests.cs`（追加用例，扩展其 `RecordingRepository` 实现两个新方法）

**Interfaces:**
- Consumes: Task 1–3。
- Produces:
  - `static class ProjectBulkProjection { IgwProject WithDevice(IgwProject current, ProjectDeviceSnapshot snapshot, out string deviceFullName) }`：返回新 `IgwProject`（浅拷贝：所有非拓扑属性引用复用；`Channels` 为新列表；目标通道为新 `ChannelDef`，其 `Devices` 为新列表且目标设备被替换/追加；其它通道、设备对象**引用不变**）。`deviceFullName = "{channel.Name}.{device.Name}"`。
  - `ProjectApplyScope.Devices`（新枚举成员）。
  - 协调器构造函数新增可选参数 `Func<IgwProject, IReadOnlyList<string>, CancellationToken, Task>? applyDevicesRuntime = null`（为 null 时回退 `_applyRuntime(project, Full, ct)`）。
  - `Task<ProjectBulkApplyResult> ExecuteBulkAsync(ProjectBulkTagRequest request, ProjectRevision? expected, CancellationToken ct)`；`record ProjectBulkApplyResult(ProjectBulkTagResult Persisted, bool RuntimeApplied, string? ErrorCode, string? ErrorMessage, string DeviceFullName)`。
  - 降级：运行态失败 `SetDegraded(project, Devices, msg)` 并记住 `_pendingDevices`；`RetryRuntimeApplyAsync` 在 `_pendingScope==Devices` 时调用 `applyDevicesRuntime(_pendingProject, _pendingDevices)`。

`ProjectBulkProjection.WithDevice` 实现：

```csharp
namespace IGW.Core;

using IGW.Abstractions;

/// <summary>批量注册后的写时复制：只替换一台设备，其余拓扑对象引用复用（不克隆、不序列化全工程）。</summary>
public static class ProjectBulkProjection
{
    public static IgwProject WithDevice(IgwProject current, ProjectDeviceSnapshot snapshot, out string deviceFullName)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(snapshot);
        IgwProject next = current.ShallowCopy();
        next.Channels = [.. current.Channels];
        int channelIndex = next.Channels.FindIndex(c => c.Id == snapshot.Channel.Id);
        ChannelDef channel;
        if (channelIndex < 0)
        {
            channel = snapshot.Channel.ShallowCopy();
            channel.Devices = [snapshot.Device];
            next.Channels.Add(channel);
        }
        else
        {
            channel = next.Channels[channelIndex].ShallowCopy();
            channel.Devices = [.. channel.Devices];
            int deviceIndex = channel.Devices.FindIndex(d => d.Id == snapshot.Device.Id);
            if (deviceIndex < 0) channel.Devices.Add(snapshot.Device);
            else channel.Devices[deviceIndex] = snapshot.Device;
            next.Channels[channelIndex] = channel;
        }
        deviceFullName = channel.Name + "." + snapshot.Device.Name;
        return next;
    }
}
```

`IgwProject.ShallowCopy()` 与 `ChannelDef.ShallowCopy()`：在 `src/IGW.Abstractions/ProjectModel.cs` 对应类型里各加一行 `public IgwProject ShallowCopy() => (IgwProject)MemberwiseClone();`（`ChannelDef` 同理）。若 `Channels`/`Devices` 属性为 `init` 或只读，改用对象初始化器逐属性复制，并在测试里断言复制完整（下面的测试会发现遗漏）。

- [ ] **Step 1: 写投影失败测试**

```csharp
using IGW.Abstractions;
using IGW.Core;
using Xunit;

namespace IGW.Tests;

public sealed class ProjectBulkProjectionTests
{
    private static IgwProject Project() => new()
    {
        Name = "p",
        Channels =
        [
            new ChannelDef { Id = "c1", Name = "A", DriverId = "Siemens.S7",
                Devices = [new DeviceDef { Id = "d1", Name = "D1" }, new DeviceDef { Id = "d2", Name = "D2" }] },
            new ChannelDef { Id = "c2", Name = "B", DriverId = "Siemens.S7", Devices = [new DeviceDef { Id = "d3", Name = "D3" }] },
        ],
    };

    [Fact]
    public void Replaces_only_the_target_device_and_reuses_other_objects()
    {
        var current = Project();
        var replacement = new DeviceDef { Id = "d1", Name = "D1", Tags = [new TagDef { Name = "T" }] };
        var next = ProjectBulkProjection.WithDevice(current,
            new ProjectDeviceSnapshot(new ChannelDef { Id = "c1", Name = "A", DriverId = "Siemens.S7" }, replacement), out var full);

        Assert.Equal("A.D1", full);
        Assert.Same(replacement, next.Channels[0].Devices[0]);
        Assert.Same(current.Channels[0].Devices[1], next.Channels[0].Devices[1]);
        Assert.Same(current.Channels[1], next.Channels[1]);
        Assert.NotSame(current.Channels[0], next.Channels[0]);
        Assert.Empty(current.Channels[0].Devices[0].Tags);          // 原工程不被修改
        Assert.Equal(current.Name, next.Name);
    }

    [Fact]
    public void Adds_new_device_and_new_channel()
    {
        var current = Project();
        var next = ProjectBulkProjection.WithDevice(current,
            new ProjectDeviceSnapshot(new ChannelDef { Id = "c9", Name = "N", DriverId = "Siemens.S7" }, new DeviceDef { Id = "d9", Name = "D9" }), out var full);
        Assert.Equal("N.D9", full);
        Assert.Equal(3, next.Channels.Count);
        Assert.Equal(2, current.Channels.Count);
    }
}
```

- [ ] **Step 2: 确认失败** → **Step 3: 实现投影** → **Step 4: 确认通过**（`--filter ProjectBulkProjectionTests`）

- [ ] **Step 5: 写协调器失败测试**（追加到 `ProjectConfigurationCoordinatorTests`；扩展 `RecordingRepository`：`BulkUpsertTagsAsync` 返回预置结果，`LoadDeviceAsync` 返回预置快照，并记录 `LoadRuntimeProjectAsync` 调用次数）

```csharp
    [Fact]
    public async Task Bulk_execution_applies_only_the_target_device_without_full_reload()
    {
        var repository = new RecordingRepository();
        repository.BulkResult = new ProjectBulkTagResult(true, false, new ProjectRevision(2), "c1", "d1", 3, 0, 0, null, null);
        repository.DeviceSnapshot = new ProjectDeviceSnapshot(
            new ChannelDef { Id = "c1", Name = "A", DriverId = "Siemens.S7" }, new DeviceDef { Id = "d1", Name = "D1" });
        IReadOnlyList<string>? appliedDevices = null;
        int fullApplies = 0;
        var coordinator = new ProjectConfigurationCoordinator(repository, new IgwProject(),
            (_, _, _) => { fullApplies++; return Task.CompletedTask; },
            validate: null,
            applyDevicesRuntime: (_, devices, _) => { appliedDevices = devices; return Task.CompletedTask; });

        var result = await coordinator.ExecuteBulkAsync(new ProjectBulkTagRequest(), null, default);

        Assert.True(result.RuntimeApplied);
        Assert.Equal(["A.D1"], appliedDevices);
        Assert.Equal(0, fullApplies);
        Assert.Equal(0, repository.LoadRuntimeProjectCalls);
        Assert.Equal("D1", Assert.Single(Assert.Single(coordinator.HealthyProject.Channels).Devices).Name);
    }

    [Fact]
    public async Task Failed_scoped_apply_degrades_and_retries_the_same_devices()
    {
        var repository = new RecordingRepository();
        repository.BulkResult = new ProjectBulkTagResult(true, false, new ProjectRevision(2), "c1", "d1", 1, 0, 0, null, null);
        repository.DeviceSnapshot = new ProjectDeviceSnapshot(
            new ChannelDef { Id = "c1", Name = "A", DriverId = "Siemens.S7" }, new DeviceDef { Id = "d1", Name = "D1" });
        int calls = 0;
        var coordinator = new ProjectConfigurationCoordinator(repository, new IgwProject(),
            (_, _, _) => Task.CompletedTask, validate: null,
            applyDevicesRuntime: (_, _, _) => ++calls == 1 ? throw new InvalidOperationException("boom") : Task.CompletedTask);

        var failed = await coordinator.ExecuteBulkAsync(new ProjectBulkTagRequest(), null, default);
        Assert.False(failed.RuntimeApplied);
        Assert.Equal("runtime_apply_failed", failed.ErrorCode);
        Assert.True(coordinator.Degraded);

        var retried = await coordinator.RetryRuntimeApplyAsync(default);
        Assert.True(retried.RuntimeApplied);
        Assert.False(coordinator.Degraded);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Persist_failure_or_conflict_does_not_touch_runtime()
    {
        var repository = new RecordingRepository();
        repository.BulkResult = new ProjectBulkTagResult(false, true, new ProjectRevision(5), null, null, 0, 0, 0, "revision_conflict", "x");
        bool applied = false;
        var coordinator = new ProjectConfigurationCoordinator(repository, new IgwProject(),
            (_, _, _) => Task.CompletedTask, validate: null,
            applyDevicesRuntime: (_, _, _) => { applied = true; return Task.CompletedTask; });
        var result = await coordinator.ExecuteBulkAsync(new ProjectBulkTagRequest(), new ProjectRevision(4), default);
        Assert.True(result.Persisted.Conflict);
        Assert.False(applied);
    }
```

- [ ] **Step 6: 确认失败 → 实现 `ExecuteBulkAsync`**：

```csharp
    public async Task<ProjectBulkApplyResult> ExecuteBulkAsync(
        ProjectBulkTagRequest request,
        ProjectRevision? expected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ProjectBulkTagResult persisted;
            try
            {
                persisted = await _repository.BulkUpsertTagsAsync(request, expected, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var current = expected ?? await _repository.GetRevisionAsync(cancellationToken).ConfigureAwait(false);
                persisted = new ProjectBulkTagResult(false, false, current, null, null, 0, 0, 0, "persistence_failed", ex.Message);
            }
            if (!persisted.Applied || persisted.DeviceId is null)
                return new ProjectBulkApplyResult(persisted, false, persisted.ErrorCode, persisted.Error, "");

            IgwProject next;
            string deviceFullName;
            try
            {
                ProjectDeviceSnapshot snapshot = await _repository.LoadDeviceAsync(persisted.DeviceId, cancellationToken)
                    .ConfigureAwait(false);
                next = ProjectBulkProjection.WithDevice(HealthyProject, snapshot, out deviceFullName);
            }
            catch (Exception ex)
            {
                SetDegraded(null, ProjectApplyScope.Full, "数据库已提交，但重新读取设备失败：" + ex.Message);
                return new ProjectBulkApplyResult(persisted, false, "runtime_apply_failed", DegradedMessage, "");
            }

            return await ApplyDevicesAsync(persisted, next, [deviceFullName], cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
```

`ApplyDevicesAsync`（私有）：调用 `_applyDevicesRuntime ?? ((p, _, ct) => _applyRuntime(p, ProjectApplyScope.Full, ct))`；成功写 `_healthyProject`、清 pending/降级；失败 `_pendingDevices = devices; SetDegraded(next, ProjectApplyScope.Devices, ex.Message)`。`RetryRuntimeApplyAsync` 增加分支：`_pendingProject is not null && _pendingScope == ProjectApplyScope.Devices` 时走按设备重试。

- [ ] **Step 7: 确认通过**：`--filter "ProjectConfigurationCoordinatorTests|ProjectBulkProjectionTests"` 全 PASS。
- [ ] **Step 8: 提交** `git commit -m "feat: coordinator bulk execution with copy-on-write device projection"`

---

### Task 5: 引擎按设备应用 `ApplyDevicesAsync`

**Files:**
- Modify: `src/IGW.Core/Engine.cs`
- Test: `tests/IGW.Tests/EngineScopedApplyTests.cs`

**Interfaces:**
- Produces:
  - `RuntimeEngine.ApplyDevicesAsync(IgwProject newProject, IReadOnlyCollection<string> deviceFullNames, Func<Task>? beginApply = null) : Task<ScopedDeviceApplyResult>`
  - `record ScopedDeviceApplyResult(IReadOnlyList<string> Restarted, IReadOnlyList<string> RemovedTagIds, IReadOnlyList<string> AddedTagIds)`
  - `RuntimeEngine.UpdateDeviceMetadata(IgwProject project, IReadOnlyCollection<string> deviceFullNames)`（`UpdateProjectMetadata` 的按设备版本）

**实现要点（参照 Engine.cs:3528-3573 `ApplyCoreAsync` 与 3642-3669 `HardReconnectChannelCoreAsync`）：**
- 持 `_deviceLifecycleGate`；先 `beginApply?.Invoke()`。
- `TemplateMaps(newProject)` 只算一次；对每个 `deviceFullName` 在 `newProject` 中定位 `(ch, dev)`；找不到则跳过。
- 运行中的 `DeviceRuntime dr`（`DeviceSnapshot()` 按名称查找）：签名 `DeviceSig(ch, dev, hashes)` 等于 `dr.Sig` → 跳过；否则记录 `oldTagIds = dr.TagIds`，`await StopDeviceAsync(dr, unregisterTags: false, drainTimeout: ApplyStopGrace)`，`StartDevice(ch, dev, factory, templates, hashes, reuseHubEntries: true)`。新设备（未运行）直接 `StartDevice(..., reuseHubEntries: false)`。
- 重启后 `newTagIds = EnumDeviceTagIds(EffectiveDevice(...), fullName)`；`removed = old - new` 逐个 `_hub.Unregister(id)`；`added = new - old`。
- 调用 `UpdateDeviceMetadata(newProject, names)`，并 `_project = newProject`。
- **不得**遍历或计算其它设备的签名。
- 注意 `BindDevice(..., reuseHubEntries: true)` 对新增点必须能注册新 Hub 条目：实现者先读 Engine.cs:4245 `BindDevice`，若 `reuseHubEntries` 分支对不存在的条目不注册，则改为「存在则复用、不存在则注册」，并用测试锁住。

- [ ] **Step 1: 写失败测试**（使用 `Simulator` 驱动，参照现有引擎测试的构造方式——实现者先 `grep -l "new RuntimeEngine(" tests/IGW.Tests` 找一个现成用例照搬构造代码）

测试用例（全部必须写出完整代码）：
1. `Only_target_device_is_restarted`：工程两台 Simulator 设备 A、B 各 3 点；启动；给 A 追加 1 点并 `ApplyDevicesAsync(new, ["Ch.A"])`；断言 B 的 `DeviceRuntime` 实例（通过 `internal` 的 `DeviceSnapshot()`，Core 已对 IGW.Tests 开放 InternalsVisibleTo）与之前 `ReferenceEquals`；A 的新点 `Hub.TryGetDef("Ch.A.New", out _)` 为真。
2. `Existing_tag_handles_survive_restart`：记录 A 旧点的 Hub 句柄（`_hub` 的句柄获取 API，实现者查 `DataHub` 现有方法，如 `TryGetHandle`），应用后句柄值不变。
3. `Removed_tags_are_unregistered`：删 A 的一个点后应用，`TryGetDef` 为假，结果 `RemovedTagIds` 含该 id。
4. `Unchanged_device_is_skipped`：签名不变时 `Restarted` 为空。

- [ ] **Step 2: 确认失败 → Step 3: 实现 → Step 4: 通过**（`--filter EngineScopedApplyTests`，并回归 `--filter "FullyQualifiedName~Engine"`）
- [ ] **Step 5: 提交** `git commit -m "feat: engine applies changes to named devices only"`

---

### Task 6: 西门子地址模式守卫

**Files:**
- Create: `src/IGW.Runtime/SiemensAddressModeGuard.cs`
- Test: `tests/IGW.Tests/SiemensAddressModeGuardTests.cs`

**Interfaces:**
- Produces: `static class SiemensAddressModeGuard { string? Check(ChannelDef channel, DeviceDef device) }`——非 `Siemens.S7` 通道返回 null；遍历设备根点与所有组点，用 `SiemensAddressDescriptor.Parse(address).Kind` 分类；同时存在 Classic 与 Symbolic 时返回 `"设备 {device.Name} 同时包含 S7 绝对地址（如 {classic}）和 S7 Plus 符号地址（如 {symbolic}），请分到两台设备"`；无法解析的地址忽略（交给驱动报错）。

- [ ] **Step 1: 写失败测试**

```csharp
using IGW.Abstractions;
using IGW.Runtime;
using Xunit;

namespace IGW.Tests;

public sealed class SiemensAddressModeGuardTests
{
    private static ChannelDef Channel(string driver = "Siemens.S7") => new() { Name = "C", DriverId = driver };
    private static DeviceDef Device(params string[] addresses) => new()
    {
        Name = "D",
        Groups = [new TagGroupDef { Name = "G", Tags = [.. addresses.Select((a, i) => new TagDef { Name = "T" + i, Address = a })] }],
    };

    [Fact] public void Classic_only_passes() => Assert.Null(SiemensAddressModeGuard.Check(Channel(), Device("DB1.DBX0.0", "I0.1", "MW10")));
    [Fact] public void Symbolic_only_passes() => Assert.Null(SiemensAddressModeGuard.Check(Channel(), Device("DB490.Type.Error1", "Db.Path")));
    [Fact] public void Other_drivers_are_ignored() => Assert.Null(SiemensAddressModeGuard.Check(Channel("Modbus.Tcp"), Device("DB1.DBX0.0", "Db.Path")));

    [Fact]
    public void Mixed_is_rejected_with_examples()
    {
        string? error = SiemensAddressModeGuard.Check(Channel(), Device("DB1.DBX0.0", "Db.Path"));
        Assert.NotNull(error);
        Assert.Contains("DB1.DBX0.0", error);
        Assert.Contains("Db.Path", error);
    }
}
```

- [ ] **Step 2–4: 失败 → 实现 → 通过**（`--filter SiemensAddressModeGuardTests`）
- [ ] **Step 5: 提交** `git commit -m "feat: reject mixed S7 classic and symbolic addresses per device"`

---

### Task 7: OPC UA 设备子树替换

**Files:**
- Modify: `src/IGW.Northbound.OpcUaServer/IgwNodeManager.cs`、`src/IGW.Northbound.OpcUaServer/OpcUaServerHost.cs`
- Test: `tests/IGW.Tests/OpcUaDeviceSubtreeTests.cs`

**Interfaces:**
- Produces: `IgwNodeManager.ReplaceDeviceSubtree(ChannelDef channel, DeviceDef device)`；`OpcUaServerHost.ReplaceDevice(ChannelDef channel, DeviceDef device)`（服务未运行时 no-op）。

**实现要点：**
- `IgwNodeManager` 目前把 `_project` 固定在构造时；新增字段 `Dictionary<string, FolderState> _channelFolders`、`_deviceFolders`（在 `CreateAddressSpace` 建通道/设备文件夹时登记）。
- `ReplaceDeviceSubtree` 在 `lock (Lock)` 内：若设备文件夹存在，收集其后代变量 NodeId，逐个 `DeleteNode(SystemContext, nodeId)`（`CustomNodeManager2` 自带），从 `_vars/_defs` 移除以 `devPath + "."` 开头的键，最后删除设备文件夹本身；通道文件夹不存在则新建并挂到 ObjectsFolder（参照 `CreateAddressSpace` 里的 `AddReference` + `AddExternalReference(ObjectIds.ObjectsFolder, …)`——运行期新增顶层节点需用 `AddExternalReference`，实现者查 SDK 的 `CustomNodeManager2.AddExternalReference`）；随后用现有 `CreateFolder/CreateTag/BuildGroup` 重建设备子树并 `AddPredefinedNode(SystemContext, deviceFolder)`；新建变量需像启动时一样挂好 `OnSimpleReadValue` 等回调（`CreateTag` 已做）。
- 被删节点上的监控项由 SDK 在 `DeleteNode` 时通知为 `BadNodeIdUnknown`，无需额外处理。

- [ ] **Step 1: 写失败测试**：参照现有 OPC UA 测试（`grep -l "IgwNodeManager\|OpcUaServerHost" tests/IGW.Tests` 找启动服务端的现成代码）启动服务，工程含通道 C、设备 A（点 x）、设备 B（点 y）；调用 `ReplaceDevice(C, A')`（A' 有点 x、z）；用客户端 Browse 断言 `C.A` 下有 x、z，`C.B.y` 仍可读；对 `C.B.y` 建立的订阅在替换前后都能收到值（不被中断）。再测 `ReplaceDevice(新通道 N, 设备 D)` 后 `N.D` 可浏览。
- [ ] **Step 2–4: 失败 → 实现 → 通过**，回归 `--filter "FullyQualifiedName~OpcUa"`
- [ ] **Step 5: 提交** `git commit -m "feat: replace a single device subtree in the OPC UA address space"`

---

### Task 8: 实时流删除点判定 + 北向回退策略

**Files:**
- Modify: `src/IGW.Core/RealtimeStreamCoordinator.cs`
- Create: `src/IGW.Runtime/ScopedApplyPolicy.cs`
- Test: `tests/IGW.Tests/RealtimeStreamCoordinatorTests.cs`（追加）、`tests/IGW.Tests/ScopedApplyPolicyTests.cs`

**Interfaces:**
- Produces:
  - `RealtimeStreamCoordinator.HasRoutesForTags(IEnumerable<string> tagIds) : bool`——`lock (_gate)` 内检查 `_routes` 中是否有任一 route 指向这些点（`_routes` 的键是句柄，实现者按其值类型取点位 id；若值不含 id，则由调用方传入句柄：改签名为 `HasRoutesForHandles(IEnumerable<int>)` 并在 GatewayHost 侧用 `Hub` 把 id 换成句柄——二选一，以真实数据结构为准，测试同步调整）。
  - `static class ScopedApplyPolicy { bool RequiresFullApply(IgwProject project, DeviceDef? oldDevice, DeviceDef newDevice, string deviceFullName) }`：以下任一为真返回 true：
    1. 新或旧设备中任一点 `Alarm is not null`；
    2. `ProjectStore.ToJson` 序列化以下各段后包含 `deviceFullName + "."`（Ordinal）：`project.History`、`project.ComputedTags`、`project.MesUpload.Tasks`、`project.MqttPublish`、`project.Schedules`（这些段体量小，不含全量点位）；
    3. 设备 `TemplateRef` 非空（模板实例走原有路径）。

- [ ] **Step 1: 写失败测试**

```csharp
using IGW.Abstractions;
using IGW.Runtime;
using Xunit;

namespace IGW.Tests;

public sealed class ScopedApplyPolicyTests
{
    private static DeviceDef Plain() => new() { Name = "D", Tags = [new TagDef { Name = "T", Address = "DB1.X" }] };

    [Fact] public void Plain_device_with_unrelated_project_does_not_need_full_apply() =>
        Assert.False(ScopedApplyPolicy.RequiresFullApply(new IgwProject(), null, Plain(), "C.D"));

    [Fact]
    public void Alarmed_tag_needs_full_apply()
    {
        var device = Plain();
        device.Tags[0].Alarm = new AlarmDef();
        Assert.True(ScopedApplyPolicy.RequiresFullApply(new IgwProject(), null, device, "C.D"));
    }

    [Fact]
    public void Mes_reference_to_device_needs_full_apply()
    {
        var project = new IgwProject();
        project.MesUpload.Tasks.Add(new MesTask { Id = "m", Name = "m", DeviceRef = "C.D.T" });
        Assert.True(ScopedApplyPolicy.RequiresFullApply(project, null, Plain(), "C.D"));
    }

    [Fact]
    public void Template_instance_needs_full_apply()
    {
        var device = Plain();
        device.TemplateRef = "tpl";
        Assert.True(ScopedApplyPolicy.RequiresFullApply(new IgwProject(), null, device, "C.D"));
    }
}
```

（`AlarmDef`/`MesTask` 的必填属性以实际模型为准；若构造需要更多字段，在测试里补齐。）

`RealtimeStreamCoordinatorTests` 追加：打开一个订阅点 `C.D.T` 的流后 `HasRoutesForTags(["C.D.T"])` 为真、`["C.D.X"]` 为假；无流时为假（照搬该文件现有打开流的写法）。

- [ ] **Step 2–4: 失败 → 实现 → 通过**（`--filter "ScopedApplyPolicyTests|RealtimeStreamCoordinatorTests"`）
- [ ] **Step 5: 提交** `git commit -m "feat: scoped apply policy and realtime route lookup for removed tags"`

---

### Task 9: GatewayHost 按设备运行态应用 + `ApplyBulkTagsAsync`

**Files:**
- Modify: `src/IGW.Runtime/GatewayHost.cs`、`src/IGW.Core/IGatewayHost.cs`
- Test: `tests/IGW.Tests/GatewayHostBulkTagTests.cs`

**Interfaces:**
- Consumes: Task 4（协调器构造参数 `applyDevicesRuntime`、`ExecuteBulkAsync`）、Task 5、6、7、8。
- Produces:
  - `IGatewayHost.ApplyBulkTagsAsync(ProjectBulkTagRequest request, string? expectedRevision, CancellationToken ct) : Task<ProjectBulkTagResponse>`
  - `record ProjectBulkTagResponse(bool Applied, bool Conflict, string Revision, string? ChannelId, string? DeviceId, int Created, int Updated, int Deleted, bool DeviceRestarted, string? ErrorCode, string? Error, string Timing)`（放 `IGW.Core`）。

**实现要点：**
1. 构造协调器处传入 `applyDevicesRuntime: ApplyScopedRuntimeAsync`。
2. `ApplyBulkTagsAsync`：
   - `expectedRevision` 非空时解析为 `ProjectRevision`，不等于 `CurrentStorageRevision` → 直接冲突返回；
   - **协议混用预检**：在调用协调器前，从 `Project`（内存健康工程）按请求的通道名/设备名找现有设备，构造「现有点 + 请求点（按组路径和点名覆盖）- replaceGroups 删除的组」的候选 `DeviceDef`，调用 `SiemensAddressModeGuard.Check`，不为 null → 返回 `ErrorCode="mixed_siemens_address_mode"`，数据库不动；
   - 调用 `_configuration.ExecuteBulkAsync`，`UpdateStorageRevision`，用分段计时（沿用 `_applySteps` / `PublishApplyTiming`）生成 `Timing`。
3. `ApplyScopedRuntimeAsync(IgwProject next, IReadOnlyList<string> devices, CancellationToken ct)`，在 `_gate` 内：
   - `Hub is null || Engine is null` 或授权阻断 → `Project = next` 后返回（与 `ApplyFullRuntimeCoreAsync` 开头一致）；
   - 对每个设备：定位新旧 `DeviceDef`；`ScopedApplyPolicy.RequiresFullApply` 为真 → 整体改走 `ApplyFullRuntimeAsync(next)` 并返回；
   - `var result = await Engine.ApplyDevicesAsync(next, devices)`（**不设置 `Reloading`**）；
   - `_opcua?.ReplaceDevice(ch, dev)`；
   - `RealtimeStreams?.HasRoutesForTags(result.RemovedTagIds)` 为真 → `await RetireRealtimeStreamsAsync(RealtimeProjectRevision.Compute(next))` 后 `StartRealtimeStreams()`；否则不动 `_realtimeRevision`；
   - `Project = next`。
4. 失败时抛出（协调器进入降级）；**不调用** `RestoreHealthyRuntimeAsync`（按设备应用失败只影响该设备，重试即可）。

- [ ] **Step 1: 写失败测试**（参照现有 `GatewayHost` 集成测试的启动方式——`grep -l "new GatewayHost(" tests/IGW.Tests` 找现成 fixture；用 Simulator 驱动与临时数据目录）

用例（完整代码由实现者按现成 fixture 写出，断言如下）：
1. `Bulk_tags_create_channel_device_and_are_readable`：空工程启动 → `ApplyBulkTagsAsync`（Simulator 通道，2 组 10 点）→ `Applied`、`Created=10`；`Hub.TryGetDef("PLC1.Sim.G1.T0", out _)` 为真；`Reloading` 全程为假（在另一个任务里每 5ms 采样 `host.Reloading`，断言从未为真）。
2. `Other_devices_keep_running`：工程已有设备 X 运行；对新设备注册 → X 的 `DeviceRuntime` 引用不变。
3. `Mixed_siemens_addresses_are_rejected_before_persisting`：Siemens.S7 通道已有 Classic 点，请求加符号点 → `ErrorCode=="mixed_siemens_address_mode"`，修订号不变。
4. `Alarmed_device_falls_back_to_full_apply`：请求点带 `Alarm`（通过扩展 `ProjectBulkTagItem`？——不扩展；改为预置工程让 MES 引用该设备）→ 结果成功且 `LastApplyTiming` 含「重建 PLC 采集」（全量路径的步骤名）。
5. `Stale_if_match_is_a_conflict`。

- [ ] **Step 2–4: 失败 → 实现 → 通过**，回归 `--filter "FullyQualifiedName~GatewayHost"`
- [ ] **Step 5: 提交** `git commit -m "feat: gateway host applies bulk tag registration to one device"`

---

### Task 10: DCS 别名解析器后台重建

**Files:**
- Modify: `src/IGW.Northbound.RestApi/DcsTagOperations.cs`（`Resolver()`，约 :214-229）
- Test: `tests/IGW.Tests/DcsTagOperationsTests.cs`（追加）

**Interfaces:**
- Produces: 行为变化——工程对象变化后，第一次读取发现过期时**启动**后台构建并**立即返回旧解析器**（若存在）；构建完成后原子替换。首次（无旧解析器）仍同步构建。

**实现要点：**

```csharp
    private Task? _resolverBuild;

    private DcsTagAliasResolver Resolver()
    {
        var project = _runtime.Project;
        var resolver = Volatile.Read(ref _resolver);
        if (resolver is not null && ReferenceEquals(Volatile.Read(ref _resolverProject), project)) return resolver;
        lock (_resolverLock)
        {
            if (_resolver is null)
            {
                _resolver = new DcsTagAliasResolver(project, tagId => _runtime.TryGetDef(tagId, out _));
                _resolverProject = project;
                return _resolver;
            }
            if (!ReferenceEquals(_resolverProject, project) && (_resolverBuild is null || _resolverBuild.IsCompleted))
            {
                _resolverBuild = Task.Run(() =>
                {
                    var built = new DcsTagAliasResolver(project, tagId => _runtime.TryGetDef(tagId, out _));
                    lock (_resolverLock)
                    {
                        _resolver = built;
                        _resolverProject = project;
                    }
                });
            }
            return _resolver;
        }
    }
```

（`_runtime.Project` 的实际成员名以文件现状为准；`_resolverProject` 读写都在锁内或用 `Volatile`。）

- [ ] **Step 1: 写失败测试**：构造 `DcsTagOperations`（照搬 `DcsTagOperationsTests` 现有 fixture），先读一次建立解析器；替换运行时工程对象为新增了设备 N 的工程；立即读取旧点位——成功且耗时 < 50ms（不等待重建）；轮询最多 2 秒后读取 N 的新点成功。
- [ ] **Step 2–4: 失败 → 实现 → 通过**（`--filter "DcsTag"` 全部通过）
- [ ] **Step 5: 提交** `git commit -m "perf: rebuild DCS alias resolver in background without blocking reads"`

---

### Task 11: REST 端点 `POST /api/v1/project/bulk/tags`

**Files:**
- Create: `src/IGW.Northbound.RestApi/ProjectBulkTagApi.cs`
- Modify: 映射 `MapProjectConfigurationApi` 的位置（`grep -rn "MapProjectConfigurationApi(" src`），在其后追加 `app.MapProjectBulkTagApi(host);`
- Test: `tests/IGW.Tests/ProjectBulkTagApiTests.cs`

**Interfaces:**
- Consumes: Task 1、Task 9。
- Produces: HTTP 契约——
  - 200：`{applied, revision, channelId, deviceId, created, updated, deleted, deviceRestarted, timing}`，响应头 `ETag`；
  - 400：`{error:"invalid_items", details:[{index, reason}]}` / `{error:"invalid_request", detail}` / `{error:"mixed_siemens_address_mode"|"channel_driver_mismatch"|"persistence_failed", detail}`；
  - 409：`{error:"revision_conflict", revision}`；
  - 500：`{error:"runtime_apply_failed", detail, revision}`；
  - `If-Match` 可选，格式错误 400 `invalid_if_match`。

```csharp
using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace IGW.Northbound.RestApi;

/// <summary>点位批量注册：一次请求写一台设备，行级批量落库，只重建该设备。</summary>
public static class ProjectBulkTagApi
{
    public const long MaximumRequestBytes = 512L * 1024 * 1024;

    public static void MapProjectBulkTagApi(this IEndpointRouteBuilder app, IGatewayHost host)
    {
        app.MapPost("/api/v1/project/bulk/tags", async (HttpRequest request, HttpResponse response, CancellationToken ct) =>
        {
            if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = MaximumRequestBytes;

            string? expected = request.Headers.IfMatch.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(expected) && !ProjectRevision.TryParse(expected, out _))
                return Results.BadRequest(new { error = "invalid_if_match" });

            ProjectBulkTagRequest? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<ProjectBulkTagRequest>(request.Body, ProjectStore.Options, ct)
                    .ConfigureAwait(false);
            }
            catch (JsonException ex) { return Results.BadRequest(new { error = "invalid_request", detail = ex.Message }); }
            if (body is null) return Results.BadRequest(new { error = "invalid_request", detail = "请求体为空" });

            var errors = ProjectBulkTagValidator.Validate(body);
            if (errors.Count > 0)
                return Results.BadRequest(new { error = "invalid_items", details = errors });

            ProjectBulkTagResponse result;
            try
            {
                result = await host.ApplyBulkTagsAsync(body, string.IsNullOrWhiteSpace(expected) ? null : expected, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { return Results.Json(new { error = "bulk_failed", detail = ex.Message }, statusCode: 500); }

            response.Headers.ETag = result.Revision;
            if (result.Conflict)
                return Results.Json(new { error = "revision_conflict", revision = result.Revision }, statusCode: 409);
            if (result.ErrorCode == "runtime_apply_failed")
                return Results.Json(new { error = result.ErrorCode, detail = result.Error, revision = result.Revision }, statusCode: 500);
            if (!result.Applied)
                return Results.BadRequest(new { error = result.ErrorCode ?? "bulk_rejected", detail = result.Error });
            return Results.Ok(new
            {
                applied = true, revision = result.Revision, channelId = result.ChannelId, deviceId = result.DeviceId,
                created = result.Created, updated = result.Updated, deleted = result.Deleted,
                deviceRestarted = result.DeviceRestarted, timing = result.Timing,
            });
        });
    }
}
```

> 设计写的是「流式解析 items」。`JsonSerializer.DeserializeAsync` 已是流式读取请求体（不整体缓冲字符串），对象图仍在内存——10 万项约数十 MB，可接受；若基准显示内存超标，再改为 `Utf8JsonReader` 手写逐项解析（Task 12 基准决定）。

- [ ] **Step 1: 写失败测试**：用 `WebApplicationFactory` 或现有 API 测试手法（`grep -l "MapProjectConfigurationApi" tests/IGW.Tests` 照搬）+ 伪造 `IGatewayHost`（记录收到的请求并返回预置结果），覆盖：200 正常 + ETag；400 `invalid_items`（带 index）；400 `invalid_if_match`；409；500 `runtime_apply_failed`；枚举用 camelCase 字符串（`"dataType":"bool","access":"readWrite"`）能正确反序列化。
- [ ] **Step 2–4: 失败 → 实现 → 通过**（`--filter ProjectBulkTagApiTests`）
- [ ] **Step 5: 提交** `git commit -m "feat: POST /api/v1/project/bulk/tags endpoint"`

---

### Task 12: 百万点基准、全量回归与发布说明

**Files:**
- Modify: `benchmarks/IGW.ProjectStorage.Performance/`（新增批量场景，照现有百万点场景的写法）
- Create: `docs/release-notes-bulk-tag-upsert.md`（发布时再并入正式版本号的 release notes）

- [ ] **Step 1: 基准场景**（仓储层 + 协调器层，不起真实 PLC）：
  1. 空工程单请求 10 万点；
  2. 预置 100 台设备 × 1 万点（100 万点）后，对新设备单请求 10 万点；
  3. 100 万点分 10 次请求写 10 台设备，记录总耗时与峰值工作集（`Process.PeakWorkingSet64`）；
  4. 场景 3 期间另一线程持续对已有设备调用 `DcsTagOperations` 读取，统计失败/503 次数。
- [ ] **Step 2: 运行基准**，把结果贴进 release notes；任一指标未达 Global Constraints 的性能目标 → 停止，回报数据与瓶颈分析，不要自行放宽目标。
- [ ] **Step 3: 全量回归**：`dotnet test tests/IGW.Tests/IGW.Tests.csproj -c Release`，Expected: 失败 0，通过数 = 1799 + 新增。
- [ ] **Step 4: 发布说明**：写明新接口契约、名称规则、协议混用校验、按设备应用范围与回退条件、基准数据。
- [ ] **Step 5: 提交** `git commit -m "perf: million-point bulk registration benchmark and release notes"`

---

## Self-Review 记录

- Spec 覆盖：§3 接口→T1/T11；§4.1→T2；§4.2→T3；§4.3→T4；§5.1→T5；§5.2→T7；§5.3→T8/T9；§5.4→T8/T9；§6 错误处理→T2/T4/T9/T11；§7 测试与基准→各任务 + T12；协议混用→T6/T9；DCS 别名解析→T10。
- 类型一致性：`ProjectBulkTagRequest/Result`、`ProjectDeviceSnapshot`、`ProjectBulkApplyResult`、`ProjectBulkTagResponse`、`ScopedDeviceApplyResult` 在定义任务与使用任务中签名一致。
- 需实现者按现状确认的点（均已在对应任务写明判定方法）：修订号初值（T2）、`BindDevice` 复用分支（T5）、Hub 句柄 API（T5）、`_routes` 值结构（T8）、OPC UA 运行期新增顶层节点 API（T7）、各现成测试 fixture 的位置（T5/T7/T9/T10/T11）。
