# Siemens DB、S7 Plus 与安全认证 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让一个 `Siemens.S7` 设备自动兼容结构化 DB 导入、S7 Classic/S7 Plus 混合采集，以及匿名失败后的本机加密身份验证。

**Architecture:** Web 端使用独立递归解析器把 DB/UDT 声明转换为可测试的 AST 和叶子点；Runtime 通过 Classic/Plus 传输抽象按点位地址自动路由。S7 Plus 使用独立 native 适配层，工程仅保存凭据引用，Runtime 通过 DPAPI 凭据库解析认证信息。

**Tech Stack:** .NET 10、ASP.NET Core Minimal API、Vue 3/TypeScript、Node test runner、Rust `s7commplus` 0.1.x native bridge、Windows DPAPI、xUnit。

## Global Constraints

- 外部驱动 ID 始终为 `Siemens.S7`，不得增加用户可选的 Classic/Plus 或优化/非优化模式。
- S7-1200/1500优先 Plus；S7-200/300/400只使用现有 Classic。
- 同一台 PLC 的 Classic 绝对地址与 Plus 符号地址可以同时采集。
- 默认匿名；只有 PLC 明确返回认证要求时进入“待认证”，认证拒绝不得回退 Classic。
- 密码不得进入 `.igwproj`、Git、日志、API 响应、诊断包或安装包。
- 本机凭据使用 DPAPI 加密；迁移机器后要求重新验证且不得丢工程配置。
- 无数据需求时不主动连接；现有采集周期、熔断和自动保存语义保持不变。
- 保留工作区现有用户改动；每次暂存和提交必须使用显式文件列表。
- 没有真实 PLC/受支持 PLCSIM Advanced 时，不得宣称 S7 Plus 已完成生产实机验证。

---

### Task 1: 递归 Siemens DB/UDT 解析器

**Files:**
- Create: `src/IGW.WebUi/src/utils/siemensDbImport.ts`
- Modify: `src/IGW.WebUi/src/utils/siemensDbImport.test.cjs`
- Modify: `src/IGW.WebUi/src/views/Project.vue`

**Interfaces:**
- Produces: `parseSiemensDb(source: string): SiemensDbImportResult`
- Produces: `SiemensDbImportResult = { dbName: string; dbNumber?: number; optimized: boolean; groups: ImportedGroup[]; tags: ImportedTag[]; skipped: ImportIssue[] }`
- Produces: `ImportedTag = { name: string; path: string; address: string; dataType: string; description: string; stringLength?: number }`
- Consumes: existing Project.vue import action and tag/group data shape.

- [ ] **Step 1: Complete the failing DB2100 fixture test**

Extend `siemensDbImport.test.cjs` to load the real fixture from `E:/项目/南卡工厂翻包机/YanJing.NK.Moudle/DB2100_电芯OCV_DataDB.db` and assert exactly 70 leaf tags, 11 `CellData_N` groups, empty `skipped`, the known offsets (`DBB6`, `DBD58`, `DBW1830`, `DBD1832`), and Chinese descriptions.

- [ ] **Step 2: Add grammar coverage before implementation**

Add table-driven tests for nested inline structs, named UDT references, arrays with non-zero/negative lower bounds, multidimensional arrays, STRING/WSTRING, BOOL packing, date/time scalars, and unsupported pointer/reference types. Assert unsupported leaves produce `{ path, type, reason }` rather than disappearing.

- [ ] **Step 3: Run tests and verify RED**

Run: `node --test src/IGW.WebUi/src/utils/siemensDbImport.test.cjs`

Expected: FAIL because `parseSiemensDb` is absent or does not expand structure arrays.

- [ ] **Step 4: Implement lexer, parser, layout, and emitter**

Implement focused internal units in `siemensDbImport.ts`:

```ts
type Token = { kind: 'word'|'number'|'string'|'symbol'|'comment'; text: string; offset: number }
type TypeNode = ScalarType | ArrayType | StructType | NamedType
type FieldNode = { name: string; type: TypeNode; comment: string }

export function parseSiemensDb(source: string): SiemensDbImportResult
function tokenize(source: string): Token[]
function parseDeclarations(tokens: Token[]): DbAst
function layoutClassic(ast: DbAst): LayoutResult
function emitImport(ast: DbAst, layout?: LayoutResult): SiemensDbImportResult
```

Do not strip comments before tokenization. For optimized DBs emit symbolic paths and do not invent byte offsets. For non-optimized DBs apply Classic alignment and retain array lower bounds in symbolic paths.

- [ ] **Step 5: Replace only the old parser call site**

In `Project.vue`, import `parseSiemensDb`, remove the embedded regex parsing branch, translate `ImportedGroup/ImportedTag` into the existing project tree objects, and keep the existing dialog, confirmation, autosave, selection, and tree expansion behavior unchanged.

- [ ] **Step 6: Verify GREEN and frontend compilation**

Run:

```powershell
node --test src/IGW.WebUi/src/utils/siemensDbImport.test.cjs
pnpm --dir src/IGW.WebUi typecheck
pnpm --dir src/IGW.WebUi build
```

Expected: all parser tests pass; typecheck and production build exit 0.

- [ ] **Step 7: Commit parser files only**

```powershell
git add src/IGW.WebUi/src/utils/siemensDbImport.ts src/IGW.WebUi/src/utils/siemensDbImport.test.cjs src/IGW.WebUi/src/views/Project.vue
git commit -m "feat: import nested Siemens DB structures"
```

### Task 2: Siemens 地址描述与批次路由

**Files:**
- Create: `src/IGW.Drivers.IoTClient/SiemensAddressDescriptor.cs`
- Create: `src/IGW.Drivers.IoTClient/SiemensRoutePlanner.cs`
- Create: `tests/IGW.Tests/SiemensRoutePlannerTests.cs`
- Modify: `tests/IGW.Tests/IGW.Tests.csproj`

**Interfaces:**
- Produces: `SiemensAddressDescriptor.Parse(string address)`
- Produces: `SiemensAddressKind { Classic, Symbolic }`
- Produces: `SiemensRoutePlanner.Plan(IReadOnlyList<TagBinding>): SiemensRoutePlan`
- Produces: `SiemensRoutePlan.Classic` and `.Plus` immutable binding lists.

- [ ] **Step 1: Write failing address classification tests**

Cover `DB2100.DBW2`, `DB2100.DBX0.0`, `DB2100_电芯OCV_DataDB.CellData[0].KValue`, malformed empty symbols, and old S7-200 `VW100` addresses. Assert parsing happens once and exposes DB number, byte/bit offset, and symbolic path when applicable.

- [ ] **Step 2: Write failing mixed-route tests**

Create a batch containing Classic and symbolic bindings and assert stable original ordering within each route. Assert S7-300 rejects symbolic points with a clear configuration result, while S7-1200/1500 accepts mixed batches.

- [ ] **Step 3: Run focused tests and verify RED**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter SiemensRoutePlannerTests`

Expected: FAIL because address and route types do not exist.

- [ ] **Step 4: Implement immutable descriptors and planner**

Use exact public surface:

```csharp
public enum SiemensAddressKind { Classic, Symbolic }
public sealed record SiemensAddressDescriptor(
    SiemensAddressKind Kind, string Raw, int? DbNumber,
    int? ByteOffset, int? BitOffset, string? SymbolPath)
{
    public static SiemensAddressDescriptor Parse(string address);
}

public sealed record SiemensRoutePlan(
    IReadOnlyList<TagBinding> Classic,
    IReadOnlyList<TagBinding> Plus);
```

Keep parsing outside scan loops; the hybrid driver will build and cache descriptors per configured binding.

- [ ] **Step 5: Run focused and full IGW tests**

Run:

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter SiemensRoutePlannerTests
dotnet test tests/IGW.Tests/IGW.Tests.csproj
```

Expected: PASS.

- [ ] **Step 6: Commit route planner files only**

```powershell
git add src/IGW.Drivers.IoTClient/SiemensAddressDescriptor.cs src/IGW.Drivers.IoTClient/SiemensRoutePlanner.cs tests/IGW.Tests/SiemensRoutePlannerTests.cs tests/IGW.Tests/IGW.Tests.csproj
git commit -m "feat: plan mixed Siemens address routes"
```

### Task 3: S7 Plus native bridge and managed transport

**Files:**
- Create: `native/igw-s7plus/Cargo.toml`
- Create: `native/igw-s7plus/src/lib.rs`
- Create: `native/igw-s7plus/README.md`
- Create: `src/IGW.Drivers.IoTClient/SiemensPlusTransport.cs`
- Create: `src/IGW.Drivers.IoTClient/SiemensPlusNative.cs`
- Create: `tests/IGW.Tests/SiemensPlusTransportTests.cs`
- Modify: `src/IGW.Drivers.IoTClient/IGW.Drivers.IoTClient.csproj`
- Modify: `tests/IGW.Tests/IGW.Tests.csproj`

**Interfaces:**
- Produces: `ISiemensPlusTransport` with `ConnectAsync`, `AuthenticateAsync`, `BrowseAsync`, `ReadAsync`, `WriteAsync`, `DisconnectAsync`.
- Produces: native ABI version `igw_s7plus_abi_version() == 1`.
- Consumes: `s7commplus` pinned to one reviewed 0.1.x revision; no floating Git branch.

- [ ] **Step 1: Write managed contract tests with a fake native API**

Cover anonymous success, authentication-required classification, correct/incorrect credentials, symbol browse, batch read/write, timeout, cancellation, disconnect, and native error-message ownership. No test may require a PLC.

- [ ] **Step 2: Run focused tests and verify RED**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter SiemensPlusTransportTests`

Expected: FAIL because the transport contract does not exist.

- [ ] **Step 3: Create a versioned native C ABI**

Expose only fixed-layout data and caller-owned buffers:

```rust
#[no_mangle] pub extern "C" fn igw_s7plus_abi_version() -> u32 { 1 }
#[no_mangle] pub extern "C" fn igw_s7plus_create() -> *mut IgwS7PlusHandle;
#[no_mangle] pub extern "C" fn igw_s7plus_connect(handle: *mut IgwS7PlusHandle, request: *const ConnectRequest) -> IgwResult;
#[no_mangle] pub extern "C" fn igw_s7plus_authenticate(handle: *mut IgwS7PlusHandle, user: ByteSlice, password: ByteSlice) -> IgwResult;
#[no_mangle] pub extern "C" fn igw_s7plus_browse(handle: *mut IgwS7PlusHandle, output: MutableByteSlice) -> IgwResult;
#[no_mangle] pub extern "C" fn igw_s7plus_read_batch(handle: *mut IgwS7PlusHandle, items: *const ReadItem, count: usize, output: *mut ReadResult) -> IgwResult;
#[no_mangle] pub extern "C" fn igw_s7plus_write_batch(handle: *mut IgwS7PlusHandle, items: *const WriteItem, count: usize) -> IgwResult;
#[no_mangle] pub extern "C" fn igw_s7plus_destroy(handle: *mut IgwS7PlusHandle);
```

Wrap every exported function with panic containment. Never return a Rust-owned string pointer. Classify result codes as `Ok`, `AuthenticationRequired`, `AuthenticationFailed`, `PermissionDenied`, `UnsupportedFirmware`, `Timeout`, `TransportFailure`, or `ProtocolFailure`.

- [ ] **Step 4: Implement the managed P/Invoke wrapper**

`SiemensPlusNative` checks ABI version before creating a handle. `SiemensPlusTransport` converts native results to typed `SiemensPlusException` without including credentials, uses one serialized connection operation lane, and returns IGW `TagSample`/`StatusCode` values.

- [ ] **Step 5: Build and test the bridge**

Run:

```powershell
cargo test --manifest-path native/igw-s7plus/Cargo.toml
cargo build --release --manifest-path native/igw-s7plus/Cargo.toml
dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter SiemensPlusTransportTests
```

Expected: native unit tests and managed contract tests pass. Copy rules place `igw_s7plus.dll` beside Runtime during build/publish without requiring Rust on the target machine.

- [ ] **Step 6: Commit native bridge and managed adapter**

```powershell
git add native/igw-s7plus src/IGW.Drivers.IoTClient/SiemensPlusTransport.cs src/IGW.Drivers.IoTClient/SiemensPlusNative.cs src/IGW.Drivers.IoTClient/IGW.Drivers.IoTClient.csproj tests/IGW.Tests/SiemensPlusTransportTests.cs tests/IGW.Tests/IGW.Tests.csproj
git commit -m "feat: add authenticated S7 Plus transport"
```

### Task 4: Hybrid Siemens device driver

**Files:**
- Create: `src/IGW.Drivers.IoTClient/SiemensClassicTransport.cs`
- Create: `src/IGW.Drivers.IoTClient/SiemensHybridDeviceDriver.cs`
- Create: `tests/IGW.Tests/SiemensHybridDriverTests.cs`
- Modify: `src/IGW.Drivers.IoTClient/IoTPlcFactories.cs`
- Modify: `src/IGW.Drivers.IoTClient/IoTPlcDriver.cs`

**Interfaces:**
- Produces: `ISiemensClassicTransport` as the testable boundary around the existing IoTClient Siemens client.
- Consumes: `ISiemensPlusTransport`, `SiemensRoutePlanner`.
- Produces: one `IDeviceDriver` with one public connection state and independent Classic/Plus health.
- Produces: `ISiemensDeviceDiagnostics` with `SiemensConnectionDetail Snapshot()`.

- [ ] **Step 1: Write failing hybrid behavior tests**

Use fake transports to verify 1200/1500 Plus-first behavior, 200/300/400 Classic-only behavior, mixed routing, lazy Classic connection, no-demand behavior inherited from `DeviceLoop`, authentication-required without Classic bypass, partial-online quality, and independent transport failure isolation.

- [ ] **Step 2: Run focused tests and verify RED**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter SiemensHybridDriverTests`

Expected: FAIL because the hybrid driver does not exist.

- [ ] **Step 3: Implement hybrid connection and per-route batch operations**

The driver must:

```csharp
public interface ISiemensClassicTransport : IAsyncDisposable
{
    ConnectionState State { get; }
    Task ConnectAsync(CancellationToken ct);
    Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> batch, CancellationToken ct);
    Task<StatusCode> WriteAsync(TagBinding tag, object value, CancellationToken ct);
    Task DisconnectAsync();
}

public sealed class SiemensHybridDeviceDriver : IDeviceDriver, ISiemensDeviceDiagnostics
{
    public Task ConnectAsync(CancellationToken ct);
    public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> batch, CancellationToken ct);
    public Task<StatusCode> WriteAsync(TagBinding tag, object value, CancellationToken ct);
    public Task DisconnectAsync();
}
```

For authenticated devices, resolve credentials immediately before Plus authentication and clear the managed reference after the call. Authentication-required/authentication-failed errors remain stable until configuration or credentials change so `DeviceLoop` does not create a high-speed retry loop.

- [ ] **Step 4: Register the hybrid factory without changing driver ID**

`IoTSiemensDriverFactory` continues to report `Siemens.S7`, but creates `SiemensHybridDeviceDriver`. Extract the current IoTClient Siemens creation into a Classic transport adapter; do not alter Omron, Mitsubishi, AB, or Modbus factories.

- [ ] **Step 5: Run focused, demand, and full tests**

Run:

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter SiemensHybridDriverTests
dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter RuntimeDemandTests
dotnet test tests/IGW.Tests/IGW.Tests.csproj
```

Expected: PASS and existing demand/circuit-breaker behavior unchanged.

- [ ] **Step 6: Commit hybrid driver files only**

```powershell
git add src/IGW.Drivers.IoTClient/SiemensClassicTransport.cs src/IGW.Drivers.IoTClient/SiemensHybridDeviceDriver.cs src/IGW.Drivers.IoTClient/IoTPlcFactories.cs src/IGW.Drivers.IoTClient/IoTPlcDriver.cs tests/IGW.Tests/SiemensHybridDriverTests.cs
git commit -m "feat: route Siemens Classic and Plus automatically"
```

### Task 5: DPAPI device credential vault

**Files:**
- Create: `src/IGW.Core/Security/DeviceCredentialStore.cs`
- Create: `src/IGW.Core/Security/DpapiDeviceSecretProtector.cs`
- Create: `tests/IGW.Tests/DeviceCredentialStoreTests.cs`
- Modify: `src/IGW.Core/IGW.Core.csproj`

**Interfaces:**
- Produces: `IDeviceCredentialStore.GetAsync/SaveAsync/DeleteAsync`.
- Produces: `DeviceCredential(string Username, string Password)`.
- Produces: `IDeviceSecretProtector.Protect/Unprotect` for deterministic fake-protector tests.

- [ ] **Step 1: Write failing vault tests**

Use a temporary `IGW_DATA_DIR` and fake protector. Assert atomic save, opaque GUID references, no plaintext in the vault file, replacement only after successful save, delete, missing reference, corrupt ciphertext, and foreign-machine/unprotect failure classification.

- [ ] **Step 2: Run focused tests and verify RED**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter DeviceCredentialStoreTests`

Expected: FAIL because the credential store does not exist.

- [ ] **Step 3: Implement encrypted atomic storage**

Use exact contracts:

```csharp
public sealed record DeviceCredential(string Username, string Password);
public interface IDeviceCredentialStore
{
    Task<DeviceCredential?> GetAsync(string reference, CancellationToken ct = default);
    Task<string> SaveAsync(DeviceCredential credential, string? replaceReference = null, CancellationToken ct = default);
    Task DeleteAsync(string reference, CancellationToken ct = default);
}
```

Store only version, reference, protected payload, and timestamps under `%ProgramData%\IGW\secrets`. Write to a temporary file, flush, then atomically replace. Apply Windows ACLs for the Runtime identity, SYSTEM, and Administrators. `DpapiDeviceSecretProtector` uses `DataProtectionScope.LocalMachine`; non-Windows returns a clear unsupported-platform result rather than falling back to plaintext.

- [ ] **Step 4: Add secret-leak regression tests**

Serialize a project containing `authMode=credential` and `credentialRef=<guid>`, then recursively scan project JSON, log capture, API DTO serialization, and temp vault directory for the test username/password. Only protected vault bytes may differ from plaintext.

- [ ] **Step 5: Run focused and full tests**

Run:

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter DeviceCredentialStoreTests
dotnet test tests/IGW.Tests/IGW.Tests.csproj
```

Expected: PASS.

- [ ] **Step 6: Commit vault files only**

```powershell
git add src/IGW.Core/Security/DeviceCredentialStore.cs src/IGW.Core/Security/DpapiDeviceSecretProtector.cs src/IGW.Core/IGW.Core.csproj tests/IGW.Tests/DeviceCredentialStoreTests.cs
git commit -m "feat: protect PLC credentials with DPAPI"
```

### Task 6: Runtime authentication service and REST API

**Files:**
- Create: `src/IGW.Runtime/SiemensAuthenticationService.cs`
- Create: `src/IGW.Northbound.RestApi/SiemensAuthenticationApi.cs`
- Create: `tests/IGW.Tests/SiemensAuthenticationServiceTests.cs`
- Modify: `src/IGW.Runtime/Program.cs`
- Modify: `src/IGW.Runtime/GatewayHost.cs`
- Modify: `src/IGW.Northbound.RestApi/IgwRestApi.cs`
- Modify: `src/IGW.Core/Engine.cs`
- Modify: `src/IGW.Core/IGatewayHost.cs`
- Modify: `tests/IGW.Tests/IGW.Tests.csproj`

**Interfaces:**
- Produces: `GET /api/v1/devices/{name}/authentication`.
- Produces: `POST /api/v1/devices/{name}/authentication/verify`.
- Produces: `DELETE /api/v1/devices/{name}/authentication`.
- Produces: device diagnostic reason codes surfaced by `/api/v1/devices`.

- [ ] **Step 1: Write failing service tests**

Cover successful verify-and-save, failed verify preserving old credentials, delete restoring anonymous, unknown/non-Siemens device, IP/model change requiring revalidation, rate-limited repeated failures, and cancellation. Use fake Plus transport and fake credential store.

- [ ] **Step 2: Run focused tests and verify RED**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter SiemensAuthenticationServiceTests`

Expected: FAIL because the authentication service does not exist.

- [ ] **Step 3: Implement transactional verification**

`VerifyAndSaveAsync` resolves the configured device, establishes a temporary Plus session, authenticates, performs one minimal permitted symbol browse/read, and only then saves the credential. Update a cloned project with `authMode=credential` and `credentialRef`, call the existing atomic `ReloadAsync`, and delete the replaced secret only after reload succeeds. Add `IGW.Runtime` as a test-only project reference so the service is exercised without moving Runtime-specific orchestration into Core.

- [ ] **Step 4: Add dedicated endpoints and safe DTOs**

Use DTOs that never include ciphertext or saved password:

```csharp
public sealed record SiemensAuthStatusDto(string Mode, bool HasCredential, string State, string Message);
public sealed record SiemensAuthVerifyRequest(string Username, string Password);
```

Limit username/password lengths, reject empty password, apply the existing `/api/v1` write permission, and log only device plus result category.

- [ ] **Step 5: Extend device diagnostics**

Add stable reason fields such as `AuthenticationRequired`, `AuthenticationFailed`, `PermissionDenied`, `UnsupportedFirmware`, `TransportFailure`, and `SymbolMismatch`. Preserve `ConnectionState` compatibility for existing consumers while `/api/v1/devices` exposes `detailState` and `detailMessage`.

- [ ] **Step 6: Run focused and API regression tests**

Run:

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter SiemensAuthenticationServiceTests
dotnet test tests/IGW.Tests/IGW.Tests.csproj
```

Expected: PASS; no API snapshot contains test credentials.

- [ ] **Step 7: Commit service/API files only**

```powershell
git add src/IGW.Runtime/SiemensAuthenticationService.cs src/IGW.Runtime/Program.cs src/IGW.Runtime/GatewayHost.cs src/IGW.Northbound.RestApi/SiemensAuthenticationApi.cs src/IGW.Northbound.RestApi/IgwRestApi.cs src/IGW.Core/Engine.cs src/IGW.Core/IGatewayHost.cs tests/IGW.Tests/SiemensAuthenticationServiceTests.cs tests/IGW.Tests/IGW.Tests.csproj
git commit -m "feat: verify and store Siemens credentials safely"
```

### Task 7: 工程配置认证交互

**Files:**
- Create: `src/IGW.WebUi/src/components/SiemensAuthenticationDialog.vue`
- Create: `src/IGW.WebUi/src/components/SiemensAuthenticationDialog.test.cjs`
- Modify: `src/IGW.WebUi/src/api.ts`
- Modify: `src/IGW.WebUi/src/views/Project.vue`
- Modify: `src/IGW.WebUi/src/utils/deviceRuntime.ts`
- Modify: `src/IGW.WebUi/src/utils/deviceRuntime.test.cjs`

**Interfaces:**
- Consumes: authentication REST endpoints and `/api/v1/devices` detail state.
- Produces: `SiemensAuthenticationDialog` with `verify`, `delete`, `close` events.

- [ ] **Step 1: Write failing UI contract tests**

Assert Siemens device creation remains anonymous with no credential inputs; `AuthenticationRequired` renders “需要身份验证” and action; dialog has optional username and password; “验证并保存” disables during request; failed verification keeps the dialog open; success closes and refreshes only device state; deleting credentials returns to anonymous.

- [ ] **Step 2: Run UI tests and verify RED**

Run:

```powershell
node --test src/IGW.WebUi/src/components/SiemensAuthenticationDialog.test.cjs
node --test src/IGW.WebUi/src/utils/deviceRuntime.test.cjs
```

Expected: FAIL because the dialog/API methods do not exist.

- [ ] **Step 3: Add typed API methods**

Add `siemensAuthStatus(device)`, `verifySiemensAuth(device, username, password)`, and `deleteSiemensAuth(device)` to `api.ts`. Set a 30-second timeout for online verification and never persist form values in local storage or the project object.

- [ ] **Step 4: Implement the authentication dialog**

Use Element Plus password input with `autocomplete="new-password"`, no prefilled saved value, explicit pending state, error category text, and a destructive confirmation before deleting local credentials. Clear local refs on close, success, and component unmount.

- [ ] **Step 5: Integrate device status and context action**

In `Project.vue`, show the authentication action only for Siemens devices when status requires it or when credentials already exist. Do not collapse/reset tree expansion, current selection, open MES flow, or unsaved project edits when the dialog closes.

- [ ] **Step 6: Verify tests, typecheck, and build**

Run:

```powershell
node --test src/IGW.WebUi/src/components/SiemensAuthenticationDialog.test.cjs
node --test src/IGW.WebUi/src/utils/deviceRuntime.test.cjs
pnpm --dir src/IGW.WebUi typecheck
pnpm --dir src/IGW.WebUi build
```

Expected: PASS.

- [ ] **Step 7: Commit UI files only**

```powershell
git add src/IGW.WebUi/src/components/SiemensAuthenticationDialog.vue src/IGW.WebUi/src/components/SiemensAuthenticationDialog.test.cjs src/IGW.WebUi/src/api.ts src/IGW.WebUi/src/views/Project.vue src/IGW.WebUi/src/utils/deviceRuntime.ts src/IGW.WebUi/src/utils/deviceRuntime.test.cjs
git commit -m "feat: prompt for Siemens authentication when required"
```

### Task 8: 全量验证与发布依赖检查

**Files:**
- Modify: `installer/IGW.iss`
- Modify: `installer/version.txt`
- Create: `tools/verify-s7plus-portable.ps1`
- Create: `docs/siemens-s7plus-validation.md`

**Interfaces:**
- Consumes: release native DLL, Runtime publish output, installer output.
- Produces: clean-machine verification script and explicit real-PLC validation record.

- [ ] **Step 1: Add installer/publish assertions**

Make publish fail if `igw_s7plus.dll` is absent. Installer includes the DLL once beside `IGW.Runtime.exe`; it must not include Cargo registry, Rust source build output, PLC credentials, `.igwproj` secrets, or test fixtures containing credentials.

- [ ] **Step 2: Add portable smoke script**

`verify-s7plus-portable.ps1` copies published files to a path containing Chinese characters and spaces, starts Runtime with isolated `IGW_DATA_DIR`, calls health/drivers/auth-status endpoints, confirms native ABI load, and scans output/data/logs for sentinel passwords.

- [ ] **Step 3: Run all automated verification once**

Run:

```powershell
cargo test --manifest-path native/igw-s7plus/Cargo.toml
dotnet test IGW.slnx
node --test src/IGW.WebUi/src/utils/siemensDbImport.test.cjs
node --test src/IGW.WebUi/src/components/SiemensAuthenticationDialog.test.cjs
pnpm --dir src/IGW.WebUi typecheck
pnpm --dir src/IGW.WebUi build
dotnet publish src/IGW.Runtime/IGW.Runtime.csproj -c Release -r win-x64 --self-contained true
powershell -NoProfile -ExecutionPolicy Bypass -File tools/verify-s7plus-portable.ps1
```

Expected: every command exits 0; Runtime starts from migrated path; no sentinel credential is found.

- [ ] **Step 4: Record real PLC verification truthfully**

In `docs/siemens-s7plus-validation.md`, record PLC model, firmware, TIA version, optimized/non-optimized mix, anonymous result, correct/wrong credential result, browse/read/write result, test date, and operator. If hardware is unavailable, mark each hardware case `未验证`—not `通过`.

- [ ] **Step 5: Build the installer and verify on another machine**

Run the existing installer build command, install on a clean Windows machine without .NET/Rust SDK, confirm native loading, anonymous connection, credential prompt, DPAPI persistence across service restart, and re-verification after moving only the project to a second machine.

- [ ] **Step 6: Commit release verification files only**

```powershell
git add installer/IGW.iss installer/version.txt tools/verify-s7plus-portable.ps1 docs/siemens-s7plus-validation.md
git commit -m "build: package and verify S7 Plus runtime"
```
