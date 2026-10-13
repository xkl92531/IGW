# SSE Production Hardening Specification

## Goal

Keep PLC scan and write paths independent from browser delivery while making the SSE layer safe for one-million-point projects, one hundred concurrent streams, slow networks, project edits, and long-running browser sessions.

## Required behavior

- Point deltas must be emitted as bounded frames: no frame may contain more than 256 values or exceed 1 MiB.
- A stalled response write must time out and release the server-side realtime session.
- A client that resynchronizes, encounters a sequence gap, stops, or changes generation must cancel the old response body before reconnecting.
- HTTP 401, 403, and 400 are terminal until configuration/authentication changes; HTTP 429 retries slowly instead of permanently disabling realtime status.
- Saving MES flow configuration or log retention must not retire point-value streams or restart unrelated OPC UA, MQTT, history, alarm, computed-tag, or scheduling components.
- Point-table, stream-policy, or read-authorization changes must retire old streams and require a new topology revision.
- Ingress fan-out must batch updates per session and avoid allocating a target array for every changed point.
- Browser stores must parse each frame once and notify Vue only when a complete snapshot or a coalesced UI delta is ready.
- Global runtime status must not subscribe to every MES task state; a flow page subscribes only to its selected task.
- Existing low-frequency health polling remains HTTP and PLC writes continue through the existing priority write queue and read-back verification.

## Acceptance evidence

- New regression tests are observed failing before each behavior is implemented.
- Targeted backend and frontend suites pass after each phase.
- Full backend tests, frontend tests, typecheck, production build, and Release build pass at completion.
- A network-level SSE test proves bounded frames and connection cleanup. Existing in-process million-point results remain coordinator-only evidence and are not relabeled as end-to-end PLC evidence.
