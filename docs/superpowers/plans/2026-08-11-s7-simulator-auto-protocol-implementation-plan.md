# S7 Simulator Automatic Protocol Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the standalone PLC simulator classify Siemens imports as Classic, S7 Plus, or Hybrid and expose matching real protocol behavior from the imported project and shared value store.

**Architecture:** Extend the protocol-neutral simulation model with an evidence-backed Siemens profile, then make DB and KEP importers populate absolute and symbolic addresses without flattening structures. Replace the static built-in Classic memory server with project-backed servers; add a dedicated S7 Plus server boundary and a same-port protocol dispatcher for Hybrid. All protocol services share `SimulationValueStore`.

**Tech Stack:** .NET 8 WPF, HslCommunication Classic S7 server, Rust/native S7 Plus protocol codec compatible with IGW `s7commplus` client, OPC Foundation .NET Standard server, console regression test project.

## Global Constraints

- The simulator remains a standalone tool and must not depend on a running IGW process.
- Users do not choose Classic or Plus manually; import evidence determines the profile.
- Non-optimized DB uses Classic absolute addresses; optimized DB uses S7 Plus symbolic addresses.
- Mixed KEP address evidence uses Hybrid on one configured port.
- Structure, array, type, and Chinese comment hierarchy must be retained.
- All protocol services share one in-memory current-value store; no history is persisted.
- Work stays on `master`; the simulator repository is currently clean.

---

### Task 1: Add an evidence-backed Siemens profile to the simulation model

**Files:**
- Modify: `YanJing.NK.Moudle/PlcSim/YanJing.NK.PlcSim/Core/SimulationProject.cs`
- Modify: `YanJing.NK.Moudle/PlcSim/YanJing.NK.PlcSim.Tests/Program.cs`

**Interfaces:**
- Produces: `SiemensSimulationProfile { None, Classic, Plus, Hybrid }`.
- Produces: `SimulationDevice.SiemensProfile`, `SimulationDevice.ProtocolEvidence`.
- Produces: `SimulationTag.ClassicAddress` and `SimulationTag.SymbolicAddress`; retains `ProtocolAddress` as compatibility projection.

- [ ] **Step 1: Add failing model assertions**

```csharp
var modelProject = new SimulationProject("profile");
var modelDevice = modelProject.AddDevice("PLC");
modelDevice.SiemensProfile = SiemensSimulationProfile.Hybrid;
modelDevice.ProtocolEvidence = "1 absolute + 1 symbolic";
var modelTag = modelDevice.AddGroup("DB1").AddTag("Value", SimulationDataType.Int16);
modelTag.ClassicAddress = "DB1.DBW0";
modelTag.SymbolicAddress = "DB1.Value";
AssertEqual("DB1.DBW0", modelTag.AddressFor(SimulationProtocol.SiemensS7), "classic projection");
AssertEqual("DB1.Value", modelTag.AddressFor(SimulationProtocol.SiemensS7Plus), "plus projection");
```

- [ ] **Step 2: Run tests and verify RED**

Run: `dotnet run --project YanJing.NK.Moudle/PlcSim/YanJing.NK.PlcSim.Tests/YanJing.NK.PlcSim.Tests.csproj -c Release`

Expected: compile failure for missing profile/address members.

- [ ] **Step 3: Implement the model members**

```csharp
public enum SiemensSimulationProfile { None, Classic, Plus, Hybrid }

public sealed class SimulationDevice
{
    public SiemensSimulationProfile SiemensProfile { get; set; }
    public string ProtocolEvidence { get; set; } = string.Empty;
}

public sealed class SimulationTag
{
    public string ClassicAddress { get; set; } = string.Empty;
    public string SymbolicAddress { get; set; } = string.Empty;
    public string AddressFor(SimulationProtocol protocol) => protocol switch
    {
        SimulationProtocol.SiemensS7 => ClassicAddress,
        SimulationProtocol.SiemensS7Plus => SymbolicAddress,
        _ => ProtocolAddress,
    };
}
```

Keep `ProtocolAddress` for OPC UA/Omron and legacy UI bindings.

- [ ] **Step 4: Run tests and verify GREEN**

Run the command from Step 2.

- [ ] **Step 5: Commit Task 1 in the simulator repository**

```powershell
git add YanJing.NK.Moudle/PlcSim/YanJing.NK.PlcSim/Core/SimulationProject.cs YanJing.NK.Moudle/PlcSim/YanJing.NK.PlcSim.Tests/Program.cs
git commit -m "feat: model detected Siemens protocol profiles"
```

### Task 2: Classify Siemens DB imports and generate both address forms

**Files:**
- Modify: `YanJing.NK.Moudle/PlcSim/YanJing.NK.PlcSim/Import/SiemensDbSimulationImporter.cs`
- Modify: `YanJing.NK.Moudle/PlcSim/YanJing.NK.PlcSim.Tests/Program.cs`

**Interfaces:**
- Consumes: Task 1 profile and per-protocol addresses.
- Produces: Classic endpoint/profile for explicit `FALSE`; Plus endpoint/profile for optimized imports.

- [ ] **Step 1: Add failing classification assertions**

For the existing non-optimized fixture:

```csharp
AssertEqual(SiemensSimulationProfile.Classic, dbImport.Project.Devices.Single().SiemensProfile, "nonoptimized profile");
AssertEqual(SimulationProtocol.SiemensS7, dbImport.Project.Devices.Single().Endpoints.Single().Protocol, "nonoptimized endpoint");
AssertEqual("DB2100.DBW168", importedIsNg.ClassicAddress, "classic address retained");
AssertEqual("DB2100_电芯OCV_DataDB.CellData_0.IsNG", importedIsNg.SymbolicAddress, "symbol path retained");
```

For `optimizedVarImport`:

```csharp
AssertEqual(SiemensSimulationProfile.Plus, optimizedVarImport.Project.Devices.Single().SiemensProfile, "optimized profile");
AssertEqual(SimulationProtocol.SiemensS7Plus, optimizedVarImport.Project.Devices.Single().Endpoints.Single().Protocol, "optimized endpoint");
AssertEqual(true, optimizedVarImport.Project.EnumerateTags().All(x => x.SymbolicAddress.Length > 0), "optimized symbols generated");
```

- [ ] **Step 2: Run tests and verify RED**

Run the simulator test command from Task 1.

- [ ] **Step 3: Populate profile, evidence, and addresses**

Set endpoint/profile before adding members:

```csharp
device.SiemensProfile = optimized ? SiemensSimulationProfile.Plus : SiemensSimulationProfile.Classic;
device.ProtocolEvidence = optimized
    ? "S7_Optimized_Access 未显式为 FALSE；使用符号地址"
    : "S7_Optimized_Access = FALSE；使用绝对地址";
device.AddEndpoint(optimized ? SimulationProtocol.SiemensS7Plus : SimulationProtocol.SiemensS7, 102, 102);
```

After adding each tag:

```csharp
tag.ClassicAddress = address;
tag.SymbolicAddress = tag.SymbolPath[(tag.Group.Device.Name.Length + 1)..];
tag.ProtocolAddress = optimized ? tag.SymbolicAddress : tag.ClassicAddress;
```

The cursor continues to compute offsets for both profiles so a later Hybrid import can expose both addresses.

- [ ] **Step 4: Run tests and verify GREEN**

Run the simulator test command.

- [ ] **Step 5: Commit Task 2**

```powershell
git add YanJing.NK.Moudle/PlcSim/YanJing.NK.PlcSim/Import/SiemensDbSimulationImporter.cs YanJing.NK.Moudle/PlcSim/YanJing.NK.PlcSim.Tests/Program.cs
git commit -m "feat: auto-detect Siemens DB protocol"
```

### Task 3: Classify KEP JSON by driver metadata and tag address evidence

**Files:**
- Create: `YanJing.NK.Moudle/PlcSim/YanJing.NK.PlcSim/Import/SiemensProtocolClassifier.cs`
- Modify: `YanJing.NK.Moudle/PlcSim/YanJing.NK.PlcSim/Import/KepJsonSimulationImporter.cs`
- Modify: `YanJing.NK.Moudle/PlcSim/YanJing.NK.PlcSim.Tests/Program.cs`

**Interfaces:**
- Produces: `SiemensProtocolDetection Detect(string driver, IEnumerable<string> addresses)` where the result contains `Profile`, `Evidence`, `ClassicCount`, and `SymbolicCount`.
- Consumes: Task 1 address fields and profile.

- [ ] **Step 1: Add failing classifier tests**

```csharp
AssertEqual(SiemensSimulationProfile.Classic,
    SiemensProtocolClassifier.Detect("Siemens TCP/IP Ethernet", new[] { "DB1.DBW0", "M0.0" }).Profile,
    "KEP absolute profile");
AssertEqual(SiemensSimulationProfile.Plus,
    SiemensProtocolClassifier.Detect("Siemens S7 Plus", new[] { "Recipe.Header.Code" }).Profile,
    "KEP explicit plus profile");
AssertEqual(SiemensSimulationProfile.Hybrid,
    SiemensProtocolClassifier.Detect("Siemens", new[] { "DB1.DBW0", "Recipe.Header.Code" }).Profile,
    "KEP mixed profile");
```

- [ ] **Step 2: Run tests and verify RED**

Run the simulator test command.

- [ ] **Step 3: Implement deterministic classification**

Use compiled patterns for `DB\d+\.(DBX|DBB|DBW|DBD)`, `^[MIQ][BWD]?\d+`, and symbolic paths. Explicit Plus driver metadata wins when all addresses are symbolic; mixed address evidence always produces Hybrid. Classic/Plus create one endpoint, Hybrid creates both endpoint records on listen port 102.

Populate each Siemens tag:

```csharp
if (SiemensProtocolClassifier.IsClassicAddress(address)) tag.ClassicAddress = address;
else tag.SymbolicAddress = address;
tag.ProtocolAddress = address;
```

- [ ] **Step 4: Run fixture and synthetic KEP tests**

Run the simulator test command. Expected: existing 7,351-point fixture count remains unchanged and synthetic Classic/Plus/Hybrid cases pass.

- [ ] **Step 5: Commit Task 3**

```powershell
git add YanJing.NK.Moudle/PlcSim/YanJing.NK.PlcSim/Import/SiemensProtocolClassifier.cs YanJing.NK.Moudle/PlcSim/YanJing.NK.PlcSim/Import/KepJsonSimulationImporter.cs YanJing.NK.Moudle/PlcSim/YanJing.NK.PlcSim.Tests/Program.cs
git commit -m "feat: classify KEP Siemens protocols"
```

### Task 4: Replace the static Classic server with imported-project memory

**Files:**
- Create: `YanJing.NK.Moudle/PlcSim/YanJing.NK.PlcSim/Protocols/SiemensClassicSimulationServer.cs`
- Modify: `YanJing.NK.Moudle/PlcSim/YanJing.NK.PlcSim/MainWindow.xaml.cs`
- Modify: `YanJing.NK.Moudle/PlcSim/YanJing.NK.PlcSim.Tests/Program.cs`

**Interfaces:**
- Produces: `SiemensClassicSimulationServer(SimulationDevice, SimulationValueStore, int port)` with `Start()`, `OnlineCount`, `EnableWrite`, and `DisposeAsync()`.
- Consumes: `SimulationTag.ClassicAddress` and shared `SimulationValueStore`.

- [ ] **Step 1: Add a failing Classic loopback test**

Create a non-optimized two-tag DB project, start the server on a free port, connect `SiemensClient(SiemensVersion.S1500, "127.0.0.1", port, 0, 1)`, read `DB2100.DBW2`, write a new value, and assert the shared store changed.

- [ ] **Step 2: Run tests and verify RED**

Run the simulator test command. Expected: missing `SiemensClassicSimulationServer`.

- [ ] **Step 3: Implement project-backed Classic memory**

At startup, group tags by DB number parsed from `ClassicAddress`, allocate each DB to the maximum required byte end, encode initial store values into HSL memory, and subscribe to `SimulationValueStore.ValueChanged`. On external writes, decode affected tags back into the store. String, Bool, integer, and floating-point codecs must use S7 big-endian layout.

- [ ] **Step 4: Wire MainWindow to the imported device**

Replace the model selector with the detected profile and evidence. `BtnStart_Click` selects the imported Siemens device, creates `SiemensClassicSimulationServer` only for Classic/Hybrid, and reports the actual imported DB/tag count.

- [ ] **Step 5: Run loopback and full simulator tests**

Expected: Classic read/write loopback passes; existing OPC UA and Omron tests still pass.

- [ ] **Step 6: Commit Task 4**

```powershell
git add YanJing.NK.Moudle/PlcSim/YanJing.NK.PlcSim/Protocols/SiemensClassicSimulationServer.cs YanJing.NK.Moudle/PlcSim/YanJing.NK.PlcSim/MainWindow.xaml.cs YanJing.NK.Moudle/PlcSim/YanJing.NK.PlcSim.Tests/Program.cs
git commit -m "feat: serve imported DBs over S7 Classic"
```

### Task 5: Implement the S7 Plus server boundary and IGW loopback

**Files:**
- Create: `YanJing.NK.Moudle/PlcSim/YanJing.NK.PlcSim/Protocols/ISiemensPlusSimulationServer.cs`
- Create: `YanJing.NK.Moudle/PlcSim/YanJing.NK.PlcSim/Protocols/SiemensPlusSimulationServer.cs`
- Create: `YanJing.NK.Moudle/PlcSim/native/nk-s7plus-server/Cargo.toml`
- Create: `YanJing.NK.Moudle/PlcSim/native/nk-s7plus-server/src/lib.rs`
- Modify: `YanJing.NK.Moudle/PlcSim/YanJing.NK.PlcSim/YanJing.NK.PlcSim.csproj`
- Modify: `YanJing.NK.Moudle/PlcSim/YanJing.NK.PlcSim.Tests/Program.cs`

**Interfaces:**
- Produces native exports: `nk_s7plus_server_create`, `nk_s7plus_server_add_symbol`, `nk_s7plus_server_start`, `nk_s7plus_server_stop`, `nk_s7plus_server_destroy`.
- Produces callbacks: native read callback returns a typed payload by symbol; native write callback supplies symbol and typed payload.
- Consumes: Task 1 symbolic addresses and `SimulationValueStore`.

- [ ] **Step 1: Add a failing native lifecycle test**

The managed test creates an optimized DB project, constructs `SiemensPlusSimulationServer`, starts it on a free port, and asserts `IsRunning` without loading IGW.

- [ ] **Step 2: Add a failing IGW-client loopback test**

Reference or launch the existing IGW `igw_s7plus.dll` test client, connect anonymously, browse symbols, read one symbol, write it, and assert the simulator store changed. This test is the release gate; a TCP-only accept test is insufficient.

- [ ] **Step 3: Implement the native server lifecycle and crash boundary**

Every exported function wraps Rust work in `catch_unwind`; session tasks catch parse, transport, and client-disconnect errors. `stop` cancels the accept loop, closes sessions, and joins worker threads without throwing into WPF.

- [ ] **Step 4: Implement the IGW-required protocol subset**

Support the exact sequence used by IGW's current `s7commplus` client: COTP connect, TLS InitSsl/bootstrap or the client's validated fallback dialect, anonymous session creation/setup, program/type browse, batch get, and batch set. Symbols are registered from `SimulationTag.SymbolicAddress`; type metadata is generated from `SimulationDataType` and retained group hierarchy.

- [ ] **Step 5: Bind native callbacks to the shared store**

`SiemensPlusSimulationServer` pins delegates for its lifetime, maps symbol to tag ID once at startup, uses `SimulationValueConverter` for typed values, and writes with source `SimulationWriteSource.SiemensS7Plus`.

- [ ] **Step 6: Run lifecycle, browse, read, write, and stop tests**

Run the simulator test command and the IGW native S7 Plus FFI tests. Expected: anonymous browse/read/write succeed and repeated start/stop does not crash.

- [ ] **Step 7: Commit Task 5**

```powershell
git add YanJing.NK.Moudle/PlcSim/native/nk-s7plus-server YanJing.NK.Moudle/PlcSim/YanJing.NK.PlcSim/Protocols/ISiemensPlusSimulationServer.cs YanJing.NK.Moudle/PlcSim/YanJing.NK.PlcSim/Protocols/SiemensPlusSimulationServer.cs YanJing.NK.Moudle/PlcSim/YanJing.NK.PlcSim/YanJing.NK.PlcSim.csproj YanJing.NK.Moudle/PlcSim/YanJing.NK.PlcSim.Tests/Program.cs
git commit -m "feat: simulate S7 Plus symbolic access"
```

### Task 6: Same-port Hybrid dispatcher and automatic UI

**Files:**
- Create: `YanJing.NK.Moudle/PlcSim/YanJing.NK.PlcSim/Protocols/SiemensHybridSimulationServer.cs`
- Modify: `YanJing.NK.Moudle/PlcSim/YanJing.NK.PlcSim/MainWindow.xaml`
- Modify: `YanJing.NK.Moudle/PlcSim/YanJing.NK.PlcSim/MainWindow.xaml.cs`
- Modify: `YanJing.NK.Moudle/PlcSim/YanJing.NK.PlcSim.Tests/Program.cs`

**Interfaces:**
- Produces: `SiemensHybridSimulationServer` that owns one listener and routes COTP payloads to Classic or Plus session handlers.
- Consumes: Tasks 4 and 5 protocol handlers and Task 1 profile/evidence.

- [ ] **Step 1: Add a failing same-port test**

Start one Hybrid server, connect one Classic client and one IGW Plus client to the same port, read one address through each path, and write through each path. Assert both values reach the same store.

- [ ] **Step 2: Run tests and verify RED**

Run the simulator test command.

- [ ] **Step 3: Implement handshake dispatch**

Accept the socket once, inspect the first COTP negotiation and first data PDU without losing bytes, classify Classic S7comm (`0x32`) versus S7 Plus (`0x72`/InitSsl), and pass a buffered connection abstraction to the matching handler.

- [ ] **Step 4: Replace manual model/protocol selection in WPF**

Display:

```text
自动识别：Classic
依据：S7_Optimized_Access = FALSE；70 个绝对地址
```

or the Plus/Hybrid equivalent. The only editable Siemens field is the listen port. `启动全部协议` starts exactly the detected Siemens profile plus configured OPC UA/Omron services.

- [ ] **Step 5: Run automated UI source assertions and protocol loopbacks**

Expected: no selectable `ModelBox`; source contains the detected profile/evidence binding; Classic and Plus clients share one Hybrid port.

- [ ] **Step 6: Commit Task 6**

```powershell
git add YanJing.NK.Moudle/PlcSim/YanJing.NK.PlcSim/Protocols/SiemensHybridSimulationServer.cs YanJing.NK.Moudle/PlcSim/YanJing.NK.PlcSim/MainWindow.xaml YanJing.NK.Moudle/PlcSim/YanJing.NK.PlcSim/MainWindow.xaml.cs YanJing.NK.Moudle/PlcSim/YanJing.NK.PlcSim.Tests/Program.cs
git commit -m "feat: auto-start detected Siemens protocol"
```

### Task 7: Full interoperability and packaging verification

**Files:**
- Test/build artifacts only.

**Interfaces:**
- Verifies: import classification, protocol services, shared values, UI behavior, and portable publish.

- [ ] **Step 1: Run simulator regression tests**

Run: `dotnet run --project YanJing.NK.Moudle/PlcSim/YanJing.NK.PlcSim.Tests/YanJing.NK.PlcSim.Tests.csproj -c Release`

Expected: all import and real-protocol loopback assertions pass.

- [ ] **Step 2: Build and publish the simulator**

Run:

```powershell
dotnet publish YanJing.NK.Moudle/PlcSim/YanJing.NK.PlcSim/YanJing.NK.PlcSim.csproj -c Release -r win-x64 --self-contained true
```

Expected: native Plus server DLL and all OPC UA/HSL dependencies are present beside the executable.

- [ ] **Step 3: Verify non-optimized DB with IGW**

Import a non-optimized DB, confirm `自动识别：Classic`, connect IGW at `127.0.0.1:102`, then read and write an absolute tag.

- [ ] **Step 4: Verify optimized DB with IGW**

Import an optimized DB, confirm `自动识别：S7 Plus`, connect IGW at `127.0.0.1:102`, browse the structure, then read and write a symbolic tag.

- [ ] **Step 5: Verify cross-protocol shared values**

Write the same logical field through IGW S7 and OPC UA; confirm the simulator grid and the other protocol update immediately.

- [ ] **Step 6: Report external compatibility accurately**

Record tested IGW version, importer fixture, endpoint, read/write evidence, and process CPU/memory. If Kepware S7 Plus is unavailable for testing, state that IGW interoperability passed and Kepware interoperability remains unverified.
