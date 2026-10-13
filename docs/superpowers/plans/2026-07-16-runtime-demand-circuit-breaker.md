# Runtime Demand Connection and Circuit Breaker Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Runtime 仅在存在真实数据需求时连接 PLC，并在连续通信失败 3 次后熔断 30 秒，消除离线设备造成的 CPU 与日志风暴。

**Architecture:** `DataHub` 按“通道.设备”聚合临时访问、持续钉住、点订阅和全量订阅需求，设备循环通过 O(1) 查询决定是否连接。`DeviceLoop` 增加空闲断开和 Closed/Open/HalfOpen 熔断状态机；MES 在生命周期内钉住启用任务引用的点，浏览操作使用临时设备级需求。

**Tech Stack:** .NET 10 / C#、xUnit、现有 `DataHub`/`RuntimeEngine`/`MesUploadEngine`。

## Global Constraints

- 连续通信失败阈值固定为 3 次，熔断窗口固定为 30 秒。
- 半开状态只允许一次连接和有效读取；失败立即重新熔断 30 秒。
- 无数据需求时不得调用驱动 `ConnectAsync` 或 `ReadAsync`。
- 正常设备的扫描周期和 `all`/`active` 扫描模式语义不变。
- MES、OPC UA、Quick Client/REST、计划、告警/计算、历史和 MQTT 都必须能产生数据需求。
- 不提交或覆盖工作区中与本修复无关的既有修改。

---

### Task 1: DataHub 设备级需求聚合

**Files:**
- Modify: `src/IGW.Core/DataHub.cs`
- Test: `tests/IGW.Tests/DataplaneTests.cs`

**Interfaces:**
- Produces: `public bool HasDeviceDemand(string deviceName, int windowMs)`。
- Produces: 点注册、`Pin/Unpin`、`Touch` 和全量订阅对设备需求计数的统一维护。

- [ ] **Step 1: 写入失败测试**

在 `DataplaneTests.cs` 增加四个测试：

```csharp
[Fact]
public async Task 设备需求_无消费者时为否_Touch后在窗口内为真()
{
    await using var hub = new DataHub(shards: 1);
    hub.Register("C1.D1.T1", Def("T1"));
    Assert.False(hub.HasDeviceDemand("C1.D1", 30_000));
    hub.Touch("C1.D1.T1");
    Assert.True(hub.HasDeviceDemand("C1.D1", 30_000));
    Assert.False(hub.HasDeviceDemand("C1.D2", 30_000));
}

[Fact]
public async Task 设备需求_Pin和点订阅在释放前持续有效()
{
    await using var hub = new DataHub(shards: 1);
    hub.Register("C1.D1.T1", Def("T1"));
    hub.Pin("C1.D1.T1");
    Assert.True(hub.HasDeviceDemand("C1.D1", 1));
    hub.Unpin("C1.D1.T1");
    Assert.False(hub.HasDeviceDemand("C1.D1", 1));

    using var sub = hub.SubscribePoints(_ => ValueTask.CompletedTask, "alarm");
    sub.Add("C1.D1.T1");
    Assert.True(hub.HasDeviceDemand("C1.D1", 1));
    sub.Remove("C1.D1.T1");
    Assert.False(hub.HasDeviceDemand("C1.D1", 1));
}

[Fact]
public async Task 设备需求_全量订阅对全部设备有效并在释放后消失()
{
    await using var hub = new DataHub(shards: 1);
    hub.Register("C1.D1.T1", Def("T1"));
    var all = hub.SubscribeAll(_ => ValueTask.CompletedTask, "history");
    Assert.True(hub.HasDeviceDemand("C1.D1", 1));
    all.Dispose();
    Assert.False(hub.HasDeviceDemand("C1.D1", 1));
}
```

- [ ] **Step 2: 运行测试并确认失败**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~设备需求" --no-restore`

Expected: 编译失败，提示 `DataHub` 不包含 `HasDeviceDemand`。

- [ ] **Step 3: 实现 O(1) 设备需求状态**

在 `DataHub.Entry` 保存解析后的 `DeviceName`，增加设备状态字典：

```csharp
private sealed class DeviceDemand
{
    public long LastTouchTick;
    public int Pins;
    public int PointSubscriptions;
}

private readonly ConcurrentDictionary<string, DeviceDemand> _deviceDemand = new(StringComparer.OrdinalIgnoreCase);
private int _wildcardDemand;
```

注册点位时从 TagId 前两段生成设备名。`Touch` 刷新 `LastTouchTick`；`Pin/Unpin` 与点订阅首次 Add/最终 Remove 更新对应计数；全量订阅创建/释放更新 `_wildcardDemand`。实现：

```csharp
public bool HasDeviceDemand(string deviceName, int windowMs)
{
    if (Volatile.Read(ref _wildcardDemand) > 0) return true;
    if (!_deviceDemand.TryGetValue(deviceName, out var d)) return false;
    if (Volatile.Read(ref d.Pins) > 0 || Volatile.Read(ref d.PointSubscriptions) > 0) return true;
    long last = Volatile.Read(ref d.LastTouchTick);
    return last != 0 && Environment.TickCount64 - last <= Math.Max(1, windowMs);
}
```

- [ ] **Step 4: 运行 DataHub 测试**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "DataplaneTests|ConsumerEquivalenceTests|设备需求" --no-restore`

Expected: 全部通过。

### Task 2: DeviceLoop 无需求不连接

**Files:**
- Modify: `src/IGW.Core/Engine.cs`
- Create: `tests/IGW.Tests/RuntimeDemandTests.cs`

**Interfaces:**
- Consumes: `DataHub.HasDeviceDemand(string, int)`。
- Produces: `DeviceLoop.AcquireDemand()`、`DeviceLoop.WaitUntilConnectedAsync(CancellationToken)`。

- [ ] **Step 1: 写入无需求与出现需求测试**

创建可计数的 `DemandTestDriver : IDeviceDriver`，连接只更新状态，读取返回 Good 样本。增加：

```csharp
[Fact]
public async Task 无数据需求时不连接_Touch后自动连接读取()
{
    await using var hub = HubWithOneTag(out var binding);
    await using var driver = new DemandTestDriver();
    using var cts = new CancellationTokenSource();
    var loop = NewLoop(driver, binding, hub, idlePollMs: 10);
    loop.Start(cts.Token);

    await Task.Delay(80);
    Assert.Equal(0, driver.ConnectCount);
    Assert.Equal(0, driver.ReadCount);

    hub.Touch(binding.TagId);
    await Wait.Until(() => driver.ReadCount > 0);
    cts.Cancel();
    await loop.Completion;
}
```

- [ ] **Step 2: 运行测试并确认失败**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter RuntimeDemandTests --no-restore`

Expected: 失败，因为当前循环启动即连接。

- [ ] **Step 3: 实现需求门控**

给 `DeviceLoop` 注入设备名和需求查询：

```csharp
private bool HasDemand() => !_writes.IsEmpty || Volatile.Read(ref _explicitDemand) > 0 ||
    _hub.HasDeviceDemand(_name, _activeWindowMs);
```

循环最前面在无需求时断开已连接驱动、标坏质量并低频等待；有需求后才进入现有连接/扫描代码。`RuntimeEngine.StartDevice` 传入设备全名，默认空闲检查间隔 250ms，测试可注入 10ms。

- [ ] **Step 4: 增加临时设备级需求**

实现 `AcquireDemand()` 返回一次性释放句柄，并实现等待连接方法。`RuntimeEngine.BrowseAsync` 在浏览前获取临时需求并等待设备连接，浏览完成释放。

- [ ] **Step 5: 运行 RuntimeDemandTests**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter RuntimeDemandTests --no-restore`

Expected: 无需求测试与 Touch 激活测试通过。

### Task 3: 读取失败熔断状态机

**Files:**
- Modify: `src/IGW.Core/Engine.cs`
- Modify: `tests/IGW.Tests/RuntimeDemandTests.cs`

**Interfaces:**
- Produces: 设备级 `Closed/Open/HalfOpen` 状态机；生产默认阈值 3、窗口 30000ms。

- [ ] **Step 1: 写入快速读取失败回归测试**

让测试驱动 `ReadAsync` 立即抛出异常，测试使用 3 次阈值、200ms 熔断窗口：

```csharp
[Fact]
public async Task 快速读取失败三次后熔断_窗口内不再连接或读取()
{
    await using var hub = HubWithOneTag(out var binding);
    hub.Pin(binding.TagId);
    await using var driver = new DemandTestDriver { FailReads = true };
    using var cts = new CancellationTokenSource();
    var loop = NewLoop(driver, binding, hub, idlePollMs: 5, circuitOpenMs: 200);
    loop.Start(cts.Token);

    await Wait.Until(() => driver.ReadCount == 3);
    int connects = driver.ConnectCount;
    await Task.Delay(100);
    Assert.Equal(3, driver.ReadCount);
    Assert.Equal(connects, driver.ConnectCount);

    cts.Cancel();
    await loop.Completion;
}
```

再增加半开失败重新熔断、半开成功恢复两个测试。

- [ ] **Step 2: 运行测试并确认失败**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter RuntimeDemandTests --no-restore`

Expected: 读取次数在 100ms 内超过 3，证明重连风暴被测试捕获。

- [ ] **Step 3: 实现熔断状态机**

增加状态和参数：

```csharp
private enum CircuitState { Closed, Open, HalfOpen }
private CircuitState _circuit;
private int _communicationFailures;
private long _circuitOpenUntil;
```

连接或读取失败统一进入 `RecordCommunicationFailureAsync`。Closed 连续第 3 次失败进入 Open；HalfOpen 任一失败立即回到 Open。Open 到期且存在需求时进入 HalfOpen。只有一次有效读取成功才调用 `RecordCommunicationSuccess` 关闭熔断和清零计数。

- [ ] **Step 4: 将日志改为状态变化输出**

进入 Open 输出一次 Warning；进入 HalfOpen 输出一次 Information；恢复输出一次 Information。熔断期间不输出重复读取失败日志。

- [ ] **Step 5: 运行熔断测试**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter RuntimeDemandTests --no-restore`

Expected: 所有需求与熔断测试通过。

### Task 4: MES 与其他消费者接入需求模型

**Files:**
- Modify: `src/IGW.Northbound.Mes/MesUploadEngine.cs`
- Modify: `tests/IGW.Tests/MesRangePayloadTests.cs`
- Modify: `tests/IGW.Tests/DataplaneTests.cs`

**Interfaces:**
- Consumes: `IDataHub.Pin(string)` / `Unpin(string)` 与 DataHub 点/全量订阅需求计数。
- Produces: 启用 MES 任务生命周期内的点位需求租约。

- [ ] **Step 1: 写入 MES 需求测试**

创建包含启用任务、条件点和跨 PLC 数据范围的工程目录，构造 `MesUploadEngine` 后断言对应设备存在需求；Dispose 后需求消失。

- [ ] **Step 2: 运行测试并确认失败**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "MesDemand|设备需求" --no-restore`

Expected: 失败，因为 MES 当前只读取缓存，不声明需求。

- [ ] **Step 3: 实现任务引用点钉住**

启动时收集启用任务的 Trigger、条件、字段、结构体、数据范围、分流、回写复制源和心跳引用；用工程目录/Hub 点表把组或设备前缀展开为真实 TagId，去重后逐点 `Pin`。Dispose 时逐点 `Unpin`。

- [ ] **Step 4: 验证其他消费者**

扩展 DataHub 测试，确认告警/计算的点订阅、历史/MQTT 的全量订阅、OPC UA Pin 和 REST Touch 都通过同一设备需求查询生效。

- [ ] **Step 5: 运行相关测试**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "MesDemand|DataplaneTests|RuntimeDemandTests" --no-restore`

Expected: 全部通过。

### Task 5: 完整验证与 CPU 复测

**Files:**
- Verify only.

**Interfaces:**
- Consumes: Tasks 1-4 的完整实现。
- Produces: 测试结果、重试频率与 CPU 对比证据。

- [ ] **Step 1: 运行完整后端测试**

Run: `dotnet test IGW.slnx --no-restore`

Expected: 0 failed。

- [ ] **Step 2: 运行前端回归**

Run from `src/IGW.WebUi`: `$tests=(Get-ChildItem -Path src -Recurse -Filter *.test.cjs).FullName; node --test $tests; npm run typecheck; npm run build`

Expected: 全部命令 exit 0。

- [ ] **Step 3: 构建独立 Runtime 并使用离线设备工程复测**

用独立输出目录启动 Runtime，避免覆盖已安装服务；使用不同 REST/OPC UA 端口和当前离线设备工程。连续采样 15 秒：

```powershell
Get-Counter '\Process(IGW.Runtime)\% Processor Time' -SampleInterval 1 -MaxSamples 15
```

Expected: 每台离线设备最多快速尝试 3 次，随后每 30 秒最多一次半开探测；CPU 不再持续占用多个核心，日志不再每秒产生大量相同错误。

- [ ] **Step 4: 检查变更范围**

Run: `git diff --check -- src/IGW.Core/DataHub.cs src/IGW.Core/Engine.cs src/IGW.Northbound.Mes/MesUploadEngine.cs tests/IGW.Tests/DataplaneTests.cs tests/IGW.Tests/RuntimeDemandTests.cs tests/IGW.Tests/MesRangePayloadTests.cs`

Expected: exit 0。
