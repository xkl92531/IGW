<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { api, type LogFilterOptionsDto, type LogCleanupStatusDto, type LogCenterEntryDto, type LogCenterResponseDto, type LogCenterStatusDto, type LogHistogramBucketDto, type LogPayloadExchangeDto } from '../api'
import { buildLogQuery, createLogFilters, logFiltersFromQuery, observedDeviceState, type LogCategory, type LogFilters } from '../utils/logCenter'
import { useRoute } from 'vue-router'
import { useI18n } from '../i18n'
import { canEdit } from '../auth'
import LogScopeRail from '../components/logs/LogScopeRail.vue'
import LogFilterBar from '../components/logs/LogFilterBar.vue'
import LogTrendPanel from '../components/logs/LogTrendPanel.vue'
import LogTimeCorridor from '../components/logs/LogTimeCorridor.vue'
import LogDetailDrawer from '../components/logs/LogDetailDrawer.vue'
import RuntimeLogDirectoryDialog from '../components/RuntimeLogDirectoryDialog.vue'
import ScanTimingPanel from '../components/ScanTimingPanel.vue'

const { t } = useI18n()
// 只能看的账号：可以查询、导出日志，不能清理日志、改保留天数和存储位置(后端 /api/v1/log-center 写操作按 logs 判断)
const editable = computed(() => canEdit('logs'))
// 地址里带的条件：采集性能页跳来带 scope；工程配置右键「查看日志」带 path(工程路径)或 taskId 和 hours
const route = useRoute()
const initialFilters = createLogFilters(logFiltersFromQuery(route?.query || {}))
const draft = reactive<LogFilters>({ ...initialFilters })
const applied = ref<LogFilters>({ ...initialFilters })
const rows = ref<LogCenterEntryDto[]>([])
const total = ref(0)
const tookMs = ref(0)
const histogram = ref<LogHistogramBucketDto[]>([])
const devices = ref<LogCenterResponseDto['devices']>([])
const outages = ref<LogCenterResponseDto['outages']>([])
const status = ref<LogCenterStatusDto | null>(null)
const page = ref(1)
const pageSize = ref(50)
const loading = ref(false)
const statusLoading = ref(false)
const loadError = ref('')
const staleResults = ref(false)
const selected = ref<LogCenterEntryDto | null>(null)
const payload = ref<LogPayloadExchangeDto | null>(null)
const payloadLoading = ref(false)
const settingsOpen = ref(false)
const savingSettings = ref(false)
const retentionDraft = ref(30)
const storageDraft = ref('')
const directoryPickerOpen = ref(false)
const settingsError = ref('')
const partialResult = ref(false)
const liveWindow = ref(true)
let queryController: AbortController | null = null
let statusController: AbortController | null = null
let refreshTimer: ReturnType<typeof setTimeout> | undefined
let payloadGeneration = 0

// 查询条件的下拉选项(协议/通道/设备/标记组)，按当前工程给出；取不到时下拉为空，不影响查日志
const filterOptions = ref<LogFilterOptionsDto>({ protocols: [], channels: [], devices: [], groups: [] })
async function loadFilterOptions() {
  try { filterOptions.value = await api.logFilterOptions() } catch { /* 旧网关没有这个接口 */ }
}
const activeChips = computed(() => {
  const chips: Array<{ key: keyof LogFilters; label: string; value: string }> = []
  const filters = applied.value
  if (filters.category !== 'all') chips.push({ key: 'category', label: t('分类'), value: t(filters.category) })
  const display: Partial<Record<keyof LogFilters, (value: string) => string>> = {
    protocol: value => filterOptions.value.protocols.find(item => item.id === value)?.name || value,
    device: value => filterOptions.value.devices.find(item => item.path === value)?.name || value,
  }
  const fields: Array<[keyof LogFilters, string]> = [
    ['protocol', '协议'], ['channel', '通道'], ['device', '设备'], ['group', '标记组'], ['task', '任务'],
    ['taskId', '任务 ID'], ['path', '工程位置'],
    ['module', '模块'], ['feature', '功能'], ['interface', '接口'], ['level', '级别'], ['database', '数据块'],
    ['search', '关键词'], ['traceId', '排障编号'],
  ]
  for (const [key, label] of fields) {
    if (!filters[key] || key === 'task' && filters.group) continue   // 标记组已经显示了，不再重复显示它的任务
    chips.push({ key, label: t(key === 'group' && !filters.task ? '标记组 / 接口 URL' : label), value: display[key]?.(filters[key]) ?? filters[key] })
  }
  return chips
})
const scopeCounts = computed(() => ({ [applied.value.category]: total.value }))
const onlineDevices = computed(() => devices.value.filter(device => observedDeviceState(device) === 'online').length)
const knownDevices = computed(() => devices.value.filter(device => observedDeviceState(device) !== 'unknown').length)
const questDb = computed(() => status.value?.engine === 'questdb')
const historyImporting = computed(() => questDb.value && status.value?.historyImported === false)
const storageMigrating = computed(() => {
  const value = status.value?.migrationStatus
  return typeof value === 'number' ? value >= 0 && value <= 2
    : typeof value === 'string' && ['pending', 'copying', 'copied'].includes(value.toLowerCase())
})
const storageStateText = computed(() => storageMigrating.value ? t('目录迁移中') : t(status.value?.available ? '运行中' : '写入积压'))
const partialResultText = computed(() => historyImporting.value ? t('旧日志尚未全部导入，当前结果可能不完整；原文件保留')
  : t('时间范围较大，只统计了最近的部分，可缩小范围或加筛选条件'))
const storageSizeText = computed(() => {
  const bytes = status.value?.storageBytes || 0
  return bytes >= 1_073_741_824 ? `${(bytes / 1_073_741_824).toFixed(1)} GB` : `${Math.round(bytes / 1_048_576)} MB`
})
// ---- 一键清理：删掉网关不再写入的旧报文/旧日志，后台进行，不停服务 ----
const cleanupOpen = ref(false)
const cleanup = ref<LogCleanupStatusDto | null>(null)
const cleanupStarting = ref(false)
let cleanupTimer: ReturnType<typeof setTimeout> | undefined
const cleanupRunning = computed(() => cleanup.value?.state === 'Running')
const cleanupScanning = computed(() => cleanup.value?.scanning === true)
// 勾选要清理的项：新出现的默认勾上，取消过的保持不勾(轮询刷新列表时不被重新勾上)
const cleanupChecked = ref<string[]>([])
const cleanupSeen = new Set<string>()
watch(() => cleanup.value?.residues, residues => {
  const paths = (residues || []).map(item => item.path)
  cleanupChecked.value = [...cleanupChecked.value.filter(path => paths.includes(path)),
    ...paths.filter(path => !cleanupSeen.has(path))]
  paths.forEach(path => cleanupSeen.add(path))
})
function sizeText(bytes: number) {
  return bytes >= 1_073_741_824 ? `${(bytes / 1_073_741_824).toFixed(1)} GB` : `${Math.round(bytes / 1_048_576)} MB`
}
async function pollCleanup(scan = false) {
  clearTimeout(cleanupTimer)
  try { cleanup.value = await api.logCleanupStatus(scan) }
  catch { /* 下次再取 */ }
  // 清理中、查找中持续刷新(对话框关了也继续，顶部状态栏会显示)
  if (cleanupRunning.value || cleanupScanning.value || cleanupOpen.value && cleanup.value?.blockedReason) cleanupTimer = setTimeout(() => void pollCleanup(), 2000)
}
async function openCleanup() {
  cleanupOpen.value = true
  // 打开窗口时才去各硬盘上找旧日志：装过好几个盘、卸载重装后没有记录的也能找到
  await pollCleanup(true)
}
async function startCleanup() {
  cleanupStarting.value = true
  try {
    cleanup.value = await api.startLogCleanup(cleanupChecked.value)
    void pollCleanup()
  } catch (error: any) {
    ElMessage.error(t('启动清理失败：') + (error?.response?.data?.error || error?.message || ''))
  } finally { cleanupStarting.value = false }
}

const migrationPercent = computed(() => {
  if (!status.value?.migrationTotalBytes) return 0
  return Math.min(100, Math.round(status.value.migrationCopiedBytes * 100 / status.value.migrationTotalBytes))
})

function replaceDraft(value: LogFilters) {
  if (value.from !== draft.from || value.to !== draft.to) liveWindow.value = false
  Object.assign(draft, value)
}

function describeQueryFailure(error: any): string {
  const responseStatus = Number(error?.response?.status)
  if (responseStatus === 404)
    return t('当前 IGW 后端未提供新版日志接口，请升级或启动匹配版本')
  return error?.response?.data?.error || error?.message || t('日志查询失败')
}

async function reload(mode: 'initial' | 'manual' | 'silent' = 'manual') {
  queryController?.abort()
  const controller = new AbortController()
  queryController = controller
  if (mode !== 'silent') loading.value = true
  try {
    const result = await api.logCenterLogs(buildLogQuery(applied.value, page.value, pageSize.value), controller.signal,
      mode === 'silent' ? { background: true } : undefined)
    if (controller.signal.aborted) return
    rows.value = result.items || []
    total.value = result.total || 0
    tookMs.value = result.tookMs || 0
    histogram.value = result.histogram || []
    partialResult.value = result.partial === true
    devices.value = result.devices || []
    outages.value = result.outages || []
    loadError.value = ''
    staleResults.value = false
  } catch (error: any) {
    if (controller.signal.aborted || error?.code === 'ERR_CANCELED') return
    loadError.value = describeQueryFailure(error)
    staleResults.value = rows.value.length > 0
    if (mode === 'manual') ElMessage.error(loadError.value)
  } finally {
    if (queryController === controller) loading.value = false
  }
}

async function loadStatus() {
  statusController?.abort()
  const controller = new AbortController()
  statusController = controller
  statusLoading.value = true
  try { status.value = await api.logCenterStatus(controller.signal, true) }
  catch (error: any) {
    if (!controller.signal.aborted && error?.code !== 'ERR_CANCELED' && !status.value)
      status.value = { available: false, state: 'unavailable', port: 0, retentionDays: 30, storageDirectory: '', spool: { pending: 0, failed: 0, dropped: 0 }, migrationTotalBytes: 0, migrationCopiedBytes: 0 }
  } finally { if (statusController === controller) statusLoading.value = false }
}

async function applyFilters() { applied.value = { ...draft }; page.value = 1; await reload('manual') }
async function resetFilters() {
  const reset = createLogFilters({ category: draft.category })
  Object.assign(draft, reset); applied.value = { ...reset }; page.value = 1; liveWindow.value = true
  await reload('manual')
}
async function selectScope(value: LogCategory) {
  draft.category = value; applied.value = { ...applied.value, category: value }; page.value = 1
  await reload('manual')
}
async function removeChip(key: keyof LogFilters) {
  const value = key === 'category' ? 'all' : ''
  // 去掉标记组时连同它的任务一起去掉
  const changes: Partial<LogFilters> = key === 'group' ? { group: '', task: '' } : { [key]: value }
  Object.assign(draft, changes)
  applied.value = { ...applied.value, ...changes } as LogFilters
  page.value = 1
  await reload('manual')
}
async function changePage(value: number) { page.value = value; await reload('manual') }
async function changePageSize(value: number) { pageSize.value = value; page.value = 1; await reload('manual') }
function selectTrend(bucket: LogHistogramBucketDto) {
  const index = histogram.value.indexOf(bucket), start = new Date(bucket.start), next = histogram.value[index + 1]?.start
  Object.assign(draft, { from: start.toISOString(), to: next ? new Date(next).toISOString() : new Date(start.getTime() + 60_000).toISOString() })
  liveWindow.value = false
  void applyFilters()
}

function selectEntry(entry: LogCenterEntryDto) {
  payloadGeneration++
  selected.value = entry
  payload.value = null
  if (entry.payloadId) void loadPayload()
}
async function loadPayload() {
  const payloadId = selected.value?.payloadId
  if (!payloadId) return
  const generation = ++payloadGeneration
  payloadLoading.value = true
  try {
    const result = await api.logPayloadExchange(payloadId)
    if (generation === payloadGeneration && selected.value?.payloadId === payloadId) payload.value = result
  }
  catch (error: any) {
    if (generation === payloadGeneration) ElMessage.error(error?.response?.data?.error || error?.message || t('报文读取失败'))
  }
  finally { if (generation === payloadGeneration) payloadLoading.value = false }
}
function closeEntry() { payloadGeneration++; selected.value = null; payload.value = null; payloadLoading.value = false }
async function downloadPayload() {
  if (!selected.value?.payloadId) return
  try {
    const blob = await api.downloadLogPayload(selected.value.payloadId)
    const url = URL.createObjectURL(blob), anchor = document.createElement('a')
    anchor.href = url; anchor.download = `igw-log-payload-${selected.value.payloadId}.txt`; anchor.click(); URL.revokeObjectURL(url)
  } catch (error: any) { ElMessage.error(error?.response?.data?.error || error?.message || t('报文下载失败')) }
}

// ---------- 打包导出：按当前筛选条件生成 zip，方便从现场发给其他人 ----------
const exportOpen = ref(false)
const exportRange = ref<'current' | 'hour' | 'day' | 'today'>('current')
const exportPayloads = ref(true)
const exportFormat = ref<'jsonl' | 'csv' | 'text'>('jsonl')
const exporting = ref(false)
const exportLoaded = ref(0)
let exportController: AbortController | null = null
function exportFilters(): LogFilters {
  const now = new Date()
  const range = exportRange.value
  if (range === 'current') return { ...applied.value }
  const from = range === 'hour' ? new Date(now.getTime() - 3_600_000)
    : range === 'day' ? new Date(now.getTime() - 86_400_000)
    : new Date(now.getFullYear(), now.getMonth(), now.getDate())
  return { ...applied.value, from: from.toISOString(), to: now.toISOString() }
}
async function runExport() {
  if (exporting.value) return
  exporting.value = true; exportLoaded.value = 0
  exportController = new AbortController()
  try {
    const params = buildLogQuery(exportFilters(), 1, 50)
    params.format = exportFormat.value
    delete params.page; delete params.pageSize
    const result = await api.exportLogs(params, exportPayloads.value, loaded => { exportLoaded.value = loaded }, exportController.signal)
    const url = URL.createObjectURL(result.blob), anchor = document.createElement('a')
    anchor.href = url; anchor.download = result.fileName; anchor.click(); URL.revokeObjectURL(url)
    exportOpen.value = false
    if (result.truncated) ElMessage.warning(t('已导出 {count} 条，达到单次上限，请缩小时间范围分批导出', { count: result.entries }))
    else ElMessage.success(t('已导出 {count} 条日志', { count: result.entries }))
  } catch (error: any) {
    if (error?.name === 'CanceledError' || error?.name === 'AbortError') return
    let message = error?.message || t('日志导出失败')
    const data = error?.response?.data
    if (data instanceof Blob) { try { message = JSON.parse(await data.text()).error || message } catch { /* 非 JSON 响应 */ } }
    ElMessage.error(message)
  } finally { exporting.value = false; exportController = null }
}
function cancelExport() { exportController?.abort(); exportOpen.value = false }
const exportLoadedText = computed(() => exportLoaded.value >= 1_048_576
  ? `${(exportLoaded.value / 1_048_576).toFixed(1)} MB` : `${Math.round(exportLoaded.value / 1024)} KB`)

function openSettings() {
  retentionDraft.value = status.value?.retentionDays || 30; storageDraft.value = status.value?.storageDirectory || ''
  settingsError.value = ''
  settingsOpen.value = true
}
async function saveSettings() {
  if (storageMigrating.value) { ElMessage.warning(t('日志目录正在迁移，请完成后再修改设置')); return }
  if (!Number.isInteger(retentionDraft.value) || retentionDraft.value < 1 || retentionDraft.value > 3650) {
    ElMessage.warning(t('日志保留天数必须在 1 到 3650 天之间')); return
  }
  savingSettings.value = true; settingsError.value = ''
  try {
    if (!status.value || retentionDraft.value !== status.value.retentionDays) await api.updateLogRetention(retentionDraft.value)
    const moving = storageDraft.value.trim() && (!status.value || storageDraft.value.trim() !== status.value.storageDirectory)
    if (moving) await api.changeLogStorage(storageDraft.value.trim())
    await loadStatus(); settingsOpen.value = false
    ElMessage.success(t(moving ? '日志目录迁移已开始，完成后自动切换；原目录暂时保留' : '日志设置已保存并开始生效'))
  } catch (error: any) {
    settingsError.value = error?.response?.data?.error || error?.message || t('日志设置保存失败'); ElMessage.error(settingsError.value)
  } finally { savingSettings.value = false }
}

function slideWindowToNow() {
  if (!liveWindow.value) return
  const duration = Math.max(60_000, Date.parse(applied.value.to) - Date.parse(applied.value.from)), now = Date.now()
  applied.value = { ...applied.value, from: new Date(now - duration).toISOString(), to: new Date(now).toISOString() }
  // 只滑动时间：正在编辑、还没点「查询日志」的其他条件不能被每 10 秒的刷新冲掉
  draft.from = applied.value.from; draft.to = applied.value.to
}
async function refreshNow() { slideWindowToNow(); await Promise.all([reload('manual'), loadStatus()]) }
function scheduleRefresh() {
  clearTimeout(refreshTimer)
  refreshTimer = setTimeout(async () => {
    // 用户自己点的查询还没返回(大范围查询要十几秒)：这次不刷新，不能把它取消掉
    if (loading.value) { scheduleRefresh(); return }
    slideWindowToNow(); await Promise.all([reload('silent'), loadStatus()]); scheduleRefresh()
  }, 10_000)
}
onMounted(() => { void Promise.all([reload('initial'), loadStatus(), pollCleanup(), loadFilterOptions()]); scheduleRefresh() })
onUnmounted(() => { clearTimeout(refreshTimer); clearTimeout(cleanupTimer); queryController?.abort(); statusController?.abort() })
</script>

<template>
  <section class="log-workbench" data-testid="log-workbench">
    <header class="workbench-head"><div><div class="eyebrow">IGW EVENT WORKBENCH</div><h1>{{ t('日志中心') }}</h1><p>{{ t(questDb ? '按采集、接口、状态和性能维度追踪网关事件，日志和接口报文保存在本机 QuestDB 中。' : '按采集、接口、状态和性能维度追踪网关事件，日志由网关按时间分段压缩保存在本机。') }}</p></div>
      <div class="head-actions"><el-button v-if="editable" data-testid="log-cleanup" @click="openCleanup">{{ t('一键清理') }}</el-button><el-button data-testid="log-export" @click="exportOpen = true">{{ t('打包导出') }}</el-button><el-button @click="openSettings">{{ t('日志设置') }}</el-button><el-button type="primary" :loading="loading" @click="refreshNow">{{ t('立即刷新') }}</el-button></div></header>
    <div class="runtime-strip" :class="status?.available ? 'healthy' : 'unavailable'">
      <span class="runtime-state"><i />{{ questDb ? 'QuestDB' : t('日志存储') }} · {{ storageStateText }}</span><span>{{ t('保留') }} <b>{{ status?.retentionDays ?? '—' }} {{ t('天') }}</b></span>
      <span>{{ t('已用空间') }} <b>{{ storageSizeText }}</b></span><span>{{ t('待写入') }} <b>{{ status?.spool.pending ?? 0 }}</b></span><span>{{ t('写入失败') }} <b>{{ status?.spool.failed ?? 0 }}</b></span><span>{{ t('已丢弃') }} <b>{{ status?.spool.dropped ?? 0 }}</b></span>
      <span class="storage-path" :title="status?.storageDirectory">{{ status?.storageDirectory || t('存储位置待初始化') }}</span>
      <span v-if="storageMigrating" class="migration" data-testid="storage-migration-status">{{ t('目录迁移中') }} {{ migrationPercent }}%</span>
      <span v-if="cleanupRunning" class="migration" data-testid="cleanup-strip">{{ t('后台清理中：已删除 {files} 个文件', { files: cleanup?.deletedFiles ?? 0 }) }}</span>
    </div>
    <el-alert v-if="status?.migrationFailure" :title="status.migrationFailure" type="error" :closable="false" show-icon />
    <el-alert v-if="status?.historyError" data-testid="history-import-error" :title="status.historyError" type="warning" :closable="false" show-icon />
    <el-alert v-if="status?.databaseError && !status.historyError" :title="status.databaseError" type="warning" :closable="false" show-icon />
    <LogFilterBar :model-value="draft" :loading="loading" :options="filterOptions" @update:model-value="replaceDraft" @apply="applyFilters" @reset="resetFilters" />
    <div v-if="activeChips.length" class="active-filters"><span>{{ t('已应用') }}</span><button v-for="chip in activeChips" :key="chip.key" type="button" data-testid="active-filter-chip" @click="removeChip(chip.key)">{{ chip.label }}: {{ chip.value }} <i>×</i></button></div>
    <div v-if="loadError" class="query-warning" :class="{ stale: staleResults }" :data-testid="staleResults ? 'stale-results' : 'query-error'"><b>{{ staleResults ? t('当前显示上次成功结果') : t('日志查询失败') }}</b><span>{{ loadError }}</span><button type="button" @click="reload('manual')">{{ t('重试') }}</button></div>
    <div class="workbench-grid"><LogScopeRail :model-value="applied.category" :counts="scopeCounts" @update:model-value="selectScope" />
      <main class="event-workspace"><LogTrendPanel :buckets="histogram" @select="selectTrend" />
        <div class="result-meta"><span>{{ t('找到 {count} 条事件', { count: total }) }}</span><span>{{ t('查询耗时 {ms} ms', { ms: tookMs }) }}</span><span v-if="partialResult" class="outage" data-testid="partial-result">{{ partialResultText }}</span><span v-if="devices.length">{{ t('在线设备 {online}/{known}', { online: onlineDevices, known: knownDevices }) }}</span><span v-if="outages.length" class="outage">{{ t('时间范围内离线 {count} 次', { count: outages.length }) }}</span></div>
        <details v-if="applied.category === 'performance'" class="live-diagnostics" open><summary><span>{{ t('实时采集诊断') }}</span><router-link to="/performance" @click.stop>{{ t('打开完整采集性能页') }}</router-link></summary><ScanTimingPanel /></details>
        <LogTimeCorridor :items="rows" :loading="loading" @select="selectEntry" /><el-pagination data-testid="result-pagination" background layout="total, sizes, prev, pager, next" :total="total" :current-page="page" :page-size="pageSize" :page-sizes="[25,50,100,200]" @current-change="changePage" @size-change="changePageSize" />
      </main></div>
    <LogDetailDrawer :entry="selected" :highlight-path="applied.path" :payload="payload" :payload-loading="payloadLoading" @close="closeEntry" @load-payload="loadPayload" @download-payload="downloadPayload" />
    <el-dialog v-model="settingsOpen" :title="t('日志设置')" width="720px" append-to-body :close-on-click-modal="!savingSettings">
      <div class="settings-form">
        <section class="settings-section">
          <h3>{{ t('存储与保留') }}</h3>
          <small v-if="settingsOpen && historyImporting" data-testid="history-import-status">{{ t('旧日志在后台导入，不影响页面操作') }} · {{ status?.historyImportedFiles ?? 0 }}/{{ status?.historyTotalFiles ?? 0 }} {{ t('个文件') }}</small>
          <label>{{ t('日志保留') }}</label><div><el-input-number v-model="retentionDraft" :min="1" :max="3650" :disabled="!editable || storageMigrating" /> <span>{{ t('天') }}</span></div>
          <label>{{ t('日志存储位置') }}</label><div class="path-editor"><el-input v-model="storageDraft" :disabled="!editable || storageMigrating" /><el-button v-if="editable" :disabled="storageMigrating" data-testid="log-storage-browse" @click="directoryPickerOpen = true">{{ t('浏览文件夹') }}</el-button></div>
          <small>{{ t(questDb ? '选择网关电脑上的目录，QuestDB 数据库、旧日志和接口报文一起迁移；期间采集继续，日志先进入缓存，失败恢复原目录。' : '更换位置时会先复制并校验日志和接口报文，失败会继续使用原位置。') }}</small>
          <small v-if="questDb">{{ t('旧日志后台导入，源文件先保留；过期分段按保留期清理，其它旧文件可一键清理。导入未完成和仍在读取的旧报文会受到保护。') }}</small>
          <el-alert v-if="settingsError" :title="settingsError" type="error" :closable="false" />
        </section>
      </div>
      <template #footer><el-button :disabled="savingSettings" @click="settingsOpen = false">{{ t('取消') }}</el-button><el-button v-if="editable" type="primary" data-testid="log-settings-save" :loading="savingSettings" :disabled="storageMigrating" @click="saveSettings">{{ t('保存') }}</el-button></template></el-dialog>
    <el-dialog v-model="cleanupOpen" :title="t('一键清理日志残留')" width="620px" append-to-body data-testid="log-cleanup-dialog">
      <div class="cleanup-body">
        <p>{{ t('清理网关已不再写入的旧报文和旧日志，释放磁盘空间。清理在网关后台进行，不停服务，采集和接口照常运行；关闭本窗口也会继续。') }}</p>
        <el-alert v-if="cleanup?.blockedReason" :title="cleanup.blockedReason" type="warning" :closable="false" show-icon />
        <template v-if="cleanupRunning">
          <div class="export-progress" role="status">{{ t('正在后台清理：已删除 {files} 个文件，释放 {size}', { files: cleanup?.deletedFiles ?? 0, size: sizeText(cleanup?.deletedBytes ?? 0) }) }}</div>
        </template>
        <template v-else>
          <el-alert v-if="cleanup?.state === 'Completed'" type="success" :closable="false" show-icon
            :title="t('清理完成：删除 {files} 个文件，释放 {size}', { files: cleanup.deletedFiles, size: sizeText(cleanup.deletedBytes) })" />
          <el-alert v-if="cleanup?.state === 'Failed'" type="error" :closable="false" show-icon :title="t('清理中断：') + (cleanup.error || '')" />
          <el-alert v-for="item in cleanup?.skipped || []" :key="item" type="warning" :closable="false" :title="item" />
          <div v-if="cleanupScanning" class="export-progress" role="status" data-testid="cleanup-scanning">{{ t('正在查找各磁盘上网关以前留下的日志，稍等片刻…') }}</div>
          <div v-if="cleanup?.residues.length" class="cleanup-list">
            <div>{{ t('勾选要清理的项目：') }}</div>
            <el-checkbox-group v-model="cleanupChecked" class="cleanup-checks">
              <el-checkbox v-for="item in cleanup.residues" :key="item.path" :value="item.path" class="cleanup-item">
                <b>{{ t(item.kind) }}</b><code>{{ item.path }}</code>
                <small v-if="item.filePattern">{{ t('只删其中的 igw-日期.log 文件，文件夹里的其它东西不动') }}</small>
              </el-checkbox>
            </el-checkbox-group>
          </div>
          <el-empty v-else-if="cleanup && !cleanupScanning" :image-size="60" :description="t('没有需要清理的残留')" />
          <small>{{ t('只清理勾选的项目；当前日志、接口报文、工程配置和授权不会被删除。') }}</small>
        </template>
      </div>
      <template #footer><el-button @click="cleanupOpen = false">{{ t('关闭') }}</el-button><el-button v-if="editable" type="primary" data-testid="log-cleanup-start"
        :disabled="cleanupRunning || cleanupScanning || !!cleanup?.blockedReason || !cleanupChecked.length" :loading="cleanupStarting" @click="startCleanup">{{ t('开始清理') }}</el-button></template>
    </el-dialog>
    <el-dialog v-model="exportOpen" :title="t(questDb ? '导出 QuestDB 日志' : '打包导出日志')" width="560px" :close-on-click-modal="!exporting" :show-close="!exporting" data-testid="log-export-dialog">
      <div class="export-form">
        <small v-if="questDb" data-testid="export-data-source">{{ t('数据来源：当前 QuestDB 日志数据库，按下面的范围和当前筛选导出。') }}</small>
        <label>{{ t('日志格式') }}</label>
        <el-radio-group v-model="exportFormat" :disabled="exporting" data-testid="export-format">
          <el-radio-button value="jsonl">JSONL</el-radio-button><el-radio-button value="csv">CSV</el-radio-button><el-radio-button value="text">{{ t('可读文本') }}</el-radio-button>
        </el-radio-group>
        <small>{{ t('JSONL 保留完整日志及全部属性；CSV 可用表格软件打开；文本便于现场阅读。') }}</small>
        <label>{{ t('时间范围') }}</label>
        <el-radio-group v-model="exportRange" :disabled="exporting">
          <el-radio-button value="current">{{ t('当前筛选') }}</el-radio-button>
          <el-radio-button value="hour">{{ t('最近 1 小时') }}</el-radio-button>
          <el-radio-button value="today">{{ t('今天') }}</el-radio-button>
          <el-radio-button value="day">{{ t('最近 24 小时') }}</el-radio-button>
        </el-radio-group>
        <small>{{ t('分类、级别、设备、关键词等筛选条件与当前页面一致；单次最多 31 天、50 万条。') }}</small>
        <el-checkbox v-model="exportPayloads" :disabled="exporting">{{ t('附带接口请求/响应报文') }}</el-checkbox>
        <small>{{ t('压缩包包含所选格式日志、来源与筛选清单；接口报文放在 payloads 目录。') }}</small>
        <div v-if="exporting" class="export-progress" role="status">{{ t('正在生成导出包，已接收 {size}…', { size: exportLoadedText }) }}</div>
      </div>
      <template #footer><el-button @click="cancelExport">{{ t('取消') }}</el-button><el-button type="primary" :loading="exporting" data-testid="log-export-run" @click="runExport">{{ t('开始导出') }}</el-button></template>
    </el-dialog>
    <RuntimeLogDirectoryDialog v-if="editable" v-model="directoryPickerOpen" :initial-directory="storageDraft || status?.storageDirectory || ''" @select="storageDraft = $event" />
  </section>
</template>

<style scoped>
.log-workbench { max-width:1720px; margin:0 auto; color:#26364a; }.workbench-head { display:flex; align-items:flex-start; justify-content:space-between; gap:18px; margin-bottom:12px; }.eyebrow { color:#1a5fb4; font-size:10px; font-weight:800; letter-spacing:.16em; }.workbench-head h1 { margin:4px 0; font-size:23px; }.workbench-head p { margin:0; color:#6e7b8d; font-size:12px; }.head-actions { display:flex; gap:8px; }
.runtime-strip { display:flex; align-items:center; gap:20px; min-height:38px; margin-bottom:10px; padding:0 12px; border:1px solid #dfe6ef; border-radius:9px; background:#f8fafc; color:#68768a; font-size:11px; }.runtime-strip b { color:#34475e; }.runtime-state { color:#455a72; font-weight:700; }.runtime-state i { display:inline-block; width:7px; height:7px; margin-right:6px; border-radius:50%; background:#c6493d; }.runtime-strip.healthy .runtime-state i { background:#2f8a50; }.storage-path { min-width:0; overflow:hidden; margin-left:auto; color:#7c8999; font-family:Consolas,monospace; text-overflow:ellipsis; white-space:nowrap; }.migration { color:#1a5fb4; font-weight:700; }
.active-filters { display:flex; align-items:center; flex-wrap:wrap; gap:6px; min-height:30px; padding:5px 2px; color:#7a8798; font-size:11px; }.active-filters button { padding:4px 7px; border:1px solid #ccdae9; border-radius:14px; background:#f2f7fc; color:#3d5f83; font-size:11px; cursor:pointer; }.active-filters i { margin-left:4px; font-style:normal; }.query-warning { display:grid; grid-template-columns:auto 1fr auto; align-items:center; gap:12px; margin:8px 0; padding:9px 12px; border:1px solid #efc6c0; border-radius:8px; background:#fdf1ef; color:#8f3a31; font-size:12px; }.query-warning.stale { border-color:#e8d49c; background:#fff8e6; color:#795c12; }.query-warning button { border:0; background:transparent; color:inherit; font-weight:700; cursor:pointer; }
.workbench-grid { display:grid; grid-template-columns:178px minmax(0,1fr); gap:10px; margin-top:10px; }.event-workspace { display:flex; min-width:0; flex-direction:column; gap:9px; }.result-meta { display:flex; gap:18px; padding:0 4px; color:#7b8797; font-size:11px; }.result-meta .outage { color:#a24a3e; }.live-diagnostics { border:1px solid #dfe6ef; border-radius:10px; background:#fff; }.live-diagnostics summary { display:flex; align-items:center; justify-content:space-between; gap:12px; padding:10px 13px; color:#40556e; font-size:12px; font-weight:700; cursor:pointer; }.live-diagnostics summary a { color:#1b6fbd; font-weight:600; text-decoration:none; }.live-diagnostics summary a:hover { text-decoration:underline; }.live-diagnostics :deep(.timing-panel) { border-top:1px solid #edf1f5; }.event-workspace>.el-pagination { align-self:flex-end; padding:5px 0 14px; }
.settings-form { display:flex; flex-direction:column; gap:16px; color:#536176; }.settings-section { display:flex; flex-direction:column; gap:10px; padding:14px; border:1px solid #e3e9f1; border-radius:10px; background:#fbfcfe; }.settings-section h3 { margin:0 0 2px; color:#2e425a; font-size:14px; }.settings-form label { margin-top:5px; font-size:12px; font-weight:700; }.settings-form small { color:#7d8998; line-height:1.55; }.path-editor { display:flex; gap:8px; }
@media (max-width:1100px) { .workbench-grid { grid-template-columns:1fr; }.runtime-strip { gap:12px; flex-wrap:wrap; padding:8px 12px; }.storage-path { width:100%; margin-left:0; }.workbench-head { flex-direction:column; } }
@media (max-width:720px) { .head-actions { width:100%; }.result-meta { flex-wrap:wrap; }.query-warning { grid-template-columns:1fr; }}
.export-form { display:flex; flex-direction:column; gap:10px; color:#536176; }.export-form label { font-size:12px; font-weight:700; }.export-form small { color:#7d8998; line-height:1.55; }.export-progress { padding:8px 10px; border-radius:6px; background:#f2f7fc; color:#1a5fb4; font-size:12px; }
.cleanup-body { display:grid; gap:10px; }
.cleanup-body p { margin:0; color:#52606d; line-height:1.6; }
.cleanup-list { display:grid; gap:6px; }
.cleanup-checks { display:grid; gap:6px; }
.cleanup-item { height:auto; margin-right:0; align-items:flex-start; white-space:normal; }
.cleanup-item :deep(.el-checkbox__label) { display:grid; grid-template-columns: 150px 1fr; gap:2px 8px; line-height:1.5; }
.cleanup-item b { font-weight:600; }
.cleanup-item code { word-break: break-all; font-size:12px; color:#334155; }
.cleanup-item small { grid-column: 2; color:#8a94a0; font-size:12px; }
</style>
