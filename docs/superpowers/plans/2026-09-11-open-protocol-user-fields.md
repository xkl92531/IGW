# Open Protocol User Fields Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans and dispatch independent driver, protocol UI and MES field editor work with explicit ownership.

**Goal:** Expose required device connection fields, extensible acquisition fields and configurable business upload fields while preserving existing protocols.
**Architecture:** Standard MID0061 revision1 result catalog expands to the complete known field set; custom read-only result definitions decode bounded slices of that same payload. MES additional fields have typed sources and required validation. Listener input context is saved with its barcode job and recovered by event sequence for later screw-result uploads.
**Tech Stack:** .NET 10, existing SQLite, Vue/WPF, existing JSON codec.

## Constraints
- User approved all three categories. No unrelated changes, commits, deployment or physical device writes.
- Required configuration values validated at configuration use; required dynamic values fail before HTTP transmission and retain event.
- New arbitrary MID/revision execution is not claimed. Custom acquisition decodes only declared supported received frames and cannot replace protocol control/acknowledgement behavior.
- Optional fields never fabricate zero values; metadata from the next barcode must not contaminate earlier results.

## Tasks
- [x] Driver: expose full revision1 catalog; add customResultFields JSON definitions (Name,DataType,Offset,Length,Scale,Required) with address Custom:Name; add supported optional connection parameters; test decode/invaliddefinitions/realwire/eventmapping.
- [x] Protocol UI: required endpoint and connection parameters, expandable supported advanced connection table, full standard result catalog and custom fields editor with validation and examples; WPF parity.
- [x] MES: MesPayload.ExtraFields list (Name,DataType,Source,Value,Path,Required), Source Fixed/Tag/Input/Job; typed JSON composition and dynamic required checks, source binding/preview editors Web/WPF; tests no HTTP on missing required source.
- [x] Core/root: optional TighteningJobStatus.Context snapshot, PrepareAsync optional context, atomic idempotence comparison, lookup by persisted result association. Listener supplies input/mapped snapshot; protocol events expose job/input context from matching historical job; tests two products and replays.
- [x] Integration: verify expanded fields enter event journal/QC/MES; run focused and full backend/frontend suites, typecheck, Web production build, Runtime and WPF build; document actual capability and physical test limits.

Coordinate dotnet builds serially. Use failing tests for behavioral changes before implementation and inspect test output. Preserve original project settings and legacy templates.

## Final verification (2026-09-11)
- Backend: 1446/1446 passed, no skips; `artifacts/protocol-qa/user-fields-final-regression.trx`.
- Frontend: 53 Vitest files, 250/250 passed; extra-field Node tests 3/3 and migration Node tests 3/3 passed. Typecheck and production build passed (dependency PURE annotation warnings only).
- WPF and Runtime build: zero errors and warnings. WPF local-variable collision corrected during integration.
- Review caught mutable Job status changing across restart retries; persistent event-to-job snapshots now cover this, with a disk restart regression.
- Earlier full run observed an existing diagnostics test ordering failure (expected first read count 1, got 2); focused rerun passed and final complete run passed, without changing that test or diagnostics implementation.
- No physical controller, Linux deployment, or visual screenshot verification; no commit/push/deployment.
