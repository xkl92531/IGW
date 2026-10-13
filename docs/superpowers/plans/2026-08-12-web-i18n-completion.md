# IGW Web Internationalization Completion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove fixed Chinese copy from the IGW Web engineering configuration domain while preserving all persisted configuration values and user/PLC data.

**Architecture:** Keep the existing `useI18n()` service and Chinese-key catalog. Route every fixed user-facing string through `t`, `tr`, or `$t`; represent persisted Chinese enums as untranslated values with translated display labels. Add a source audit that rejects new bare fixed Chinese in the covered Web files while allowing comments, catalog keys, and raw protocol/user data.

**Tech Stack:** Vue 3, TypeScript, Element Plus, Node built-in test runner, Vite.

## Global Constraints

- Scope is Web only; do not modify the WPF configurator.
- User names, PLC variable names, addresses, NodeIds, API field names, and raw device/MES errors remain unchanged.
- Existing serialized enum values remain unchanged; only their visible labels are translated.
- Language switching must not dirty or rewrite project configuration.
- Follow RED-GREEN-REFACTOR for each implementation task.

---

### Task 1: Internationalization Audit Guard

**Files:**
- Create: `src/IGW.WebUi/src/i18n/projectI18nAudit.test.cjs`
- Modify: `src/IGW.WebUi/src/i18n/catalog.ts`

**Interfaces:**
- Consumes: existing `t`, `tr`, and `$t` conventions.
- Produces: a Node test that reports uncovered fixed Chinese by file and line.

- [ ] Write the failing audit test with literal fixtures for visible template text, visible attributes, notifications, confirmations, and wizard metadata.
- [ ] Run `node --test src/i18n/projectI18nAudit.test.cjs`; verify it fails on current wizard and property editor copy.
- [ ] Implement only the scanner exemptions needed for comments, catalog keys, raw protocol examples, and explicitly marked raw data.
- [ ] Keep the test failing until production copy is routed through i18n.

### Task 2: Engineering Configuration Core and Wizards

**Files:**
- Modify: `src/IGW.WebUi/src/views/Project.vue`
- Modify: `src/IGW.WebUi/src/i18n/catalog.ts`
- Test: `src/IGW.WebUi/src/i18n/projectI18nAudit.test.cjs`

**Interfaces:**
- Consumes: `const { t } = useI18n()` and template `$t`.
- Produces: fully translated toolbar, menus, channel/device wizard, project/channel/device/group/tag property editors, diagnostics, and import flows.

- [ ] Convert script messages and wizard metadata to `t(...)` calls with named parameters.
- [ ] Convert template titles, labels, help copy, empty states, and dialog buttons to `$t(...)`.
- [ ] Replace persisted-enum options with `:label="$t('…')" value="原值"` so values do not change.
- [ ] Add every new key to `catalog.ts` with hand-written English copy.
- [ ] Run the audit and catalog tests; verify this file no longer reports fixed Chinese.

### Task 3: Engineering Configuration Child Dialogs

**Files:**
- Modify as reported by the audit under `src/IGW.WebUi/src/components/*.vue`.
- Modify: `src/IGW.WebUi/src/i18n/catalog.ts`
- Test: `src/IGW.WebUi/src/i18n/projectI18nAudit.test.cjs`

**Interfaces:**
- Consumes: the same `useI18n()` runtime and catalog.
- Produces: translated OPC UA browser, Siemens authentication, CSV/DB/KEP previews, validation, diagnostics, and template dialogs reached from Project.

- [ ] Route fixed component copy through `tr`/`$t` while preserving raw server and field data.
- [ ] Translate display labels for fixed enum/status values without modifying their stored values.
- [ ] Add literal English catalog entries for all new fixed keys.
- [ ] Run the audit and catalog tests until both pass.

### Task 4: Regression and Runtime Verification

**Files:**
- Modify only files required by failures found during verification.

**Interfaces:**
- Consumes: completed Web i18n changes.
- Produces: verified production Web bundle.

- [ ] Run all `*.test.cjs` files with Node's test runner and require zero failures.
- [ ] Run `npm run typecheck` and require zero TypeScript errors.
- [ ] Run `npm run build` and require exit code zero.
- [ ] Open the running Web UI, switch to English, and inspect channel wizard, device wizard, channel/device property editor, and one import or diagnostics dialog.
- [ ] Switch back to Chinese and confirm configuration values and user names were not rewritten.
- [ ] Review `git diff --check` and the final diff for accidental WPF or persisted-value changes.

