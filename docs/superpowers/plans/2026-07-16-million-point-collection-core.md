# Million-Point Arbitrary-Cycle Collection Core Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the first independently testable delivery of the million-point design: arbitrary millisecond scan demand, per-device deadline scheduling without backlog or drift, MES demand sharing, batch DataHub updates, and a repeatable one-million-point benchmark.

**Architecture:** MES and other consumers register immutable scan-demand leases. Each device compiles those demands and tag defaults into address blocks, then one per-device scheduler runs blocks by monotonic deadlines while coalescing missed periods. Drivers still use the existing `ReadAsync` compatibility API in this phase; protocol-native precompiled reads, million-point project pagination, and hardware certification are separate follow-up plans.

**Tech Stack:** .NET 10 / C#, xUnit 2.9, existing `IGW.Abstractions`, `IGW.Core`, `IGW.Northbound.Mes`, REST minimal APIs, Vue UI unchanged in this phase.

## Global Constraints

- Preserve all unrelated changes in the current dirty worktree; never reset, restore, or overwrite user work.
- User configuration exposes only an arbitrary positive integer collection interval in milliseconds; never expose internal lanes, priorities, block sizes, thread counts, or connection counts.
- Target workload is 1,000,000 in-memory points at 200 ms plus a small subset of hundreds of points per task at 10 ms.
- No full-rate history persistence is added. The collection hot path stores current values only.
- Different devices execute independently. One offline, slow, or circuit-open device cannot delay another device.
- An overrun never queues every missed period. Keep only the newest due request, retain the original phase, and report the miss.
- An enabled MES task requests its referenced points at `MesTask.CollectIntervalMs`; multiple consumers of one point use the shortest active interval.
- No data consumer means the existing no-connect behavior remains in force.
- Cross-PLC MES payload behavior remains: an offline source produces `null`/bad quality while other sources continue.
- No per-point permanent `Task`, `Timer`, thread, string parsing, `ListTagIds()` call, LINQ grouping, or project-tree traversal is allowed in the scan hot path.
- No new NuGet package is required for this phase.
- Task commit commands are checkpoints only; because the worktree is dirty, execute them only after explicit user authorization.

## Delivery Split

This plan intentionally covers only the collection-core subproject. After it passes, create separate implementation plans for:

1. S7, Omron EtherNet/IP, and OPC UA protocol-native compiled read plans.
2. Million-point project storage, paginated REST APIs, incremental autosave, and virtualized UI.
3. Synthetic and real-hardware certification, capacity reporting, and installer release.

The core delivered here remains usable with every existing driver through `IDeviceDriver.ReadAsync`.

## File Map

- Create `src/IGW.Core/MonotonicClock.cs`: production and testable monotonic clocks.
- Create `src/IGW.Core/ScanDeadlineQueue.cs`: arbitrary-period deadline heap and overrun coalescing.
- Create `src/IGW.Core/ScanDemandRegistry.cs`: immutable per-device demand leases and versions.
- Create `src/IGW.Core/DeviceScanPlan.cs`: demand/address compiler and compiled block model.
- Create `src/IGW.Core/DeviceTimingMetrics.cs`: allocation-free period/late counters and snapshots.
- Modify `src/IGW.Core/Engine.cs`: replace `ScanGroup` polling with compiled blocks and deadline queue.
- Modify `src/IGW.Core/DataHub.cs`: subscription-aware batch update path.
- Modify `src/IGW.Northbound.Mes/MesUploadEngine.cs`: register one demand lease per enabled MES task.
- Modify `src/IGW.Runtime/GatewayHost.cs`: construct one registry and share it with Runtime/MES.
- Modify `src/IGW.Northbound.RestApi/IgwRestApi.cs`: expose actual interval and overrun diagnostics.
- Create `tests/IGW.Tests/ScanDeadlineQueueTests.cs`.
- Create `tests/IGW.Tests/ScanDemandRegistryTests.cs`.
- Create `tests/IGW.Tests/DeviceScanPlanTests.cs`.
- Create `tests/IGW.Tests/DataHubBatchTests.cs`.
- Modify `tests/IGW.Tests/RuntimeDemandTests.cs`.
- Modify `tests/IGW.Tests/MesRangePayloadTests.cs`.
- Modify `tests/IGW.Tests/ObservabilityTests.cs`.
- Create `tests/IGW.Performance/IGW.Performance.csproj` and `tests/IGW.Performance/Program.cs`.
- Modify `src/IGW.Core/IGW.Core.csproj` and `IGW.slnx` for the performance harness.

---

### Task 1: Monotonic Clock and Deadline Queue

**Files:**
- Create: `src/IGW.Core/MonotonicClock.cs`
- Create: `src/IGW.Core/ScanDeadlineQueue.cs`
- Create: `tests/IGW.Tests/ScanDeadlineQueueTests.cs`

**Interfaces:**
- Produces: `IMonotonicClock.NowMilliseconds`, `SystemMonotonicClock.Instance`.
- Produces: `ScanDeadlineQueue.Add(CompiledScanBlock block, long firstDueMs)`, `TryTakeDue(long nowMs, out CompiledScanBlock block)`, and `Complete(CompiledScanBlock block, long completedAtMs)`.
- Produces: `ScanCompletion(long NextDueMs, long MissedPeriods, long LatenessMs)`.

- [ ] **Step 1: Write deterministic failing tests for arbitrary intervals, phase retention, and overrun coalescing**

```csharp
using IGW.Core;

namespace IGW.Tests;

public sealed class ScanDeadlineQueueTests
{
    [Fact]
    public void ArbitraryIntervals_AreOrderedByExactDeadline()
    {
        var q = new ScanDeadlineQueue();
        var a = CompiledScanBlock.ForTest(1, 17);
        var b = CompiledScanBlock.ForTest(2, 35);
        q.Add(b, 35);
        q.Add(a, 17);

        Assert.False(q.TryTakeDue(16, out _));
        Assert.True(q.TryTakeDue(17, out var due));
        Assert.Same(a, due);
    }

    [Fact]
    public void Complete_UsesOriginalPhase_InsteadOfCompletionPlusPeriod()
    {
        var q = new ScanDeadlineQueue();
        var block = CompiledScanBlock.ForTest(1, 17);
        q.Add(block, 100);
        Assert.True(q.TryTakeDue(100, out _));

        ScanCompletion completion = q.Complete(block, 108);

        Assert.Equal(117, completion.NextDueMs);
        Assert.Equal(0, completion.MissedPeriods);
        Assert.Equal(8, completion.LatenessMs);
    }

    [Fact]
    public void Complete_CoalescesEveryMissedPeriod_IntoOneFutureDeadline()
    {
        var q = new ScanDeadlineQueue();
        var block = CompiledScanBlock.ForTest(1, 10);
        q.Add(block, 100);
        Assert.True(q.TryTakeDue(100, out _));

        ScanCompletion completion = q.Complete(block, 136);

        Assert.Equal(140, completion.NextDueMs);
        Assert.Equal(3, completion.MissedPeriods);
        Assert.Equal(36, completion.LatenessMs);
        Assert.False(q.TryTakeDue(139, out _));
        Assert.True(q.TryTakeDue(140, out _));
    }
}
```

- [ ] **Step 2: Run the tests and verify they fail because the new scheduler types do not exist**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter FullyQualifiedName~ScanDeadlineQueueTests`

Expected: build fails with missing `ScanDeadlineQueue`, `CompiledScanBlock`, and `ScanCompletion`.

- [ ] **Step 3: Implement the monotonic clock and single-entry-per-block deadline heap**

```csharp
// src/IGW.Core/MonotonicClock.cs
namespace IGW.Core;

internal interface IMonotonicClock
{
    long NowMilliseconds { get; }
}

internal sealed class SystemMonotonicClock : IMonotonicClock
{
    public static readonly SystemMonotonicClock Instance = new();
    private SystemMonotonicClock() { }
    public long NowMilliseconds => Environment.TickCount64;
}

// src/IGW.Core/ScanDeadlineQueue.cs
namespace IGW.Core;

internal readonly record struct ScanCompletion(long NextDueMs, long MissedPeriods, long LatenessMs);

internal sealed class ScanDeadlineQueue
{
    private readonly PriorityQueue<CompiledScanBlock, long> _queue = new();
    private readonly Dictionary<int, long> _scheduled = new();

    public int Count => _scheduled.Count;

    public void Add(CompiledScanBlock block, long firstDueMs)
    {
        if (!_scheduled.TryAdd(block.Id, firstDueMs))
            throw new InvalidOperationException($"读取块 {block.Id} 已在截止时间队列中。");
        block.DueMs = firstDueMs;
        _queue.Enqueue(block, firstDueMs);
    }

    public bool TryTakeDue(long nowMs, out CompiledScanBlock block)
    {
        while (_queue.TryPeek(out var candidate, out long due))
        {
            if (!_scheduled.TryGetValue(candidate.Id, out long current) || current != due)
            {
                _queue.Dequeue();
                continue;
            }
            if (due > nowMs) break;
            _queue.Dequeue();
            _scheduled.Remove(candidate.Id);
            block = candidate;
            return true;
        }
        block = null!;
        return false;
    }

    public long DelayUntilNext(long nowMs) =>
        _queue.TryPeek(out _, out long due) ? Math.Max(0, due - nowMs) : long.MaxValue;

    public ScanCompletion Complete(CompiledScanBlock block, long completedAtMs)
    {
        long due = block.DueMs;
        long lateness = Math.Max(0, completedAtMs - due);
        long missed = lateness / block.PeriodMs;
        long next = checked(due + (missed + 1) * block.PeriodMs);
        Add(block, next);
        return new ScanCompletion(next, missed, lateness);
    }
}
```

- [ ] **Step 4: Run deadline tests**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter FullyQualifiedName~ScanDeadlineQueueTests`

Expected: 3 tests pass.

- [ ] **Step 5: Checkpoint**

Run: `git diff --check -- src/IGW.Core/MonotonicClock.cs src/IGW.Core/ScanDeadlineQueue.cs tests/IGW.Tests/ScanDeadlineQueueTests.cs`

Optional authorized commit:

```powershell
git add src/IGW.Core/MonotonicClock.cs src/IGW.Core/ScanDeadlineQueue.cs tests/IGW.Tests/ScanDeadlineQueueTests.cs
git commit -m "feat: add arbitrary scan deadline scheduler"
```

### Task 2: Immutable Scan-Demand Leases

**Files:**
- Create: `src/IGW.Core/ScanDemandRegistry.cs`
- Create: `tests/IGW.Tests/ScanDemandRegistryTests.cs`

**Interfaces:**
- Consumes: `DataHub.TryGetHandleId(string, out int)` and tag paths in `Channel.Device...` form.
- Produces: `IScanDemandLease`, `DeviceScanDemandSnapshot`, and `ScanDemandRegistry`.
- Produces: `Acquire(string owner, IEnumerable<string> tagIds, int periodMs)`, `Snapshot(string deviceName)`, `Version(string deviceName)`, and `HasDemand(string deviceName)`.

- [ ] **Step 1: Write failing tests for per-device partitioning, shortest interval, and lease release**

```csharp
using IGW.Abstractions;
using IGW.Core;

namespace IGW.Tests;

public sealed class ScanDemandRegistryTests
{
    [Fact]
    public async Task Acquire_PartitionsAcrossDevices_AndUsesShortestPeriod()
    {
        await using var hub = new DataHub(shards: 1);
        Register(hub, "C.P1.DB.A");
        Register(hub, "C.P2.DB.B");
        var registry = new ScanDemandRegistry(hub);

        using var slow = registry.Acquire("mes:slow", new[] { "C.P1.DB.A" }, 200);
        using var fast = registry.Acquire("mes:fast", new[] { "C.P1.DB.A", "C.P2.DB.B" }, 17);

        Assert.Equal(17, registry.Snapshot("C.P1").EffectivePeriods.Single().PeriodMs);
        Assert.Equal(17, registry.Snapshot("C.P2").EffectivePeriods.Single().PeriodMs);
    }

    [Fact]
    public async Task Dispose_RemovesOnlyItsOwnDemand_AndBumpsVersion()
    {
        await using var hub = new DataHub(shards: 1);
        Register(hub, "C.P1.DB.A");
        var registry = new ScanDemandRegistry(hub);
        using var slow = registry.Acquire("mes:slow", new[] { "C.P1.DB.A" }, 200);
        var fast = registry.Acquire("mes:fast", new[] { "C.P1.DB.A" }, 10);
        long before = registry.Version("C.P1");

        fast.Dispose();

        Assert.True(registry.Version("C.P1") > before);
        Assert.Equal(200, registry.Snapshot("C.P1").EffectivePeriods.Single().PeriodMs);
        Assert.True(registry.HasDemand("C.P1"));
    }

    private static void Register(DataHub hub, string id) => hub.Register(id, new TagDef
    {
        Name = id.Split('.').Last(), Address = "DB1.DBD0", DataType = IgwDataType.Float, ScanRateMs = 1000
    });
}
```

- [ ] **Step 2: Run the tests and verify missing registry types**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter FullyQualifiedName~ScanDemandRegistryTests`

Expected: build fails with missing `ScanDemandRegistry`.

- [ ] **Step 3: Implement immutable per-device snapshots without per-point tasks**

```csharp
// src/IGW.Core/ScanDemandRegistry.cs
using System.Collections.Concurrent;

namespace IGW.Core;

public interface IScanDemandLease : IDisposable { }

public readonly record struct EffectiveScanPeriod(string TagId, int Handle, int PeriodMs);

public sealed record DeviceScanDemandSnapshot(
    long Version,
    IReadOnlyList<EffectiveScanPeriod> EffectivePeriods)
{
    public static readonly DeviceScanDemandSnapshot Empty = new(0, Array.Empty<EffectiveScanPeriod>());
}

public sealed class ScanDemandRegistry
{
    private readonly record struct DemandPoint(string TagId, int Handle);
    private sealed record Registration(long Id, string Owner, int PeriodMs, DemandPoint[] Points);
    private sealed class DeviceState
    {
        public readonly object Gate = new();
        public readonly Dictionary<long, Registration> Registrations = new();
        public long Version;
        public DeviceScanDemandSnapshot Snapshot = DeviceScanDemandSnapshot.Empty;
    }

    private readonly DataHub _hub;
    private readonly ConcurrentDictionary<string, DeviceState> _devices = new(StringComparer.OrdinalIgnoreCase);
    private long _nextId;

    public ScanDemandRegistry(DataHub hub) => _hub = hub;

    public IScanDemandLease Acquire(string owner, IEnumerable<string> tagIds, int periodMs)
    {
        if (periodMs <= 0) throw new ArgumentOutOfRangeException(nameof(periodMs));
        long id = Interlocked.Increment(ref _nextId);
        var byDevice = new Dictionary<string, List<DemandPoint>>(StringComparer.OrdinalIgnoreCase);
        foreach (string tagId in tagIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!_hub.TryGetHandleId(tagId, out int handle)) continue;
            string device = DeviceNameOf(tagId);
            if (!byDevice.TryGetValue(device, out var points)) byDevice[device] = points = new();
            points.Add(new DemandPoint(tagId, handle));
        }
        foreach (var pair in byDevice)
        {
            var state = _devices.GetOrAdd(pair.Key, _ => new DeviceState());
            lock (state.Gate)
            {
                state.Registrations[id] = new Registration(id, owner, periodMs, pair.Value.ToArray());
                Rebuild(state);
            }
        }
        return new Lease(this, id, byDevice.Keys.ToArray());
    }

    public bool HasDemand(string deviceName) =>
        _devices.TryGetValue(deviceName, out var state) && state.Snapshot.EffectivePeriods.Count > 0;

    public long Version(string deviceName) =>
        _devices.TryGetValue(deviceName, out var state) ? Volatile.Read(ref state.Version) : 0;

    public DeviceScanDemandSnapshot Snapshot(string deviceName) =>
        _devices.TryGetValue(deviceName, out var state) ? Volatile.Read(ref state.Snapshot) : DeviceScanDemandSnapshot.Empty;

    private void Release(long id, string[] devices)
    {
        foreach (string device in devices)
        {
            if (!_devices.TryGetValue(device, out var state)) continue;
            lock (state.Gate)
                if (state.Registrations.Remove(id)) Rebuild(state);
        }
    }

    private static void Rebuild(DeviceState state)
    {
        var periods = new Dictionary<string, (int Handle, int PeriodMs)>(StringComparer.OrdinalIgnoreCase);
        foreach (var registration in state.Registrations.Values)
        foreach (var point in registration.Points)
            if (!periods.TryGetValue(point.TagId, out var current) || registration.PeriodMs < current.PeriodMs)
                periods[point.TagId] = (point.Handle, registration.PeriodMs);
        long version = ++state.Version;
        state.Snapshot = new DeviceScanDemandSnapshot(version,
            periods.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(x => new EffectiveScanPeriod(x.Key, x.Value.Handle, x.Value.PeriodMs)).ToArray());
    }

    private static string DeviceNameOf(string tagId)
    {
        int first = tagId.IndexOf('.');
        int second = first < 0 ? -1 : tagId.IndexOf('.', first + 1);
        return second < 0 ? tagId : tagId[..second];
    }

    private sealed class Lease(ScanDemandRegistry owner, long id, string[] devices) : IScanDemandLease
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Release(id, devices);
        }
    }
}
```

- [ ] **Step 4: Run demand tests**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter FullyQualifiedName~ScanDemandRegistryTests`

Expected: all tests pass.

- [ ] **Step 5: Checkpoint**

Run: `git diff --check -- src/IGW.Core/ScanDemandRegistry.cs tests/IGW.Tests/ScanDemandRegistryTests.cs`

Optional authorized commit:

```powershell
git add src/IGW.Core/ScanDemandRegistry.cs tests/IGW.Tests/ScanDemandRegistryTests.cs
git commit -m "feat: add shared scan demand registry"
```

### Task 3: Device Scan-Plan Compiler

**Files:**
- Create: `src/IGW.Core/DeviceScanPlan.cs`
- Create: `tests/IGW.Tests/DeviceScanPlanTests.cs`

**Interfaces:**
- Consumes: `DeviceScanDemandSnapshot`, `TagBinding`, `DataHub.TryGetHandleId`, and `DriverLimits`.
- Produces: `CompiledScanBlock` and `DeviceScanPlanCompiler.Compile(...)`.
- Guarantees: same handle appears once at its shortest period; exact periods remain arbitrary; address blocks obey driver limits.

- [ ] **Step 1: Write failing plan tests**

```csharp
using IGW.Abstractions;
using IGW.Core;

namespace IGW.Tests;

public sealed class DeviceScanPlanTests
{
    [Fact]
    public async Task Compile_UsesMesPeriodWhenItIsFasterThanTagDefault()
    {
        await using var hub = new DataHub(shards: 1);
        var bindings = Bindings(hub, 3, 1000);
        var demands = new DeviceScanDemandSnapshot(1, bindings.Select((_, i) =>
            new EffectiveScanPeriod($"C.D.G.T{i}", Handle(hub, $"C.D.G.T{i}"), 17)).ToArray());

        var plan = DeviceScanPlanCompiler.Compile(bindings, hub, demands,
            new DriverLimits { MaxReadUnits = 120, MaxGap = 8 }, includeAllTags: false);

        Assert.All(plan, block => Assert.Equal(17, block.PeriodMs));
        Assert.Equal(3, plan.Sum(block => block.Tags.Count));
    }

    [Fact]
    public async Task Compile_DoesNotRoundArbitraryPeriodsIntoLanes()
    {
        await using var hub = new DataHub(shards: 1);
        var bindings = Bindings(hub, 2, 1000);
        var demands = new DeviceScanDemandSnapshot(1, new[]
        {
            new EffectiveScanPeriod("C.D.G.T0", Handle(hub, "C.D.G.T0"), 17),
            new EffectiveScanPeriod("C.D.G.T1", Handle(hub, "C.D.G.T1"), 83),
        });

        var plan = DeviceScanPlanCompiler.Compile(bindings, hub, demands, new DriverLimits(), false);

        Assert.Equal(new[] { 17, 83 }, plan.Select(x => x.PeriodMs).Order().ToArray());
    }

    private static List<TagBinding> Bindings(DataHub hub, int count, int scanRate)
    {
        var list = new List<TagBinding>();
        for (int i = 0; i < count; i++)
        {
            string id = $"C.D.G.T{i}";
            var def = new TagDef { Name = $"T{i}", Address = $"DB1.DBD{i * 4}", DataType = IgwDataType.Float, ScanRateMs = scanRate };
            hub.Register(id, def);
            list.Add(new TagBinding { TagId = id, Def = def, Address = new TagAddress(1, i * 4, 4) });
        }
        return list;
    }

    private static int Handle(DataHub hub, string id) => hub.TryGetHandleId(id, out int h) ? h : -1;
}
```

- [ ] **Step 2: Run plan tests and verify missing compiler types**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter FullyQualifiedName~DeviceScanPlanTests`

Expected: build fails with missing `DeviceScanPlanCompiler` and `CompiledScanBlock`.

- [ ] **Step 3: Implement exact-period/address-block compilation**

```csharp
// src/IGW.Core/DeviceScanPlan.cs
using IGW.Abstractions;

namespace IGW.Core;

internal sealed class CompiledScanBlock
{
    public required int Id { get; init; }
    public required int PeriodMs { get; init; }
    public required int Area { get; init; }
    public required int Start { get; init; }
    public required int Count { get; init; }
    public required IReadOnlyList<TagBinding> Tags { get; init; }
    public required int[] Handles { get; init; }
    public long DueMs { get; set; }

    internal static CompiledScanBlock ForTest(int id, int periodMs) => new()
    {
        Id = id, PeriodMs = periodMs, Area = 0, Start = 0, Count = 0,
        Tags = Array.Empty<TagBinding>(), Handles = Array.Empty<int>()
    };
}

internal static class DeviceScanPlanCompiler
{
    public static IReadOnlyList<CompiledScanBlock> Compile(
        IReadOnlyList<TagBinding> bindings,
        DataHub hub,
        DeviceScanDemandSnapshot demands,
        DriverLimits limits,
        bool includeAllTags)
    {
        var byHandle = new Dictionary<int, TagBinding>(bindings.Count);
        var periods = new Dictionary<int, int>(bindings.Count);
        foreach (var binding in bindings)
        {
            if (!hub.TryGetHandleId(binding.TagId, out int handle)) continue;
            byHandle[handle] = binding;
            if (includeAllTags) periods[handle] = Math.Max(1, binding.Def.ScanRateMs);
        }
        foreach (var demand in demands.EffectivePeriods)
        {
            if (!byHandle.TryGetValue(demand.Handle, out var binding) ||
                !string.Equals(binding.TagId, demand.TagId, StringComparison.OrdinalIgnoreCase)) continue;
            int requested = Math.Max(1, demand.PeriodMs);
            int tagDefault = Math.Max(1, binding.Def.ScanRateMs);
            int effective = Math.Min(requested, tagDefault);
            if (!periods.TryGetValue(demand.Handle, out int current) || effective < current)
                periods[demand.Handle] = effective;
        }

        int id = 0;
        var result = new List<CompiledScanBlock>();
        foreach (var periodGroup in periods.GroupBy(x => x.Value).OrderBy(x => x.Key))
        {
            var tags = periodGroup.Select(x => byHandle[x.Key]).ToArray();
            foreach (var readBlock in AddressBlockOptimizer.Plan(tags, limits.MaxReadUnits, limits.MaxGap))
            {
                int[] handles = readBlock.Tags.Select(tag =>
                    hub.TryGetHandleId(tag.TagId, out int handle) ? handle : -1).ToArray();
                result.Add(new CompiledScanBlock
                {
                    Id = id++, PeriodMs = periodGroup.Key,
                    Area = readBlock.Area, Start = readBlock.Start, Count = readBlock.Count,
                    Tags = readBlock.Tags, Handles = handles
                });
            }
        }
        return result;
    }
}
```

- [ ] **Step 4: Run plan tests and the existing address optimizer tests**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter "FullyQualifiedName~DeviceScanPlanTests|FullyQualifiedName~AddressBlock"`

Expected: all selected tests pass.

- [ ] **Step 5: Checkpoint**

Run: `git diff --check -- src/IGW.Core/DeviceScanPlan.cs tests/IGW.Tests/DeviceScanPlanTests.cs`

Optional authorized commit:

```powershell
git add src/IGW.Core/DeviceScanPlan.cs tests/IGW.Tests/DeviceScanPlanTests.cs
git commit -m "feat: compile arbitrary device scan plans"
```

### Task 4: Per-Device Deadline Execution and Timing Metrics

**Files:**
- Create: `src/IGW.Core/DeviceTimingMetrics.cs`
- Modify: `src/IGW.Core/Engine.cs`
- Modify: `tests/IGW.Tests/RuntimeDemandTests.cs`

**Interfaces:**
- Consumes: `ScanDemandRegistry`, `DeviceScanPlanCompiler`, and `ScanDeadlineQueue`.
- Produces: extended `DeviceStats` properties `ConfiguredMinPeriodMs`, `ActualAveragePeriodMs`, `ActualP95PeriodMs`, `ActualP99PeriodMs`, `OverduePeriods`, `ConsecutiveOverruns`, `PlannedBlocks`, and `SamplesPerSecond`.
- Changes: `RuntimeEngine(..., ScanDemandRegistry? scanDemands = null)` and an optional final `DeviceLoop(..., ScanDemandRegistry? scanDemands = null, IMonotonicClock? clock = null)` parameter so existing direct-loop tests remain source-compatible.

- [ ] **Step 1: Add failing runtime tests for arbitrary rate and non-accumulating overrun**

Add to `tests/IGW.Tests/RuntimeDemandTests.cs`:

```csharp
[Fact]
public async Task DeviceLoop_UsesMesDemandPeriod_WithoutChangingTagDefinition()
{
    await using var hub = new DataHub(shards: 1);
    var binding = Binding("C1.D1.T1", 1000);
    hub.Register(binding.TagId, binding.Def);
    var demands = new ScanDemandRegistry(hub);
    using var lease = demands.Acquire("mes:flow", new[] { binding.TagId }, 17);
    await using var driver = new DemandTestDriver();
    var loop = NewLoop(driver, binding, hub, scanDemands: demands);
    using var cts = new CancellationTokenSource(160);

    loop.Start(cts.Token);
    await loop.Completion;

    Assert.True(driver.ReadCount >= 5, $"actual reads: {driver.ReadCount}");
    Assert.Equal(1000, binding.Def.ScanRateMs);
}

[Fact]
public async Task DeviceLoop_WhenReadIsSlowerThanPeriod_DoesNotBuildBacklog()
{
    await using var hub = new DataHub(shards: 1);
    var binding = Binding("C1.D1.T1", 1000);
    hub.Register(binding.TagId, binding.Def);
    var demands = new ScanDemandRegistry(hub);
    using var lease = demands.Acquire("mes:flow", new[] { binding.TagId }, 10);
    await using var driver = new DemandTestDriver { ReadDelayMs = 35 };
    var loop = NewLoop(driver, binding, hub, scanDemands: demands);
    using var cts = new CancellationTokenSource(190);

    loop.Start(cts.Token);
    await loop.Completion;

    Assert.InRange(driver.ReadCount, 3, 7);
    Assert.True(loop.Stats.OverduePeriods > 0);
    Assert.True(loop.Stats.ConsecutiveOverruns > 0);
}
```

Extend the existing test helpers instead of adding a second helper family:

```csharp
private static DeviceLoop NewLoop(DemandTestDriver driver, TagBinding binding, DataHub hub,
    int circuitOpenMs = 30_000, ScanDemandRegistry? scanDemands = null) =>
    new(driver, new[] { binding }, hub, NullLogger.Instance, "C1.D1",
        scanMode: "all", activeWindowMs: 30_000, idlePollMs: 10,
        circuitOpenMs: circuitOpenMs, scanDemands: scanDemands);
```

Add `public int ReadDelayMs { get; init; }` to `DemandTestDriver` and insert this at the start of its successful `ReadAsync` path:

```csharp
if (ReadDelayMs > 0) await Task.Delay(ReadDelayMs, ct);
```

- [ ] **Step 2: Run the selected runtime tests and verify they fail against the old `ScanGroup` loop**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter "FullyQualifiedName~DeviceLoop_UsesMesDemandPeriod|FullyQualifiedName~DeviceLoop_WhenReadIsSlower"`

Expected: build fails for missing constructor/stats members.

- [ ] **Step 3: Implement allocation-free timing aggregation**

```csharp
// src/IGW.Core/DeviceTimingMetrics.cs
namespace IGW.Core;

internal sealed class DeviceTimingMetrics
{
    private readonly long[] _periodBuckets = new long[32];
    private long _lastComplete;
    private long _periodSum;
    private long _periodCount;
    private long _samples;
    private long _windowStarted = Environment.TickCount64;
    private long _overdue;
    private long _consecutive;

    public void RecordCompletion(long nowMs, int samples, long missedPeriods)
    {
        long previous = Interlocked.Exchange(ref _lastComplete, nowMs);
        if (previous > 0)
        {
            long period = Math.Max(0, nowMs - previous);
            Interlocked.Add(ref _periodSum, period);
            Interlocked.Increment(ref _periodCount);
            int bucket = 0;
            for (long ceiling = 1; ceiling < period && bucket < 31; ceiling <<= 1) bucket++;
            Interlocked.Increment(ref _periodBuckets[bucket]);
        }
        Interlocked.Add(ref _samples, samples);
        if (missedPeriods > 0)
        {
            Interlocked.Add(ref _overdue, missedPeriods);
            Interlocked.Increment(ref _consecutive);
        }
        else Interlocked.Exchange(ref _consecutive, 0);
    }

    public DeviceTimingSnapshot Snapshot()
    {
        long count = Volatile.Read(ref _periodCount);
        long elapsed = Math.Max(1, Environment.TickCount64 - Volatile.Read(ref _windowStarted));
        return new DeviceTimingSnapshot(
            count == 0 ? 0 : (double)Volatile.Read(ref _periodSum) / count,
            Percentile(0.95), Percentile(0.99), Volatile.Read(ref _overdue),
            Volatile.Read(ref _consecutive), 1000.0 * Volatile.Read(ref _samples) / elapsed);
    }

    private int Percentile(double percentile)
    {
        long total = 0;
        for (int i = 0; i < _periodBuckets.Length; i++) total += Volatile.Read(ref _periodBuckets[i]);
        if (total == 0) return 0;
        long target = Math.Max(1, (long)Math.Ceiling(total * percentile));
        long seen = 0;
        for (int i = 0; i < _periodBuckets.Length; i++)
            if ((seen += Volatile.Read(ref _periodBuckets[i])) >= target)
                return i == 0 ? 0 : i >= 30 ? int.MaxValue : 1 << i;
        return int.MaxValue;
    }
}

internal readonly record struct DeviceTimingSnapshot(
    double AveragePeriodMs, int P95PeriodMs, int P99PeriodMs,
    long OverduePeriods, long ConsecutiveOverruns, double SamplesPerSecond);
```

- [ ] **Step 4: Refactor `DeviceLoop` to rebuild immutable plans on demand-version changes and execute due blocks**

In `src/IGW.Core/Engine.cs`:

```csharp
public sealed record DeviceStats(long ReadsOk, long ReadsErr, long WritesOk, long WritesErr,
    int LastScanMs, string LastError, double SuccessRate)
{
    public int ConfiguredMinPeriodMs { get; init; }
    public double ActualAveragePeriodMs { get; init; }
    public int ActualP95PeriodMs { get; init; }
    public int ActualP99PeriodMs { get; init; }
    public long OverduePeriods { get; init; }
    public long ConsecutiveOverruns { get; init; }
    public int PlannedBlocks { get; init; }
    public double SamplesPerSecond { get; init; }
}
```

Replace `_groups` with `_bindings`, `_scanDemands`, `_deadlines`, `_planVersion`, `_plan`, and `_timing`. Build the initial plan at start and rebuild only when `ScanDemandRegistry.Version(_name)` changes (plus the existing low-frequency active-tag refresh). For every due block:

Use these constructor additions so existing call sites remain compatible:

```csharp
private readonly ScanDemandRegistry _scanDemands;
private readonly IMonotonicClock _clock;

// Append these optional parameters to the existing DeviceLoop constructor.
ScanDemandRegistry? scanDemands = null,
IMonotonicClock? clock = null

_scanDemands = scanDemands ?? new ScanDemandRegistry(hub);
_clock = clock ?? SystemMonotonicClock.Instance;
```

In `RuntimeEngine`, retain the shared instance and pass it into every new `DeviceLoop`:

```csharp
private readonly ScanDemandRegistry _scanDemands;
public ScanDemandRegistry ScanDemands => _scanDemands;

public RuntimeEngine(IgwProject project, DriverRegistry registry, DataHub hub, ILoggerFactory lf,
    ScanDemandRegistry? scanDemands = null)
{
    _project = project;
    _registry = registry;
    _hub = hub;
    _lf = lf;
    _scanDemands = scanDemands ?? new ScanDemandRegistry(hub);
    _log = lf.CreateLogger("RuntimeEngine");
}
```

```csharp
long started = _clock.NowMilliseconds;
var samples = await _active.ReadAsync(block.Tags, ct);
Publish(samples);
long completed = _clock.NowMilliseconds;
ScanCompletion completion = _deadlines.Complete(block, completed);
_timing.RecordCompletion(completed, samples.Count, completion.MissedPeriods);
```

`HasDemand()` must become:

```csharp
private bool HasDemand() =>
    Volatile.Read(ref _explicitDemand) > 0 ||
    !_writes.IsEmpty ||
    _scanDemands.HasDemand(_name) ||
    _hub.HasDeviceDemand(_name, _activeWindowMs);
```

When rebuilding, preserve phase for blocks with the same stable key `(PeriodMs, Area, Start, Count)`; new blocks start at the current monotonic time. When the circuit opens or a device recovers, resume at current deadlines and never replay missed periods. Keep all existing reconnect, failover, half-open, write queue, bad-quality marking, and cancellation branches.

- [ ] **Step 5: Run runtime-demand and circuit-breaker tests**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter "FullyQualifiedName~RuntimeDemandTests|FullyQualifiedName~DataplaneTests"`

Expected: all selected tests pass, including the two new arbitrary/overrun tests.

- [ ] **Step 6: Checkpoint**

Run: `git diff --check -- src/IGW.Core/DeviceTimingMetrics.cs src/IGW.Core/Engine.cs tests/IGW.Tests/RuntimeDemandTests.cs`

Optional authorized commit:

```powershell
git add src/IGW.Core/DeviceTimingMetrics.cs src/IGW.Core/Engine.cs tests/IGW.Tests/RuntimeDemandTests.cs
git commit -m "feat: run devices on arbitrary deadline plans"
```

### Task 5: Subscription-Aware Batch DataHub Updates

**Files:**
- Modify: `src/IGW.Core/DataHub.cs`
- Modify: `src/IGW.Core/Engine.cs`
- Create: `tests/IGW.Tests/DataHubBatchTests.cs`

**Interfaces:**
- Produces: `DataHubUpdate(object Handle, DataValue Value)` and `DataHub.UpdateBatch(ReadOnlySpan<DataHubUpdate>)`.
- Guarantees: values and timestamps always update; notifications occur only when value/quality changes and a subscriber is interested.

- [ ] **Step 1: Write failing tests for no-subscriber fast path and change-only notifications**

```csharp
using IGW.Abstractions;
using IGW.Core;

namespace IGW.Tests;

public sealed class DataHubBatchTests
{
    [Fact]
    public async Task UpdateBatch_UpdatesLatestValueWithoutPublishingWhenNoSubscriberExists()
    {
        await using var hub = new DataHub(shards: 1);
        hub.Register("C.D.G.T", new TagDef { Name = "T", Address = "0", DataType = IgwDataType.Int32 });
        object handle = Assert.IsAssignableFrom<object>(hub.GetHandle("C.D.G.T"));

        hub.UpdateBatch(new[] { new DataHubUpdate(handle, DataValue.GoodAt(42, DateTime.UtcNow)) });

        Assert.True(hub.TryGet("C.D.G.T", out var value));
        Assert.Equal(42, value.Value);
        Assert.Equal(0, hub.PublishedChangeCount);
    }

    [Fact]
    public async Task UpdateBatch_SuppressesEqualValueAndQuality_ButPublishesQualityChange()
    {
        await using var hub = new DataHub(shards: 1);
        hub.Register("C.D.G.T", new TagDef { Name = "T", Address = "0", DataType = IgwDataType.Int32 });
        object handle = hub.GetHandle("C.D.G.T")!;
        var seen = new List<TagChange>();
        using var sub = hub.SubscribeAll(change => { lock (seen) seen.Add(change); return ValueTask.CompletedTask; }, "test");

        hub.UpdateBatch(new[] { new DataHubUpdate(handle, DataValue.GoodAt(42, DateTime.UtcNow)) });
        hub.UpdateBatch(new[] { new DataHubUpdate(handle, DataValue.GoodAt(42, DateTime.UtcNow.AddMilliseconds(1))) });
        hub.UpdateBatch(new[] { new DataHubUpdate(handle, DataValue.BadAt(StatusCode.BadCommFailure, DateTime.UtcNow)) });
        await Task.Delay(100);

        lock (seen) Assert.Equal(2, seen.Count);
    }
}
```

- [ ] **Step 2: Run batch tests and verify missing API**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter FullyQualifiedName~DataHubBatchTests`

Expected: build fails with missing `DataHubUpdate`, `UpdateBatch`, and `PublishedChangeCount`.

- [ ] **Step 3: Implement batch writes and subscriber-aware dispatch**

Add to `src/IGW.Core/DataHub.cs`:

```csharp
public readonly struct DataHubUpdate
{
    public object Handle { get; }
    public DataValue Value { get; }
    public DataHubUpdate(object handle, DataValue value) { Handle = handle; Value = value; }
}

private long _publishedChangeCount;
internal long PublishedChangeCount => Interlocked.Read(ref _publishedChangeCount);

public void UpdateBatch(ReadOnlySpan<DataHubUpdate> updates)
{
    foreach (ref readonly var update in updates)
        if (update.Handle is Entry entry) UpdateEntry(entry, in update.Value);
}

private void UpdateEntry(Entry entry, in DataValue value)
{
    DataValue previous = ReadVal(entry);
    bool changed = previous.Quality != value.Quality || !previous.ValueEquals(in value);
    WriteVal(entry, in value);
    if (!changed || (Volatile.Read(ref _wildcardDemand) <= 0 && entry.PointSubs.Length == 0)) return;
    var shard = _shards[(int)((uint)entry.Handle % (uint)_shards.Length)];
    if (shard.Bus.Writer.TryWrite(new Change(entry, value)))
        Interlocked.Increment(ref _publishedChangeCount);
}
```

Change both `UpdateByHandle` and `Update` to call `UpdateEntry`. In `DeviceLoop.Publish`, rent `DataHubUpdate[]` from `ArrayPool<DataHubUpdate>.Shared`, resolve the already cached handle from `_pub`, call `UpdateBatch`, clear the used span, and return the buffer in `finally`.

- [ ] **Step 4: Run batch and existing subscription/backpressure tests**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter "FullyQualifiedName~DataHubBatchTests|FullyQualifiedName~DataplaneTests"`

Expected: all selected tests pass; existing bounded/drop counters remain correct when subscribers exist.

- [ ] **Step 5: Checkpoint**

Run: `git diff --check -- src/IGW.Core/DataHub.cs src/IGW.Core/Engine.cs tests/IGW.Tests/DataHubBatchTests.cs`

Optional authorized commit:

```powershell
git add src/IGW.Core/DataHub.cs src/IGW.Core/Engine.cs tests/IGW.Tests/DataHubBatchTests.cs
git commit -m "perf: batch latest-value cache updates"
```

### Task 6: MES Task Demand Registration Without Full-Table Hot Scans

**Files:**
- Modify: `src/IGW.Northbound.Mes/MesUploadEngine.cs`
- Modify: `src/IGW.Runtime/GatewayHost.cs`
- Modify: `tests/IGW.Tests/MesRangePayloadTests.cs`

**Interfaces:**
- Consumes: `ScanDemandRegistry.Acquire`, `MesDataRangeCatalog`, and `MesTask.CollectIntervalMs`.
- Produces: one `IScanDemandLease` per enabled MES task.
- Removes: `_demandTags` and the current global `PinDataDemand` pass over `_hub.ListTagIds()`.

- [ ] **Step 1: Add failing tests for two task periods sharing one point and disposal**

Replace the existing `MesDemand_跨PLC数据范围会同时声明两台设备需求` test body in `tests/IGW.Tests/MesRangePayloadTests.cs` with:

```csharp
[Fact]
public async Task MesDemand_跨PLC数据范围采用最短任务周期并在释放后清除()
{
    var project = CrossPlcProject();
    await using var hub = new DataHub(shards: 1);
    const string plcATag = "Line.PLC_A.DBRecipe.Name";
    const string plcBTag = "Line.PLC_B.DBQuality.Result.Value";
    hub.Register(plcATag, FindTag(project, plcATag));
    hub.Register(plcBTag, FindTag(project, plcBTag));
    var demands = new ScanDemandRegistry(hub);
    MesTask TaskAt(string id, int periodMs) => new()
    {
        Id = id, Name = id, Enabled = true, CollectIntervalMs = periodMs,
        Payload = new MesPayload
        {
            Mode = MesPayloadModes.DataRanges,
            DataRanges =
            {
                new MesDataRange { SourcePath = "Line.PLC_A.DBRecipe" },
                new MesDataRange { SourcePath = "Line.PLC_B.DBQuality" },
            },
        },
    };
    var settings = new MesUploadSettings { Enabled = true, LogDbPath = "" };
    settings.Tasks.Add(TaskAt("slow", 200));
    settings.Tasks.Add(TaskAt("fast", 17));

    var engine = new MesUploadEngine(hub, settings, (_, _, _) => Task.FromResult(true),
        NullLogger.Instance, catalog: MesDataRangeCatalog.Build(project), scanDemands: demands);

    Assert.All(demands.Snapshot("Line.PLC_A").EffectivePeriods, item => Assert.Equal(17, item.PeriodMs));
    Assert.All(demands.Snapshot("Line.PLC_B").EffectivePeriods, item => Assert.Equal(17, item.PeriodMs));
    await engine.DisposeAsync();
    Assert.False(demands.HasDemand("Line.PLC_A"));
    Assert.False(demands.HasDemand("Line.PLC_B"));
}
```

- [ ] **Step 2: Run the MES test and verify the constructor/lease behavior is missing**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter FullyQualifiedName~MesEngine_RegistersEnabledTaskRanges`

Expected: build fails because `MesUploadEngine` does not accept `ScanDemandRegistry`.

- [ ] **Step 3: Resolve each enabled task into a stable tag-id array and acquire its lease**

Append `ScanDemandRegistry? scanDemands = null` to the existing constructor after `deviceState`, assign `_scanDemands = scanDemands`, and add:

```csharp
private readonly ScanDemandRegistry? _scanDemands;
private readonly List<IScanDemandLease> _scanDemandLeases = new();

private void RegisterScanDemands(IEnumerable<MesTask> tasks)
{
    if (_scanDemands is null) return;
    foreach (var task in tasks.Where(task => task.Enabled))
    {
        string[] tagIds = ResolveTaskTagIds(task).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (tagIds.Length == 0) continue;
        _scanDemandLeases.Add(_scanDemands.Acquire(
            $"mes:{task.Id}", tagIds, Math.Max(1, task.CollectIntervalMs)));
    }
}
```

`ResolveTaskTagIds` must reuse the existing selector collection logic for triggers, conditions, heartbeat, handshake, switch, writeback, legacy fields/structs, and every upload node. For `MesPayload.DataRanges`, recursively flatten `MesCatalogGroup.Tags` and `Children`, applying `MesDataRange.Excludes` before returning `MesCatalogTag.TagId`. It must never call `ListTagIds()` for data-range payloads. Legacy group selectors may use one startup-only binary prefix search over `DataHub.SortedTagIds`, never `selectors.Any` for every tag.

Dispose every lease before disposing the task loops:

```csharp
foreach (var lease in _scanDemandLeases) lease.Dispose();
_scanDemandLeases.Clear();
```

In `GatewayHost.StartSouthAsync`, construct and retain one registry:

```csharp
public ScanDemandRegistry ScanDemands { get; private set; } = null!;

Hub = new DataHub();
ScanDemands = new ScanDemandRegistry(Hub);
Engine = new RuntimeEngine(Project, _registry, Hub, _lf, ScanDemands);
```

Pass `ScanDemands` into `MesUploadEngine` in normal startup and test-upload construction. Do not create a second registry in the northbound phase.

- [ ] **Step 4: Run MES range, runtime demand, and disposal tests**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter "FullyQualifiedName~MesRangePayloadTests|FullyQualifiedName~RuntimeDemandTests"`

Expected: all selected tests pass; disabled tasks produce no demand; disposing MES releases demand and permits the device to disconnect.

- [ ] **Step 5: Checkpoint**

Run: `git diff --check -- src/IGW.Northbound.Mes/MesUploadEngine.cs src/IGW.Runtime/GatewayHost.cs tests/IGW.Tests/MesRangePayloadTests.cs`

Optional authorized commit:

```powershell
git add src/IGW.Northbound.Mes/MesUploadEngine.cs src/IGW.Runtime/GatewayHost.cs tests/IGW.Tests/MesRangePayloadTests.cs
git commit -m "feat: drive collection from MES task demand"
```

### Task 7: Runtime Period and Overrun Diagnostics

**Files:**
- Modify: `src/IGW.Northbound.RestApi/IgwRestApi.cs`
- Modify: `tests/IGW.Tests/ObservabilityTests.cs`

**Interfaces:**
- Consumes: extended `DeviceStats`.
- Produces: additive JSON properties under the existing runtime device-status endpoint.

- [ ] **Step 1: Extend the existing observability test with exact diagnostic fields**

```csharp
Assert.True(device.TryGetProperty("configuredMinPeriodMs", out _));
Assert.True(device.TryGetProperty("actualAveragePeriodMs", out _));
Assert.True(device.TryGetProperty("actualP95PeriodMs", out _));
Assert.True(device.TryGetProperty("actualP99PeriodMs", out _));
Assert.True(device.TryGetProperty("overduePeriods", out _));
Assert.True(device.TryGetProperty("consecutiveOverruns", out _));
Assert.True(device.TryGetProperty("plannedBlocks", out _));
Assert.True(device.TryGetProperty("samplesPerSecond", out _));
```

- [ ] **Step 2: Run the observability test and verify fields are absent**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter FullyQualifiedName~ObservabilityTests`

Expected: the updated assertion fails on the first missing property.

- [ ] **Step 3: Add the fields to the existing device-status projection**

```csharp
configuredMinPeriodMs = d.Stats.ConfiguredMinPeriodMs,
actualAveragePeriodMs = Math.Round(d.Stats.ActualAveragePeriodMs, 2),
actualP95PeriodMs = d.Stats.ActualP95PeriodMs,
actualP99PeriodMs = d.Stats.ActualP99PeriodMs,
overduePeriods = d.Stats.OverduePeriods,
consecutiveOverruns = d.Stats.ConsecutiveOverruns,
plannedBlocks = d.Stats.PlannedBlocks,
samplesPerSecond = Math.Round(d.Stats.SamplesPerSecond, 1),
periodHealthy = d.Stats.ConsecutiveOverruns == 0,
```

Keep the existing connection state, online counts, read/write counters, errors, and success rate unchanged.

- [ ] **Step 4: Run observability tests**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter FullyQualifiedName~ObservabilityTests`

Expected: all selected tests pass.

- [ ] **Step 5: Checkpoint**

Run: `git diff --check -- src/IGW.Northbound.RestApi/IgwRestApi.cs tests/IGW.Tests/ObservabilityTests.cs`

Optional authorized commit:

```powershell
git add src/IGW.Northbound.RestApi/IgwRestApi.cs tests/IGW.Tests/ObservabilityTests.cs
git commit -m "feat: expose collection deadline diagnostics"
```

### Task 8: Repeatable One-Million-Point Performance Harness

**Files:**
- Create: `tests/IGW.Performance/IGW.Performance.csproj`
- Create: `tests/IGW.Performance/Program.cs`
- Modify: `src/IGW.Core/IGW.Core.csproj`
- Modify: `IGW.slnx`

**Interfaces:**
- Consumes: `DataHub`, `ScanDemandRegistry`, `RuntimeEngine`, and the existing driver registry/factory interfaces.
- Produces: a console benchmark that prints one JSON summary and returns non-zero with `--assert` when thresholds fail.

- [ ] **Step 1: Create the performance project and verify the solution cannot build until `Program.cs` exists**

```xml
<!-- tests/IGW.Performance/IGW.Performance.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\IGW.Abstractions\IGW.Abstractions.csproj" />
    <ProjectReference Include="..\..\src\IGW.Core\IGW.Core.csproj" />
  </ItemGroup>
</Project>
```

Add `<InternalsVisibleTo Include="IGW.Performance" />` to `IGW.Core.csproj`, then add the project under `/tests/` in `IGW.slnx`.

Run: `dotnet build IGW.slnx --no-restore`

Expected: build fails because the executable has no entry point.

- [ ] **Step 2: Implement a synthetic 50-device, one-million-point workload**

`Program.cs` must:

1. Accept `--points 1000000`, `--devices 50`, `--period 200`, `--fast-points 500`, `--fast-period 10`, `--seconds 30`, and `--assert`.
2. Generate exactly `points / devices` contiguous numeric bindings per device without creating a Vue/REST project representation.
3. Register all points once in `DataHub` and use one `ScanDemandRegistry`.
4. Acquire one 200 ms demand lease per device and one 10 ms lease for the first `fast-points` of each configured fast task.
5. Use a synthetic driver that reuses a preallocated `TagSample[]` per compiled tag-list instance and never sleeps.
6. Warm up for five seconds, reset counters, then measure for the requested duration.
7. Print one JSON object containing point count, device count, duration, process CPU percentage, working set MB, Gen0/1/2 counts, samples per second, P95/P99, overruns, and dropped changes.
8. Under `--assert`, return exit code 2 when queue growth occurs, working set exceeds 8192 MB, process CPU exceeds 50% on a machine with at least 12 logical processors, or any device becomes synchronously blocked by an injected slow peer.

Use `Process.TotalProcessorTime`, `GC.CollectionCount`, `GC.GetTotalAllocatedBytes`, and the extended `DeviceStats`; do not add BenchmarkDotNet.

- [ ] **Step 3: Run a small smoke benchmark**

Run: `dotnet run --project tests/IGW.Performance/IGW.Performance.csproj --no-restore -- --points 10000 --devices 5 --period 200 --fast-points 100 --fast-period 10 --seconds 5`

Expected: exit code 0 and one valid JSON summary with non-zero `samplesPerSecond`.

- [ ] **Step 4: Run the full benchmark outside the normal unit-test suite**

Run: `dotnet run -c Release --project tests/IGW.Performance/IGW.Performance.csproj --no-restore -- --points 1000000 --devices 50 --period 200 --fast-points 500 --fast-period 10 --seconds 30`

Expected: exit code 0; save the JSON output in the task report. Do not claim hardware certification from this synthetic result.

- [ ] **Step 5: Checkpoint**

Run: `git diff --check -- tests/IGW.Performance src/IGW.Core/IGW.Core.csproj IGW.slnx`

Optional authorized commit:

```powershell
git add tests/IGW.Performance src/IGW.Core/IGW.Core.csproj IGW.slnx
git commit -m "test: add million-point collection benchmark"
```

### Task 9: Full Regression and Phase-One Acceptance

**Files:**
- Modify only files required to fix failures caused by Tasks 1-8.

**Interfaces:**
- Consumes: every phase-one interface.
- Produces: a verified phase-one baseline and evidence for the protocol-driver plan.

- [ ] **Step 1: Run the complete .NET test suite**

Run: `dotnet test IGW.slnx --no-restore`

Expected: all tests pass; no test is skipped because of the new scheduler.

- [ ] **Step 2: Run the Release build**

Run: `dotnet build IGW.slnx -c Release --no-restore`

Expected: 0 errors. Existing unrelated warnings may remain; record them without hiding new warnings introduced by this work.

- [ ] **Step 3: Run frontend regression even though this phase does not change UI**

Run from `src/IGW.WebUi`:

```powershell
npm test
npm run typecheck
npm run build
```

Expected: all frontend tests pass, typecheck exits 0, production build exits 0.

- [ ] **Step 4: Run focused diff and formatting checks**

Run:

```powershell
git diff --check -- src/IGW.Core src/IGW.Northbound.Mes src/IGW.Runtime/GatewayHost.cs src/IGW.Northbound.RestApi/IgwRestApi.cs tests/IGW.Tests tests/IGW.Performance IGW.slnx
git status --short
```

Expected: `git diff --check` produces no output. `git status` may show unrelated pre-existing changes; list only files changed by this plan in the phase report.

- [ ] **Step 5: Run the 30-minute acceptance benchmark on the reference machine when available**

Run:

```powershell
dotnet run -c Release --project tests/IGW.Performance/IGW.Performance.csproj --no-restore -- --points 1000000 --devices 50 --period 200 --fast-points 500 --fast-period 10 --seconds 1800 --assert
```

Expected on a 12-core/32-GB reference machine: exit code 0, no sustained queue growth, working set below 8192 MB, total CPU below 50%, 10 ms P99 lateness no more than 10 ms, 200 ms P99 lateness no more than 50 ms, and no synchronous cross-device blocking.

- [ ] **Step 6: Produce the phase report**

Report exact test counts, build results, benchmark command, hardware, JSON metrics, remaining driver limitations, and the next plan name `2026-07-16-protocol-native-read-plans.md`. Do not package or install a release until the protocol and UI phases also pass.

Optional authorized commit after review:

```powershell
git add src/IGW.Core src/IGW.Northbound.Mes/MesUploadEngine.cs src/IGW.Runtime/GatewayHost.cs src/IGW.Northbound.RestApi/IgwRestApi.cs tests/IGW.Tests tests/IGW.Performance IGW.slnx
git commit -m "feat: add million-point arbitrary-cycle collection core"
```
