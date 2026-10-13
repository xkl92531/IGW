# OPC UA Structured Import Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Preserve OPC UA structure-variable hierarchy through browsing and import while allowing safe structure/tag renaming without changing NodeIds.

**Architecture:** The driver walker will return explicit path segments and recurse through component-bearing Variable nodes while filtering properties. A pure Web UI mapping module will own source-path interpretation and target-name aliases; the dialog renders and edits those aliases, and the existing atomic import pipeline remains the only project mutation path.

**Tech Stack:** .NET 10, OPC Foundation UA client 1.5.378.145, xUnit, Vue 3, TypeScript, Element Plus, Node test runner.

## Global Constraints

- OPC UA NodeId remains unchanged when any display/import name is edited.
- `HasProperty` metadata is not imported as PLC structure fields.
- Existing clients that return only `path` remain compatible.
- Browse cancellation, cycle protection, depth limit 32, and tag limit 10000 remain enforced.
- Existing dirty workspace files outside this feature are not staged or reverted.

---

### Task 1: Preserve OPC UA variable-component hierarchy

**Files:**
- Modify: `src/IGW.Abstractions/Drivers.cs`
- Modify: `src/IGW.Drivers.OpcUaClient/OpcUaBrowseWalker.cs`
- Modify: `src/IGW.Drivers.OpcUaClient/OpcUaClientDeviceDriver.cs`
- Modify: `tests/IGW.Tests/IGW.Tests.csproj`
- Create: `tests/IGW.Tests/OpcUaBrowseWalkerTests.cs`

**Interfaces:**
- Produces: `BrowsedTag.PathSegments : IReadOnlyList<string>`.
- Produces: `OpcUaBrowseChild(string Id, string Name, bool IsVariable, bool IsProperty)`.

- [ ] **Step 1: Write failing walker tests**

Create an in-memory `IOpcUaBrowseSource` that describes `Objects -> Recipe(variable) -> Header(variable) -> BatchId(variable)` plus a `Recipe.EngineeringUnits` property. Assert the result contains only `BatchId`, with `PathSegments == ["Recipe", "Header"]`, excludes the property, keeps the leaf NodeId, and preserves a segment named `Cell.Data` without splitting it.

- [ ] **Step 2: Verify the tests fail**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter OpcUaBrowseWalkerTests`

Expected: compile/test failure because `PathSegments` and `IsProperty` do not exist and Variable nodes are terminal.

- [ ] **Step 3: Implement the minimal walker model**

Add `PathSegments` to `BrowsedTag`. In `Walk`, request each child's non-property descendants; recurse when an Object or Variable has structural descendants, otherwise emit a leaf tag. Carry an immutable copied segment array into every emitted tag and derive legacy `Path` with `string.Join('.')`.

- [ ] **Step 4: Classify OPC UA properties at the session boundary**

In `SessionBrowseSource.AddReferences`, compare each reference type with `ReferenceTypeIds.HasProperty`; set `IsProperty=true` and let the walker exclude it. Keep namespace-zero filtering and continuation handling unchanged.

- [ ] **Step 5: Run focused and full .NET tests**

Run:

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter OpcUaBrowseWalkerTests
dotnet test IGW.slnx --no-restore --verbosity minimal
```

Expected: all tests pass with no new warnings.

### Task 2: Build a pure target-name mapping module

**Files:**
- Create: `src/IGW.WebUi/src/utils/opcUaStructuredImport.ts`
- Create: `src/IGW.WebUi/src/utils/opcUaStructuredImport.test.cjs`
- Modify: `src/IGW.WebUi/src/api.ts`

**Interfaces:**
- Produces: `sourcePathSegments(tag): string[]`.
- Produces: `toImportedOpcUaTag(tag, aliases): ImportedTag`.
- Produces: `validateOpcUaAliases(aliases): string[]`.
- Produces: `replaceImportedTarget(target, importedTarget): void`.

- [ ] **Step 1: Write failing TypeScript behavior tests**

Use the existing `typescript.transpileModule` test pattern. Assert explicit `pathSegments` wins over dotted `path`, a folder alias changes every descendant `groupPath`, a tag alias changes only `name`, `address` stays byte-for-byte equal, blank/dotted aliases are rejected, and `replaceImportedTarget` updates `groups` for devices but `subGroups` for groups.

- [ ] **Step 2: Verify the test fails**

Run: `node --test src/IGW.WebUi/src/utils/opcUaStructuredImport.test.cjs`

Expected: failure because the module/functions do not exist.

- [ ] **Step 3: Implement mapping and validation**

Use source-prefix keys joined with `\u0000`. Apply folder aliases prefix-by-prefix so parent/child renames cascade deterministically. Store tag aliases by NodeId. Prepend an optional root group only when non-empty. Return a fresh `ImportedTag`; never mutate the browsed response.

- [ ] **Step 4: Extend the API type**

Add optional `pathSegments?: string[]` to `OpcUaBrowsedTag`; retain `path` for old runtimes.

- [ ] **Step 5: Verify helper tests pass**

Run: `node --test src/IGW.WebUi/src/utils/opcUaStructuredImport.test.cjs`

Expected: all mapping tests pass.

### Task 3: Add scalable structure and tag renaming to the browser dialog

**Files:**
- Modify: `src/IGW.WebUi/src/components/OpcUaTagBrowserDialog.vue`
- Modify: `src/IGW.WebUi/src/components/OpcUaTagBrowserDialog.ui.test.cjs`
- Modify: `src/IGW.WebUi/src/views/Project.vue`
- Modify: `src/IGW.WebUi/src/views/Project.opcua-import.test.cjs`

**Interfaces:**
- Consumes: Task 2 mapping/validation helpers.
- Emits: existing `{ tags: ImportedTag[]; overwriteConflicts: boolean }` payload.

- [ ] **Step 1: Write failing UI source-contract tests**

Assert the dialog contains a selected-node target-name editor, reset-name action, validation message, and imports `toImportedOpcUaTag`. Assert `Project.vue` calls `replaceImportedTarget` instead of always assigning `target.groups`.

- [ ] **Step 2: Verify UI tests fail**

Run:

```powershell
node --test src/IGW.WebUi/src/components/OpcUaTagBrowserDialog.ui.test.cjs
node --test src/IGW.WebUi/src/views/Project.opcua-import.test.cjs
```

Expected: missing target-name editor/helper assertions fail.

- [ ] **Step 3: Wire explicit path segments into the virtual tree**

Give every folder node its exact source segment array and every tag node its NodeId identity. Build the tree through `sourcePathSegments`; search and checkbox keys continue using source values.

- [ ] **Step 4: Add the selected-node target-name editor**

Clicking a folder or leaf selects it for editing in the right panel. Bind folder aliases by source prefix and tag aliases by NodeId. Show source path, target name, reset action, and inline validation; disable Apply while aliases are invalid. Display the computed renamed path in selected-tag rows.

- [ ] **Step 5: Correct project target replacement**

Replace direct `target.groups = ...` assignment with `replaceImportedTarget`, preserving the device/group container shape.

- [ ] **Step 6: Run Web UI tests and type checks**

Run:

```powershell
node --test src/IGW.WebUi/src/utils/opcUaStructuredImport.test.cjs src/IGW.WebUi/src/components/OpcUaTagBrowserDialog.ui.test.cjs src/IGW.WebUi/src/views/Project.opcua-import.test.cjs
tools\pnpm.cmd --dir src\IGW.WebUi typecheck
tools\pnpm.cmd --dir src\IGW.WebUi build
```

Expected: tests, typecheck, and production build pass.

### Task 4: End-to-end verification

**Files:**
- Verify only; no new production files.

**Interfaces:**
- Consumes: all prior tasks.

- [ ] **Step 1: Run complete automated verification**

Run:

```powershell
dotnet test IGW.slnx --no-restore --verbosity minimal
node --test src/IGW.WebUi/src/utils/*.test.cjs src/IGW.WebUi/src/components/*.test.cjs src/IGW.WebUi/src/views/*.test.cjs
```

Expected: no failures.

- [ ] **Step 2: Perform live browser verification**

Open the existing IGW project page, browse the simulator OPC UA endpoint, select a nested structure, rename one structure and one leaf, import, and verify the engineering tree retains nested groups while the stored addresses remain original NodeIds.

- [ ] **Step 3: Review the final diff**

Confirm only the planned driver, model, UI, tests, spec, and plan files are included; do not stage existing `MesFlow` or pnpm-store changes.
