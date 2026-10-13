# Interface Listener Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans; independent helper and UI tasks may run in parallel, root owns engine integration.

**Goal:** IGW subscribes to backend WebSocket/SSE and processes changed JSON values, with reusable custom JSON input/output configuration.
**Architecture:** MesTask.Listener feeds a durable SQLite inbox and independent ordered worker; typed JSON helpers are shared by listener and HTTP nodes. The existing tightening coordinator remains the barcode transaction owner.
**Tech Stack:** .NET 10, ClientWebSocket, HttpClient, SQLite, Vue.
**Spec:** docs/superpowers/specs/2026-09-11-interface-listener-design.md

## Constraints
- Preserve existing dirty work, legacy templates, periodic tasks and protocol events; no physical device writes or deployment.
- No silent event dropping or barcode replacement; invalid JSON and missing required paths report failures.
- SSE is GET-only; subscription and acknowledgement messages apply to WebSocket.

## Tasks
- [x] Add model contracts in MesModel.cs: MesTask.Listener, MesHttp.JsonTemplate/ResponseMappings, MesJsonMapping and MesInterfaceListener.
- [x] Implement and test MesJsonCodec in a new file: TrySelect(root,path), Render(template,context), Map(root,mappings). Test nested arrays, quotes, numeric/bool types and missing paths.
- [x] Implement and test MesInterfaceClient in a new file: continuous WebSocket/SSE messages with bounded frame size, cancellation, headers, SSE id/event parsing and serialized WebSocket send.
- [x] Implement separate interface inbox table and tests for deduplication, conflict, cursor, baseline and ordered retry.
- [x] Integrate InterfaceEvent engine loop, response mapping and typed request JSON; connect GatewayHost coordinator and test listener-to-job/HTTP/PLC behavior.
- [x] Add Web/WPF listener and JSON editors with previews and component tests; retain existing controls and defaults.
- [x] Run focused backend/frontend suites, full regressions, typecheck, production Web build and WPF/Runtime builds; document interface examples and physical limitations.

Run backend with `dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore`; serialize dotnet processes. Frontend focused tests use npm vitest with at most 2 workers. Root reviews integration and runs final verification. No commit/push is part of this work.
