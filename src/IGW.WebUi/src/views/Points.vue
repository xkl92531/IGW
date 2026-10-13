<script setup lang="ts">
import { ref, computed, h, onMounted, onUnmounted, nextTick, reactive, watch } from 'vue'
import { useRoute } from 'vue-router'
import { ElMessage } from 'element-plus'
import { api, type ProjectTagDefinitionDto, type ProjectTagGroupDto, type TagScanTimingDto } from '../api'
import { useI18n } from '../i18n'
import { QuickClientTiming, timingLabel, visibleTimingIds } from '../utils/quickClientTiming'
import { formatScanStartedAt } from '../utils/scanTimings'
import ArrayValueViewer from '../components/ArrayValueViewer.vue'
import type { ArrayDef } from '../utils/arrayTags'
import { RealtimeStreamClient } from '../utils/realtimeStream'
import { createRealtimeStore, type RealtimeConnectionState, type RealtimeTagValue } from '../stores/realtime'
import type { RealtimeFrame } from '../utils/realtimeFrame'
import { loadExactScope, loadFirstPopulatedScope } from '../utils/quickClientScope'
import { pointValueDisplay, pointTypeDisplay, pointTypeKey } from '../utils/pointValueDisplay'
import { parsePointWriteInput, pointWriteHint } from '../utils/siemensDataTypes'

/** Quick Client:工程定义按精确路径分页加载，点位与数组读写使用共享 SignalR 连接。 */

// Segoe MDL2 图标(与桌面工具栏同款,Windows 浏览器真渲染)
const G = { new: '', open: '', save: '', write: '', refresh: '' }

type Item = { itemId: string; description: string; address: string; dataType: string; rawType: string; writable: boolean; value: string; ts: string; quality: string; good: boolean; uc: number; array?: ArrayDef | null }
type Group = { name: string; items: Item[] }
type TreeNode = { key: string; label: string; path: string; kind: string; description?: string | null; server?: boolean; children: TreeNode[]; loaded?: boolean }

const allItems = new Map<string, Item>()
const curGroup = ref<Group | null>(null)
const itemCount = ref(0)
const log = ref<{ date: string; time: string; msg: string }[]>([])
const treeRef = ref<any>()
// q 可由父(工程配置 QC 弹窗)传入,也兼容旧 ?q= 直链
// editable=false(工程配置只能看)：只能看值、读取，不能写 PLC
const props = withDefaults(defineProps<{ q?: string; preferredScope?: string; suspended?: boolean; editable?: boolean }>(), { editable: true })
const q = computed(() => props.q !== undefined ? props.q : String(useRoute().query.q || ''))
const serverNode = reactive<TreeNode>({ key: '__srv', label: 'IGW Runtime', path: '', kind: 'Root', server: true, children: [] })
const treeData = ref<TreeNode[]>([serverNode])
const treeProps = { value: 'key', label: 'label', children: 'children' }
const expandedKeys = ref<string[]>(['__srv'])
const gridRows = computed(() => curGroup.value?.items || [])
const { t } = useI18n()
const timings = ref<Record<string, TagScanTimingDto>>({})
const timingFailed = ref(false)
const timingScopeLoading = ref(false)
const scopeLoading = ref(false)
const scopeQuery = ref('')
const scopeNextCursor = ref<string | undefined>()
const scopeCursorHistory = ref<(string | undefined)[]>([])
const scopeActiveCursor = ref<string | undefined>()
const scopePage = ref(1)
const writing = ref(false)
const writeStatus = ref('')
const pageHidden = ref(document.hidden)
const viewport = ref({ start: -1, end: -1 })
const timingViewportEl = ref<HTMLElement>()
const timingPoller = new QuickClientTiming(api.tagScanTimings, (items, failed) => {
  timings.value = Object.fromEntries(items.map(item => [item.tagId, item]))
  timingFailed.value = failed
})
function syncTimings() {
  timingPoller.set(visibleTimingIds(gridRows.value, viewport.value.start, viewport.value.end),
    !props.suspended && !pageHidden.value && !timingScopeLoading.value)
}
function onTimingRowsRendered(range: { rowVisibleStart: number; rowVisibleEnd: number }) {
  viewport.value = { start: range.rowVisibleStart, end: range.rowVisibleEnd }
}
function timingVisibilityChanged() { pageHidden.value = document.hidden }
watch([gridRows, viewport, () => props.suspended, pageHidden, timingScopeLoading], syncTimings, { flush: 'sync' })
onMounted(() => document.addEventListener('visibilitychange', timingVisibilityChanged))
onUnmounted(() => { timingPoller.dispose(); document.removeEventListener('visibilitychange', timingVisibilityChanged) })
function timingTooltip(item?: TagScanTimingDto) {
  const batch = item?.batch
  if (!batch) return item?.mode === 'subscription' ? t('订阅推送') : t('暂无周期批次记录')
  return [t('同批共享耗时，不是该实时值的独立读取耗时'),
    `${t('批次开始时间')}: ${formatScanStartedAt(batch.startedAt)}`,
    ...(item?.mode === 'standby' ? [t('历史批次，当前未执行轮询')] : []),
    `${t('驱动 / 本地')}: ${batch.driverMs.toFixed(2)} / ${batch.localMs.toFixed(2)} ms`,
    `${t('设定周期 / 实际开始间隔')}: ${batch.configuredPeriodMs} / ${batch.actualIntervalMs ?? '—'} ms`,
    `${t('批次点数')}: ${batch.tagCount}`].join('\n')
}
const streamState = ref<RealtimeConnectionState>('idle')
let projectRevision = ''
let realtimeStore = createRealtimeStore('')
let scopeController: AbortController | null = null
let currentScopeToken = 0
let activeScopeNode: TreeNode | null = null
let projectChangeTimer: ReturnType<typeof setTimeout> | undefined
let scopeSearchTimer: ReturnType<typeof setTimeout> | undefined

function addLog(msg: string) {
  const d = new Date()
  const pad = (n: number, w = 2) => String(n).padStart(w, '0')
  log.value.unshift({ date: `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`, time: `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`, msg })
  if (log.value.length > 200) log.value.pop()
}

function toItem(t: ProjectTagDefinitionDto): Item {
  return reactive({
    itemId: t.id,
    description: String(t.description || '').trim(),
    address: t.address,
    dataType: t.array ? `${pointTypeDisplay(t.array.elementType ?? t.dataType)}[${(t.array.dimensions || []).join('×')}]` : pointTypeDisplay(t.dataType),
    rawType: pointTypeKey(t.dataType),
    writable: ['write', 'readwrite'].includes(String(t.access).toLowerCase()),
    value: t.array ? '数组切片…' : '—', ts: '', quality: '', good: false, uc: 0,
    array: t.array || null,
  })
}

function childNode(group: ProjectTagGroupDto): TreeNode {
  return reactive({ key: group.path, label: group.path, path: group.path, kind: group.kind, description: group.description, children: [] })
}
function treeNodeName(data: TreeNode): string {
  if (data.server) return data.label
  const segments = data.label.split('.').filter(Boolean)
  return segments[segments.length - 1] || data.label
}
function onNodeExpand(data: TreeNode) {
  if (data?.key && !expandedKeys.value.includes(data.key))
    expandedKeys.value = [...expandedKeys.value, data.key]
}
function onNodeCollapse(data: TreeNode) {
  if (!data?.key) return
  const collapsed = new Set<string>()
  const collect = (node: TreeNode) => {
    collapsed.add(node.key)
    node.children.forEach(collect)
  }
  collect(data)
  expandedKeys.value = expandedKeys.value.filter(key => !collapsed.has(key))
}
function qual(v: any): string {
  if (v.good) return '良好'
  const symbolic = String(v.quality || '').trim()
  const uncertain = symbolic.toLowerCase().includes('uncert')
  const label = uncertain ? '不确定' : '差'
  const status = symbolic && !['bad', 'uncertain'].includes(symbolic.toLowerCase()) ? ` · ${symbolic}` : ''
  const numeric = Number(v.qualityCode)
  const code = Number.isFinite(numeric) && numeric > 0
    ? ` (0x${(numeric >>> 0).toString(16).toUpperCase().padStart(8, '0')})`
    : ''
  return `${label}${status}${code}`
}
function fmtTs(s: any): string {
  if (!s) return ''
  const d = new Date(s); const p = (n: number, w = 2) => String(n).padStart(w, '0')
  return `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}.${p(d.getMilliseconds(), 3)}`
}
function applyVal(it: Item, v: any) {
  const s = v.value == null ? '—' : String(v.value)
  const qq = qual(v)
  if (it.uc !== 0 && s === it.value && qq === it.quality) {
    // A repeated PLC value can still carry a newer measurement timestamp.
    if (v.sourceTs) it.ts = fmtTs(v.sourceTs)
    return   // Update Count counts value/quality changes, not repeated measurements.
  }
  it.value = s; it.quality = qq; it.good = !!v.good; it.ts = fmtTs(v.sourceTs); it.uc = it.uc === 0 ? 1 : it.uc + 1
}

const pendingUiValues = new Map<string, RealtimeTagValue>()
const UI_VALUES_PER_FRAME = 400
let pendingFullSync = false
let fullSyncValues: RealtimeTagValue[] = []
let fullSyncCursor = 0
let uiFrameHandle = 0
function flushUiValues() {
  uiFrameHandle = 0
  let remaining = UI_VALUES_PER_FRAME
  // Apply live changes first; reserve part of the frame so a snapshot also makes progress.
  const deltaBudget = pendingFullSync ? UI_VALUES_PER_FRAME - 100 : UI_VALUES_PER_FRAME
  let deltasApplied = 0
  for (const [id, value] of pendingUiValues) {
    if (deltasApplied >= deltaBudget) break
    pendingUiValues.delete(id)
    const item = allItems.get(id)
    if (item) applyVal(item, value)
    deltasApplied++
    remaining--
  }
  if (pendingFullSync) {
    while (fullSyncCursor < fullSyncValues.length && remaining > 0) {
      const value = fullSyncValues[fullSyncCursor++]
      // A delta may already have been rendered and removed from the pending map.
      // Use the store's latest sample so a later snapshot slice cannot restore old data.
      const item = allItems.get(value.id)
      if (item && !pendingUiValues.has(value.id)) applyVal(item, realtimeStore.values.get(value.id) ?? value)
      remaining--
    }
    if (fullSyncCursor === fullSyncValues.length) {
      pendingFullSync = false
      fullSyncValues = []
      fullSyncCursor = 0
    }
  }
  if (pendingFullSync || pendingUiValues.size)
    uiFrameHandle = window.requestAnimationFrame(flushUiValues)
}
function queueUiValues(values: RealtimeTagValue[] = [], fullSync = false) {
  if (fullSync) {
    pendingUiValues.clear()
    pendingFullSync = true
    fullSyncValues = [...realtimeStore.values.values()]
    fullSyncCursor = 0
  }
  for (const value of values) pendingUiValues.set(value.id, value)
  if (!uiFrameHandle) uiFrameHandle = window.requestAnimationFrame(flushUiValues)
}
function cancelUiFlush() {
  if (uiFrameHandle) window.cancelAnimationFrame(uiFrameHandle)
  uiFrameHandle = 0
  pendingFullSync = false
  fullSyncValues = []
  fullSyncCursor = 0
  pendingUiValues.clear()
}
function onRealtimeFrame(frame: RealtimeFrame) {
  const applied = realtimeStore.apply(frame)
  if (applied.snapshotCompleted) queueUiValues([], true)
  else if (applied.valuesChanged && realtimeStore.status === 'live') queueUiValues(applied.values)
  streamState.value = realtimeStore.status
}
const realtimeClient = new RealtimeStreamClient({
  onFrame: onRealtimeFrame,
  onState: state => {
    streamState.value = state
    realtimeStore.setConnectionState(state)
    if (state === 'project-changed') scheduleProjectGenerationReload()
  },
  onError: error => addLog(`实时流连接失败: ${error instanceof Error ? error.message : String(error)}`),
})
function scheduleProjectGenerationReload() {
  clearTimeout(projectChangeTimer)
  if (props.suspended || !activeScopeNode) return
  projectChangeTimer = setTimeout(() => {
    projectChangeTimer = undefined
    const node = activeScopeNode
    if (!node) return
    addLog('工程点表已变化，正在重新加载当前范围并恢复实时流')
    void loadScope(node.path, node)
  }, 1_000)
}
function startRealtime() {
  realtimeClient.stop()
  cancelUiFlush()
  if (props.suspended || !projectRevision) return
  const tagIds = (curGroup.value?.items || []).filter(item => !item.array).slice(0, 10_000).map(item => item.itemId)
  if (!tagIds.length) return
  realtimeStore = createRealtimeStore(projectRevision)
  realtimeClient.start({
    protocolVersion: 1,
    clientId: `quick-client-${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`,
    projectRevision,
    tagIds,
    topics: [],
    includeSnapshot: true,
  })
}
function refreshSnapshot() {
  if (streamState.value === 'idle') startRealtime()
  else realtimeClient.requestSnapshot()
  addLog('已请求实时快照')
}

// ---------- 从 PLC 读取(点击刷新 / 定时刷新) ----------
// QC 窗口只接收已经采集到的变化，打开窗口不会让范围内的点持续轮询；
// 需要 PLC 当前值时由这里显式读取一次。数组点在下方切片区单独读取。
const READ_CHUNK = 2_000
// 定时刷新周期直接填毫秒：0=不刷新；太小会把 PLC 读满，因此下限 50ms，上限 1 小时。
const AUTO_REFRESH_MIN_MS = 50
const AUTO_REFRESH_MAX_MS = 3_600_000
const AUTO_REFRESH_KEY = 'igw.qc.autoRefreshMs'
const LEGACY_AUTO_REFRESH_KEY = 'igw.qc.autoRefreshSeconds'
const refreshing = ref(false)
// 只有手动刷新才显示“正在读取”和禁用按钮；定时刷新每秒都跑，切换提示会让工具栏闪烁
const manualRefreshing = ref(false)
const autoRefreshMs = ref(loadAutoRefreshMs())
const autoRefreshInput = ref(String(autoRefreshMs.value))
let autoRefreshTimer: ReturnType<typeof setTimeout> | undefined

function normalizeAutoRefreshMs(value: unknown): number {
  const ms = Math.round(Number(value))
  if (!Number.isFinite(ms) || ms <= 0) return 0
  return Math.min(AUTO_REFRESH_MAX_MS, Math.max(AUTO_REFRESH_MIN_MS, ms))
}

function loadAutoRefreshMs(): number {
  try {
    const saved = localStorage.getItem(AUTO_REFRESH_KEY)
    if (saved !== null) return normalizeAutoRefreshMs(saved)
    // 旧版本存的是秒，升级后按毫秒继续生效。
    const legacy = Number(localStorage.getItem(LEGACY_AUTO_REFRESH_KEY))
    return Number.isFinite(legacy) && legacy > 0 ? normalizeAutoRefreshMs(legacy * 1000) : 0
  } catch { return 0 }
}

function applyAutoRefreshInput(raw: string) {
  const ms = normalizeAutoRefreshMs(raw)
  autoRefreshMs.value = ms
  autoRefreshInput.value = String(ms)
}

async function refreshFromPlc(ids?: string[], silent = false) {
  if (refreshing.value) return
  const targets = ids ?? (curGroup.value?.items || []).filter(item => !item.array).map(item => item.itemId)
  if (!targets.length) {
    if (!silent) ElMessage.info(t('当前范围没有可从 PLC 读取的点位'))
    return
  }
  refreshing.value = true
  manualRefreshing.value = !silent
  const started = performance.now()
  let good = 0
  const errors = new Set<string>()
  try {
    for (let offset = 0; offset < targets.length; offset += READ_CHUNK) {
      const result = await api.readValues(targets.slice(offset, offset + READ_CHUNK))
      good += result.goodCount
      if (result.error) errors.add(result.error)
      for (const value of result.values) {
        const item = allItems.get(value.id)
        if (item) applyVal(item, value)
      }
    }
    const elapsed = Math.round(performance.now() - started)
    if (!silent || errors.size)
      addLog(t('已从 PLC 读取 {count} 点（有效 {good}），耗时 {ms} ms', { count: targets.length, good, ms: elapsed })
        + (errors.size ? `；${[...errors].join('；')}` : ''))
  } catch (e: any) {
    const message = e?.response?.data?.error || e?.message || ''
    addLog(t('从 PLC 读取失败：{message}', { message }))
    if (!silent) ElMessage.error(t('从 PLC 读取失败：{message}', { message }))
  } finally {
    refreshing.value = false
    manualRefreshing.value = false
  }
}

function refreshNow() {
  if (streamState.value === 'idle') startRealtime()
  void refreshFromPlc()
}

function readRow(row: Item) {
  if (row.array) { ElMessage.info(t('数组点请在下方切片区读取')); return }
  void refreshFromPlc([row.itemId])
}

function scheduleAutoRefresh() {
  clearTimeout(autoRefreshTimer)
  autoRefreshTimer = undefined
  if (!autoRefreshMs.value || props.suspended || pageHidden.value) return
  autoRefreshTimer = setTimeout(async () => {
    autoRefreshTimer = undefined
    // 上一次读取结束后才开始计下一个周期，PLC 慢时不会堆积请求。
    await refreshFromPlc(undefined, true)
    scheduleAutoRefresh()
  }, autoRefreshMs.value)
}
watch(autoRefreshMs, ms => {
  autoRefreshInput.value = String(ms)
  try { localStorage.setItem(AUTO_REFRESH_KEY, String(ms)) } catch {}
})
watch([autoRefreshMs, () => props.suspended, pageHidden], scheduleAutoRefresh, { immediate: true })
onUnmounted(() => { clearTimeout(autoRefreshTimer); autoRefreshTimer = undefined })

async function loadScope(path: string, node: TreeNode, requestedCursor?: string, requestedPage = 1, initialization?: number): Promise<number> {
  // 手动选组、搜索和翻页使旧的初始化失效；迟到的恢复请求不能清掉新选择。
  if (initialization === undefined) ++scopeInitializationGeneration
  else if (initialization !== scopeInitializationGeneration) return -1
  timingScopeLoading.value = true
  scopeLoading.value = true
  clearTimeout(projectChangeTimer)
  projectChangeTimer = undefined
  activeScopeNode = node
  const token = ++currentScopeToken
  scopeController?.abort(); scopeController = new AbortController()
  realtimeClient.stop(); cancelUiFlush(); curRow.value = null; allItems.clear()
  curGroup.value = { name: path || 'IGW Runtime', items: [] }
  const controller = scopeController
  let cursor: string | undefined
  let firstPage = true
  let streamedCount = 0
  const query = scopeQuery.value.trim()
  try {
    do {
      const page = await api.projectTagScope({
        root: path || undefined,
        q: query || undefined,
        cursor: firstPage ? requestedCursor : cursor,
        limit: 1_000,
      }, controller.signal)
      if (token !== currentScopeToken || controller.signal.aborted) return -1
      projectRevision = page.realtimeRevision
      if (firstPage) {
        node.children.splice(0, node.children.length, ...page.groups.map(childNode))
        node.loaded = true
        treeData.value = [...treeData.value]
        itemCount.value = page.total
        scopeActiveCursor.value = requestedCursor
        scopePage.value = requestedPage
      }
      const items = curGroup.value!.items
      const next = page.definitions.map(toItem)
      next.forEach(item => allItems.set(item.itemId, item))
      items.push(...next)
      // 小范围继续渐进加载，保持原有体验；大范围和搜索结果严格一页 1000 点，避免浏览器订阅过多信号。
      const eagerSmallScope = !query && !requestedCursor && page.total <= 10_000
      cursor = eagerSmallScope ? page.nextCursor || undefined : undefined
      scopeNextCursor.value = eagerSmallScope ? undefined : page.nextCursor || undefined
      await nextTick()
      if (token !== currentScopeToken || controller.signal.aborted) return -1
      if (firstPage) {
        const visibleRows = Math.ceil(Math.max(0, (timingViewportEl.value?.clientHeight ?? 0) - 32) / 30)
        viewport.value = visibleRows ? { start: 0, end: Math.min(items.length, visibleRows) - 1 } : { start: -1, end: -1 }
        timingScopeLoading.value = false
        startRealtime()
        streamedCount = items.length
      }
      firstPage = false
    } while (cursor)
    const items = curGroup.value!.items
    addLog(`已加载范围 '${path || 'IGW Runtime'}' (${items.length} 项)`)
    // Reconfigure once after the background pages finish, not once per page.
    if (items.length !== streamedCount) startRealtime()
    // 打开范围先从 PLC 拉一次当前值：实时流是被动的(只送已采集到的值)，
    // 范围内没在采集的点否则会一直显示为空。之后要不要持续更新由“定时刷新”决定。
    void refreshFromPlc(undefined, true)
    return items.length
  } catch (e: any) {
    if (token !== currentScopeToken || controller.signal.aborted) return -1
    timingScopeLoading.value = false
    if (curGroup.value?.items.length) startRealtime()
    if (e?.name !== 'CanceledError' && e?.name !== 'AbortError') ElMessage.error('载入监控范围失败:' + (e?.response?.data?.error || e?.message || ''))
    return -1
  } finally {
    if (token === currentScopeToken) { scopeLoading.value = false; timingScopeLoading.value = false }
  }
}
async function openScope(path: string, node: TreeNode, initialization?: number): Promise<number> {
  clearTimeout(scopeSearchTimer)
  scopeQuery.value = ''
  scopeCursorHistory.value = []
  scopeActiveCursor.value = undefined
  scopeNextCursor.value = undefined
  scopePage.value = 1
  return loadScope(path, node, undefined, 1, initialization)
}
function searchScope(value: string) {
  scopeQuery.value = value
  clearTimeout(scopeSearchTimer)
  scopeSearchTimer = setTimeout(() => {
    const node = activeScopeNode
    if (!node) return
    scopeCursorHistory.value = []
    scopeActiveCursor.value = undefined
    scopeNextCursor.value = undefined
    scopePage.value = 1
    void loadScope(node.path, node)
  }, 250)
}
function nextScopePage() {
  const node = activeScopeNode, next = scopeNextCursor.value
  if (!node || !next || scopeLoading.value) return
  scopeCursorHistory.value = [...scopeCursorHistory.value, scopeActiveCursor.value]
  void loadScope(node.path, node, next, scopePage.value + 1)
}
function previousScopePage() {
  const node = activeScopeNode
  if (!node || !scopeCursorHistory.value.length || scopeLoading.value) return
  const history = [...scopeCursorHistory.value]
  const previous = history.pop()
  scopeCursorHistory.value = history
  void loadScope(node.path, node, previous, Math.max(1, scopePage.value - 1))
}
function pickGroup(data: TreeNode) {
  if (data.server) return
  treeRef.value?.setCurrentKey(data.key)
  void openScope(data.path, data)
}
let scopeInitializationGeneration = 0
async function initializeScope() {
  const generation = ++scopeInitializationGeneration
  const path = q.value.trim()
  let node: TreeNode
  if (path) {
    node = reactive<TreeNode>({ key: path, label: path, path, kind: 'Group', children: [] })
    serverNode.label = '范围 · ' + path
    serverNode.children.splice(0, serverNode.children.length, node)
  } else {
    serverNode.label = 'IGW Runtime'
    node = serverNode
  }
  treeData.value = [...treeData.value]
  // 只沿范围树按需查找第一个有直属点的组，保留百万点分页能力；不再下载完整工程。
  // 这也兼容“当前组自身无点、实际点都在多层下级组”的现场结构。
  const preferred = String(props.preferredScope || '').trim()
  const open = (candidate: TreeNode) => generation === scopeInitializationGeneration
    ? openScope(candidate.path, candidate, generation) : Promise.resolve(-1)
  const selected = preferred
    ? await loadExactScope(node, open, preferred)
    : await loadFirstPopulatedScope(node, open)
  if (generation !== scopeInitializationGeneration) return
  if (preferred && !selected) {
    realtimeClient.stop(); cancelUiFlush(); allItems.clear()
    curGroup.value = null; itemCount.value = 0; activeScopeNode = null
    ElMessage.error(t('监控范围已变化，请重新选择要监控的标记组'))
  }
  await nextTick()
  if (selected) treeRef.value?.setCurrentKey(selected.key)
}

onMounted(initializeScope)
onUnmounted(() => { ++scopeInitializationGeneration; ++currentScopeToken; clearTimeout(projectChangeTimer); clearTimeout(scopeSearchTimer); scopeController?.abort(); realtimeClient.stop(); cancelUiFlush() })
watch(() => props.suspended, suspended => suspended ? realtimeClient.stop() : startRealtime())
watch([() => props.q, () => props.preferredScope], initializeScope)
defineExpose({ getActiveScopePath: () => activeScopeNode?.path ?? q.value })

// ---------- 选中行 / 写入 ----------
const curRow = ref<Item | null>(null)
const wOpen = ref(false); const wItem = ref<Item | null>(null); const wVal = ref('')
function openWrite(it: Item | null) {
  if (writing.value || !props.editable) return
  if (!it) { ElMessage.info('先选中一个项'); return }
  if (!it.writable) { ElMessage.info(`${it.itemId} 为只读`); return }
  if (it.array) { curRow.value = it; ElMessage.info('请在下方数组切片中选择元素或当前切片写入'); return }
  wItem.value = it; wVal.value = it.value === '—' ? '' : it.value; wOpen.value = true
}
async function doWrite() {
  if (writing.value || !props.editable) return
  const it = wItem.value; if (!it) return
  const parsed = parsePointWriteInput(wVal.value, it.rawType)
  if (parsed.error) { ElMessage.warning(parsed.error); return }
  const v = parsed.value
  const submittedText = wVal.value
  writing.value = true
  writeStatus.value = '正在写入 PLC…'
  try {
    const response = await api.write(it.itemId, v)
    // The write endpoint returns only after the device loop finishes verification.
    // Realtime values are coalesced display state, not receipts for individual writes:
    // unchanged values and short PLC pulses may never reach a rendered frame.
    // Keep the table driven by actual samples; never copy the requested value into it.
    if (response?.statusCode === 0x00b80000 && response?.quality === 'GoodUnverified') {
      wOpen.value = false
      addLog(`写入仅获协议确认 ${it.itemId}（请求值：${submittedText}），未回读验证 PLC 实际值`)
      ElMessage.warning(t('协议已接受写入，未回读验证 PLC 实际值'))
      return
    }
    if (response?.statusCode !== 0 || response?.quality !== 'Good')
      throw new Error(t('写入响应未包含有效的确认结果，请检查运行时日志'))
    wOpen.value = false
    addLog(`写入已通过回读验证 ${it.itemId}（请求值：${submittedText}）`)
    ElMessage.success('写入并回读确认成功')
  }
  catch (e: any) {
    addLog(`写入失败 ${it.itemId}: ${e?.message || ''}`); ElMessage.error('写入失败:' + (e?.response?.data?.error || e.message))
  }
  finally { writing.value = false; writeStatus.value = '' }
}

// ---------- 组:导出 CSV / 复制 ----------
function exportCsv(g: Group | null) {
  if (!g) return
  const head = '项ID,数据类型,值,Quality,UpdateCount'
  const lines = g.items.map((i) => `${i.itemId},${i.dataType},${i.value},${i.quality},${i.uc}`)
  const blob = new Blob(['﻿' + [head, ...lines].join('\r\n')], { type: 'text/csv' })
  const a = document.createElement('a'); a.href = URL.createObjectURL(blob); a.download = g.name.replace(/[^\w.-]/g, '_') + '.csv'; a.click(); URL.revokeObjectURL(a.href)
  addLog(`已导出 CSV:${g.name} (${g.items.length} 项)`)
}
async function copyText(t: string, note: string) { try { await navigator.clipboard.writeText(t); addLog(note) } catch {} }

// ---------- 浮动右键菜单 ----------
const menu = ref<{ show: boolean; x: number; y: number; items: any[] }>({ show: false, x: 0, y: 0, items: [] })
function hideMenu() { menu.value.show = false }
function showMenu(e: MouseEvent, items: any[]) { e.preventDefault(); menu.value = { show: true, x: Math.min(e.clientX, innerWidth - 200), y: Math.min(e.clientY, innerHeight - items.length * 30 - 8), items } }
const SEP = { sep: true }
function onItemCtx(row: Item, _c: any, e: MouseEvent) {
  curRow.value = row
  showMenu(e, [
    { label: t('从 PLC 读取此点'), act: () => readRow(row) }, ...(props.editable ? [{ label: t('同步写入…'), act: () => openWrite(row) }] : []), SEP,
    { label: '复制项 ID', gesture: 'Ctrl+C', act: () => copyText(row.itemId, `已复制 ${row.itemId}`) }, SEP,
    { label: '属性…', act: () => ElMessage({ message: `项ID:${row.itemId}\n数据类型:${row.dataType}\n值:${row.value}\nQuality:${row.quality}\nUpdate Count:${row.uc}\n可写:${row.writable ? '是' : '否'}`, type: 'info', customClass: 'preline' }) },
  ])
}
function onTreeCtx(e: MouseEvent, data: any) {
  if (!data.path) return
  treeRef.value?.setCurrentKey(data.key)
  showMenu(e, [
    { label: '打开并请求快照', act: () => openScope(data.path, data) }, SEP,
    { label: '导出当前 CSV…', act: () => exportCsv(curGroup.value) },
    { label: '复制当前项 ID', act: () => copyText((curGroup.value?.items || []).map((i: Item) => i.itemId).join('\n'), `已复制 ${curGroup.value?.items.length || 0} 个项 ID`) }, SEP,
    { label: '属性…', act: () => ElMessage.info(`路径:${data.path}${data.description ? '\n说明:' + data.description : ''}`) },
  ])
}
function clickMenu(it: any) { if (it.sep || it.disabled) return; menu.value.show = false; it.act?.() }
onMounted(() => window.addEventListener('click', hideMenu))
onUnmounted(() => window.removeEventListener('click', hideMenu))

type ColumnKey = 'itemId' | 'description' | 'address' | 'dataType' | 'value' | 'ts' | 'quality' | 'uc'
const COLUMN_WIDTH_KEY = 'igw.quick-client.column-widths'
const defaultColumnWidths: Record<ColumnKey, number> = {
  itemId: 360, description: 220, address: 340, dataType: 110,
  value: 150, ts: 130, quality: 240, uc: 120,
}
const minimumColumnWidths: Record<ColumnKey, number> = {
  itemId: 180, description: 130, address: 180, dataType: 90,
  value: 90, ts: 110, quality: 150, uc: 105,
}
function initialColumnWidths(): Record<ColumnKey, number> {
  let saved: Partial<Record<ColumnKey, number>> = {}
  try { saved = JSON.parse(localStorage.getItem(COLUMN_WIDTH_KEY) || '{}') }
  catch { /* 损坏的本机偏好使用默认值 */ }
  return Object.fromEntries((Object.keys(defaultColumnWidths) as ColumnKey[]).map(key => {
    const value = Number(saved[key])
    return [key, Number.isFinite(value) ? Math.max(minimumColumnWidths[key], Math.min(900, value)) : defaultColumnWidths[key]]
  })) as Record<ColumnKey, number>
}
const columnWidths = reactive(initialColumnWidths())
let columnResize: { key: ColumnKey; startX: number; startWidth: number } | null = null
function beginColumnResize(key: ColumnKey, event: MouseEvent) {
  event.preventDefault()
  event.stopPropagation()
  columnResize = { key, startX: event.clientX, startWidth: columnWidths[key] }
  window.addEventListener('mousemove', onColumnResize)
  window.addEventListener('mouseup', endColumnResize, { once: true })
  window.addEventListener('blur', endColumnResize, { once: true })
}
function onColumnResize(event: MouseEvent) {
  if (!columnResize) return
  const width = columnResize.startWidth + event.clientX - columnResize.startX
  columnWidths[columnResize.key] = Math.max(minimumColumnWidths[columnResize.key], Math.min(900, width))
}
function endColumnResize() {
  if (!columnResize) return
  try { localStorage.setItem(COLUMN_WIDTH_KEY, JSON.stringify(columnWidths)) }
  catch { /* 私密模式可能拒绝持久化，不影响当前拖拽 */ }
  columnResize = null
  window.removeEventListener('mousemove', onColumnResize)
  window.removeEventListener('mouseup', endColumnResize)
  window.removeEventListener('blur', endColumnResize)
}
function columnHeader(key: ColumnKey, title: string) {
  return h('div', { class: 'qc-column-header', title }, [
    h('span', { class: 'qc-column-title' }, title),
    h('span', {
      class: 'qc-column-resizer',
      'data-column-resizer': key,
      title: `拖动调整“${title}”列宽`,
      onMousedown: (event: MouseEvent) => beginColumnResize(key, event),
    }),
  ])
}
onUnmounted(() => {
  columnResize = null
  window.removeEventListener('mousemove', onColumnResize)
  window.removeEventListener('mouseup', endColumnResize)
  window.removeEventListener('blur', endColumnResize)
})

const columns = computed(() => {
  // Table-v2 caches cell renderers. Change the column snapshot when timing arrives,
  // even if the PLC value itself has not changed and no scroll event occurs.
  const timingSnapshot = timings.value
  return [
  { key: 'itemId', dataKey: 'itemId', title: '项 ID', width: columnWidths.itemId, minWidth: minimumColumnWidths.itemId, headerCellRenderer: () => columnHeader('itemId', '项 ID') },
  { key: 'description', dataKey: 'description', title: '字段说明', width: columnWidths.description, minWidth: minimumColumnWidths.description, headerCellRenderer: () => columnHeader('description', '字段说明'), cellRenderer: ({ rowData }: any) => h('span', { title: rowData.description || '暂无字段说明', class: 'qc-cell-ellipsis' }, rowData.description || '—') },
  { key: 'address', dataKey: 'address', title: '实际地址 / NodeId', width: columnWidths.address, minWidth: minimumColumnWidths.address, headerCellRenderer: () => columnHeader('address', '实际地址 / NodeId'), cellRenderer: ({ rowData }: any) => h('span', { title: rowData.address, class: 'qc-cell-ellipsis' }, rowData.address) },
  { key: 'dataType', dataKey: 'dataType', title: '数据类型', width: columnWidths.dataType, minWidth: minimumColumnWidths.dataType, headerCellRenderer: () => columnHeader('dataType', '数据类型') },
  { key: 'value', dataKey: 'value', title: '值', width: columnWidths.value, minWidth: minimumColumnWidths.value, headerCellRenderer: () => columnHeader('value', '值'), cellRenderer: ({ rowData }: any) => h('b', { style: { color: rowData.good ? '#111' : '#C0392B' } }, pointValueDisplay(rowData.value, rowData.rawType)) },
  { key: 'scanTiming', title: t('最近批次耗时'), width: 145,
    cellRenderer: ({ rowData }: any) => h('span', { title: timingTooltip(timingSnapshot[rowData.itemId]) }, t(timingLabel(timingSnapshot[rowData.itemId]))) },
  { key: 'ts', dataKey: 'ts', title: 'Timestamp', width: columnWidths.ts, minWidth: minimumColumnWidths.ts, headerCellRenderer: () => columnHeader('ts', 'Timestamp') },
  { key: 'quality', dataKey: 'quality', title: 'Quality / 原因', width: columnWidths.quality, minWidth: minimumColumnWidths.quality, headerCellRenderer: () => columnHeader('quality', 'Quality / 原因'), cellRenderer: ({ rowData }: any) => h('span', { title: rowData.quality, style: { color: rowData.good ? '#1B7F37' : rowData.quality.startsWith('不确定') ? '#B8860B' : '#C0392B' } }, rowData.quality || '—') },
  { key: 'uc', dataKey: 'uc', title: 'Update Count', width: columnWidths.uc, minWidth: minimumColumnWidths.uc, headerCellRenderer: () => columnHeader('uc', 'Update Count'), align: 'right' },
] })
const rowEventHandlers = {
  onClick: ({ rowData }: any) => { curRow.value = rowData },
  onDblclick: ({ rowData }: any) => openWrite(rowData),
  onContextmenu: ({ rowData, event }: any) => onItemCtx(rowData, null, event as MouseEvent),
}
</script>

<template>
  <div class="qc" @click="hideMenu">
    <!-- 工具栏 -->
    <div class="card qbar">
      <button class="tb" disabled title="新建"><span class="mdl">{{ G.new }}</span></button>
      <button class="tb" disabled title="打开"><span class="mdl">{{ G.open }}</span></button>
      <button class="tb" disabled title="保存"><span class="mdl">{{ G.save }}</span></button>
      <span class="sep" />
      <button v-if="editable" class="tb" title="写入选中项" @click="openWrite(curRow)"><span class="mdl">{{ G.write }}</span></button>
      <button class="tb" data-test="qc-refresh" :disabled="manualRefreshing" :title="t('从 PLC 读取当前范围')" @click="refreshNow"><span class="mdl">{{ G.refresh }}</span></button>
      <label class="auto-refresh" :title="t('填写刷新周期(毫秒)，0 表示不刷新；最小 50 毫秒。上一次读取完成后才开始计时。')">
        {{ t('定时刷新') }}
        <input data-test="qc-auto-refresh" type="number" min="0" :max="AUTO_REFRESH_MAX_MS" step="100"
          :value="autoRefreshInput" :placeholder="t('0=不刷新')"
          @change="applyAutoRefreshInput(($event.target as HTMLInputElement).value)" />
        <span class="unit">ms</span>
        <span v-if="!autoRefreshMs" class="unit">{{ t('不刷新') }}</span>
      </label>
      <span class="sep" />
      <span class="stream-state" :class="streamState" :title="t('表格显示已采集到的值；点击刷新或开启定时刷新从 PLC 读取')">实时流: {{ streamState }}</span>
      <input data-test="qc-scope-search" class="scope-search" :value="scopeQuery"
        placeholder="搜索当前范围点名、地址或说明" @input="searchScope(($event.target as HTMLInputElement).value)" />
      <button class="page-button" :disabled="scopeLoading || !scopeCursorHistory.length" @click="previousScopePage">上一页</button>
      <span class="page-label">第 {{ scopePage }} 页</span>
      <button class="page-button" :disabled="scopeLoading || !scopeNextCursor" @click="nextScopePage">下一页</button>
      <span class="grow" />
      <span v-if="manualRefreshing" class="qstatus" role="status">{{ t('正在从 PLC 读取…') }}</span>
      <span v-if="scopeLoading" class="qstatus" role="status">{{ t('正在加载点位，已读取 {count} 项…', { count: gridRows.length }) }}</span>
      <span v-if="timingFailed" class="qstatus" role="status">{{ t('批次耗时暂不可用') }}</span>
      <span class="cnt">匹配 {{ itemCount }} 点 · 当前 {{ gridRows.length }} 点</span>
    </div>

    <div class="qmain">
      <!-- 左:服务器 → 组树 -->
      <div class="card qtree">
        <el-auto-resizer>
          <template #default="{ height, width }">
            <el-tree-v2 ref="treeRef" :data="treeData" :props="treeProps" :height="height" :width="width" :item-size="44"
                        node-key="key" highlight-current :expand-on-click-node="false"
                        :default-expanded-keys="expandedKeys" @node-click="pickGroup" @node-contextmenu="onTreeCtx"
                        @node-expand="onNodeExpand" @node-collapse="onNodeCollapse">
              <template #default="{ data }">
                <span class="tn" :class="{ server: data.server }" :title="[data.path || data.label, data.description].filter(Boolean).join('\n')">
                  <span class="ic">{{ data.server ? '🖧' : '📁' }}</span>
                  <span class="tn-copy">
                    <span class="tn-main"><strong>{{ treeNodeName(data) }}</strong><small v-if="data.description">{{ data.description }}</small></span>
                    <span v-if="!data.server" class="tn-path">{{ data.path }}</span>
                  </span>
                </span>
              </template>
            </el-tree-v2>
          </template>
        </el-auto-resizer>
      </div>

      <!-- 右:监控项网格 -->
      <div class="card qgrid" v-loading="scopeLoading && !gridRows.length" :element-loading-text="t('正在加载工程点位…')" :aria-busy="scopeLoading">
        <div ref="timingViewportEl" class="virtual-grid" :class="{ sliced: !!curRow?.array }">
          <el-auto-resizer>
            <template #default="{ height, width }">
              <el-table-v2 :key="curGroup?.name" :columns="columns" :data="gridRows" :width="width" :height="height"
                :row-height="30" :header-height="32" :row-event-handlers="rowEventHandlers" :on-rows-rendered="onTimingRowsRendered" fixed />
            </template>
          </el-auto-resizer>
        </div>
        <ArrayValueViewer v-if="curRow?.array" class="array-pane" :tag-id="curRow.itemId"
          :definition="curRow.array" :writable="editable && curRow.writable" :suspended="suspended" />
      </div>
    </div>

    <!-- 底部事件日志 -->
    <div class="card qlog">
      <div class="qlogh">事件日志</div>
      <el-table :data="log" size="small" height="132" :show-header="true">
        <el-table-column prop="date" label="日期" width="100" />
        <el-table-column prop="time" label="时间" width="90" />
        <el-table-column prop="msg" label="事件" show-overflow-tooltip />
      </el-table>
    </div>

    <!-- 浮动右键菜单 -->
    <div v-if="menu.show" class="ctxmenu" :style="{ left: menu.x + 'px', top: menu.y + 'px' }" @click.stop @contextmenu.prevent>
      <template v-for="(it, i) in menu.items" :key="i">
        <div v-if="it.sep" class="ci-sep" />
        <div v-else class="ci" :class="{ dis: it.disabled }" @click="clickMenu(it)"><span>{{ it.label }}</span><small v-if="it.gesture">{{ it.gesture }}</small></div>
      </template>
    </div>

    <!-- 写值 -->
    <el-dialog v-model="wOpen" class="qc-write-dialog" :title="`写入 ${wItem?.itemId || ''}`" width="440"
               append-to-body :z-index="5200" :close-on-click-modal="!writing" :close-on-press-escape="!writing" :show-close="!writing">
      <div class="hint" style="margin-bottom:8px">数据类型:{{ wItem?.dataType }}</div>
      <el-input v-model="wVal" :disabled="writing" :placeholder="wItem?.rawType==='bool' ? 'true / false' : '值'" />
      <p v-if="writing" role="status">{{ t(writeStatus) }}</p>
      <div v-if="pointWriteHint(wItem?.rawType)" class="hint" style="margin-top:6px">{{ pointWriteHint(wItem?.rawType) }}</div>
      <template #footer><el-button :disabled="writing" @click="wOpen=false">取消</el-button><el-button type="primary" :loading="writing" @click="doWrite">写入</el-button></template>
    </el-dialog>
  </div>
</template>

<style scoped>
.qc { display: flex; flex-direction: column; gap: 8px; height: 100%; min-height: 460px; }
.qbar { display: flex; align-items: center; gap: 4px; padding: 6px 8px; }
.qbar .sep { width: 1px; height: 18px; background: #e2e6eb; margin: 0 5px; }
.qbar .grow { flex: 1; }
.qbar .ms { color: #888; font-size: 12px; margin-left: 2px; }
.qbar .cnt { color: #555; font-size: 12px; }
.qbar .qstatus { color: #657181; font-size: 12px; white-space: nowrap; margin-right: 8px; }
.scope-search { width: min(280px, 24vw); height: 28px; box-sizing: border-box; border: 1px solid #d8dee8; border-radius: 5px; padding: 0 9px; outline: none; }
.scope-search:focus { border-color: #409eff; box-shadow: 0 0 0 2px rgba(64,158,255,.12); }
.page-button { height: 28px; border: 1px solid #d8dee8; border-radius: 5px; background: #fff; padding: 0 9px; cursor: pointer; }
.page-button:disabled { color: #a8afb9; background: #f5f7fa; cursor: not-allowed; }
.page-label { color: #657181; font-size: 12px; white-space: nowrap; }
.tb { width: 30px; height: 28px; border: 1px solid transparent; border-radius: 5px; background: transparent; cursor: pointer; display: inline-flex; align-items: center; justify-content: center; color: #444; }
.tb:hover:not(:disabled) { background: #eef2f8; border-color: #dbe2ea; }
.tb:disabled { color: #c8ccd2; cursor: not-allowed; }
.mdl { font-family: 'Segoe MDL2 Assets', 'Segoe Fluent Icons'; font-size: 16px; line-height: 1; }
.qmain { display: flex; gap: 8px; flex: 1; min-height: 0; }
.qtree { width: 360px; overflow: hidden; padding: 6px; flex-shrink: 0; }
.qgrid { flex: 1; min-width: 0; padding: 0; overflow: hidden; display: flex; flex-direction: column; }
.array-pane { flex: 1; min-height: 0; border-top: 1px solid #dfe6ee; }
.virtual-grid { height: 100%; min-height: 0; }
.virtual-grid.sliced { height: 36%; flex: 0 0 36%; }
.stream-state { font-size: 12px; color: #6b7280; padding: 2px 7px; border-radius: 10px; background: #f1f5f9; }
.auto-refresh { display: inline-flex; align-items: center; gap: 4px; font-size: 12px; color: #475467; }
.auto-refresh input { width: 76px; height: 24px; font-size: 12px; text-align: right; }
.auto-refresh .unit { color: #98a2b3; }
.stream-state.live { color: #18743a; background: #eaf7ee; }
.stream-state.stale, .stream-state.reconnecting, .stream-state.resyncing { color: #9a6700; background: #fff7d6; }
.stream-state.project-changed, .stream-state.capacity-limited, .stream-state.runtime-unavailable { color: #b42318; background: #fff0ee; }
.tn { width: 100%; min-width: 0; display: grid; grid-template-columns: 20px minmax(0, 1fr); align-items: center; font-size: 13px; line-height: 1.2; }
.tn .ic { text-align: center; }
.tn-copy { min-width: 0; display: grid; gap: 3px; }
.tn-main { min-width: 0; display: flex; align-items: baseline; gap: 7px; }
.tn-main strong, .tn-main small, .tn-path { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.tn-main strong { flex: none; max-width: 48%; color: #33465a; }
.tn-main small { min-width: 0; color: #65798e; font-size: 11px; }
.tn-path { color: #8796a7; font-size: 10.5px; }
.tn.server .tn-copy { display: block; }
:deep(.qc-column-header) { position: relative; width: 100%; height: 100%; display: flex; align-items: center; min-width: 0; }
:deep(.qc-column-title), :deep(.qc-cell-ellipsis) { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
:deep(.qc-column-resizer) { position: absolute; z-index: 3; top: -8px; right: -8px; bottom: -8px; width: 14px; cursor: col-resize; user-select: none; }
:deep(.qc-column-resizer)::after { content: ''; position: absolute; top: 7px; bottom: 7px; left: 6px; width: 1px; background: #d5dee8; transition: background .15s, width .15s; }
:deep(.qc-column-resizer:hover)::after { width: 2px; background: #409eff; }
.qlog { height: 168px; padding: 0; overflow: hidden; flex-shrink: 0; }
.qlogh { font-weight: 600; font-size: 13px; padding: 5px 10px; background: #f2f5f9; }
.hint { color: #888; font-size: 12px; }
.ctxmenu { position: fixed; z-index: 3000; background: #fff; border: 1px solid #dcdfe6; border-radius: 6px; box-shadow: 0 6px 24px rgba(0,0,0,.16); padding: 4px; min-width: 170px; user-select: none; }
.ci { display: flex; align-items: center; justify-content: space-between; gap: 20px; padding: 6px 12px; border-radius: 4px; font-size: 13px; cursor: pointer; }
.ci:hover { background: #eef2f8; }
.ci small { color: #a0a6ae; font-size: 11px; }
.ci.dis { color: #c0c4cc; cursor: not-allowed; } .ci.dis:hover { background: transparent; }
.ci-sep { height: 1px; background: #eef0f3; margin: 4px 6px; }
</style>
