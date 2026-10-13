import axios from 'axios'
import { beginUiActivity, installRequestActivity } from './utils/requestActivity'
import { realtimeTransport } from './utils/signalrTransport'
import { getSessionToken, withAccessToken } from './utils/sessionToken'
import type { ArrayDef } from './utils/arrayTags'

export interface MesJsonMapping { sourcePath: string; targetPath: string }
export type MesConditionSource = 'Tag' | 'Input' | 'Mapped' | 'Response' | 'Variable'
export interface MesCondition {
  source?: MesConditionSource; path?: string; tagId: string; op: string; value: string
  arrayQuantifier?: string; arrayStart?: number[]; arrayLengths?: number[]; arrayMemberPath?: string
}
export interface MesInterfaceListener {
  protocol: 'SignalR' | 'WebSocket' | 'SSE'; url: string; headers: Record<string, string>
  receiveMethod: string; subscribeMethod: string; replyMethod: string
  subscriptionTemplate: string; responseTemplate: string; ignoreResponseTemplate: string; eventName: string; idField: string
  changeFields: string[]; triggerOnFirst: boolean; reconnectMs: number; idleTimeoutSec: number
  maxMessageBytes: number; inputMappings: MesJsonMapping[]
}
export interface MesHttpJsonConfig { jsonTemplate: string; responseMappings: MesJsonMapping[]; bodyTemplate: string }

export const http = axios.create({ timeout: 8000 })
installRequestActivity(http)
export interface TagFaultDto {
  tagId: string
  channel: string
  device: string
  address: string
  dataType: string
  quality: string
  qualityHex: string
  category: string
  cause: string
  suggestion: string
  firstSeenAt: string
  lastSeenAt: string
  recoveredAt: string | null
  occurrenceCount: number
  isolated: boolean
  active: boolean
}

export interface TagFaultsDto {
  active: TagFaultDto[]
  recovered: TagFaultDto[]
  trackedCount: number
  overflowCount: number
}

export const INVALID_ACQUISITION_DIAGNOSTICS_ZIP = 'invalid-acquisition-diagnostics-zip'
let activeProjectRevision = ''
export function setActiveProjectRevision(revision: string) {
  activeProjectRevision = String(revision || '').trim()
}
export function getActiveProjectRevision() { return activeProjectRevision }
function projectRevisionHeaders() {
  return activeProjectRevision
    ? { ["X-Igw-Project-Revision"]: activeProjectRevision }
    : undefined
}
http.interceptors.request.use((c) => {
  const token = getSessionToken()
  if (token) c.headers!.Authorization = 'Bearer ' + token
  return c
})

// 登录失效(401)时回登录页；没有权限(403)时提示原因。处理函数由 App.vue 注册。
export let onUnauthorized: (() => void) | null = null
export function setUnauthorizedHandler(h: () => void) { onUnauthorized = h }
let onForbidden: ((code: string, message: string) => void) | null = null
export function setForbiddenHandler(h: (code: string, message: string) => void) { onForbidden = h }
const permissionErrors = new Set(['password_change_required', 'module_forbidden', 'edit_forbidden', 'admin_required', 'feature_unlicensed'])
let authPrompting = false
http.interceptors.response.use(
  (r) => r,
  (err) => {
    const url = String(err?.config?.url || '')
    const isPlcAuthentication = url.includes('/authentication/verify')
    const isLogin = url.includes('/api/v1/auth/login') || url.includes('/api/v1/auth/password')
    const data = err?.response?.data
    if (err?.response?.status === 401 && !isPlcAuthentication && !isLogin && onUnauthorized && !authPrompting) {
      authPrompting = true
      try { onUnauthorized() } finally { setTimeout(() => (authPrompting = false), 400) }
    }
    if (err?.response?.status === 403 && permissionErrors.has(data?.error)) {
      // 页面上的错误提示多数直接显示 err.message：换成网关给的中文原因
      err.message = data.message || err.message
      onForbidden?.(data.error, err.message)
    }
    return Promise.reject(err)
  },
)

export interface LicenseItemDto { id: string; name: string; allowed: boolean }
export interface LicenseDto {
  status: string; blocked: boolean; customer: string
  expiry?: string; daysLeft: number; machineId: string; message: string
  edition?: string
  /** false = 授权码不限制驱动/功能(试用或旧授权码)。 */
  driversLimited?: boolean; drivers?: LicenseItemDto[]
  featuresLimited?: boolean; features?: LicenseItemDto[]
}
export interface HealthDto {
  status: string; version?: string; project: string; tags: number; activeTags: number
  devices: number; devicesOnline: number; devicesStandby?: number; opcUaSessions: number; license?: LicenseDto
}
export interface OpcUaBrowsePreviewBody {
  url: string; securityPolicy: string; anonymous: boolean
  username: string; password: string; timeoutMs: number
}
export interface OpcUaBrowsedTag {
  name: string; address: string; path: string; pathSegments?: string[]; dataType: string | number; isArray: boolean
  array?: ArrayDef | null; stringLength?: number | null; access?: string | number; metadata?: Record<string, string> | null
}
export interface OpcUaBrowseIssue { nodeId: string; name: string; pathSegments: string[]; reason: string }
export interface OpcUaBrowseResult { tags: OpcUaBrowsedTag[]; truncated: boolean; issues?: OpcUaBrowseIssue[] }
export interface ScanTimingDeviceDto {
  logicalBatchCount?: number | null; latestProtocolRequestCount?: number | null
  protocolRequestSamples?: ProtocolRequestSampleDto[] | null; omittedProtocolRequestSamples?: number | null
  deviceSessionFingerprint?: string | null; sessionGeneration?: number | null
  sameEndpointDeviceCount?: number | null; sameEndpointSessionCount?: number | null
  knownSessionLimit?: number | null; sessionLimitSource?: string | null
  name: string; mode: 'polling' | 'subscription' | 'mixed' | 'standby'
  configuredMinPeriodMs: number; configuredMaxPeriodMs: number; pollingTags: number; subscribedTags: number
  plannedBlocks: number; lastScanMs: number; lastScanAt: string | null; actualAveragePeriodMs: number; actualP95PeriodMs: number
  overduePeriods: number; samplesPerSecond: number; lastDriverMs?: number; lastLocalMs?: number; lastTagCount?: number
  queueP95Ms?: number | null; protocolRequestP95Ms?: number | null; protocolRequestMaxMs?: number | null
  localP95Ms?: number | null; networkP95RttMs?: number | null; networkLossPercent?: number | null
  latestAttribution?: string | null; latestAttributionConfidence?: string | null
}
export interface NetworkProbeWindowDto {
  from?: string; to?: string; count?: number; successCount?: number; failedCount?: number
  latestRttMs?: number | null; p95RttMs?: number | null; baselineP95RttMs?: number | null
  lossPercent?: number | null; status?: string | null; error?: string | null; routeVerified?: boolean
  sourceAddress?: string | null; remoteAddress?: string | null
}
export interface AcquisitionAttributionDto {
  code?: string | null; label?: string | null; confidence?: string | null
  evidence?: string[] | null; factors?: string[] | null
}
export interface ScanTimingRecordDto {
  logicalBatchCount?: number | null; latestProtocolRequestCount?: number | null
  protocolRequestSamples?: ProtocolRequestSampleDto[] | null; omittedProtocolRequestSamples?: number | null
  deviceSessionFingerprint?: string | null; sessionGeneration?: number | null
  sequence: number; startedAt: string; blockId: string; configuredPeriodMs: number; actualIntervalMs: number | null
  totalMs: number; driverMs: number; localMs: number; tagCount: number; sampleCount: number; badCount: number
  status: 'good' | 'partial' | 'bad' | 'error'; error: string | null; missedPeriods: number; tagIds: string[]
  protocol?: { requests: number; failedRequests: number; cancelledRequests: number; requestTotalMs: number; requestMaxMs: number; queueMs: number; physicalRequests?: number | null; opaqueOperations?: number; samples?: ProtocolRequestSampleDto[] | null; omittedSamples?: number } | null
  network?: NetworkProbeWindowDto | null; attribution?: AcquisitionAttributionDto | null
  sameSessionRetries?: number | null; batchDownshifts?: number | null
}
export interface ProtocolRequestSampleDto {
  descriptor?: { operation: string; scope: string; tagCount: number; payloadBytes: number; gapBytes: number } | null
  elapsedMs: number; failed: boolean; cancelled: boolean
}
export interface ScanTimingsDto { devices: ScanTimingDeviceDto[]; device: string | null; records: ScanTimingRecordDto[]; capacity: number }
export interface TagScanTimingDto {
  tagId: string; device: string; mode: 'polling' | 'subscription' | 'standby'
  batch: Pick<ScanTimingRecordDto, 'sequence' | 'startedAt' | 'configuredPeriodMs' | 'actualIntervalMs' | 'totalMs' | 'driverMs' | 'localMs' | 'tagCount' | 'status'> | null
}
export interface OpcUaBrowsedNode {
  nodeId: string; name: string; isVariable: boolean; mayHaveChildren: boolean; pathSegments: string[]
}
export interface OpcUaBrowseChildrenResult { nodes: OpcUaBrowsedNode[] }
export interface OpcUaBrowseNodeBody {
  connection: OpcUaBrowsePreviewBody
  nodeId: string; name?: string; isVariable?: boolean; pathSegments: string[]; limit?: number
}
export interface SiemensAuthStatusDto { mode: string; hasCredential: boolean; state: string; message: string }
export type ProjectParameterScope = 'CurrentGroup' | 'CurrentDevice' | 'OtherDevice'
export interface ProjectParameterOption {
  value: string; label: string; fullPath: string
  scope: ProjectParameterScope; kind: 'tag' | 'group'; description?: string; address?: string
}
export interface ProjectParameterOptionsResponse { items: ProjectParameterOption[]; truncated: boolean }
export interface MesRangeMemberDto {
  path: string; jsonName: string; dataType: string; access: string
  shapeKind?: string | null; dimensions?: number[] | null; lowerBounds?: number[] | null
}
export interface MesRangeOptionDto {
  sourcePath: string; devicePath: string; name: string; scope: 'Group' | 'Struct' | 'Array'
  tagCount: number; defaultOutputPath: string; shapeKind?: string | null
  dimensions: number[]; lowerBounds: number[]; existingElementCount: number
  members: MesRangeMemberDto[]; diagnosticCodes: string[]
}
/** 「配置数据」树里的一项：DB/结构体/数组或单个点(按层加载)。 */
export interface MesRangeNodeDto {
  sourcePath: string; name: string; scope: 'Group' | 'Struct' | 'Array' | 'Tag'; dataType?: string | null
  tagCount: number; hasChildren: boolean; defaultOutputPath: string; shapeKind?: string | null
  dimensions: number[]; lowerBounds: number[]; members: MesRangeMemberDto[]
}
export interface MesRangeChildrenResponse { items: MesRangeNodeDto[]; nextCursor?: string | null; total: number }
/** 报文结构里的一项：key 是按原名的结构路径(如 Counter、UDT1.A、Cells[].Temp)，改名和排除都按它记。 */
export interface MesPayloadNodeDto {
  key: string; name: string; kind: 'value' | 'object' | 'array'; tagId?: string | null; dataType?: string | null
  length?: number | null; excludable: boolean; children: MesPayloadNodeDto[]
  /** 数组元素里的成员：可以不上报(记在 range.omit，只影响报文)。 */
  omittable?: boolean
}
export interface MesPayloadPreviewResponse {
  ranges: { sourcePath: string; outputPath: string; scope: string; root: MesPayloadNodeDto; tagCount: number }[]
  error?: string | null
}
export interface MesRangeOptionsResponse {
  items: MesRangeOptionDto[]; nextCursor?: string | null; total: number
  diagnostics: { code: string; sourcePath: string; message: string }[]
}
export interface ProjectTagGroupDto {
  name: string; path: string; kind: 'Channel' | 'Device' | 'Group'; description?: string | null
}
export interface ProjectTagDefinitionDto {
  id: string; name: string; address: string; dataType: string; access: string
  description?: string | null; engUnit?: string | null; stringLength?: number | null; array?: ArrayDef | null
}
export interface ProjectTagScopeResponse {
  projectRevision: string; realtimeRevision: string; root: string; groups: ProjectTagGroupDto[]
  definitions: ProjectTagDefinitionDto[]; nextCursor?: string | null; total: number
}
export interface DatabaseStatusDto {
  databaseKind: string; target: string
  /** sqlite / syncing / online / offline */
  state: string; message?: string | null; pending: number; lastSyncUtc?: string | null
  sources: { name: string; state: string; pending: number; message?: string | null }[]
}
export interface UiPreferencesDto { locale: 'zh-CN' | 'en-US'; projectAutoSave?: boolean; projectAutoSaveSeconds?: number }
export interface NetworkAdapterDto {
  id: string; name: string; ipv4: string; prefixLength: number; label: string
}
export interface ConnectionDiagnosticDto {
  device: string; channel: string; protocol: string; code: string; category: string
  scope: string; state: string; availability?: string; cause: string; suggestion: string; rawError: string
  firstOccurredAt: string; lastOccurredAt: string; recoveredAt?: string | null
  occurrenceCount: number; retrying: boolean; nextRetryMs: number; active: boolean
}
export interface ConnectionDiagnosticsDto {
  active: ConnectionDiagnosticDto[]; recovered: ConnectionDiagnosticDto[]
}
export interface RuntimeLogEntryDto {
  timestamp: string; level: string; category: string; message: string
}
export interface RuntimeLogSnapshotDto {
  retentionDays: number; items: RuntimeLogEntryDto[]
  warnings?: string[]
  storageDirectory?: string; defaultDirectory?: string
}
/** 磁盘项附带卷标与容量；普通文件夹这几个字段为空。 */
/** QC 刷新：从 PLC 读取后返回的最新值，字段与实时流的点值一致。 */
export interface PlcReadValueDto {
  id: string; value: unknown; quality?: string; qualityCode?: number; good?: boolean
  sourceTs?: string; serverTs?: string; error?: string
}
export interface PlcReadResultDto {
  goodCount: number; sampleCount: number; error: string | null; elapsedMs: number
  values: PlcReadValueDto[]
}
export interface RuntimeLogDirectoryEntryDto {
  name: string; path: string
  label?: string | null; freeBytes?: number | null; totalBytes?: number | null
}
/** currentPath 为空表示当前是磁盘列表。 */
export interface RuntimeLogDirectoryListingDto {
  currentPath: string; parentPath: string | null
  directories: RuntimeLogDirectoryEntryDto[]; truncated: boolean
}
export interface FlowTemplateTargetCatalogDto {
  groups: { path: string; hasTask: boolean; hasFlow: boolean; templateRef?: string | null }[]
  deviceTemplates: string[]
}
export interface LogCenterEntryDto {
  timestamp: string; level: string; category: string; message: string
  traceId?: string | null; payloadId?: string | null; attributes: Record<string, unknown>
}
export interface LogHistogramBucketDto { start: string; total: number; warnings: number; errors: number }
export interface LogDeviceSummaryDto { device: string; status: string; timestamp: string }
export interface LogOutageSummaryDto { device: string; startedAt: string; recoveredAt?: string | null }
export interface LogCenterResponseDto {
  items: LogCenterEntryDto[]; total: number; tookMs: number; histogram: LogHistogramBucketDto[]
  devices: LogDeviceSummaryDto[]; outages: LogOutageSummaryDto[]
  /** 扫描超过时间预算时为 true：总数只统计了最近的一部分。 */
  partial?: boolean
}
/** 日志中心一键清理：可清理的残留、后台删除进度。 */
export interface LogCleanupStatusDto {
  state: 'Idle' | 'Running' | 'Completed' | 'Failed'
  /** filePattern 为空时清掉整个目录，否则只清目录里的旧版文本日志。 */
  residues: Array<{ path: string; kind: string; filePattern?: string | null }>
  /** 正在各本地硬盘上查找网关以前留下的日志。 */
  scanning?: boolean
  blockedReason?: string | null
  deletedFiles: number; deletedBytes: number
  skipped: string[]; error?: string | null
  startedAt?: string | null; finishedAt?: string | null
}

/** 日志中心查询条件选项：协议/通道/设备下拉、开启了采集流程的标记组(选中按它绑定的任务查)。 */
export interface LogFilterOptionsDto {
  protocols: Array<{ id: string; name: string }>
  channels: Array<{ name: string; protocol: string }>
  devices: Array<{ channel: string; name: string; path: string }>
  groups: Array<{ name: string; path: string; channel: string; device: string; taskId: string; taskName: string }>
}

export interface LogCenterStatusDto {
  available: boolean; state: string; port: number; retentionDays: number; storageDirectory: string
  spool: { pending: number; failed: number; dropped: number }
  migrationStatus?: string | number | null; migrationTotalBytes: number; migrationCopiedBytes: number; migrationFailure?: string | null
  storageBytes?: number
  engine?: 'segments' | 'questdb'; historyImported?: boolean; historyError?: string | null
  historyTotalFiles?: number; historyImportedFiles?: number; historyImportedRows?: number; historyFailedFiles?: number
  walPendingTransactions?: number; databaseError?: string | null
}
/** 接口报文拆成发送内容和返回内容；split=false 表示无法识别格式，整段放在 request。 */
export interface LogPayloadExchangeDto {
  id: string; request: string; response: string
  requestTruncated: boolean; responseTruncated: boolean; split: boolean
}
export interface LogPayloadPageDto {
  id: string; page: number; pageSize: number; totalPages: number; totalLength: number
  hasMore: boolean; contentType: string; content: string; truncated: boolean
}
export interface ArrayInfoDto {
  id: string; elementType: string; dimensions: number[]; lowerBounds: number[]; totalElements: number
  overallQuality: 'Good' | 'Uncertain' | 'Bad'; good: number; uncertain: number; bad: number
}
export interface ArraySliceDto {
  id: string; elementType: string; dimensions: number[]; lowerBounds: number[]
  start: number[]; length: number[]; totalElements: number; values: unknown[]
  qualityCodes: number[]; overallQuality: 'Good' | 'Uncertain' | 'Bad'
}

async function runtimeRequest<T = any>(operation: string, args: unknown = {}, signal?: AbortSignal): Promise<T> {
  const label = ({ write: '正在写入 PLC…', writeArraySlice: '正在写入 PLC…', arrayInfo: '正在读取数组…', arraySlice: '正在读取数组…', tags: '正在加载工程点位…' } as Record<string, string>)[operation]
  const finish = label ? beginUiActivity(label) : undefined
  try { return await realtimeTransport.request<T>(operation, args, activeProjectRevision, signal) }
  catch (error: any) {
    if (error?.response?.status === 401 && onUnauthorized && !authPrompting) {
      authPrompting = true
      try { onUnauthorized() } finally { setTimeout(() => { authPrompting = false }, 400) }
    }
    throw error
  }
  finally { finish?.() }
}

export const api = {
  uiPreferences: () => http.get<UiPreferencesDto>('/api/v1/ui/preferences').then((r) => r.data),
  updateUiPreferences: (locale: UiPreferencesDto['locale']) =>
    http.put<UiPreferencesDto>('/api/v1/ui/preferences', { locale }).then((r) => r.data),
  databaseStatus: () => http.get<DatabaseStatusDto>('/api/v1/database/status').then((r) => r.data),
  updateProjectAutoSave: (projectAutoSave: boolean, projectAutoSaveSeconds: number) =>
    http.put<UiPreferencesDto>('/api/v1/ui/preferences', { projectAutoSave, projectAutoSaveSeconds }).then((r) => r.data),
  health: () => runtimeRequest<HealthDto>('health'),
  networkAdapters: () => http.get<NetworkAdapterDto[]>('/api/v1/network-adapters').then((r) => r.data),
  devices: () => runtimeRequest<any[]>('devices'),
  reconnectChannel: (channel: string) =>
    http.post<{ channel: string; restarted: number }>(`/api/v1/channels/${encodeURIComponent(channel)}/reconnect`, null, { timeout: 120000 }).then((r) => r.data),
  tags: () => runtimeRequest<string[]>('tags'),
  values: (ids: string[]) => ids.length ? runtimeRequest<any[]>('values', { ids }) : Promise.resolve([]),
  /** 显式从 PLC 读取这些点（不登记持续采集）；单次最多 10000 点。 */
  readValues: (ids: string[]) => runtimeRequest<PlcReadResultDto>('readValues', { ids }),
  scanTimings: (device?: string, tag?: string, limit = 100) =>
    runtimeRequest<ScanTimingsDto>('scanTimings', { device, tag, limit }),
  acquisitionDiagnosticsExport: async () => {
    const response = await http.get<Blob>('/api/v1/diagnostics/acquisition-export', {
      responseType: 'blob', timeout: 120000,
    })
    const contentType = String(response.headers['content-type'] || '').split(';', 1)[0].trim().toLowerCase()
    if (contentType !== 'application/zip' || !(response.data instanceof Blob))
      throw new Error(INVALID_ACQUISITION_DIAGNOSTICS_ZIP)
    return response.data
  },
  tagScanTimings: (tagIds: string[], signal?: AbortSignal) =>
    runtimeRequest<{ items: TagScanTimingDto[]; capturedAt: string }>('tagScanTimings', { tagIds }, signal),
  write: (id: string, value: any) => runtimeRequest('write', { id, value }),
  arrayInfo: (id: string) => runtimeRequest<ArrayInfoDto>('arrayInfo', { id }),
  arraySlice: (id: string, start: number[], length: number[]) =>
    runtimeRequest<ArraySliceDto>('arraySlice', { id, start, length }),
  writeArraySlice: (id: string, start: number[], length: number[], values: unknown) =>
    runtimeRequest('writeArraySlice', { id, start, length, values }),
  /** 在线列出 PLC 上的数据块(块名 → DB 号，S7-1200/1500)；supported=false 表示设备不在线或不支持。 */
  siemensDataBlocks: (device: string) =>
    http.get<{ supported: boolean; blocks: { name: string; number: number }[]; error?: string | null }>(
      `/api/v1/devices/${encodeURIComponent(device)}/siemens/data-blocks`, { timeout: 25000 }).then((r) => r.data),
  siemensDbPreview: (source: string, typeSources: string[] = [], defaultDb = 1) =>
    http.post<any>('/api/v1/siemens/db-import/preview', { source, typeSources, defaultDb }, { timeout: 120000 }).then((r) => r.data),
  license: () => http.get<LicenseDto>('/api/v1/license').then((r) => r.data),
  activate: (code: string) => http.post<LicenseDto>('/api/v1/license', { code }).then((r) => r.data),
  // 工程配置(P2):rev = X-Igw-Rev 内容哈希,保存时带回做并发冲突检测
  // 完整工程可达数十 MB(压缩后约 1 MB)，远程访问或网关繁忙时 8 秒默认超时不够。
  project: () => http.get('/api/v1/project', { timeout: 120000 }).then((r) => {
    const rev = (r.headers['x-igw-rev'] as string) || ''
    setActiveProjectRevision(rev)
    return { proj: r.data, rev }
  }),
  /** 已有版本 rev 时只在工程变化后才下载；没变化返回 null(服务端 304，不传输工程)。 */
  projectIfChanged: (rev: string) => http.get('/api/v1/project', {
    timeout: 120000,
    headers: rev ? { 'If-None-Match': rev } : {},
    validateStatus: status => status === 200 || status === 304,
  }).then((r) => {
    if (r.status === 304) return null
    const nextRev = (r.headers['x-igw-rev'] as string) || ''
    setActiveProjectRevision(nextRev)
    return { proj: r.data, rev: nextRev }
  }),
  projectTagScope: (params: { root?: string; q?: string; cursor?: string; limit?: number }, signal?: AbortSignal) =>
    http.get<ProjectTagScopeResponse>('/api/v1/project/tag-scope', { params, signal }).then((r) => {
      setActiveProjectRevision(r.data.projectRevision)
      // 兼容已经部署的 SSE 初版 Runtime：它的范围接口只有 projectRevision。
      // 前端若直接读取缺失的 realtimeRevision，会在 QC 中静默跳过实时流启动，
      // 表现为点表存在但所有值一直为空。旧 Runtime 的 SSE 本身正是使用该工程版本。
      return {
        ...r.data,
        realtimeRevision: String(r.data.realtimeRevision || r.data.projectRevision || '').trim(),
      }
    }),
  parameterOptions: (params: {
    q?: string; scopeGroup: string; kind: 'tag' | 'group'; access: 'read' | 'write'; limit?: number
  }, signal?: AbortSignal) =>
    http.get<ProjectParameterOptionsResponse>('/api/v1/project/parameter-options', { params, signal }).then((r) => r.data),
  /** 按层取「配置数据」树的子项(含单个点)；q 不为空时在数据块里搜索。 */
  mesRangeChildren: (params: {
    /** 不填时按整个工程浏览(通道 → 设备 → 数据块 → 数据块内)；搜索必须填 */
    scopeGroup?: string; parent?: string; q?: string; cursor?: string; limit?: number
  }, signal?: AbortSignal) =>
    http.get<MesRangeChildrenResponse>('/api/v1/project/mes-range-children', { params, signal }).then((r) => r.data),
  /** 把数据范围编译成报文结构，供「报文高级设置」逐项改名、选择是否上报。 */
  /** 「配置数据」→ 旧版字段/结构映射：普通点成字段，子组成对象，按下标命名的子组成数组(枚举子组)。 */
  mesFieldMapping: (body: { scopeGroup?: string; ranges: any[] }) =>
    http.post<{ fields: any[]; structs: any[] }>('/api/v1/project/mes-field-mapping', body).then((r) => r.data),
  mesPayloadPreview: (body: { scopeGroup?: string; ranges: any[] }, signal?: AbortSignal) =>
    http.post<MesPayloadPreviewResponse>('/api/v1/project/mes-payload-preview', body, { signal }).then((r) => r.data),
  mesRangeOptions: (params: {
    scopeGroup?: string; q?: string; cursor?: string; limit?: number
  }, signal?: AbortSignal) =>
    http.get<MesRangeOptionsResponse>('/api/v1/project/mes-range-options', { params, signal }).then((r) => r.data),
  putProject: (proj: any, rev: string, allowDestructive = false) =>
    http.put('/api/v1/project', proj, {
      headers: {
        ...(rev ? { 'X-Igw-Rev': rev } : {}),
        ...(allowDestructive ? { 'X-Igw-Allow-Destructive': 'true' } : {}),
      },
      timeout: 120000,
    }).then((r) => {
      const nextRev = (r.headers['x-igw-rev'] as string) || r.data?.rev || ''
      setActiveProjectRevision(nextRev)
      return { data: r.data, rev: nextRev }
    }),
  // MES 画布只提交 MES 配置，避免复制/序列化/回读包含海量点位的整个工程。
  putMesUpload: (mesUpload: any, rev: string, allowDestructive = false) =>
    http.put('/api/v1/project/mes-upload', mesUpload, {
      headers: {
        ...(rev ? { 'X-Igw-Rev': rev } : {}),
        ...(allowDestructive ? { 'X-Igw-Allow-Destructive': 'true' } : {}),
      },
      timeout: 30_000,
    }).then((r) => {
      const nextRev = (r.headers['x-igw-rev'] as string) || ''
      setActiveProjectRevision(nextRev)
      return { data: r.data, rev: nextRev }
    }),
  drivers: () => http.get<any[]>('/api/v1/drivers').then((r) => r.data),
  browse: (device: string) => http.post<any[]>(`/api/v1/devices/${encodeURIComponent(device)}/browse`).then((r) => r.data),
  browseOpcUaPreview: (body: OpcUaBrowsePreviewBody) =>
    http.post<OpcUaBrowseResult>('/api/v1/opcua/browse-preview', body, { timeout: 120000 }).then((r) => r.data),
  browseOpcUaChildren: (body: OpcUaBrowseNodeBody) =>
    http.post<OpcUaBrowseChildrenResult>('/api/v1/opcua/browse-children', body, { timeout: 30000 }).then((r) => r.data),
  browseOpcUaBranch: (body: OpcUaBrowseNodeBody) =>
    http.post<OpcUaBrowseResult>('/api/v1/opcua/browse-branch', body, { timeout: 120000 }).then((r) => r.data),
  siemensAuthStatus: (device: string) =>
    http.get<SiemensAuthStatusDto>(`/api/v1/devices/${encodeURIComponent(device)}/authentication`).then((r) => r.data),
  verifySiemensAuth: (device: string, username: string, password: string) =>
    http.post<SiemensAuthStatusDto>(`/api/v1/devices/${encodeURIComponent(device)}/authentication/verify`,
      { username, password }, { timeout: 30000 }).then((r) => r.data),
  deleteSiemensAuth: (device: string) =>
    http.delete<SiemensAuthStatusDto>(`/api/v1/devices/${encodeURIComponent(device)}/authentication`, { timeout: 30000 }).then((r) => r.data),
  // MES 采集上报(P3)
  mes: () => runtimeRequest('mes'),
  mesLog: (limit = 80) => runtimeRequest('mesLog', { limit }),
  // 异常中心聚合(设备离线根因折叠 + 节点异常,严重度排序;q=搜索过滤)
  anomalies: (q?: string) => runtimeRequest('anomalies', { q }),
  connectionDiagnostics: () => runtimeRequest<ConnectionDiagnosticsDto>('connectionDiagnostics'),
  // 点位级故障：哪个点位读不了、为什么、该怎么改
  tagFaults: () => runtimeRequest<TagFaultsDto>('tagFaults'),
  runtimeLogs: (params: Record<string, string | number>) =>
    http.get<RuntimeLogSnapshotDto>('/api/v1/runtime-logs', { params }).then((r) => r.data),
  updateRuntimeLogSettings: (retentionDays: number, storageDirectory?: string) =>
    http.put<{ retentionDays: number; storageDirectory: string; defaultDirectory: string }>('/api/v1/runtime-logs/settings', { retentionDays, storageDirectory }).then((r) => r.data),
  runtimeLogDirectories: (path = '') =>
    http.get<RuntimeLogDirectoryListingDto>('/api/v1/runtime-logs/directories', { params: { path } }).then((r) => r.data),
  /** 日志查询：现场大范围查询网关要扫很多日志文件，给足 60 秒(网关自己 8 秒左右会先返回部分结果)；background=自动刷新，不出「正在…」提示。 */
  logCenterLogs: (params: Record<string, string | number>, signal?: AbortSignal, options?: { background?: boolean }) =>
    http.get<LogCenterResponseDto>('/api/v1/log-center/logs', { params, signal, timeout: 60_000, igwBackground: options?.background }).then((r) => r.data),
  /** scan=true：同时在各本地硬盘上查找网关以前留下的日志(后台进行，查完随状态返回)。 */
  logCleanupStatus: (scan = false) => http.get<LogCleanupStatusDto>('/api/v1/log-center/cleanup',
    { params: scan ? { scan: true } : undefined, igwBackground: true }).then((r) => r.data),
  /** 开始一键清理：只清勾选的 paths；立即返回，网关在后台删除(不停服务)；已在清理时返回当前进度。 */
  startLogCleanup: (paths: string[]) => http.post<LogCleanupStatusDto>('/api/v1/log-center/cleanup', { paths },
    { validateStatus: status => status === 202 || status === 409 }).then((r) => r.data),
  logFilterOptions: () =>
    http.get<LogFilterOptionsDto>('/api/v1/log-center/filter-options', { igwBackground: true }).then((r) => r.data),
  logCenterStatus: (signal?: AbortSignal, background = false) =>
    http.get<LogCenterStatusDto>('/api/v1/log-center/status', { signal, igwBackground: background }).then((r) => r.data),
  updateLogRetention: (retentionDays: number) =>
    http.put<{ retentionDays: number; restartPending: boolean }>('/api/v1/log-center/settings/retention', { retentionDays }).then((r) => r.data),
  changeLogStorage: (storageDirectory: string) =>
    http.post<{ status: string; sourceDirectory: string; targetDirectory: string; totalBytes: number; copiedBytes: number }>(
      '/api/v1/log-center/settings/storage', { storageDirectory }).then((r) => r.data),
  logPayloadExchange: (id: string) =>
    http.get<LogPayloadExchangeDto>(`/api/v1/log-center/payloads/${encodeURIComponent(id)}/exchange`).then((r) => r.data),
  logPayload: (id: string, page = 0) =>
    http.get<LogPayloadPageDto>(`/api/v1/log-center/payloads/${encodeURIComponent(id)}`, { params: { page } }).then((r) => r.data),
  /** 按日志中心筛选条件打包导出(zip：文本日志 + 相关报文 + 清单)。大时间段可能要几十秒。 */
  exportLogs: async (params: Record<string, string | number>, includePayloads: boolean,
    onProgress?: (loadedBytes: number) => void, signal?: AbortSignal) => {
    const response = await http.get<Blob>('/api/v1/log-center/export', {
      params: { ...params, payloads: includePayloads ? 'true' : 'false' },
      responseType: 'blob', timeout: 600_000, signal,
      onDownloadProgress: event => onProgress?.(event.loaded),
    })
    const disposition = String(response.headers['content-disposition'] || '')
    const fileName = /filename\*=UTF-8''([^;]+)/i.exec(disposition)?.[1]
      ?? /filename="?([^";]+)"?/i.exec(disposition)?.[1] ?? 'igw-logs.zip'
    return {
      blob: response.data,
      fileName: decodeURIComponent(fileName),
      entries: Number(response.headers['x-igw-log-entries'] || 0),
      truncated: String(response.headers['x-igw-log-truncated'] || '') === 'true',
    }
  },
  downloadLogPayload: (id: string) =>
    http.get<Blob>(`/api/v1/log-center/payloads/${encodeURIComponent(id)}/download`, { responseType: 'blob' }).then((r) => r.data),
  // 任务时间线:节点流转事件(内存环形缓冲,时间正序)
  mesTimeline: (task: string) => runtimeRequest<any[]>('mesTimeline', { task }),

  // ---- 设备模板化配置(device-template-config)----
  templates: () => http.get<TemplateDto[]>('/api/v1/templates').then((r) => r.data),
  templateExport: (name: string) => http.get(`/api/v1/templates/${encodeURIComponent(name)}/export`).then((r) => r.data),
  templateExtract: (channel: string, device: string, templateName: string) =>
    http.post('/api/v1/templates/extract', { channel, device, templateName }, { timeout: 120000 }).then((r) => r.data),
  templateApply: (body: ApplyBody) =>
    http.post('/api/v1/templates/apply', body, { timeout: 120000 }).then((r) => r.data),
  templateImport: (tmplJson: string) =>
    http.post('/api/v1/templates/import', tmplJson, { headers: { 'Content-Type': 'application/json' }, timeout: 120000 }).then((r) => r.data),
  templateDelete: (name: string) =>
    http.delete(`/api/v1/templates/${encodeURIComponent(name)}`, { timeout: 120000 }).then((r) => r.data),
  templateUpdate: (name: string, tmplJson: string) =>
    http.put(`/api/v1/templates/${encodeURIComponent(name)}`, tmplJson, { headers: { 'Content-Type': 'application/json' }, timeout: 120000 }).then((r) => r.data),
  templateImportCsv: (csvText: string) =>
    http.post('/api/v1/templates/import-csv', csvText, { headers: { 'Content-Type': 'text/csv' }, timeout: 300000 }).then((r) => r.data),
  get templateCsvSampleUrl() { return withAccessToken('/api/v1/templates/import-csv/sample') },
  // 套用验证作业
  validationStart: (devices: string[]) => http.post('/api/v1/validation/jobs', { devices }).then((r) => r.data),
  validationJob: (id: string) => http.get(`/api/v1/validation/jobs/${encodeURIComponent(id)}`).then((r) => r.data),
  validationAdopt: (channel: string, device: string, points: AdoptPoint[]) =>
    http.post('/api/v1/validation/adopt', { channel, device, points }, { timeout: 120000 }).then((r) => r.data),

  // ---- MES 流程模板(mes-flow-template;印章语义:套用即拷贝断开)----
  flowTemplates: () => http.get<any[]>('/api/v1/mes/flow-templates').then((r) => r.data),
  flowTemplateTargets: () => http.get<FlowTemplateTargetCatalogDto>('/api/v1/mes/flow-template-targets').then((r) => r.data),
  flowTemplateExtract: (task: string, name: string, description?: string) =>
    http.post('/api/v1/mes/flow-templates/extract', { task, name, description }, { timeout: 120000 }).then((r) => r.data),
  flowTemplateApply: (templateName: string, targetGroup: string, replaceExisting = false, taskName?: string) =>
    http.post('/api/v1/mes/flow-templates/apply', { templateName, targetGroup, taskName, replaceExisting }, { timeout: 120000 }).then((r) => r.data),
  flowTemplateApplyBulk: (templateName: string, targetGroups: string[]) =>
    http.post('/api/v1/mes/flow-templates/apply-bulk', { templateName, targetGroups }, { timeout: 300000 }).then((r) => r.data),
  flowTemplateExport: (name: string) =>
    http.get(`/api/v1/mes/flow-templates/${encodeURIComponent(name)}/export`).then((r) => r.data),
  flowTemplateImport: (tmplJson: string) =>
    http.post('/api/v1/mes/flow-templates/import', tmplJson, { headers: { 'Content-Type': 'application/json' }, timeout: 120000 }).then((r) => r.data),
  flowTemplateDelete: (name: string) =>
    http.delete(`/api/v1/mes/flow-templates/${encodeURIComponent(name)}`, { timeout: 120000 }).then((r) => r.data),
}

export interface TemplateDto { name: string; driverId: string; description?: string; tagCount: number; instanceCount: number }
export interface ApplyBody { templateName: string; channel: string; deviceName: string; endpoint: string; settings?: Record<string, string>; description?: string }
export interface AdoptPoint { name: string; address: string; dataType?: string }

