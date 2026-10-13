# UI responsiveness corrections

User approved correcting the four findings in the preceding review.

Scope and acceptance:
- Writeback destinations default to the current DB subtree. Cross-PLC copy sources remain available. Preserve existing configured full paths, label out-of-scope selections accurately, and restrict search on the server rather than filtering a globally truncated page.
- MES flow opening uses task documents and the lightweight catalog regardless of total tag count. Legacy fallback is only for genuinely unsupported endpoints, never ordinary failures. Preserve save, batch-save, templates, new-task creation, and selected DB behavior.
- QC shows the first definition page before subsequent pages finish and can cancel obsolete loads. Apply initial snapshots and deltas with bounded work per frame; preserve actual-value confirmation and subscription correctness.
- OPC UA browse/import avoids monopolizing the browser with large selected branches. Preserve full import contents, type accuracy, alias/conflict handling and cancellation. No arbitrary loss of selected tags or new PLC writes.

Execution: isolated worktree based on 1a23e2d; test-first changes per area. One browse implementer agent; root owns the other files. Final independent code review and frontend/backend validation. No deployment, installation or live PLC writes. Original untracked tests/IGW.ArrayStress stays untouched.

Tasks:
1. Parameter search scope: add a scoped query option used by write destinations; tests for duplicate MESFlag across two PLCs and cross-PLC sources.
2. Task loading: prefer existing paged/task APIs; tests for small projects, request failures and save compatibility.
3. QC: progressive page publication and bounded update queue; tests for pending later page, cancellation and multi-frame deltas.
4. Browse: agent owns OpcUaTagBrowserDialog and dedicated utilities/tests; capture failing behavioral tests, implement responsive preparation and verify.
5. Review and verify the combined change; return reviewed changes to the original checkout without touching unrelated files.


Approved additional scope: migrate point reads/writes, array slices and realtime status to a shared SignalR WebSocket connection. Keep management/configuration HTTP. No SSE fallback or write replay. Retain coordinator admission, cancellation, project revision, branch authorization, structured PLC errors and actual readback verification.

Review corrections: prevent delayed writes after connection startup timeout; hold sent-request leases until settlement; preserve scalar rename; cooperatively index existing import targets; load explicit MES task after template and anomaly navigation, including descendant tag pages.

Field diagnostics: reported 3500 vs5800 are milliseconds. Photo shows PLC192.168.100.90, current sessions5, maximum11, displayed505ms. Measurement interval/software identity is not established; these figures do not prove comparable scan latency or full-DB reads. No field performance acceptance claimed.


Validation recorded: full backend 868/868, full frontend 189/189 before final alias regression; later targeted MES18/18 and indexed repository1/1 passed. Frontend typecheck and production build passed. Node contract suite initially226/227 revealed three missing English strings; added translations and catalog3/3 passed. Frozen pnpm11 install accepted the lockfile with existing overrides retained. Final browse alias test and final build/contract checks recorded in delivery.

Runtime boundaries: UI point operations and live status use SignalR. Existing public PLC REST endpoints remain for external-client compatibility, while browser code uses no HTTP point fallback. No deployment/service restart or real PLC write was performed. Field latency remains unverified.
