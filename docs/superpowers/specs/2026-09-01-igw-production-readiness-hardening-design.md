# IGW Production Readiness Hardening Design

## Goal

Remove the release-blocking security, PLC data-integrity, concurrent-save, and SSE consistency defects found in the 2026-09-01 code review without changing existing project topology or silently weakening write verification.

## Compatibility rules

- Existing channels, devices, tag identifiers, MES flow graphs, database files, and portable project data remain loadable.
- Authorization is evaluated against canonical path segments, not display names.
- A user may be authorized for any number of branches across different PLC data blocks. For example, `PLC1.DB10` and `PLC1.DB20` may both be configured. Fixing `PLC1.DB10` so it no longer matches `PLC1.DB100` must not restrict cross-DB tag selection.
- Configuration selectors continue to enumerate all tags allowed by the authenticated user and display the complete actual path when names collide.
- Read/write tags keep the existing write-then-read-back behavior and retry policy until the latency policy is changed explicitly.
- Pure write tags are never reported as read-back verified. They require either a configured confirmation tag or an explicit protocol-acknowledged mode.
- Low-frequency pages continue to use HTTP. PLC scan and write execution remain independent of browser SSE delivery.
- Existing insecure projects are migrated without discarding credentials. New projects use the hardened defaults.

## 1. Authorization and secret handling

### Canonical branch matching

Introduce one shared branch matcher used by REST, DCS-compatible operations, SSE request validation, and configuration tag lookup.

A tag is within a branch only when one of the following is true:

1. the normalized tag ID equals the normalized branch; or
2. the tag ID begins with the branch followed by the canonical `.` path separator.

Whitespace and redundant trailing separators are removed before comparison. Matching remains case-insensitive to preserve current project behavior. Multiple permitted branches are ORed, so cross-DB selection remains supported.

Tests must cover exact matches, descendants, sibling-prefix rejection (`DB10` versus `DB100`), multiple DB branches, empty branches, and the same rules through REST, DCS, and SSE entry points.

### Redacted project API

`GET /api/v1/project` must return an editable project DTO that never contains reusable credentials. User passwords, MQTT passwords, MES bearer tokens, and other connection secrets are replaced by presence metadata and an unchanged-secret sentinel understood only by the write endpoint.

Only authenticated writers may obtain the editable project DTO or save project configuration. Read-only users use dedicated status/tag endpoints and cannot download the complete project model.

`PUT /api/v1/project` and targeted MES saves merge unchanged secret fields from the live project under the project update lock. A client may replace a secret by supplying a new value or clear it through an explicit `clearSecret` operation; an omitted or sentinel field preserves the current value.

### Credential storage and migration

- API user passwords are stored as versioned PBKDF2 hashes with a per-user random salt. Authentication accepts legacy plaintext once, rewrites it to a hash during the next successful locked save, and never exposes either representation through REST.
- MQTT passwords, MES tokens, and other reversible credentials are encrypted with AES-GCM using a random installation master key stored beside the database in the application data directory. The key file is required when migrating a live development/installation data set to another computer.
- Existing plaintext reversible credentials are encrypted during a successful project save. A failed migration leaves the original file untouched and logs a clear error without printing the secret.
- New projects enable REST authentication by default. Existing projects with security disabled remain startable but emit a persistent high-severity health warning until an administrator enables authentication.
- HTTPS termination is supported and documented, but not made mandatory in this compatibility phase. When plain HTTP is exposed on a non-loopback address, health diagnostics report an explicit security warning.

## 2. Atomic project updates

Every project mutation accepts an `expectedRevision` and follows one critical section:

1. acquire the project update gate;
2. compare `expectedRevision` with the live revision;
3. clone the live project;
4. apply the requested mutation and merge unchanged secrets;
5. reconcile topology and validate;
6. save using atomic file replacement;
7. update the live project and revision;
8. restart only the affected runtime components;
9. release the gate.

A stale request returns HTTP 409 and cannot save anything. Full saves and targeted MES saves use the same coordinator so they cannot overwrite one another. Tests use two requests with the same base revision and prove exactly one succeeds.

## 3. PLC read and write truthfulness

### Siemens WSTRING

WSTRING reads return `Bad` with the driver error when the address is invalid, the length word cannot be read, the current length is outside the declared maximum, a character read fails, or only part of the string is available. Valid zero-length strings remain `Good` and empty. Driver diagnostics include the remote endpoint, actual PLC address, declared capacity, current length, and failing offset without logging sensitive business values by default.

### Generic write confirmation modes

Add a protocol-neutral confirmation definition to writable tags:

- `ReadBack` (default for readable writable tags): read the same tag and compare its normalized value.
- `ConfirmationTag`: read a configured tag and compare it with a configured or derived expected value.
- `ProtocolAcknowledged`: accept a successful driver write response but return a distinct `GoodUnverified` result and log that no PLC read-back occurred.

Pure write tags without a valid confirmation strategy fail validation before runtime. They are not sent and are reported as configuration errors rather than connection faults. A write mismatch or point-level write exception must not close the whole device session unless the driver health probe confirms the transport is unusable.

The driver capability snapshot determines which confirmation modes are available. OPC UA, Siemens S7, Modbus, Mitsubishi, EtherNet/IP, MQTT-backed drivers, and the simulator share the same engine contract; protocol-specific implementations only provide capability and transport results.

## 4. SSE frame, reconnection, and state consistency

### Byte-bounded protocol

- Every serialized SSE event, including `hello`, snapshots, deltas, runtime events, and errors, must remain at or below 1 MiB.
- `hello` reports accepted and missing counts plus a byte-bounded sample; complete large missing lists are obtained through a paged HTTP diagnostics endpoint.
- Snapshot and delta chunking uses measured UTF-8 serialized byte size, not only item count.
- A single oversized value is represented by an explicit `value-too-large` state containing its tag ID and measured size; it must not terminate the stream.
- If headers have already been sent, a recoverable protocol error is emitted as a terminal SSE event before the lease is released whenever the response remains writable.

### Client lifecycle

Each connection attempt creates its own abort controller and stale timer before `fetch`. Timers are cleared in `finally`, and every retry starts a new timer even when no frame was received. Tests cover first-attempt timeout, a second attempt that hangs before headers, explicit stop, generation replacement, 401/403 terminal handling, and 429 delayed retry.

### Per-stream state and retained-state deletion

Connection state is tracked separately for device runtime, point values, MES status, and selected MES flow. One stream receiving `hello` cannot mark another stream live. Cached entity states carry a stream generation and timestamp and are ignored after their owning stream becomes stale.

MES task removal and topology replacement publish tombstones or a generation reset. Current clients delete obsolete tasks immediately; new clients receive only retained states from the current generation.

The preferred connection model is one multiplexed runtime-state stream per browser plus dedicated point-value streams only for pages that display live values. The configured limit remains a stream limit and the UI/health page reports both active streams and estimated browser sessions.

## 5. Performance and operational observability

- `/health` uses the coordinator's cached metrics snapshot. It never enumerates every stream for every browser poll.
- Quick Client applies full snapshots to Vue in bounded batches across animation frames and keeps only the visible/page working set reactive.
- Write telemetry separates driver response time, read-back verification time, retry time, final verification status, and connection-reset reason.
- The current ten-retry behavior remains configurable per tag or device. The first write response SLA and final verified completion SLA are reported separately.
- Security and protocol diagnostics never log passwords, tokens, complete payloads, or other configured secrets.

## 6. Reproducible test and release gate

- `tests/IGW.Tests/IGW.Tests.csproj` and all required test sources are committed, no longer ignored, and included in `IGW.slnx`.
- CI fails when zero tests are discovered.
- Backend tests cover authorization boundaries, secret redaction/migration, atomic revision conflicts, WSTRING failures, confirmation modes, and MES tombstones.
- Frontend tests cover per-stream state, stale reconnects, generation changes, deletion events, and chunked UI application.
- Network-level SSE tests use the real ASP.NET pipeline and prove the 1 MiB limit, stalled-client cleanup, large snapshot completion, and project-revision retirement.
- Dependency locks are updated to remove the known PostCSS and nanoid advisories, followed by a production build and audit.
- The million-point benchmark remains in-process evidence. A separate acceptance run covers 100 real SSE TCP connections, simultaneous initial snapshots, reconnect storms, and browser parsing. Real PLC read/write acceptance is recorded separately for each available protocol and is never inferred from simulator results.

## Delivery phases

1. Authorization boundary, redacted configuration, secret migration, and atomic project saves.
2. WSTRING truthfulness and generic write confirmation.
3. SSE byte bounds, reconnect lifecycle, per-stream state, and tombstones.
4. Health/UI performance, dependency upgrades, committed tests, CI, and end-to-end capacity evidence.

Each phase follows red-green-refactor, is independently reviewable, and leaves the full Release build and all previously committed tests green before the next phase starts.

## Acceptance criteria

- Cross-DB authorized selection works for multiple configured branches while sibling-prefix tags are rejected.
- No REST response contains a reusable credential or credential hash.
- Two concurrent saves from the same revision cannot both succeed.
- PLC read failures never appear as `Good` values.
- Every successful write result identifies whether it was read-back verified, confirmation-tag verified, or protocol-acknowledged only.
- No point-level write failure closes a healthy device session.
- Every legal SSE request either remains within the event-size limit or receives a structured recoverable/terminal protocol result instead of an abrupt broken stream.
- A stale or replaced SSE connection cannot keep a page globally live or retain deleted MES tasks.
- `dotnet test IGW.slnx` executes the expected backend test count, frontend tests and type checking pass, and CI rejects zero-test runs.
- Performance claims distinguish in-process coordinator, real network/browser, simulator PLC, and real PLC evidence.
