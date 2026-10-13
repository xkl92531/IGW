# Public Tag-Only PLC API Design

**Date:** 2026-08-10

## Goal

Expose one public PLC API surface in Swagger. Keep the existing `/Tag/*` request and response contracts, present them as IGW PLC operations, and remove the unused native `/api/v1/plc/*` API family.

## Confirmed behavior

- Swagger document title is `IGW PLC 接口`.
- Swagger contains only endpoints whose route starts with `/Tag/`.
- The Swagger tag shown for every `/Tag/*` endpoint is `PLC 操作`.
- Existing `/Tag/*` paths, HTTP methods, request models, response envelopes, and DCS-compatible value conversion remain unchanged.
- All `/Tag/*` endpoints are anonymous at the IGW HTTP security layer. They do not require HTTP Basic credentials and do not perform user read, write, or branch permission checks.
- Swagger no longer advertises a Basic authentication scheme or global security requirement.
- Internal `/api/v1/*` management endpoints keep their existing project-security behavior.
- The five native `/api/v1/plc/*` endpoints are removed rather than merely hidden. Requests to them return 404.
- `/Tag/AddTags` is included in the anonymous public surface and can update the project point configuration.
- Gateway licensing and the underlying runtime/device availability behavior are not redefined by this change. The change removes user permission checks only.

## Source changes

### Public Tag API

Retain `DcsTagCompatibilityApi` as the implementation of the established wire contract. Change its endpoint metadata tag from `DCS 兼容接口` to `PLC 操作`. Internal class and contract names may continue to use `Dcs` because they describe compatibility implementation details that are not exposed in Swagger.

### Native PLC API removal

Remove the `MapPlcApi` registration from Runtime. Delete `PlcApi.cs` and its dedicated regression test after confirming no production caller exists. The repository search performed before this design found no Runtime, Web UI, or other business-code caller of `/api/v1/plc/*`; only the endpoint implementation, Runtime registration, security classification, and its regression test referenced it.

### Swagger

Update all public document labels to `IGW PLC 接口`. Describe the API as PLC point query, read, write, validation, and configuration operations without advertising a second DCS group. Restrict document inclusion to `/Tag/*` paths. Remove BasicAuth schema and global security requirements because every documented endpoint is anonymous.

### Security middleware

Stop classifying `/Tag` and `/Tag/*` as protected paths. Keep `/api/v1` protected when project security is enabled. Remove obsolete DCS/native-PLC read/write permission classification helpers if no remaining production reference exists.

The middleware remains before endpoint mapping. An anonymous `/Tag/*` request passes through unchanged; a protected `/api/v1/*` request continues through the existing authentication and authorization checks.

## Compatibility and removal boundary

- Preserved: all eleven `/Tag/*` route/method pairs and their JSON contracts.
- Preserved: DCS alias resolution, automatic type mapping, PLC read/write behavior, and atomic point updates.
- Removed: five `/api/v1/plc/*` route/method pairs.
- Removed: public Swagger BasicAuth controls.
- Not changed: MES routes, project/configuration routes, licensing routes, OPC UA, drivers, or Web UI behavior.

## Error behavior

- Removed `/api/v1/plc/*` routes use the normal ASP.NET Core 404 response.
- `/Tag/*` validation, missing-point, bad-quality, conversion, and runtime errors keep their existing DCS-compatible response envelope.
- Anonymous access does not turn a disconnected PLC or invalid write into success; existing operation-level errors remain authoritative.

## Test design

Use test-first changes and verify each failing test before production edits.

1. Endpoint metadata tests require all eleven `/Tag/*` endpoints to use the `PLC 操作` tag.
2. Route-policy tests require Swagger inclusion for `Tag/*` only and require `/Tag/*` to be outside the protected path set.
3. Runtime integration tests require:
   - Swagger title `IGW PLC 接口`;
   - no BasicAuth security scheme or global requirement;
   - no `/api/v1/plc/*` entries;
   - all expected `/Tag/*` entries;
   - an enabled project-security configuration does not challenge `/Tag/*`;
   - an internal `/api/v1/*` route still returns 401 without credentials.
4. A route-removal test requires representative `/api/v1/plc/*` requests to return 404.
5. Run the focused API tests, the complete `IGW.Tests` suite, and a Release build.

## Acceptance criteria

- Swagger presents a single `PLC 操作` group under the title `IGW PLC 接口`.
- Only `/Tag/*` PLC endpoints are present in Swagger.
- `/Tag/*` succeeds or returns its normal operation-level result without HTTP authentication credentials, even when project security is enabled.
- Native `/api/v1/plc/*` routes no longer exist.
- Internal management API security remains active.
- Existing `/Tag/*` contract tests and the full test suite pass.
