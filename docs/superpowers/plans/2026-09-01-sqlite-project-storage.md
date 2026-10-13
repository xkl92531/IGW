# SQLite Project Storage Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace full-project JSON persistence with a revisioned SQLite repository that supports lossless runtime snapshots, local mutations, safe legacy migration, and million-point paged queries.

**Architecture:** Stable repository and query contracts live in `IGW.Abstractions`; `IGW.Persistence.Sqlite` owns schema, migrations, entity mapping, encrypted secrets, and staging database switching. Runtime keeps an immutable `IgwProject` snapshot for PLC execution but every persisted mutation goes through `ProjectConfigurationCoordinator`, so SQLite is the only source of truth.

**Tech Stack:** .NET 10, C# 13, Microsoft.Data.Sqlite 10.0.11, System.Text.Json, xUnit 2.9.3

**Spec:** `docs/superpowers/specs/2026-09-01-sqlite-project-store-kep-json-design.md`

## Global Constraints

- SQLite is the only project primary store; no automatic JSON/SQLite dual write is allowed.
- `/Tag` and `/iotgateway` remain anonymous internal-network HTTP APIs; this work does not add authentication or HTTPS.
- Existing PLC scan, subscription, write, write-verification, retry, and connection-fault behavior must not change.
- `.igwproj` is accepted only by the one-time legacy migration path and is never written by the new repository.
- All entity identity uses stable IDs; canonical paths are display/search and migration aids, not primary keys.
- Million-point configuration reads are paged or streamed and must not serialize the complete project for ordinary edits.
- The pre-existing `installer/version.txt` change is user-owned and must not be staged by these tasks.

## File and Module Map

- `src/IGW.Abstractions/ProjectStorageContracts.cs`: repository, query, command, revision, and migration contracts.
- `src/IGW.Persistence.Sqlite/ProjectDatabase.cs`: connection creation and SQLite pragmas.
- `src/IGW.Persistence.Sqlite/Schema/001_initial.sql`: complete version-1 project schema.
- `src/IGW.Persistence.Sqlite/ProjectSchemaMigrator.cs`: ordered schema migration runner.
- `src/IGW.Persistence.Sqlite/SqliteProjectRepository.cs`: public repository façade and transactions.
- `src/IGW.Persistence.Sqlite/ProjectSnapshotReader.cs`: SQLite rows to immutable `IgwProject` runtime snapshot.
- `src/IGW.Persistence.Sqlite/ProjectSnapshotWriter.cs`: full snapshot/staging writer.
- `src/IGW.Persistence.Sqlite/ProjectEntityMapper.cs`: channel/device/group/tag row mapping only.
- `src/IGW.Persistence.Sqlite/ProjectSecretVault.cs`: at-rest secret extraction and restoration.
- `src/IGW.Persistence.Sqlite/LegacyProjectMigrator.cs`: one-time JSON/`.igwproj` migration and backup.
- `src/IGW.Core/ProjectConfigurationCoordinator.cs`: revision gate, validation, persistence, and runtime apply orchestration.
- `src/IGW.Northbound.RestApi/ProjectConfigurationApi.cs`: paged management endpoints and deprecated full-project bridge.
- `benchmarks/IGW.ProjectStorage.Performance`: million-point storage/query benchmark.

---

### Task 1: Add Repository Contracts and SQLite Project

**Files:**
- Create: `src/IGW.Abstractions/ProjectStorageContracts.cs`
- Create: `src/IGW.Persistence.Sqlite/IGW.Persistence.Sqlite.csproj`
- Create: `tests/IGW.Tests/ProjectStorageContractTests.cs`
- Modify: `IGW.slnx`
- Modify: `tests/IGW.Tests/IGW.Tests.csproj`

**Interfaces:**
- Produces: `ProjectRevision`, `ProjectNodeRef`, `ProjectTreeNode`, `ProjectTagRow`, `ProjectTagQuery`, `ProjectPage<T>`, `ProjectMutation`, `ProjectMutationResult`, `IProjectRepository`.
- Consumes: existing `IgwProject`, `TagDef`, `MesTask`, and `ProjectStore.ValidationReport` semantics.

- [ ] **Step 1: Write the failing contract tests**

```csharp
using IGW.Abstractions;

namespace IGW.Tests;

public sealed class ProjectStorageContractTests
{
    [Fact]
    public void Tag_query_rejects_unbounded_pages()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ProjectTagQuery("group-1", null, 0, 10_001).Validate());
    }

    [Fact]
    public void Revision_etag_is_stable_and_quoted()
    {
        Assert.Equal("\"project-42\"", new ProjectRevision(42).ETag);
    }
}
```

- [ ] **Step 2: Run the tests and verify the contracts do not exist**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~ProjectStorageContractTests --no-restore`

Expected: compilation fails because `ProjectTagQuery` and `ProjectRevision` are undefined.

- [ ] **Step 3: Define the contracts**

```csharp
namespace IGW.Abstractions;

public readonly record struct ProjectRevision(long Value)
{
    public string ETag => $"\"project-{Value}\"";
}

public enum ProjectNodeKind { Project, Channel, Device, Group, Tag }
public sealed record ProjectNodeRef(ProjectNodeKind Kind, string Id);
public sealed record ProjectTreeNode(
    string Id, ProjectNodeKind Kind, string Name, string? Description,
    string ActualPath, bool HasChildren, int DirectTagCount, long RowVersion);
public sealed record ProjectTagRow(
    string Id, string Name, string ActualPath, string Address, IgwDataType DataType,
    TagAccess Access, int ScanRateMs, string? Description, long RowVersion);
public sealed record ProjectTagQuery(string? GroupId, string? Search, int Offset, int Limit)
{
    public ProjectTagQuery Validate()
    {
        if (Offset < 0) throw new ArgumentOutOfRangeException(nameof(Offset));
        if (Limit is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(Limit));
        return this;
    }
}
public sealed record ProjectPage<T>(IReadOnlyList<T> Items, long Total, int Offset, int Limit);
public sealed record ProjectEntityDocument(
    ProjectNodeRef Entity, string? ParentId, int SortOrder, string DocumentJson);
public enum ProjectDocumentKind { ProjectSection, MesTask, FlowTemplate, DeviceTemplate }
public sealed record ProjectDocument(ProjectDocumentKind Kind, string Key, string DocumentJson, long RowVersion);

public enum ProjectMutationKind { UpsertEntity, DeleteEntities, MoveEntities, SetEnabled, ReplaceMesTask }
public sealed record ProjectMutation(
    ProjectMutationKind Kind, ProjectNodeKind EntityKind,
    IReadOnlyList<string> EntityIds, string? TargetId, string? DocumentJson);
public sealed record ProjectMutationResult(bool Applied, bool Conflict, ProjectRevision Revision, string? Error);

public interface IProjectReadSnapshot : IAsyncDisposable
{
    ProjectRevision Revision { get; }
    IAsyncEnumerable<ProjectEntityDocument> StreamEntitiesAsync(
        ProjectNodeKind kind, string? parentId, CancellationToken cancellationToken);
    IAsyncEnumerable<ProjectDocument> StreamDocumentsAsync(
        ProjectDocumentKind kind, CancellationToken cancellationToken);
}

public interface IProjectRepository : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<ProjectRevision> GetRevisionAsync(CancellationToken cancellationToken);
    Task<ProjectTreeNode> GetRootAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ProjectTreeNode>> GetChildrenAsync(ProjectNodeRef parent, CancellationToken cancellationToken);
    Task<ProjectPage<ProjectTagRow>> GetTagsAsync(ProjectTagQuery query, CancellationToken cancellationToken);
    Task<string> GetEntityDocumentAsync(ProjectNodeRef entity, CancellationToken cancellationToken);
    Task<ProjectMutationResult> ExecuteAsync(ProjectMutation mutation, ProjectRevision expected, CancellationToken cancellationToken);
    Task<IgwProject> LoadRuntimeProjectAsync(CancellationToken cancellationToken);
    Task<ProjectRevision> ReplaceFromSnapshotAsync(IgwProject project, ProjectRevision expected, CancellationToken cancellationToken);
    Task<IProjectReadSnapshot> OpenReadSnapshotAsync(CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Create the project and references**

Create `IGW.Persistence.Sqlite.csproj` targeting `net10.0`, referencing `IGW.Abstractions` and `IGW.Core`, and referencing `Microsoft.Data.Sqlite` version `10.0.11`. Add it to `IGW.slnx` and add a test project reference.

- [ ] **Step 5: Run the contract tests**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~ProjectStorageContractTests`

Expected: 2 tests pass.

- [ ] **Step 6: Commit**

```powershell
git add IGW.slnx src/IGW.Abstractions/ProjectStorageContracts.cs src/IGW.Persistence.Sqlite tests/IGW.Tests/IGW.Tests.csproj tests/IGW.Tests/ProjectStorageContractTests.cs
git commit -m "feat: add project repository contracts"
```

### Task 2: Create the Versioned SQLite Schema

**Files:**
- Create: `src/IGW.Persistence.Sqlite/ProjectDatabase.cs`
- Create: `src/IGW.Persistence.Sqlite/ProjectSchemaMigrator.cs`
- Create: `src/IGW.Persistence.Sqlite/Schema/001_initial.sql`
- Create: `tests/IGW.Tests/ProjectDatabaseSchemaTests.cs`
- Modify: `src/IGW.Persistence.Sqlite/IGW.Persistence.Sqlite.csproj`

**Interfaces:**
- Consumes: database path and cancellation token.
- Produces: `ProjectDatabase.OpenAsync()`, `ProjectSchemaMigrator.MigrateAsync()`, schema version 1.

- [ ] **Step 1: Write a failing schema test**

```csharp
[Fact]
public async Task New_database_uses_wal_foreign_keys_and_all_required_tables()
{
    await using var fixture = await ProjectDbFixture.CreateAsync();
    await using var connection = await fixture.Database.OpenAsync(default);
    Assert.Equal("wal", await ScalarAsync<string>(connection, "PRAGMA journal_mode;"));
    Assert.Equal(1L, await ScalarAsync<long>(connection, "PRAGMA foreign_keys;"));
    string[] tables = await ReadNamesAsync(connection,
        "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;");
    Assert.Contains("project_meta", tables);
    Assert.Contains("channels", tables);
    Assert.Contains("devices", tables);
    Assert.Contains("tag_groups", tables);
    Assert.Contains("tags", tables);
    Assert.Contains("tag_options", tables);
    Assert.Contains("project_sections", tables);
    Assert.Contains("mes_tasks", tables);
    Assert.Contains("mes_task_index", tables);
    Assert.Contains("flow_templates", tables);
    Assert.Contains("device_templates", tables);
    Assert.Contains("secret_entries", tables);
}
```

- [ ] **Step 2: Run the test and verify it fails**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~ProjectDatabaseSchemaTests`

Expected: fails because `ProjectDatabase` and the fixture do not exist.

- [ ] **Step 3: Implement connection setup and embedded migrations**

`ProjectDatabase.OpenAsync` must open `Data Source=<absolute path>;Mode=ReadWriteCreate;Cache=Shared`, then execute:

```sql
PRAGMA foreign_keys=ON;
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;
PRAGMA busy_timeout=5000;
```

Embed `Schema/001_initial.sql`. The migration must create the twelve tables listed in the test, `schema_history(version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL)`, all foreign keys, unique sibling-name constraints, and these indexes:

```sql
CREATE INDEX ix_groups_parent_order ON tag_groups(device_id, parent_group_id, sort_order);
CREATE INDEX ix_tags_group_order ON tags(device_id, group_id, sort_order);
CREATE INDEX ix_tags_path ON tags(canonical_path);
CREATE INDEX ix_tags_address ON tags(address);
CREATE INDEX ix_mes_tasks_group ON mes_tasks(group_id);
CREATE INDEX ix_mes_task_refs ON mes_task_index(referenced_tag_id, referenced_path);
```

The migrator must execute a migration and insert its schema-history row in the same transaction.

- [ ] **Step 4: Add failure rollback coverage**

Add a test-only migration containing invalid SQL after a valid `CREATE TABLE`. Assert neither the table nor the schema-history row remains after the exception.

- [ ] **Step 5: Run schema tests**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~ProjectDatabaseSchemaTests`

Expected: all schema and rollback tests pass.

- [ ] **Step 6: Commit**

```powershell
git add src/IGW.Persistence.Sqlite tests/IGW.Tests/ProjectDatabaseSchemaTests.cs
git commit -m "feat: add versioned sqlite project schema"
```

### Task 3: Implement Lossless Snapshot Write and Read

**Files:**
- Create: `src/IGW.Persistence.Sqlite/ProjectEntityMapper.cs`
- Create: `src/IGW.Persistence.Sqlite/ProjectSnapshotWriter.cs`
- Create: `src/IGW.Persistence.Sqlite/ProjectSnapshotReader.cs`
- Create: `src/IGW.Persistence.Sqlite/ProjectSecretVault.cs`
- Create: `tests/IGW.Tests/ProjectSnapshotRoundTripTests.cs`

**Interfaces:**
- Consumes: `IgwProject`, `InstallationSecretProtector`, open SQLite transaction.
- Produces: `ProjectSnapshotWriter.WriteAsync(...)`, `ProjectSnapshotReader.ReadAsync(...)`.

- [ ] **Step 1: Write a failing full-field round-trip test**

Build one project containing a channel and device with IDs/settings, nested groups, an ordinary tag, an array tag, string length/encoding, scaling, deadband, alarm, write confirmation, metadata, a device template/delta, computed tag, schedule, security user, OPC UA user, MQTT password, MES task/flow/response write-back, and flow template. Persist it and assert the returned project is structurally equal after normalizing collection order.

```csharp
IgwProject actual = await repository.LoadRuntimeProjectAsync(default);
Assert.Equal(ProjectStore.ToJson(ProjectTestData.Normalize(expected)),
             ProjectStore.ToJson(ProjectTestData.Normalize(actual)));
```

- [ ] **Step 2: Write a failing plaintext-secret test**

```csharp
[Fact]
public async Task Snapshot_database_never_contains_plaintext_secrets()
{
    IgwProject project = ProjectTestData.Full();
    project.MqttPublish.Password = "mqtt-plain-marker";
    await fixture.Repository.ReplaceFromSnapshotAsync(project, new ProjectRevision(0), default);
    byte[] bytes = await File.ReadAllBytesAsync(fixture.DatabasePath);
    Assert.DoesNotContain("mqtt-plain-marker", Encoding.UTF8.GetString(bytes));
    Assert.Equal("mqtt-plain-marker",
        (await fixture.Repository.LoadRuntimeProjectAsync(default)).MqttPublish.Password);
}
```

- [ ] **Step 3: Run the tests and verify they fail**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~ProjectSnapshotRoundTripTests`

Expected: fails because snapshot reader/writer and vault are absent.

- [ ] **Step 4: Implement entity and section mapping**

Write channels, devices, groups, tags, and tag options with parameterized commands. Store low-cardinality settings in named `project_sections` rows: `settings`, `opcUa`, `history`, `mqttPublish`, `computedTags`, `schedules`, and `security`. Store each template and MES task separately. Build `mes_task_index` by extracting every tag/path reference from the task document.

Before writing settings JSON, call a dedicated extractor that replaces reversible secrets with `secret://<owner>/<name>` references and writes the protected values through `InstallationSecretProtector` into `secret_entries`. Hash pending API passwords before the security section is serialized. Restore references only after reading all entities.

- [ ] **Step 5: Preserve raw property bags and every ordinary-tag extension**

`ProjectEntityMapper` must always create a `tag_options` row, even when the point is not an array. The row includes scaling, deadband, alarm, write confirmation, metadata, `igw_properties_json`, and `kep_properties_json`; this prevents ordinary tags from losing stable IDs or optional settings.

- [ ] **Step 6: Run round-trip and existing secret tests**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~ProjectSnapshotRoundTripTests|FullyQualifiedName~InstallationSecretProtectorTests|FullyQualifiedName~ProjectSecurityTests"`

Expected: all selected tests pass and no plaintext marker is found.

- [ ] **Step 7: Commit**

```powershell
git add src/IGW.Persistence.Sqlite tests/IGW.Tests/ProjectSnapshotRoundTripTests.cs
git commit -m "feat: persist lossless project snapshots in sqlite"
```

### Task 4: Add Paged Queries and Atomic Local Mutations

**Files:**
- Create: `src/IGW.Persistence.Sqlite/SqliteProjectRepository.cs`
- Create: `src/IGW.Persistence.Sqlite/ProjectMutationExecutor.cs`
- Create: `tests/IGW.Tests/SqliteProjectRepositoryTests.cs`

**Interfaces:**
- Implements: every member of `IProjectRepository` from Task 1.
- Produces: revision-checked local updates and stable-ID multi-selection operations.

- [ ] **Step 1: Write failing pagination and tree tests**

Insert 25,050 tags across nested groups, request `offset=10_000, limit=200`, and assert exactly 200 ordered rows plus `Total=25_050`. Request a group’s children and assert it returns only direct children with complete actual paths and descriptions.

- [ ] **Step 2: Write failing conflict and multi-selection tests**

```csharp
ProjectRevision revision = await repository.GetRevisionAsync(default);
var ids = new[] { "tag-1", "tag-2", "tag-3" };
var first = await repository.ExecuteAsync(
    new(ProjectMutationKind.MoveEntities, ProjectNodeKind.Tag, ids, "group-b", null),
    revision, default);
var stale = await repository.ExecuteAsync(
    new(ProjectMutationKind.DeleteEntities, ProjectNodeKind.Tag, ids, null, null),
    revision, default);
Assert.True(first.Applied);
Assert.True(stale.Conflict);
Assert.Equal(3, await CountTagsInGroupAsync("group-b"));
```

- [ ] **Step 3: Run and confirm failure**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~SqliteProjectRepositoryTests`

Expected: fails because repository operations are not implemented.

- [ ] **Step 4: Implement indexed reads**

Use one count query and one bounded select for tag pages. Escape `%`, `_`, and the escape character for LIKE searches. Tree reads query direct channel/device/group children only and compute `HasChildren` with `EXISTS`, not recursive materialization.

- [ ] **Step 5: Implement mutation transactions**

At the beginning of each write transaction, compare `project_meta.revision` with `expected.Value`. Apply the complete ID list using a temporary ID table or bounded parameter batches, update canonical paths recursively for moves/renames, validate sibling uniqueness, increment revision once, and commit. On a stale revision, roll back and return `Conflict=true` without changing any row.

- [ ] **Step 6: Run repository tests**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~SqliteProjectRepositoryTests`

Expected: pagination, hierarchy, multi-selection, and conflict tests pass.

- [ ] **Step 7: Commit**

```powershell
git add src/IGW.Persistence.Sqlite tests/IGW.Tests/SqliteProjectRepositoryTests.cs
git commit -m "feat: add paged project queries and atomic mutations"
```

### Task 5: Implement Safe Legacy Project Migration

**Files:**
- Create: `src/IGW.Persistence.Sqlite/LegacyProjectMigrator.cs`
- Create: `src/IGW.Persistence.Sqlite/ProjectDatabaseBootstrapper.cs`
- Create: `tests/IGW.Tests/LegacyProjectMigrationTests.cs`

**Interfaces:**
- Consumes: SQLite target path, legacy project path, legacy `igw.masterkey` path.
- Produces: `ProjectDatabaseBootstrapper.OpenOrMigrateAsync(...)` and timestamped read-only legacy backup.

Define the bootstrap result types in `ProjectDatabaseBootstrapper.cs`:

```csharp
public enum ProjectStorageSource { ExistingDatabase, MigratedLegacy, InitialProject }
public sealed record ProjectStoragePaths(string DatabasePath, string LegacyProjectPath, string MasterKeyPath);
public sealed record ProjectBootstrapResult(
    IProjectRepository Repository, ProjectStorageSource Source, string? LegacyBackupPath);
```

- [ ] **Step 1: Write failing migration tests**

Cover four cases: no files creates `InitialProject`; legacy-only imports and backs up; database-plus-legacy always chooses database; invalid legacy leaves no target database and does not rename the source.

```csharp
var result = await bootstrapper.OpenOrMigrateAsync(paths, default);
Assert.Equal(ProjectStorageSource.MigratedLegacy, result.Source);
Assert.True(File.Exists(result.LegacyBackupPath));
Assert.Equal("legacy-project", (await result.Repository.LoadRuntimeProjectAsync(default)).Name);
```

- [ ] **Step 2: Run and confirm failure**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~LegacyProjectMigrationTests`

- [ ] **Step 3: Implement staging migration**

Load legacy content through the existing `ProjectStore.Load` so old encrypted values and model normalization remain compatible. Write to `<project.db>.migrating`, reopen it, validate a complete runtime snapshot, checkpoint WAL, close connections, then atomically move it to the final database path. Only after success rename the legacy file to `<name>.migrated-<UTC timestamp>.bak` and mark it read-only.

- [ ] **Step 4: Make migration idempotent and crash-safe**

At startup, delete only stale `.migrating` files whose final database is absent and whose owner lock is not held. Never overwrite an existing final database. Record migration source hash and completion timestamp in `project_meta`.

- [ ] **Step 5: Run migration tests**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~LegacyProjectMigrationTests`

Expected: all cases pass.

- [ ] **Step 6: Commit**

```powershell
git add src/IGW.Persistence.Sqlite tests/IGW.Tests/LegacyProjectMigrationTests.cs
git commit -m "feat: migrate legacy projects to sqlite safely"
```

### Task 6: Route Runtime Saves Through the Repository

**Files:**
- Create: `src/IGW.Core/ProjectConfigurationCoordinator.cs`
- Create: `tests/IGW.Tests/ProjectConfigurationCoordinatorTests.cs`
- Modify: `src/IGW.Core/IGatewayHost.cs`
- Modify: `src/IGW.Runtime/GatewayHost.cs`
- Modify: `src/IGW.Runtime/Program.cs`
- Modify: `src/IGW.Runtime/IGW.Runtime.csproj`

**Interfaces:**
- Consumes: `IProjectRepository`, runtime apply callback, current immutable snapshot.
- Produces: `ProjectConfigurationCoordinator.ApplySnapshotAsync` and `ExecuteAsync`.

- [ ] **Step 1: Write failing coordinator tests**

```csharp
[Fact]
public async Task Persistence_commits_before_runtime_apply_and_reports_apply_failure()
{
    var events = new List<string>();
    var coordinator = CoordinatorFixture.Create(events, apply: _ => throw new IOException("driver restart"));
    var result = await coordinator.ApplySnapshotAsync(ProjectTestData.Full(), new ProjectRevision(1), default);
    Assert.Equal(new[] { "validate", "persist", "apply" }, events);
    Assert.True(result.Persisted);
    Assert.False(result.RuntimeApplied);
    Assert.Equal("runtime_apply_failed", result.ErrorCode);
}
```

Also prove a revision conflict performs neither persistence nor runtime apply, and a successful MES-only replacement does not restart the southbound engine.

- [ ] **Step 2: Run and confirm failure**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~ProjectConfigurationCoordinatorTests`

- [ ] **Step 3: Implement the coordinator**

The coordinator owns one async gate. It validates, persists through `IProjectRepository`, reloads the new runtime snapshot, then calls a typed apply callback with the affected scope. Return:

```csharp
public sealed record ProjectApplyResult(
    bool Persisted, bool RuntimeApplied, bool Conflict,
    ProjectRevision Revision, string? ErrorCode, string? ErrorMessage);
```

Do not mutate `GatewayHost.Project` before persistence succeeds. If runtime apply fails, keep the new database revision, retain the last healthy runtime snapshot, expose a degraded health state, and allow an explicit retry.

- [ ] **Step 4: Change startup to SQLite bootstrap**

`Program.cs` resolves `%ProgramData%\IGW\project.db`, calls `ProjectDatabaseBootstrapper.OpenOrMigrateAsync`, loads the runtime snapshot, updates log retention, and constructs `GatewayHost` with the repository/coordinator. Keep the old `.igwproj` resolver only as a migration source resolver.

- [ ] **Step 5: Remove production calls to `ProjectStore.Save`**

Replace saves in `GatewayHost`, Siemens authentication, template operations, DCS updates, and initial project creation with coordinator commands or snapshot replacement. `ProjectStore` remains referenced only by legacy parsing, validation helpers, test fixtures, and temporary deep-clone callers that have not yet been decomposed.

Verification: `rg -n "ProjectStore\.Save" src` must return only the documented legacy migration path, or no matches.

- [ ] **Step 6: Run runtime regression tests**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~ProjectConfigurationCoordinatorTests|FullyQualifiedName~GatewayHostRevisionTests|FullyQualifiedName~RuntimeStartupContractTests|FullyQualifiedName~RealtimeProjectGenerationTests"`

Expected: all selected tests pass.

- [ ] **Step 7: Commit**

```powershell
git add src/IGW.Core src/IGW.Runtime tests/IGW.Tests/ProjectConfigurationCoordinatorTests.cs
git commit -m "refactor: use sqlite repository for runtime project updates"
```

### Task 7: Add Paged Project Configuration APIs

**Files:**
- Create: `src/IGW.Northbound.RestApi/ProjectConfigurationApi.cs`
- Create: `tests/IGW.Tests/ProjectConfigurationApiTests.cs`
- Modify: `src/IGW.Northbound.RestApi/IgwRestApi.cs`
- Modify: `src/IGW.Core/IGatewayHost.cs`
- Modify: `src/IGW.Northbound.RestApi/PublicPlcApiPolicy.cs`

**Interfaces:**
- Consumes: `IProjectRepository`, `ProjectConfigurationCoordinator` via `IGatewayHost`.
- Produces: `/api/v1/project/summary`, `/tree/{kind}/{id}/children`, `/tags`, `/mes-tasks`, `/entity/{kind}/{id}`, `/mutations`.

- [ ] **Step 1: Write failing HTTP contract tests**

Use `TestServer` with a temporary repository. Assert `GET /api/v1/project/tags?groupId=g1&offset=100&limit=50&q=temp` returns 50 items, total, offset, limit, actual paths, descriptions, and `ETag`. Assert `/mes-tasks?scope=device&deviceId=d1` omits groups whose flow switch is disabled and `/mes-tasks?scope=channel&channelId=c1` never returns another channel. Assert a stale `If-Match` mutation returns 409 and the latest ETag.

- [ ] **Step 2: Run and confirm 404/failure**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~ProjectConfigurationApiTests`

- [ ] **Step 3: Implement endpoints**

Map the endpoints in `ProjectConfigurationApi.MapProjectConfigurationApi`. Require `If-Match` for mutations, parse `ProjectRevision`, return 428 when absent, 409 on conflict, 400 for invalid page sizes, and 200 with the next ETag after success. Do not include these management routes in the public PLC Swagger document.

- [ ] **Step 4: Keep a bounded legacy bridge**

The existing `GET/PUT /api/v1/project` remains temporarily available for old callers only when the project contains at most 50,000 tags. Above that threshold return 413 with code `full_project_endpoint_disabled` and links to the paged endpoints. Add `Deprecation: true` and `Sunset` documentation headers. It must persist through the coordinator rather than `ProjectStore.Save`.

- [ ] **Step 5: Run API and public-policy tests**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~ProjectConfigurationApiTests|FullyQualifiedName~PublicPlcApiPolicyTests|FullyQualifiedName~GatewayHostRevisionTests"`

- [ ] **Step 6: Commit**

```powershell
git add src/IGW.Northbound.RestApi src/IGW.Core/IGatewayHost.cs tests/IGW.Tests/ProjectConfigurationApiTests.cs
git commit -m "feat: add paged project configuration api"
```

### Task 8: Move Configuration Searches to Indexed Repository Queries

**Files:**
- Modify: `src/IGW.Northbound.RestApi/ProjectParameterSearch.cs`
- Modify: `src/IGW.Northbound.RestApi/ProjectTagScopeApi.cs`
- Modify: `src/IGW.Northbound.RestApi/MesRangeOptionsApi.cs`
- Modify: `src/IGW.Northbound.RestApi/IgwRestApi.cs`
- Create: `tests/IGW.Tests/ProjectRepositorySearchTests.cs`

**Interfaces:**
- Consumes: repository canonical paths, array shape documents, tag access.
- Produces: paged parameter, tag-scope, and MES-range results without traversing `host.Project`.

- [ ] **Step 1: Add failing million-branch search tests**

Create 100,000 tags with duplicate leaf names under different paths. Assert searches return the correct actual paths, permissions, lengths, array roots, and a cursor without loading unrelated groups. Use a repository spy that fails the test if `LoadRuntimeProjectAsync` is called.

- [ ] **Step 2: Run and confirm failure**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~ProjectRepositorySearchTests`

- [ ] **Step 3: Replace full-project enumeration**

Move the query logic behind repository methods that filter by canonical path boundaries, not prefix-only name matching. Preserve the existing response DTOs so MES flow selectors and cross-DB selection remain compatible. Array searches return the one logical array root and shape metadata, never all expanded elements.

- [ ] **Step 4: Run search regressions**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~ProjectRepositorySearchTests|FullyQualifiedName~ProjectParameterSearchTests|FullyQualifiedName~ProjectTagScopeApiTests|FullyQualifiedName~MesRangeOptionsApiTests"`

- [ ] **Step 5: Commit**

```powershell
git add src/IGW.Northbound.RestApi tests/IGW.Tests/ProjectRepositorySearchTests.cs
git commit -m "perf: query project selectors from sqlite indexes"
```

### Task 9: Add Million-Point Storage Benchmarks and Completion Gate

**Files:**
- Create: `benchmarks/IGW.ProjectStorage.Performance/IGW.ProjectStorage.Performance.csproj`
- Create: `benchmarks/IGW.ProjectStorage.Performance/Program.cs`
- Create: `tests/IGW.Tests/ProjectStorageSmokePerformanceTests.cs`
- Modify: `IGW.slnx`
- Modify: `docs/MONITORING.md`

**Interfaces:**
- Consumes: repository public APIs only.
- Produces: JSON benchmark evidence with row counts, elapsed times, allocations/working set, database bytes, and revision.

- [ ] **Step 1: Write the bounded smoke performance test**

Generate 100,000 tags, bulk-write them, query ten nonadjacent pages, update one tag, and assert the query response never contains more than the requested page size. Use a generous CI timeout but assert functional boundedness rather than a workstation-specific millisecond SLA.

- [ ] **Step 2: Run the smoke test and record the failing baseline**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~ProjectStorageSmokePerformanceTests`

- [ ] **Step 3: Implement the standalone million-point benchmark**

The tool accepts `--points`, `--database`, and `--output`; defaults to one million points and writes:

```json
{
  "points": 1000000,
  "importMs": 0,
  "pageP50Ms": 0,
  "pageP95Ms": 0,
  "singleUpdateMs": 0,
  "exportPreparationMs": 0,
  "peakWorkingSetBytes": 0,
  "databaseBytes": 0,
  "revision": 0
}
```

Populate zeros with measured values. Fail when row counts or revisions are wrong; report performance numbers without inventing a pass threshold.

- [ ] **Step 4: Run all storage tests and benchmark**

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~ProjectStorage|FullyQualifiedName~LegacyProjectMigration|FullyQualifiedName~ProjectConfiguration"
dotnet run --project benchmarks/IGW.ProjectStorage.Performance -c Release -- --points 1000000 --output artifacts/project-storage-million.json
```

Expected: tests pass; benchmark JSON reports exactly 1,000,000 points and nonzero timing/size metrics.

- [ ] **Step 5: Run the full backend gate**

Run: `dotnet test IGW.slnx -c Release --no-restore --verbosity minimal`

Expected: all discovered tests pass; the output must not report zero tests.

- [ ] **Step 6: Commit**

```powershell
git add IGW.slnx benchmarks/IGW.ProjectStorage.Performance tests/IGW.Tests/ProjectStorageSmokePerformanceTests.cs docs/MONITORING.md
git commit -m "test: add million-point project storage benchmark"
```

## Storage Plan Exit Criteria

- Runtime starts from `project.db`, migrates one legacy project exactly once, and never writes project JSON automatically.
- All runtime configuration saves use repository revisions and the coordinator.
- Tree, tag, selector, and MES-range queries are paged/indexed and preserve complete actual paths.
- Ordinary tags and all advanced fields round-trip through SQLite without plaintext secrets.
- Full backend tests and the one-million-point benchmark complete with recorded evidence.
