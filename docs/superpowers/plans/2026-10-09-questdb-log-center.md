# QuestDB Log Center Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 用 QuestDB 支撑跨平台 IGW 在线日志中心，保持 PLC 通信与日志故障隔离。

**Architecture:** 官方数据库运行包由网关监督，HTTP ILP 批量写入、HTTP SQL 查询。磁盘批次缓存提供重试和重启恢复，UUID 去重；旧分段按后台任务导入。新接口报文按表存储，旧报文按文件回退读取。

**Tech Stack:** .NET 10 / HttpClient / QuestDB 10.0.1 / xUnit / Inno Setup / Linux tar / Docker。

**Spec:** ../specs/2026-10-09-questdb-logs.md

## Global Constraints

- Windows 和 Linux、离线安装、自带 Java、无 WSL；固定官方下载哈希。
- 默认 30 天、HOUR 分区；不宣称已经验证 9 亿条容量。
- PLC 日志调用不得等待数据库或磁盘；队列和磁盘缓存均有上限。
- 保持现有日志 API；数据库只监听 127.0.0.1，使用 Basic 认证。
- 旧历史导入可恢复、原文件保留；不部署现场或合并 master。

## Review Focus

- 超时后实际写成功：重发不能产生重复，也不能删除未确认的缓存。
- 多行、中文、引号、反斜杠日志：ILP 不能破坏正文和筛选语义。
- 数据库退出、重启、磁盘写满：采集正常，状态显示失败和丢弃。
- 目录迁移中断：原数据库仍可恢复，禁止两进程同时打开同一目录。
- 历史导入未完成或 WAL 挂起：查询须显示 partial/degraded。

### Task 1: HTTP client, schema and query

**Files:** Create `src/IGW.Observability/QuestDb/QuestDbClient.cs`, `QuestDbLogCodec.cs`, `QuestDbLogQuery.cs`; test `tests/IGW.Observability.Tests/QuestDbTests.cs`.
**Interfaces:** Produces `QuestDbClient.QueryAsync(sql, ct) -> JsonDocument`, `WriteAsync(byte[], ct)`, `InitializeAsync(retentionDays, ct)`; codec stable ID and ILP; query returns existing `LogCenterResponse`.

- [ ] Write tests for SQL escaping, Chinese/multiline codec, path boundary and deterministic IDs; run `dotnet test tests/IGW.Observability.Tests`, expect failure before implementation.
- [ ] Implement authenticated bounded HTTP requests, WAL HOUR TTL tables, UUID dedup, exact and substring filters, histogram and bounded export.
- [ ] Run unit tests and native database integration with `IGW_QUESTDB_TEST_RUNTIME`; expect correct totals, dedup, payload and no-auth rejection.
- [ ] Commit the client/query slice.

### Task 2: Managed lifecycle and durable queue

**Files:** Create `QuestDbProcess.cs`, `QuestDbLogStore.cs`, `ILogRecordSink.cs`; modify `LogStoreLoggerProvider.cs`, `LogSegmentStore.cs`.
**Interfaces:** Consumes Task 1; produces `ILogRecordSink.Append(LogRecord)`, store status/flush/dispose and managed process start/stop.

- [ ] Test nonblocking append against unreachable database, bounded drop accounting, retry/restart and persisted IDs; expect failures.
- [ ] Implement queue, fsync batch files, retry backoff, disk cap and poison-batch quarantine with visible failure. Start the owned DB on loopback; restart on exit; drain owned output without unbounded logs.
- [ ] Verify real write/query latency and restart recovery; expect no duplicates and PLC-facing append bounded.
- [ ] Commit queue/lifecycle slice.

### Task 3: Runtime, payload and history compatibility

**Files:** Modify `RuntimeTelemetry.cs`, `LogCenterBackend.cs`, `LogPayloadStore.cs`; create `QuestDbHistoryImporter.cs`.
**Interfaces:** Consumes Tasks 1/2; existing public API stays compatible. Payload store optionally uses QuestDB; file readers remain for old payloads.

- [ ] Test native end-to-end query, payload pages/download/export, resumable history, incomplete-history partial and retention failure; expect failures.
- [ ] Route new records and payloads to DB, migrate legacy segments in bounded background batches with deterministic IDs and atomic checkpoints; expose readiness, pending and failures.
- [ ] Preserve JSON attribute types and cleanup protection; verify new writes create no per-log or per-payload files.
- [ ] Commit runtime integration.

### Task 4: Storage move and retention

**Files:** Modify `LogCenterBackend.cs`; new lifecycle migration methods and tests.
**Interfaces:** Consumes managed store; `BeginStorageChangeAsync` existing API remains asynchronous.

- [ ] Test stop/copy/restart and rollback on target failure while queued writes continue.
- [ ] Serialize access during migration; copy only stopped data, persist target before start and revert settings on failure; apply SQL TTL before saving retention config.
- [ ] Native restart + moved directory + injected failure tests pass; commit.

### Task 5: Offline distribution and operating guide

**Files:** Create `installer/package-questdb.ps1`, shell counterpart/version manifest; modify Windows/Linux/Docker packaging; docs operating guide/benchmark.
**Interfaces:** Stages verified runtime at `app/questdb`; runtime discovers `questdb/bin` relative to executable.

- [ ] Test release manifest, stage contents, licenses and Windows/Linux/Docker hooks; expect failures before integration.
- [ ] Package pinned archives; set Linux Java execute bits; installer stops owned child processes before replacement. No downloads on field installation.
- [ ] Run targeted .NET tests, native smoke and package scripts. Record real throughput/latency and unverified capacity limits; review complete branch and fix findings.
- [ ] Commit final integration and provide evidence and worktree path.
