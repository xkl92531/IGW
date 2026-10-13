# IGW System Copy I18n Follow-up Implementation Plan

> **For agentic workers:** Use superpowers:test-driven-development and superpowers:verification-before-completion.

**Goal:** Remove the remaining Chinese IGW-owned copy from English mode without translating project data or raw third-party diagnostics.

**Architecture:** Keep translation at the Web UI presentation boundary. Pure presentation helpers and runtime mark formatters receive the existing translation function; the catalog remains the single English message source.

**Tech Stack:** Vue 3.5, TypeScript 5.7, Node test runner, Vite 6.

### Task 1: Reproduce the missing translations

**Files:**
- Modify: `src/IGW.WebUi/src/utils/deviceRuntime.test.cjs`
- Create: `src/IGW.WebUi/src/utils/projectPresentation.test.cjs`
- Create: `src/IGW.WebUi/src/utils/licensePresentation.test.cjs`

- Add English translator fixtures and assert observable English output.
- Run the focused tests and verify they fail because the presentation APIs do not yet support translation.

### Task 2: Implement project and runtime presentation translation

**Files:**
- Create: `src/IGW.WebUi/src/utils/projectPresentation.ts`
- Modify: `src/IGW.WebUi/src/utils/deviceRuntime.ts`
- Modify: `src/IGW.WebUi/src/views/Project.vue`
- Modify: `src/IGW.WebUi/src/i18n/catalog.ts`

- Route tree roots, collection marks and MES flow dialog title through presentation helpers.
- Add an optional translator to device/channel runtime marks while keeping their current Chinese default contract.
- Pass `t` from `Project.vue` and add parameterized English catalog entries.

### Task 3: Implement structured license presentation

**Files:**
- Create: `src/IGW.WebUi/src/utils/licensePresentation.ts`
- Modify: `src/IGW.WebUi/src/views/GatewaySettings.vue`
- Modify: `src/IGW.WebUi/src/i18n/catalog.ts`

- Derive known license messages from `status`, `expiry` and `daysLeft`.
- Translate the IGW-owned trial customer label and preserve real customer names.
- Keep the backend message only as an unknown-status diagnostic fallback.

### Task 4: Verify

- Run focused presentation and catalog tests.
- Run all Web UI Node tests.
- Run `vue-tsc --noEmit` and the Vite production build.
- Inspect the final diff and preserve unrelated `installer/version.txt` changes.
