<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { usePagedRows } from '../composables/usePagedRows'
import { api, INVALID_ACQUISITION_DIAGNOSTICS_ZIP, type ScanTimingDeviceDto, type ScanTimingRecordDto, type ScanTimingsDto } from '../api'
import { filterScanTimingDevices, formatScanStartedAt, formatTimingMs, isPassiveTimingMode, scanTimingWindowSummary } from '../utils/scanTimings'
import { useI18n } from '../i18n'

const props = defineProps<{ selectedChannels?: ReadonlySet<string> }>()
const { t } = useI18n()
const open = ref(true)
const snapshot = ref<ScanTimingsDto>({ devices: [], device: null, records: [], capacity: 200 })
const selectedDevice = ref('')
const tagQuery = ref('')
const loading = ref(false)
const error = ref('')
const exportError = ref('')
const exporting = ref(false)
const expandedRecordSequences = ref<number[]>([])
const lastSuccessAt = ref<number | null>(null)
const snapshotAgeNow = ref(Date.now())
let timer: ReturnType<typeof setInterval> | undefined
let queryTimer: ReturnType<typeof setTimeout> | undefined
let generation = 0
let pendingRefresh = false
let destroyed = false

const devices = computed(() => filterScanTimingDevices(snapshot.value.devices, props.selectedChannels || new Set()))
// 表格按“实际完成间隔比配置周期慢多少倍”排序，最慢的设备排在第一页最前面；只渲染当前页。
function lagRatio(device: ScanTimingDeviceDto): number {
  if (isPassiveTimingMode(device.mode)) return -1
  const configured = Number(device.configuredMinPeriodMs) || 0
  const actual = Number(device.actualP95PeriodMs) || Number(device.lastScanMs) || 0
  return configured > 0 ? actual / configured : actual / 1000
}
const devicesBySlowness = computed(() => [...devices.value].sort((a, b) => lagRatio(b) - lagRatio(a) || a.name.localeCompare(b.name)))
const devicePage = usePagedRows(devicesBySlowness, { resetOn: () => [...(props.selectedChannels || [])].join('|') })
const selectedSummary = computed(() => devices.value.find(device => device.name === selectedDevice.value) || null)
const records = computed<ScanTimingRecordDto[]>(() => snapshot.value.device === selectedDevice.value ? snapshot.value.records : [])
const windowSummary = computed(() => scanTimingWindowSummary(records.value))
const latestProtocolMaxMs = computed(() => records.value[0]?.protocol?.requestMaxMs ?? null)
const staleAgeSeconds = computed(() => lastSuccessAt.value === null ? null : Math.max(0, Math.floor((snapshotAgeNow.value - lastSuccessAt.value) / 1000)))

function optionalTiming(value?: number | null) { return formatTimingMs(value ?? null) }
function optionalCount(value?: number | null) { return value != null && value >= 0 ? value : '—' }
function physicalRequests(row: ScanTimingRecordDto) {
  if (row.latestProtocolRequestCount !== undefined) return optionalCount(row.latestProtocolRequestCount)
  if (row.protocol?.physicalRequests !== undefined) return optionalCount(row.protocol.physicalRequests)
  return row.protocol?.opaqueOperations ? '—' : optionalCount(row.protocol?.requests)
}
function nearSessionLimit(row: ScanTimingDeviceDto) {
  const count = row.sameEndpointSessionCount, limit = row.knownSessionLimit
  return count != null && count >= 0 && limit != null && limit > 0 && !!row.sessionLimitSource && (count >= limit * 0.8 || limit - count <= 2)
}
function optionalPercent(value?: number | null) { return value == null ? '—' : `${Math.round(value * 100) / 100}%` }
function confidenceLabel(value?: string | null) {
  return t(({ confirmed: '已确认', suspected: '疑似', medium: '中', low: '低' } as Record<string, string>)[String(value)] || String(value || '—'))
}
function attributionLabel(label?: string | null) {
  if (!label) return '—'
  const safeLabel = /网络|PLC|OPC UA/.test(label) && !label.includes('疑似') ? `${label}疑似` : label
  return t(safeLabel)
}
function probeStatusLabel(status?: string | null) {
  return t(({
    healthy: '健康',
    degraded: '退化',
    unavailable: '不可用',
    success: '成功',
    timeout: '超时',
    cancelled: '已取消',
  } as Record<string, string>)[String(status)] || String(status || '不可用'))
}
function factorLabel(code: string) {
  return t(({
    'igw-local-processing': 'IGW本地处理',
    'igw-shared-queue': 'IGW共享队列',
    'network-suspected': '网络链路疑似',
    'plc-opc-server-suspected': 'PLC/OPC UA服务器疑似',
    'request-split-or-retry': '请求拆分或重试',
  } as Record<string, string>)[code] || code)
}
function networkUnavailableReason(row: ScanTimingRecordDto) {
  if (!row.network) return t('当前批次没有网络探测窗口')
  return row.network.error || t('网络探测状态为 {status}', { status: probeStatusLabel(row.network.status) })
}
function localTimestampFilename(now = new Date()) {
  const pad = (value: number) => String(value).padStart(2, '0')
  return `igw-acquisition-diagnostics-${now.getFullYear()}${pad(now.getMonth() + 1)}${pad(now.getDate())}-${pad(now.getHours())}${pad(now.getMinutes())}${pad(now.getSeconds())}.zip`
}

async function downloadDiagnostics() {
  if (exporting.value) return
  exporting.value = true
  exportError.value = ''
  try {
    const blob = await api.acquisitionDiagnosticsExport()
    const href = URL.createObjectURL(blob)
    try {
      const link = document.createElement('a')
      link.href = href
      link.download = localTimestampFilename()
      link.click()
    } finally { URL.revokeObjectURL(href) }
  } catch (reason: any) {
    const message = reason?.response?.data?.error || reason?.message
    const messageKey = message === INVALID_ACQUISITION_DIAGNOSTICS_ZIP ? '诊断导出响应不是 ZIP 文件' : String(message || '')
    exportError.value = messageKey ? t(messageKey) : t('诊断包导出失败')
  } finally { exporting.value = false }
}

async function load() {
  if (destroyed || !open.value || document.hidden || expandedRecordSequences.value.length) return
  snapshotAgeNow.value = Date.now()
  if (loading.value) { pendingRefresh = true; return }
  const requestGeneration = generation
  const requestDevice = selectedDevice.value || undefined
  const requestTag = tagQuery.value.trim() || undefined
  pendingRefresh = false
  loading.value = true
  try {
    const result = await api.scanTimings(requestDevice, requestTag, 200)
    if (destroyed || requestGeneration !== generation) return
    snapshot.value = result
    lastSuccessAt.value = Date.now()
    snapshotAgeNow.value = lastSuccessAt.value
    const visible = filterScanTimingDevices(result.devices, props.selectedChannels || new Set())
    const next = visible.some(device => device.name === result.device)
      ? String(result.device)
      : visible.some(device => device.name === selectedDevice.value) ? selectedDevice.value : visible[0]?.name || ''
    const mustReload = !!next && result.device !== next
    selectedDevice.value = next
    error.value = ''
    if (mustReload) refresh()
  } catch (reason: any) {
    if (destroyed || requestGeneration !== generation) return
    snapshotAgeNow.value = Date.now()
    error.value = reason?.response?.data?.error || reason?.message || t('采集耗时读取失败')
  } finally {
    loading.value = false
    if (!destroyed && open.value && !document.hidden && (pendingRefresh || requestGeneration !== generation)) {
      pendingRefresh = false
      queueMicrotask(() => void load())
    }
  }
}

function refresh() { generation++; pendingRefresh = true; void load() }

function startPolling() {
  stopPolling()
  if (!open.value || document.hidden) return
  refresh()
  timer = setInterval(() => void load(), 2000)
}
function stopPolling() { if (timer) clearInterval(timer); timer = undefined }
function onVisibilityChange() { document.hidden ? stopPolling() : startPolling() }
function clearRecordExpansion() { expandedRecordSequences.value = [] }
function changeRecordExpansion(_row: ScanTimingRecordDto, expandedRows: ScanTimingRecordDto[]) {
  expandedRecordSequences.value = expandedRows.map(row => row.sequence)
  if (expandedRows.length) {
    generation++
    pendingRefresh = false
  } else {
    refresh()
  }
}
function changeDevice() { clearRecordExpansion(); refresh() }
function searchTags() {
  clearTimeout(queryTimer)
  clearRecordExpansion()
  generation++
  pendingRefresh = true
  queryTimer = setTimeout(() => void load(), 300)
}
function modeLabel(mode: ScanTimingDeviceDto['mode']) {
  return t(({ polling: '轮询', subscription: '订阅', mixed: '混合', standby: '待机' })[mode])
}
function statusLabel(status: ScanTimingRecordDto['status']) {
  return t(({ good: '正常', partial: '部分成功', bad: '质量差', error: '错误' })[status])
}
function statusType(status: ScanTimingRecordDto['status']): 'success' | 'warning' | 'danger' {
  return status === 'good' ? 'success' : status === 'partial' ? 'warning' : 'danger'
}
watch(() => props.selectedChannels, () => {
  clearRecordExpansion()
  const visible = devices.value
  if (!visible.some(device => device.name === selectedDevice.value)) selectedDevice.value = visible[0]?.name || ''
  refresh()
}, { deep: true })
watch(open, value => { clearRecordExpansion(); value ? startPolling() : stopPolling() })
onMounted(() => { document.addEventListener('visibilitychange', onVisibilityChange); startPolling() })
onUnmounted(() => { destroyed = true; generation++; pendingRefresh = false; stopPolling(); clearTimeout(queryTimer); document.removeEventListener('visibilitychange', onVisibilityChange) })
</script>

<template>
  <section class="scan-panel">
    <header class="scan-head">
      <div><strong>{{ $t('采集耗时') }}</strong><small>{{ $t('运行时诊断快照，每 2 秒刷新，不额外读取 PLC') }}</small></div>
      <div class="scan-actions">
        <button class="export-button" type="button" :disabled="exporting" @click="downloadDiagnostics">{{ $t(exporting ? '正在导出…' : '导出诊断包') }}</button>
        <button class="scan-toggle" type="button" @click="open = !open">{{ $t(open ? '收起' : '展开') }}</button>
      </div>
    </header>
    <div v-if="open" v-loading="loading && !snapshot.devices.length" class="scan-body">
      <div class="scan-filters">
        <el-select v-model="selectedDevice" :placeholder="$t('选择设备')" filterable @change="changeDevice">
          <el-option v-for="device in devices" :key="device.name" :label="device.name" :value="device.name" />
        </el-select>
        <el-input v-model="tagQuery" clearable :placeholder="$t('按标签路径筛选最近 200 批…')" @input="searchTags" />
        <span v-if="snapshot.capacity" class="capacity">{{ $t('诊断缓存') }} {{ snapshot.capacity }} {{ $t('批') }}</span>
        <span v-if="expandedRecordSequences.length" class="refreshing">{{ $t('展开查看中，自动刷新已暂停') }}</span>
        <span v-else-if="loading && snapshot.devices.length" class="refreshing">{{ $t('更新中…') }}</span>
      </div>
      <el-alert v-if="exportError" :title="exportError" type="error" :closable="false" show-icon />
      <el-alert v-if="error" :title="error" type="error" :closable="false" show-icon />
      <p v-if="error && staleAgeSeconds !== null" class="stale-note">{{ t('诊断快照已过期：{seconds} 秒前更新', { seconds: staleAgeSeconds }) }}</p>
      <el-empty v-if="!devices.length" :image-size="54" :description="$t('暂无采集耗时数据')" />
      <template v-else>
        <h3 class="section-title">{{ $t('所有设备概览') }}</h3>
        <el-table :data="devicePage.pagedRows.value" row-key="name" max-height="300" highlight-current-row @row-click="(row: ScanTimingDeviceDto) => { selectedDevice = row.name; changeDevice() }">
          <el-table-column prop="name" :label="$t('设备')" min-width="190" show-overflow-tooltip />
          <el-table-column :label="$t('模式')" width="84"><template #default="{ row }">{{ modeLabel(row.mode) }}</template></el-table-column>
          <el-table-column :label="$t('配置周期')" width="130"><template #default="{ row }">{{ row.configuredMinPeriodMs > 0 ? `${formatTimingMs(row.configuredMinPeriodMs)}${row.configuredMaxPeriodMs !== row.configuredMinPeriodMs ? ` – ${formatTimingMs(row.configuredMaxPeriodMs)}` : ''}` : '—' }}</template></el-table-column>
          <el-table-column :label="$t('平均完成间隔')" width="120"><template #default="{ row }">{{ isPassiveTimingMode(row.mode) ? '—' : formatTimingMs(row.actualAveragePeriodMs) }}</template></el-table-column>
          <el-table-column :label="$t('P95 完成间隔')" width="120"><template #default="{ row }">{{ isPassiveTimingMode(row.mode) ? '—' : `${$t('约')} ${formatTimingMs(row.actualP95PeriodMs)}` }}</template></el-table-column>
          <el-table-column :label="$t('最近批耗时')" width="105"><template #default="{ row }">{{ isPassiveTimingMode(row.mode) ? '—' : formatTimingMs(row.lastScanMs) }}</template></el-table-column>
          <el-table-column prop="pollingTags" :label="$t('轮询点位')" width="95" />
          <el-table-column prop="subscribedTags" :label="$t('订阅点位')" width="95" />
          <el-table-column :label="$t('逻辑批次')" width="95"><template #default="{ row }">{{ optionalCount(row.logicalBatchCount) }}</template></el-table-column>
          <el-table-column :label="$t('最近实际请求')" width="120"><template #default="{ row }">{{ optionalCount(row.latestProtocolRequestCount) }}</template></el-table-column>
          <el-table-column :label="$t('设备会话指纹 / 代次')" min-width="200"><template #default="{ row }">{{ row.deviceSessionFingerprint || '—' }} / {{ optionalCount(row.sessionGeneration) }}</template></el-table-column>
          <el-table-column :label="$t('同端点设备 / 活动会话')" min-width="180"><template #default="{ row }">{{ optionalCount(row.sameEndpointDeviceCount) }} / {{ optionalCount(row.sameEndpointSessionCount) }}</template></el-table-column>
          <el-table-column prop="overduePeriods" :label="$t('超期')" width="70" />
          <el-table-column :label="$t('轮询吞吐')" width="105"><template #default="{ row }">{{ row.samplesPerSecond }} {{ $t('点/秒') }}</template></el-table-column>
        </el-table>
        <el-pagination v-if="devicePage.total.value > devicePage.pageSize.value" class="scan-pager" small background
                       layout="total, prev, pager, next" :total="devicePage.total.value"
                       :page-size="devicePage.pageSize.value" v-model:current-page="devicePage.page.value" />
      </template>
      <template v-if="selectedSummary">
        <h3 class="section-title detail-title">{{ selectedSummary.name }} · {{ $t('批次详情') }}</h3>
        <el-alert v-if="nearSessionLimit(selectedSummary)" type="warning" :closable="false" show-icon :title="$t('活动设备会话接近已知上限')" :description="`${optionalCount(selectedSummary.sameEndpointSessionCount)} / ${optionalCount(selectedSummary.knownSessionLimit)} · ${$t('上限来源')} ${selectedSummary.sessionLimitSource}`" />
        <div class="session-evidence">
          <span>{{ $t('设备会话指纹 / 代次') }} {{ selectedSummary.deviceSessionFingerprint || '—' }} / {{ optionalCount(selectedSummary.sessionGeneration) }}</span>
          <span>{{ $t('同端点设备') }} {{ optionalCount(selectedSummary.sameEndpointDeviceCount) }}</span>
          <span>{{ $t('活动设备会话') }} {{ optionalCount(selectedSummary.sameEndpointSessionCount) }}</span>
          <span>{{ $t('已知会话上限') }} {{ optionalCount(selectedSummary.knownSessionLimit) }} · {{ $t('上限来源') }} {{ selectedSummary.sessionLimitSource || '—' }}</span>
        </div>
        <div class="metrics">
          <div><span>{{ $t('模式') }}</span><b>{{ modeLabel(selectedSummary.mode) }}</b></div>
          <div><span>{{ $t('设定采集周期') }}</span><b v-if="selectedSummary.configuredMinPeriodMs > 0">{{ formatTimingMs(selectedSummary.configuredMinPeriodMs) }}<template v-if="selectedSummary.configuredMaxPeriodMs !== selectedSummary.configuredMinPeriodMs"> – {{ formatTimingMs(selectedSummary.configuredMaxPeriodMs) }}</template></b><b v-else>—</b></div>
          <div><span>{{ $t('平均完成间隔') }}</span><b>{{ isPassiveTimingMode(selectedSummary.mode) ? $t('不适用') : formatTimingMs(selectedSummary.actualAveragePeriodMs) }}</b></div>
          <div><span>{{ $t('P95 完成间隔（近 60 秒）') }}</span><b>{{ isPassiveTimingMode(selectedSummary.mode) ? $t('不适用') : `${$t('约')} ${formatTimingMs(selectedSummary.actualP95PeriodMs)}` }}</b></div>
          <div><span>{{ $t('最近一批耗时') }}</span><b>{{ isPassiveTimingMode(selectedSummary.mode) ? $t('无主动读取') : formatTimingMs(selectedSummary.lastScanMs) }}</b></div>
          <div><span>{{ $t('最近周期读取') }}</span><b>{{ selectedSummary.lastScanAt ? formatScanStartedAt(selectedSummary.lastScanAt) : '—' }}</b></div>
          <div><span>{{ $t('轮询样本吞吐') }}</span><b>{{ selectedSummary.samplesPerSecond }} {{ $t('点/秒') }}</b></div>
          <div><span>{{ $t('点位') }}</span><b>{{ $t('轮询点位') }} {{ selectedSummary.pollingTags }} · {{ $t('订阅点位') }} {{ selectedSummary.subscribedTags }}</b></div>
          <div><span>{{ $t('逻辑批次') }} {{ optionalCount(selectedSummary.logicalBatchCount) }}</span><b>{{ $t('实际协议请求') }} {{ optionalCount(selectedSummary.latestProtocolRequestCount) }}</b></div>
          <div><span>{{ $t('超期') }}</span><b :class="{ overdue: selectedSummary.overduePeriods }">{{ selectedSummary.overduePeriods }} {{ $t('个周期') }}</b></div>
        </div>
        <div class="metrics layered-metrics">
          <div><span>{{ $t('IGW 排队 P95') }}</span><b>{{ optionalTiming(selectedSummary.queueP95Ms) }}</b></div>
          <div><span>{{ $t('批次请求最大耗时 P95') }}</span><b>{{ optionalTiming(selectedSummary.protocolRequestP95Ms) }}</b></div>
          <div><span>{{ $t('最近 / 总体请求最大') }}</span><b>{{ optionalTiming(latestProtocolMaxMs) }} / {{ optionalTiming(selectedSummary.protocolRequestMaxMs) }}</b></div>
          <div><span>{{ $t('网络 RTT P95 / 丢包') }}</span><b>{{ optionalTiming(selectedSummary.networkP95RttMs) }} / {{ optionalPercent(selectedSummary.networkLossPercent) }}</b></div>
          <div><span>{{ $t('IGW 本地 P95') }}</span><b>{{ optionalTiming(selectedSummary.localP95Ms) }}</b></div>
          <div><span>{{ $t('最近归因 / 置信度') }}</span><b>{{ attributionLabel(selectedSummary.latestAttribution) }} / {{ confidenceLabel(selectedSummary.latestAttributionConfidence) }}</b></div>
        </div>
        <p v-if="isPassiveTimingMode(selectedSummary.mode)" class="passive-note">{{ $t(selectedSummary.mode === 'standby' ? '当前没有执行周期轮询；下方可查看保留的历史记录，设定周期来自标签配置。' : '订阅模式由 PLC/服务器被动推送，没有主动读取往返；设定周期来自标签配置，实际采样由服务端决定。') }}</p>
        <p v-else class="timing-note">{{ $t('下表仅记录周期轮询批次。概览间隔按同批完成到完成统计；批次“实际开始间隔”按同批开始到开始统计。驱动耗时包含拆包与重试，同批标签共享本批耗时。') }}</p>
        <div class="window-summary">
          <span>{{ $t('当前筛选 {count} / 缓存 {capacity} 批', { count: windowSummary.count, capacity: snapshot.capacity }) }}</span>
          <span>{{ $t('平均读取耗时') }} <b>{{ formatTimingMs(windowSummary.averageTotalMs) }}</b></span>
          <span>{{ $t('最大耗时') }} <b>{{ formatTimingMs(windowSummary.maxTotalMs) }}</b></span>
          <span :class="{ overdue: windowSummary.slowCount }">{{ $t('慢批次') }} <b>{{ windowSummary.slowCount }}</b></span>
        </div>
        <el-table v-if="records.length" :data="records" row-key="sequence" :expand-row-keys="expandedRecordSequences" max-height="360" @expand-change="changeRecordExpansion">
          <el-table-column type="expand" width="42">
            <template #default="{ row }">
              <div class="protocol-timing">
                <span>{{ $t('实际协议请求数') }} · {{ $t('请求数 / 失败 / 取消') }} <b>{{ row.protocol ? `${physicalRequests(row)} / ${optionalCount(row.protocol.failedRequests)} / ${optionalCount(row.protocol.cancelledRequests)}` : '—' }}</b></span>
                <span>{{ $t('请求累计') }} <b>{{ formatTimingMs(row.protocol ? Math.round(row.protocol.requestTotalMs * 100) / 100 : null) }}</b></span>
                <span>{{ $t('请求最大') }} <b>{{ formatTimingMs(row.protocol ? Math.round(row.protocol.requestMaxMs * 100) / 100 : null) }}</b></span>
                <span>{{ $t('排队') }} <b>{{ formatTimingMs(row.protocol ? Math.round(row.protocol.queueMs * 100) / 100 : null) }}</b></span>
                <span>{{ $t('同会话重试 / 批次降级') }} <b>{{ row.sameSessionRetries == null && row.batchDownshifts == null ? '—' : `${optionalCount(row.sameSessionRetries)} / ${optionalCount(row.batchDownshifts)}` }}</b></span>
                <small>{{ $t('仅周期轮询；累计可能大于墙钟耗时（并行请求）。未提供协议指标显示 —，不表示零请求；本页不涵盖全部写入记录。') }}</small>
              </div>
              <div class="request-evidence">
                <p>{{ $t('本批会话') }} {{ row.deviceSessionFingerprint || '—' }} / {{ optionalCount(row.sessionGeneration) }} · {{ $t('逻辑批次') }} {{ optionalCount(row.logicalBatchCount) }} · {{ $t('实际协议请求') }} {{ physicalRequests(row) }} · {{ $t('省略请求样本') }} {{ optionalCount(row.omittedProtocolRequestSamples) }}</p>
                <table class="request-samples" v-if="row.protocolRequestSamples?.length">
                  <thead><tr><th>{{ $t('操作') }}</th><th>{{ $t('DB / 地址范围') }}</th><th>{{ $t('点位数') }}</th><th>{{ $t('有效载荷字节') }}</th><th>{{ $t('间隙字节') }}</th><th>{{ $t('耗时') }}</th><th>{{ $t('状态') }}</th></tr></thead>
                  <tbody><tr v-for="(request, index) in row.protocolRequestSamples.slice(0, 64)" :key="index">
                    <td>{{ request.descriptor?.operation || '—' }}</td><td>{{ request.descriptor?.scope || '—' }}</td>
                    <td>{{ optionalCount(request.descriptor?.tagCount) }}</td><td>{{ optionalCount(request.descriptor?.payloadBytes) }}</td><td>{{ optionalCount(request.descriptor?.gapBytes) }}</td>
                    <td>{{ optionalTiming(request.elapsedMs) }}</td><td>{{ $t(request.cancelled ? '已取消' : request.failed ? '错误' : '成功') }}</td>
                  </tr></tbody>
                </table>
                <p v-else>{{ $t('协议请求样本不可用') }} —</p>
              </div>
              <div class="network-evidence">
                <span>{{ $t('探测状态') }} <b>{{ probeStatusLabel(row.network?.status) }}</b></span>
                <span>{{ $t('网络 RTT 最新 / P95 / 基线 P95') }} <b>{{ optionalTiming(row.network?.latestRttMs) }} / {{ optionalTiming(row.network?.p95RttMs) }} / {{ optionalTiming(row.network?.baselineP95RttMs) }}</b></span>
                <span>{{ $t('探测成功 / 失败 / 丢包') }} <b>{{ row.network ? `${row.network.successCount ?? '—'} / ${row.network.failedCount ?? '—'} / ${optionalPercent(row.network.lossPercent)}` : '—' }}</b></span>
                <span>{{ $t('源地址 / 远端地址') }} <b>{{ row.network?.sourceAddress || '—' }} / {{ row.network?.remoteAddress || '—' }}</b></span>
                <small v-if="row.network && (!row.network.sourceAddress || !row.network.remoteAddress)">{{ $t('当前批次未提供源/远端地址；可从诊断包 network.csv 查看。') }}</small>
                <p v-if="!row.network || row.network.status === 'unavailable'" class="unavailable-note"><b>{{ $t('当前网络证据不可用，无法区分网络链路与 PLC/OPC UA 服务器') }}</b>：{{ networkUnavailableReason(row) }}</p>
              </div>
              <div v-if="row.attribution" class="attribution-evidence">
                <span><b>{{ attributionLabel(row.attribution.label) }}</b> · {{ confidenceLabel(row.attribution.confidence) }}</span>
                <div v-if="row.attribution.evidence?.length"><b>{{ $t('证据') }}</b><code v-for="item in row.attribution.evidence.slice(0, 12)" :key="item">{{ item }}</code></div>
                <div v-if="row.attribution.factors?.length"><b>{{ $t('影响因素') }}</b><code v-for="item in row.attribution.factors.slice(0, 8)" :key="item">{{ factorLabel(item) }}</code></div>
              </div>
              <div class="tag-list"><b>{{ $t('本批标签（{count}）', { count: row.tagIds.length }) }}</b><code v-for="tag in row.tagIds" :key="tag">{{ tag }}</code><span v-if="!row.tagIds.length">{{ $t('无匹配标签') }}</span></div>
            </template>
          </el-table-column>
          <el-table-column prop="sequence" label="#" width="76" />
          <el-table-column :label="$t('开始时间')" min-width="190"><template #default="{ row }">{{ formatScanStartedAt(row.startedAt) }}</template></el-table-column>
          <el-table-column prop="blockId" :label="$t('采集块')" min-width="120" show-overflow-tooltip />
          <el-table-column :label="$t('设定周期')" width="100"><template #default="{ row }">{{ formatTimingMs(row.configuredPeriodMs) }}</template></el-table-column>
          <el-table-column :label="$t('实际开始间隔')" width="120"><template #default="{ row }">{{ formatTimingMs(row.actualIntervalMs) }}</template></el-table-column>
          <el-table-column :label="$t('总耗时')" width="90"><template #default="{ row }">{{ formatTimingMs(row.totalMs) }}</template></el-table-column>
          <el-table-column :label="$t('驱动 / 本地')" width="125"><template #default="{ row }">{{ row.driverMs }} / {{ row.localMs }} ms</template></el-table-column>
          <el-table-column prop="tagCount" :label="$t('标签数')" width="80" />
          <el-table-column :label="$t('样本 / 差')" width="100"><template #default="{ row }">{{ row.sampleCount }} / {{ row.badCount }}</template></el-table-column>
          <el-table-column :label="$t('状态')" width="105"><template #default="{ row }"><el-tag :type="statusType(row.status)" size="small">{{ statusLabel(row.status) }}</el-tag></template></el-table-column>
          <el-table-column :label="$t('漏期')" width="70"><template #default="{ row }">{{ row.missedPeriods }}</template></el-table-column>
          <el-table-column prop="error" :label="$t('错误')" min-width="160" show-overflow-tooltip><template #default="{ row }">{{ row.error || '—' }}</template></el-table-column>
        </el-table>
        <el-empty v-else :image-size="48" :description="$t('当前筛选没有采集批次')" />
      </template>
    </div>
  </section>
</template>

<style scoped>
.session-evidence { display:flex; flex-wrap:wrap; gap:8px 20px; margin:10px 0; font-size:12px; }
.request-evidence { padding:8px 16px; overflow:auto; font-size:12px; }
.request-samples { border-collapse:collapse; width:100%; }
.request-samples th,.request-samples td { padding:6px 10px; border:1px solid #e5ebf2; text-align:left; overflow-wrap:anywhere; }
.protocol-timing,.network-evidence { display:flex; flex-wrap:wrap; gap:8px 20px; padding:10px 16px; }
.protocol-timing small,.network-evidence small { flex-basis:100%; color:#7b8796; }
.network-evidence { border-top:1px solid #edf1f5; }
.unavailable-note { flex-basis:100%; margin:0; color:#b88230; }
.attribution-evidence { margin:0 16px; padding:10px; border-radius:7px; background:#f7f9fc; display:grid; gap:7px; }
.attribution-evidence > div { display:flex; flex-wrap:wrap; gap:6px; align-items:center; }
.attribution-evidence code { color:#40556b; background:#fff; border:1px solid #e5ebf2; border-radius:4px; padding:2px 5px; overflow-wrap:anywhere; }
.scan-panel { margin: 12px 16px; border: 1px solid #dce5ef; border-radius: 10px; background: #fff; overflow: hidden; }
.scan-head { display:flex; align-items:center; justify-content:space-between; padding:12px 14px; background:#f7faff; }
.scan-head > div { display:flex; align-items:baseline; gap:10px; }.scan-head small,.capacity { color:#7b8796; font-size:12px; }
.scan-actions { flex-shrink:0; }.export-button,.scan-toggle { border:0; background:transparent; color:#337ecc; cursor:pointer; }.export-button:disabled { color:#9aabbf; cursor:wait; }
.refreshing { color:#337ecc; font-size:12px; }
.stale-note { margin:8px 0; color:#b88230; font-size:12px; }
.scan-body { padding:12px 14px; min-height:90px; }.scan-filters { display:flex; gap:10px; align-items:center; margin-bottom:10px; }
.scan-filters :deep(.el-select) { width:260px; }.scan-filters :deep(.el-input) { width:320px; }.capacity { margin-left:auto; }
.metrics { display:grid; grid-template-columns:repeat(auto-fit,minmax(150px,1fr)); gap:8px; margin:10px 0; }
.metrics > div { padding:9px 10px; border:1px solid #e5ebf2; border-radius:7px; background:#fbfcfe; display:flex; flex-direction:column; gap:4px; }
.metrics span { color:#728095; font-size:12px; }.metrics b { color:#26384f; font-size:13px; }.metrics .overdue { color:#c45656; }
.layered-metrics { grid-template-columns:repeat(auto-fit,minmax(180px,1fr)); }
.passive-note,.timing-note { margin:8px 0; color:#69778a; font-size:12px; }.passive-note { color:#8a6d3b; }
.section-title { margin:12px 0 7px; font-size:14px; color:#34465b; }.detail-title { margin-top:16px; }
.window-summary { display:flex; flex-wrap:wrap; gap:16px; margin:8px 0; padding:8px 10px; border-radius:7px; background:#f7f9fc; color:#64748b; font-size:12px; }.window-summary b { color:#26384f; }.window-summary .overdue,.window-summary .overdue b { color:#c45656; }
.tag-list { padding:8px 16px; display:flex; flex-direction:column; gap:5px; }.tag-list code { overflow-wrap:anywhere; color:#40556b; }
@media (max-width: 760px) { .scan-filters { align-items:stretch; flex-direction:column; }.scan-filters :deep(.el-select),.scan-filters :deep(.el-input) { width:100%; }.capacity { margin-left:0; } }
.scan-pager { margin-top: 8px; justify-content: flex-end; }
</style>
