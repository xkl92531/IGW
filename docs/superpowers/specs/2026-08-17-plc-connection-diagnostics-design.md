# PLC Connection Diagnostics Design

## Goal

Replace the generic PLC "offline" presentation with structured, actionable diagnostics shared by the runtime, project tree, diagnostics dialog, and monitoring center.

## Scope

- Applies to every runtime device and protocol, not only Siemens.
- Shows current active connection incidents and recovered incidents from the current Runtime process.
- Does not persist incident history to SQLite or project JSON.
- Keeps raw driver errors for troubleshooting while presenting a stable user-facing category and suggestion.
- Separates transport/session failures from data-access failures so a reachable PLC with bad DB/tag addresses is not reported as fully offline.

## Diagnostic Categories

The runtime exposes one of these stable codes:

1. `NetworkAdapterUnavailable`: selected server adapter is missing or has no usable IPv4 address.
2. `NameResolutionFailure`: host name cannot be resolved.
3. `NetworkUnreachable`: route/host is unreachable.
4. `ConnectionRefused`: target host is reachable but the configured TCP port rejects the connection.
5. `ConnectionTimeout`: connection or handshake timed out.
6. `ProtocolMismatch`: PLC model, protocol variant, rack/slot, unit ID, or protocol handshake is incompatible.
7. `AuthenticationRequired`: anonymous access is rejected and credentials are required.
8. `AuthenticationFailed`: configured credentials were rejected.
9. `SecurityPolicyMismatch`: certificate, security policy, signing, encryption, or trust validation failed.
10. `PermissionDenied`: PLC protection or account permissions deny the requested operation.
11. `DataAddressMismatch`: the transport/session is online but DB, symbol, register, tag, or address is invalid.
12. `PartialDataFailure`: the connection is usable but only part of the configured data can be read.
13. `RemoteClosed`: the remote endpoint closed an established connection.
14. `UnknownConnectionFailure`: no stable classification matched; raw error remains visible.

Explicit driver detail codes take precedence. Exception types, socket error codes, OPC UA status names, protocol messages, and lastly conservative keyword matching provide the cross-driver fallback.

## Runtime Model

Each `DeviceLoop` owns an in-memory incident tracker. An active incident contains:

- device and channel name;
- driver/protocol identifier;
- category code and localized-neutral Chinese label;
- concise cause and recommended action;
- sanitized raw error;
- first occurrence, latest occurrence, occurrence count;
- retrying flag and next retry delay;
- connection state and whether the failure affects transport or only data access.

Repeated failures with the same category update the existing incident. A category change moves the prior incident to the recovered ring and opens a new incident. A confirmed successful connection/read closes the active incident. The recovered ring is bounded to 200 entries per Runtime process.

`LastError` is updated on connection, read, and write exceptions and cleared only after confirmed recovery. Driver-provided `IDeviceConnectionDiagnostics` detail remains authoritative for Siemens and network adapter binding.

## API and Presentation

- `/api/v1/devices` adds a structured `diagnostic` object without removing existing fields.
- `/api/v1/anomalies` includes category, suggestion, raw error, timestamps, count, retry state, protocol, and scope for device-root incidents.
- A read-only endpoint returns active and recovered connection incidents for the monitoring table.
- Project-tree device badges show a short category label; the tooltip shows cause and suggestion.
- Monitor adds a searchable connection-diagnostics table above the general anomaly list. Filters cover active/recovered state, station/channel, device, protocol, and category.
- The UI never translates raw device/driver text, but all stable category labels and table headings use the existing i18n catalog.

## Error Handling and Safety

- Raw errors are flattened and limited to 2,000 characters to prevent oversized API payloads and unreadable tables.
- Credentials, authorization headers, and URI user-info are redacted.
- Unknown errors are never discarded or falsely classified.
- Standby devices without collection demand remain `Standby`, not connection incidents.
- Partial DB/tag failures remain connected and are classified as data-access incidents.

## Testing

- Unit-test classification for socket refusal, timeout, unreachable network, DNS failure, authentication, security checks, permission, protocol mismatch, data-address mismatch, remote close, and unknown fallback.
- Unit-test incident lifecycle: repeat aggregation, category transition, recovery, bounded history, and standby exclusion.
- API tests verify backward compatibility and structured fields.
- Web tests verify project-tree labels/tooltips, monitoring filters, and active/recovered presentation.
- Run the full Node test set, Vue type check/build, .NET tests, and solution build.
