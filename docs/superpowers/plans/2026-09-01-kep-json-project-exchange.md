# KEP-Compatible JSON Project Exchange Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Produce and consume one streamed JSON file whose `project` body is native KEP configuration and whose `igw` extension restores every IGW-specific setting without loss.

**Architecture:** `IGW.ProjectExchange` streams KEP and IGW sections directly between JSON and SQLite staging stores. Protocol-specific KEP codecs implement a shared abstraction in their corresponding driver assemblies; raw KEP property bags preserve valid properties IGW does not yet understand. A probe tool performs real KEP import/export comparison before compatibility is claimed.

**Tech Stack:** .NET 10, System.Text.Json streaming APIs, AES-GCM, PBKDF2-SHA256, xUnit, KEP Configuration API

**Spec:** `docs/superpowers/specs/2026-09-01-sqlite-project-store-kep-json-design.md`

## Global Constraints

- This plan begins only after `2026-09-01-sqlite-project-storage.md` is complete.
- The exported root contains `project` and `igw`; the UI does not expose a separate “Export KEP” command.
- `project` contains only KEP-native fields; IGW-only values belong under `igw`.
- Pure KEP JSON without `igw` remains importable.
- IGW export/import must preserve all settings, stable IDs, arrays, nested arrays, MES graphs, templates, write confirmation, lengths, permissions, and credentials.
- Unsupported or unsafe KEP mappings produce explicit compatibility errors; no similar driver or default value may be substituted silently.
- Real KEP compatibility is reported only after target-version round-trip verification.
- No production KEP configuration is replaced without explicit authorization and a verified backup.
- The pre-existing `installer/version.txt` change is user-owned and must not be staged.

## File and Module Map

- `src/IGW.Abstractions/ProjectExchangeContracts.cs`: exchange DTOs and `IKepDriverCodec`.
- `src/IGW.ProjectExchange/ProjectJsonExporter.cs`: streamed `{project,igw}` writer.
- `src/IGW.ProjectExchange/ProjectJsonInspector.cs`: bounded preflight parser.
- `src/IGW.ProjectExchange/ProjectJsonImporter.cs`: streamed import into a staging database.
- `src/IGW.ProjectExchange/Kep/KepProjectWriter.cs`: common KEP hierarchy writer.
- `src/IGW.ProjectExchange/Kep/KepProjectReader.cs`: common KEP hierarchy reader.
- `src/IGW.ProjectExchange/Igw/IgwExtensionWriter.cs`: full IGW sidecar writer.
- `src/IGW.ProjectExchange/Igw/IgwExtensionReader.cs`: ID/path/fingerprint merge.
- `src/IGW.ProjectExchange/Security/PortableSecretEnvelope.cs`: password-encrypted portable secrets.
- Driver `Kep/*KepDriverCodec.cs` files: protocol property mapping only; no PLC I/O.
- `src/IGW.Northbound.RestApi/ProjectExchangeApi.cs`: inspect/import/export HTTP endpoints.
- `tools/IGW.KepProjectProbe`: real KEP load/export/compare tool.

---

### Task 1: Define Exchange Contracts and Compatibility Diagnostics

**Files:**
- Create: `src/IGW.Abstractions/ProjectExchangeContracts.cs`
- Create: `src/IGW.ProjectExchange/IGW.ProjectExchange.csproj`
- Create: `tests/IGW.Tests/ProjectExchangeContractTests.cs`
- Modify: `IGW.slnx`
- Modify: `tests/IGW.Tests/IGW.Tests.csproj`

**Interfaces:**
- Produces: `IProjectExchangeService`, `IKepDriverCodec`, `KepCompatibilityIssue`, `ProjectImportPreview`, `ProjectImportOptions`, `ProjectExportOptions`.
- Consumes: `IProjectRepository`, `ChannelDef`, `DeviceDef`, `Utf8JsonReader`, `Utf8JsonWriter`.

- [ ] **Step 1: Write failing contract tests**

```csharp
[Fact]
public void Compatibility_is_failed_by_connection_changing_errors()
{
    var report = new KepCompatibilityReport([
        new("PLC1", "driver_not_equivalent", KepIssueSeverity.Error,
            "No target KEP driver mapping")
    ]);
    Assert.False(report.CanImportIntoKep);
}

[Fact]
public void Export_options_require_password_when_portable_secrets_exist()
{
    var options = new ProjectExportOptions(true, null);
    Assert.Throws<ArgumentException>(() => options.Validate(hasPortableSecrets: true));
}
```

- [ ] **Step 2: Run and confirm compilation failure**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~ProjectExchangeContractTests --no-restore`

- [ ] **Step 3: Define the shared contracts**

```csharp
public enum KepIssueSeverity { Info, Warning, Error }
public sealed record KepCompatibilityIssue(string Path, string Code, KepIssueSeverity Severity, string Message);
public sealed record KepCompatibilityReport(IReadOnlyList<KepCompatibilityIssue> Issues)
{
    public bool CanImportIntoKep => Issues.All(x => x.Severity != KepIssueSeverity.Error);
}
public sealed record ProjectExportOptions(bool IncludePortableSecrets, string? MigrationPassword)
{
    public void Validate(bool hasPortableSecrets)
    {
        if (IncludePortableSecrets && hasPortableSecrets && string.IsNullOrEmpty(MigrationPassword))
            throw new ArgumentException("migration_password_required", nameof(MigrationPassword));
    }
}
public sealed record ProjectImportOptions(string Token, string FileHash, string? MigrationPassword);
public sealed record ProjectImportPreview(
    string Token, string FileHash, long Channels, long Devices, long Groups, long Tags,
    long MesTasks, bool HasPortableSecrets, KepCompatibilityReport Compatibility);
public sealed record ProjectImportResult(
    bool Applied, ProjectRevision Revision, string? ErrorCode, KepCompatibilityReport Compatibility);

public interface IKepDriverCodec
{
    string IgwDriverId { get; }
    IReadOnlyCollection<string> KepDriverNames { get; }
    KepCompatibilityReport Inspect(ChannelDef channel);
    void WriteChannelProperties(Utf8JsonWriter writer, ChannelDef channel);
    void WriteDeviceProperties(Utf8JsonWriter writer, ChannelDef channel, DeviceDef device);
    void ReadChannelProperties(JsonElement source, ChannelDef target, IDictionary<string, JsonElement> unknown);
    void ReadDeviceProperties(JsonElement source, DeviceDef target, IDictionary<string, JsonElement> unknown);
}

public interface IProjectExchangeService
{
    Task<ProjectImportPreview> InspectAsync(Stream input, CancellationToken cancellationToken);
    Task<ProjectImportResult> ImportAsync(Stream input, ProjectImportOptions options, CancellationToken cancellationToken);
    Task ExportAsync(Stream output, ProjectExportOptions options, CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Add project references**

`IGW.ProjectExchange` references `IGW.Abstractions`, `IGW.Core`, and `IGW.Persistence.Sqlite`. Add it to `IGW.slnx`, `IGW.Runtime`, and the test project.

- [ ] **Step 5: Run tests and commit**

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~ProjectExchangeContractTests
git add IGW.slnx src/IGW.Abstractions/ProjectExchangeContracts.cs src/IGW.ProjectExchange tests/IGW.Tests
git commit -m "feat: add project exchange contracts"
```

### Task 2: Implement the Common KEP Hierarchy Codec

**Files:**
- Create: `src/IGW.ProjectExchange/Kep/KepProjectWriter.cs`
- Create: `src/IGW.ProjectExchange/Kep/KepProjectReader.cs`
- Create: `src/IGW.ProjectExchange/Kep/KepTypeMap.cs`
- Create: `src/IGW.ProjectExchange/Kep/KepRawPropertyBag.cs`
- Create: `tests/IGW.Tests/KepProjectCodecTests.cs`
- Create: `tests/IGW.Tests/Fixtures/Kep/common-nested-project.json`

**Interfaces:**
- Consumes: repository-streamed channels/devices/groups/tags and `IKepDriverCodec` registry.
- Produces: native `project` JSON and imported connection hierarchy with raw property bags.

- [ ] **Step 1: Add failing native-shape tests**

Load `common-nested-project.json`, import it, and assert nested groups, tag addresses, type codes 0–34, access, scan rate, descriptions, and unknown `servermain.*` fields. Export it again and assert unknown valid properties remain value-equivalent after JSON normalization.

```csharp
JsonElement project = await ExportProjectElementAsync(imported);
Assert.Equal("PLC8", project.GetProperty("channels")[0]
    .GetProperty("common.ALLTYPES_NAME").GetString());
Assert.False(project.GetProperty("channels")[0].TryGetProperty("igw", out _));
```

- [ ] **Step 2: Run and confirm failure**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~KepProjectCodecTests`

- [ ] **Step 3: Implement streaming KEP write**

Write KEP fields in deterministic order: project common fields, `channels`, channel common/driver fields, `devices`, device fields, `tags`, and recursive `tag_groups`. Common tag mapping is:

```csharp
writer.WriteString("common.ALLTYPES_NAME", tag.Name);
writer.WriteString("common.ALLTYPES_DESCRIPTION", tag.Description ?? "");
writer.WriteString("servermain.TAG_ADDRESS", tag.Address);
writer.WriteNumber("servermain.TAG_DATA_TYPE", KepTypeMap.ToCode(tag.DataType, tag.IsArray));
writer.WriteNumber("servermain.TAG_READ_WRITE_ACCESS", tag.Access == TagAccess.Read ? 0 : 1);
writer.WriteNumber("servermain.TAG_SCAN_RATE_MILLISECONDS", KepScanRate.Normalize(tag.ScanRateMs));
```

After known fields, write preserved raw properties except reserved hierarchy/property names. Duplicate or conflicting raw names produce a path-level diagnostic.

- [ ] **Step 4: Implement bounded KEP read**

Use `Utf8JsonReader` for the outer hierarchy and clone only one channel/device/tag property object at a time for codec dispatch. Enforce configured maximum depth, count, and UTF-8 byte limits. Generate stable IDs when no IGW extension exists and preserve unknown namespaced properties in `kep_properties_json`.

- [ ] **Step 5: Run codec tests and commit**

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~KepProjectCodecTests
git add src/IGW.ProjectExchange/Kep tests/IGW.Tests/KepProjectCodecTests.cs tests/IGW.Tests/Fixtures/Kep/common-nested-project.json
git commit -m "feat: add native kep hierarchy codec"
```

### Task 3: Implement the Lossless IGW Extension

**Files:**
- Create: `src/IGW.ProjectExchange/Igw/IgwExtensionWriter.cs`
- Create: `src/IGW.ProjectExchange/Igw/IgwExtensionReader.cs`
- Create: `src/IGW.ProjectExchange/Igw/IgwEntityMatcher.cs`
- Create: `tests/IGW.Tests/IgwExtensionRoundTripTests.cs`

**Interfaces:**
- Consumes: complete repository data and KEP-imported staging hierarchy.
- Produces: schema version 4 `igw` document and deterministic merge result.

- [ ] **Step 1: Write a failing all-fields round-trip test**

Use the full storage fixture and duplicate leaf names in different groups. Export `{project,igw}`, import it into an empty staging repository, and compare normalized runtime snapshots. Assert IDs for an ordinary tag and all parent entities remain unchanged.

```csharp
Assert.Equal(expected.Channels[0].Id, actual.Channels[0].Id);
Assert.Equal(expected.Channels[0].Devices[0].Groups[0].Id,
             actual.Channels[0].Devices[0].Groups[0].Id);
Assert.Equal(expectedTag.Id, actualTag.Id);
Assert.Equal(ProjectStore.ToJson(expected.MesUpload), ProjectStore.ToJson(actual.MesUpload));
```

- [ ] **Step 2: Add an ambiguity failure test**

Construct an extension without a stable ID whose saved path matches two normalized candidates. Assert import stops with `igw_match_ambiguous`, lists both complete paths, and does not create a final database.

- [ ] **Step 3: Run and confirm failure**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~IgwExtensionRoundTripTests`

- [ ] **Step 4: Implement schema version 4 writer**

Always emit stable identity and extensions for every channel, device, group, and tag. Tag extension records contain ID, canonical path, structural fingerprint, array, metadata, string length, encoding, scaling, deadband, alarm, engineering unit, write confirmation, IGW raw properties, and KEP raw properties. Write project sections, templates, MES tasks/graphs, task enablement, flow availability, and runtime switches exactly once.

- [ ] **Step 5: Implement merge precedence**

Match in this exact order: stable ID, canonical path, structural fingerprint. A candidate is accepted only when unique. Compare duplicated KEP/IGW values such as address, data type, and access; mismatch produces `kep_igw_conflict` and blocks import rather than choosing a side.

- [ ] **Step 6: Run round-trip tests and commit**

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~IgwExtensionRoundTripTests
git add src/IGW.ProjectExchange/Igw tests/IGW.Tests/IgwExtensionRoundTripTests.cs
git commit -m "feat: preserve complete igw project extensions"
```

### Task 4: Add Portable Encrypted Secrets

**Files:**
- Create: `src/IGW.ProjectExchange/Security/PortableSecretEnvelope.cs`
- Create: `src/IGW.ProjectExchange/Security/PortableSecretCollector.cs`
- Create: `tests/IGW.Tests/PortableSecretEnvelopeTests.cs`

**Interfaces:**
- Consumes: migration password and reversible secret entries.
- Produces: versioned `igw.secrets` AES-GCM envelope.

- [ ] **Step 1: Write failing cryptographic tests**

Test correct-password round-trip, wrong-password authentication failure, unique salt/nonce across two exports, no plaintext marker, and no password requirement when no reversible secrets exist.

```csharp
byte[] first = PortableSecretEnvelope.Encrypt(secrets, "migration-pass");
byte[] second = PortableSecretEnvelope.Encrypt(secrets, "migration-pass");
Assert.NotEqual(first, second);
Assert.Equal(secrets, PortableSecretEnvelope.Decrypt(first, "migration-pass"));
Assert.Throws<CryptographicException>(() =>
    PortableSecretEnvelope.Decrypt(first, "wrong-pass"));
```

- [ ] **Step 2: Run and confirm failure**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~PortableSecretEnvelopeTests`

- [ ] **Step 3: Implement the envelope**

Use a random 16-byte salt, PBKDF2-SHA256 with 310,000 iterations and a 32-byte key, a random 12-byte nonce, AES-GCM with a 16-byte tag, and associated data `IGW portable project secrets v1`. Serialize only secret keys and values inside the authenticated ciphertext. Zero derived keys and plaintext buffers in `finally`.

The JSON metadata is:

```json
{
  "version": 1,
  "kdf": "pbkdf2-sha256",
  "iterations": 310000,
  "salt": "base64",
  "cipher": "aes-256-gcm",
  "nonce": "base64",
  "tag": "base64",
  "ciphertext": "base64"
}
```

- [ ] **Step 4: Run crypto and existing secret tests**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~PortableSecretEnvelopeTests|FullyQualifiedName~InstallationSecretProtectorTests|FullyQualifiedName~ProjectSecurityTests"`

- [ ] **Step 5: Commit**

```powershell
git add src/IGW.ProjectExchange/Security tests/IGW.Tests/PortableSecretEnvelopeTests.cs
git commit -m "feat: encrypt portable project secrets"
```

### Task 5: Implement Protocol-Specific KEP Codecs

**Files:**
- Create: `src/IGW.Drivers.Simulator/Kep/SimulatorKepDriverCodec.cs`
- Create: `src/IGW.Drivers.IoTClient/Kep/SiemensS7KepDriverCodec.cs`
- Create: `src/IGW.Drivers.IoTClient/Kep/ModbusTcpKepDriverCodec.cs`
- Create: `src/IGW.Drivers.IoTClient/Kep/OmronFinsKepDriverCodec.cs`
- Create: `src/IGW.Drivers.IoTClient/Kep/AllenBradleyKepDriverCodec.cs`
- Create: `src/IGW.Drivers.Mitsubishi/Kep/MitsubishiKepDriverCodec.cs`
- Create: `src/IGW.Drivers.EtherNetIP/Kep/OmronCipKepDriverCodec.cs`
- Create: `src/IGW.Drivers.OpcUaClient/Kep/OpcUaClientKepDriverCodec.cs`
- Create: `src/IGW.Drivers.Mqtt/Kep/MqttKepDriverCodec.cs`
- Create: `tests/IGW.Tests/KepDriverCodecTests.cs`
- Create: `tests/IGW.Tests/Fixtures/Kep/Drivers/*.json`
- Modify: `src/IGW.Runtime/Program.cs`
- Modify: `src/IGW.Runtime/IGW.Runtime.csproj`

**Interfaces:**
- Implements: `IKepDriverCodec` from Task 1.
- Produces: one mapping and compatibility report per IGW protocol.

- [ ] **Step 1: Add sanitized golden fixtures and failing tests**

For each protocol, use a JSON fragment exported by the target KEP version with nondefault channel/device settings. Test KEP fixture → IGW settings → KEP output. Compare driver identifier, host/port, model/unit/rack/slot, timeouts, addressing mode, security policy, and every fixture property the codec claims to support.

```csharp
[Theory]
[MemberData(nameof(DriverFixtures))]
public void Driver_fixture_round_trips_without_claimed_property_loss(string driverId, string fixture)
{
    DriverRoundTrip result = Harness.RoundTrip(driverId, fixture);
    Assert.Empty(result.Report.Issues.Where(x => x.Severity == KepIssueSeverity.Error));
    Assert.Equal(result.ExpectedKnownProperties, result.ActualKnownProperties);
}
```

- [ ] **Step 2: Run and confirm failure**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~KepDriverCodecTests`

- [ ] **Step 3: Implement equivalent codecs**

Each codec owns only deterministic property translation and diagnostics. It does not open sockets or call the PLC driver. Preserve unrecognized namespaced properties in raw bags. A setting that changes endpoint/protocol semantics and has no verified KEP equivalent returns an Error diagnostic.

- [ ] **Step 4: Mark non-equivalent protocols explicitly**

`MqttKepDriverCodec` and any protocol with no matching KEP channel driver retain IGW settings but return `driver_not_supported_by_kep` as Error. The exporter still produces an IGW-restorable file, while `compatibility.canImportIntoKep` remains false.

- [ ] **Step 5: Register codecs**

In `Program.cs`, build the codec registry alongside the driver registry and reject duplicate `IgwDriverId` or KEP display names at startup. Registration order must not select a codec. Add the Simulator project reference needed to register `SimulatorKepDriverCodec`.

- [ ] **Step 6: Run driver tests and commit**

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~KepDriverCodecTests
git add src/IGW.Drivers.Simulator src/IGW.Drivers.IoTClient src/IGW.Drivers.Mitsubishi src/IGW.Drivers.EtherNetIP src/IGW.Drivers.OpcUaClient src/IGW.Drivers.Mqtt src/IGW.Runtime/Program.cs tests/IGW.Tests/KepDriverCodecTests.cs tests/IGW.Tests/Fixtures/Kep/Drivers
git commit -m "feat: map igw protocols to native kep properties"
```

### Task 6: Stream Export Without Materializing the Full Project

**Files:**
- Create: `src/IGW.ProjectExchange/ProjectJsonExporter.cs`
- Create: `tests/IGW.Tests/ProjectJsonStreamingExportTests.cs`

**Interfaces:**
- Consumes: read-only repository snapshot, codec registry, optional migration password.
- Produces: deterministic UTF-8 `{project,igw}` stream and compatibility summary.

- [ ] **Step 1: Write a failing bounded-memory export test**

Populate 250,000 tags, export through a stream wrapper that rejects writes larger than 128 KiB, and assert valid root keys, point count, no plaintext secret, and no call to `LoadRuntimeProjectAsync`.

- [ ] **Step 2: Run and confirm failure**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~ProjectJsonStreamingExportTests`

- [ ] **Step 3: Implement snapshot-consistent streaming**

Open one read transaction at a fixed revision. Use `Utf8JsonWriter` directly on the output stream and repository async enumerators for channels, devices, groups, tags, sections, tasks, and templates. Flush in bounded batches. Write compatibility issues after inspecting all channels; do not build an in-memory list of tags or a full JSON string.

```csharp
await using IProjectReadSnapshot snapshot = await repository.OpenReadSnapshotAsync(ct);
await using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
writer.WriteStartObject();
writer.WritePropertyName("project");
await kepWriter.WriteAsync(writer, snapshot, ct);
writer.WritePropertyName("igw");
await igwWriter.WriteAsync(writer, snapshot, options, ct);
writer.WriteEndObject();
await writer.FlushAsync(ct);
```

- [ ] **Step 4: Run tests and commit**

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~ProjectJsonStreamingExportTests
git add src/IGW.ProjectExchange/ProjectJsonExporter.cs tests/IGW.Tests/ProjectJsonStreamingExportTests.cs
git commit -m "feat: stream kep compatible project exports"
```

### Task 7: Stream Inspect and Import Through a Staging Database

**Files:**
- Create: `src/IGW.ProjectExchange/ProjectJsonInspector.cs`
- Create: `src/IGW.ProjectExchange/ProjectJsonImporter.cs`
- Create: `src/IGW.ProjectExchange/ProjectImportSessionStore.cs`
- Create: `tests/IGW.Tests/ProjectJsonImportTests.cs`

**Interfaces:**
- Consumes: upload stream, import options, repository database switcher.
- Produces: hash-bound preview token and atomic project replacement.

- [ ] **Step 1: Write failing pure-KEP and IGW import tests**

Test pure KEP import assigns IDs/default IGW settings; IGW round-trip retains IDs and all extensions; wrong password, duplicated paths, dangling MES references, malformed arrays, and KEP/IGW conflicts fail before final database replacement.

- [ ] **Step 2: Write a failing preview-token test**

Inspect file A, then attempt commit with file B and A’s token. Assert `import_file_changed` and that the live database revision and runtime generation do not change.

```csharp
ProjectImportPreview preview = await service.InspectAsync(fileA, default);
ProjectImportResult result = await service.ImportAsync(fileB,
    new(preview.Token, preview.FileHash, "migration-pass"), default);
Assert.False(result.Applied);
Assert.Equal("import_file_changed", result.ErrorCode);
```

- [ ] **Step 3: Run and confirm failure**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~ProjectJsonImportTests`

- [ ] **Step 4: Implement bounded inspect/import**

Copy uploads to a size-limited temporary file while calculating SHA-256. Stream-parse into a staging SQLite database, validate counts and references, and store a preview session containing hash, staging path, counts, issues, and expiry. On commit, compare token/hash, checkpoint and close staging, enter the configuration coordinator gate, back up and replace the live database, reopen the repository, load/validate the runtime snapshot, publish a new generation, and delete the session.

- [ ] **Step 5: Prove rollback after cutover failure**

Inject a failure while reopening the replacement database. Assert the prior database is restored, the prior runtime snapshot remains active, the failed staging file is quarantined with diagnostics, and no partial new revision is reported.

- [ ] **Step 6: Run tests and commit**

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~ProjectJsonImportTests
git add src/IGW.ProjectExchange tests/IGW.Tests/ProjectJsonImportTests.cs
git commit -m "feat: import project json through atomic staging"
```

### Task 8: Expose Import, Preview, and Export APIs

**Files:**
- Create: `src/IGW.Northbound.RestApi/ProjectExchangeApi.cs`
- Create: `tests/IGW.Tests/ProjectExchangeApiTests.cs`
- Modify: `src/IGW.Northbound.RestApi/IgwRestApi.cs`
- Modify: `src/IGW.Core/IGatewayHost.cs`

**Interfaces:**
- Produces: `POST /api/v1/project/import/inspect`, `POST /api/v1/project/import/commit`, `GET /api/v1/project/export`.

- [ ] **Step 1: Write failing HTTP tests**

Assert inspect accepts multipart JSON, returns counts/issues/token without changing revision; commit changes revision once; export streams `application/json` with a sanitized filename; a secret-bearing export without password returns 400 `migration_password_required`.

- [ ] **Step 2: Run and confirm 404/failure**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~ProjectExchangeApiTests`

- [ ] **Step 3: Map bounded endpoints**

Disable response buffering for export, set explicit upload size/depth/count limits for import, propagate request cancellation, and return structured issue arrays. Import/export management endpoints keep existing project-management authorization behavior and are excluded from the public PLC Swagger contract; `/Tag` and `/iotgateway` policies remain unchanged.

```csharp
group.MapPost("/import/inspect", InspectProjectAsync);
group.MapPost("/import/commit", CommitProjectImportAsync);
group.MapGet("/export", ExportProjectAsync);
```

- [ ] **Step 4: Run API tests and commit**

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~ProjectExchangeApiTests|FullyQualifiedName~PublicPlcApiPolicyTests"
git add src/IGW.Northbound.RestApi src/IGW.Core/IGatewayHost.cs tests/IGW.Tests/ProjectExchangeApiTests.cs
git commit -m "feat: expose streamed project json exchange api"
```

### Task 9: Build the Real KEP Round-Trip Probe

**Files:**
- Create: `tools/IGW.KepProjectProbe/IGW.KepProjectProbe.csproj`
- Create: `tools/IGW.KepProjectProbe/Program.cs`
- Create: `tools/IGW.KepProjectProbe/KepConfigurationClient.cs`
- Create: `tools/IGW.KepProjectProbe/KepProjectComparer.cs`
- Create: `tests/IGW.Tests/KepProjectComparerTests.cs`
- Modify: `IGW.slnx`

**Interfaces:**
- Consumes: exported JSON, test KEP base URL/credentials, explicit backup path.
- Produces: backup, re-exported KEP JSON, JSON comparison report, exit code.

- [ ] **Step 1: Write failing offline comparer tests**

Compare two KEP projects and assert differences include exact paths for driver, channel/device settings, missing groups/tags, address, type, access, and scan rate. Ordering differences alone must not fail.

- [ ] **Step 2: Run and confirm failure**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~KepProjectComparerTests`

- [ ] **Step 3: Implement the guarded probe workflow**

Require `--input`, `--base-url`, `--backup`, `--output`, and literal `--allow-project-replace`. First export the current test KEP project into the backup path and verify it parses. Then call `PUT /config/v1/project/services/JsonProjectLoad`, collect status/errors, re-export KEP, compare normalized topology/properties, and write a report containing KEP version, timestamps, input hash, acceptance status, differences, and backup path. Never print credentials.

- [ ] **Step 4: Add dry-run mode**

Without `--allow-project-replace`, validate input and connectivity but stop before import with exit code 2 and a clear message. This is the default.

- [ ] **Step 5: Run comparer tests and build the tool**

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~KepProjectComparerTests
dotnet build tools/IGW.KepProjectProbe/IGW.KepProjectProbe.csproj -c Release
```

- [ ] **Step 6: Commit**

```powershell
git add IGW.slnx tools/IGW.KepProjectProbe tests/IGW.Tests/KepProjectComparerTests.cs
git commit -m "feat: add guarded kep project round trip probe"
```

### Task 10: Run Exchange Completion Gates

**Files:**
- Create: `docs/manual/project-json-migration.md`

- [ ] **Step 1: Run focused exchange tests**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~Kep|FullyQualifiedName~IgwExtension|FullyQualifiedName~ProjectJson|FullyQualifiedName~PortableSecret|FullyQualifiedName~ProjectExchange"`

Expected: all selected tests pass.

- [ ] **Step 2: Run full backend verification**

Run: `dotnet test IGW.slnx -c Release --no-restore --verbosity minimal`

Expected: all discovered tests pass.

- [ ] **Step 3: Execute a local one-million-point IGW round-trip**

Export the benchmark database to JSON, import it into a fresh staging database, and compare entity counts, stable IDs, settings hashes, MES documents, and secret presence. Record elapsed time, peak memory, and file bytes.

- [ ] **Step 4: Document the evidence boundary**

In `docs/manual/project-json-migration.md`, distinguish local schema/golden tests, IGW million-point round-trip, target KEP round-trip, simulator evidence, and real PLC evidence. Do not mark target KEP compatibility as passed until the guarded probe is run against the actual version.

- [ ] **Step 5: Commit documentation**

```powershell
git add docs/manual/project-json-migration.md
git commit -m "docs: document kep compatible project migration"
```

## Exchange Plan Exit Criteria

- The server streams one `{project,igw}` JSON without loading one million points into one string or browser object.
- Pure KEP input imports successfully; IGW export/import is lossless for every configured field.
- Each supported KEP protocol has golden-fixture coverage and explicit unsupported diagnostics.
- Portable credentials are authenticated, encrypted, and never present in plaintext.
- Import preview and atomic commit cannot replace the live database with unvalidated or changed content.
- Direct-import compatibility is stated only when the real target KEP probe passes.
