# OPC UA Subscription-First Acquisition Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:executing-plans` to implement this plan task by task. The user explicitly requested implementation-first verification instead of TDD; add or update focused regression tests after each behavior is implemented.

**Goal:** Reduce upstream OPC UA load by sharing one physical session for matching endpoints and serving periodic MES reads from subscription-fed DataHub snapshots, with bounded batch-read fallback for points that lack a Good initial value.

**Architecture:** Remove logical device identity from the OPC UA physical connection key while preserving all connection-affecting settings. Extend DeviceLoop subscription candidates to include explicit ScanDemand points; existing first-read and per-point fallback state becomes the correctness boundary, so successfully primed points leave the polling plan and failed points remain polled until a Good notification arrives.

**Tech Stack:** .NET 8, OPC UA .NET Standard, xUnit, Vue 3/TypeScript diagnostics UI.

**Spec:** `docs/superpowers/specs/2026-09-08-opcua-subscription-first-acquisition-design.md`

## Global Constraints

- Work only in `E:\项目\IGW\.worktrees\layered-acquisition-diagnostics` on `codex/layered-acquisition-diagnostics`; preserve the dirty root checkout.
- Do not use TDD; implement each behavior, then run focused regression tests and finally the complete validation suite.
- Writes stay direct and high priority. Management APIs remain HTTP and point read/write plus live state remain SignalR.
- Logical device state, diagnostics, subscriptions, writes and reload lifecycle remain isolated even when the physical OPC UA Session is shared.
- A stable unchanged value is valid while the OPC UA Session/subscription remains healthy; do not create age-only fallback reads from DataValue timestamps.

---

### Task 1: Share the OPC UA physical session by endpoint configuration

**Files:**
- Modify: `src/IGW.Core/OpcUaConnectionPool.cs`
- Modify: `src/IGW.Abstractions/Drivers.cs`
- Modify: `src/IGW.Drivers.OpcUaClient/OpcUaClientDeviceDriver.cs`
- Modify: `tests/IGW.Tests/SharedOpcUaConnectionsTests.cs`

**Interfaces:**
- `OpcUaConnectionPool.ConnectionKey(ChannelDef, DeviceDef, string?)` keeps its signature for callers but hashes only normalized endpoint and connection-affecting settings.
- `IDeviceDriver` documentation describes logical device ownership separately from optional pooled transports.

- [x] Remove `deviceIdentity` from the connection-key payload and update comments to document the physical endpoint boundary.
- [x] Update tests so equal endpoint/configuration keys match across device IDs and names, while endpoint, credentials, security, adapter and timeout changes remain isolated.
- [x] Verify the runtime creates one driver for two matching OPC UA devices and removing one device keeps the peer connected.
- [x] Run `dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter SharedOpcUa --nologo`.

### Task 2: Move explicit MES scan demand to subscription-first acquisition

**Files:**
- Modify: `src/IGW.Core/Engine.cs`
- Modify: `src/IGW.Abstractions/Drivers.cs`
- Modify: `tests/IGW.Tests/DeviceLoopSubscriptionTests.cs`

**Interfaces:**
- `DeviceLoop.ReconcileRealtimeSubscriptionAsync` builds candidates from the union of active DataHub tags and `DeviceScanDemandSnapshot.EffectivePeriods`.
- `_subscriptionFallbackTagIds` remains the set of subscribed points that still require batch polling.

- [x] Include explicit ScanDemand points in the subscription candidate set and remove the old exclusion.
- [x] Keep the effective demand period when creating monitored items so MES sampling uses its configured rate even when the TagDef default differs.
- [x] Preserve the one-time prime read and per-point Good/Bad fallback behavior.
- [x] Dispose the upstream subscription after the last QC/MES demand expires while keeping the configured idle Session policy.
- [x] Update subscription logs and interface comments to state that periodic consumers use the DataHub snapshot.
- [x] Add regression coverage showing a MES point is primed once, stops polling after Good, and resumes/stops fallback correctly after bad prime/good notification.
- [x] Run `dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter DeviceLoopSubscriptionTests --nologo`.

### Task 3: Verify request reduction, compatibility and release readiness

**Files:**
- Modify if needed: `docs/performance/2026-09-08-opcua-subscription-first-verification.md`

**Interfaces:**
- Verification report records source/build proof separately from simulator and field acceptance.

- [x] Run focused shared-session, subscription, write-priority, reconnect and diagnostics tests.
- [x] Run the complete backend test suite.
- [x] Run the complete frontend test, type-check and production build commands used by this repository.
- [x] Run local OPC UA simulator acceptance and record physical connection/request counts before and after subscription priming.
- [x] Build the Windows installer only after all validations pass; record version, absolute path, size and SHA256.
- [x] Commit all implementation and verification artifacts on the feature branch, leaving the root checkout untouched.

## Self-review

- Spec coverage: session boundary, subscription candidate union, first-value fallback, stable-value semantics, write priority, diagnostics and verification each map to a task.
- Placeholder scan: no TBD/TODO or unspecified implementation action remains.
- Type consistency: existing public signatures are retained; no new cross-task API is required.
