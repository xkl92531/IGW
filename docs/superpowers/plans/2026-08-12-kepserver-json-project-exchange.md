# KEPServer JSON Project Exchange Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Web project-level import and export use one JSON configuration format, retain KEP connectivity compatibility, and preserve IGW-only configuration such as MES flows.

**Architecture:** Keep the KEP `project` connectivity tree and add an `igw` extension for IGW-only project settings. The extension omits `channels` to avoid duplicating million-point projects. `Project.vue` downloads the combined document as `.json`; IGW persistence remains API-backed and unchanged.

**Tech Stack:** Vue 3, TypeScript, Node test runner, Vite.

## Global Constraints

- File exchange exposes JSON configuration only.
- Preserve MES and other IGW-only project settings in the `igw` extension.
- Existing user names, addresses, descriptions and tree hierarchy must survive an export/import round trip.
- Unknown drivers retain their driver identifier instead of being silently mapped to another driver.

---

### Task 1: KEPServer serializer and round-trip contract

**Files:**
- Modify: `src/IGW.WebUi/src/interop.ts`
- Create: `src/IGW.WebUi/src/interop.kep-project.test.cjs`

**Interfaces:**
- Produces: `exportKepJson(project: Project): string`
- Consumes: existing `importKepJson(json: string)`

- [x] Write a failing test that transpiles `interop.ts`, exports a project containing nested groups and representative tag types, and asserts the lowercase `project.channels/devices/tag_groups/tags` hierarchy and KEP symbolic properties.
- [x] Run `node --test src/interop.kep-project.test.cjs` and confirm it fails because `exportKepJson` does not exist.
- [x] Implement explicit driver, endpoint, data type, access and recursive group/tag serialization.
- [x] Extend the test to re-import the JSON and assert channel/device/tag counts, hierarchy, address, type, access, scan period and description.
- [x] Run the focused test until it passes.

### Task 2: Project toolbar export behavior

**Files:**
- Modify: `src/IGW.WebUi/src/views/Project.vue`
- Modify: `src/IGW.WebUi/src/views/Project.kep-project-import.test.cjs`
- Modify: `src/IGW.WebUi/src/i18n/catalog.ts`

**Interfaces:**
- Consumes: `exportKepJson(project)` from Task 1.

- [x] Change the existing UI test to require “导出 KEPServer JSON”, `.json`, and absence of `.igwproj`.
- [x] Run the focused UI test and confirm it fails against the old export function.
- [x] Replace direct internal project serialization with `exportKepJson`, change the downloaded suffix to `.json`, and update bilingual copy.
- [x] Run the focused UI and catalog tests until they pass.

### Task 3: Full verification

**Files:**
- Verify only.

- [x] Run `node --test src/**/*.test.cjs`.
- [x] Run `npm run typecheck`.
- [x] Run `npm run build`.
- [x] Inspect `git diff --check` and confirm unrelated Runtime/MES changes remain untouched.

### Task 4: Preserve IGW collection flows

- [x] Reproduce the loss of `mesUpload` with an export/import round-trip test.
- [x] Add the versioned `igw` extension without duplicating `channels`.
- [x] Restore the extension during IGW import while retaining native KEP JSON fallback.
- [x] Run the full Web test, type-check and production build suite.
