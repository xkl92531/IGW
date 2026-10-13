# Paged Project Editors Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace full-project browser/WPF editing with attractive lazy-loaded tree, paged tables, stable multi-selection, local commands, and server-streamed JSON import/export.

**Architecture:** The Vue page becomes a shell over feature-scoped API clients, composables, and focused components; only the selected branch/page/form is reactive. WPF uses the same paged HTTP contracts through a dedicated workspace service. Both editors send revision-checked local mutations and never construct the one-million-point project in client memory.

**Tech Stack:** Vue 3.5, TypeScript 5.7, Element Plus 2.9, Vitest 3.2, axios, .NET 10 WPF, xUnit

**Spec:** `docs/superpowers/specs/2026-09-01-sqlite-project-store-kep-json-design.md`

## Global Constraints

- This plan begins after the SQLite storage plan and JSON exchange plan APIs are available.
- The user-facing project exchange actions are named “导入 JSON” and “导出 JSON”; there is no `.igwproj` or separate “Export KEP” action.
- Disabled collection-flow groups do not appear in the MES task editor; task filters remain current device/current channel, not all channels.
- Names that collide are disambiguated with complete actual paths and descriptions.
- Clicking a different node closes the previous context menu.
- Row selection uses stable IDs, shows a clear background, and copy/cut/paste applies to the complete frozen selection.
- PLC runtime/SSE behavior is not coupled to whether a configuration page is open.
- The pre-existing `installer/version.txt` change is user-owned and must not be staged.

## File and Module Map

- `src/IGW.WebUi/src/features/project/types.ts`: paged API DTOs only.
- `src/IGW.WebUi/src/features/project/api.ts`: summary/tree/tag/entity/mutation/import/export calls.
- `src/IGW.WebUi/src/features/project/useProjectTree.ts`: lazy tree cache and invalidation.
- `src/IGW.WebUi/src/features/project/useTagPage.ts`: paged/search/sort state.
- `src/IGW.WebUi/src/features/project/useProjectSelection.ts`: stable selection and clipboard snapshots.
- `src/IGW.WebUi/src/features/project/useProjectMutations.ts`: revision-aware command execution.
- `src/IGW.WebUi/src/features/project/components/*`: shell sections with one responsibility each.
- `src/IGW.Configurator/ProjectApiModels.cs`: WPF copies of paged transport DTOs.
- `src/IGW.Configurator/ProjectWorkspaceService.cs`: WPF lazy workspace and commands.

---

### Task 1: Add Typed Paged API Client

**Files:**
- Create: `src/IGW.WebUi/src/features/project/types.ts`
- Create: `src/IGW.WebUi/src/features/project/api.ts`
- Create: `src/IGW.WebUi/src/features/project/api.spec.ts`
- Modify: `src/IGW.WebUi/src/api.ts`

**Interfaces:**
- Consumes: storage and exchange REST endpoints.
- Produces: `projectApi.summary`, `children`, `tags`, `entity`, `mutate`, `inspectImport`, `commitImport`, `exportUrl`.

- [ ] **Step 1: Write failing API tests**

Inject a mocked `AxiosInstance` and assert tag queries pass `groupId`, `q`, `offset`, and `limit`; mutations send `If-Match`; response ETags update the active project revision; import uses multipart and export never fetches the full JSON into JavaScript. Export `createProjectApi(http: AxiosInstance)` and create the production singleton from the existing shared client.

```ts
it('sends the complete frozen mutation with If-Match', async () => {
  const post = vi.fn().mockResolvedValue({
    data: { applied: true }, headers: { etag: '"project-8"' },
  })
  const projectApi = createProjectApi({ post } as unknown as AxiosInstance)
  await projectApi.mutate(command, '"project-7"')
  expect(post).toHaveBeenCalledWith('/api/v1/project/mutations', command,
    expect.objectContaining({ headers: { 'If-Match': '"project-7"' } }))
  expect(post.mock.calls[0][1].entityIds).toEqual(['t1', 't2', 't3'])
})
```

- [ ] **Step 2: Run and confirm failure**

Run: `pnpm --dir src/IGW.WebUi test -- src/features/project/api.spec.ts`

- [ ] **Step 3: Implement exact DTOs and API methods**

Mirror backend camelCase contracts; do not use `any` in the new feature. `exportUrl` returns a URL with export options only; the caller assigns it to a native download link or uses `window.location`, so axios does not buffer the file.

- [ ] **Step 4: Run tests and type check**

```powershell
pnpm --dir src/IGW.WebUi test -- src/features/project/api.spec.ts
pnpm --dir src/IGW.WebUi typecheck
```

- [ ] **Step 5: Commit**

```powershell
git add src/IGW.WebUi/src/api.ts src/IGW.WebUi/src/features/project
git commit -m "feat: add paged project api client"
```

### Task 2: Implement Lazy Tree and Paged Tag State

**Files:**
- Create: `src/IGW.WebUi/src/features/project/useProjectTree.ts`
- Create: `src/IGW.WebUi/src/features/project/useTagPage.ts`
- Create: `src/IGW.WebUi/src/features/project/useProjectTree.spec.ts`
- Create: `src/IGW.WebUi/src/features/project/useTagPage.spec.ts`

**Interfaces:**
- Consumes: `projectApi.children` and `projectApi.tags`.
- Produces: branch-only tree cache, debounced search, page state, cancellation, and revision invalidation.

- [ ] **Step 1: Write failing tree behavior tests**

Expand a device, expand one group, collapse/re-expand, then change revision. Assert only requested branches are fetched, cached branches are reused within a revision, and all branch caches are invalidated after revision change.

- [ ] **Step 2: Write failing pagination/cancellation tests**

Start search A then search B before A returns. Assert A is aborted and cannot overwrite B. Assert page state holds at most the requested rows and not the total collection.

```ts
expect(state.items).toHaveLength(100)
expect(state.total).toBe(1_000_000)
expect(requests[0].signal.aborted).toBe(true)
```

- [ ] **Step 3: Run and confirm failure**

Run: `pnpm --dir src/IGW.WebUi test -- src/features/project/useProjectTree.spec.ts src/features/project/useTagPage.spec.ts`

- [ ] **Step 4: Implement composables**

Key caches by `${revision}:${kind}:${id}`. Use one `AbortController` per request generation, a 200 ms search debounce, default page size 200, and an explicit `dispose()` that aborts outstanding requests. Store DTO arrays in shallow refs to avoid deep proxying.

- [ ] **Step 5: Run tests and commit**

```powershell
pnpm --dir src/IGW.WebUi test -- src/features/project/useProjectTree.spec.ts src/features/project/useTagPage.spec.ts
git add src/IGW.WebUi/src/features/project
git commit -m "feat: add lazy project tree and paged tag state"
```

### Task 3: Make Selection and Clipboard Operations Deterministic

**Files:**
- Create: `src/IGW.WebUi/src/features/project/useProjectSelection.ts`
- Create: `src/IGW.WebUi/src/features/project/useProjectMutations.ts`
- Create: `src/IGW.WebUi/src/features/project/useProjectSelection.spec.ts`
- Create: `src/IGW.WebUi/src/features/project/useProjectMutations.spec.ts`

**Interfaces:**
- Consumes: stable row IDs and `projectApi.mutate`.
- Produces: selected ID set, frozen clipboard snapshot, revision conflict recovery.

- [ ] **Step 1: Write failing multi-selection tests**

Select three rows, change the visible page, invoke copy/cut, and assert the snapshot remains exactly three IDs. After a successful cut, clear only moved IDs. After 409, keep the selection and expose a reload message.

- [ ] **Step 2: Run and confirm failure**

Run: `pnpm --dir src/IGW.WebUi test -- src/features/project/useProjectSelection.spec.ts src/features/project/useProjectMutations.spec.ts`

- [ ] **Step 3: Implement frozen snapshots**

```ts
export interface ProjectClipboardSnapshot {
  mode: 'copy' | 'cut'
  kind: ProjectNodeKind
  entityIds: readonly string[]
  sourceParentId: string
  sourceRevision: string
}

function freezeSelection(mode: 'copy' | 'cut'): ProjectClipboardSnapshot {
  return Object.freeze({
    mode, kind: selectedKind.value,
    entityIds: Object.freeze([...selectedIds.value]),
    sourceParentId: parentId.value,
    sourceRevision: revision.value,
  })
}
```

Mutation response handling updates the revision first, invalidates only affected branches/pages, and never toggles unrelated collection-flow tasks.

- [ ] **Step 4: Run tests and commit**

```powershell
pnpm --dir src/IGW.WebUi test -- src/features/project/useProjectSelection.spec.ts src/features/project/useProjectMutations.spec.ts
git add src/IGW.WebUi/src/features/project
git commit -m "fix: make project multi selection operations deterministic"
```

### Task 4: Split the Project Page into Focused Components

**Files:**
- Create: `src/IGW.WebUi/src/features/project/components/ProjectToolbar.vue`
- Create: `src/IGW.WebUi/src/features/project/components/ProjectTreePane.vue`
- Create: `src/IGW.WebUi/src/features/project/components/ProjectTagTable.vue`
- Create: `src/IGW.WebUi/src/features/project/components/ProjectPropertyPanel.vue`
- Create: `src/IGW.WebUi/src/features/project/components/ProjectContextMenu.vue`
- Create: `src/IGW.WebUi/src/features/project/components/ProjectBatchBar.vue`
- Create: `src/IGW.WebUi/src/features/project/components/ProjectEditorShell.spec.ts`
- Modify: `src/IGW.WebUi/src/views/Project.vue`

**Interfaces:**
- Consumes: composables from Tasks 2–3.
- Produces: accessible lazy editor UI with resizable columns and unambiguous selection.

- [ ] **Step 1: Write failing component tests**

Mount the shell with two duplicate names under different paths. Assert descriptions and actual paths render, selected rows receive `is-selected`, dragging a column separator persists width, and clicking another tree node closes the old context menu.

- [ ] **Step 2: Run and confirm failure**

Run: `pnpm --dir src/IGW.WebUi test -- src/features/project/components/ProjectEditorShell.spec.ts`

- [ ] **Step 3: Implement the shell and components**

`Project.vue` owns only selected node, pane sizes, and component composition. The table uses server paging and a stable `row-key="id"`; selected rows use an accessible blue-gray background and left accent. The tree/list show `name`, `description`, and ellipsized actual path with a native tooltip containing the full value.

```css
.project-shell {
  --tree-width: 360px;
  --property-width: 420px;
  grid-template-columns: minmax(300px, var(--tree-width)) 6px minmax(520px, 1fr) 6px minmax(360px, var(--property-width));
}
.project-row.is-selected {
  background: #eaf3ff;
  box-shadow: inset 3px 0 #409eff;
}
```

- [ ] **Step 4: Keep behavior-specific rules explicit**

The collection-flow switch only controls whether MES flow/report design is available for that group. It never auto-creates or auto-starts a task. Parent and child mutual-exclusion validation comes from the backend; the UI displays its exact conflict. A context-menu action unavailable because flow is disabled is disabled and explains why.

- [ ] **Step 5: Run component tests, type check, and commit**

```powershell
pnpm --dir src/IGW.WebUi test -- src/features/project/components/ProjectEditorShell.spec.ts
pnpm --dir src/IGW.WebUi typecheck
git add src/IGW.WebUi/src/views/Project.vue src/IGW.WebUi/src/features/project/components
git commit -m "refactor: split paged project editor components"
```

### Task 5: Replace Browser-Side Project JSON Conversion

**Files:**
- Create: `src/IGW.WebUi/src/features/project/components/ProjectImportDialog.vue`
- Create: `src/IGW.WebUi/src/features/project/components/ProjectExportDialog.vue`
- Create: `src/IGW.WebUi/src/features/project/components/ProjectExchangeDialogs.spec.ts`
- Modify: `src/IGW.WebUi/src/interop.ts`
- Modify: `src/IGW.WebUi/src/interop.spec.ts`
- Modify: `src/IGW.WebUi/src/views/Project.vue`

**Interfaces:**
- Consumes: exchange inspect/commit/export APIs.
- Produces: user-visible JSON preview, compatibility warnings, password prompts, and streamed download.

- [ ] **Step 1: Write failing dialog tests**

Assert the file chooser accepts `.json`; inspect shows channel/device/group/tag/MES counts and compatibility errors; commit remains disabled on blocking errors; a password field appears when secrets are present; export has one action named “导出 JSON”. Assert `.igwproj` and “导出 KEP” are absent.

- [ ] **Step 2: Run and confirm failure**

Run: `pnpm --dir src/IGW.WebUi test -- src/features/project/components/ProjectExchangeDialogs.spec.ts`

- [ ] **Step 3: Implement server-driven dialogs**

Upload once for inspect, keep only token/hash/summary in Vue state, and commit by token. Use a normal browser download navigation for export. Clear migration password and file references when the dialog closes.

- [ ] **Step 4: Remove KEP conversion from the frontend**

Delete `exportKepJson`, `importKepJson`, tag-extension collection, KEP type maps, and KEP channel/device writers from the frontend after callers and tests move to the server API. Preserve unrelated DCS conversion functions until a separately approved removal.

Verification: `rg -n "exportKepJson|importKepJson|\.igwproj|导出 KEP" src/IGW.WebUi/src` returns no active project-exchange usage.

- [ ] **Step 5: Run tests, type check, build, and commit**

```powershell
pnpm --dir src/IGW.WebUi test
pnpm --dir src/IGW.WebUi typecheck
pnpm --dir src/IGW.WebUi build
git add src/IGW.WebUi/src src/IGW.Runtime/wwwroot
git commit -m "refactor: use server streamed json project exchange"
```

### Task 6: Update MES Task Browser to Repository Paging

**Files:**
- Create: `src/IGW.WebUi/src/features/mes/useMesTaskCatalog.ts`
- Create: `src/IGW.WebUi/src/features/mes/useMesTaskCatalog.spec.ts`
- Modify: `src/IGW.WebUi/src/views/MesFlow.vue`

**Interfaces:**
- Consumes: paged MES task endpoint scoped to current device/current channel.
- Produces: only flow-enabled tasks with name/status search.

- [ ] **Step 1: Write failing scope/filter tests**

Given tasks from two channels, assert the default list contains only current-device flow-enabled groups; switching to current-channel includes sibling devices but never another channel; disabled-flow groups never render; filters support name plus all/running/stopped.

- [ ] **Step 2: Run and confirm failure**

Run: `pnpm --dir src/IGW.WebUi test -- src/features/mes/useMesTaskCatalog.spec.ts`

- [ ] **Step 3: Implement paged catalog and preserve SSE runtime state**

HTTP supplies low-frequency task definitions and pages; SSE continues supplying live state keyed by stable task ID. Merge state without replacing the catalog or toggling any task. Opening the flow page does not change collection state.

- [ ] **Step 4: Run MES UI regressions and commit**

```powershell
pnpm --dir src/IGW.WebUi test -- src/features/mes/useMesTaskCatalog.spec.ts
pnpm --dir src/IGW.WebUi typecheck
git add src/IGW.WebUi/src/features/mes src/IGW.WebUi/src/views/MesFlow.vue
git commit -m "perf: page mes task catalog by current scope"
```

### Task 7: Adapt the WPF Configurator to the Same Contracts

**Files:**
- Create: `src/IGW.Configurator/ProjectApiModels.cs`
- Create: `src/IGW.Configurator/ProjectWorkspaceService.cs`
- Create: `tests/IGW.Tests/ProjectWorkspaceServiceTests.cs`
- Modify: `src/IGW.Configurator/ApiClient.cs`
- Modify: `src/IGW.Configurator/MainViewModel.cs`

**Interfaces:**
- Consumes: project summary/tree/tags/entity/mutation/import/export endpoints.
- Produces: WPF lazy workspace with revision-aware commands.

- [ ] **Step 1: Write failing workspace tests**

Use a fake `HttpMessageHandler` and assert connecting loads only summary/root, expanding loads direct children, selecting a group loads one tag page, and saving one entity sends one mutation rather than a complete `IgwProject`.

- [ ] **Step 2: Run and confirm failure**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~ProjectWorkspaceServiceTests`

- [ ] **Step 3: Implement WPF API methods and workspace**

Add typed methods for every paged endpoint, persist current ETag, and expose async commands. Replace `GetProjectAsync`/`PutProjectAsync` calls in editing paths. `OpenProjectCommand` uploads JSON for inspect/commit; `ExportProjectCommand` streams the API response to a chosen `.json` file.

```csharp
public Task<ProjectPage<ProjectTagRowDto>> GetTagsAsync(
    string? groupId, string? query, int offset, int limit, CancellationToken ct);
public Task<ProjectMutationResponse> MutateAsync(
    ProjectMutationRequest mutation, string etag, CancellationToken ct);
public Task<ProjectImportPreviewDto> InspectImportAsync(
    Stream json, CancellationToken ct);
```

- [ ] **Step 4: Remove user-facing `.igwproj` behavior**

Change WPF dialogs and labels to JSON only. Keep no direct file deserialization of `IgwProject` in `MainViewModel`. Confirm `rg -n "\.igwproj|GetProjectAsync|PutProjectAsync" src/IGW.Configurator` returns only an explicitly documented legacy compatibility helper or no matches.

- [ ] **Step 5: Run tests and build WPF**

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~ProjectWorkspaceServiceTests
dotnet build src/IGW.Configurator/IGW.Configurator.csproj -c Release --no-restore
```

- [ ] **Step 6: Commit**

```powershell
git add src/IGW.Configurator tests/IGW.Tests/ProjectWorkspaceServiceTests.cs
git commit -m "refactor: page wpf project configuration workspace"
```

### Task 8: Add UI Performance and Full Release Gates

**Files:**
- Create: `src/IGW.WebUi/src/features/project/projectEditor.performance.spec.ts`
- Modify: `docs/manual/project-json-migration.md`

- [ ] **Step 1: Add a bounded-reactivity performance test**

Mock a project total of one million tags and a returned page of 200. Mount the editor, expand three branches, search, select three rows, and save one property. Assert rendered tag rows never exceed 200 plus fixed UI chrome, API calls are bounded, and no full-project endpoint is requested.

- [ ] **Step 2: Run frontend verification**

```powershell
pnpm --dir src/IGW.WebUi test
pnpm --dir src/IGW.WebUi typecheck
pnpm --dir src/IGW.WebUi build
```

Expected: all tests, type check, and production build pass.

- [ ] **Step 3: Run backend and solution verification**

```powershell
dotnet test IGW.slnx -c Release --no-restore --verbosity minimal
dotnet build IGW.slnx -c Release --no-restore
```

Expected: all discovered tests pass; solution build has zero errors.

- [ ] **Step 4: Perform browser acceptance**

Start Runtime with a generated large project database. In the real browser, verify lazy expansion, full actual paths/descriptions, adjustable widths, visible selection background, three-row copy/cut/paste, context-menu dismissal, task scope filtering, JSON inspect/export, and no long main-thread stall. Record browser/network evidence separately from source tests.

- [ ] **Step 5: Update operator documentation and commit**

Document JSON migration password behavior, pure KEP import, compatibility report meaning, and the fact that logs/history/license/certificate private keys are excluded.

```powershell
git add src/IGW.WebUi src/IGW.Runtime/wwwroot docs/manual/project-json-migration.md
git commit -m "test: verify paged million point project editors"
```

## Editor Plan Exit Criteria

- Web and WPF editors never load/save a complete million-point project for ordinary operations.
- Tree, table, search, selection, context menu, widths, descriptions, and actual paths satisfy the stated interaction rules.
- JSON import/export is server-streamed and is the only user-facing full-project exchange.
- MES task lists show only flow-enabled groups and default to the current device, with an explicit current-channel filter.
- Existing SSE live status remains independent from configuration loading and task definitions.
- Frontend tests/typecheck/build, backend tests, WPF build, and browser acceptance evidence are all recorded.
