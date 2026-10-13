<script setup lang="ts">
import { ref, computed, reactive, onMounted, onUnmounted, onActivated, onDeactivated, nextTick, toRaw, watch } from 'vue'
import { onBeforeRouteLeave } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import { api, type NetworkAdapterDto, type OpcUaBrowsePreviewBody } from '../api'
import { canEdit, canView } from '../auth'
import { useRouter } from 'vue-router'
import { logCenterLink } from '../utils/logCenter'
import Points from './Points.vue'
import MesFlow from './MesFlow.vue'
import WorkWindow, { type WorkWindowRect } from '../components/WorkWindow.vue'
import OpcUaTagBrowserDialog from '../components/OpcUaTagBrowserDialog.vue'
import SiemensAuthenticationDialog from '../components/SiemensAuthenticationDialog.vue'
import ArrayTagEditor from '../components/ArrayTagEditor.vue'
import ModbusTagEditor from '../components/ModbusTagEditor.vue'
import ProtocolDeviceSettings from '../components/ProtocolDeviceSettings.vue'
import { resolveTagProtocolContext, validateModbusTag, validateProtocolSettings } from '../utils/protocolEditors'
import ProjectContextMenu from '../features/project/components/ProjectContextMenu.vue'
import ProjectImportDialog from '../features/project/components/ProjectImportDialog.vue'
import ProjectExportDialog from '../features/project/components/ProjectExportDialog.vue'
import { projectApi } from '../features/project/api'
import {
  applyLazyChildren,
  assignLazyIds,
  buildLazyMutations,
  captureBaseline,
  combineLazyMutations,
  createLazyState,
  hasUnloadedSubGroups,
  LazySaveUnsupportedError,
  needsLoad,
  refreshBaselineNode,
  removedMesTasks,
  unloadedTagTotal,
} from '../features/project/lazyProject'
import { useProjectOperationFeedback, paintProjectFeedback } from '../features/project/useProjectOperationFeedback'
import { useTimedAutoSave } from '../composables/useTimedAutoSave'
import { loadProjectAutoSaveSettings, projectAutoSaveSettings } from '../composables/projectAutoSaveSettings'
import {
  channelRuntimeMark,
  deviceRuntimeMark,
  findRuntimeDevice,
  indexRuntimeDevices,
  type RuntimeDeviceIndex,
  type RuntimeMark,
} from '../utils/deviceRuntime'
import {
  authorizeDeletion,
  consumeDeletionAuthorization,
  emptyProjectShape,
  projectShape,
  shapeLoss,
  unapprovedShapeLoss,
  type ProjectShape,
} from '../utils/projectTopology'
import { isMesTaskBoundaryGroup, migrateMesReferences, pruneOrphanMesTasks } from '../utils/mesDataRanges'
import { addressByte, schemaChildren, type StructureRow } from '../utils/structureTree'
import { findDbNumber, rememberDbNumber, rememberedDbNumber } from '../utils/siemensDbNumbers'
import { findSiemensDbImportGroup, mergeSiemensDbImport } from '../utils/siemensDbImport'
import { dataTypeEditorValue, editableDataTypesForDriver, pointWriteHint } from '../utils/siemensDataTypes'
import {
  configureDeviceFirstLevelMesFlows,
  ensureMesTaskForGroup,
  findMesGroupAvailabilityConflict,
  findMesTaskEnableConflict,
  isMesFlowAvailable,
  setAllMesTasksEnabled,
  setMesTaskEnabledWithConfirmation,
  setMesTaskFlowAvailable,
} from '../utils/projectMesSync'
import {
  createProjectClipboardItems,
  materializeProjectClipboardItem,
  resolveProjectGridTargets,
  type ProjectClipboardItem,
  type ProjectGridTarget,
} from '../utils/projectGridClipboard'
import { applyTagImport, buildImportPreview, parseKepTagCsv, type ImportedTag } from '../utils/kepTagImport'
import { flattenLegacyOpcUaDeviceRoot, replaceImportedTarget } from '../utils/opcUaStructuredImport'
import { pointTypeDisplay } from '../utils/pointValueDisplay'
import { applyMesFlowSnapshot } from '../utils/projectMesSync'
import { useI18n } from '../i18n'
import {
  collectionStateMark,
  isMesFlowConfigurationDisabled,
  mesFlowDialogTitle,
  projectTreeDescription,
  systemTreeLabel,
} from '../utils/projectPresentation'
import { adapterOptions, applyAdapterValue, selectedAdapterValue } from '../utils/networkAdapterSelection'
import { createCompactId } from '../utils/runtimeId'
import { useRealtimeStore } from '../stores/realtime'
import { storeToRefs } from 'pinia'

defineOptions({ name: 'Project' })   // App.vue 按此名称缓存本页面
const { t } = useI18n()
const projectOperation = useProjectOperationFeedback()
const operationBusy = projectOperation.busy
const operationLabel = projectOperation.label
const realtime = useRealtimeStore()
const { deviceStates: realtimeDeviceStates, status: realtimeStatus } = storeToRefs(realtime)
// 只有查看权限：工程只能浏览/查看属性/导出，所有修改入口隐藏或禁用(改了也会被网关拒绝)
const projectEditable = computed(() => canEdit('project'))
// 通道重连属于监控中心的操作权限(网关按 /api/v1/channels 归到 monitor)
const monitorEditable = computed(() => canEdit('monitor'))

const mesFlowOpen = ref(false)   // MES 采集流程画布:右键弹窗打开(不再跳转 /mes 整页)
const mesFlowPreparing = ref(false)
let mesFlowOpenGeneration = 0
const mesFlowGroup = ref('')     // 从哪个标记组打开的 → 传给弹窗自动选中该组的采集任务
let mesScopeRenames: { from: string; to: string }[] = []
const mesFlowRef = ref<any>()
let mesScopeOwner: any = null
type ToolName = 'qc' | 'mes'
const qcMinimized = ref(false)
const qcMaximized = ref(false)
const mesFlowMinimized = ref(false)
const mesFlowMaximized = ref(false)
function initialToolRect(tool: ToolName): WorkWindowRect {
  const viewportWidth = typeof window === 'undefined' ? 1440 : window.innerWidth
  const viewportHeight = typeof window === 'undefined' ? 900 : window.innerHeight
  const x = tool === 'mes' ? 28 : 72
  const y = tool === 'mes' ? 36 : 64
  return {
    x, y,
    width: Math.max(760, viewportWidth - x - 28),
    height: Math.max(520, viewportHeight - y - 42),
  }
}
const qcRect = ref<WorkWindowRect>(initialToolRect('qc'))
const mesFlowRect = ref<WorkWindowRect>(initialToolRect('mes'))
// Element Plus 默认模态层从 2000 起。工作窗必须低于它，否则确认框遮罩会压住页面，框体却被工作窗盖住。
const toolBackZ = 2201
const toolFrontZ = 2202
const qcZ = ref(toolBackZ)
const mesFlowZ = ref(toolFrontZ)

function focusTool(tool: ToolName) {
  if (tool === 'qc') {
    qcZ.value = toolFrontZ
    mesFlowZ.value = toolBackZ
  } else {
    mesFlowZ.value = toolFrontZ
    qcZ.value = toolBackZ
  }
}
function prepareToolOpen(tool: ToolName, maximized = false) {
  if (tool === 'qc') { qcMinimized.value = false; qcMaximized.value = maximized }
  else { mesFlowMinimized.value = false; mesFlowMaximized.value = maximized }
  focusTool(tool)
}
function restoreTool(tool: ToolName) {
  if (tool === 'qc') { qcMinimized.value = false; qcMaximized.value = false }
  else { mesFlowMinimized.value = false; mesFlowMaximized.value = false }
  focusTool(tool)
}
function updateToolMaximized(tool: ToolName, value: boolean) {
  if (tool === 'qc') qcMaximized.value = value
  else mesFlowMaximized.value = value
  focusTool(tool)
}
const siemensAuthOpen = ref(false)
const siemensAuthDeviceName = ref('')
const siemensAuthNode = ref<any>(null)

/** 工程配置(对齐桌面 WPF / KEPServerEX 范式):
 *  左树 工程→连接→通道→设备→组(嵌套);右明细网格 = 选中节点的「子项」为行(通道/设备/标记),列头随上下文变。
 *  左键选中、右键上下文菜单、双击改属性、复制/剪切/粘贴/删除(Ctrl+C/X/V/Del,焦点在树或右表自动路由)、新建向导;
 *  保存 = PUT /project 热重载(带并发冲突检测)。 */

const proj = ref<any>(null)
/** 超过 5 万点：同一个页面，设备/组的下级在展开或选中时才从服务端载入，保存只提交改动的节点。 */
const lazyMode = ref<boolean | null>(null)
let lazyState = createLazyState()
/** 新建空工程等整体替换后，下一次保存必须整包上传(没有可比较的保存基准)。 */
let forceFullSave = false
const rev = ref('')
const dirty = ref(false)
const editVersion = ref(0)
const autoSaveBlocked = ref(false)
const saveError = ref('')
const savedShape = ref<ProjectShape>(emptyProjectShape())
const authorizedDeletion = ref<ProjectShape>(emptyProjectShape())
const drivers = ref<any[]>([])
const networkAdapters = ref<NetworkAdapterDto[]>([])
const driversLoading = ref(false)
const networkAdaptersLoading = ref(false)
let driversRequest: Promise<boolean> | null = null
let networkAdaptersRequest: Promise<boolean> | null = null

async function loadDrivers(force = false): Promise<boolean> {
  if (driversRequest) await driversRequest
  if (!force && drivers.value.length) return true
  driversLoading.value = true
  const request = (async () => {
    try {
      const rows = await api.drivers()
      if (Array.isArray(rows) && rows.length) drivers.value = rows
      return Array.isArray(rows)
    } catch {
      // 短暂启动失败不能抹掉已成功加载的设备/驱动选项；打开配置时会再次拉取。
      return false
    } finally { driversLoading.value = false }
  })()
  driversRequest = request
  try { return await request }
  finally { if (driversRequest === request) driversRequest = null }
}

async function loadNetworkAdapters(force = false): Promise<boolean> {
  if (networkAdaptersRequest) await networkAdaptersRequest
  if (!force && networkAdapters.value.length) return true
  networkAdaptersLoading.value = true
  const request = (async () => {
    try {
      const rows = await api.networkAdapters()
      if (Array.isArray(rows)) networkAdapters.value = rows
      return Array.isArray(rows)
    } catch {
      // 保留上一次成功结果，避免一次网络抖动让下拉框永久变空。
      return false
    } finally { networkAdaptersLoading.value = false }
  })()
  networkAdaptersRequest = request
  try { return await request }
  finally { if (networkAdaptersRequest === request) networkAdaptersRequest = null }
}

async function loadConfigurationLookups(force = false) {
  return await Promise.all([loadDrivers(force), loadNetworkAdapters(force)])
}
const ver = ref(0)                      // 结构变化后强制树/网格重算
function bump() { ver.value++; const key = sel.value?.key; if (key) nextTick(() => treeRef.value?.setCurrentKey(key)) }
const markDirty = () => { if (!projectEditable.value) return; dirty.value = true; editVersion.value++ }
function markConfirmedDeletion(before: ProjectShape) {
  authorizedDeletion.value = authorizeDeletion(authorizedDeletion.value, before, projectShape(proj.value))
  markDirty()
}
const treeRef = ref<any>()

// 左右可拖拽分隔:树宽可调(拖中间竖条),文字按树宽自适应截断+悬停看全
const treeW = ref(360)
let resizing = false
function startResize(e: MouseEvent) {
  resizing = true; e.preventDefault()
  const move = (ev: MouseEvent) => { if (resizing) treeW.value = Math.max(260, Math.min(680, treeW.value + ev.movementX)) }
  const up = () => { resizing = false; window.removeEventListener('mousemove', move); window.removeEventListener('mouseup', up) }
  window.addEventListener('mousemove', move); window.addEventListener('mouseup', up)
}

// 底部健康信息保持低频请求；设备在线状态统一来自 App 的共享 SignalR，
// 避免 HTTP 快照失败/乱序时把同一设备在“未下发、离线”之间反复覆盖。
const health = ref<any>(null)
const runtimeLastUpdated = ref('')
const runtimeDevices = computed<RuntimeDeviceIndex>(() => indexRuntimeDevices([...realtimeDeviceStates.value.values()]))
const runtimeStatusAvailable = computed(() => realtimeStatus.value === 'live' || realtimeDeviceStates.value.size > 0)
const runtimeStatusError = computed(() => {
  if (runtimeStatusAvailable.value || ['idle', 'connecting', 'reconnecting', 'resyncing'].includes(realtimeStatus.value)) return ''
  if (realtimeStatus.value === 'project-changed') return t('工程点表已经变化，正在重建实时状态连接')
  if (realtimeStatus.value === 'capacity-limited') return t('实时状态客户端已达到上限')
  return t('运行状态接口不可用')
})
watch(realtimeDeviceStates, devices => {
  if (devices.size) runtimeLastUpdated.value = new Date().toLocaleTimeString('zh-CN', { hour12: false })
}, { immediate: true })
let runtimeTimer: any
let runtimePolling = false
async function pollRuntimeStatus(): Promise<boolean> {
  if (runtimePolling) return false
  runtimePolling = true
  try {
    health.value = await api.health()
    return true
  } catch {
    health.value = null
    return false
  } finally { runtimePolling = false }
}
// 页面被缓存(切到其他页面)时暂停底部状态轮询，回来再恢复。
onActivated(() => { clearInterval(runtimeTimer); runtimeTimer = setInterval(pollRuntimeStatus, 3000) })
onDeactivated(() => clearInterval(runtimeTimer))
onUnmounted(() => clearInterval(runtimeTimer))

// 稳定 key:同一 def 对象恒定 key,重算树后仍能定位/高亮(避免位置 key 漂移)
const uidMap = new WeakMap<object, string>()
let uidc = 0
function uid(def: any): string { let u = uidMap.get(def); if (!u) { u = 'n' + (++uidc); uidMap.set(def, u) } return u }

// ---------- 载入 / 保存 ----------
async function load() {
  return projectOperation.run('load', t('正在载入工程…'), async () => {
  applyLoadedProject(await fetchProject())
  })
}
/** 当前工程：普通工程取完整内容；大工程只取通道/设备骨架(并重置按需载入状态)。 */
async function fetchProject(): Promise<{ proj: any; rev: string }> {
  if (!lazyMode.value) return await api.project()
  const shell = await projectApi.shell()
  lazyState = createLazyState()
  for (const [id, counts] of Object.entries(shell.lazy || {})) lazyState.counts.set(id, counts)
  captureBaseline(lazyState, shell.project)
  return { proj: shell.project, rev: shell.rev }
}
function applyLoadedProject(r: { proj: any; rev: string }) {
  // 重新载入会替换工程对象；旧范围及尚未完成的打开动作不能继续应用到新工程。
  qcOpen.value = false; qcOpening.value = false
  pendingQcRoot.value = null; pendingQcActive.value = null; qcPreferredScope.value = ''
  mesScopeRenames = []
  mesFlowGroup.value = ''
  // 普通工程一次载入全部内容，也以它为保存基准：保存时只提交改动过的节点
  if (!lazyMode.value) {
    lazyState = createLazyState()
    captureBaseline(lazyState, r.proj)
  }
  forceFullSave = false
  const serverShape = projectShape(r.proj)
  proj.value = r.proj; rev.value = r.rev
  dirty.value = false
  savedShape.value = serverShape
  authorizedDeletion.value = emptyProjectShape()
  autoSaveBlocked.value = false; saveError.value = ''
  sel.value = null; gridSel.value = []; curRow.value = null; gridSelectedKeys.value = new Set(); gridCurrentKey.value = ''; bump()
}
onMounted(async () => {
  // 状态、摘要并行请求，不再逐个等待。
  const [, summaryResult] = await Promise.allSettled([pollRuntimeStatus(), projectApi.summary()])
  try {
    if (summaryResult.status === 'rejected') throw summaryResult.reason
    const summary = summaryResult.value
    lazyMode.value = summary.fullProjectSupported === false || Number(summary.totalTagCount || 0) > 50_000
  } catch {
    // 兼容尚未提供摘要接口的旧 Runtime；完整接口若被限制，下面仍会按 413 自动切到按需载入。
    lazyMode.value = Number(health.value?.tags || 0) > 50_000
  }
  void loadConfigurationLookups()
  try { await load() }
  catch (e: any) {
    if (Number(e?.response?.status) === 413 && !lazyMode.value) {
      lazyMode.value = true
      try { await load() } catch (retry: any) { ElMessage.error(t('载入工程失败:') + (retry?.message || '')) }
    }
    else ElMessage.error(t('载入工程失败:') + (e?.message || ''))
  }
})
// 页面被缓存后，快捷键和菜单关闭只在工程配置可见时生效，不能影响其他页面。
let everActivated = false
onActivated(() => {
  window.addEventListener('keydown', onKey)
  window.addEventListener('click', hideMenu)
  if (everActivated) void refreshIfChanged()
  everActivated = true
})
onDeactivated(() => { window.removeEventListener('keydown', onKey); window.removeEventListener('click', hideMenu) })
onUnmounted(() => { window.removeEventListener('keydown', onKey); window.removeEventListener('click', hideMenu) })

/**
 * 从其他页面切回工程配置：只问服务端工程版本变没变(未变化时服务端回 304，不传工程)。
 * 没变化就直接显示缓存的页面，展开状态和选中项都保留；变化了且本页没有未保存修改时才重新载入。
 */
async function refreshIfChanged() {
  if (lazyMode.value !== false || !proj.value || dirty.value || saveBusy.value) return
  try {
    const changed = await api.projectIfChanged(rev.value)
    if (changed && !dirty.value) applyLoadedProject(changed)
  } catch { /* 网络抖动时继续显示当前内容，手动“重新载入”可再试 */ }
}

const saving = ref(false)
const backgroundSaving = ref(false)
const saveBusy = computed(() => saving.value || backgroundSaving.value)
let activeProjectSave: Promise<boolean> | null = null
function saveApply(automatic = false): Promise<boolean> {
  if (activeProjectSave) return activeProjectSave
  const current = performSaveApply(automatic).finally(() => {
    if (activeProjectSave === current) activeProjectSave = null
  })
  activeProjectSave = current
  return current
}
async function performSaveApply(automatic = false): Promise<boolean> {
  if (!proj.value || !projectEditable.value) return true
  if (saveBusy.value) return false
  const activeSaving = automatic ? backgroundSaving : saving
  const savingProject = proj.value
  let succeeded = false
  activeSaving.value = true
  saveError.value = ''
  try {
    if (!automatic) await nextPaint()
    if (lazyMode.value) return succeeded = await saveLazy(automatic)
  const savedVersion = editVersion.value
  const nextShape = projectShape(toRaw(proj.value))
  const authorizedAtSave = { ...authorizedDeletion.value }
  const destructive = shapeLoss(savedShape.value, nextShape)
  const unapproved = unapprovedShapeLoss(savedShape.value, nextShape, authorizedAtSave)
  if (automatic && unapproved.length) {
    saveError.value = `检测到未经过用户确认的配置数量减少（${unapproved.join('，')}），后台自动保存已停止；请检查后点击“保存并应用”确认。`
    autoSaveBlocked.value = true
    return false
  }
    // 与大工程一样只提交改动过的节点；只有局部修改表达不了的改动(工程属性、删 MES 任务、新建工程)才整包上传
    const incremental = await saveIncremental(automatic, authorizedAtSave)
    if (incremental !== null) return succeeded = incremental
  const snapshot = structuredClone(toRaw(proj.value))
    const result = await api.putProject(snapshot, rev.value, !automatic || destructive.length > 0)
    if (proj.value !== savingProject) return false
    if (result.rev) rev.value = result.rev
    captureBaseline(lazyState, snapshot)
    savedShape.value = nextShape
    authorizedDeletion.value = consumeDeletionAuthorization(authorizedDeletion.value, authorizedAtSave)
    if (editVersion.value === savedVersion) dirty.value = false
    autoSaveBlocked.value = false
    if (!automatic) ElMessage.success(t('已保存并热重载生效'))
    succeeded = true
    return true
  } catch (e: any) {
    saveError.value = e?.response?.data?.detail || e?.response?.data?.error || e.message || t('未知错误')
    if (e instanceof LazySaveUnsupportedError) autoSaveBlocked.value = true
    if (e?.response?.status === 409) {
      autoSaveBlocked.value = true
      if (!automatic)
        await ElMessageBox.confirm(saveError.value + '\n' + t('重新载入(丢弃本地未保存修改)?'), t('保存冲突'),
          { type: 'warning', confirmButtonText: t('重新载入'), cancelButtonText: t('先不') }).then(() => load()).catch(() => {})
    } else if (!automatic) ElMessage.error(t('保存失败:') + saveError.value)
    return false
  } finally {
    activeSaving.value = false
    if (succeeded && proj.value === savingProject && !dirty.value) commitQcScopeChange()
  }
}

/**
 * 普通工程的局部保存：只提交与保存基准相比改动过的节点(同一事务)。没有改动时不发请求。
 * 返回 null 表示这次改动局部修改表达不了，由调用方整包保存。
 */
async function saveIncremental(automatic: boolean, authorizedAtSave: ProjectShape): Promise<boolean | null> {
  if (forceFullSave || removedMesTasks(lazyState, proj.value)) return null
  const savedVersion = editVersion.value
  assignLazyIds(lazyState, proj.value)
  let command
  try { command = combineLazyMutations(buildLazyMutations(lazyState, proj.value)) }
  catch (e) { if (e instanceof LazySaveUnsupportedError) return null; throw e }
  const committed = captureSaveCommit()
  if (command) {
    const result = await projectApi.mutate(command, rev.value)
    if (proj.value !== committed.owner || lazyState !== committed.state) return false
    rev.value = result.revision || projectApi.revision() || rev.value
    if (result.mesTasksReconciled) {
      await refreshMesAfterReconcile()
      committed.baseline.mesBaseline = new Map((proj.value?.mesUpload?.tasks || []).map((task: any) => [String(task.id), JSON.stringify(task)]))
    }
  }
  if (!applySaveCommit(committed)) return false
  authorizedDeletion.value = consumeDeletionAuthorization(authorizedDeletion.value, authorizedAtSave)
  if (editVersion.value === savedVersion) dirty.value = false
  autoSaveBlocked.value = false
  if (!automatic) ElMessage.success(t(command ? '已保存并热重载生效' : '没有需要保存的修改'))
  return true
}

// 保存基准必须对应实际发送的内容；网络等待期间的新修改仍应在下一轮提交。
function captureSaveCommit() {
  const owner = proj.value, state = lazyState, baseline = createLazyState()
  captureBaseline(baseline, owner)
  return { owner, state, baseline, previousIds: new Set(state.baseline.keys()), shape: projectShape(owner) }
}
function applySaveCommit(committed: ReturnType<typeof captureSaveCommit>): boolean {
  if (proj.value !== committed.owner || lazyState !== committed.state) return false
  // 保留保存期间按需载入的已持久化节点，但不把新编辑的内容误记为已发送。
  for (const [id, entry] of committed.state.baseline)
    if (!committed.previousIds.has(id) && !committed.baseline.baseline.has(id)) committed.baseline.baseline.set(id, entry)
  committed.state.baseline = committed.baseline.baseline
  committed.state.mesBaseline = committed.baseline.mesBaseline
  committed.state.sectionBaseline = committed.baseline.sectionBaseline
  savedShape.value = committed.shape
  return true
}

/** 服务端在局部保存后清理了失效的 MES 任务：只取回 MES 配置，工程树对象保持不变。 */
async function refreshMesAfterReconcile() {
  // 只取工程外壳(通道/设备表头 + MES 配置)：局部保存后不能再把整个点位工程重新下载一遍。
  const shell = await projectApi.shell()
  applyMesFlowSnapshot(proj.value, shell.project)
  rev.value = shell.rev || rev.value
}

/** 大工程保存：只提交与载入时相比改动过的节点(同一事务)，不上传整个工程。 */
async function saveLazy(automatic: boolean): Promise<boolean> {
  const savedVersion = editVersion.value
  assignLazyIds(lazyState, proj.value)
  const command = combineLazyMutations(buildLazyMutations(lazyState, proj.value))
  const committed = captureSaveCommit()
  if (command) {
    const result = await projectApi.mutate(command, rev.value)
    if (proj.value !== committed.owner || lazyState !== committed.state) return false
    rev.value = result.revision || projectApi.revision() || rev.value
    if (result.mesTasksReconciled) {
      await refreshMesAfterReconcile()
      committed.baseline.mesBaseline = new Map((proj.value?.mesUpload?.tasks || []).map((task: any) => [String(task.id), JSON.stringify(task)]))
    }
  }
  if (!applySaveCommit(committed)) return false
  authorizedDeletion.value = emptyProjectShape()
  if (editVersion.value === savedVersion) dirty.value = false
  autoSaveBlocked.value = false
  bump()
  if (!automatic) ElMessage.success(t(command ? '已保存并热重载生效' : '没有需要保存的修改'))
  return true
}

// ---------- 大工程按需载入 ----------
// 请求按工程载入代次隔离；同一节点的浅/深读取共享在途请求，避免互相覆盖或复用旧工程响应。
const lazyRequests = new WeakMap<ReturnType<typeof createLazyState>, Map<string, Promise<void>>>()
function containerKind(def: any): 'device' | 'group' { return 'endpoint' in def ? 'device' : 'group' }
/** 载入设备/组的直接下级；deep=true 载入整棵子树(复制、剪切、导入等需要完整内容)。 */
async function ensureLoaded(def: any, deep = false): Promise<void> {
  if (!lazyMode.value || !def?.id || !needsLoad(lazyState, def)) return
  const state = lazyState
  let requests = lazyRequests.get(state)
  if (!requests) { requests = new Map(); lazyRequests.set(state, requests) }
  const key = def.id
  let request = requests.get(key)
  if (!request) {
    request = (async () => {
      const children = await projectApi.lazyChildren(containerKind(def), def.id, deep)
      if (state !== lazyState || !needsLoad(state, def)) return
      if (children.rev && rev.value && children.rev !== rev.value) {
        if (!dirty.value) {
          ElMessage.warning(t('工程已在其他地方被修改，已重新载入'))
          await load()
          return
        }
        ElMessage.warning(t('工程已在其他地方被修改；保存本页修改时会提示冲突'))
      }
      applyLazyChildren(state, containerKind(def), def, children, deep)
      bump()
    })().finally(() => { if (requests.get(key) === request) requests.delete(key) })
    requests.set(key, request)
  }
  await request
}
/** 确保节点(通道/设备/组)下的全部内容都已载入。 */
async function ensureDeepLoaded(kind: string, def: any): Promise<void> {
  if (!lazyMode.value || !def) return
  const state = lazyState
  const visit = async (node: any): Promise<void> => {
    if (state !== lazyState) return
    await ensureLoaded(node, true)
    if (state !== lazyState) return
    // 等待的可能是已经在途的浅请求；必须按响应后的树继续检查，不能假设孙层已完整。
    for (const group of ('endpoint' in node ? node.groups : node.subGroups) || []) await visit(group)
  }
  if (kind === 'channel') for (const device of def.devices || []) await visit(device)
  else if (kind === 'device' || kind === 'group') await visit(def)
}
async function ensureTargetsDeepLoaded(selected: ProjectGridTarget[]) {
  for (const target of selected) await ensureDeepLoaded(target.kind, target.def)
}

const autoSave = useTimedAutoSave({
  dirty,
  saving: saveBusy,
  changeVersion: editVersion,
  save: () => saveApply(true),
  canSave: () => projectEditable.value && !mesFlowOpen.value && !autoSaveBlocked.value,
  intervalMs: () => projectAutoSaveSettings.seconds * 1000,
  enabled: () => projectAutoSaveSettings.enabled,
})
void loadProjectAutoSaveSettings()
/** 属性/新建对话框点“确定”后立即保存，不用等周期自动保存或再点“保存并应用”。 */
function saveAfterConfirm() {
  if (!autoSaveBlocked.value) void autoSave.flush()
}
const autoSaveState = autoSave.state
const autoSaveHint = computed(() => projectAutoSaveSettings.enabled
  ? t('属性对话框点“确定”即保存；其他改动每 {seconds} 秒在后台静默保存一次，可在 IGW 设置中调整', { seconds: projectAutoSaveSettings.seconds })
  : t('属性对话框点“确定”即保存；其他改动需点击“保存并应用”。可在 IGW 设置中开启自动保存'))
const autoSavedText = autoSave.lastSavedText

async function beforeMesFlowClose() {
  const ok = !mesFlowRef.value || await mesFlowRef.value.flushAutoSave()
  if (!ok) ElMessage.error(t('采集流程自动保存失败，已阻止关闭，请检查页面提示后重试'))
  return ok
}

async function afterMesFlowClose() {
  try {
    const current = await fetchProject()
    if (!proj.value) {
      await load()
      return
    }
    // 流程窗口只修改 mesUpload。保留工程树对象身份，避免关闭窗口后展开状态和选中节点失效。
    applyMesFlowSnapshot(proj.value, current.proj)
    captureBaseline(lazyState, proj.value)
    rev.value = current.rev
    savedShape.value = projectShape(current.proj)
    authorizedDeletion.value = emptyProjectShape()
    dirty.value = false
    autoSaveBlocked.value = false
    saveError.value = ''
    bump()
  }
  catch (e: any) { ElMessage.error(t('重新同步工程失败:') + (e?.message || '')) }
}

onBeforeRouteLeave(async () => {
  if (mesFlowRef.value && !(await mesFlowRef.value.flushAutoSave())) {
    ElMessage.error(t('采集流程尚未保存，已取消离开页面'))
    return false
  }
  if (dirty.value && !(await autoSave.flush())) {
    ElMessage.error(t('工程配置尚未保存，已取消离开页面'))
    return false
  }
  return true
})
async function reload() {
  try { return await projectOperation.run('reload', t('正在重新载入工程…'), async () => {
  if (dirty.value) { try { await ElMessageBox.confirm(t('有未保存的修改，重新载入会丢弃。继续？'), t('提示'), { type: 'warning' }) } catch { return } }
  await load(); ElMessage.success(t('已重新载入'))
  }) } catch (error: any) { ElMessage.error(error?.response?.data?.error || error?.message || String(error)) }
}

// ---------- 服务端流式 JSON 整体导入 / 导出 ----------
const projectImportOpen = ref(false)
const projectExportOpen = ref(false)
function exportKepProject() { projectExportOpen.value = true }

async function requestMesFlowClose() {
  mesFlowOpenGeneration++
  if (mesFlowPreparing.value) {
    mesFlowPreparing.value = false
    mesFlowOpen.value = false
    mesFlowMinimized.value = false
    mesFlowMaximized.value = false
    return
  }
  if (!(await beforeMesFlowClose())) return
  mesFlowOpen.value = false
  mesFlowMinimized.value = false
  mesFlowMaximized.value = false
  await afterMesFlowClose()
}
function importKepProject() { if (projectEditable.value) projectImportOpen.value = true }
async function onProjectImported() {
  try { return await projectOperation.run('onProjectImported', t('正在刷新导入后的工程…'), async () => {
  await load()
  sel.value = null; gridSel.value = []; curRow.value = null; gridSelectedKeys.value = new Set(); gridCurrentKey.value = ''
  bump()
  ElMessage.success(t('JSON 配置已导入并应用'))
  }) } catch (error: any) { ElMessage.error(error?.response?.data?.error || error?.message || String(error)) }
}

// ---------- 树 ----------
// 本版本不支持的驱动：旧工程里的通道照常显示，但网关不再加载，名称上明确标注。驱动列表还没拉到时不下结论。
const isUnsupportedDriver = (id: string) => drivers.value.length > 0 && !drivers.value.some((d) => d.driverId === id)
// 授权码不包含的驱动：不能新建这种通道；已有的通道照常显示、名称上标注，但网关不采集
const isUnlicensedDriver = (id: string) => drivers.value.some((d) => d.driverId === id && d.licensed === false)
const driverName = (id: string) => {
  if (isUnsupportedDriver(id)) return t('{driver}(不支持)', { driver: id })
  const name = drivers.value.find((d) => d.driverId === id)?.displayName || id
  return isUnlicensedDriver(id) ? t('{driver}(未授权)', { driver: name }) : name
}
function cnt(g: any): number {
  const unloaded = unloadedTagTotal(lazyState, g)
  if (unloaded !== undefined) return unloaded
  return (g.tags?.length || 0) + (g.subGroups || []).reduce((s: number, x: any) => s + cnt(x), 0)
}
function devTagCount(d: any): number {
  const unloaded = unloadedTagTotal(lazyState, d)
  if (unloaded !== undefined) return unloaded
  return (d.tags?.length || 0) + (d.groups || []).reduce((s: number, g: any) => s + cnt(g), 0)
}
/** 尚未载入的设备/组：放一个“正在载入”子节点，让树上出现展开箭头。 */
function lazyPlaceholder(parent: any): any[] {
  return [{ key: `${parent.key}:lazy`, kind: 'loading', label: t('正在载入…'), icon: '⏳', ref: null, parent, holder: null, children: [] }]
}

const treeProps = { value: 'key', label: 'label', children: 'children' }   // el-tree-v2 字段映射(虚拟滚动,大数据量不卡)
const treeData = computed<any[]>(() => {
  void ver.value
  if (!proj.value) return []
  const p = proj.value
  const conn: any = { key: '__conn', kind: 'connectivity', label: systemTreeLabel('connectivity', t), icon: '🔌', ref: p, parent: null, holder: null, children: [] }
  const root: any = { key: '__proj', kind: 'project', label: systemTreeLabel('project', t), icon: '🗂', ref: p, parent: null, holder: null, children: [conn] }
  conn.parent = root
  conn.children = (p.channels || []).map((c: any) => {
    const cn: any = { key: uid(c), kind: 'channel', label: c.name || systemTreeLabel('channel', t), icon: '📡', ref: c, parent: conn, channel: c, holder: p.channels, children: [] }
    cn.children = (c.devices || []).map((d: any) => {
      const dn: any = { key: uid(d), kind: 'device', label: d.name || systemTreeLabel('device', t), icon: '🖥', ref: d, parent: cn, channel: c, device: d, holder: c.devices, children: [] }
      dn.children = hasUnloadedSubGroups(lazyState, d) ? lazyPlaceholder(dn) : groupNodes(d.groups || (d.groups = []), dn, c, d)
      return dn
    })
    return cn
  })
  return [root]
})
function groupNodes(gs: any[], parent: any, ch: any, dev: any): any[] {
  return gs.map((g: any) => {
    const gn: any = { key: uid(g), kind: 'group', label: g.name || systemTreeLabel('group', t), icon: '📁', ref: g, parent, channel: ch, device: dev, holder: gs, children: [] }
    gn.children = hasUnloadedSubGroups(lazyState, g) ? lazyPlaceholder(gn) : groupNodes(g.subGroups || (g.subGroups = []), gn, ch, dev)
    return gn
  })
}
const expandedKeys = ref<string[]>(['__proj', '__conn'])
// el-tree-v2 每次 data 变化都会把展开态重置回 default-expanded-keys(见源码 watch(props.data)→setExpandedKeys)。
// 故实时维护 expandedKeys=当前全部展开节点,任何 bump()/拖拽/结构改动后都能原样恢复,不再塌树。
function onNodeExpand(data: any) {
  if (data?.key && !expandedKeys.value.includes(data.key)) expandedKeys.value = [...expandedKeys.value, data.key]
  if (needsLoad(lazyState, data?.ref)) void ensureLoaded(data.ref).catch(lazyLoadFailed)
}
function lazyLoadFailed(error: any) {
  ElMessage.error(t('载入下级失败:') + (error?.response?.data?.error || error?.message || String(error)))
}
// 折叠:必须连同「全部子孙」的 key 一起从 expandedKeys 移除。
// 否则子孙仍留在 default-expanded-keys 里,el-tree-v2 的 setExpandedKeys 会沿 parent 链
// 把被折叠的父节点重新展开(见其源码 keys.forEach → while(node) node=node.parent 逐级 add),
// 表现就是「点父节点折叠没反应/卡住」——正是深展开到 CodeStruct 后折叠 S7Sim 不动的根因。
function onNodeCollapse(data: any) {
  if (!data?.key) return
  const drop = new Set<string>()
  const walk = (n: any) => { if (!n) return; if (n.key) drop.add(n.key); (n.children || []).forEach(walk) }
  walk(data)
  expandedKeys.value = expandedKeys.value.filter((k) => !drop.has(k))
}

// ---------- 选中(树)+ 焦点面板 + 网格选中 ----------
const sel = ref<any>(null)                       // {kind, ref, channel?, device?, holder?, key}
const activePane = ref<'tree' | 'grid'>('tree')
const gridSel = ref<any[]>([])                   // 网格 checkbox 多选(行 {def,...})
const curRow = ref<any>(null)                    // 网格当前行
const gridTableRef = ref<any>()
const gridSelectedKeys = ref<Set<string>>(new Set())
const gridCurrentKey = ref('')

function pickNode(node: any) {
  // 工具栏动作稍后只拿 sel；必须保留原始父链，否则 pathOf(sel) 会退化成短名，
  // 同名组和 Quick Client 精确范围都会指向错误节点。
  sel.value = { kind: node.kind, ref: node.ref, channel: node.channel, device: node.device,
    holder: node.holder, key: node.key, parent: node.parent }
  if (needsLoad(lazyState, node.ref)) void ensureLoaded(node.ref).catch(lazyLoadFailed)
  activePane.value = 'tree'; gridSel.value = []; curRow.value = null
  gridSelectedKeys.value = new Set(); gridCurrentKey.value = ''
  nextTick(() => gridTableRef.value?.clearSelection?.())
}
function onNodeClick(data: any) { hideMenu(); if (data?.kind !== 'loading') pickNode(data) }

// ---------- 右明细网格(子项为行,列随上下文)----------
function tagTypeLabel(tag: any): string {
  if (!tag?.array) return tag?.dataType || ''
  return `${pointTypeDisplay(tag.array.elementType ?? tag.dataType)}[${(tag.array.dimensions || []).join(' × ')}]`
}
function tagPointLabel(tag: any): string {
  if (!tag?.array) return tag.scaling?.enabled ? t(tag.scaling.squareRoot ? '平方根' : '线性') : t('无')
  const count = (tag.array.dimensions || []).reduce((total: number, value: number) => total * value, 1)
  return t('{count} 个元素', { count: Number.isSafeInteger(count) ? count : '—' })
}
const view = computed<{ headers: string[]; rows: any[]; rowKind: string; addLabel: string }>(() => {
  void ver.value
  const n = sel.value
  if (!n) return { headers: [], rows: [], rowKind: '', addLabel: '' }
  if (n.kind === 'project' || n.kind === 'connectivity') {
    return {
      headers: ['通道名称', '驱动程序', '连接', '共享', '虚拟网络', '说明'].map((label) => t(label)), rowKind: 'channel', addLabel: t('新建通道…'),
      rows: (proj.value.channels || []).map((c: any) => ({ selectionKey: uid(c), def: c, kind: 'channel', holder: proj.value.channels, sourcePath: c.name, c1: c.name, c2: driverName(c.driverId), c3: t('其他'), c4: t('不适用'), c5: t('不适用'), c6: c.description || '' })),
    }
  }
  if (n.kind === 'channel') {
    return {
      headers: ['设备名称', '驱动/型号', '连接地址', '说明', '', ''].map((label) => t(label)), rowKind: 'device', addLabel: t('新建设备…'),
      rows: (n.ref.devices || []).map((d: any) => ({ selectionKey: uid(d), def: d, kind: 'device', holder: n.ref.devices, sourcePath: `${pathOf(n)}.${d.name}`, c1: d.name, c2: driverName(n.ref.driverId), c3: d.endpoint, c4: d.description || '', c5: '', c6: '' })),
    }
  }
  // 设备/组右侧明细必须展示“直接子组 + 直接点位”。之前这里只列 tags，导致真实 DB 子组
  // 只能在左树操作，右侧行既看不到它，也不可能提供 MES 流程入口。
  const groups = n.kind === 'device' ? (n.ref.groups || (n.ref.groups = [])) : (n.ref.subGroups || (n.ref.subGroups = []))
  const tags = n.ref.tags || (n.ref.tags = [])
  return {
    headers: ['名称', '节点类型 / 地址', '数据类型', '扫描ms', '点数 / 缩放', '说明'].map((label) => t(label)), rowKind: 'mixed', addLabel: t('新建标记…'),
    rows: containerRows(groups, tags, pathOf(n)),
  }
})
// 右侧明细的行：子组(结构体)和结构体数组点位可以展开看里面，平时收起
function containerRows(groups: any[], tags: any[], basePath: string): any[] {
  return [
    ...groups.map((g: any) => ({ selectionKey: uid(g), def: g, kind: 'group', holder: groups, sourcePath: `${basePath}.${g.name}`, c1: '📁 ' + g.name, c2: t('标记组 / PLC 数据块'), c3: '', c4: '', c5: t('{count} 点', { count: cnt(g) }), c6: g.description || '',
      hasChildren: cnt(g) > 0 || (g.subGroups?.length || 0) > 0 })),
    ...tags.map((tag: any) => ({ selectionKey: uid(tag), def: tag, kind: 'tag', holder: tags, sourcePath: `${basePath}.${tag.name}`, c1: tag.name, c2: tag.address, c3: tagTypeLabel(tag), c4: tag.scanRateMs, c5: tagPointLabel(tag), c6: tag.description || '',
      hasChildren: !!tag.array?.struct?.fields?.length })),
  ]
}
// 结构体数组点位展开后的成员(只读说明行)：名称、TIA 偏移(字节.位；数组按第一个元素)、类型
function schemaRows(rows: StructureRow[]): any[] {
  return rows.map((row) => ({ selectionKey: row.key, kind: 'schema', schema: row, c1: row.label, c2: row.offset, c3: row.type, c4: '', c5: '', c6: row.field?.description || '', hasChildren: row.hasChildren }))
}
const gridLoadedRows = new Map<string, any>()   // 已展开过的组/点位：修改后要刷新它们的子行
async function gridChildrenOf(row: any): Promise<any[]> {
  if (row.kind === 'group') {
    if (needsLoad(lazyState, row.def)) await ensureLoaded(row.def)
    return containerRows(row.def.subGroups || (row.def.subGroups = []), row.def.tags || (row.def.tags = []), row.sourcePath)
  }
  if (row.kind === 'tag') return schemaRows(schemaChildren(uid(row.def), row.def.array?.struct, addressByte(row.def.address)))
  if (row.kind === 'schema') return schemaRows(schemaChildren(row.schema.key, row.schema.struct, row.schema.baseByte ?? null))
  return []
}
async function loadGridChildren(row: any, _node: unknown, resolve: (rows: any[]) => void) {
  try {
    const children = await gridChildrenOf(row)
    if (row.kind !== 'schema') gridLoadedRows.set(gridRowKey(row), row)
    resolve(children)
  } catch (error) { lazyLoadFailed(error); resolve([]) }
}
// 切换左侧节点时整表重建(懒加载的子行缓存跟着清掉)；本节点内改了东西时刷新已展开的子行
const gridTableKey = computed(() => sel.value?.key || '')
watch(gridTableKey, () => gridLoadedRows.clear())
watch(() => ver.value, () => {
  for (const [key, row] of gridLoadedRows)
    void gridChildrenOf(row).then((children) => gridTableRef.value?.updateKeyChildren?.(key, children)).catch(() => {})
})
const visCols = computed(() => view.value.headers.map((h, i) => ({ h, i })).filter((x) => x.h))

const GRID_COLUMN_WIDTHS_KEY = 'igw.project.grid-column-widths.v1'
function loadGridColumnWidths(): Record<string, number> {
  try {
    const saved = JSON.parse(localStorage.getItem(GRID_COLUMN_WIDTHS_KEY) || '{}')
    return saved && typeof saved === 'object' ? saved : {}
  } catch { return {} }
}
const gridColumnWidths = ref<Record<string, number>>(loadGridColumnWidths())
function gridColumnKey(col: { i: number }): string { return `${view.value.rowKind}:${col.i}` }
function gridColumnWidth(col: { i: number }): number | undefined { return gridColumnWidths.value[gridColumnKey(col)] }
function onGridColumnResize(newWidth: number, _oldWidth: number, column: any) {
  const key = String(column?.columnKey || '')
  if (!key) return
  gridColumnWidths.value = { ...gridColumnWidths.value, [key]: Math.max(70, Math.round(newWidth)) }
  try { localStorage.setItem(GRID_COLUMN_WIDTHS_KEY, JSON.stringify(gridColumnWidths.value)) } catch { /* 浏览器禁用存储时仍保留本次页面宽度。 */ }
}

function gridRowKey(row: any): string { return row?.selectionKey || uid(row?.def) }
function gridRowClass({ row }: { row: any }): string {
  return gridSelectedKeys.value.has(gridRowKey(row)) ? 'clk project-grid-row-selected' : 'clk'
}
function selectOnlyGridRow(row: any) {
  const key = gridRowKey(row)
  gridSelectedKeys.value = new Set([key])
  gridCurrentKey.value = key
  curRow.value = row
  gridTableRef.value?.clearSelection?.()
  gridTableRef.value?.toggleRowSelection?.(row, true)
  gridTableRef.value?.setCurrentRow?.(row)
}
function onRowClick(row: any, _column: any, event: MouseEvent) {
  if (row?.kind === 'schema') return   // 结构成员说明行只读，不参与选择/编辑
  hideMenu(); activePane.value = 'grid'; curRow.value = row; gridCurrentKey.value = gridRowKey(row)
  const target = event?.target as HTMLElement | null
  if (target?.closest?.('.el-checkbox')) return
  if (event?.ctrlKey || event?.metaKey) {
    gridTableRef.value?.toggleRowSelection?.(row)
    gridTableRef.value?.setCurrentRow?.(row)
    return
  }
  selectOnlyGridRow(row)
}
function onRowDblClick(row: any) { if (row?.kind !== 'schema') editDef(row.def) }
function onSelChange(rows: any[]) {
  gridSel.value = rows
  gridSelectedKeys.value = new Set(rows.map(gridRowKey))
  if (rows.length) activePane.value = 'grid'
}

// ---------- 上下文能力(工具栏/菜单显隐)----------
const k = computed(() => sel.value?.kind || '')
const canNewChannel = computed(() => ['project', 'connectivity'].includes(k.value))
const canNewDevice = computed(() => k.value === 'channel')
const canNewGroupTag = computed(() => ['device', 'group'].includes(k.value))
const canPaste = computed(() => { void clipVer.value; return clip.length > 0 && !clipboardBusy.value })

// ---------- 剪贴板(客户端,大型组只序列化一次；剪切直接移动)----------
type Clip = ProjectClipboardItem
let clip: Clip[] = []
const clipVer = ref(0)
const clipboardBusy = ref<'' | 'copy' | 'cut' | 'paste'>('')
const clone = (o: any) => JSON.parse(JSON.stringify(o))
function uniqueName(base: string, existing: string[]): string {
  const set = new Set(existing); if (!set.has(base)) return base
  for (let i = 1; ; i++) { const n = base + '_' + i; if (!set.has(n)) return n }
}
// 当前操作目标:焦点在网格且有选中行 → 行;否则 → 树节点
function targets(): ProjectGridTarget[] {
  if (activePane.value === 'grid') {
    return resolveProjectGridTargets(view.value.rows, gridSelectedKeys.value, gridCurrentKey.value)
  }
  const n = sel.value
  return n && n.ref && n.kind !== 'project' && n.kind !== 'connectivity'
    ? [{ selectionKey: n.key, kind: n.kind, def: n.ref, holder: n.holder, sourcePath: pathOf(n) }]
    : []
}
async function doCopy() {
  if (clipboardBusy.value) return
  const selected = targets(); if (!selected.length) { ElMessage.info(t('先选中要复制的项')); return }
  clipboardBusy.value = 'copy'
  await nextPaint()
  try {
    await ensureTargetsDeepLoaded(selected)
    const prepared: Clip[] = []
    for (let index = 0; index < selected.length; index++) {
      prepared.push(...createProjectClipboardItems([selected[index]], 'copy'))
      if (index < selected.length - 1) await nextPaint()
    }
    clip = prepared; clipVer.value++
    ElMessage.success(t('已复制 {count} 项', { count: clip.length }))
  } catch (error: any) {
    ElMessage.error(t('复制失败：{message}', { message: error?.message || String(error) }))
  } finally { clipboardBusy.value = '' }
}
function removeTarget(target: ProjectGridTarget) {
  const holder = target.holder
  if (holder) {
    const index = holder.indexOf(target.def)
    if (index >= 0) { holder.splice(index, 1); return }
  }
  removeDef(target.kind, target.def)
}
async function doCut() {
  if (clipboardBusy.value || !projectEditable.value) return
  const selected = targets(); if (!selected.length) { ElMessage.info(t('先选中要剪切的项')); return }
  clipboardBusy.value = 'cut'
  try {
    await nextPaint()
    await ensureTargetsDeepLoaded(selected)
    const before = projectShape(proj.value)
    clip = createProjectClipboardItems(selected, 'cut'); clipVer.value++
    for (const x of selected) removeTarget(x)
    gridSelectedKeys.value = new Set(); gridCurrentKey.value = ''; gridSel.value = []; curRow.value = null
    markConfirmedDeletion(before); bump(); ElMessage.success(t('已剪切 {count} 项', { count: clip.length }))
  } finally { clipboardBusy.value = '' }
}
async function doPaste() {
  if (!clip.length || clipboardBusy.value || !projectEditable.value) return
  clipboardBusy.value = 'paste'
  await nextPaint()
  let n = 0
  const remaining: Clip[] = []
  try {
    for (let index = 0; index < clip.length; index++) {
      const item = clip[index]
      if (pasteOne(item)) n++
      else remaining.push(item)
      if (index < clip.length - 1) await nextPaint()
    }
    if (clip.some((item) => item.mode === 'cut')) { clip = remaining; clipVer.value++ }
    if (n) { markDirty(); bump(); ElMessage.success(t('已粘贴 {count} 项', { count: n })) }
    else ElMessage.warning(t('粘贴目标不匹配：通道粘到「连接」，设备粘到通道，标记/组粘到设备或组'))
  } finally { clipboardBusy.value = '' }
}
function pasteOne(it: Clip): boolean {
  const n = sel.value
  if (it.kind === 'channel') {
    const nc = materializeProjectClipboardItem(it); nc.name = uniqueName(nc.name || t('通道'), proj.value.channels.map((x: any) => x.name)); nc.id = ''
    proj.value.channels.push(nc); finishMovedPaste(it, nc.name); return true
  }
  if (it.kind === 'device') {
    const ch = k.value === 'channel' ? n.ref : n?.channel
    if (!ch) return false
    const nd = materializeProjectClipboardItem(it); nd.name = uniqueName(nd.name || t('设备'), (ch.devices || []).map((x: any) => x.name)); nd.id = ''
    ;(ch.devices ||= []).push(nd)
    const channelNode = treeNodeForDef(ch)
    finishMovedPaste(it, `${channelNode ? pathOf(channelNode) : ch.name}.${nd.name}`)
    return true
  }
  if (it.kind === 'tag') {
    if (!['device', 'group'].includes(k.value)) return false
    const list = n.ref.tags ||= []
    const nt = materializeProjectClipboardItem(it); nt.name = uniqueName(nt.name || t('标记'), list.map((x: any) => x.name)); nt.id = ''
    list.push(nt); finishMovedPaste(it, `${pathOf(n)}.${nt.name}`); return true
  }
  if (it.kind === 'group') {
    if (!['device', 'group'].includes(k.value)) return false
    const list = k.value === 'device' ? (n.ref.groups ||= []) : (n.ref.subGroups ||= [])
    const ng = materializeProjectClipboardItem(it); ng.name = uniqueName(ng.name || t('组'), list.map((x: any) => x.name)); ng.id = ''
    list.push(ng); finishMovedPaste(it, `${pathOf(n)}.${ng.name}`); return true
  }
  return false
}
// 从工程里删掉一个 def
function removeDef(kind: string, def: any) {
  if (kind === 'channel') { const a = proj.value.channels; const i = a.indexOf(def); if (i >= 0) a.splice(i, 1); return }
  if (kind === 'device') { for (const c of proj.value.channels) { const i = (c.devices || []).indexOf(def); if (i >= 0) { c.devices.splice(i, 1); return } } return }
  if (kind === 'tag') { const h = ownerTags(def); if (h) { const i = h.indexOf(def); if (i >= 0) h.splice(i, 1) } return }
  if (kind === 'group') { const h = ownerGroupArr(def); if (h) { const i = h.indexOf(def); if (i >= 0) h.splice(i, 1) } return }
}
// 找某标记所在的 tags 数组(遍历工程)
function ownerTags(t: any): any[] | null {
  for (const c of proj.value.channels) for (const d of c.devices || []) {
    if ((d.tags || []).includes(t)) return d.tags
    const r = walkGroups(d.groups || [], (g) => (g.tags || []).includes(t) ? g.tags : null); if (r) return r
  }
  return null
}
function ownerGroupArr(g: any): any[] | null {
  for (const c of proj.value.channels) for (const d of c.devices || []) {
    if ((d.groups || []).includes(g)) return d.groups
    const r = walkGroups(d.groups || [], (x) => (x.subGroups || []).includes(g) ? x.subGroups : null); if (r) return r
  }
  return null
}
function walkGroups(gs: any[], hit: (g: any) => any): any {
  for (const g of gs) { const r = hit(g); if (r) return r; const r2 = walkGroups(g.subGroups || [], hit); if (r2) return r2 }
  return null
}

// ---------- 删除 ----------
async function doDelete() {
  if (!projectEditable.value) return
  try { return await projectOperation.run('doDelete', t('正在删除所选配置…'), async () => {
  const selected = targets(); if (!selected.length) { ElMessage.info(t('先选中要删除的项')); return }
  const label = selected.length === 1 ? `「${selected[0].def.name}」` : t('选中的 {count} 项', { count: selected.length })
  const treeNode = activePane.value === 'tree'
  try { await ElMessageBox.confirm(t('删除 {label}{suffix}？', { label, suffix: treeNode ? t('及其下全部内容') : '' }), t('删除'), { type: 'warning' }) } catch { return }
  const before = projectShape(proj.value)
  for (const x of selected) removeDef(x.kind, x.def)
  // 大工程只载入了部分标记组，不能按“本页找不到”判断流程已成孤儿。
  const removedTasks = lazyMode.value ? [] : pruneOrphanMesTasks(proj.value)
  if (treeNode) sel.value = null
  gridSel.value = []; curRow.value = null; gridSelectedKeys.value = new Set(); gridCurrentKey.value = ''; markConfirmedDeletion(before); bump()
  ElMessage.success(removedTasks.length
    ? t('已删除 {count} 项，并同步移除 {flows} 个采集流程', { count: selected.length, flows: removedTasks.length })
    : t('已删除 {count} 项', { count: selected.length }))
  }) } catch (error: any) { ElMessage.error(error?.response?.data?.error || error?.message || String(error)) }
}

// ---------- 新建 ----------
function newChannel() { if (projectEditable.value) openChannelWizard() }
function newDevice() {
  if (!projectEditable.value) return
  const ch = k.value === 'channel' ? sel.value.ref : sel.value?.channel
  if (!ch) { ElMessage.info(t('请先选中一个通道')); return }
  openDeviceWizard(ch)
}
function newGroup() {
  if (!projectEditable.value) return
  const n = sel.value; if (!['device', 'group'].includes(k.value)) { ElMessage.info(t('请在设备或组上新建')); return }
  const list = k.value === 'device' ? (n.ref.groups ||= []) : (n.ref.subGroups ||= [])
  openGroupDialog(null, list)
}
function newTag() {
  if (!projectEditable.value) return
  const n = sel.value; if (!['device', 'group'].includes(k.value)) { ElMessage.info(t('请在设备或组上新建标记')); return }
  openTagDialog(null, (n.ref.tags ||= []))
}
// 网格空白/行 右键的新建(按当前视图类型)
function newByView() {
  if (!projectEditable.value) return
  if (view.value.rowKind === 'channel') newChannel()
  else if (view.value.rowKind === 'device') newDevice()
  else newTag()
}

// 双击/属性:按 def 类型打开对应编辑
function editDef(def: any) {
  if (!isChannel(def) && !isDevice(def) && !isTag(def) && needsLoad(lazyState, def)) {
    void ensureLoaded(def).then(() => openGroupDialog(def, null)).catch(lazyLoadFailed)
    return
  }
  if (isChannel(def)) openChannelProps(def)
  else if (isDevice(def)) openDeviceDialog(def, null)
  else if (isTag(def)) openTagDialog(def, null)
  else openGroupDialog(def, null)
}
const isChannel = (d: any) => d && 'driverId' in d && 'devices' in d
const isDevice = (d: any) => d && 'endpoint' in d
const isTag = (d: any) => d && 'address' in d && 'dataType' in d
function editSelected() { if (sel.value?.ref && !['project', 'connectivity'].includes(k.value)) editDef(sel.value.ref) }
// 树节点双击=改属性(对齐桌面;工程节点=工程属性)
function onTreeDbl(data: any) {
  if (data?.kind === 'loading') return
  pickNode(data)
  if (data.kind === 'project') openProjProps()
  else if (data.kind !== 'connectivity') editDef(data.ref)
}

// ===================== 上下文菜单 =====================
const menu = reactive<{ show: boolean; x: number; y: number; items: any[] }>({ show: false, x: 0, y: 0, items: [] })
function hideMenu() { menu.show = false }
// 只能看：去掉会修改工程的菜单项(edit: true)，再收掉多余的分隔线
function visibleMenuItems(items: any[]): any[] {
  if (projectEditable.value) return items
  const out: any[] = []
  for (const it of items) if (!it.edit && (!it.sep || (out.length && !out[out.length - 1].sep))) out.push(it)
  while (out.length && out[out.length - 1].sep) out.pop()
  return out
}
function showMenu(e: MouseEvent, all: any[]) {
  e.preventDefault()
  const items = visibleMenuItems(all)
  if (!items.length) return
  menu.items = items
  menu.x = Math.min(e.clientX, window.innerWidth - 200)
  menu.y = Math.min(e.clientY, window.innerHeight - items.length * 30 - 10)
  menu.show = true
}
const SEP = { sep: true }
// 「查看日志」：带着工程路径打开日志中心，查这一项及其下级最近 24 小时的全部日志。
// 绑了采集流程的组再给一项按任务查 MES 上报日志。没有日志中心查看权限的账号不显示。
const router = useRouter()
function logMenuItems(path: string, taskId?: string): any[] {
  if (!path || !canView('logs')) return []
  return [
    { label: t('查看日志'), act: () => void router.push(logCenterLink({ path })) },
    ...(taskId ? [{ label: t('查看采集上报日志'), act: () => void router.push(logCenterLink({ taskId })) }] : []),
    SEP,
  ]
}
function clickItem(it: any) { if (it.disabled || it.sep || (it.edit && !projectEditable.value)) return; menu.show = false; it.act?.() }

function supportsSiemensPlus(node: any): boolean {
  if (node?.kind !== 'device' || node.channel?.driverId !== 'Siemens.S7') return false
  const model = String(node.ref?.settings?.model || 'S7-1200').replace(/\s+/g, '').toUpperCase()
  return model === 'S7-1200' || model === 'S7-1500'
}

function siemensAuthMenuLabel(node: any): string {
  const status = findRuntimeDevice(runtimeDevices.value, node.channel?.name || '', node.ref?.name || '')
  if (status?.detailState === 'AuthenticationRequired' || status?.detailState === 'AuthenticationFailed')
    return t('⚠ PLC 需要身份验证…')
  if (node.ref?.settings?.authMode === 'credential') return t('PLC 身份验证（本机已配置）…')
  return t('PLC 身份验证…')
}

async function openSiemensAuthentication(node: any) {
  if (dirty.value && !(await autoSave.flush())) {
    ElMessage.error(t('工程配置尚未保存，暂不能验证 PLC：') + (saveError.value || t('请稍后重试')))
    return
  }
  siemensAuthNode.value = node
  siemensAuthDeviceName.value = `${node.channel?.name || ''}.${node.ref?.name || ''}`.replace(/^\./, '')
  siemensAuthOpen.value = true
}

async function syncSiemensAuthentication() {
  const node = siemensAuthNode.value
  try {
    const current = lazyMode.value
      ? await projectApi.shell().then(shell => ({ proj: shell.project, rev: shell.rev }))
      : await api.project()
    const remoteChannel = (current.proj?.channels || []).find((channel: any) => channel.name === node?.channel?.name)
    const remoteDevice = (remoteChannel?.devices || []).find((device: any) => device.name === node?.ref?.name)
    if (node?.ref && remoteDevice) {
      node.ref.settings ||= {}
      const mode = remoteDevice.settings?.authMode || 'anonymous'
      node.ref.settings.authMode = mode
      if (remoteDevice.settings?.credentialRef) node.ref.settings.credentialRef = remoteDevice.settings.credentialRef
      else delete node.ref.settings.credentialRef
      // 认证设置已与服务端一致：只刷新这台设备的保存基准，其余未保存修改照常保留
      refreshBaselineNode(lazyState, 'device', node.ref, node.channel?.id || null)
      rev.value = current.rev
    }
  } catch { /* 状态轮询仍会刷新认证结果；不重置工程树和当前选择。 */ }
  await pollRuntimeStatus()
}

// MES 任务绑定到数据源的真实主目录边界：
// - PLC 按第一个直接拥有点位的 DB 组识别，STRUCT/成员组不能重复绑定；
// - OPC UA 按设备下一级顶层目录识别，内部结构分支不能被拆成多个流程；
// - 旧工程里已经存在任务的路径始终允许进入，避免历史配置失去管理入口。
function isPrimaryMesBoundaryGroup(node: any): boolean {
  if (node?.kind !== 'group') return false
  let depth = 0
  let ancestorHasDirectTags = false
  for (let p = node.parent; p?.kind === 'group'; p = p.parent) {
    depth++
    if ((p.ref?.tags || []).length) ancestorHasDirectTags = true
  }
  return isMesTaskBoundaryGroup(node.channel?.driverId || '', node.ref, depth, ancestorHasDirectTags)
}
function isMesBoundaryGroup(node: any): boolean {
  if (node?.kind !== 'group') return false
  if (taskFor(pathOf(node))) return true
  let depth = 0
  let ancestorHasDirectTags = false
  for (let p = node.parent; p?.kind === 'group'; p = p.parent) {
    depth++
    if ((p.ref?.tags || []).length) ancestorHasDirectTags = true
  }
  return isMesTaskBoundaryGroup(node.channel?.driverId || '', node.ref, depth,
    ancestorHasDirectTags, isPrimaryMesBoundaryGroup(node.parent))
}
function finishMovedPaste(item: Clip, newPath: string) {
  if (!item.sourcePath) return
  migrateMesRefs(item.sourcePath, newPath)
  delete item.sourcePath
}

async function openMesFlowFor(node: any) {
  if (mesFlowOpen.value && mesScopeOwner && mesScopeOwner !== proj.value) {
    ElMessage.warning(t('工程已重新载入，请关闭并重新打开采集流程后再监控'))
    return
  }
  if (!mesFlowOpen.value) { mesScopeRenames = []; mesScopeOwner = proj.value }
  const generation = ++mesFlowOpenGeneration
  mesFlowGroup.value = pathOf(node)
  mesFlowPreparing.value = true
  mesFlowOpen.value = true
  // 流程画布包含任务列表、画布和节点配置三栏。首次按普通窗口打开会在高 DPI 下挤成窄条，
  // 因此从工程配置进入时默认占满可用工作区；用户仍可用标题栏按钮还原并自由拖放/缩放。
  prepareToolOpen('mes', true)
  // 先让最大化工作窗和准备状态完成一次绘制，再做大型工程的保存/热重载。
  // 避免 structuredClone、结构安全检查及网络往返期间用户误以为点击没有响应。
  await nextTick()
  await nextPaint()
  // 准备态已经卸载旧画布；新的画布将读取当前工程，旧路径映射不再适用。
  mesScopeRenames = []
  if (generation !== mesFlowOpenGeneration || !mesFlowOpen.value) return
  if (dirty.value && !(await autoSave.flush())) {
    if (generation !== mesFlowOpenGeneration) return
    mesFlowPreparing.value = false
    mesFlowOpen.value = false
    ElMessage.error(t('工程配置尚未保存，暂不能打开采集流程：') + (saveError.value || t('请稍后重试')))
    return
  }
  if (generation === mesFlowOpenGeneration && mesFlowOpen.value) mesFlowPreparing.value = false
}

function nextPaint(): Promise<void> {
  return paintProjectFeedback()
}

function mesMenuItems(node: any): any[] {
  if (!isMesBoundaryGroup(node))
    return [{ label: t('MES 采集流程(请在所属 PLC 数据块上设置)'), disabled: true }]
  const currentTask = taskFor(pathOf(node))
  const flowDisabled = isMesFlowConfigurationDisabled(currentTask)
  const conflict = currentTask && !flowDisabled && !currentTask.enabled
    ? findMesTaskEnableConflict(proj.value?.mesUpload, {
      groupRef: currentTask.groupRef,
      id: currentTask.id,
    })
    : ''
  const switchLabel = conflict
    ? t('采集上报(MES,需先停用父级或子级任务)') + `：${conflict}`
    : t('采集上报(MES,本数据块单独开关)')
  return [
    { label: (currentTask?.enabled ? '✓ ' : '') + switchLabel, act: () => toggleCollect(node), disabled: flowDisabled || !!conflict, edit: true },
    {
      label: t('MES 采集流程(当前数据块)…'),
      act: () => openMesFlowFor(node),
      disabled: isMesFlowConfigurationDisabled(currentTask),
    },
  ]
}

// 把右侧明细行的 def 找回带 parent/channel/device 的树节点，确保路径和边界判断与左树完全一致。
function treeNodeForDef(def: any): any | null {
  const walk = (nodes: any[]): any | null => {
    for (const n of nodes) {
      if (n.ref === def) return n
      const hit = walk(n.children || [])
      if (hit) return hit
    }
    return null
  }
  return walk(treeData.value)
}

// 树右键
async function onTreeCtx(e: MouseEvent, data: any) {
  e.preventDefault()
  if (data?.kind === 'loading') return
  pickNode(data)
  // 采集流程边界要看本组是否直接有点位，先载入本组下级再给菜单。
  if (needsLoad(lazyState, data.ref)) {
    try { await ensureLoaded(data.ref) } catch (error) { lazyLoadFailed(error); return }
  }
  nextTick(() => treeRef.value?.setCurrentKey(data.key))
  let items: any[] = []
  // edit: true = 修改工程的菜单项，只能看时不显示
  const cut = { label: t('剪切'), gesture: 'Ctrl+X', act: doCut, edit: true }
  const copy = { label: t('复制'), gesture: 'Ctrl+C', act: doCopy }
  const paste = { label: t('粘贴'), gesture: 'Ctrl+V', act: doPaste, disabled: !canPaste.value, edit: true }
  const del = { label: t('删除'), gesture: 'Del', danger: true, act: doDelete, edit: true }
  const props = { label: t('属性…'), act: editSelected }
  const qc = { label: t('在此处打开点位监控(Quick Client)'), act: () => quickClient(data) }
  if (data.kind === 'project') items = [{ label: t('新建通道…'), act: newChannel, edit: true }, SEP, qc, SEP, { label: t('属性…'), act: openProjProps }]
  else if (data.kind === 'connectivity') items = [{ label: t('新建通道…'), act: newChannel, edit: true }, SEP, qc, SEP, paste]
  else if (data.kind === 'channel') items = [
    { label: t('新建设备…'), act: newDevice, edit: true }, SEP,
    { label: t('一键全开采集(本通道所有主标记组)'), act: () => enableAllCollect(data), edit: true },
    { label: t('一键全关采集(本通道)'), act: () => disableAllCollect(data), edit: true }, SEP,
    ...(monitorEditable.value ? [{ label: t(channelReconnectBusy(data.ref?.name) ? '正在重连…' : '立即重连'), act: () => reconnectChannel(data), disabled: channelReconnectBusy(data.ref?.name) }, SEP] : []),
    cut, copy, paste, del, SEP,
    { label: t('诊断…'), act: () => openDiag(data) }, SEP,
    ...logMenuItems(pathOf(data)),
    qc, props]
  else if (data.kind === 'device') items = [
    { label: t('新建标记组'), act: newGroup, edit: true }, { label: t('新建标记'), act: newTag, edit: true },
    ...(data.channel?.driverId === 'OpcUa.Client' || supportsSiemensPlus(data) ? [{ label: t('自动生成标记…'), act: () => genTags(data) }] : []), SEP,
    { label: t('一键开启直属组采集流程…'), act: () => enableDeviceFirstLevelFlows(data), edit: true }, SEP,
    ...(supportsSiemensPlus(data) ? [{ label: siemensAuthMenuLabel(data), act: () => openSiemensAuthentication(data) }, SEP] : []),
    { label: t('导入 CSV…'), act: importCsv, edit: true },
    ...(isSiemensNode(data) ? [{ label: t('从 PLC 文件导入(西门子 DB)…'), act: importSiemensDb, edit: true }] : []),
    { label: t('导出 CSV'), act: exportCsv, disabled: !(data.ref.tags || []).length }, SEP,
    cut, copy, paste, del, SEP,
    ...logMenuItems(pathOf(data)),
    qc, props]
  else {
    items = [
    { label: t('新建子组'), act: newGroup, edit: true }, { label: t('新建标记'), act: newTag, edit: true }, SEP,
    ...mesMenuItems(data), SEP,
    { label: t('导入 CSV…'), act: importCsv, edit: true },
    ...(isSiemensNode(data) ? [{ label: t('从 PLC 文件导入(西门子 DB)…'), act: importSiemensDb, edit: true }] : []),
    { label: t('导出 CSV'), act: exportCsv, disabled: !(data.ref.tags || []).length }, SEP,
    cut, copy, paste, del, SEP,
    ...logMenuItems(pathOf(data), taskFor(pathOf(data))?.id),
    qc, props]
  }
  showMenu(e, items)
}
// 网格右键(行)
async function onRowCtx(row: any, _col: any, e: MouseEvent) {
  e.preventDefault()
  ;(e as any)._rowHandled = true
  if (row?.kind === 'schema') return
  if (row.kind === 'group' && needsLoad(lazyState, row.def)) {
    try { await ensureLoaded(row.def) } catch (error) { lazyLoadFailed(error); return }
  }
  if (!gridSelectedKeys.value.has(gridRowKey(row))) selectOnlyGridRow(row)
  else { curRow.value = row; gridCurrentKey.value = gridRowKey(row); gridTableRef.value?.setCurrentRow?.(row) }
  activePane.value = 'grid'
  const common = [
    { label: '粘贴', gesture: 'Ctrl+V', act: doPaste, disabled: !canPaste.value, edit: true }, SEP,
    { label: '剪切', gesture: 'Ctrl+X', act: doCut, edit: true },
    { label: '复制', gesture: 'Ctrl+C', act: doCopy },
    { label: '删除', gesture: 'Del', danger: true, act: doDelete, edit: true }, SEP,
    { label: '属性…', act: () => editDef(row.def) },
  ]
  let items: any[]
  if (row.kind === 'group') {
    const node = treeNodeForDef(row.def)
    items = [
      { label: '新建子组', act: () => openGroupDialog(null, (row.def.subGroups ||= [])), edit: true },
      { label: '新建标记', act: () => openTagDialog(null, (row.def.tags ||= [])), edit: true }, SEP,
      ...(node ? mesMenuItems(node) : [{ label: 'MES 采集流程(无法解析节点路径)', disabled: true }]), SEP,
      ...(node ? [{ label: '在此处打开点位监控(Quick Client)', act: () => quickClient(node) }, SEP] : []),
      ...logMenuItems(row.sourcePath, taskFor(row.sourcePath)?.id),
      ...common,
    ]
  } else items = [
    { label: view.value.addLabel, act: newByView, edit: true },
    ...logMenuItems(row.sourcePath),
    ...common,
  ]
  showMenu(e, items)
}
// 网格空白右键
function onGridCtx(e: MouseEvent) {
  if ((e as any)._rowHandled) return
  if (!sel.value) return
  activePane.value = 'grid'
  showMenu(e, [{ label: view.value.addLabel, act: newByView, edit: true }, { label: t('粘贴'), gesture: 'Ctrl+V', act: doPaste, disabled: !canPaste.value, edit: true }])
}

// ===================== 键盘 =====================
function onKey(e: KeyboardEvent) {
  if (operationBusy.value || saving.value || clipboardBusy.value) return
  const t = e.target as HTMLElement
  if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.isContentEditable)) return
  if (anyDialogOpen.value) return
  if (!proj.value) return
  if (e.ctrlKey && e.key.toLowerCase() === 'c') { doCopy(); e.preventDefault() }
  else if (!projectEditable.value) return   // 只能看：只保留复制
  else if (e.key === 'Delete') { doDelete(); e.preventDefault() }
  else if (e.ctrlKey && e.key.toLowerCase() === 'x') { doCut(); e.preventDefault() }
  else if (e.ctrlKey && e.key.toLowerCase() === 'v') { doPaste(); e.preventDefault() }
}

// ===================== 对话框 =====================
const typeOpts = ['bool', 'byte', 'int16', 'uint16', 'int32', 'uint32', 'int64', 'uint64', 'float', 'double', 'string', 'wString', 'char', 'bcd', 'lBcd', 'byteArray']
const accessOpts = computed(() => [{ v: 'read', l: t('只读') }, { v: 'write', l: t('只写') }, { v: 'readWrite', l: t('读写') }])
const deadbandOpts = computed(() => [{ v: 'none', l: t('无') }, { v: 'absolute', l: t('绝对值') }, { v: 'percentOfRange', l: t('量程百分比') }])

// ========== 添加向导(通道/设备,对齐桌面 ChannelWizard / DeviceWizard:多步 + 汇总) ==========
// 通道类型清单(与桌面 ChannelWizard.DriverList 一字不差:KEP 风格显示名 → 驱动 id)
const CH_TYPES = [
  { name: 'Modbus TCP/IP Ethernet', id: 'Modbus.Tcp' },
  { name: 'Siemens TCP/IP Ethernet', id: 'Siemens.S7' },
  { name: 'Omron FINS Ethernet', id: 'Omron.Fins' },
  { name: 'Omron NJ Ethernet', id: 'Omron.Cip' },
  { name: 'Mitsubishi Ethernet', id: 'Mitsubishi.Mc' },
  { name: 'Allen-Bradley ControlLogix Ethernet', id: 'AB.Eip' },
  { name: 'OPC UA Client', id: 'OpcUa.Client' },
  { name: 'MQTT Client', id: 'Mqtt.Client' },
  { name: 'Simulator', id: 'Simulator' },
]
const NETWORK_BINDING_DRIVERS = CH_TYPES.filter((item) => item.id !== 'Simulator').map((item) => item.id)
const DEFAULT_ADAPTER_VALUE = '__os_default__'
const wiz = reactive<any>({ open: false, kind: '', step: 0, steps: [], f: {}, driverId: '', driverName: '', channelName: '', targetCh: null })
const wizInstr = computed<Record<string, string>>(() => ({
  type: t('选择要创建的通道类型:'), name: t('指定此对象的标识。'),
  net: t('用于指定: 要绑定的网络适配器的名称,或者允许操作系统选择默认项。'),
  write: t('当写队列中存在多个写操作时,选择如何将写操作数据传递到底层的通信驱动程序。'),
  float: t('选择如何将无效的浮点数发送到客户端。'),
  dname: t('指定此对象的标识。'), model: t('用于选择: 与此 ID 关联的设备的特定类型。'),
  id: t('指定设备的驱动程序特定站点或节点(设备 IP / 端点)。'),
  scan: t('用于指定: 确定扫描设备中标记的频率所用方法。'),
  timeout: t('通信超时设置(连接 / 请求 / 重试 / 间隔)。'),
  demote: t('自动降级:连续通信失败达阈值后,自动停采该设备一段时间再重试。'),
  s7addr: t('选择 16 位和 32 位值的字节顺序。大端字节序是 Siemens S7 控制器的默认值。'),
  mbunit: t('Modbus 从站号(Unit Id)。'), fins: t('FINS 节点号(目标节点留空=取 IP 末位;源节点为本网关节点)。'),
  protocol: t('配置当前协议的会话、地址和传输参数。'),
  abparam: t('Allen-Bradley PLC 型号与 CPU 路径(ControlLogix 背板槽=1,0)。'),
  ua: t('OPC UA 服务器端点属于通道；通道下设备共享这一连接，设备仅用于组织导入的地址结构。'),
  summary: t('正在完成向导。下面是汇总,单击「完成」创建。'),
}))
const wizStepKey = computed(() => wiz.steps[wiz.step])
const wizIsLast = computed(() => wiz.step === wiz.steps.length - 1)
const wizDriverDisplay = computed(() => CH_TYPES.find((t) => t.id === wiz.driverId)?.name || wiz.driverName)
const wizAdapterOptions = computed(() => adapterOptions(networkAdapters.value, { networkAdapter: '默认值' }))
const wizAdapterLabel = computed(() => {
  if (!wiz.f.net || wiz.f.net === DEFAULT_ADAPTER_VALUE) return t('默认值（由操作系统选择）')
  return wizAdapterOptions.value.find((option) => option.value === wiz.f.net)?.label || t('配置的网卡当前不可用')
})
async function openChannelWizard() {
  try { return await projectOperation.run('openChannelWizard', t('正在准备通道向导…'), async () => {
  await loadConfigurationLookups(true)
  wiz.kind = 'channel'; wiz.step = 0; wiz.driverId = CH_TYPES.find((item) => !isUnlicensedDriver(item.id))?.id || 'Simulator'
  wiz.steps = channelWizardSteps(wiz.driverId)
  wiz.f = {
    name: uniqueName(t('通道'), (proj.value.channels || []).map((c: any) => c.name)),
    net: DEFAULT_ADAPTER_VALUE, writeOpt: '仅写入非布尔型标记的最新值', duty: '10', float: '替换为零',
    uaUrl: '', uaSecurity: 'None', uaAnonymous: true,
    uaUsername: '', uaPassword: '', uaTimeoutMs: '10000',
  }
  wiz.open = true
  }) } catch (error: any) { ElMessage.error(error?.response?.data?.error || error?.message || String(error)) }
}
function channelWizardSteps(driverId: string) {
  if (driverId === 'OpcUa.Client') return ['type', 'name', 'ua', 'net', 'summary']
  return NETWORK_BINDING_DRIVERS.includes(driverId)
    ? ['type', 'name', 'net', 'write', 'float', 'summary']
    : ['type', 'name', 'write', 'float', 'summary']
}
async function openDeviceWizard(ch: any) {
  try { return await projectOperation.run('openDeviceWizard', t('正在准备设备向导…'), async () => {
  if (isUnsupportedDriver(ch.driverId)) { ElMessage.warning(t('本版本不支持该通道的驱动，不能再添加设备。')); return }
  await loadDrivers(true)
  wiz.kind = 'device'; wiz.step = 0; wiz.targetCh = ch; wiz.driverId = ch.driverId; wiz.channelName = ch.name
  wiz.driverName = drivers.value.find((d) => d.driverId === ch.driverId)?.displayName || ch.driverId
  const s = ['dname']
  if (ch.driverId === 'Siemens.S7') s.push('model')
  if (ch.driverId !== 'OpcUa.Client') s.push('id')
  s.push('scan')
  if (ch.driverId !== 'Modbus.Tcp') s.push('timeout')
  s.push('demote')
  if (ch.driverId === 'Siemens.S7') s.push('s7addr')
  else if (ch.driverId === 'Modbus.Tcp') s.push('protocol')
  else if (ch.driverId === 'Omron.Fins') s.push('fins')
  else if (ch.driverId === 'AB.Eip') s.push('abparam')
  s.push('summary')
  wiz.steps = s
  wiz.f = {
    name: uniqueName(t('设备'), (ch.devices || []).map((x: any) => x.name)), id: ch.driverId === 'OpcUa.Client' ? '' : ch.driverId === 'Modbus.Tcp' ? '192.168.0.1:502' : '192.168.0.1', model: 'S7-1200',
    scanMode: '遵循客户端指定的扫描速率', initCache: '禁用',
    connTimeout: '3', reqTimeout: '2000', retries: '2', delay: '0',
    demote: '禁用', demoteAfter: '3', demotePeriod: '10000',
    byteOrder: '大端字节序', unitId: '1', destNode: '', srcNode: '1', abPlc: 'ControlLogix', abPath: '1,0',
    protocolSettings: { unitId: '1', timeoutMs: '2000', zeroBased: 'false', wordOrder: 'ABCD', regBlock: '120', coilBlock: '512', writeMode: 'auto' },
  }
  wiz.open = true
  }) } catch (error: any) { ElMessage.error(error?.response?.data?.error || error?.message || String(error)) }
}
function wizBack() { if (wiz.step > 0) wiz.step-- }
function wizNext() {
  const key = wizStepKey.value
  if (key === 'name' && !String(wiz.f.name).trim()) { ElMessage.warning(t('请输入通道名称')); return }
  if (key === 'dname' && !String(wiz.f.name).trim()) { ElMessage.warning(t('请输入设备名称')); return }
  if (key === 'type' && wiz.kind === 'channel') wiz.steps = channelWizardSteps(wiz.driverId)
  if (!wizIsLast.value) { wiz.step++; return }
  wizFinish()
}
async function wizFinish() {
  if (!projectEditable.value) return
  try { return await projectOperation.run('wizFinish', t('正在创建配置…'), async () => {
  const f = wiz.f
  if (wiz.kind === 'channel') {
    if (wiz.driverId === 'OpcUa.Client') {
      f.uaUrl = normalizeOpcUrl(f.uaUrl)
      if (!f.uaUrl) { ElMessage.warning(t('请填写 UA 服务器地址')); return }
      if (!f.uaUrl.startsWith('opc.tcp://')) { ElMessage.warning(t('UA 服务器地址必须使用 opc.tcp://')); return }
    }
    let settings: any = wiz.driverId === 'OpcUa.Client'
      ? {
          url: normalizeOpcUrl(f.uaUrl), securityPolicy: f.uaSecurity || 'None', anonymous: f.uaAnonymous ? 'true' : 'false',
          username: String(f.uaUsername || '').trim(), password: f.uaPassword || '', timeoutMs: String(f.uaTimeoutMs || '10000').trim(),
          diagnostics: '禁用', batchingMode: 'auto',
        }
      : { wordOrder: 'ABCD', timeoutMs: '2000', batchingMode: 'auto',
          writeOptimization: f.writeOpt, dutyCycle: String(f.duty || '10').trim(), nonNormalFloat: f.float, diagnostics: '禁用' }
    if (NETWORK_BINDING_DRIVERS.includes(wiz.driverId))
      settings = applyAdapterValue(settings, f.net === DEFAULT_ADAPTER_VALUE ? '' : f.net, networkAdapters.value)
    proj.value.channels.push({
      id: '', name: String(f.name).trim(), driverId: wiz.driverId, description: '',
      settings,
      devices: [],
    })
    wiz.open = false; markDirty(); bump(); saveAfterConfirm(); ElMessage.success(t('通道已创建(右键通道「新建设备」加设备)'))
    return
  }
  // device
  if (wiz.driverId !== 'OpcUa.Client' && !String(f.id).trim()) { ElMessage.warning(t('请填写设备 ID(IP / 端点)')); return }
  const s: any = {
    scanMode: f.scanMode, initialFromCache: f.initCache, connTimeout: String(f.connTimeout).trim(), timeoutMs: String(f.reqTimeout).trim(),
    retries: String(f.retries).trim(), requestDelay: String(f.delay).trim(), dataCollection: '启用',
    demote: f.demote, demoteAfter: String(f.demoteAfter || '3').trim(), demotePeriodMs: String(f.demotePeriod || '10000').trim(),
  }
  const id = wiz.driverId
  if (id === 'Siemens.S7') { const [r, sl] = rackSlot(f.model); s.model = f.model; s.rack = String(r); s.slot = String(sl); s.byteOrder = f.byteOrder === '小端字节序' ? 'little' : 'big' }
  else if (id === 'Modbus.Tcp') {
    const error = validateProtocolSettings(id, f.protocolSettings)
    if (error) { ElMessage.warning(error); return }
    Object.assign(s, f.protocolSettings)
  }
  else if (id === 'Omron.Fins') { if (String(f.destNode).trim()) s.destNode = String(f.destNode).trim(); s.srcNode = String(f.srcNode || '1').trim() }
  else if (id === 'AB.Eip') { s.plcType = f.abPlc; s.path = String(f.abPath).trim() }
  ;(wiz.targetCh.devices ||= []).push({ id: '', name: String(f.name).trim(), endpoint: wiz.driverId === 'OpcUa.Client' ? '' : String(f.id).trim(), description: '', settings: s, groups: [], tags: [] })
  wiz.open = false; markDirty(); bump(); saveAfterConfirm(); ElMessage.success(t('设备已创建'))
  }) } catch (error: any) { ElMessage.error(error?.response?.data?.error || error?.message || String(error)) }
}

// ===== 通道/设备属性:对齐桌面 PropertyEditorDialog(左分类列表+右字段页;combo 存显示原串,与桌面同键同值)=====
const ETH_DRIVERS = NETWORK_BINDING_DRIVERS
const MODEL_RS: any = { 'S7-300': [0, 2], 'S7-400': [0, 2] }   // 其余型号 → (0,1)
const rackSlot = (model: string) => MODEL_RS[model] || [0, 1]

// -- 通道属性(编辑;表单副本,确定才写回 → 取消不脏数据) --
const chPropsOpen = ref(false)
const chPropsRef = ref<any>(null)     // 原对象(确定时写回)
const chForm = ref<any>({})           // 编辑副本 {name, description, settings}
const chAdapterSelection = ref('')
const chAdapterOptions = computed(() => adapterOptions(networkAdapters.value, chForm.value?.settings || {}))
const chCat = ref('general')
const chPropsDriver = computed(() => drivers.value.find((d) => d.driverId === chPropsRef.value?.driverId))
const chCats = computed(() => {
  const id = chPropsRef.value?.driverId || ''
  const cats = [{ k: 'general', l: t('常规') }]
  if (id === 'OpcUa.Client') cats.push({ k: 'ua', l: t('UA 服务器') })
  if (ETH_DRIVERS.includes(id)) cats.push({ k: 'eth', l: t('以太网通信') })
  cats.push({ k: 'write', l: t('写优化') }, { k: 'adv', l: t('高级') })
  if (id === 'Simulator' || id === '') cats.push({ k: 'persist', l: t('持久化') })
  if ((chPropsDriver.value?.channelSettings || []).length) cats.push({ k: 'driver', l: t('驱动参数') })
  cats.push({ k: 'custom', l: t('自定义参数') })
  return cats
})
function chTagCount(c: any): number { return (c?.devices || []).reduce((s: number, d: any) => s + devTagCount(d), 0) }
function seed(obj: any, key: string, def: string) { if (obj[key] === undefined || obj[key] === null || obj[key] === '') obj[key] = def }
function normalizeOpcUrl(value: unknown) {
  const url = String(value || '').trim()
  return url && !url.includes('://') ? `opc.tcp://${url}` : url
}
const chLegacyEndpointHint = ref('')
async function openChannelProps(c: any) {
  try { return await projectOperation.run('openChannelProps', t('正在读取通道属性…'), async () => {
  await loadConfigurationLookups(true)
  chPropsRef.value = c; c.settings ||= {}
  const s = { ...(c.settings || {}) }
  // 预置各页默认值(与桌面 GetValueOrDefault 相同;写回即为所见值)
  seed(s, 'diagnostics', '禁用')
  if (ETH_DRIVERS.includes(c.driverId)) seed(s, 'networkAdapter', '默认值')
  chLegacyEndpointHint.value = ''
  if (c.driverId === 'OpcUa.Client') {
    const legacy = [...new Set((c.devices || []).map((d: any) => String(d.endpoint || '').trim()).filter(Boolean))]
    if (!String(s.url || '').trim() && legacy.length === 1) {
      s.url = normalizeOpcUrl(legacy[0])
      chLegacyEndpointHint.value = `已从旧设备端点「${legacy[0]}」带入表单；点击确定后才会保存到通道。`
    } else if (!String(s.url || '').trim() && legacy.length > 1) {
      chLegacyEndpointHint.value = `旧工程存在 ${legacy.length} 个不同设备端点，无法自动合并；请明确填写本通道的 UA 服务器地址，原设备端点不会被删除。`
    }
    seed(s, 'securityPolicy', 'None')
    seed(s, 'anonymous', 'true'); seed(s, 'username', ''); seed(s, 'password', ''); seed(s, 'timeoutMs', '10000')
    seed(s, 'requestBudgetMs', 'auto'); seed(s, 'structArrayReads', 'true')
  }
  seed(s, 'writeOptimization', '仅写入非布尔型标记的最新值'); seed(s, 'dutyCycle', '10')
  seed(s, 'nonNormalFloat', '替换为零')
  seed(s, 'batchingMode', 'auto')
  if (c.driverId === 'Simulator' || c.driverId === '') { seed(s, 'itemPersistence', '禁用'); seed(s, 'dataFile', '') }
  chForm.value = { name: c.name, description: c.description || '', settings: s }
  chAdapterSelection.value = selectedAdapterValue(s)
  const declared = (chPropsDriver.value?.channelSettings || []).map((x: any) => x.key)
    .concat(['diagnostics', 'networkAdapter', 'networkAdapterId', 'networkAdapterIpv4', 'writeOptimization', 'dutyCycle', 'nonNormalFloat', 'itemPersistence', 'dataFile',
      'url', 'securityPolicy', 'anonymous', 'username', 'password', 'timeoutMs', 'requestBudgetMs', 'structArrayReads', 'batchingMode', 'maxGap'])
  chExtra.value = extraRows(s, declared)
  chCat.value = 'general'
  chPropsOpen.value = true
  }) } catch (error: any) { ElMessage.error(error?.response?.data?.error || error?.message || String(error)) }
}
async function confirmChProps() {
  if (!projectEditable.value) return
  try { return await projectOperation.run('confirmChProps', t('正在更新通道属性…'), async () => {
  const c = chPropsRef.value, f = chForm.value
  if (!String(f.name).trim()) { ElMessage.warning(t('填通道名')); return }
  if (c.driverId === 'OpcUa.Client') {
    f.settings.url = normalizeOpcUrl(f.settings.url)
    if (!f.settings.url.startsWith('opc.tcp://')) { ElMessage.warning(t('UA 服务器地址必须使用 opc.tcp://')); return }
    const timeout = Number(f.settings.timeoutMs)
    if (!Number.isFinite(timeout) || timeout < 1000 || timeout > 120000) { ElMessage.warning(t('连接超时应在 1000–120000 毫秒之间')); return }
    const budget = String(f.settings.requestBudgetMs ?? '').trim().toLowerCase()
    if (budget === '' || budget === 'auto') f.settings.requestBudgetMs = 'auto'
    else {
      const ms = Number(budget)
      if (!Number.isFinite(ms) || ms < 0 || ms > 60000) { ElMessage.warning(t('单请求耗时预算应为 auto、0 或 1–60000 毫秒')); return }
      f.settings.requestBudgetMs = String(Math.round(ms))
    }
  }
  const oldPath = c.name
  if (ETH_DRIVERS.includes(c.driverId))
    f.settings = applyAdapterValue(f.settings, chAdapterSelection.value, networkAdapters.value)
  f.settings.batchingMode = f.settings.batchingMode === 'manual' && chSupportsManualBatchGap.value ? 'manual' : 'auto'
  if (f.settings.batchingMode === 'manual') {
    const maxGap = Number(f.settings.maxGap)
    f.settings.maxGap = String(Number.isFinite(maxGap) ? Math.min(4096, Math.max(0, Math.trunc(maxGap))) : 0)
  }
  c.name = String(f.name).trim(); c.description = f.description
  c.settings = { ...f.settings }
  const declared = Object.keys(f.settings)
  syncExtras(c.settings, chExtra.value, declared)
  if (oldPath !== c.name) migrateMesRefs(oldPath, c.name)
  chPropsOpen.value = false; markDirty(); bump(); saveAfterConfirm()
  }) } catch (error: any) { ElMessage.error(error?.response?.data?.error || error?.message || String(error)) }
}

// -- 一键全开/全关采集(通道右键,对齐桌面 EnableAllCollect/DisableAllCollect) --
// PLC 以真实 DB 为边界；OPC UA 以设备下一级顶层目录为边界。
function mainGroupsUnderChannel(ch: any): { path: string; g: any; devPath: string }[] {
  const out: any[] = []
  const walk = (g: any, parentPath: string, devPath: string, depth: number, ancestorHasTags: boolean) => {
    const path = parentPath + '.' + g.name
    const hasTags = (g.tags || []).length > 0
    if (isMesTaskBoundaryGroup(ch.driverId || '', g, depth, ancestorHasTags)) out.push({ path, g, devPath })
    if (ch.driverId === 'OpcUa.Client') return
    for (const sg of g.subGroups || []) walk(sg, path, devPath, depth + 1, ancestorHasTags || hasTags)
  }
  for (const dev of ch.devices || []) {
    const devPath = `${ch.name}.${dev.name}`
    for (const g of dev.groups || []) walk(g, devPath, devPath, 0, false)
  }
  return out
}
function gatherGroupTags(g: any, path: string): { tagId: string; fieldName: string }[] {
  const out: any[] = []
  const walk = (grp: any, prefix: string) => {
    for (const t of grp.tags || []) out.push({ tagId: prefix + '.' + t.name, fieldName: t.name })
    for (const sg of grp.subGroups || []) walk(sg, prefix + '.' + sg.name)
  }
  walk(g, path)
  return out
}
async function enableDeviceFirstLevelFlows(node: any) {
  if (!projectEditable.value) return
  try { return await projectOperation.run('enableDeviceFirstLevelFlows', t('正在配置设备采集流程…'), async () => {
  await ensureDeepLoaded('device', node.ref)
  const groups = node.ref?.groups || []
  if (!groups.length) { ElMessage.info(t('本设备没有直属第一层标记组')); return }
  const deviceRef = `${node.channel?.name || ''}.${node.ref?.name || ''}`.replace(/^\./, '')
  const deepTasks = (proj.value?.mesUpload?.tasks || []).filter((task: any) => {
    const groupRef = String(task?.groupRef || '')
    if (!groupRef.startsWith(`${deviceRef}.`)) return false
    return groupRef.slice(deviceRef.length + 1).includes('.') && isMesFlowAvailable(task)
  }).length
  try {
    await ElMessageBox.confirm(
      t('将开启设备「{device}」的 {count} 个直属第一层采集流程；更深层已开放流程将关闭但保留配置。', {
        device: node.ref.name, count: groups.length,
      }) + (deepTasks ? `\n${t('将关闭更深层流程：{count} 个', { count: deepTasks })}` : ''),
      t('一键开启直属组采集流程'),
      { type: 'warning', confirmButtonText: t('确定'), cancelButtonText: t('取消') },
    )
  } catch { return }
  proj.value.mesUpload ||= { tasks: [] }
  const result = configureDeviceFirstLevelMesFlows(
    proj.value.mesUpload,
    node.channel?.name || '',
    node.ref?.name || '',
    groups,
    createCompactId,
  )
  if (!result.created && !result.opened && !result.closed) {
    ElMessage.info(t('直属第一层采集流程已经全部开启'))
    return
  }
  markDirty(); bump()
  ElMessage.success(t('直属组采集流程已更新：开放 {opened}，新建 {created}，关闭深层 {closed}', result))
  }) } catch (error: any) { ElMessage.error(error?.response?.data?.error || error?.message || String(error)) }
}
async function enableAllCollect(node: any) {
  if (!projectEditable.value) return
  try { return await projectOperation.run('enableAllCollect', t('正在启用批量采集…'), async () => {
  const ch = node.ref
  await ensureDeepLoaded('channel', ch)
  const mains = mainGroupsUnderChannel(ch)
  if (!mains.length) { ElMessage.info(t('本通道下没有可采集的主标记组')); return }
  proj.value.mesUpload ||= { tasks: [] }
  if (!proj.value.mesUpload.defaultMesUrl) {
    try {
      await ElMessageBox.confirm(
        t('工程还没设置「默认 MES 地址」。一键全开后新建的采集任务没有上报地址,会一直空跑不上报。') + '\n' +
        t('建议先在 工程属性 → MES 上报 里填默认地址。仍要继续?'),
        t('未设置默认 MES 地址'), { type: 'warning' })
    } catch { return }
  }
  let created = 0, turnedOn = 0
  const tasks = proj.value.mesUpload.tasks ||= []
  for (const m of mains) {
    const task = tasks.find((t: any) => t.groupRef === m.path)
    const descendants = tasks.some((t: any) => String(t.groupRef || '').startsWith(`${m.path}.`))
    if (!task && !descendants) {
      tasks.push({ id: createCompactId(), name: m.g.name, groupRef: m.path, deviceRef: m.devPath, enabled: false,
        collectIntervalMs: 1000, confirmRewrite: true, payload: { fields: gatherGroupTags(m.g, m.path), structs: [], append: {} }, steps: [] })
      created++
    }
  }
  const before = tasks.filter((task: any) => task.enabled).length
  setAllMesTasksEnabled(proj.value.mesUpload, true, pathOf(node))
  turnedOn = Math.max(0, tasks.filter((task: any) => task.enabled).length - before)
  markDirty(); bump()
  ElMessage.success(t('一键全开:共 {count} 个主标记组(新建 {created}、启用 {enabled});保存后生效', { count: mains.length, created, enabled: turnedOn }))
  }) } catch (error: any) { ElMessage.error(error?.response?.data?.error || error?.message || String(error)) }
}
async function disableAllCollect(node: any) {
  if (!projectEditable.value) return
  try { return await projectOperation.run('disableAllCollect', t('正在停止批量采集…'), async () => {
  const prefix = node.ref.name + '.'
  let n = 0
  for (const t of proj.value?.mesUpload?.tasks || [])
    if (t.groupRef && t.groupRef.startsWith(prefix) && t.enabled) { t.enabled = false; n++ }
  if (!n) { ElMessage.info(t('本通道下没有正在采集的标记组')); return }
  markDirty(); bump()
  ElMessage.success(t('一键全关:关闭 {count} 个(保存后生效)', { count: n }))
  }) } catch (error: any) { ElMessage.error(error?.response?.data?.error || error?.message || String(error)) }
}
// Quick Client:弹窗打开点位监控(对齐桌面:QC 是配置工具里弹出的独立窗口),按节点路径预选组
const qcOpen = ref(false)
const qcQ = ref('')
const qcRef = ref<any>()
const qcPreferredScope = ref('')
const qcOpening = ref(false)
const pendingQcRoot = ref<string | null>(null)
const pendingQcActive = ref<string | null>(null)
const qcPreparing = computed(() => qcOpening.value || pendingQcRoot.value !== null)
let qcOpenGeneration = 0
watch(qcOpen, open => {
  if (!open) { ++qcOpenGeneration; qcOpening.value = false; pendingQcRoot.value = null; pendingQcActive.value = null }
})
onUnmounted(() => { ++qcOpenGeneration })
async function prepareQuickClient(scope: () => string) {
  const generation = ++qcOpenGeneration, owner = proj.value
  qcOpening.value = dirty.value || saveBusy.value
  if (!qcOpening.value) { qcQ.value = scope(); qcPreferredScope.value = '' }
  qcOpen.value = true
  await nextTick()
  prepareToolOpen('qc')
  if (qcOpening.value) await nextPaint()
  if (generation !== qcOpenGeneration || !qcOpen.value) return
  // 手动保存与自动保存都可能仍在进行；等待同一个实际请求，而不是误报未保存。
  if (activeProjectSave) await activeProjectSave
  if (generation !== qcOpenGeneration || !qcOpen.value) return
  if (dirty.value && !(await autoSave.flush())) {
    if (generation !== qcOpenGeneration || !qcOpen.value) return
    qcOpen.value = false; qcOpening.value = false
    ElMessage.error(t('工程配置尚未保存，暂不能打开点位监控：') + (saveError.value || t('请稍后重试')))
    return
  }
  if (generation !== qcOpenGeneration || !qcOpen.value) return
  if (proj.value !== owner) {
    qcOpen.value = false; qcOpening.value = false
    ElMessage.error(t('工程已重新载入，请重新打开点位监控'))
    return
  }
  qcQ.value = scope(); qcPreferredScope.value = ''
  qcOpening.value = false
}
function quickClient(node: any) {
  return prepareQuickClient(() => node && !['project', 'connectivity'].includes(node.kind)
    ? pathOf(treeNodeForDef(node.ref) || node) : '')
}
function openQcForFlow() {
  if (mesScopeOwner && mesScopeOwner !== proj.value) {
    ElMessage.error(t('工程已重新载入，请关闭并重新打开采集流程后再监控'))
    return Promise.resolve()
  }
  return prepareQuickClient(() => mesFlowGroup.value)
}
function onMesFlowActiveGroupChanged(path: string) {
  if (mesScopeOwner && mesScopeOwner !== proj.value) return
  let resolved = path
  for (const change of mesScopeRenames)
    if (resolved === change.from || resolved.startsWith(`${change.from}.`)) resolved = change.to + resolved.slice(change.from.length)
  mesFlowGroup.value = resolved
}
function queueQcScopeChange(oldPath: string, newPath: string) {
  if (!qcOpen.value) return
  const rewrite = (path: string) => path === oldPath || path.startsWith(`${oldPath}.`) || path.startsWith(`${oldPath}[`)
    ? newPath + path.slice(oldPath.length) : path
  const root = pendingQcRoot.value ?? qcQ.value
  const active = pendingQcActive.value ?? qcRef.value?.getActiveScopePath?.() ?? root
  let nextRoot = rewrite(root)
  const nextActive = rewrite(active)
  if (nextRoot === root && nextActive === active) return
  // 跨设备移组时，旧的绑定根已不包含所选组；用移动后的精确组作为新的根。
  if (nextRoot && nextActive && nextActive !== nextRoot && !nextActive.startsWith(`${nextRoot}.`)) nextRoot = nextActive
  pendingQcRoot.value = nextRoot; pendingQcActive.value = nextActive
}
function commitQcScopeChange() {
  if (pendingQcRoot.value === null) return
  qcQ.value = pendingQcRoot.value
  qcPreferredScope.value = pendingQcActive.value || ''
  pendingQcRoot.value = null; pendingQcActive.value = null
}

// 通道诊断与工程树共享同一份运行状态，避免弹窗快照与树状态互相矛盾。
const diagOpen = ref(false)
const diagCh = ref<any>(null)
const diagLoading = ref(false)
async function loadDiag() {
  diagLoading.value = true
  try { await pollRuntimeStatus() } finally { diagLoading.value = false }
}
function openDiag(node: any) {
  diagCh.value = node; diagOpen.value = true; loadDiag()
}
const diagRows = computed(() => {
  const ch = diagCh.value?.ref
  if (!ch) return []
  return (ch.devices || []).map((d: any) => {
    const s = findRuntimeDevice(runtimeDevices.value, ch.name, d.name) || null
    return {
      name: d.name,
      endpoint: d.endpoint || '',
      runtimeMark: deviceRuntimeMark(s || undefined, runtimeStatusAvailable.value),
      detailState: s?.detailState || '',
      detailMessage: s?.detailMessage || '',
      tagCount: s?.tagCount ?? devTagCount(d),
      activeTags: s?.activeTags ?? 0,
      stats: s?.stats || null,
    }
  })
})
const DIAG_TONE_COLOR: Record<string, string> = {
  online: '#1B7F37', standby: '#536273', connecting: '#B8860B', offline: '#D92D20', fault: '#C0392B', unknown: '#888',
}
const diagToneColor = (tone: string) => DIAG_TONE_COLOR[tone] || '#888'

function treeRuntimeMark(node: any): RuntimeMark | null {
  if (node?.kind === 'channel')
    return channelRuntimeMark(node.ref?.name || '', node.ref?.devices, runtimeDevices.value, runtimeStatusAvailable.value, t)
  if (node?.kind === 'device') {
    const status = findRuntimeDevice(runtimeDevices.value, node.channel?.name || '', node.ref?.name || '')
    return deviceRuntimeMark(status, runtimeStatusAvailable.value, t)
  }
  return null
}
function treeDescription(node: any): string {
  return projectTreeDescription(node?.kind, node?.ref)
}
function treeNodeTitle(node: any): string {
  const description = treeDescription(node)
  const label = description ? `${node.label} · ${description}` : node.label
  const base = label + (node.kind === 'channel'
    ? ` · ${driverName(node.ref.driverId)}`
    : node.kind === 'device'
      ? ` (${t('{count} 点', { count: devTagCount(node.ref) })})`
      : node.kind === 'group'
        ? ` (${t('{count} 点', { count: cnt(node.ref) })})`
        : '')
  const status = treeRuntimeMark(node)
  return status ? `${base} · ${status.title}` : base
}

// -- 设备(新建 / 编辑) --
const devOpen = ref(false)
const devForm = ref<any>({})
const devEditRef = ref<any>(null)      // 非空=编辑
const devTargetList = ref<any>(null)   // 新建时:通道对象
const devDriver = computed(() => {
  const id = devEditRef.value ? channelOf(devEditRef.value)?.driverId : devTargetList.value?.driverId
  return drivers.value.find((d) => d.driverId === id)
})
function channelOf(dev: any): any { return proj.value.channels.find((c: any) => (c.devices || []).includes(dev)) }
function devDeclaredKeys(): string[] {
  const id = devEditRef.value ? channelOf(devEditRef.value)?.driverId : devTargetList.value?.driverId
  return (drivers.value.find((d) => d.driverId === id)?.deviceSettings || []).map((s: any) => s.key)
}
// 新建设备:保留简表(对齐桌面 DeviceWizard);编辑设备走下面的分类属性页
async function openDeviceDialog(dev: any | null, ch: any | null) {
  try { return await projectOperation.run('openDeviceDialog', t('正在读取设备配置…'), async () => {
  await loadDrivers(true)
  if (dev) { await openDeviceProps(dev, false); return }
  if (isUnsupportedDriver(ch.driverId)) { ElMessage.warning(t('本版本不支持该通道的驱动，不能再添加设备。')); return }
  devEditRef.value = null; devTargetList.value = ch
  const nm = uniqueName(t('设备'), (ch.devices || []).map((x: any) => x.name))
  const s: any = {}; for (const st of (drivers.value.find((d) => d.driverId === ch.driverId)?.deviceSettings) || []) s[st.key] = st.default
  devForm.value = { name: nm, endpoint: ch.driverId === 'Modbus.Tcp' ? '192.168.0.1:502' : '', description: '', settings: s }
  devExtra.value = []
  devOpen.value = true
  }) } catch (error: any) { ElMessage.error(error?.response?.data?.error || error?.message || String(error)) }
}
function confirmDevice() {
  if (!projectEditable.value) return
  const f = devForm.value; if (!f.name.trim()) { ElMessage.warning(t('填设备名')); return }
  const error = validateProtocolSettings(devTargetList.value?.driverId, f.settings)
  if (error) { ElMessage.warning(error); return }
  const endpoint = devTargetList.value?.driverId === 'OpcUa.Client' ? '' : f.endpoint.trim()
  ;(devTargetList.value.devices ||= []).push({ id: '', name: f.name.trim(), endpoint, description: f.description, settings: { ...f.settings }, groups: [], tags: [] })
  devOpen.value = false; markDirty(); bump(); saveAfterConfirm()
}

// -- 设备属性(编辑,分类页;字段/键/默认值与桌面 PropertyEditorDialog 完全一致) --
const devPropsOpen = ref(false)
const devPropsRef = ref<any>(null)
const devPForm = ref<any>({})
const devCat = ref('general')
const devPDriverId = computed(() => channelOf(devPropsRef.value)?.driverId || '')
const devPDriver = computed(() => drivers.value.find((d) => d.driverId === devPDriverId.value))
const devPChName = computed(() => channelOf(devPropsRef.value)?.name || '')
const devCats = computed(() => {
  const id = devPDriverId.value
  const cats = [{ k: 'general', l: t('常规') }, { k: 'scan', l: t('扫描模式') }, { k: 'timing', l: t('定时') }, { k: 'demote', l: t('自动降级') }]
  if (id === 'Modbus.Tcp') cats.splice(2, 1)
  if (['Siemens.S7', 'Omron.Fins', 'AB.Eip'].includes(id)) cats.push({ k: 'comm', l: t('通信参数') })
  if (id === 'Modbus.Tcp') cats.push({ k: 'protocol', l: 'Modbus TCP' })
  if (id === 'Siemens.S7') cats.push({ k: 's7', l: t('S7 通信参数') }, { k: 'addr', l: t('寻址选项') })
  cats.push({ k: 'redu', l: t('冗余') })
  if ((devPDriver.value?.deviceSettings || []).length && id !== 'Modbus.Tcp') cats.push({ k: 'driver', l: t('驱动参数') })
  cats.push({ k: 'custom', l: t('自定义参数') })
  return cats
})
async function openDeviceProps(dev: any, refreshDrivers = true) {
  try { return await projectOperation.run('openDeviceProps', t('正在读取设备属性…'), async () => {
  if (refreshDrivers) await loadDrivers(true)
  devPropsRef.value = dev; dev.settings ||= {}
  const id = channelOf(dev)?.driverId || ''
  const s = { ...(dev.settings || {}) }
  // 各页默认值(与桌面 GetValueOrDefault 同)
  if (id === 'Siemens.S7') { seed(s, 'model', 'S7-1200'); const [r, sl] = rackSlot(s.model); seed(s, 'rack', String(r)); seed(s, 'slot', String(sl)) }
  if (id === 'Modbus.Tcp') { seed(s, 'idFormat', '十进制'); seed(s, 'unitId', '1') }
  seed(s, 'dataCollection', '启用')
  seed(s, 'scanMode', '遵循客户端指定的扫描速率'); seed(s, 'activeWindowSec', '30'); seed(s, 'initialFromCache', '启用')
  seed(s, 'connTimeout', '3'); seed(s, 'timeoutMs', '2000'); seed(s, 'retries', '2'); seed(s, 'requestDelay', '0')
  seed(s, 'demote', '禁用'); seed(s, 'demoteAfter', '3'); seed(s, 'demotePeriodMs', '10000')
  if (id === 'Siemens.S7') { seed(s, 'port', '102'); seed(s, 'mpiId', '0'); seed(s, 'pdu', '960'); seed(s, 'localTsap', '4D57'); seed(s, 'remoteTsap', '4D57'); seed(s, 'byteOrder', 'big') }
  if (id === 'Omron.Fins') { seed(s, 'destNode', ''); seed(s, 'srcNode', '1'); seed(s, 'port', '9600') }
  if (id === 'AB.Eip') { seed(s, 'plcType', 'ControlLogix'); seed(s, 'path', '1,0') }
  if (id === 'Modbus.Tcp') { seed(s, 'zeroBased', '禁用'); seed(s, 'wordOrder', 'ABCD'); seed(s, 'regBlock', '120'); seed(s, 'coilBlock', '512'); seed(s, 'writeMode', 'auto') }
  seed(s, 'secondaryEndpoint', ''); seed(s, 'monitorIntervalSec', '300'); seed(s, 'returnToPrimary', '是')
  devPForm.value = { name: dev.name, endpoint: dev.endpoint, description: dev.description || '', settings: s }
  const KNOWN = ['model', 'rack', 'slot', 'idFormat', 'unitId', 'dataCollection', 'scanMode', 'activeWindowSec', 'initialFromCache',
    'connTimeout', 'timeoutMs', 'retries', 'requestDelay', 'demote', 'demoteAfter', 'demotePeriodMs',
    'port', 'mpiId', 'destNode', 'srcNode', 'plcType', 'path', 'zeroBased', 'wordOrder', 'regBlock', 'coilBlock', 'writeMode',
    'pdu', 'localTsap', 'remoteTsap', 'byteOrder', 'secondaryEndpoint', 'monitorIntervalSec', 'returnToPrimary']
  const declared = (devPDriver.value?.deviceSettings || []).map((x: any) => x.key).concat(KNOWN)
  devExtra.value = extraRows(s, declared)
  devCat.value = 'general'
  devPropsOpen.value = true
  }) } catch (error: any) { ElMessage.error(error?.response?.data?.error || error?.message || String(error)) }
}
// S7 型号联动机架/槽(与桌面 ModelRackSlot 同)
function onModelChange() {
  const [r, sl] = rackSlot(devPForm.value.settings.model)
  devPForm.value.settings.rack = String(r); devPForm.value.settings.slot = String(sl)
}
async function confirmDeviceProps() {
  if (!projectEditable.value) return
  try { return await projectOperation.run('confirmDeviceProps', t('正在更新设备属性…'), async () => {
  const d = devPropsRef.value, f = devPForm.value
  const protocolError = validateProtocolSettings(devPDriverId.value, f.settings)
  if (protocolError) { ElMessage.warning(protocolError); devCat.value = 'protocol'; return }
  if (!String(f.name).trim()) { ElMessage.warning(t('填设备名')); return }
  const node = treeNodeForDef(d)
  const oldPath = node ? pathOf(node) : `${devPChName.value}.${d.name}`
  d.name = String(f.name).trim(); d.endpoint = String(f.endpoint || '').trim(); d.description = f.description
  d.settings = { ...f.settings }
  syncExtras(d.settings, devExtra.value, Object.keys(f.settings))
  const newPath = `${oldPath.split('.').slice(0, -1).join('.')}.${d.name}`.replace(/^\./, '')
  if (oldPath !== newPath) migrateMesRefs(oldPath, newPath)
  devPropsOpen.value = false; markDirty(); bump(); saveAfterConfirm()
  }) } catch (error: any) { ElMessage.error(error?.response?.data?.error || error?.message || String(error)) }
}

// -- 组(新建 / 编辑) --
const grpOpen = ref(false)
const grpForm = ref<any>({})
const grpEditRef = ref<any>(null)
const grpTargetList = ref<any>(null)
const grpEditPath = ref('')
const grpMesBoundary = ref(false)
const grpMesTask = ref<any>(null)
const grpMesConflict = computed(() => {
  if (!grpMesBoundary.value || !grpEditPath.value || isMesFlowAvailable(grpMesTask.value)) return ''
  return findMesGroupAvailabilityConflict(proj.value?.mesUpload, grpEditPath.value, grpMesTask.value)
})
function openGroupDialog(g: any | null, list: any[] | null) {
  grpEditRef.value = g; grpTargetList.value = list
  const node = g ? treeNodeForDef(g) : null
  grpEditPath.value = node ? pathOf(node) : ''
  grpMesBoundary.value = !!node && isMesBoundaryGroup(node)
  grpMesTask.value = grpMesBoundary.value ? taskFor(grpEditPath.value) : null
  grpForm.value = g
    ? { name: g.name, description: g.description || '', flowAvailable: isMesFlowAvailable(grpMesTask.value) }
    : { name: uniqueName(t('标记组'), (list || []).map((x: any) => x.name)), description: '', flowAvailable: false }
  grpOpen.value = true
}
function confirmMesTaskDisableDialog() {
  return ElMessageBox.confirm(
    t('该数据块当前正在采集上报。停用后将停止本数据块的采集与上报，是否继续？'),
    t('停用采集流程'),
    { type: 'warning', confirmButtonText: t('确认停用'), cancelButtonText: t('取消') },
  )
}
async function confirmGroup() {
  if (!projectEditable.value) return
  try { return await projectOperation.run('confirmGroup', t('正在更新标记组…'), async () => {
  const f = grpForm.value; if (!f.name.trim()) { ElMessage.warning(t('填组名')); return }
  let task = grpMesTask.value
  let createdTask = false
  if (!task && f.flowAvailable === true) {
    const conflict = findMesGroupAvailabilityConflict(proj.value?.mesUpload, grpEditPath.value)
    if (conflict) {
      ElMessage.warning(t('父级和子级采集任务不能同时开启，请先停用：') + conflict)
      f.flowAvailable = false
      return
    }
    proj.value.mesUpload ||= { tasks: [] }
    const ensured = ensureMesTaskForGroup(proj.value.mesUpload, grpEditPath.value, createCompactId())
    task = ensured.task
    createdTask = ensured.created
    grpMesTask.value = task
  }
  if (task && f.flowAvailable !== isMesFlowAvailable(task)) {
    if (f.flowAvailable === false && task.enabled === true) {
      const stopResult = await setMesTaskEnabledWithConfirmation(
        proj.value?.mesUpload,
        { groupRef: task.groupRef, id: task.id },
        false,
        confirmMesTaskDisableDialog,
      )
      if (stopResult.cancelled) {
        f.flowAvailable = true
        return
      }
    }
    const result = setMesTaskFlowAvailable(
      proj.value?.mesUpload,
      { groupRef: task.groupRef, id: task.id },
      f.flowAvailable === true,
    )
    if (result.conflictGroupRef) {
      if (createdTask) {
        const tasks = proj.value?.mesUpload?.tasks || []
        const index = tasks.indexOf(task)
        if (index >= 0) tasks.splice(index, 1)
        grpMesTask.value = null
      }
      ElMessage.warning(t('父级和子级采集任务不能同时开启，请先停用：') + result.conflictGroupRef)
      f.flowAvailable = false
      return
    }
  }
  if (grpEditRef.value) {
    const oldPath = grpEditPath.value
    Object.assign(grpEditRef.value, { name: f.name.trim(), description: f.description })
    const newPath = `${oldPath.split('.').slice(0, -1).join('.')}.${grpEditRef.value.name}`.replace(/^\./, '')
    if (oldPath && oldPath !== newPath) migrateMesRefs(oldPath, newPath)
  }
  else grpTargetList.value.push({ id: '', name: f.name.trim(), description: f.description, subGroups: [], tags: [] })
  grpOpen.value = false; markDirty(); bump(); saveAfterConfirm()
  }) } catch (error: any) { ElMessage.error(error?.response?.data?.error || error?.message || String(error)) }
}

// -- 标记属性(对齐桌面 PropertyEditorDialog 标记版:左分类[常规/缩放/报警]+右字段页) --
const tagOpen = ref(false)
const tagForm = ref<any>({})
const tagEditRef = ref<any>(null)
const tagTargetList = ref<any>(null)
const tagCat = ref('general')
const tagPathPrefix = ref('')
const tagEditPath = ref('')
const tagProtocolContext = ref<any>(null)
const isProtocolTag = computed(() => tagDriverId.value === 'Modbus.Tcp')
const tagConfirmationMode = computed({
  get: () => tagForm.value.writeConfirmation?.mode || 'readBack',
  set: (mode: string) => {
    tagForm.value.writeConfirmation = {
      mode,
      confirmationTagId: tagForm.value.writeConfirmation?.confirmationTagId || '',
      expectedValue: tagForm.value.writeConfirmation?.expectedValue || '',
    }
  },
})
const tagConfirmationOptions = computed(() => {
  const node = tagEditRef.value ? treeNodeForDef(tagEditRef.value) : sel.value
  const channel = tagProtocolContext.value?.channel || node?.channel
  const device = tagProtocolContext.value?.device || node?.device
  if (!channel || !device) return []
  const prefix = `${channel.name}.${device.name}`
  const options: { path: string; label: string }[] = []
  const editingFullPath = `${tagPathPrefix.value}.${tagForm.value.name || ''}`.replace(/^\./, '')
  const add = (tag: any, path: string) => {
    if (!tag?.name || tag.access === 'write') return
    const fullPath = `${prefix}.${path}.${tag.name}`.replace(/\.\./g, '.')
    if (fullPath === editingFullPath) return
    const detail = [tag.address, tag.description].filter(Boolean).join(' · ')
    options.push({ path: fullPath, label: detail ? `${fullPath} — ${detail}` : fullPath })
  }
  ;(device.tags || []).forEach((tag: any) => add(tag, ''))
  const walk = (groups: any[], parent: string) => (groups || []).forEach((group: any) => {
    const groupPath = parent ? `${parent}.${group.name}` : group.name
    ;(group.tags || []).forEach((tag: any) => add(tag, groupPath))
    walk(group.subGroups || [], groupPath)
  })
  walk(device.groups || [], '')
  return options
})
// KEP 数据类型清单(中文显示名 → 存储值;未支持的置灰,与桌面 KepTypes 一致)
const KEP_TYPES = computed(() => editableDataTypesForDriver(tagDriverId.value).map(option => ({ l: option.label, v: option.value })))
// 地址格式字典(逐驱动·逐类型,与 KEP 同款排版;每条按 IGW 驱动真实能解析的写)
// note = 该驱动地址语法/类型支持的补充说明(与 KEP 差异处标注)
const ADDR_GUIDE: Record<string, { note: string; rows: { t: string; a: string }[] }> = {
  'Siemens.S7': {
    note: '与 KEP「Siemens TCP/IP Ethernet」一致。DB 需在 TIA 取消「优化的块访问」才有绝对地址。位地址 x.y(y=0~7)。',
    rows: [
      { t: '位 Bool', a: 'DB1.DBX0.0 / M0.0 / Q0.0 / I0.0' },
      { t: '字节 Byte/Char', a: 'DB1.DBB0 / MB0 / IB0 / QB0' },
      { t: '16 位 Int16/UInt16/字/BCD', a: 'DB1.DBW0 / MW0 / IW0 / QW0' },
      { t: '32 位 Int32/UInt32/双字/浮点/LBCD', a: 'DB1.DBD0 / MD0' },
      { t: '64 位 双精度/LLong/QWord', a: 'DB1.DBD0(占 8 字节)' },
      { t: '字符串 String', a: 'DB1.DBB8(S7 String,含 2 字节头)' },
      { t: '宽字符串 WString', a: 'DB1.DBB8(UTF-16)' },
      { t: '字节数组 ByteArray', a: 'DB1.DBB0(从此偏移读原始块)' },
      { t: 'S7-200/Smart', a: 'VW100 / VD100 / V100 / M0.0 / Q0.0 / I0.0' },
      { t: '数组', a: '按元素连续地址逐个加:DB1.DBW0、DB1.DBW2、…' },
    ],
  },
  'Modbus.Tcp': {
    note: '区号=首位数字,其余=寄存器/线圈号(1 基,零基址在设备属性→数据编码切换)。5 位(40001)和 6 位(400001)都可以,与 KEP 兼容。本驱动暂不支持 String/字节数组(只支持 布尔+16/32/64 位整型+浮点)。',
    rows: [
      { t: '线圈 Bool(读写)', a: '00001 / 000001(0 区)' },
      { t: '离散输入 Bool(只读)', a: '10001 / 100001(1 区)' },
      { t: '输入寄存器(只读)', a: '30001 / 300001(3 区)' },
      { t: '保持寄存器 16 位', a: '40001 / 400001(4 区)' },
      { t: '32 位 Int32/UInt32/浮点', a: '40001(占 2 寄存器,字序看设备属性)' },
      { t: '64 位 双精度/LLong', a: '40001(占 4 寄存器)' },
      { t: '寄存器某一位 Bool', a: '40001.3(位 0~15)' },
    ],
  },
  'Omron.Cip': {
    note: '与 KEP「Omron NJ/NX」一致=Sysmac 里的变量名(全局变量需勾『发布到网络』)。类型须与 PLC 变量一致。',
    rows: [
      { t: '单变量', a: 'MyVar' },
      { t: '结构体成员', a: 'MyStruct.Member' },
      { t: '数组元素', a: 'MyArray[3]' },
      { t: '二维数组元素', a: 'MyArray[2,3]' },
      { t: '嵌套', a: 'MyUdt.Sub.Item' },
      { t: '字符串', a: 'MyString(Sysmac STRING 变量)' },
    ],
  },
  'AB.Eip': {
    note: '与 KEP「Allen-Bradley ControlLogix」一致=控制器/程序标记名。',
    rows: [
      { t: '控制器标记', a: 'MyTag' },
      { t: '程序标记', a: 'Program:MainProgram.Count' },
      { t: '数组元素', a: 'MyArray[3]' },
      { t: 'UDT 成员', a: 'MyUdt.Member' },
      { t: 'DINT 某一位', a: 'MyDint.0' },
    ],
  },
  'Omron.Fins': {
    note: '欧姆龙 FINS 区号+地址,与 KEP「Omron FINS」一致。位:区.位(如 CIO0.1)。',
    rows: [
      { t: 'DM 数据存储器', a: 'D200' },
      { t: 'CIO 通道 I/O', a: 'CIO0 / CIO0.1(位)' },
      { t: '链接寄存器 W', a: 'W100' },
      { t: '保持继电器 H', a: 'H0' },
      { t: '辅助继电器 A', a: 'A0' },
    ],
  },
  'Mitsubishi.Mc': {
    note: 'MC 协议(二进制)软元件,与 KEP「Mitsubishi Ethernet」一致。X/Y/W/B 用十六进制。需在 GX Works 给以太网模块开 MC 协议+设端口。',
    rows: [
      { t: '数据寄存器 D', a: 'D100' },
      { t: '文件寄存器 R', a: 'R200' },
      { t: '链接寄存器 W(hex)', a: 'W1A' },
      { t: '内部继电器 M', a: 'M50' },
      { t: '输入/输出 X/Y(hex)', a: 'X10 / Y20' },
      { t: '链接继电器 B(hex)', a: 'B1F' },
    ],
  },
  'OpcUa.Client': {
    note: '上游 OPC UA 服务器的 NodeId。',
    rows: [
      { t: '字符串 NodeId', a: 'ns=2;s=Channel1.Device1.Tag1' },
      { t: '数值 NodeId', a: 'ns=3;i=1001' },
    ],
  },
}
const tagDriverId = ref('')
const tagDriver = computed(() => drivers.value.find((d) => d.driverId === tagDriverId.value))
const tagAddrGuide = computed(() => {
  const guide = ADDR_GUIDE[tagDriverId.value]
  return guide ? { ...guide, note: t(guide.note), rows: guide.rows.map((row) => ({ ...row, t: t(row.t) })) } : undefined
})
const tagAddrRows = computed(() => tagAddrGuide.value?.rows || [])
// 缩放类型:无/线性/平方根(存 scaling.enabled + squareRoot)
const tagScaleType = computed({
  get: () => (tagForm.value.scaling?.enabled ? (tagForm.value.scaling.squareRoot ? '平方根' : '线性') : '无'),
  set: (v: string) => { const s = tagForm.value.scaling; s.enabled = v !== '无'; s.squareRoot = v === '平方根' },
})
function blankTag(list: any[]): any {
  return { id: '', name: uniqueName(t('标记'), list.map((x: any) => x.name)), address: '', dataType: 'int16', isArray: false, array: null, access: 'read', scanRateMs: 100, description: '', engUnit: '', stringLength: null, textEncoding: null,
    writeConfirmation: null,
    scaling: { enabled: false, squareRoot: false, rawLow: 0, rawHigh: 100, engLow: 0, engHigh: 100, clamp: false }, deadband: 0, deadbandType: 'none',
    alarm: { enabled: false, hiHi: null, hi: null, lo: null, loLo: null, deadband: 0 } }
}
function openTagDialog(t: any | null, list: any[] | null) {
  tagEditRef.value = t; tagTargetList.value = list
  tagProtocolContext.value = resolveTagProtocolContext(proj.value, t, list)
  const node = t ? treeNodeForDef(t) : null
  tagEditPath.value = node ? pathOf(node) : ''
  if (t) {
    t.scaling ||= { enabled: false, squareRoot: false, rawLow: 0, rawHigh: 100, engLow: 0, engHigh: 100, clamp: false }
    t.alarm ||= { enabled: false, hiHi: null, hi: null, lo: null, loLo: null, deadband: 0 }
    tagForm.value = clone(t)
    tagForm.value.dataType = dataTypeEditorValue(tagForm.value.dataType)
    if (tagForm.value.array?.elementType) tagForm.value.array.elementType = dataTypeEditorValue(tagForm.value.array.elementType)
  }
  else tagForm.value = blankTag(list || [])
  tagPathPrefix.value = tagProtocolContext.value?.prefix || (sel.value ? pathOf(sel.value) : '')
  tagDriverId.value = tagProtocolContext.value?.driverId || node?.channel?.driverId || ''
  if (!t && tagDriverId.value === 'Modbus.Tcp') tagForm.value.address = 'HR:0'
  tagCat.value = 'general'
  tagOpen.value = true
}
async function confirmTag() {
  if (!projectEditable.value) return
  try { return await projectOperation.run('confirmTag', t('正在更新点位属性…'), async () => {
  const f = tagForm.value; if (!f.name.trim()) { ElMessage.warning(t('填标记名')); return }
  if (tagDriverId.value === 'Modbus.Tcp') {
    const error = validateModbusTag(f, tagProtocolContext.value?.device?.settings)
    if (error) { ElMessage.warning(t(error)); tagCat.value = 'general'; return }
  }
  if ((f.name as string).includes('.')) { ElMessage.warning(t('名称不能含「.」')); return }
  if (f.access === 'write' && (!f.writeConfirmation || f.writeConfirmation.mode === 'readBack')) {
    ElMessage.warning(t('只写点必须选择“确认点”或“仅协议确认”'))
    tagCat.value = 'writeConfirmation'
    return
  }
  if (f.writeConfirmation?.mode === 'confirmationTag' &&
      (!f.writeConfirmation.confirmationTagId || !String(f.writeConfirmation.expectedValue || '').trim())) {
    ElMessage.warning(t('请选择完整确认点路径并填写期望值'))
    tagCat.value = 'writeConfirmation'
    return
  }
  if (tagEditRef.value) {
    const oldPath = tagEditPath.value
    Object.assign(tagEditRef.value, clone(f))
    const newPath = `${oldPath.split('.').slice(0, -1).join('.')}.${tagEditRef.value.name}`.replace(/^\./, '')
    if (oldPath && oldPath !== newPath) migrateMesRefs(oldPath, newPath)
  }
  else tagTargetList.value.push(clone(f))
  tagOpen.value = false; markDirty(); bump(); saveAfterConfirm()
  }) } catch (error: any) { ElMessage.error(error?.response?.data?.error || error?.message || String(error)) }
}
const opcBrowserOpen = ref(false)
const opcBrowserNode = ref<any>(null)
const opcBrowserTarget = computed(() => opcBrowserNode.value?.ref || { tags: [], groups: [] })
const opcBrowserDeviceName = computed(() => opcBrowserNode.value?.channel?.driverId === 'Siemens.S7'
  ? `${opcBrowserNode.value.channel?.name || ''}.${opcBrowserNode.value.ref?.name || ''}`.replace(/^\./, '')
  : '')
const opcBrowserConnection = computed<OpcUaBrowsePreviewBody>(() => {
  const s = opcBrowserNode.value?.channel?.settings || {}
  return {
    url: normalizeOpcUrl(s.url || ''), securityPolicy: s.securityPolicy || 'None', anonymous: s.anonymous !== 'false',
    username: s.username || '', password: s.password || '', timeoutMs: Number(s.timeoutMs || 10000) || 10000,
  }
})
const anyDialogOpen = computed(() => wiz.open || chPropsOpen.value || devPropsOpen.value || grpOpen.value || tagOpen.value || projOpen.value || opcBrowserOpen.value)

// ===================== 拖拽移动(左树/右列表 → 设备/组)=====================
// 只保存本页实际定义；放下后捕获来源，不能在异步加载结束时再读取已被 dragend 清空的状态。
const dragData = ref<ProjectGridTarget[] | null>(null)
let dragProject: any = null
const dropKey = ref<string>('')
function canDropInto(tgt: any, sources = dragData.value): boolean {
  if (!projectEditable.value || !sources?.length || !tgt?.ref || !['device', 'group'].includes(tgt.kind)) return false
  const targetPath = pathOf(tgt)
  if (sources.some(source => source.kind === 'group' && (source.def === tgt.ref ||
      isDescendantGroup(source.def, tgt.ref) || targetPath === source.sourcePath ||
      targetPath.startsWith(`${source.sourcePath}.`)))) return false
  const groups = tgt.kind === 'device' ? tgt.ref.groups : tgt.ref.subGroups
  return sources.some(source => source.holder !== (source.kind === 'tag' ? tgt.ref.tags : groups))
}
function beginProjectDrag(e: DragEvent, sources: ProjectGridTarget[]) {
  if (!projectEditable.value || operationBusy.value || saving.value || clipboardBusy.value || !sources.length) {
    e.preventDefault(); onDragEnd(); return
  }
  // 多选父组和组内点时只移动父组，避免把同一棵子树拆开移动两次。
  dragProject = proj.value
  dragData.value = sources.filter(source => !sources.some(parent => parent !== source && parent.kind === 'group' &&
    source.sourcePath?.startsWith(`${parent.sourcePath}.`)))
  if (e.dataTransfer) {
    e.dataTransfer.effectAllowed = 'move'
    try { e.dataTransfer.setData('text/plain', dragData.value.map(source => source.sourcePath).join('\n')) } catch {}
  }
}
function onDragStart(e: DragEvent, data: any) {
  if (data.kind !== 'group') { e.preventDefault(); onDragEnd(); return }
  beginProjectDrag(e, [{ kind: data.kind, def: data.ref, holder: data.holder, sourcePath: pathOf(data), selectionKey: data.key }])
}
function onGridDragStart(e: DragEvent, row: any) {
  if (!['group', 'tag'].includes(row?.kind)) { e.preventDefault(); onDragEnd(); return }
  const candidates = new Map<string, ProjectGridTarget>()
  for (const item of [...view.value.rows, ...gridSel.value, row]) candidates.set(gridRowKey(item), item)
  const selected = gridSelectedKeys.value.has(gridRowKey(row))
    ? resolveProjectGridTargets([...candidates.values()], gridSelectedKeys.value)
    : [row]
  beginProjectDrag(e, selected.filter(item => ['group', 'tag'].includes(item.kind)))
}
function gridDropTarget(row: any): any | null {
  return ['group', 'device'].includes(row?.kind) ? treeNodeForDef(row.def) : null
}
function onGridDragOver(e: DragEvent, row: any) { onDragOver(e, gridDropTarget(row)) }
function onGridDrop(e: DragEvent, row: any) { return onHtml5Drop(e, gridDropTarget(row)) }
function onGridDragLeave(row: any) {
  const target = gridDropTarget(row)
  if (target) onDragLeave(target)
}
function onDragOver(e: DragEvent, data: any) {
  if (dragProject !== proj.value || !canDropInto(data)) { if (dropKey.value) dropKey.value = ''; return }
  e.preventDefault(); if (e.dataTransfer) e.dataTransfer.dropEffect = 'move'; dropKey.value = data.key
}
function onDragLeave(data: any) { if (dropKey.value === data.key) dropKey.value = '' }
function onDragEnd() { dragData.value = null; dropKey.value = ''; dragProject = null }
// 节点全路径(通道.设备.组…,与 MesTask.GroupRef 同构)
function pathOf(node: any): string {
  const parts: string[] = []
  for (let n = node; n && n.kind !== 'connectivity' && n.kind !== 'project'; n = n.parent) parts.unshift(n.ref?.name || '')
  return parts.join('.')
}
function isDescendantGroup(g: any, maybeChild: any): boolean {
  for (const sg of g.subGroups || []) { if (sg === maybeChild || isDescendantGroup(sg, maybeChild)) return true }
  return false
}
async function onHtml5Drop(e: DragEvent, tgt: any) {
  e.preventDefault()
  const sources = dragData.value
  const projectAtDrop = dragProject
  onDragEnd()
  if (proj.value !== projectAtDrop || !canDropInto(tgt, sources) || operationBusy.value || saving.value || clipboardBusy.value) return
  try { return await projectOperation.run('moveEntities', t('正在移动标记组…'), async () => {
    if (proj.value !== projectAtDrop) return
    // 目标下级可能尚未载入；先加载，避免下一次加载把刚移动进去的点覆盖掉。
    await ensureLoaded(tgt.ref)
    if (proj.value !== projectAtDrop) return
    const targetPath = pathOf(tgt)
    const devicePath = targetPath.split('.').slice(0, 2).join('.')
    for (const source of sources!) {
      if (source.kind === 'group' && source.sourcePath?.split('.').slice(0, 2).join('.') !== devicePath) {
        await ensureDeepLoaded('group', source.def)
        if (proj.value !== projectAtDrop) return
      }
    }
    if (!projectEditable.value || !canDropInto(tgt, sources)) return
    if (sources!.some(source => !source.holder?.includes(source.def))) return
    for (const source of sources!) {
      const list = source.kind === 'tag' ? (tgt.ref.tags ||= [])
        : tgt.kind === 'device' ? (tgt.ref.groups ||= []) : (tgt.ref.subGroups ||= [])
      if (source.holder === list) continue
      source.holder!.splice(source.holder!.indexOf(source.def), 1)
      source.def.name = uniqueName(source.def.name || t(source.kind === 'tag' ? '标记' : '组'), list.map((item: any) => item.name))
      list.push(source.def)
      const newPath = `${targetPath}.${source.def.name}`
      if (source.sourcePath && newPath !== source.sourcePath) migrateMesRefs(source.sourcePath, newPath)
    }
    markDirty(); bump()
    // 重建选中节点的父链，让移动后右侧路径、QC 范围和后续剪切继续指向真实位置。
    const selected = sel.value?.ref && treeNodeForDef(sel.value.ref)
    if (selected) pickNode(selected)
    ElMessage.success(t('已移动到「{name}」', { name: tgt.ref.name }))
  }) } catch (error: any) { ElMessage.error(error?.message || String(error)) }
}
// 工程路径改变后，统一重写 MES 任务引用；OPC UA NodeId/PLC 地址和 JSON 字段名保持不变。
function migrateMesRefs(oldPath: string, newPath: string) {
  queueQcScopeChange(oldPath, newPath)
  if (mesFlowOpen.value) {
    mesScopeRenames.push({ from: oldPath, to: newPath })
    const path = mesFlowGroup.value
    if (path === oldPath || path.startsWith(`${oldPath}.`)) mesFlowGroup.value = newPath + path.slice(oldPath.length)
  }
  return migrateMesReferences(proj.value?.mesUpload?.tasks || [], oldPath, newPath)
}

// ===================== MES 采集标记 / 开关(对齐桌面树上的 ● 采集中)=====================
function taskFor(path: string): any | null {
  return (proj.value?.mesUpload?.tasks || []).find((t: any) => t.groupRef === path) || null
}
function groupMark(node: any): { text: string; color: string } | null {
  void ver.value
  if (node.kind === 'group') {
    const task = taskFor(pathOf(node))
    return collectionStateMark('group', task?.enabled, 0, t)
  }
  if (node.kind === 'channel' || node.kind === 'device') {
    const prefix = pathOf(node) + '.'
    const n = (proj.value?.mesUpload?.tasks || []).filter((t: any) => t.enabled && t.groupRef && (t.groupRef + '.').startsWith(prefix)).length
    return collectionStateMark(node.kind, undefined, n, t)
  }
  return null
}
async function toggleCollect(node: any) {
  if (!projectEditable.value) return
  try { return await projectOperation.run('toggleCollect', t('正在更新采集状态…'), async () => {
  const task = taskFor(pathOf(node))
  if (!task) { ElMessage.info(t('本数据块还没配置采集任务 — 请先打开「MES 采集流程」完成配置')); return }
  if (!isMesFlowAvailable(task)) { ElMessage.info(t('请先在标记组属性中开启「采集流程」')); return }
  const result = await setMesTaskEnabledWithConfirmation(
    proj.value?.mesUpload,
    { groupRef: task.groupRef, id: task.id },
    !task.enabled,
    confirmMesTaskDisableDialog,
  )
  if (result.cancelled) return
  if (result.conflictGroupRef) {
    ElMessage.warning(t('父级和子级采集任务不能同时开启，请先停用：') + result.conflictGroupRef)
    return
  }
  if (!result.changed) return
  markDirty(); bump()
  mesFlowRef.value?.syncTaskEnabledFromProject?.(String(task.groupRef || ''), task.enabled === true)
  ElMessage.success(t(task.enabled ? '已开启本组采集(保存后生效)' : '已停用本组采集(保存后生效)'))
  }) } catch (error: any) { ElMessage.error(error?.response?.data?.error || error?.message || String(error)) }
}

// MES 设计窗口持有独立工程快照；窗口内切换运行状态后，只同步当前完整组路径，
// 避免重复任务 ID 或并行编辑把其他数据块的运行状态带着改变。
function onMesTaskEnabledChange(groupRef: string, enabled: boolean) {
  const normalizedRef = String(groupRef || '').trim().toLowerCase()
  const current = (proj.value?.mesUpload?.tasks || []).find((item: any) =>
    String(item?.groupRef || '').trim().toLowerCase() === normalizedRef)
  if (!current || current.enabled === enabled) return
  current.enabled = enabled
  bump()
}

// ===================== 自动生成标记(OPC UA / Siemens S7 Plus 浏览 + 树形选择)=====================
async function genTags(node: any) {
  const driverId = node?.channel?.driverId
  if (driverId !== 'OpcUa.Client' && driverId !== 'Siemens.S7') { ElMessage.info(t('当前驱动不支持自动浏览生成标记')); return }
  if (driverId === 'OpcUa.Client' && !String(node.channel?.settings?.url || '').trim()) {
    ElMessage.warning(t('请先在通道属性「UA 服务器」中配置端点地址'))
    return
  }
  // 浏览结果会与设备现有点位合并，先载入设备的全部内容。
  try { await projectOperation.run('genTags', t('正在载入设备点位…'), () => ensureDeepLoaded('device', node.ref)) }
  catch (error) { lazyLoadFailed(error); return }
  opcBrowserNode.value = node
  opcBrowserOpen.value = true
}
function hasConfiguredMesFlow(task: any): boolean {
  return !!task && ((task.flow?.nodes?.length || 0) > 0 || (task.flow?.edges?.length || 0) > 0)
}
async function applyOpcUaTags(payload: {
  tags: ImportedTag[]
  overwriteConflicts: boolean
  sourceDeviceName?: string
  targetDeviceName?: string
}) {
  if (!projectEditable.value) return
  try { return await projectOperation.run('applyOpcUaTags', t('正在导入浏览结果…'), async () => {
  const target = opcBrowserTarget.value
  try {
    const beforeImportShape = projectShape(proj.value)
    const node = opcBrowserNode.value
    const sourceDeviceName = String(payload.sourceDeviceName || '').trim()
    const targetDeviceName = String(payload.targetDeviceName || target?.name || '').trim()
    const oldDevicePath = node ? pathOf(node) : ''
    const newDevicePath = oldDevicePath && targetDeviceName
      ? `${oldDevicePath.split('.').slice(0, -1).join('.')}.${targetDeviceName}`
      : oldDevicePath
    if (payload.targetDeviceName !== undefined) {
    if (!targetDeviceName) throw new Error(t('设备名称不能为空'))
    if (targetDeviceName.includes('.')) throw new Error(t('设备名称不能包含“.”'))
      const duplicate = (node?.channel?.devices || []).some((device: any) => device !== target
        && String(device.name || '').trim().toLocaleLowerCase() === targetDeviceName.toLocaleLowerCase())
    if (duplicate) throw new Error(t('设备名称已存在：{name}', { name: targetDeviceName }))
    }

    const flattened = sourceDeviceName
      ? flattenLegacyOpcUaDeviceRoot(target, sourceDeviceName)
      : { target, changed: false as const, error: undefined as string | undefined }
    if (flattened.error) throw new Error(flattened.error)
    const legacyRootPath = sourceDeviceName && oldDevicePath ? `${oldDevicePath}.${sourceDeviceName}` : ''
    const tasks = proj.value?.mesUpload?.tasks || []
    const legacyTaskIndex = legacyRootPath
      ? tasks.findIndex((task: any) => String(task.groupRef || '').toLocaleLowerCase() === legacyRootPath.toLocaleLowerCase())
      : -1
    if (flattened.changed && legacyTaskIndex >= 0 && hasConfiguredMesFlow(tasks[legacyTaskIndex]))
      throw new Error(t('旧设备根「{name}」存在非空采集流程，请先把流程迁移到具体 DB 后再导入', { name: sourceDeviceName }))

    const preview = buildImportPreview(flattened.target, payload.tags)
    const result = applyTagImport(flattened.target, preview, payload.overwriteConflicts)
    const renamed = !!oldDevicePath && oldDevicePath !== newDevicePath
    if (!result.imported && !flattened.changed && !renamed) {
      ElMessage.info(t('所选标记均已存在，已跳过 {count} 个', { count: preview.skipped }))
      return
    }
    if (flattened.changed && legacyTaskIndex >= 0) tasks.splice(legacyTaskIndex, 1)
    replaceImportedTarget(target, result.target)
    if (flattened.changed && legacyRootPath) migrateMesRefs(legacyRootPath, oldDevicePath)
    if (payload.targetDeviceName !== undefined) target.name = targetDeviceName
    if (renamed) migrateMesRefs(oldDevicePath, newDevicePath)
    flattened.changed ? markConfirmedDeletion(beforeImportShape) : markDirty()
    bump()
    ElMessage.success(t('已导入 {count} 个标记{overwritten}{flattened}(未保存)', {
      count: result.imported,
      overwritten: result.overwritten ? t('，覆盖 {count} 个', { count: result.overwritten }) : '',
      flattened: flattened.changed ? t('，已展开旧设备根') : '',
    }))
  } catch (e: any) {
    ElMessage.error(t('导入失败:') + (e?.message || e))
  }
  }) } catch (error: any) { ElMessage.error(error?.response?.data?.error || error?.message || String(error)) }
}

// ===================== 通道维护 =====================
const reconnectingChannels = ref<Set<string>>(new Set())
const channelReconnectBusy = (name: string) => reconnectingChannels.value.has(String(name || '').toLocaleLowerCase())
async function reconnectChannel(node: any) {
  const name = String(node?.ref?.name || '').trim()
  if (!name || channelReconnectBusy(name) || !monitorEditable.value) return
  const key = name.toLocaleLowerCase()
  reconnectingChannels.value = new Set(reconnectingChannels.value).add(key)
  try {
    const result = await api.reconnectChannel(name)
    ElMessage.success(t('通道「{name}」已重建 {count} 个设备连接', { name, count: result.restarted }))
    await pollRuntimeStatus()
    window.setTimeout(() => pollRuntimeStatus(), 800)
    window.setTimeout(() => pollRuntimeStatus(), 2000)
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error || e?.message || t('通道「{name}」重连失败', { name }))
  } finally {
    const next = new Set(reconnectingChannels.value); next.delete(key); reconnectingChannels.value = next
  }
}

// ===================== 工程属性(对齐桌面 PropertyEditorDialog 项目:左分类+右字段页)=====================
const projOpen = ref(false)
const projForm = ref<any>({})
const projCat = ref('general')
// 分类列表(与桌面一致:未接入的接口置灰、可见不可点)
const projCats = computed(() => [
  { k: 'general', l: t('常规'), on: true }, { k: 'opcda', l: 'OPC DA', on: false },
  { k: 'opcua', l: 'OPC UA', on: true }, { k: 'history', l: t('数据记录'), on: true },
  { k: 'iot', l: t('IoT 发布'), on: true }, { k: 'dde', l: 'DDE', on: false },
  { k: 'fastdde', l: 'FastDDE/SuiteLink', on: false }, { k: 'opcae', l: 'OPC AE', on: false },
  { k: 'opchda', l: 'OPC HDA', on: false }, { k: 'thingworx', l: 'ThingWorx', on: false },
])
const projTotalTags = computed(() => (proj.value?.channels || []).reduce((s: number, c: any) => s + (c.devices || []).reduce((s2: number, d: any) => s2 + devTagCount(d), 0), 0))
function openProjProps() {
  const p = proj.value
  p.settings ||= {}; p.opcUa ||= {}; p.mesUpload ||= {}; p.history ||= {}; p.mqttPublish ||= {}
  const u = p.opcUa.users?.[0] || { username: '', password: '' }
  projForm.value = {
    name: p.name, description: p.description || '',
    // OPC UA(桌面 OPC UA 页全字段)
    uaEnabled: p.opcUa.enabled !== false, uaDiag: !!p.opcUa.diagnosticsEnabled,
    uaAnon: p.opcUa.allowAnonymous !== false, uaUser: u.username || '', uaPass: u.password || '',
    uaEnc: p.opcUa.allowEncryption !== false, uaPort: p.opcUa.port ?? 4840,
    uaMaxSess: p.opcUa.maxSessions ?? 128, uaMinTo: p.opcUa.minSessionTimeoutSec ?? 15, uaMaxTo: p.opcUa.maxSessionTimeoutSec ?? 60,
    uaCacheTo: p.opcUa.tagCacheTimeoutSec ?? 5, uaRetProps: !!p.opcUa.returnTagProperties, uaRetHints: !!p.opcUa.returnAddressHints,
    uaMaxQ: p.opcUa.maxDataQueueSize ?? 2, uaMaxRetx: p.opcUa.maxRetransmitQueueSize ?? 10, uaMaxNotif: p.opcUa.maxNotificationsPerPublish ?? 65536,
    // 数据记录
    histEnabled: !!p.history.enabled, histPath: p.history.dbPath || 'history.db',
    histFlush: p.history.flushIntervalMs ?? 2000, histRows: p.history.retentionRows ?? 1000000,
    // IoT 发布
    mqEnabled: !!p.mqttPublish.enabled, mqHost: p.mqttPublish.brokerHost || '127.0.0.1', mqPort: p.mqttPublish.brokerPort ?? 1883,
    mqPrefix: p.mqttPublish.topicPrefix || 'igw', mqUser: p.mqttPublish.username || '', mqPass: p.mqttPublish.password || '',
    mqQos: p.mqttPublish.qos ?? 0, mqRetain: !!p.mqttPublish.retain, mqInterval: p.mqttPublish.publishIntervalMs ?? 0,
  }
  projCat.value = 'general'
  projOpen.value = true
}
function confirmProjProps() {
  if (!projectEditable.value) return
  const p = proj.value, f = projForm.value
  if (!String(f.name).trim()) { ElMessage.warning(t('填工程名')); return }
  p.name = String(f.name).trim(); p.description = f.description
  const ua = p.opcUa
  ua.enabled = !!f.uaEnabled; ua.diagnosticsEnabled = !!f.uaDiag; ua.allowAnonymous = !!f.uaAnon
  ua.allowEncryption = !!f.uaEnc; ua.port = +f.uaPort || 4840
  ua.users = f.uaUser ? [{ username: String(f.uaUser).trim(), password: f.uaPass || '' }] : []
  ua.maxSessions = +f.uaMaxSess || 128; ua.minSessionTimeoutSec = +f.uaMinTo || 15; ua.maxSessionTimeoutSec = +f.uaMaxTo || 60
  ua.tagCacheTimeoutSec = +f.uaCacheTo || 5; ua.returnTagProperties = !!f.uaRetProps; ua.returnAddressHints = !!f.uaRetHints
  ua.maxDataQueueSize = +f.uaMaxQ || 2; ua.maxRetransmitQueueSize = +f.uaMaxRetx || 10; ua.maxNotificationsPerPublish = +f.uaMaxNotif || 65536
  p.history.enabled = !!f.histEnabled; p.history.dbPath = f.histPath || 'history.db'
  p.history.flushIntervalMs = +f.histFlush || 2000; p.history.retentionRows = +f.histRows || 1000000
  p.mqttPublish.enabled = !!f.mqEnabled; p.mqttPublish.brokerHost = f.mqHost || '127.0.0.1'; p.mqttPublish.brokerPort = +f.mqPort || 1883
  p.mqttPublish.topicPrefix = f.mqPrefix || 'igw'; p.mqttPublish.username = f.mqUser; p.mqttPublish.password = f.mqPass
  p.mqttPublish.qos = +f.mqQos || 0; p.mqttPublish.retain = !!f.mqRetain; p.mqttPublish.publishIntervalMs = +f.mqInterval || 0
  projOpen.value = false; markDirty(); bump(); saveAfterConfirm()
}

// ===================== 工具栏(对齐桌面:Segoe MDL2 图标 + 按选中节点显隐)=====================
const G = { newProj: '', open: '', save: '', reload: '', exp: '', ch: '', dev: '', grp: '', tag: '', props: '', undo: '', cut: '', copy: '', paste: '', del: '' }
// 显隐规则与桌面 ShowNew*/ShowProps 完全一致
// 只有查看权限(projectEditable=false)：工具栏不显示新建/导入/保存/剪切/粘贴/删除
const showNewChannel = computed(() => projectEditable.value && ['connectivity', 'channel', 'device', 'group'].includes(k.value))
const showNewDevice = computed(() => projectEditable.value && ['channel', 'device', 'group'].includes(k.value))
const showNewGroup = computed(() => projectEditable.value && ['device', 'group'].includes(k.value))
const showNewTag = computed(() => projectEditable.value && ['device', 'group'].includes(k.value))
const showProps = computed(() => !!sel.value && (k.value === 'project' || !!sel.value.ref))
function tbProps() { if (k.value === 'project') openProjProps(); else editSelected() }
// 新建空工程(对齐桌面「新建工程」)
async function newProject() {
  if (!projectEditable.value) return
  try { return await projectOperation.run('newProject', t('正在创建空工程…'), async () => {
  if (lazyMode.value) { ElMessage.info(t('大工程请通过“导入 JSON 配置”替换整个工程')); return }
  try { await ElMessageBox.confirm(t('新建空工程会清空当前编辑内容(未保存的会丢失)。继续?'), t('新建工程'), { type: 'warning' }) } catch { return }
  const before = projectShape(proj.value)
  proj.value = { schemaVersion: 1, name: '项目', description: '', settings: { restPort: 48020, opcUaPort: 4840 },
    opcUa: { enabled: true, port: 4840, allowAnonymous: true, allowEncryption: true }, history: {}, mqttPublish: {}, mesUpload: { tasks: [] }, channels: [] }
  markConfirmedDeletion(before); sel.value = null; bump()
  forceFullSave = true
  ElMessage.success(t('已新建空工程(未保存)'))
  }) } catch (error: any) { ElMessage.error(error?.response?.data?.error || error?.message || String(error)) }
}

// 通道/设备「高级参数」:settings 字典的自由键值编辑(与桌面 写优化/定时/降级/冗余 等属性页同键)
function extraRows(settings: any, declared: string[]): any[] {
  const d = new Set(declared)
  return Object.entries(settings || {}).filter(([k]) => !d.has(k)).map(([k, v]) => ({ key: k, value: v }))
}
const chExtra = ref<any[]>([])
const devExtra = ref<any[]>([])
const chSiemensClassicOnly = computed(() => {
  const channel = chPropsRef.value
  if (channel?.driverId !== 'Siemens.S7' || !(channel.devices || []).length) return false
  return channel.devices.every((device: any) => ['S7-200', 'S7-200Smart', 'S7-300', 'S7-400'].includes(device?.settings?.model || 'S7-1200'))
})
const chSupportsManualBatchGap = computed(() =>
  ['Modbus.Tcp', 'Mitsubishi.Mc'].includes(chPropsRef.value?.driverId || '') || chSiemensClassicOnly.value)
const chUsesProtocolCapacityBatching = computed(() => !chSupportsManualBatchGap.value)
function syncExtras(target: any, rows: any[], declared: string[]) {
  const d = new Set(declared)
  for (const k of Object.keys(target)) if (!d.has(k)) delete target[k]
  for (const r of rows) if (String(r.key).trim()) target[String(r.key).trim()] = String(r.value ?? '')
}

// ---------- 从 PLC 文件导入(西门子 DB 源,移植桌面 SiemensDbImporter:同偏移/对齐规则)----------
const dbInput = ref<HTMLInputElement>()
function importSiemensDb() {
  if (!projectEditable.value) return
  if (!['device', 'group'].includes(k.value)) { ElMessage.info(t('请先选设备或标记组再导入')); return }
  dbInput.value?.click()
}
async function onDbFile(e: Event) {
  if (!projectEditable.value) return
  try { return await projectOperation.run('onDbFile', t('正在解析 PLC 文件…'), async () => {
  const input = e.target as HTMLInputElement
  const files = [...(input.files || [])]; if (!files.length) return
  const n = sel.value
  const targetKind = n?.kind
  if (targetKind !== 'device' && targetKind !== 'group') { input.value = ''; return }
  const importingProject = proj.value
  const targetIsCurrent = () => {
    if (proj.value !== importingProject || !proj.value?.channels?.includes(n.channel) || !n.channel.devices?.includes(n.device)) return false
    let current = n
    while (current.kind === 'group') {
      const parent = current.parent
      const children = parent?.kind === 'device' ? parent.ref.groups : parent?.ref?.subGroups
      if (!children?.includes(current.ref)) return false
      current = parent
    }
    return current.kind === 'device' && current.ref === n.device
  }
  try {
      const contents = await Promise.all(files.map((file) => file.text()))
      const dbIndex = contents.findIndex((content) => /\bDATA_BLOCK\b/i.test(content))
      if (dbIndex < 0) { ElMessage.warning(t('选择的文件中没有 DATA_BLOCK 声明')); return }
      const typeSources = contents.filter((_, index) => index !== dbIndex)
      let res = await api.siemensDbPreview(contents[dbIndex], typeSources)
      // TIA 按符号名导出的 DB 源文件不带 DB 号：非优化 DB 按绝对地址访问，必须用对 DB 号，不能猜。
      // 先按块名从 PLC 在线查(S7-1200/1500)，查不到带出上次填的号让用户确认，都没有才空着让用户填
      let dbNumberSource = ''
      if (res.dbNumberKnown === false && !res.optimized) {
        const devicePath = pathOf(n).split('.').slice(0, 2).join('.')
        let number: number | null = null
        let lookupNote = ''
        if (isSiemensNode(n)) {
          try {
            const online = await api.siemensDataBlocks(devicePath)
            number = findDbNumber(online.blocks, res.dbName)
            if (number) dbNumberSource = t('从 PLC 在线查到')
            else lookupNote = online.supported
              ? t('PLC 上没有找到名为「{name}」的 DB。', { name: res.dbName })
              : t('没能从 PLC 在线查询({reason})。', { reason: online.error || t('设备未连接或不是 S7-1200/1500') })
          } catch (error: any) { lookupNote = t('没能从 PLC 在线查询({reason})。', { reason: error?.response?.data?.error || error?.message || '' }) }
        }
        if (!number) {
          const remembered = rememberedDbNumber(res.dbName)
          let answer = ''
          try {
            ({ value: answer } = await ElMessageBox.prompt(
              (lookupNote ? lookupNote + '\n' : '')
                + t('文件「{name}」里没有写 DB 号，请填写它在 TIA 里的 DB 号(程序块列表里名字后面方括号中的数字，如 [DB4306])。', { name: res.dbName })
                + (remembered ? '\n' + t('已带出上次导入这个 DB 时填的号，请确认。') : ''),
              t('填写 DB 号'), { inputValue: remembered ? String(remembered) : '', inputPattern: /^\s*\d{1,5}\s*$/, inputErrorMessage: t('请填写 1–65535 的整数'), confirmButtonText: t('继续导入'), customStyle: { whiteSpace: 'pre-wrap' } as any }) as any)
          } catch { return }
          number = Number(String(answer).trim())
          if (!Number.isInteger(number) || number < 1 || number > 65535) { ElMessage.warning(t('请填写 1–65535 的整数')); return }
          dbNumberSource = remembered === number ? t('上次填写') : t('手工填写')
        }
        res = await api.siemensDbPreview(contents[dbIndex], typeSources, number)
      }
      // 缺 UDT 定义：缺的那一项和(非优化 DB)它后面的点位都导不进来，先让用户补上 .udt 文件
      const missingTypes = [...new Set((res.skipped || []).filter((x: any) => /找不到命名 UDT/.test(x.reason)).map((x: any) => x.type))]
      if (missingTypes.length) {
        try {
          await ElMessageBox.confirm(
            t('缺少 UDT 定义：{types}。请在 TIA 里右键这些 PLC 数据类型 →「从块生成源」导出 .udt 文件，和 DB 文件一起选中后重新导入。', { types: missingTypes.join('、') })
              + '\n\n' + t(res.optimized ? '现在导入的话，这些结构体不会导入。' : '现在导入的话，这些结构体以及它们后面的点位(地址无法计算)不会导入。'),
            t('缺少 UDT 定义'),
            { type: 'warning', confirmButtonText: t('仍导入其余 {count} 个点位', { count: Number(res.tagCount || 0) }), cancelButtonText: t('取消，重新选择文件'), customStyle: { whiteSpace: 'pre-wrap' } as any })
        } catch { return }
      }
      const total = Number(res.tagCount || 0)
      if (!total) { ElMessageBox.alert(t('未从该 DB 解析出可采集点位。请确认文件包含完整的 VAR … END_VAR 或 STRUCT … END_STRUCT 声明。'), t('从 PLC 文件导入'), { type: 'warning' }); return }
      const base = res.dbName || `DB${res.dbNumber}`
      // 必须先载入已有 DB 再按名称合并，不能把未载入的空骨架当成空工程。
      // 只深载入匹配的 DB，其它 DB 仍保持按需载入。
      if (!targetIsCurrent()) return
      await ensureLoaded(n.ref)
      if (!targetIsCurrent() || needsLoad(lazyState, n.ref)) return
      const existing = findSiemensDbImportGroup(n.ref, targetKind, base)
      if (existing) await ensureDeepLoaded('group', existing)
      const fullyLoaded = (group: any): boolean => !needsLoad(lazyState, group) && (group.subGroups || []).every(fullyLoaded)
      if (!targetIsCurrent() || findSiemensDbImportGroup(n.ref, targetKind, base) !== existing || (existing && !fullyLoaded(existing))) return
      const result = mergeSiemensDbImport(n.ref, targetKind, base, res.root)
      if (!result.added && !result.updated) return
      markDirty(); bump()
      if (dbNumberSource) rememberDbNumber(res.dbName, res.dbNumber)   // 下次导入同名 DB 直接带出
      const numberNote = dbNumberSource ? `\n${t('DB 号 {db}：{source}', { db: res.dbNumber, source: dbNumberSource })}` : ''
      const skip = res.skipped.length
        ? `\n${t('未导入:')}${res.skipped.slice(0, 10).map((x) => `${x.path}:${x.type}（${x.reason}）`).join('、')}${res.skipped.length > 10 ? ' …' : ''}`
        : ''
      ElMessageBox.alert(t('已合并 DB{db} 到标记组「{group}」：新增 {added} 个点位，更新 {updated} 个点位；未出现在文件中的原有点位和子组已保留。', { db: res.dbNumber, group: result.group.name, added: result.added, updated: result.updated }) + numberNote + skip + `\n\n${t('点「保存并应用」即可下发。')}`, t('从 PLC 文件导入'), { type: res.skipped.length ? 'warning' : 'success', customStyle: { whiteSpace: 'pre-wrap' } as any })
  } catch (err: any) { ElMessage.error(t('解析失败:') + (err?.response?.data?.error || err.message)) }
  finally { input.value = '' }
  }) } catch (error: any) { ElMessage.error(error?.response?.data?.error || error?.message || String(error)) }
}
// 所在通道是否西门子(菜单显隐与桌面同条件)
const isSiemensNode = (node: any) => node?.channel?.driverId === 'Siemens.S7'

// ---------- CSV 导入 / 导出(当前设备/组的标记)----------
const csvInput = ref<HTMLInputElement>()
async function exportCsv() {
  try { return await projectOperation.run('exportCsv', t('正在生成 CSV…'), async () => {
  const tags = sel.value?.ref?.tags || []
  if (!tags.length) { ElMessage.info(t('本级没有标记')); return }
  const head = 'Name,Address,DataType,Access,ScanRateMs,Description'
  const lines = tags.map((t: any) => [t.name, t.address, t.dataType, t.access, t.scanRateMs, (t.description || '').replace(/,/g, ' ')].join(','))
  const blob = new Blob([[head, ...lines].join('\r\n')], { type: 'text/csv' })
  const a = document.createElement('a'); a.href = URL.createObjectURL(blob); a.download = (sel.value.ref.name || 'tags') + '.csv'; a.click(); URL.revokeObjectURL(a.href)
  }) } catch (error: any) { ElMessage.error(error?.response?.data?.error || error?.message || String(error)) }
}
function importCsv() { if (!projectEditable.value) return; if (!['device', 'group'].includes(k.value)) { ElMessage.info(t('请在设备或组上导入')); return } csvInput.value?.click() }
async function onCsv(e: Event) {
  if (!projectEditable.value) return
  const f = (e.target as HTMLInputElement).files?.[0]; if (!f) return
  const target = sel.value?.ref
  const targetKind = sel.value?.kind
  try { await projectOperation.run('importCsv', t('正在检查并导入 CSV…'), async () => {
    try {
      if (!target) throw new Error(t('导入目标已失效，请重新选择设备或标记组'))
      await ensureDeepLoaded(targetKind, target)
      const parsed = parseKepTagCsv(await f.text())
      if (parsed.errors.length) {
        const details = parsed.errors.slice(0, 12).map((x) => t('第 {row} 行：{message}', { row: x.row, message: x.message })).join('\n')
        await ElMessageBox.alert(t('CSV 未导入，发现 {count} 个错误：', { count: parsed.errors.length }) + `\n${details}${parsed.errors.length > 12 ? '\n…' : ''}`, t('CSV 导入检查'),
          { type: 'error', customStyle: { whiteSpace: 'pre-wrap' } as any })
        return
      }
      if (!parsed.tags.length) { ElMessage.warning(t('CSV 中没有可导入标记')); return }
      const preview = buildImportPreview(target, parsed.tags)
      const conflicts = preview.conflicts.length
      const structuralConflicts = preview.conflicts.filter((item) => item.kind === 'group')
      const summary = `文件：${f.name}\n标记：${parsed.tags.length} 个\n可导入：${preview.tags.length} 个\n已存在跳过：${preview.skipped} 个\n新建标记组：${preview.groupsToCreate} 个\n同路径冲突：${conflicts} 个`
      if (structuralConflicts.length) {
        await ElMessageBox.alert(`${summary}\n\n${t('存在标记与标记组互相阻挡的结构冲突：')}\n${structuralConflicts.slice(0, 10).map((item) => item.path).join('\n')}`, t('CSV 无法导入'),
          { type: 'error', customStyle: { whiteSpace: 'pre-wrap' } as any })
        return
      }
      if (!preview.tags.length) { ElMessage.info(t('CSV 中的 {count} 个标记均已存在，无需重复导入', { count: preview.skipped })); return }
      try {
        await ElMessageBox.confirm(
          `${summary}${conflicts ? '\n\n继续将覆盖同路径标记；原有其他结构保持不变。' : '\n\n确认按文件中的层级结构导入？'}`,
          t('CSV 导入预览'), {
            type: conflicts ? 'warning' : 'info',
            confirmButtonText: t(conflicts ? '覆盖并导入' : '确认导入'), cancelButtonText: t('取消'),
            customStyle: { whiteSpace: 'pre-wrap' } as any,
          })
      } catch { return }
      const result = applyTagImport(target, preview, conflicts > 0)
      replaceImportedTarget(target, result.target)
      markDirty(); bump()
      ElMessage.success(t('已导入 {count} 个标记{overwritten}(未保存)', { count: result.imported, overwritten: result.overwritten ? t('，覆盖 {count} 个', { count: result.overwritten }) : '' }))
    } catch (err: any) { ElMessage.error(t('CSV 导入失败:') + err.message) }
  }) } finally { (e.target as HTMLInputElement).value = '' }
}
</script>

<template>
  <div v-if="lazyMode !== null && proj" class="wrap" @click="hideMenu">
    <!-- 工具栏(对齐桌面:Segoe MDL2 图标 + 按选中节点显隐) -->
    <div class="card bar" :inert="operationBusy || saving || !!clipboardBusy" :aria-busy="operationBusy || saving || !!clipboardBusy">
      <!-- 文件 -->
      <template v-if="projectEditable">
        <button class="tb" :title="$t('新建工程')" :loading="operationBusy" @click="newProject"><span class="mdl">{{ G.newProj }}</span></button>
        <button class="tb" :title="$t('导入 JSON 配置')" @click="importKepProject"><span class="mdl">{{ G.open }}</span></button>
        <button class="tb pri" :title="$t('保存并应用')" :disabled="saveBusy || operationBusy" @click="saveApply(false)"><span class="mdl">{{ G.save }}</span></button>
      </template>
      <button class="tb" :title="$t('重新载入工程数据（不刷新网页）')" :disabled="saving || operationBusy" @click="reload"><span class="mdl">{{ G.reload }}</span></button>
      <button class="tb" :title="$t('导出 JSON 配置')" @click="exportKepProject"><span class="mdl">{{ G.exp }}</span></button>
      <span class="tbsep" />
      <!-- 上下文新建(与桌面同规则,按选中节点显隐) -->
      <button v-if="showNewChannel" class="tb" :title="$t('新建通道')" @click="newChannel"><span class="mdl">{{ G.ch }}</span></button>
      <button v-if="showNewDevice" class="tb" :title="$t('新建设备')" @click="newDevice"><span class="mdl">{{ G.dev }}</span></button>
      <button v-if="showNewGroup" class="tb" :title="$t('新建标记组')" @click="newGroup"><span class="mdl">{{ G.grp }}</span></button>
      <button v-if="showNewTag" class="tb" :title="$t('新建标记')" @click="newTag"><span class="mdl">{{ G.tag }}</span></button>
      <button v-if="showProps" class="tb" :title="$t('属性')" @click="tbProps"><span class="mdl">{{ G.props }}</span></button>
      <span class="tbsep" v-if="showNewChannel || showProps" />
      <!-- 剪贴板 -->
      <button class="tb" :title="$t('撤销(暂不支持)')" disabled><span class="mdl">{{ G.undo }}</span></button>
      <button v-if="projectEditable" class="tb" :title="$t('剪切 (Ctrl+X)')" :disabled="!!clipboardBusy" @click="doCut"><span class="mdl">{{ G.cut }}</span></button>
      <button class="tb" :title="$t('复制 (Ctrl+C)')" :disabled="!!clipboardBusy" @click="doCopy"><span class="mdl">{{ G.copy }}</span></button>
      <template v-if="projectEditable">
        <button class="tb" :title="$t('粘贴 (Ctrl+V)')" :disabled="!canPaste" @click="doPaste"><span class="mdl">{{ G.paste }}</span></button>
        <button class="tb" :title="$t('删除 (Del)')" @click="doDelete"><span class="mdl del">{{ G.del }}</span></button>
      </template>
      <span v-if="clipboardBusy" class="clipboard-status">{{ $t(clipboardBusy === 'copy' ? '正在复制…' : clipboardBusy === 'cut' ? '正在剪切…' : '正在粘贴…') }}</span>
      <span class="tbsep" />
      <!-- Quick Client -->
      <button class="tb qc" :title="$t('在此处打开点位监控(Quick Client)')" @click="quickClient(sel || { kind: 'project' })">QC</button>
      <input ref="csvInput" type="file" accept=".csv" style="display:none" @change="onCsv" />
      <input ref="dbInput" type="file" accept=".db,.scl,.udt,.awl,.txt" multiple style="display:none" @change="onDbFile" />
      <span v-if="operationBusy" class="clipboard-status" role="status" aria-live="polite">{{ operationLabel }}</span>
      <span v-if="projectEditable" class="tip">{{ $t('左键选中 · 右键菜单 · 双击改属性 · 拖组移动 · Ctrl+C/X/V · Del · F5刷新网页') }}</span>
      <el-tag v-if="saving" type="info" effect="light" style="margin-left:auto">{{ $t('保存中…') }}</el-tag>
      <el-tag v-else-if="autoSaveBlocked" type="danger" effect="light" style="margin-left:auto" :title="saveError">{{ $t('自动保存冲突，请手动处理') }}</el-tag>
      <el-tag v-else-if="dirty" :type="autoSaveState === 'error' ? 'danger' : 'warning'" effect="light" style="margin-left:auto" :title="saveError || autoSaveHint">
        {{ autoSaveState === 'error' ? $t('自动保存失败，等待重试')
          : projectAutoSaveSettings.enabled ? $t('待后台保存（每 {seconds} 秒）', { seconds: projectAutoSaveSettings.seconds })
          : $t('有未保存的修改（自动保存已关闭）') }}
      </el-tag>
      <el-tag v-else type="success" effect="plain" style="margin-left:auto" :title="autoSaveHint">
        {{ autoSavedText ? $t('已自动保存 {time}', { time: autoSavedText })
          : projectAutoSaveSettings.enabled ? $t('已同步 · 自动保存已开启') : $t('已同步 · 自动保存已关闭') }}
      </el-tag>
    </div>

    <div class="cols" v-loading="operationBusy || saving || !!clipboardBusy" :element-loading-text="operationLabel || (clipboardBusy ? $t(clipboardBusy === 'copy' ? '正在复制…' : clipboardBusy === 'cut' ? '正在剪切…' : '正在粘贴…') : $t('正在保存并应用工程…'))" :aria-busy="operationBusy || saving || !!clipboardBusy">
      <!-- 左树(宽度可拖) -->
      <div class="card tree" :style="{ width: treeW + 'px' }">
        <el-auto-resizer>
          <template #default="{ height, width }">
            <el-tree-v2 ref="treeRef" :data="treeData" :props="treeProps" :height="height" :width="width" :item-size="26"
                        node-key="key" highlight-current :expand-on-click-node="false"
                        :default-expanded-keys="expandedKeys" @node-click="onNodeClick" @node-contextmenu="onTreeCtx"
                        @node-expand="onNodeExpand" @node-collapse="onNodeCollapse">
              <template #default="{ data }">
                <span class="tn" :class="{ 'drop-ok': dropKey===data.key }" :draggable="projectEditable && data.kind==='group'"
                      @dragstart="onDragStart($event, data)" @dragover="onDragOver($event, data)" @dragleave="onDragLeave(data)" @drop="onHtml5Drop($event, data)" @dragend="onDragEnd"
                      :title="treeNodeTitle(data)" @dblclick.stop="onTreeDbl(data)">
                  <span class="ic">{{ data.icon }}</span>
                  <span class="tnlabel">{{ data.label }}<small v-if="treeDescription(data)" class="tree-description" :title="treeDescription(data)"> · {{ treeDescription(data) }}</small><small v-if="data.kind==='channel'"> · {{ driverName(data.ref.driverId) }}</small><small v-else-if="data.kind==='device'"> ({{ $t('{count} 点', { count: devTagCount(data.ref) }) }})</small><small v-else-if="data.kind==='group'"> ({{ $t('{count} 点', { count: cnt(data.ref) }) }})</small></span>
                  <small v-if="groupMark(data)" class="group-collection-mark" :style="{ color: groupMark(data)!.color }">{{ groupMark(data)!.text }}</small>
                  <small v-if="treeRuntimeMark(data)" class="runtime-mark" :class="`runtime-${treeRuntimeMark(data)!.tone}`" :title="treeRuntimeMark(data)!.title">
                    <i class="runtime-dot"></i>{{ treeRuntimeMark(data)!.text }}
                  </small>
                </span>
              </template>
            </el-tree-v2>
          </template>
        </el-auto-resizer>
      </div>
      <!-- 可拖分隔条 -->
      <div class="vsplit" @mousedown="startResize" :title="$t('拖动调整左右宽度')"></div>

      <!-- 右明细网格 -->
      <div class="card main">
        <template v-if="!sel"><el-empty :description="$t('左侧选择 连接性 / 通道 / 设备 / 组')" :image-size="80" /></template>
        <template v-else>
          <div class="ttl">
            <span class="ic">{{ k==='channel'?'📡':k==='device'?'🖥':k==='group'?'📁':k==='connectivity'?'🔌':'🗂' }}</span>
            <b>{{ k==='connectivity' ? $t('连接性') : k==='project' ? $t('项目') : sel.ref.name }}</b>
            <el-tag v-if="k==='channel'" size="small" effect="plain" :type="isUnsupportedDriver(sel.ref.driverId) ? 'warning' : undefined">{{ driverName(sel.ref.driverId) }}</el-tag>
            <el-button v-if="k==='channel' && monitorEditable" size="small" plain :loading="channelReconnectBusy(sel.ref.name)" @click="reconnectChannel(sel)">{{ $t('立即重连') }}</el-button>
            <el-button v-if="!['project','connectivity'].includes(k)" size="small" text @click="editSelected">{{ $t('属性…') }}</el-button>
            <span class="grow" />
            <el-button v-if="projectEditable" size="small" @click="newByView">{{ view.addLabel }}</el-button>
          </div>
          <div class="gridwrap" :class="{ 'drop-ok': dropKey === sel.key }" @contextmenu="onGridCtx"
               @dragover="onDragOver($event, sel)" @dragleave="onDragLeave(sel)" @drop.stop="onHtml5Drop($event, sel)">
            <el-table ref="gridTableRef" :key="gridTableKey" :data="view.rows" :row-key="gridRowKey" size="small" height="100%" border highlight-current-row
                      lazy :load="loadGridChildren" :tree-props="{ children: 'children', hasChildren: 'hasChildren', checkStrictly: true }"
                      @row-click="onRowClick" @row-dblclick="onRowDblClick" @row-contextmenu="onRowCtx"
                      @selection-change="onSelChange" @header-dragend="onGridColumnResize" :row-class-name="gridRowClass">
              <el-table-column type="selection" width="38" reserve-selection :selectable="(row: any) => row.kind !== 'schema'" />
              <el-table-column v-for="col in visCols" :key="col.i" :label="view.headers[col.i]"
                               :column-key="gridColumnKey(col)" :width="gridColumnWidth(col)"
                               :min-width="col.i===0?170:col.i===5?150:110" :resizable="true" show-overflow-tooltip>
                <template #default="{ row }">
                  <span class="project-grid-drag-cell" :class="{ 'drop-ok': dropKey === gridRowKey(row) }"
                        :draggable="projectEditable && ['group', 'tag'].includes(row.kind)"
                        @dragstart.stop="onGridDragStart($event, row)" @dragend.stop="onDragEnd"
                        @dragover.stop="onGridDragOver($event, row)" @dragleave.stop="onGridDragLeave(row)" @drop.stop="onGridDrop($event, row)">
                    {{ row['c'+(col.i+1)] }}
                  </span>
                </template>
              </el-table-column>
              <template v-if="projectEditable" #empty>
                <div class="emptyhint" @click="newByView">＋ {{ view.addLabel }}{{ $t('(本级为空，点此新建)') }}</div>
              </template>
            </el-table>
          </div>
        </template>
      </div>
    </div>

    <!-- 底部状态栏(对齐桌面 KEP 风格) -->
    <div class="statusbar">
      <span class="dotg" :style="{ background: health ? '#2E8B30' : '#B33' }"></span>
      <span>{{ $t(health ? '就绪' : '未连接') }}</span>
      <span class="sgrow" />
      <span class="sitem">Default User</span>
      <span class="ssep" />
      <span class="sitem">{{ $t('客户端:') }} {{ health?.opcUaSessions ?? 0 }}</span>
      <span class="ssep" />
      <span class="sitem">{{ $t('活动标记:') }} {{ health?.activeTags ?? 0 }} / {{ health?.tags ?? 0 }}</span>
    </div>

    <!-- QC 与 MES 是互不阻塞的工作窗口，可以同时调试。缩小时组件保持挂载。 -->
    <WorkWindow
      v-model="qcOpen"
      :title="$t('OPC Quick Client — 点位监控')"
      :minimized="qcMinimized"
      :maximized="qcMaximized"
      :rect="qcRect"
      :z-index="qcZ"
      @update:minimized="qcMinimized = $event"
      @update:maximized="updateToolMaximized('qc', $event)"
      @update:rect="qcRect = $event"
      @focus="focusTool('qc')"
      @request-close="qcOpen = false; qcMinimized = false; qcMaximized = false"
    >
      <div class="qc-work-content">
        <div v-if="qcPreparing" class="qc-scope-preparing">
          <span class="mes-flow-spinner" />
          <b>{{ pendingQcRoot !== null ? $t('监控范围已变更，保存工程后刷新') : $t('正在保存工程并准备点位监控…') }}</b>
          <small v-if="saveError">{{ saveError }}</small>
        </div>
        <Points v-else-if="qcOpen" ref="qcRef" :q="qcQ" :preferred-scope="qcPreferredScope" :suspended="qcMinimized" :editable="projectEditable" />
      </div>
    </WorkWindow>

    <WorkWindow
      v-model="mesFlowOpen"
      class="mes-tool-window"
      :title="mesFlowDialogTitle(mesFlowGroup, t)"
      :minimized="mesFlowMinimized"
      :maximized="mesFlowMaximized"
      :rect="mesFlowRect"
      :z-index="mesFlowZ"
      @update:minimized="mesFlowMinimized = $event"
      @update:maximized="updateToolMaximized('mes', $event)"
      @update:rect="mesFlowRect = $event"
      @focus="focusTool('mes')"
      @request-close="requestMesFlowClose"
    >
      <template #actions>
        <button class="work-title-action" :title="$t('打开当前流程的点位监控')" @click.stop="openQcForFlow">QC</button>
      </template>
      <div class="mes-work-content">
        <div v-if="mesFlowPreparing" class="mes-flow-preparing">
          <span class="mes-flow-spinner" />
          <b>{{ $t('正在保存工程并准备采集流程…') }}</b>
          <small>{{ $t('大型点位工程首次打开可能需要片刻，窗口不会重复创建。') }}</small>
        </div>
        <MesFlow v-else-if="mesFlowOpen"
          ref="mesFlowRef"
          :focus-group="mesFlowGroup"
          design
          @active-group-change="onMesFlowActiveGroupChanged"
          @task-enabled-change="onMesTaskEnabledChange"
        />
      </div>
    </WorkWindow>

    <div v-if="(qcOpen && qcMinimized) || (mesFlowOpen && mesFlowMinimized)" class="workbar">
      <button v-if="qcOpen && qcMinimized" @click="restoreTool('qc')">QC · {{ $t('点位监控') }}</button>
      <button v-if="mesFlowOpen && mesFlowMinimized" @click="restoreTool('mes')">MES · {{ $t('采集流程') }}</button>
    </div>

    <SiemensAuthenticationDialog
      v-model="siemensAuthOpen"
      :device-name="siemensAuthDeviceName"
      :editable="projectEditable"
      @verified="syncSiemensAuthentication"
      @deleted="syncSiemensAuthentication"
    />

    <!-- 通道诊断(设备连接状态,对齐桌面「诊断」窗) -->
    <el-dialog v-model="diagOpen" :title="$t('通道诊断 — {name}', { name: diagCh?.ref?.name || '' })" width="820" append-to-body>
      <el-alert v-if="runtimeStatusError" type="warning" :closable="false" show-icon :title="runtimeStatusError" style="margin-bottom:8px" />
      <div style="margin-bottom:8px;color:#888;font-size:12px">{{ $t('与工程树同步，通过 SignalR 实时更新。最近更新时间：{time}', { time: runtimeLastUpdated || $t('等待实时状态') }) }}</div>
      <el-table :data="diagRows" size="small" border v-loading="diagLoading" max-height="52vh">
        <el-table-column prop="name" :label="$t('设备')" min-width="140" show-overflow-tooltip />
        <el-table-column :label="$t('状态')" width="150">
          <template #default="{ row }">
            <span :title="row.runtimeMark.title" :style="{ color: diagToneColor(row.runtimeMark.tone), fontWeight: 600 }">● {{ row.runtimeMark.text }}</span>
            <div v-if="row.detailMessage" style="margin-top:2px;color:#777;font-size:11px;line-height:1.3">{{ row.detailMessage }}</div>
          </template>
        </el-table-column>
        <el-table-column :label="$t('点数 / 活动')" width="100"><template #default="{ row }">{{ row.tagCount }} / {{ row.activeTags }}</template></el-table-column>
        <el-table-column :label="$t('成功率')" width="90"><template #default="{ row }">{{ row.stats ? row.stats.successRate + '%' : '—' }}</template></el-table-column>
        <el-table-column :label="$t('最近扫描')" width="90"><template #default="{ row }">{{ row.stats ? row.stats.lastScanMs + 'ms' : '—' }}</template></el-table-column>
        <el-table-column :label="$t('最近错误')" min-width="160" show-overflow-tooltip><template #default="{ row }"><span style="color:#C0392B">{{ row.stats?.lastError || '' }}</span></template></el-table-column>
        <template #empty><span style="color:#999">{{ $t('本通道下没有设备') }}</span></template>
      </el-table>
      <template #footer><el-button @click="loadDiag" :loading="diagLoading">{{ $t('刷新') }}</el-button><el-button type="primary" @click="diagOpen=false">{{ $t('关闭') }}</el-button></template>
    </el-dialog>

    <!-- 浮动右键菜单 -->
    <ProjectContextMenu v-if="menu.show" :x="menu.x" :y="menu.y" :actions="menu.items" @choose="clickItem" />
    <ProjectImportDialog v-model="projectImportOpen" @imported="onProjectImported" />
    <ProjectExportDialog v-model="projectExportOpen" />

    <OpcUaTagBrowserDialog v-if="opcBrowserNode" v-model="opcBrowserOpen"
      :connection="opcBrowserConnection" :device-name="opcBrowserDeviceName || undefined" :target="opcBrowserTarget" :editable="projectEditable" @apply="applyOpcUaTags" />

    <!-- 添加向导(通道/设备,对齐桌面 ChannelWizard/DeviceWizard:多步 + 汇总) -->
    <el-dialog v-model="wiz.open" :show-close="!operationBusy" :close-on-press-escape="!operationBusy" :title="$t(wiz.kind === 'channel' ? '添加通道向导' : '添加设备向导')" width="600" append-to-body :close-on-click-modal="false">
      <div class="wizhd">{{ $t('第 {current} 步，共 {total} 步', { current: wiz.step + 1, total: wiz.steps.length }) }}</div>
      <div class="wizinstr">{{ wizInstr[wizStepKey] }}</div>
      <div class="wizbody">
        <!-- 通道:类型 -->
        <template v-if="wizStepKey === 'type'">
          <div class="wl">{{ $t('通道类型:') }}</div>
          <el-select v-model="wiz.driverId" style="width:100%">
            <el-option v-for="t in CH_TYPES" :key="t.id" :value="t.id" :label="t.name" :disabled="isUnlicensedDriver(t.id)">
              <span>{{ t.name }}</span><span v-if="isUnlicensedDriver(t.id)" class="wiz-unlicensed">{{ $t('未授权') }}</span>
            </el-option>
          </el-select>
        </template>
        <!-- 通道/设备:名称 -->
        <template v-else-if="wizStepKey === 'name' || wizStepKey === 'dname'">
          <div class="wl">{{ $t('名称:') }}</div><el-input v-model="wiz.f.name" />
        </template>
        <template v-else-if="wizStepKey === 'ua'">
          <div class="wl">{{ $t('上游 OPC UA 服务器地址:') }}</div><el-input v-model="wiz.f.uaUrl" placeholder="opc.tcp://192.168.1.10:4840" />
          <div class="hint" style="margin-top:6px">{{ $t('这里填写需要采集的远端 OPC UA 地址，不是 IGW 设置中的本机 OPC UA Server 地址。') }}</div>
          <div class="wl" style="margin-top:12px">{{ $t('安全策略:') }}</div>
          <el-select v-model="wiz.f.uaSecurity" style="width:100%"><el-option value="None" /><el-option value="Basic256Sha256" /></el-select>
          <div class="wl" style="margin-top:12px">{{ $t('匿名登录:') }}</div><el-switch v-model="wiz.f.uaAnonymous" />
          <template v-if="!wiz.f.uaAnonymous">
            <div class="wl" style="margin-top:12px">{{ $t('用户名:') }}</div><el-input v-model="wiz.f.uaUsername" />
            <div class="wl" style="margin-top:12px">{{ $t('密码:') }}</div><el-input v-model="wiz.f.uaPassword" show-password />
          </template>
          <div class="wl" style="margin-top:12px">{{ $t('连接超时(毫秒):') }}</div><el-input v-model="wiz.f.uaTimeoutMs" />
        </template>
        <template v-else-if="wizStepKey === 'net'">
          <div class="wl">{{ $t('网络适配器:') }}</div>
          <el-select v-model="wiz.f.net" filterable :loading="networkAdaptersLoading" style="width:100%">
            <el-option :value="DEFAULT_ADAPTER_VALUE" :label="$t('默认值（由操作系统选择）')" />
            <el-option v-for="option in wizAdapterOptions" :key="option.value" :value="option.value"
              :label="option.unavailable ? `${option.label}（${$t('不可用')}）` : option.label" :disabled="option.unavailable" />
          </el-select>
          <div class="hint">{{ $t('显式选择后，IGW 会从该网卡的 IPv4 地址建立南向连接。') }}</div>
        </template>
        <template v-else-if="wizStepKey === 'write'">
          <div class="wl">{{ $t('优化方法:') }}</div>
          <el-select v-model="wiz.f.writeOpt" style="width:100%">
            <el-option value="写入所有标记的所有值" :label="$t('写入所有标记的所有值')" /><el-option value="仅写入非布尔型标记的最新值" :label="$t('仅写入非布尔型标记的最新值')" /><el-option value="仅写入所有标记的最新值" :label="$t('仅写入所有标记的最新值')" />
          </el-select>
          <div class="wl" style="margin-top:14px">{{ $t('工作循环:') }}</div><el-input v-model="wiz.f.duty" style="width:140px" />
        </template>
        <template v-else-if="wizStepKey === 'float'">
          <div class="wl">{{ $t('浮点值:') }}</div>
          <el-select v-model="wiz.f.float" style="width:220px"><el-option value="替换为零" :label="$t('替换为零')" /><el-option value="不修改" :label="$t('不修改')" /></el-select>
        </template>
        <!-- 设备:型号 -->
        <template v-else-if="wizStepKey === 'model'">
          <div class="wl">{{ $t('型号:') }}</div>
          <el-select v-model="wiz.f.model" style="width:240px"><el-option value="S7-1200" /><el-option value="S7-1500" /><el-option value="S7-300" /><el-option value="S7-400" /><el-option value="S7-200" /></el-select>
        </template>
        <template v-else-if="wizStepKey === 'id'">
          <div class="wl">ID:</div><el-input v-model="wiz.f.id" :placeholder="$t('设备 IP / 端点')" />
        </template>
        <template v-else-if="wizStepKey === 'scan'">
          <div class="wl">{{ $t('扫描模式:') }}</div>
          <el-select v-model="wiz.f.scanMode" style="width:100%"><el-option value="请求所有数据以指定的扫描速率" :label="$t('请求所有数据以指定的扫描速率')" /><el-option value="遵循客户端指定的扫描速率" :label="$t('遵循客户端指定的扫描速率')" /></el-select>
          <div class="wl" style="margin-top:14px">{{ $t('来自缓存的初始更新:') }}</div>
          <el-select v-model="wiz.f.initCache" style="width:160px"><el-option value="禁用" :label="$t('禁用')" /><el-option value="启用" :label="$t('启用')" /></el-select>
        </template>
        <template v-else-if="wizStepKey === 'timeout'">
          <div class="wl">{{ $t('连接超时(秒):') }}</div><el-input v-model="wiz.f.connTimeout" style="width:140px" />
          <div class="wl" style="margin-top:10px">{{ $t('请求超时(毫秒):') }}</div><el-input v-model="wiz.f.reqTimeout" style="width:140px" />
          <div class="wl" style="margin-top:10px">{{ $t('超时前尝试次数:') }}</div><el-input v-model="wiz.f.retries" style="width:140px" />
          <div class="wl" style="margin-top:10px">{{ $t('请求间延迟(毫秒):') }}</div><el-input v-model="wiz.f.delay" style="width:140px" />
        </template>
        <template v-else-if="wizStepKey === 'demote'">
          <div class="wl">{{ $t('故障时降级:') }}</div>
          <el-select v-model="wiz.f.demote" style="width:160px"><el-option value="禁用" :label="$t('禁用')" /><el-option value="启用" :label="$t('启用')" /></el-select>
          <div class="wl" style="margin-top:12px">{{ $t('降级前失败次数:') }}</div><el-input v-model="wiz.f.demoteAfter" style="width:140px" />
          <div class="wl" style="margin-top:12px">{{ $t('降级时长(毫秒):') }}</div><el-input v-model="wiz.f.demotePeriod" style="width:140px" />
        </template>
        <template v-else-if="wizStepKey === 's7addr'">
          <div class="wl">{{ $t('字节顺序:') }}</div>
          <el-select v-model="wiz.f.byteOrder" style="width:200px"><el-option value="大端字节序" :label="$t('大端字节序')" /><el-option value="小端字节序" :label="$t('小端字节序')" /></el-select>
        </template>
        <template v-else-if="wizStepKey === 'protocol'">
          <ProtocolDeviceSettings v-model="wiz.f.protocolSettings" :driver-id="wiz.driverId" :editable="projectEditable" />
        </template>
        <template v-else-if="wizStepKey === 'fins'">
          <div class="wl">{{ $t('目标节点号 DA1:') }}</div><el-input v-model="wiz.f.destNode" style="width:140px" :placeholder="$t('留空=取 IP 末位')" />
          <div class="wl" style="margin-top:12px">{{ $t('源节点号 SA1:') }}</div><el-input v-model="wiz.f.srcNode" style="width:140px" />
        </template>
        <template v-else-if="wizStepKey === 'abparam'">
          <div class="wl">{{ $t('PLC 型号:') }}</div>
          <el-select v-model="wiz.f.abPlc" style="width:240px"><el-option value="ControlLogix" /><el-option value="CompactLogix" /><el-option value="Micro800" /><el-option value="MicroLogix" /><el-option value="SLC500" /><el-option value="PLC5" /></el-select>
          <div class="wl" style="margin-top:12px">{{ $t('CPU 路径:') }}</div><el-input v-model="wiz.f.abPath" style="width:160px" />
        </template>
        <!-- 汇总 -->
        <template v-else-if="wizStepKey === 'summary'">
          <div class="wzsum">
            <div class="wzh">{{ $t('标识') }}</div>
            <div class="wzr"><span>{{ $t('名称') }}</span><b>{{ wiz.f.name }}</b></div>
            <div class="wzr"><span>{{ $t('驱动程序') }}</span><b>{{ wiz.kind === 'channel' ? wizDriverDisplay : wiz.driverName }}</b></div>
            <template v-if="wiz.kind === 'channel'">
              <template v-if="wiz.driverId === 'OpcUa.Client'">
                <div class="wzh">{{ $t('UA 服务器') }}</div>
                <div class="wzr"><span>{{ $t('端点 URL') }}</span><b>{{ normalizeOpcUrl(wiz.f.uaUrl) }}</b></div>
                <div class="wzr"><span>{{ $t('安全策略') }}</span><b>{{ wiz.f.uaSecurity }}</b></div>
                <div class="wzr"><span>{{ $t('登录方式') }}</span><b>{{ wiz.f.uaAnonymous ? $t('匿名') : wiz.f.uaUsername }}</b></div>
                <div class="wzr"><span>{{ $t('连接超时') }}</span><b>{{ wiz.f.uaTimeoutMs }} ms</b></div>
                <div class="wzh">{{ $t('以太网设置') }}</div><div class="wzr"><span>{{ $t('网络适配器') }}</span><b>{{ wizAdapterLabel }}</b></div>
              </template>
              <template v-else>
              <div class="wzh">{{ $t('诊断') }}</div><div class="wzr"><span>{{ $t('诊断数据捕获') }}</span><b>{{ $t('禁用') }}</b></div>
              <div class="wzh">{{ $t('标记计数') }}</div><div class="wzr"><span>{{ $t('静态标记') }}</span><b>0</b></div>
              <div class="wzh">{{ $t('以太网设置') }}</div><div class="wzr"><span>{{ $t('网络适配器') }}</span><b>{{ wizAdapterLabel }}</b></div>
              <div class="wzh">{{ $t('写优化') }}</div>
              <div class="wzr"><span>{{ $t('优化方法') }}</span><b>{{ $t(wiz.f.writeOpt) }}</b></div><div class="wzr"><span>{{ $t('工作循环') }}</span><b>{{ wiz.f.duty }}</b></div>
              <div class="wzh">{{ $t('非规范浮点数处理') }}</div><div class="wzr"><span>{{ $t('浮点值') }}</span><b>{{ $t(wiz.f.float) }}</b></div>
              </template>
            </template>
            <template v-else>
              <div class="wzr" v-if="wiz.driverId==='Siemens.S7'"><span>{{ $t('型号') }}</span><b>{{ wiz.f.model }}</b></div>
              <div class="wzr"><span>{{ $t('通道分配') }}</span><b>{{ wiz.channelName }}</b></div>
              <div class="wzr" v-if="wiz.driverId !== 'OpcUa.Client'"><span>ID</span><b>{{ wiz.f.id }}</b></div>
              <div class="wzr" v-else><span>{{ $t('连接') }}</span><b>{{ $t('使用通道 UA 服务器') }}</b></div>
              <div class="wzh">{{ $t('操作模式') }}</div><div class="wzr"><span>{{ $t('数据收集') }}</span><b>{{ $t('启用') }}</b></div>
              <div class="wzh">{{ $t('扫描模式') }}</div>
              <div class="wzr"><span>{{ $t('扫描模式') }}</span><b>{{ $t(wiz.f.scanMode) }}</b></div><div class="wzr"><span>{{ $t('来自缓存的初始更新') }}</span><b>{{ $t(wiz.f.initCache) }}</b></div>
              <template v-if="wiz.driverId !== 'Modbus.Tcp'">
              <div class="wzh">{{ $t('通信超时') }}</div>
              <div class="wzr"><span>{{ $t('连接超时(秒)') }}</span><b>{{ wiz.f.connTimeout }}</b></div><div class="wzr"><span>{{ $t('请求超时(毫秒)') }}</span><b>{{ wiz.f.reqTimeout }}</b></div><div class="wzr"><span>{{ $t('超时前尝试次数') }}</span><b>{{ wiz.f.retries }}</b></div>
              </template>
              <div class="wzh">{{ $t('自动降级') }}</div>
              <div class="wzr"><span>{{ $t('故障时降级') }}</span><b>{{ $t(wiz.f.demote) }}</b></div><div class="wzr"><span>{{ $t('降级前失败次数') }}</span><b>{{ wiz.f.demoteAfter }}</b></div><div class="wzr"><span>{{ $t('降级时长(毫秒)') }}</span><b>{{ wiz.f.demotePeriod }}</b></div>
              <template v-if="wiz.driverId==='Siemens.S7'"><div class="wzh">{{ $t('寻址选项') }}</div><div class="wzr"><span>{{ $t('字节顺序') }}</span><b>{{ $t(wiz.f.byteOrder) }}</b></div></template>
              <template v-else-if="wiz.driverId === 'Modbus.Tcp'"><div class="wzh">{{ wizDriverDisplay }}</div><div class="wzr" v-for="(value, key) in wiz.f.protocolSettings" :key="key"><span>{{ key }}</span><b>{{ value }}</b></div></template>
              <template v-else-if="wiz.driverId==='Omron.Fins'"><div class="wzh">{{ $t('FINS 参数') }}</div><div class="wzr"><span>{{ $t('目标节点') }}</span><b>{{ wiz.f.destNode || '(IP 末位)' }}</b></div><div class="wzr"><span>{{ $t('源节点') }}</span><b>{{ wiz.f.srcNode }}</b></div></template>
              <template v-else-if="wiz.driverId==='AB.Eip'"><div class="wzh">{{ $t('EtherNet/IP 参数') }}</div><div class="wzr"><span>{{ $t('PLC 型号') }}</span><b>{{ wiz.f.abPlc }}</b></div><div class="wzr"><span>{{ $t('CPU 路径') }}</span><b>{{ wiz.f.abPath }}</b></div></template>
            </template>
          </div>
        </template>
      </div>
      <template #footer>
        <el-button @click="wiz.open = false">{{ $t('取消') }}</el-button>
        <el-button v-if="wiz.step > 0" @click="wizBack">{{ $t('上一步') }}</el-button>
        <el-button v-if="projectEditable" type="primary" @click="wizNext">{{ $t(wizIsLast ? '完成' : '下一步') }}</el-button>
      </template>
    </el-dialog>

    <!-- 通道属性(对齐桌面属性编辑器:左分类+右字段页) -->
    <el-dialog v-model="chPropsOpen" :show-close="!operationBusy" :close-on-press-escape="!operationBusy" :title="$t('属性编辑器 - {name}', { name: chForm.name || '' })" width="680" append-to-body>
      <div class="pe" v-if="chPropsRef">
        <div class="pe-cats">
          <div v-for="c in chCats" :key="c.k" class="pe-cat" :class="{ on: chCat === c.k }" @click="chCat = c.k">{{ c.l }}</div>
        </div>
        <div class="pe-body">
          <template v-if="chCat === 'general'">
            <div class="pe-hd">{{ $t('标识') }}</div>
            <el-form :disabled="!projectEditable" label-width="120px" size="small">
              <el-form-item :label="$t('名称')"><el-input v-model="chForm.name" /></el-form-item>
              <el-form-item :label="$t('说明')"><el-input v-model="chForm.description" /></el-form-item>
              <el-form-item :label="$t('驱动程序')"><span class="ro-green">{{ driverName(chPropsRef.driverId) }}</span></el-form-item>
            </el-form>
            <div class="pe-hd">{{ $t('诊断') }}</div>
            <el-form :disabled="!projectEditable" label-width="120px" size="small">
              <el-form-item :label="$t('诊断数据捕获')"><el-select v-model="chForm.settings.diagnostics" style="width:100%"><el-option value="禁用" :label="$t('禁用')" /><el-option value="启用" :label="$t('启用')" /></el-select></el-form-item>
            </el-form>
            <div class="pe-hd">{{ $t('标记计数') }}</div>
            <el-form :disabled="!projectEditable" label-width="120px" size="small">
              <el-form-item :label="$t('静态标记')"><span class="ro">{{ chTagCount(chPropsRef) }}</span></el-form-item>
            </el-form>
          </template>
          <template v-else-if="chCat === 'ua'">
            <div class="pe-hd">{{ $t('上游 OPC UA 服务器') }}</div>
            <el-form :disabled="!projectEditable" label-width="130px" size="small">
              <el-form-item :label="$t('远端 URL')"><el-input v-model="chForm.settings.url" placeholder="opc.tcp://192.168.1.10:4840" /></el-form-item>
              <el-form-item :label="$t('安全策略')">
                <el-select v-model="chForm.settings.securityPolicy" style="width:100%"><el-option value="None" /><el-option value="Basic256Sha256" /></el-select>
              </el-form-item>
              <el-form-item :label="$t('匿名登录')"><el-switch v-model="chForm.settings.anonymous" active-value="true" inactive-value="false" /></el-form-item>
              <template v-if="chForm.settings.anonymous === 'false'">
                <el-form-item :label="$t('用户名')"><el-input v-model="chForm.settings.username" /></el-form-item>
                <el-form-item :label="$t('密码')"><el-input v-model="chForm.settings.password" show-password /></el-form-item>
              </template>
              <el-form-item :label="$t('连接超时(ms)')"><el-input v-model="chForm.settings.timeoutMs" /></el-form-item>
              <el-form-item :label="$t('单请求耗时预算')">
                <el-select v-model="chForm.settings.requestBudgetMs" filterable allow-create default-first-option style="width:100%">
                  <el-option value="auto" :label="$t('自动（按实测开销）')" />
                  <el-option value="0" :label="$t('不限（只追求吞吐）')" />
                  <el-option value="200" label="200 ms" />
                  <el-option value="500" label="500 ms" />
                </el-select>
              </el-form-item>
              <el-form-item :label="$t('结构体数组整读')">
                <el-switch v-model="chForm.settings.structArrayReads" active-value="true" inactive-value="false" />
              </el-form-item>
            </el-form>
            <div class="hint">{{ $t('单请求耗时预算：限制每个读写请求占用 PLC 的时间，大读取会拆成多块，写入可插在中间执行。S7-1500 等串行处理请求的 PLC 建议保持自动。') }}</div>
            <div class="hint">{{ $t('结构体数组整读：地址形如 DB.数组[3].成员 的点位，一次读回整个数组再拆分，请求数从上千降到一次。服务器不提供结构定义时自动退回逐点读取。') }}</div>
            <div v-if="chLegacyEndpointHint" class="sim-notice">{{ chLegacyEndpointHint }}</div>
            <div class="sim-notice">{{ $t('此地址只用于 OPC UA Client 向远端服务器发起连接，与 IGW 本机 OPC UA Server 端口互不联动。') }}</div>
            <div class="hint">{{ $t('此通道对应一台上游 OPC UA 服务器。通道下设备仅用于组织导入的变量结构，不再单独配置端点。') }}</div>
          </template>
          <template v-else-if="chCat === 'eth'">
            <div class="pe-hd">{{ $t('以太网设置') }}</div>
            <el-form :disabled="!projectEditable" label-width="120px" size="small">
              <el-form-item :label="$t('网络适配器')">
                <el-select v-model="chAdapterSelection" filterable :loading="networkAdaptersLoading" style="width:100%">
                  <el-option value="" :label="$t('默认值（由操作系统选择）')" />
                  <el-option v-for="option in chAdapterOptions" :key="option.value" :value="option.value"
                    :label="option.unavailable ? `${option.label}（${$t('不可用')}）` : option.label" :disabled="option.unavailable" />
                </el-select>
              </el-form-item>
            </el-form>
            <div class="hint">{{ $t('显式选择后，IGW 会从该网卡的 IPv4 地址建立南向连接；默认值由操作系统路由选择。') }}</div>
          </template>
          <template v-else-if="chCat === 'write'">
            <div class="pe-hd">{{ $t('写优化') }}</div>
            <el-form :disabled="!projectEditable" label-width="120px" size="small">
              <el-form-item :label="$t('优化方法')">
                <el-select v-model="chForm.settings.writeOptimization" style="width:100%">
                <el-option value="写入所有标记的所有值" :label="$t('写入所有标记的所有值')" /><el-option value="仅写入非布尔型标记的最新值" :label="$t('仅写入非布尔型标记的最新值')" /><el-option value="仅写入所有标记的最新值" :label="$t('仅写入所有标记的最新值')" />
                </el-select>
              </el-form-item>
              <el-form-item :label="$t('工作循环')"><el-input v-model="chForm.settings.dutyCycle" placeholder="10" /></el-form-item>
            </el-form>
            <div class="hint">{{ $t('决定写请求如何合并以减轻设备负担;工作循环=每多少次写插入一次读(1–10)。') }}</div>
          </template>
          <template v-else-if="chCat === 'adv'">
            <div class="pe-hd">{{ $t('非规范浮点数处理') }}</div>
            <el-form :disabled="!projectEditable" label-width="120px" size="small">
              <el-form-item :label="$t('浮点值')"><el-select v-model="chForm.settings.nonNormalFloat" style="width:100%"><el-option value="替换为零" :label="$t('替换为零')" /><el-option value="不修改" :label="$t('不修改')" /></el-select></el-form-item>
            </el-form>
            <div class="hint">{{ $t('如何处理设备返回的非规范(NaN/Inf 等)浮点值。') }}</div>
            <div class="pe-hd">{{ $t('自动批处理') }}</div>
            <el-form :disabled="!projectEditable" label-width="120px" size="small">
              <template v-if="chUsesProtocolCapacityBatching">
                <el-form-item :label="$t('自动批处理')"><span class="ro">{{ $t('由协议容量自动决定') }}</span></el-form-item>
              </template>
              <template v-else>
                <el-form-item :label="$t('自动批处理')">
                  <el-select v-model="chForm.settings.batchingMode" style="width:100%">
                    <el-option value="auto" :label="$t('自动批处理')" />
                    <el-option value="manual" :label="$t('手动兼容')" />
                  </el-select>
                </el-form-item>
                <el-form-item v-if="chForm.settings.batchingMode === 'manual'" :label="$t('手动最大间隙')">
                  <el-input v-model="chForm.settings.maxGap" inputmode="numeric" />
                </el-form-item>
              </template>
            </el-form>
            <div class="hint">{{ $t('自动批处理按协议容量和请求成本调整；需要兼容旧工程时才启用手动最大间隙。') }}</div>
          </template>
          <template v-else-if="chCat === 'persist'">
            <div class="pe-hd">{{ $t('持久化') }}</div>
            <el-form :disabled="!projectEditable" label-width="120px" size="small">
              <el-form-item :label="$t('项持久化')"><el-select v-model="chForm.settings.itemPersistence" style="width:100%"><el-option value="禁用" :label="$t('禁用')" /><el-option value="启用" :label="$t('启用')" /></el-select></el-form-item>
              <el-form-item :label="$t('数据文件')"><el-input v-model="chForm.settings.dataFile" /></el-form-item>
            </el-form>
          </template>
          <template v-else-if="chCat === 'driver'">
            <div class="pe-hd">{{ $t('驱动参数({name})', { name: chPropsDriver?.displayName || '' }) }}</div>
            <el-form :disabled="!projectEditable" label-width="120px" size="small">
              <el-form-item v-for="s in chPropsDriver?.channelSettings || []" :key="s.key" :label="s.label">
                <el-input v-model="chForm.settings[s.key]" :placeholder="s.default" />
              </el-form-item>
            </el-form>
            <div class="hint" v-if="chPropsDriver?.addressHelp">{{ $t('地址格式:') }}{{ chPropsDriver.addressHelp }}</div>
          </template>
          <template v-else-if="chCat === 'custom'">
            <div class="pe-hd">{{ $t('自定义参数(自由键值)') }}</div>
            <div v-for="(r, ri) in chExtra" :key="ri" class="kvrow">
              <el-input v-model="r.key" :disabled="!projectEditable" :placeholder="$t('键')" size="small" style="width:150px" />
              <el-input v-model="r.value" :disabled="!projectEditable" :placeholder="$t('值')" size="small" />
              <el-button v-if="projectEditable" size="small" text type="danger" @click="chExtra.splice(ri, 1)">{{ $t('删') }}</el-button>
            </div>
            <el-button v-if="projectEditable" size="small" text @click="chExtra.push({ key: '', value: '' })">{{ $t('+ 参数') }}</el-button>
          </template>
        </div>
      </div>
      <template #footer><el-button @click="chPropsOpen=false">{{ $t(projectEditable ? '取消' : '关闭') }}</el-button><el-button v-if="projectEditable" type="primary" :loading="operationBusy" @click="confirmChProps">{{ $t('确定') }}</el-button></template>
    </el-dialog>

    <!-- 新建设备(简表,对齐桌面 DeviceWizard;属性编辑在下面的分类对话框) -->
    <el-dialog v-model="devOpen" :title="$t('新建设备')" width="520" append-to-body>
      <el-form :disabled="!projectEditable" label-width="110px">
        <el-form-item :label="$t('名称')"><el-input v-model="devForm.name" /></el-form-item>
        <el-form-item v-if="devTargetList?.driverId !== 'OpcUa.Client'" :label="$t('连接地址')"><el-input v-model="devForm.endpoint" :placeholder="$t('如 192.168.1.10:502')" /></el-form-item>
        <div v-else class="hint" style="margin-bottom:12px">{{ $t('连接地址由通道的「UA 服务器」统一提供，设备只用于组织标记结构。') }}</div>
        <el-form-item :label="$t('说明')"><el-input v-model="devForm.description" /></el-form-item>
        <ProtocolDeviceSettings v-if="devTargetList?.driverId === 'Modbus.Tcp'" v-model="devForm.settings" :driver-id="devTargetList.driverId" :editable="projectEditable" />
        <el-form-item v-for="s in devTargetList?.driverId === 'Modbus.Tcp' ? [] : devDriver?.deviceSettings || []" :key="s.key" :label="s.label">
          <el-input v-model="devForm.settings[s.key]" :placeholder="s.default" />
        </el-form-item>
      </el-form>
      <template #footer><el-button @click="devOpen=false">{{ $t(projectEditable ? '取消' : '关闭') }}</el-button><el-button v-if="projectEditable" type="primary" @click="confirmDevice">{{ $t('加入') }}</el-button></template>
    </el-dialog>

    <!-- 设备属性(对齐桌面属性编辑器:左分类+右字段页,字段/键与桌面完全一致) -->
    <el-dialog v-model="devPropsOpen" :show-close="!operationBusy" :close-on-press-escape="!operationBusy" :title="$t('属性编辑器 - {name}', { name: `${devPChName}.${devPForm.name || ''}` })" width="720" append-to-body>
      <div class="pe" v-if="devPropsRef">
        <div class="pe-cats">
          <div v-for="c in devCats" :key="c.k" class="pe-cat" :class="{ on: devCat === c.k }" @click="devCat = c.k">{{ c.l }}</div>
        </div>
        <div class="pe-body">
          <template v-if="devCat === 'general'">
            <div class="pe-hd">{{ $t('标识') }}</div>
            <el-form :disabled="!projectEditable" label-width="130px" size="small">
              <el-form-item :label="$t('名称')"><el-input v-model="devPForm.name" /></el-form-item>
              <el-form-item :label="$t('说明')"><el-input v-model="devPForm.description" /></el-form-item>
              <el-form-item :label="$t('驱动程序')"><span class="ro-green">{{ driverName(devPDriverId) }}</span></el-form-item>
              <el-form-item :label="$t('通道分配')"><span class="ro">{{ devPChName }}</span></el-form-item>
              <template v-if="devPDriverId === 'Siemens.S7'">
                <el-form-item :label="$t('型号')">
                  <el-select v-model="devPForm.settings.model" style="width:100%" @change="onModelChange">
                    <el-option value="S7-1200" /><el-option value="S7-1500" /><el-option value="S7-300" /><el-option value="S7-400" /><el-option value="S7-200" />
                  </el-select>
                </el-form-item>
                <el-form-item :label="$t('ID(IP 地址)')"><el-input v-model="devPForm.endpoint" placeholder="192.168.0.1" /></el-form-item>
              </template>
              <template v-else-if="devPDriverId === 'Modbus.Tcp'">
                <el-form-item :label="$t('端点 (IP:端口)')"><el-input v-model="devPForm.endpoint" placeholder="192.168.1.10:502" /></el-form-item>
              </template>
              <template v-else-if="devPDriverId === 'OpcUa.Client'">
                <el-form-item :label="$t('UA 服务器')"><span class="ro">{{ channelOf(devPropsRef)?.settings?.url || '请到通道属性配置' }}</span></el-form-item>
                <div v-if="devPForm.endpoint && !channelOf(devPropsRef)?.settings?.url" class="sim-notice">
                  {{ $t('旧设备端点「{endpoint}」仅作为兼容回退保留；请到通道属性填写 UA 服务器，系统不会静默删除或改写旧值。', { endpoint: devPForm.endpoint }) }}
                </div>
              </template>
              <template v-else>
                <el-form-item :label="$t('ID(端点)')"><el-input v-model="devPForm.endpoint" /></el-form-item>
              </template>
            </el-form>
            <div class="pe-hd">{{ $t('操作模式') }}</div>
            <el-form :disabled="!projectEditable" label-width="130px" size="small">
              <el-form-item :label="$t('数据收集')"><el-select v-model="devPForm.settings.dataCollection" style="width:100%"><el-option value="启用" :label="$t('启用')" /><el-option value="禁用" :label="$t('禁用')" /></el-select></el-form-item>
              <el-form-item :label="$t('静态标记')"><span class="ro">{{ devTagCount(devPropsRef) }}</span></el-form-item>
            </el-form>
          </template>
          <template v-else-if="devCat === 'scan'">
            <div class="pe-hd">{{ $t('扫描模式') }}</div>
            <el-form :disabled="!projectEditable" label-width="150px" size="small">
              <el-form-item :label="$t('扫描模式')">
                <el-select v-model="devPForm.settings.scanMode" style="width:100%">
                  <el-option value="请求所有数据以指定的扫描速率" :label="$t('请求所有数据以指定的扫描速率')" /><el-option value="遵循客户端指定的扫描速率" :label="$t('遵循客户端指定的扫描速率')" />
                </el-select>
              </el-form-item>
              <el-form-item :label="$t('活动标记超时(秒)')"><el-input v-model="devPForm.settings.activeWindowSec" /></el-form-item>
              <el-form-item :label="$t('来自缓存的初始更新')"><el-select v-model="devPForm.settings.initialFromCache" style="width:100%"><el-option value="启用" :label="$t('启用')" /><el-option value="禁用" :label="$t('禁用')" /></el-select></el-form-item>
            </el-form>
            <div class="hint">{{ $t('遵循客户端=只轮询被读取/订阅的点,省带宽;活动标记超时=多久无人读就停轮询。') }}</div>
          </template>
          <template v-else-if="devCat === 'timing'">
            <div class="pe-hd">{{ $t('通信超时') }}</div>
            <el-form :disabled="!projectEditable" label-width="150px" size="small">
              <el-form-item :label="$t('连接超时(秒)')"><el-input v-model="devPForm.settings.connTimeout" /></el-form-item>
              <el-form-item :label="$t('请求超时(毫秒)')"><el-input v-model="devPForm.settings.timeoutMs" /></el-form-item>
              <el-form-item :label="$t('超时前尝试次数')"><el-input v-model="devPForm.settings.retries" /></el-form-item>
            </el-form>
            <div class="pe-hd">{{ $t('定时') }}</div>
            <el-form :disabled="!projectEditable" label-width="150px" size="small">
              <el-form-item :label="$t('请求间延迟(毫秒)')"><el-input v-model="devPForm.settings.requestDelay" /></el-form-item>
            </el-form>
          </template>
          <template v-else-if="devCat === 'demote'">
            <div class="pe-hd">{{ $t('自动降级') }}</div>
            <el-form :disabled="!projectEditable" label-width="150px" size="small">
              <el-form-item :label="$t('故障时降级')"><el-select v-model="devPForm.settings.demote" style="width:100%"><el-option value="禁用" :label="$t('禁用')" /><el-option value="启用" :label="$t('启用')" /></el-select></el-form-item>
              <el-form-item :label="$t('降级前失败次数')"><el-input v-model="devPForm.settings.demoteAfter" /></el-form-item>
              <el-form-item :label="$t('降级时长(毫秒)')"><el-input v-model="devPForm.settings.demotePeriodMs" /></el-form-item>
            </el-form>
            <div class="hint">{{ $t('连续失败达阈值后自动停采一段时间再重试,避免故障设备拖累其它设备。') }}</div>
          </template>
          <template v-else-if="devCat === 'comm'">
            <div class="pe-hd">{{ $t('通信参数') }}</div>
            <el-form :disabled="!projectEditable" label-width="150px" size="small">
              <template v-if="devPDriverId === 'Siemens.S7'">
                <el-form-item :label="$t('端口号')"><el-input v-model="devPForm.settings.port" placeholder="102" /></el-form-item>
                <el-form-item label="MPI ID"><el-input v-model="devPForm.settings.mpiId" placeholder="0" /></el-form-item>
              </template>
              <template v-else-if="devPDriverId === 'Omron.Fins'">
                <el-form-item :label="$t('目标节点号 DA1')"><el-input v-model="devPForm.settings.destNode" :placeholder="$t('留空=取 IP 末位')" /></el-form-item>
                <el-form-item :label="$t('源节点号 SA1')"><el-input v-model="devPForm.settings.srcNode" placeholder="1" /></el-form-item>
                <el-form-item :label="$t('UDP 端口')"><el-input v-model="devPForm.settings.port" placeholder="9600" /></el-form-item>
              </template>
              <template v-else-if="devPDriverId === 'AB.Eip'">
                <el-form-item :label="$t('PLC 型号')">
                  <el-select v-model="devPForm.settings.plcType" style="width:100%">
                    <el-option value="ControlLogix" /><el-option value="CompactLogix" /><el-option value="Micro800" /><el-option value="MicroLogix" /><el-option value="SLC500" /><el-option value="PLC5" />
                  </el-select>
                </el-form-item>
                <el-form-item :label="$t('CPU 路径')"><el-input v-model="devPForm.settings.path" placeholder="1,0" /></el-form-item>
              </template>
            </el-form>
          </template>
          <template v-else-if="devCat === 'protocol'">
            <ProtocolDeviceSettings v-model="devPForm.settings" :driver-id="devPDriverId" :editable="projectEditable" />
          </template>
          <template v-else-if="devCat === 's7'">
            <div class="pe-hd">{{ $t('S7 通信参数') }}</div>
            <el-form :disabled="!projectEditable" label-width="150px" size="small">
              <el-form-item :label="$t('PDU 大小上限')"><el-input v-model="devPForm.settings.pdu" placeholder="960" /></el-form-item>
              <el-form-item :label="$t('本地 TSAP')"><el-input v-model="devPForm.settings.localTsap" placeholder="4D57" /></el-form-item>
              <el-form-item :label="$t('远程 TSAP')"><el-input v-model="devPForm.settings.remoteTsap" placeholder="4D57" /></el-form-item>
              <el-form-item :label="$t('CPU 机架')"><el-input v-model="devPForm.settings.rack" /></el-form-item>
              <el-form-item :label="$t('CPU 槽')"><el-input v-model="devPForm.settings.slot" /></el-form-item>
            </el-form>
            <div class="hint">{{ $t('机架/槽随「型号」自动带出(300/400=0,2;其余 0,1),可手改。') }}</div>
          </template>
          <template v-else-if="devCat === 'addr'">
            <div class="pe-hd">{{ $t('寻址选项') }}</div>
            <el-form :disabled="!projectEditable" label-width="150px" size="small">
              <el-form-item :label="$t('字节顺序')">
                <el-select v-model="devPForm.settings.byteOrder" style="width:100%">
                  <el-option value="big" :label="$t('大端字节序(Siemens 默认)')" /><el-option value="little" :label="$t('小端字节序')" />
                </el-select>
              </el-form-item>
            </el-form>
          </template>
          <template v-else-if="devCat === 'redu'">
            <div class="pe-hd">{{ $t('冗余') }}</div>
            <el-form :disabled="!projectEditable" label-width="150px" size="small">
              <el-form-item :label="$t('次级路径')"><el-input v-model="devPForm.settings.secondaryEndpoint" :placeholder="$t('留空=不启用冗余')" /></el-form-item>
              <el-form-item :label="$t('操作模式')"><span class="ro">{{ $t('故障切换') }}</span></el-form-item>
              <el-form-item :label="$t('监视器间隔(秒)')"><el-input v-model="devPForm.settings.monitorIntervalSec" /></el-form-item>
              <el-form-item :label="$t('尽快返回主设备')"><el-select v-model="devPForm.settings.returnToPrimary" style="width:100%"><el-option value="是" :label="$t('是')" /><el-option value="否" :label="$t('否')" /></el-select></el-form-item>
            </el-form>
            <div class="hint">{{ $t('主路径连续失败自动切到备用端点;恢复后按设置切回。') }}</div>
          </template>
          <template v-else-if="devCat === 'driver'">
            <div class="pe-hd">{{ $t('驱动参数({name})', { name: devPDriver?.displayName || '' }) }}</div>
            <el-form :disabled="!projectEditable" label-width="150px" size="small">
              <el-form-item v-for="s in devPDriver?.deviceSettings || []" :key="s.key" :label="s.label">
                <el-input v-model="devPForm.settings[s.key]" :placeholder="s.default" />
              </el-form-item>
            </el-form>
          </template>
          <template v-else-if="devCat === 'custom'">
            <div class="pe-hd">{{ $t('自定义参数(自由键值)') }}</div>
            <div v-for="(r, ri) in devExtra" :key="ri" class="kvrow">
              <el-input v-model="r.key" :disabled="!projectEditable" :placeholder="$t('键')" size="small" style="width:150px" />
              <el-input v-model="r.value" :disabled="!projectEditable" :placeholder="$t('值')" size="small" />
              <el-button v-if="projectEditable" size="small" text type="danger" @click="devExtra.splice(ri, 1)">{{ $t('删') }}</el-button>
            </div>
            <el-button v-if="projectEditable" size="small" text @click="devExtra.push({ key: '', value: '' })">{{ $t('+ 参数') }}</el-button>
          </template>
        </div>
      </div>
      <template #footer><el-button @click="devPropsOpen=false">{{ $t(projectEditable ? '取消' : '关闭') }}</el-button><el-button v-if="projectEditable" type="primary" :loading="operationBusy" @click="confirmDeviceProps">{{ $t('确定') }}</el-button></template>
    </el-dialog>

    <!-- 组 新建/编辑 -->
    <el-dialog v-model="grpOpen" :show-close="!operationBusy" :close-on-press-escape="!operationBusy" :title="grpEditRef ? '标记组属性' : '新建标记组'" width="440" append-to-body>
      <el-form :disabled="!projectEditable" label-width="90px">
        <el-form-item :label="$t('名称')"><el-input v-model="grpForm.name" /></el-form-item>
        <el-form-item :label="$t('说明')"><el-input v-model="grpForm.description" /></el-form-item>
        <template v-if="grpEditRef">
          <el-form-item v-if="grpMesBoundary" :label="$t('采集流程')">
            <el-switch v-model="grpForm.flowAvailable" :disabled="!!grpMesConflict || !projectEditable" />
            <span class="group-flow-state">{{ $t(grpForm.flowAvailable ? '已开启' : '停用') }}</span>
          </el-form-item>
          <div v-if="grpMesConflict" class="group-flow-conflict">
            {{ $t('需先停用父级或子级任务：{path}', { path: grpMesConflict }) }}
          </div>
          <el-form-item :label="$t('组中标记')"><span class="rocnt">{{ grpEditRef.tags?.length || 0 }}</span></el-form-item>
          <el-form-item :label="$t('分支中标记')"><span class="rocnt">{{ cnt(grpEditRef) }}</span><small style="color:#999;margin-left:6px">{{ $t('(含子组)') }}</small></el-form-item>
        </template>
      </el-form>
      <template #footer><el-button @click="grpOpen=false">{{ $t(projectEditable ? '取消' : '关闭') }}</el-button><el-button v-if="projectEditable" type="primary" :loading="operationBusy" @click="confirmGroup">{{ grpEditRef ? '确定' : '加入' }}</el-button></template>
    </el-dialog>

    <!-- 标记属性(对齐桌面属性编辑器:左分类[常规/缩放/报警]+右字段页) -->
    <el-dialog v-model="tagOpen" :show-close="!operationBusy" :close-on-press-escape="!operationBusy" :title="$t('属性编辑器 - {name}', { name: `${tagPathPrefix ? tagPathPrefix + '.' : ''}${tagForm.name || ''}` })" width="640" append-to-body>
      <div class="pe" v-if="tagForm">
        <div class="pe-cats">
          <div class="pe-cat" :class="{ on: tagCat === 'general' }" @click="tagCat = 'general'">{{ $t('常规') }}</div>
          <div v-if="tagForm.access !== 'read'" class="pe-cat" :class="{ on: tagCat === 'writeConfirmation' }" @click="tagCat = 'writeConfirmation'">{{ $t('写入确认') }}</div>
          <div class="pe-cat" :class="{ on: tagCat === 'scaling' }" @click="tagCat = 'scaling'">{{ $t('缩放') }}</div>
          <div class="pe-cat" :class="{ on: tagCat === 'alarm' }" @click="tagCat = 'alarm'">{{ $t('报警') }}</div>
        </div>
        <div class="pe-body">
          <template v-if="tagCat === 'general'">
            <div class="pe-hd">{{ $t('标识') }}</div>
            <el-form :disabled="!projectEditable" label-width="120px" size="small">
              <el-form-item :label="$t('名称')"><el-input v-model="tagForm.name" /></el-form-item>
              <el-form-item :label="$t('说明')"><el-input v-model="tagForm.description" /></el-form-item>
            </el-form>
            <div class="pe-hd">{{ $t('数据属性') }}</div>
            <ModbusTagEditor v-if="tagDriverId === 'Modbus.Tcp'" v-model="tagForm" :settings="tagProtocolContext?.device?.settings || {}" :editable="projectEditable" />
            <el-form :disabled="!projectEditable" label-width="120px" size="small">
              <el-form-item v-if="!isProtocolTag" :label="$t('地址')"><el-input v-model="tagForm.address" :placeholder="$t('按下方「地址格式速查」填写')" /></el-form-item>
              <el-form-item v-if="!isProtocolTag" :label="$t('数据类型')">
                <el-select v-model="tagForm.dataType" style="width:100%">
                  <el-option v-for="option in KEP_TYPES" :key="option.l" :value="option.v" :label="$t(option.l)" :disabled="!option.v" />
                </el-select>
              </el-form-item>
              <div v-if="pointWriteHint(tagForm.dataType)" class="addrnote">{{ pointWriteHint(tagForm.dataType) }}</div>
              <el-form-item v-if="!isProtocolTag" :label="$t('数组配置')">
                <ArrayTagEditor v-model="tagForm" :editable="projectEditable" style="width:100%" />
              </el-form-item>
              <el-form-item v-if="tagForm.dataType === 'string' && !isProtocolTag" :label="$t('文本编码')">
                <div style="width:100%">
                  <el-select v-model="tagForm.textEncoding" style="width:100%">
                    <el-option :value="null" :label="$t('自动识别（UTF-8 → GB18030）')" />
                    <el-option value="utf8" label="UTF-8" />
                    <el-option value="gb18030" label="GB18030" />
                  </el-select>
                  <div class="hint">{{ $t('自动模式会按点位记住识别结果，读取和回写使用同一编码。') }}</div>
                </div>
              </el-form-item>
              <el-form-item v-if="!isProtocolTag" :label="$t('客户端访问')">
                <el-select v-model="tagForm.access" style="width:100%">
                  <el-option value="read" :label="$t('只读')" /><el-option value="readWrite" :label="$t('读/写')" /><el-option value="write" :label="$t('只写')" />
                </el-select>
              </el-form-item>
              <el-form-item :label="$t('扫描速率(毫秒)')"><el-input-number v-model="tagForm.scanRateMs" :min="10" :max="3600000" controls-position="right" style="width:100%" /></el-form-item>
            </el-form>
            <template v-if="!isProtocolTag">
            <div class="pe-hd">{{ $t('地址格式速查 · {name}', { name: tagDriver?.displayName || $t('(未知驱动)') }) }}</div>
            <div class="addrnote" v-if="tagAddrGuide?.note">{{ tagAddrGuide.note }}</div>
            <div class="addrnote2" v-if="tagDriver?.addressHelp">{{ $t('驱动提示:') }}{{ tagDriver.addressHelp }}</div>
            <table class="addrtb" v-if="tagAddrRows.length">
              <thead><tr><th>{{ $t('数据类型 / 区') }}</th><th>{{ $t('地址示例(点击填入)') }}</th></tr></thead>
              <tbody><tr v-for="(r, i) in tagAddrRows" :key="i"><td>{{ r.t }}</td><td><code @click="projectEditable && (tagForm.address = r.a.split(/[ /(]/)[0])">{{ r.a }}</code></td></tr></tbody>
            </table>
            <div class="addrtip">{{ $t('数组可注册为一个逻辑标记；填写首地址、维度和 PLC 下界后，由驱动按协议能力自动分块读写。') }}</div>
            </template>
          </template>
          <template v-else-if="tagCat === 'writeConfirmation'">
            <div class="pe-hd">{{ $t('写入确认') }}</div>
            <el-form :disabled="!projectEditable" label-width="120px" size="small">
              <el-form-item :label="$t('确认方式')">
                <el-select v-model="tagConfirmationMode" style="width:100%">
                  <el-option value="readBack" :label="$t('写后回读当前点')" :disabled="tagForm.access === 'write'" />
                  <el-option value="confirmationTag" :label="$t('读取独立确认点')" />
                  <el-option value="protocolAcknowledged" :label="$t('仅协议确认（未验证 PLC 实际值）')" />
                </el-select>
              </el-form-item>
              <template v-if="tagConfirmationMode === 'confirmationTag'">
                <el-form-item :label="$t('确认点')">
                  <el-select v-model="tagForm.writeConfirmation.confirmationTagId" filterable style="width:100%" :placeholder="$t('选择完整实际路径')">
                    <el-option v-for="option in tagConfirmationOptions" :key="option.path" :value="option.path" :label="option.label" />
                  </el-select>
                </el-form-item>
                <el-form-item :label="$t('期望值')"><el-input v-model="tagForm.writeConfirmation.expectedValue" /></el-form-item>
              </template>
              <div class="addrnote" v-if="tagConfirmationMode === 'readBack'">{{ $t('驱动写入成功后回读当前 PLC 点，值一致才返回成功；不一致最多重写 10 次。') }}</div>
              <div class="addrnote" v-else-if="tagConfirmationMode === 'confirmationTag'">{{ $t('写入后读取所选确认点，达到期望值才返回成功；路径、PLC 地址和说明会同时显示。') }}</div>
              <div class="addrnote" v-else>{{ $t('只确认协议请求成功，不代表 PLC 中的实际值已经一致，结果会标记为 GoodUnverified。') }}</div>
            </el-form>
          </template>
          <template v-else-if="tagCat === 'scaling'">
            <div class="pe-hd">{{ $t('缩放') }}</div>
            <el-form :disabled="!projectEditable" label-width="120px" size="small">
              <el-form-item :label="$t('类型')">
                <el-select v-model="tagScaleType" style="width:100%"><el-option value="无" :label="$t('无')" /><el-option value="线性" :label="$t('线性')" /><el-option value="平方根" :label="$t('平方根')" /></el-select>
              </el-form-item>
              <template v-if="tagScaleType !== '无'">
                <el-form-item :label="$t('原始下限')"><el-input-number v-model="tagForm.scaling.rawLow" controls-position="right" style="width:100%" /></el-form-item>
                <el-form-item :label="$t('原始上限')"><el-input-number v-model="tagForm.scaling.rawHigh" controls-position="right" style="width:100%" /></el-form-item>
                <el-form-item :label="$t('缩放下限')"><el-input-number v-model="tagForm.scaling.engLow" controls-position="right" style="width:100%" /></el-form-item>
                <el-form-item :label="$t('缩放上限')"><el-input-number v-model="tagForm.scaling.engHigh" controls-position="right" style="width:100%" /></el-form-item>
                <el-form-item :label="$t('工程单位')"><el-input v-model="tagForm.engUnit" :placeholder="$t('如 °C、bar')" /></el-form-item>
                <el-form-item :label="$t('钳位到量程')"><el-select v-model="tagForm.scaling.clamp" style="width:100%"><el-option :value="true" :label="$t('是')" /><el-option :value="false" :label="$t('否')" /></el-select></el-form-item>
              </template>
            </el-form>
            <div class="hint">{{ $t('原始量程 → 工程量程线性映射;平方根用于流量开方。') }}</div>
          </template>
          <template v-else-if="tagCat === 'alarm'">
            <div class="pe-hd">{{ $t('报警限值') }}</div>
            <el-form :disabled="!projectEditable" label-width="120px" size="small">
              <el-form-item :label="$t('启用')"><el-select v-model="tagForm.alarm.enabled" style="width:100%"><el-option :value="false" :label="$t('否')" /><el-option :value="true" :label="$t('是')" /></el-select></el-form-item>
              <el-form-item :label="$t('高高 (HiHi)')"><el-input-number v-model="tagForm.alarm.hiHi" controls-position="right" style="width:100%" :placeholder="$t('留空=不启用')" /></el-form-item>
              <el-form-item :label="$t('高 (Hi)')"><el-input-number v-model="tagForm.alarm.hi" controls-position="right" style="width:100%" :placeholder="$t('留空=不启用')" /></el-form-item>
              <el-form-item :label="$t('低 (Lo)')"><el-input-number v-model="tagForm.alarm.lo" controls-position="right" style="width:100%" :placeholder="$t('留空=不启用')" /></el-form-item>
              <el-form-item :label="$t('低低 (LoLo)')"><el-input-number v-model="tagForm.alarm.loLo" controls-position="right" style="width:100%" :placeholder="$t('留空=不启用')" /></el-form-item>
              <el-form-item :label="$t('回差 (死区)')"><el-input-number v-model="tagForm.alarm.deadband" :min="0" controls-position="right" style="width:100%" /></el-form-item>
            </el-form>
            <div class="hint">{{ $t('值越限→产生报警,回落超过 限值±回差 才清除;不需要的限值留空。') }}</div>
          </template>
        </div>
      </div>
      <template #footer><el-button @click="tagOpen=false">{{ $t(projectEditable ? '取消' : '关闭') }}</el-button><el-button v-if="projectEditable" type="primary" :loading="operationBusy" @click="confirmTag">{{ $t('确定') }}</el-button></template>
    </el-dialog>

    <!-- 工程属性(对齐桌面属性编辑器:左分类列表[含灰置未接入接口]+右字段页) -->
    <el-dialog v-model="projOpen" :title="$t('属性编辑器 - {name}', { name: projForm.name || $t('工程') })" width="720" append-to-body>
      <div class="pe" v-if="proj">
        <div class="pe-cats">
          <div v-for="c in projCats" :key="c.k" class="pe-cat" :class="{ on: projCat === c.k, dis: !c.on }"
               :title="c.on ? '' : $t('该接口本网关未接入(后续可实现)')" @click="c.on && (projCat = c.k)">{{ c.l }}</div>
        </div>
        <div class="pe-body">
          <template v-if="projCat === 'general'">
            <div class="pe-hd">{{ $t('标识') }}</div>
            <el-form :disabled="!projectEditable" label-width="120px" size="small">
              <el-form-item :label="$t('说明')"><el-input v-model="projForm.description" /></el-form-item>
              <el-form-item :label="$t('标题')"><el-input v-model="projForm.name" /></el-form-item>
              <el-form-item :label="$t('定义的标记')"><span class="ro">{{ projTotalTags }}</span></el-form-item>
            </el-form>
          </template>
          <template v-else-if="projCat === 'opcua'">
            <div class="pe-hd">{{ $t('服务器接口') }}</div>
            <el-form :disabled="!projectEditable" label-width="150px" size="small">
              <el-form-item :label="$t('启用')"><el-switch v-model="projForm.uaEnabled" /></el-form-item>
              <el-form-item :label="$t('端口')"><el-input-number v-model="projForm.uaPort" :min="1" :max="65535" controls-position="right" style="width:100%" /></el-form-item>
              <el-form-item :label="$t('日志诊断')"><el-switch v-model="projForm.uaDiag" /></el-form-item>
            </el-form>
            <div class="pe-hd">{{ $t('客户端会话') }}</div>
            <el-form :disabled="!projectEditable" label-width="150px" size="small">
              <el-form-item :label="$t('允许匿名登录')"><el-switch v-model="projForm.uaAnon" /></el-form-item>
              <el-form-item :label="$t('用户名')"><el-input v-model="projForm.uaUser" :placeholder="$t('禁用匿名后用')" /></el-form-item>
              <el-form-item :label="$t('密码')"><el-input v-model="projForm.uaPass" show-password /></el-form-item>
              <el-form-item :label="$t('提供加密端点')"><el-switch v-model="projForm.uaEnc" /><span class="hint" style="margin-left:8px">Basic256Sha256</span></el-form-item>
              <el-form-item :label="$t('最大连接数')"><el-input-number v-model="projForm.uaMaxSess" :min="1" :max="4096" controls-position="right" style="width:100%" /></el-form-item>
              <el-form-item :label="$t('最小会话超时(秒)')"><el-input-number v-model="projForm.uaMinTo" :min="1" controls-position="right" style="width:100%" /></el-form-item>
              <el-form-item :label="$t('最大会话超时(秒)')"><el-input-number v-model="projForm.uaMaxTo" :min="1" controls-position="right" style="width:100%" /></el-form-item>
              <el-form-item :label="$t('标记缓存超时(秒)')"><el-input-number v-model="projForm.uaCacheTo" :min="0" controls-position="right" style="width:100%" /></el-form-item>
            </el-form>
            <div class="pe-hd">{{ $t('浏览') }}</div>
            <el-form :disabled="!projectEditable" label-width="150px" size="small">
              <el-form-item :label="$t('返回标记属性')"><el-switch v-model="projForm.uaRetProps" /></el-form-item>
              <el-form-item :label="$t('返回地址提示')"><el-switch v-model="projForm.uaRetHints" /></el-form-item>
            </el-form>
            <div class="pe-hd">{{ $t('监控项 / 订阅') }}</div>
            <el-form :disabled="!projectEditable" label-width="180px" size="small">
              <el-form-item :label="$t('最大数据队列大小')"><el-input-number v-model="projForm.uaMaxQ" :min="1" controls-position="right" style="width:100%" /></el-form-item>
              <el-form-item :label="$t('最大重新传送队列大小')"><el-input-number v-model="projForm.uaMaxRetx" :min="1" controls-position="right" style="width:100%" /></el-form-item>
              <el-form-item :label="$t('每次发布的最大通知数')"><el-input-number v-model="projForm.uaMaxNotif" :min="1" controls-position="right" style="width:100%" /></el-form-item>
            </el-form>
            <div class="hint">{{ $t('⚠ 端口改动需重启网关进程才生效;其余保存后热重载生效。') }}</div>
          </template>
          <template v-else-if="projCat === 'history'">
            <div class="pe-hd">{{ $t('数据记录 (SQLite)') }}</div>
            <el-form :disabled="!projectEditable" label-width="150px" size="small">
              <el-form-item :label="$t('启用')"><el-switch v-model="projForm.histEnabled" /></el-form-item>
              <el-form-item :label="$t('数据库文件')"><el-input v-model="projForm.histPath" placeholder="history.db" /></el-form-item>
              <el-form-item :label="$t('落盘间隔(毫秒)')"><el-input-number v-model="projForm.histFlush" :min="200" :max="60000" controls-position="right" style="width:100%" /></el-form-item>
              <el-form-item :label="$t('保留行数上限')"><el-input-number v-model="projForm.histRows" :min="1000" :max="100000000" controls-position="right" style="width:100%" /></el-form-item>
            </el-form>
            <div class="hint">{{ $t('按变化把所有点位写入 SQLite,供历史查询;超出保留行自动删最旧。') }}</div>
          </template>
          <template v-else-if="projCat === 'iot'">
            <div class="pe-hd">{{ $t('北向 IoT 发布 (MQTT)') }}</div>
            <el-form :disabled="!projectEditable" label-width="150px" size="small">
              <el-form-item :label="$t('启用')"><el-switch v-model="projForm.mqEnabled" /></el-form-item>
              <el-form-item :label="$t('Broker 主机')"><el-input v-model="projForm.mqHost" placeholder="127.0.0.1" /></el-form-item>
              <el-form-item :label="$t('Broker 端口')"><el-input-number v-model="projForm.mqPort" :min="1" :max="65535" controls-position="right" style="width:100%" /></el-form-item>
              <el-form-item :label="$t('主题前缀')"><el-input v-model="projForm.mqPrefix" :placeholder="$t('igw(完整=前缀/通道/设备/…/点)')" /></el-form-item>
              <el-form-item :label="$t('用户名')"><el-input v-model="projForm.mqUser" /></el-form-item>
              <el-form-item :label="$t('密码')"><el-input v-model="projForm.mqPass" show-password /></el-form-item>
              <el-form-item label="QoS"><el-select v-model="projForm.mqQos" style="width:100%"><el-option :value="0" label="0" /><el-option :value="1" label="1" /><el-option :value="2" label="2" /></el-select></el-form-item>
              <el-form-item :label="$t('保留消息')"><el-switch v-model="projForm.mqRetain" /></el-form-item>
              <el-form-item :label="$t('发布间隔(毫秒)')"><el-input-number v-model="projForm.mqInterval" :min="0" :max="3600000" controls-position="right" style="width:100%" /></el-form-item>
            </el-form>
            <div class="hint">{{ $t('0=变化即发；大于 0=周期全量发布（对标 KEP IoT Gateway）。') }}</div>
          </template>
        </div>
      </div>
      <template #footer><el-button @click="projOpen=false">{{ $t(projectEditable ? '取消' : '关闭') }}</el-button><el-button v-if="projectEditable" type="primary" @click="confirmProjProps">{{ $t('确定') }}</el-button></template>
    </el-dialog>
  </div>
  <div v-else class="wrap" aria-busy="true"><p role="status">{{ operationLabel || $t('正在载入工程…') }}</p><el-skeleton :rows="8" animated /></div>
</template>

<style scoped>
.wrap { display: flex; flex-direction: column; gap: 10px; height: 100%; }
.bar { display: flex; align-items: center; gap: 3px; flex-wrap: wrap; }
.tip { color: #aaa; font-size: 11px; margin-left: 10px; }
/* 工具栏图标按钮(对齐桌面) */
.tb { width: 30px; height: 28px; border: 1px solid transparent; border-radius: 5px; background: transparent; cursor: pointer; display: inline-flex; align-items: center; justify-content: center; color: #444; }
.tb:hover:not(:disabled) { background: #eef2f8; border-color: #dbe2ea; }
.tb:disabled { color: #c8ccd2; cursor: not-allowed; }
.tb.pri { color: #1565C0; }
.tb.qc { width: auto; padding: 0 8px; background: #2E78D6; color: #fff; font-weight: 700; font-size: 11px; }
.tb.tbtext { width: auto; padding: 0 8px; font-size: 12px; color: #444; }
.tb.tbtext:hover { background: #eef2f7; }
.tb.qc:hover { background: #2360b0; }
.mdl { font-family: 'Segoe MDL2 Assets', 'Segoe Fluent Icons'; font-size: 16px; line-height: 1; }
.mdl.del { color: #C0392B; }
.tbsep { width: 1px; height: 18px; background: #e2e6eb; margin: 0 4px; }
.cols { display: flex; gap: 0; flex: 1; min-height: 0; }
.tree { overflow: hidden; padding: 8px; flex-shrink: 0; }
.tn.drop-ok, .project-grid-drag-cell.drop-ok, .gridwrap.drop-ok { background: #e6f3ff; outline: 1px dashed #409EFF; border-radius: 4px; }
.project-grid-drag-cell { display: block; min-height: 18px; overflow: hidden; text-overflow: ellipsis; }
.project-grid-drag-cell[draggable="true"], .tn[draggable="true"] { cursor: grab; }
.rocnt { font-weight: 600; color: #333; }
.group-flow-state { margin-left: 8px; color: #526274; font-size: 12px; }
.group-flow-conflict { margin: -8px 0 12px 90px; color: #c45656; font-size: 12px; line-height: 1.5; overflow-wrap: anywhere; }
.vsplit { width: 6px; cursor: col-resize; flex-shrink: 0; position: relative; }
.vsplit::before { content: ''; position: absolute; left: 2px; top: 0; bottom: 0; width: 2px; background: #e6e9ee; border-radius: 2px; }
.vsplit:hover::before { background: #1565C0; }
.main { flex: 1; overflow: hidden; display: flex; flex-direction: column; min-width: 0; }
/* 树节点:图标固定,标签按树宽自适应截断(悬停 title 看全) */
.tn { font-size: 13px; display: flex; align-items: center; min-width: 0; width: 100%; }
.tn .ic { margin-right: 5px; flex-shrink: 0; }
.tnlabel { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; min-width: 0; flex: 1; }
.tn small { color: #9aa4ae; }
.tn .tree-description { color: #718397; }
.tn .group-collection-mark { flex-shrink: 0; margin-left: 5px; font-size: 11px; font-weight: 650; white-space: nowrap; }
.tn .runtime-mark { display: inline-flex; align-items: center; gap: 3px; flex-shrink: 0; height: 18px; margin-left: 5px; padding: 0 5px; border: 1px solid transparent; border-radius: 9px; font-size: 11px; font-weight: 700; line-height: 16px; }
.runtime-dot { width: 6px; height: 6px; border-radius: 50%; background: currentColor; box-shadow: 0 0 0 2px rgb(255 255 255 / 75%); }
.tn .runtime-online { color: #157A35; background: #EAF7EE; border-color: #BCE3C7; }
.tn .runtime-standby { color: #536273; background: #F1F4F7; border-color: #C8D0D9; }
.tn .runtime-connecting { color: #A96100; background: #FFF5DB; border-color: #F0D18A; }
.tn .runtime-offline, .tn .runtime-fault { color: #C5221F; background: #FFF0EF; border-color: #F2B8B5; }
.tn .runtime-unknown { color: #687481; background: #F1F3F5; border-color: #D9DEE3; }
.sim-notice { margin: 0 0 10px; padding: 8px 10px; color: #8A4F00; background: #FFF6DD; border: 1px solid #EBCB78; border-radius: 5px; font-size: 12px; line-height: 1.5; }
/* 让 el-tree 节点内容允许收缩,截断才生效 */
:deep(.el-tree-node__content) { min-width: 0; }
:deep(.el-tree-node__label), :deep(.el-tree-node__content > .el-tree-node__label), :deep(.el-tree-node__content span) { min-width: 0; }
/* 底部状态栏 */
.statusbar { display: flex; align-items: center; gap: 6px; height: 26px; padding: 0 10px; background: #e8eaed; border: 1px solid #e0e3e8; border-radius: 6px; font-size: 12px; color: #555; flex-shrink: 0; }
.statusbar .dotg { width: 9px; height: 9px; border-radius: 50%; }
.statusbar .sgrow { flex: 1; }
.statusbar .sitem { color: #444; }
.statusbar .ssep { width: 1px; height: 14px; background: #c8ccd2; }
.ttl { display: flex; align-items: center; gap: 8px; margin-bottom: 10px; }
.ttl .ic { font-size: 16px; }
.ttl .grow { flex: 1; }
.gridwrap { flex: 1; min-height: 0; }
.emptyhint { color: #1565C0; cursor: pointer; padding: 20px; }
.hint { color: #888; font-size: 12px; line-height: 1.6; }
.kvrow { display: flex; gap: 4px; margin-bottom: 4px; align-items: center; }
.kvrow :deep(.el-button) { flex-shrink: 0; white-space: nowrap; padding: 4px 6px; }
/* 属性编辑器(对齐桌面):左分类列表 + 右字段页 */
.pe { display: flex; gap: 0; border: 1px solid #e6e9ee; border-radius: 6px; overflow: hidden; min-height: 380px; }
.pe-cats { width: 150px; background: #f5f7fa; border-right: 1px solid #e6e9ee; padding: 6px 0; flex-shrink: 0; }
.pe-cat { padding: 8px 14px; font-size: 13px; cursor: pointer; color: #444; }
.pe-cat:hover { background: #ecf0f5; }
.pe-cat.on { background: #fff; color: #1565C0; font-weight: 600; border-right: 2px solid #1565C0; margin-right: -1px; }
.pe-cat.dis { color: #c0c4cc; cursor: not-allowed; }
.pe-cat.dis:hover { background: transparent; }
.pe-body { flex: 1; padding: 12px 16px; overflow: auto; max-height: 480px; min-width: 0; }
.pe-hd { font-weight: 600; font-size: 13px; color: #333; background: #f2f5f9; padding: 5px 10px; border-radius: 4px; margin: 8px 0; }
.pe-hd:first-child { margin-top: 0; }
.ro { color: #666; }
.ro-green { color: #1B7F37; font-weight: 600; }
/* 地址格式速查字典 */
.addrnote { font-size: 12px; color: #444; line-height: 1.6; background: #fbf8ee; border-left: 3px solid #d9b45b; padding: 5px 9px; border-radius: 3px; margin: 2px 0 6px; }
.addrnote2 { font-size: 12px; color: #888; line-height: 1.5; margin: 0 0 6px; }
.addrtb { width: 100%; border-collapse: collapse; font-size: 12px; margin: 2px 0 6px; }
.addrtb th { text-align: left; background: #f2f5f9; color: #444; padding: 4px 8px; font-weight: 600; }
.addrtb td { padding: 4px 8px; border-top: 1px solid #eef0f3; vertical-align: top; }
.addrtb td:first-child { color: #555; white-space: nowrap; }
.addrtb code { background: #eef3fb; color: #1565C0; padding: 1px 5px; border-radius: 3px; cursor: pointer; font-family: 'Consolas', monospace; }
.addrtb code:hover { background: #dbe8fb; }
.addrtip { font-size: 12px; color: #888; line-height: 1.6; margin-top: 4px; }
/* 添加向导(对齐桌面多步向导) */
.wizhd { color: #1565C0; font-weight: 600; font-size: 13px; }
.wizinstr { color: #555; font-size: 13px; margin: 8px 0 14px; line-height: 1.6; min-height: 36px; }
.wizbody { min-height: 200px; }
.wl { font-size: 13px; color: #333; margin-bottom: 6px; }
.wiz-unlicensed { float: right; margin-left: 12px; color: #c0392b; font-size: 12px; }
.wzsum { border: 1px solid #e6e9ee; border-radius: 6px; max-height: 320px; overflow: auto; }
.wzh { background: #ececec; font-weight: 600; font-size: 12.5px; padding: 4px 10px; }
.wzr { display: flex; padding: 5px 10px; font-size: 13px; border-bottom: 1px solid #f2f4f7; }
.wzr span { width: 160px; color: #666; flex-shrink: 0; }
.wzr b { font-weight: 500; color: #222; }
:deep(.el-table .clk) { cursor: pointer; }
:deep(.el-table .project-grid-row-selected > td.el-table__cell) { background: #e6f2ff !important; }
:deep(.el-table .project-grid-row-selected:hover > td.el-table__cell) { background: #d9ebff !important; }
.clipboard-status { margin-left: 4px; padding: 2px 8px; border-radius: 10px; color: #24679f; background: #e8f3ff; font-size: 11px; white-space: nowrap; }
/* 浮动右键菜单 */
.ctxmenu { position: fixed; z-index: 3000; background: #fff; border: 1px solid #dcdfe6; border-radius: 6px;
  box-shadow: 0 6px 24px rgba(0,0,0,.16); padding: 4px; min-width: 170px; user-select: none; }
.ci { display: flex; align-items: center; justify-content: space-between; gap: 20px; padding: 6px 12px; border-radius: 4px; font-size: 13px; cursor: pointer; }
.ci:hover { background: #eef2f8; }
.ci small { color: #a0a6ae; font-size: 11px; }
.ci.dis { color: #c0c4cc; cursor: not-allowed; }
.ci.dis:hover { background: transparent; }
.ci.danger { color: #C0392B; }
.ci-sep { height: 1px; background: #eef0f3; margin: 4px 6px; }
.qc-work-content, .mes-work-content { height: 100%; min-height: 0; overflow: hidden; }
.mes-flow-preparing, .qc-scope-preparing { height: 100%; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 9px; color: #40556b; background: #f8fafc; }
.mes-flow-preparing small, .qc-scope-preparing small { color: #8795a4; font-size: 12px; }
.mes-flow-spinner { width: 26px; height: 26px; border: 3px solid #d9e6f5; border-top-color: #409eff; border-radius: 50%; animation: mes-flow-spin .8s linear infinite; }
@keyframes mes-flow-spin { to { transform: rotate(360deg); } }
.work-title-action { height: 25px; padding: 0 9px; border: 1px solid #b8c6d8; border-radius: 5px; background: #f5f8fc; color: #24527e; font-size: 11px; font-weight: 700; cursor: pointer; }
.work-title-action:hover { background: #e6effa; border-color: #7da6d4; }
.workbar { position: fixed; left: 50%; bottom: 7px; z-index: 4900; display: flex; gap: 6px; padding: 5px; transform: translateX(-50%); border: 1px solid #cad5e2; border-radius: 9px; background: rgba(247,250,253,.96); box-shadow: 0 6px 20px rgba(30,55,80,.18); backdrop-filter: blur(10px); }
.workbar button { min-width: 150px; height: 30px; padding: 0 13px; border: 1px solid #c6d3e2; border-radius: 6px; background: #fff; color: #263d56; cursor: pointer; font-size: 12px; }
.workbar button:hover { color: #1565c0; border-color: #79a9df; background: #edf5ff; }
</style>

<!-- WorkWindow 内的 MesFlow 需要覆盖整页默认高度。 -->
<style>
.mes-tool-window .fwrap { height: 100% !important; min-height: 0 !important; }
</style>
