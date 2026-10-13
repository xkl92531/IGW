# Adaptive acquisition verification: IGW 1.6.43 corrections and 1.6.42 baseline

Date: 2026-09-07 (Asia/Shanghai). Worktree: `E:\项目\IGW\.worktrees\layered-acquisition-diagnostics`; branch: `codex/layered-acquisition-diagnostics`; approved product baseline: `94009d6`.

This report separates automated protocol-stub and runtime verification from field acceptance. The KEPServer worksheet below is deliberately unpopulated. No real PLC3 throughput, KEP parity/superiority, installation runtime, or deployment acceptance is claimed.

## Final review correction wave

After the original 1.6.42 packaging, the whole-branch review found four composed failure-path issues. The correction wave changes product source. The original 1.6.42 identity below is **historical and does not contain these final corrections**; the final correction delivery is 1.6.43. The old 1.6.42 is retained.

- OPC UA now preserves completed chunks and retries only the failed chunk's cursor range. The entire recovery span is excluded from normal capacity learning; a minimum failed node gets its original failure quality and does not prevent trailing nodes from completing. Transport/session failures still use the existing exception/reconnect path. Tests call the same read/decode/recovery method used by the live Session adapter with a substituted protocol boundary.
- S7 Plus now accepts only the distinct `CapacityFailure=33` as capacity-learning evidence. Generic `ProtocolFailure`, framing/VLQ/crypto failures and local ABI `BufferTooSmall` do not reduce the request window. **The pinned s7commplus 0.1.0 library does not provide a verified PLC capacity return-code mapping. The Rust adapter deliberately does not guess one: actual automatic S7 Plus downshift is active only if a future native boundary positively returns CapacityFailure; currently unknown protocol failures keep the original window.** Positive tests use the explicit capacity contract, not a claim that this PLC status was observed in the field. This conservative implementation boundary was accepted in the final correction instructions.
- A persistent S7 Plus singleton capacity rejection produces one bad sample and an exact tag path/symbol/quality/category diagnostic; earlier and trailing good samples are preserved. The transport stays connected and Hybrid exposes the point-specific partial-online diagnostic.
- Every actual `read_batch` invocation in the managed ABI buffer loop is measured separately, including the second native read after output-buffer expansion. Priority/cancellation is checked before each attempt. Successful PLC reads that only require local output storage growth are not protocol failures; the combined local retry is excluded from capacity learning. The same measured boundary is used for scalar and array reads. ABI tests exercise allocation, resize, per-attempt calls and payload decoding in the production buffer loop, not just a materialized fake Read result. This counts native protocol read attempts, not TCP segments.

| Correction-wave check | Result / local log |
|---|---|
| OPC UA recovery/window, Plus recovery/window/arrays, subscriptions, Hybrid | 47 passed, 0 failed/skipped; `final-fix-focused.log` |
| Full backend `dotnet test IGW.slnx --no-restore --nologo` | 1083 passed, 0 failed/skipped, 28 s; `final-fix-backend-full.log` |
| Rust `cargo test --offline` in `native/igw-s7plus` | 10 passed (2 classifier unit + 2 ABI + 6 FFI), 0 failed/ignored; `final-fix-native.log` |
| Frontend Vitest | 206 passed in 46 files; `final-fix-frontend.log` |

The focused frontend invocation used `pnpm test -- --run src/utils/signalrTransport.spec.ts`; the existing package script forwarded `--` and actually executed the full suite. The focused backend filter was `FullyQualifiedName~OpcUaReadRecoveryTests|FullyQualifiedName~OpcUaAdaptiveBatchTests|FullyQualifiedName~SiemensPlusReadRecoveryTests|FullyQualifiedName~SiemensPlusAdaptiveBatchTests|FullyQualifiedName~SiemensPlusArrayTests|FullyQualifiedName~DeviceLoopSubscriptionTests|FullyQualifiedName~SiemensHybrid`. No deliberate failing run was executed.

Final `vue-tsc --noEmit` exited 0 (`final-fix-typecheck.log`). The complete existing `installer/build.bat` pipeline exited 0: Rust release compilation, frontend frozen-lockfile install/build (Vite 14.02 s), self-contained win-x64 Runtime publish and Inno compile (31.250 s). Version was incremented by its bump script from 1.6.42 to 1.6.43. Post-build installer tests passed 4/4 (`final-fix-installer-tests.log`).

| Final correction artifact | Verified value |
|---|---|
| Worktree output | `E:\项目\IGW\.worktrees\layered-acquisition-diagnostics\installer\out\IGW-Setup-1.6.43.exe` |
| Delivery copy | `E:\项目\IGW\installer\out\IGW-Setup-1.6.43.exe` |
| Size, both copies | 45,177,416 bytes (43.0845 MiB) |
| SHA256, both copies | `533F838FD7E13F6D5BA572765FEEE2909F4F4A22D3CAE2647FB4B3B7B7B43191` |
| Signature, both copies | NotSigned |
| Product version | 1.6.43 |

Both old 1.6.42 copies retain their earlier SHA256; nothing overwrote them. The original dirty root's Git status is identical before/after the new artifact copy. Before compiling, SHA256 snapshots of all six modified production/native input files were saved in `final-fix-build-source-hashes.json`; after packaging they matched unchanged. Inno's log packages this worktree's freshly published `dist/IGW`. The managed informational version still names pre-commit base `7c965397529b9212694396e50de906536ae6b4e0` because final source/version/evidence are committed together after the build; that metadata alone must not be mistaken for absence of the working-tree corrections. Published DLL hashes below identify the newly built inputs. This is build provenance, not installed-service acceptance.

| Newly published input | SHA256 |
|---|---|
| IGW.Drivers.IoTClient.dll | `2DE6C2F1040D662EEC63616BB0CF465A3532FE7467737F6A35C05C78395BB8F4` |
| IGW.Drivers.OpcUaClient.dll | `BA39ADD337FCBBD8B29A3A20553428FF9EE57E06E1007CA648A4FC87B3822CA8` |
| igw_s7plus.dll | `7CE2DE97EA6A21B891000EFA9186E1BF01AFDFCE84628AB18829BDC0CC2F29B6` |

Two extra EOF blank lines in the SignalR frontend/backend tests were removed. The local `.superpowers` ledger records Task 1–7 completion and the final correction wave; it remains untracked by design.

## SignalR retirement test follow-up

Independent repetition found that `IgwSignalRTests.Cancelling_one_stream_keeps_other_lease_and_project_retirement_is_explicit` could read an already queued `s2` tag delta after retirement before the required `project-changed` event. The test now consumes the legal sequence within at most 16 returned frames and one overall 10-second deadline. It skips completion frames (`type=3`) and, while waiting for retirement, residual `s2` tag deltas. Other frame types, stream IDs and events still fail immediately. The test must receive `s2`'s `project-changed`, assert `projectRevision == "next-revision"`, and verify all leases retire. The test wire links the overall deadline to its existing receive timeout, including buffered-frame processing.

Implementation preceded verification; no deliberate failing run was used. The exact command below passed in 10 consecutive independent invocations (1 passed, 0 failed/skipped each). Logs are `signalr-retirement-1.log` through `signalr-retirement-10.log` with aggregate `signalr-retirement-repeat.log` in the local `.superpowers` evidence directory.

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --nologo --filter FullyQualifiedName~Cancelling_one_stream_keeps_other_lease_and_project_retirement_is_explicit
dotnet test IGW.slnx --no-restore --nologo
```

The subsequent full backend run passed **1083/1083**, 0 failed/skipped, 26 s (`signalr-retirement-backend-full.log`). This follow-up changes only tests and verification records. Product source and version remain unchanged; the installer was not rebuilt. Both 1.6.43 copies were rehashed and retain `533F838FD7E13F6D5BA572765FEEE2909F4F4A22D3CAE2647FB4B3B7B7B43191`, so the already packaged product content is unaffected.

## Original 1.6.42 verification commands and results

Run from the worktree root using .NET SDK 10.0.302, Node 22.23.1 and pnpm 11.25.0. No intentional failing/TDD run is part of this workflow. Logs are retained locally in `.superpowers/sdd/2026-09-07-adaptive-driver-acquisition-performance/task-7-*.log` (not tracked).

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --nologo --filter "FullyQualifiedName~SiemensClassicReadPlannerTests|FullyQualifiedName~DriverProtocolTimingTests" --logger "console;verbosity=normal"
dotnet test IGW.slnx --no-restore --nologo
cmd /c "cd src\IGW.WebUi && set COREPACK_ENABLE_PROJECT_SPEC=0&& pnpm test -- --run"
cmd /c "cd src\IGW.WebUi && set COREPACK_ENABLE_PROJECT_SPEC=0&& pnpm exec vue-tsc --noEmit"
cmd /c "cd src\IGW.WebUi && set COREPACK_ENABLE_PROJECT_SPEC=0&& pnpm build"
dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --nologo --filter FullyQualifiedName~AcquisitionDiagnosticsNoReadTests
dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --nologo --filter FullyQualifiedName~IgwSignalRTests
dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --nologo --filter "FullyQualifiedName~PerformanceEvidence|FullyQualifiedName~Plc3_block_and_scalar|FullyQualifiedName~Latency_shaped|FullyQualifiedName~Logical_devices_share_resolved_remote|FullyQualifiedName~Waiting_trigger_age_tracks" --logger "console;verbosity=detailed"
cmd /c installer\build.bat
node --test installer\bump_version.test.cjs installer\build.runtime.test.cjs installer\IGW.firewall.test.cjs
```

| Automated check | Final result |
|---|---|
| Classic planner / protocol timing fixture | 63 passed, 0 failed, 0 skipped |
| Complete backend `IGW.slnx` | 1074 passed, 0 failed, 0 skipped (27 s test duration) |
| Frontend Vitest | 46 files; 206 passed, 0 failed, 0 skipped |
| `vue-tsc --noEmit` | Exit 0 |
| Frontend production build | Exit 0; Vite 25.38 s |
| Acquisition diagnostics no-I/O | 12 passed, 0 failed, 0 skipped |
| SignalR contracts | 20 passed, 0 failed, 0 skipped |
| Detailed performance/session and failure follow-up | 5 passed, 0 failed, 0 skipped |
| Installer script tests | 4 passed, 0 failed, 0 skipped |

The first full backend run had 1072 passes and two failures. `RuntimeNetworkDiagnosticsTests.Logical_devices_share_resolved_remote_with_unverified_system_route_and_release_last_lease` still expected one driver for two distinct device IDs; its expectation is corrected to two, retaining shared DNS/probe and lease cleanup assertions. `MesHttpDefaultsTests.Waiting_trigger_age_tracks_the_latest_collection_cycle` observed 172 ms against an unchanged 150 ms bound during concurrent frontend production compilation; it passed both focused and complete follow-up runs without any MES change. This is consistent with scheduling sensitivity, not proof of its cause. The initial new PLC3 fixture also mistakenly treated parser Area as DB number; it now uses `SiemensAddressDescriptor.DbNumber`. Initial logs remain available; there were no intentional failures and no production source changes in this release-evidence task.

## Original 1.6.42 installer identity and provenance (retained)

`installer/build.bat` completed successfully: Rust S7 Plus release component, frozen-lockfile frontend install/production build, self-contained win-x64 Runtime publish, and Inno Setup compilation (30.125 s). It incremented `installer/version.txt` from 1.6.41 to 1.6.42 using the existing bump script. All required tools were already available; no reusable tool downloads were needed.

| Artifact property | Verified value |
|---|---|
| Worktree output | `E:\项目\IGW\.worktrees\layered-acquisition-diagnostics\installer\out\IGW-Setup-1.6.42.exe` |
| Delivery copy | `E:\项目\IGW\installer\out\IGW-Setup-1.6.42.exe` |
| Product version | 1.6.42 |
| Size | 45,181,504 bytes (43.0884 MiB) |
| SHA-256 (both copies) | `615FB71B3361A9F07FC37AFED995B144C2FF26671BF96200F0B4DBBA2481B1FB` |
| Authenticode | `NotSigned` — no digital signature |
| Installer tests after packaging | 4/4 passed |

The build resolved `dist\IGW` inside this worktree. Inno's log records packaging that directory, including current frontend assets. Published `IGW.Runtime.dll` and `IGW.Core.dll` both report `1.0.0+94009d6b36389bd6cce4a1a8290c35545c36ab87`, matching the current approved product source at build time; the release commit adds only tests, this document and installer version metadata. Published native DLL SHA256 matches the worktree's Rust release DLL. No installer from the older 1.6.41 release was reused. This proves build input provenance; the installer has not been installed/executed as a service in this task.

The delivery copy was verified against the source SHA256. The original `E:\项目\IGW` dirty source tree's porcelain Git status was identical before and after copying. Only its generated installer output was written; no merge, push or deployment was performed. Nonfatal build output includes the Rust linker informational warning and Vite's removal of misplaced PURE annotations.

```powershell
$artifact = Resolve-Path 'installer\out\IGW-Setup-1.6.42.exe'
Get-Item $artifact | Select-Object FullName,Length
Get-FileHash $artifact -Algorithm SHA256
Get-AuthenticodeSignature $artifact | Select-Object Status,StatusMessage
```

## PLC3 fixture evidence

`SiemensClassicReadPlannerTests.PerformanceEvidence.cs` populates all 72 configured fixture points with nonzero booleans, integers, floats and distinct strings. It compares the independent expected values against both one-point reads and a single logical codec batch. Each value and quality must match exactly, all IDs must be present exactly once, and all 72 qualities must be Good. This is a fixed test image, not a real PLC image.

At payload 880 the initial Classic plan has five bounded ranges. The scalar baseline performs 72 requests; the block path performs five (67 fewer; 93.06% reduction). The existing block decoder tests explicitly decode several configured members from one response image, including nonzero response offsets and member-specific bad quality. Ranges never cross DBs; only configured bounded ranges are requested. Source review of `SiemensClassicBlockCodec.ReadRange` shows each response is decoded into values and not stored in a persistent whole-DB cache; execution establishes decode/membership parity, not a heap-lifetime measurement. The codec returns one logical result. `DeviceScanCapabilityTests.Driver_owned_partitioning_compiles_one_logical_batch_for_all_due_tags` separately verifies one 72-tag logical plan, and source review of `DeviceLoop` confirms it calls `Publish(samples, block, writeEpochAtReadStart)` once after the driver read returns.

| DB | Start byte | Count bytes |
|---|---:|---:|
| 2100 | 0 | 864 |
| 2100 | 864 | 798 |
| 2100 | 1662 | 798 |
| 2100 | 2460 | 472 |
| 2101 | 0 | 106 |

Detailed run measured completion P95 **437.371 ms** at window 25 / four requests per batch and **77.528 ms** after rollback to window 20 / five requests per batch (82.27% lower in this artificial latency case). Delays are requested durations; Windows timer scheduling makes elapsed delays longer. No claim depends on the absolute timings being portable.

The latency-shaped protocol stub makes requests of 25 bytes take 100 ms and smaller requests take 1 ms. Twenty successful 20-byte requests train a growth probe; five 25-byte requests cause rollback to 20. Twenty complete 100-point batches per window are measured with Stopwatch and nearest-rank P95. The declared 25-byte comparison uses four requests/batch; adaptive rollback uses five. The test asserts the smaller window lowers batch completion P95 despite more requests. This deliberately shaped latency illustrates the selection objective; it predicts no real PLC speedup. The adaptive controller uses normalized request cost for rollback, and the test measures its effect on complete batches.

## Session and zero-I/O evidence

`DeviceSessionPerformanceEvidenceTests` runs two DeviceLoops with distinct device identities at the same endpoint. Polling and queued priority writes (including readback) use one primary session per device; reconnecting A changes only A's generation from 1 to 2, B remains at 1, and each driver's maximum simultaneous live sessions stays 1. `SiemensHybridSessionLifecycleTests` additionally holds old transport disposal open and verifies replacement cannot overlap or reopen the old generation.

`AcquisitionDiagnosticsNoReadTests` executes RuntimeEngine at one endpoint with separate device identities, checks independent fingerprints/sessions, and repeatedly queries cached diagnostics and exports ZIP while a scheduled read is held. It asserts no added reads, writes, connections, sessions, browsing, retries, batch downshifts, scheduler work or network probes. Unknown request/session limits remain null. `IgwSignalRTests` and frontend realtime tests verify point read/write and realtime contracts continue through SignalR; configuration/log/ZIP operations remain HTTP. This is automated contract verification, not browser screenshot or field verification.

## KEPServer / IGW field A/B worksheet — not run

1. Record the computer/OS/CPU/RAM, NIC model/driver/local IP, route/cabling/switch, PLC3 model/firmware/IP, protocol and negotiated PDU/server limits. Save identical exported point tables (names, exact addresses/NodeIds, types, lengths, arrays), demand set, scan periods, priorities and timeouts for both products. Record hashes and versions. Keep machine power/load settings identical.
2. Define one approved priority-write script before either run: same writable test addresses, typed values, stage order, injection timestamps relative to run start, verification reads, deadlines and expected acknowledgements. Use dedicated approved test points; archive the exact script and hash. Measure priority-write latency from enqueue to verified success. Do not change scripts or point tables between products.
3. Run KEP and IGW separately on the same computer/NIC/PLC; stop and confirm the other's protocol sessions are closed before starting. Disable other polling consumers. Use identical warm-up and a ten-minute (600 s) measured window after warm-up. Record start/end timestamps and order; repeat the pair in reverse order if testing for drift. Each measured run uses the same demand and write schedule.
4. Capture all logical-batch completion durations (scheduled due time to publication of every requested point), physical protocol request durations (wire send through response, excluding scheduler queue), request counts, delivered good-tag counts, missed periods, timeouts, reconnects, verified priority-write durations, sampled process CPU and working set. Retain raw timestamps; if KEP has no equivalent native metric, use identical external consumer/instrumentation for both and document its boundary before evaluating parity. Never substitute request P95 for completion P95.
5. Calculate nearest-rank completion P50/P95, protocol-request P95, requests/second and good-tag throughput over 600 s. Report CPU mean/P95 and working-set mean/peak using the same sample interval. Identify missing/unobservable measurements as unavailable, never zero.
6. Parity requires all three: IGW completion P95 <= 1.05 × KEP completion P95; IGW priority-write P95 <= KEP priority-write P95; IGW has no extra missed periods. All fields and raw evidence must be populated before a parity/superiority claim. Also report quality/value mismatches and all timeout/reconnect counts even if thresholds pass.

| Environment / inputs | Recorded value |
|---|---|
| Computer / OS / CPU / RAM / power plan | |
| NIC / driver / local IP / path | |
| PLC3 model / firmware / IP / protocol / PDU limits | |
| KEP version / configuration hash | |
| IGW version / installer SHA256 / configuration hash | |
| Point table / 72-point confirmation / hash | |
| Active demand / scan periods / priorities / timeout | |
| Priority-write script path / hash / approved addresses | |
| Warm-up / run order / competing sessions closed evidence | |
| Measurement boundaries / sample interval / raw evidence paths | |

| Ten-minute measurement | KEPServer | IGW 1.6.43 |
|---|---|---|
| Window start / end | | |
| Logical completion P50 (ms) | | |
| Logical completion P95 (ms) | | |
| Protocol-request P95 (ms) | | |
| Requests / second | | |
| Good-tag throughput / second | | |
| Missed periods | | |
| Timeouts / reconnects | | |
| Priority-write verified P95 (ms) | | |
| CPU mean / P95 (%) | | |
| Working set mean / peak (MiB) | | |
| Value / quality mismatches | | |
| Raw evidence / operator | | |

| Parity criterion | Result |
|---|---|
| IGW completion P95 <= 105% of KEP | |
| IGW write P95 <= KEP | |
| IGW missed periods <= KEP | |
| Overall field decision | |
