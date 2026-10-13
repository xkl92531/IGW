<script setup lang="ts">
import { ref, shallowRef, computed, reactive, onMounted, onUnmounted, nextTick, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { ArrowDown, Search } from '@element-plus/icons-vue'
import { api } from '../api'
import MesNodeDetail from '../components/MesNodeDetail.vue'
import MesTimeline from '../components/MesTimeline.vue'
import FlowTemplatePanel from '../components/FlowTemplatePanel.vue'
import MesDataRangeDialog from '../components/MesDataRangeDialog.vue'
import MesParameterPicker from '../components/MesParameterPicker.vue'
import MesInterfaceListenerEditor from '../components/MesInterfaceListenerEditor.vue'
import MesJsonEditor from '../components/MesJsonEditor.vue'
import MesExtraFieldsEditor from '../components/MesExtraFieldsEditor.vue'
import MesFixedValuesEditor from '../components/MesFixedValuesEditor.vue'
import MesStructCard from '../components/MesStructCard.vue'
import MesUploadMappingDialog from '../components/MesUploadMappingDialog.vue'
import { fixedValueErrors, setUploadFixedRows, uploadFixedRows } from '../utils/mesFixedValues'
import { validateExtraFields, validateTaskExtraFields } from '../utils/mesExtraFields'
import { hasParameterDictionary, usesAutomaticPayload } from '../utils/mesParameterRouting'
import MesJsonMappingEditor from '../components/MesJsonMappingEditor.vue'
import MesResponseFieldPicker from '../components/MesResponseFieldPicker.vue'
import { extractResponseBody, parseResponseFields } from '../utils/mesResponseFields'
import { useTimedAutoSave } from '../composables/useTimedAutoSave'
import { useFlowHistory } from '../composables/useFlowHistory'
import { bindBlankTemplateUploads, mesGroupDescription } from '../utils/mesDataRanges'
import MesPayloadMappingEditor from '../components/MesPayloadMappingEditor.vue'
import { mergeGeneratedMapping } from '../utils/mesFieldMappingMerge'
import { cloneReactiveValue } from '../utils/reactiveClone'
import { resolveIndexedElementPath, preserveAutoDetectedMappings, upgradeLegacyImportedStructs, nestDetectedStructs } from '../utils/mesStructMapping'
import { createCompactId } from '../utils/runtimeId'
import { findMesRuntimeTask, presentMesRuntimeNode } from '../utils/mesTriggerRuntime'
import {
  isMesFlowAvailable,
  setMesTaskEnabledWithConfirmation,
} from '../utils/projectMesSync'
import { useI18n } from '../i18n'
import { useRealtimeStore } from '../stores/realtime'
import { storeToRefs } from 'pinia'
import { RealtimeStreamClient } from '../utils/realtimeStream'
import { mesFlowTopics } from '../utils/runtimeTopics'
import { projectApi } from '../features/project/api'
import { useMesTaskCatalog } from '../features/mes/useMesTaskCatalog'
import MesBatchSaveDialog, { type MesBatchPeriodChange } from '../components/MesBatchSaveDialog.vue'
import { canEdit } from '../auth'

const { t } = useI18n()
const realtime = useRealtimeStore()
const {
  mesTasks: realtimeMesTasks,
  projectRevision: realtimeProjectRevision,
  streamStates: realtimeStreamStates,
} = storeToRefs(realtime)

/** MES 流程画布(网页版,对齐桌面):节点/连线/四向端口/多选/复制粘贴/模板/运行态高亮;保存只提交 MES 配置并热重载。 */

// ---------- 工程与任务 ----------
// focusGroup:从工程配置树某标记组右键打开=该组路径,自动选中对应采集任务
// focusTask/focusNode:从监控中心(异常中心)跳转=按任务名选中并把画布聚焦到肇事节点
// design:设计画布(工程配置右键弹窗)=true → 才显示「模板 ▾」(预置骨架/流程模板);监控中心内嵌画布是运行语境,不给模板功能
const props = defineProps<{ focusGroup?: string; focusTask?: string; focusNode?: string; design?: boolean }>()
const emit = defineEmits<{
  'active-group-change': [groupRef: string]
  'task-enabled-change': [groupRef: string, enabled: boolean]
}>()
// 只能看(只读)账号：画布只能查看/平移/缩放/选中，不能改、不能保存(后端对 MES 修改同样按 flow 页面校验)
const editable = computed(() => canEdit('flow'))
const proj = ref<any>(null)
const rev = ref('')
const pagedProjectMode = ref(false)
const taskIdx = ref(-1)
const dirty = ref(false)
const editVersion = ref(0)
const autoSaveBlocked = ref(false)
const saveError = ref('')
const orphanCleanupCount = ref(0)
const staleUploadCleanupCount = ref(0)
function markDirty(operation = '修改节点配置') { if (!editable.value) return; dirty.value = true; editVersion.value++; flowHistory.record(operation) }
const tasks = computed<any[]>(() => proj.value?.mesUpload?.tasks || [])
const task = computed<any>(() => (taskIdx.value >= 0 ? tasks.value[taskIdx.value] : null))
const collectIntervalMs = computed({
  get: () => task.value?.collectIntervalMs || 1000,
  set: (value: number | undefined) => {
    if (task.value && typeof value === 'number' && Number.isFinite(value) && value > 0)
      task.value.collectIntervalMs = value
  },
})
// 分层采集:判定点常驻轮询,上报点位改为触发满足后按需批量读取。
// 任务级三态 inherit/on/off 对应 null/true/false;null 跟随 mesUpload.tieredAcquisition。
const tieredAcquisition = computed({
  get: () => task.value?.tieredAcquisition === true ? 'on' : task.value?.tieredAcquisition === false ? 'off' : 'inherit',
  set: (mode: string) => {
    if (!task.value) return
    task.value.tieredAcquisition = mode === 'on' ? true : mode === 'off' ? false : null
  },
})
const globalTieredAcquisition = computed({
  get: () => proj.value?.mesUpload?.tieredAcquisition !== false,
  set: (value: boolean) => {
    if (!proj.value) return
    if (!proj.value.mesUpload) proj.value.mesUpload = { tasks: [] }
    proj.value.mesUpload.tieredAcquisition = value
  },
})
// 采集周期节点仅配置周期；已有事件任务仍按原配置展示，避免编辑时改变执行方式。
// 旧工程里本版本不认识的触发方式照常显示并提示，网关会停用这类任务。
const PERIODIC_TRIGGER_MODES = new Set(['', 'onchange', 'interval', 'always'])
const triggerMode = computed(() => {
  const mode = String(task.value?.trigger?.mode || '').toLowerCase()
  return mode === 'interfaceevent' ? 'InterfaceEvent' : PERIODIC_TRIGGER_MODES.has(mode) ? 'Interval' : 'Unsupported'
})
const flow = computed<any>(() => task.value?.flow)
// 操作历史：每个任务保留最近 5 次改动，可回退到任意一次之前(回退本身也能再回退)
const flowHistory = useFlowHistory<any>({
  owner: task,
  snapshot: owner => JSON.stringify({ nodes: owner.flow?.nodes || [], edges: owner.flow?.edges || [] }),
  apply: (owner, json) => {
    const saved = JSON.parse(json)
    if (!owner.flow) owner.flow = { nodes: [], edges: [] }
    owner.flow.nodes = saved.nodes
    owner.flow.edges = saved.edges
  },
})
watch(task, () => flowHistory.track(), { immediate: true })
onUnmounted(() => flowHistory.dispose())
const historyOpen = ref(false)
const historyTime = (at: number) => new Date(at).toLocaleTimeString()
function restoreHistory(entry: { id: number; label: string }) {
  if (canvasBusy.value || !editable.value) return
  const restored = flowHistory.restore(entry.id, t('回退「{name}」', { name: t(entry.label) }))
  if (!restored) { ElMessage.info(t('流程已经是该操作之前的状态')); return }
  selIds.value = new Set(); connecting.value = null
  dirty.value = true; editVersion.value++
  historyOpen.value = false
  ElMessage.success(t('已回退到「{name}」之前，保存后生效', { name: t(entry.label) }))
}
const pagedTaskDescriptions = shallowRef(new Map<string, string>())
const taskDescription = computed(() => pagedTaskDescriptions.value.get(String(task.value?.id || ''))
  || mesGroupDescription(proj.value, task.value?.groupRef || ''))
const fallbackMesTasks = shallowRef<any[]>([])
const scopedTransitionEvents = shallowRef<any[]>([])
const activeTaskStreamKey = ref('mes-flow')
const taskRealtimeStatus = computed(() => realtimeStreamStates.value.get(activeTaskStreamKey.value) || 'idle')
const taskRealtime = new RealtimeStreamClient({
  onState: state => realtime.setConnectionState(state, activeTaskStreamKey.value),
  onFrame: frame => {
    if (frame.event !== 'runtime-event') return
    const applied = realtime.apply(frame, activeTaskStreamKey.value)
    const event = applied.runtimeEvent
    const selectedTaskId = String(task.value?.id || '')
    if (applied.runtimeKind !== 'mes-flow-transition' || String(event?.payload?.taskId || '') !== selectedTaskId) return
    scopedTransitionEvents.value = [...scopedTransitionEvents.value, event].slice(-256)
  },
})
type TaskStateFilter = 'all' | 'running' | 'disabled'
type TaskScopeFilter = 'device' | 'channel'
const taskSearch = ref('')
const taskStateFilter = ref<TaskStateFilter>('all')
const taskScopeFilter = ref<TaskScopeFilter>('device')
const taskStateOptions: { key: TaskStateFilter; label: string }[] = [
  { key: 'all', label: '全部' },
  { key: 'running', label: '运行' },
  { key: 'disabled', label: '停用' },
]
const taskScopeOptions: { key: TaskScopeFilter; label: string }[] = [
  { key: 'device', label: '当前设备' },
  { key: 'channel', label: '当前通道' },
]
const focusDeviceRef = computed(() => String(props.focusGroup || '').split('.').slice(0, 2).join('.'))
const activeDeviceRef = computed(() => String(task.value?.deviceRef || focusDeviceRef.value || '').trim())
const activeChannelRef = computed(() => activeDeviceRef.value.split('.')[0] || '')
const mesCatalog = useMesTaskCatalog(projectApi, { deviceId: '', channelId: '' })

function confirmCurrentTaskDisable() {
  return ElMessageBox.confirm(
    t('该数据块当前正在采集上报。停用后将停止本数据块的采集与上报，是否继续？'),
    t('停用采集流程'),
    { type: 'warning', confirmButtonText: t('确认停用'), cancelButtonText: t('取消') },
  )
}

const taskToggleBusy = ref(false)
async function toggleCurrentTaskEnabled(next: unknown) {
  if (taskToggleBusy.value || !editable.value) return
  const current = task.value
  if (!current) return
  if (!isMesFlowAvailable(current)) {
    ElMessage.info(t('请先在标记组属性中开启「采集流程」'))
    return
  }
  taskToggleBusy.value = true
  const owner = proj.value
  try {
  const result = await setMesTaskEnabledWithConfirmation(
    proj.value?.mesUpload,
    { groupRef: current.groupRef, id: current.id },
    next === true,
    confirmCurrentTaskDisable,
  )
  if (proj.value !== owner) return
  if (result.cancelled) return
  if (result.conflictGroupRef) {
    ElMessage.warning(t('父级和子级采集任务不能同时开启，请先停用：') + result.conflictGroupRef)
    return
  }
  if (!result.changed) return
  markDirty()
  emit('task-enabled-change', String(current.groupRef || ''), current.enabled === true)
  ElMessage.success(t(current.enabled ? '已开启本组采集(保存后生效)' : '已停用本组采集(保存后生效)'))
  } catch (reason: any) { ElMessage.error(reason?.message || t('任务状态更新失败')) }
  finally { taskToggleBusy.value = false }
}
const catalogRows = computed(() => {
  if (mesCatalog.items.value.length || mesCatalog.total.value > 0) return mesCatalog.items.value
  // 兼容升级过程中尚未提供分页目录的 Runtime；只从已经加载的 MES 任务构造轻量目录，
  // 不遍历点表，也不改变 SignalR 运行态。
  return tasks.value.map((item: any) => ({
    id: String(item.id || ''), name: String(item.name || ''), groupId: null,
    groupRef: String(item.groupRef || ''), deviceRef: String(item.deviceRef || ''),
    deviceId: null, channelId: null, description: null,
    enabled: item.enabled === true, flowAvailable: item.flowAvailable !== false, rowVersion: 0,
  }))
})
const taskEntries = computed(() => catalogRows.value.filter((row) => {
  if (row.flowAvailable === false) return false
  const deviceRef = String(row.deviceRef || '')
  return taskScopeFilter.value === 'device'
    ? deviceRef.toLocaleLowerCase() === activeDeviceRef.value.toLocaleLowerCase()
    : deviceRef.split('.')[0]?.toLocaleLowerCase() === activeChannelRef.value.toLocaleLowerCase()
}).map((row) => {
  const normalizedGroupRef = String(row.groupRef || '').toLocaleLowerCase()
  let taskIndex = tasks.value.findIndex((item: any) =>
    normalizedGroupRef && String(item.groupRef || '').toLocaleLowerCase() === normalizedGroupRef)
  if (taskIndex < 0) taskIndex = tasks.value.findIndex((item: any) => item.id === row.id)
  return {
    key: row.id,
    taskIndex,
    configured: taskIndex >= 0,
    name: row.name,
    groupRef: row.groupRef || '',
    deviceRef: row.deviceRef || '',
    description: row.description || mesGroupDescription(proj.value, row.groupRef || ''),
    tagCount: 0,
    enabled: row.enabled,
  }
}))
const filteredTaskEntries = computed(() => {
  const query = taskSearch.value.trim().toLocaleLowerCase()
  return taskEntries.value.filter((entry) => {
    if (taskStateFilter.value === 'running' && !entry.enabled) return false
    if (taskStateFilter.value === 'disabled' && entry.enabled) return false
    return !query || `${entry.name} ${entry.groupRef} ${entry.description}`.toLocaleLowerCase().includes(query)
  })
})
const taskCountText = computed(() => {
  const total = mesCatalog.total.value > 0 ? mesCatalog.total.value : taskEntries.value.length
  return filteredTaskEntries.value.length === total ? String(total) : `${filteredTaskEntries.value.length}/${total}`
})
function clearTaskFilters() {
  taskSearch.value = ''
  taskStateFilter.value = 'all'
}

function catalogContextIds() {
  const activeId = String(task.value?.id || '')
  const catalogRow = catalogRows.value.find((item: any) => item.id === activeId)
  if (catalogRow?.deviceId && catalogRow?.channelId)
    return { channelId: String(catalogRow.channelId), deviceId: String(catalogRow.deviceId) }
  const [channelName, deviceName] = activeDeviceRef.value.split('.')
  const channel = (proj.value?.channels || []).find((item: any) => item.name === channelName)
  const device = (channel?.devices || []).find((item: any) => item.name === deviceName)
  return { channelId: String(channel?.id || ''), deviceId: String(device?.id || '') }
}
async function refreshTaskCatalog() {
  const context = catalogContextIds()
  if (!context.channelId || !context.deviceId) return
  mesCatalog.setContext(context.deviceId, context.channelId)
  await mesCatalog.setScope(taskScopeFilter.value)
}
watch(taskSearch, value => mesCatalog.scheduleSearch(value))
watch(taskStateFilter, value => { void mesCatalog.setState(value === 'disabled' ? 'stopped' : value) })
watch(taskScopeFilter, () => { void refreshTaskCatalog() })

const loadError = ref('')
const projectLoading = ref(false)
const taskLoadingKey = ref('')
let projectLoadGeneration = 0
let taskSelectionGeneration = 0
let projectLoadKey: string | undefined
async function load(preferredTask?: string) {
  if (projectLoading.value && projectLoadKey === preferredTask) return
  const generation = ++projectLoadGeneration
  taskSelectionGeneration++; taskLoadingKey.value = ''
  projectLoadKey = preferredTask; projectLoading.value = true
  try { await loadProject(preferredTask, generation) }
  finally { if (generation === projectLoadGeneration) projectLoading.value = false }
}
async function loadProject(preferredTask: string | undefined, generation: number) {
  loadError.value = ''
  try {
    await loadPagedProject(preferredTask, generation)
    return
  } catch (reason: any) {
    // Only an unsupported catalog endpoint permits the legacy full-project path.
    if (!reason?.legacyCatalogUnavailable) throw reason
  }
  const r = await api.project()
  if (generation !== projectLoadGeneration) return
  pagedProjectMode.value = false
  // 数组与范围有效性以服务端统一识别目录为准。浏览器不再按显示名称猜测并清空配置。
  orphanCleanupCount.value = 0
  staleUploadCleanupCount.value = 0
  // 采集周期现在只负责固定节拍。清理旧工程遗留的 Trigger.conditions，
  // 条件判断统一由独立「条件」节点表达，避免同一流程存在两套入口条件。
  let legacyTriggerConditions = 0
  let legacyImportedStructs = 0
  for (const t of r.proj?.mesUpload?.tasks || [])
    for (const n of t.flow?.nodes || []) {
      if (n.type === 'Trigger' && Array.isArray(n.conditions) && n.conditions.length) {
        legacyTriggerConditions += n.conditions.length
        n.conditions = []
      }
      if (n.type === 'Upload') legacyImportedStructs += upgradeLegacyImportedStructs(n.payload)
    }
  proj.value = r.proj; rev.value = r.rev
  dirty.value = editable.value && (legacyTriggerConditions > 0 || legacyImportedStructs > 0)
  if (legacyTriggerConditions || legacyImportedStructs) editVersion.value++
  if (staleUploadCleanupCount.value)
    ElMessage.warning(t('已保留流程并清空 {count} 个来源已失效的上报数据范围，请重新选择', { count: staleUploadCleanupCount.value }))
  autoSaveBlocked.value = false; saveError.value = ''
  // 从某个标记组右键打开画布时:自动选中该组的采集任务(没有就自动新建绑本组的)
  if (preferredTask) {
    const index = tasks.value.findIndex((item: any) => item.name === preferredTask)
    if (index >= 0) pickTask(index)
    return
  }
  if (props.focusGroup) {
    // 右键入口已经按“PLC 数据块边界”过滤；这里必须保留完整路径。
    // 例如 通道.设备.DB容器.DB1604，不能再截成前三段而错误绑定到最外层容器。
    const groupRef = props.focusGroup.trim()
    let gi = tasks.value.findIndex((t: any) => t.groupRef === groupRef)
    if (gi < 0) {
      gi = createTaskForGroup(groupRef)   // 本数据块还没采集任务 → 自动建绑组任务(默认停用,防误发)
      markDirty()
    }
    pickTask(gi)
    // 新建任务保持空画布；用户可自由添加节点或明确选择模板。
    return
  }
  // 从监控中心跳转:按任务名选中 + 聚焦肇事节点(渲染完成后平移居中并选中,归因面板随选中自动展开)
  if (props.focusTask) {
    const gi = tasks.value.findIndex((t: any) => t.name === props.focusTask)
    if (gi >= 0) {
      pickTask(gi)
      await nextTick()
      if (props.focusNode) focusNodeById(props.focusNode)
      return
    }
  }
  if (taskIdx.value < 0 && tasks.value.length) {
    const withNodes = tasks.value.findIndex((t: any) => t.flow?.nodes?.length)
    pickTask(withNodes >= 0 ? withNodes : 0)
  }
}
onMounted(() => {
  load().catch(reason => { loadError.value = reason?.response?.data?.error || reason?.message || t('载入采集流程失败'); ElMessage.error(loadError.value) })
  startLive()
})
onUnmounted(() => {
  projectLoadGeneration++; taskSelectionGeneration++
  stopLive()
  taskRealtime.stop()
  mesCatalog.dispose()
})
// 已在画布上又点了监控中心的另一条异常:props 变化 → 切任务/重新聚焦
watch(() => [props.focusTask, props.focusNode], async ([focusTask, focusNode], _previous, onCleanup) => {
  if (!focusTask) return
  let cancelled = false
  const generation = projectLoadGeneration
  const current = () => !cancelled && generation === projectLoadGeneration
  onCleanup(() => { cancelled = true })
  try {
    let gi = tasks.value.findIndex((item: any) => item.name === focusTask)
    if (gi < 0 && pagedProjectMode.value) {
      const wanted = await findTaskByName(focusTask)
      if (!wanted || !current()) return
      await pickTaskEntry({ key: wanted.id, taskIndex: -1 })
      if (!current()) return
      gi = tasks.value.findIndex((item: any) => item.name === focusTask)
    }
    if (gi < 0) return
    if (gi !== taskIdx.value) pickTask(gi)
    await nextTick()
    if (current() && focusNode) focusNodeById(focusNode)
  } catch (reason: any) { if (current()) ElMessage.error(reason?.message || t('载入采集流程失败')) }
})

function pickTask(i: number) {
  taskSelectionGeneration++; taskLoadingKey.value = ''
  taskIdx.value = i
  selIds.value.clear(); connecting.value = null
  resetLiveTrace(); liveStates.value = {}
  const t = tasks.value[i]
  if (t) {
    if (!t.flow) t.flow = { nodes: [], edges: [] }
    emit('active-group-change', String(t.groupRef || ''))
    void refreshTaskCatalog()
  }
}

async function findTaskByName(name: string) {
  let offset = 0
  while (true) {
    const page = await projectApi.mesTasks({ scope: 'all', q: name, offset, limit: 200 })
    const found = page.items.find(item => item.name === name && item.flowAvailable)
    if (found) return found
    if (!page.items.length || offset + page.items.length >= page.total) return undefined
    offset += page.items.length
  }
}
async function loadPagedProject(preferredTask?: string, generation = projectLoadGeneration) {
  pagedProjectMode.value = true
  const focus = String(preferredTask || props.focusGroup || props.focusTask || '').trim()
  let page
  try {
    page = await projectApi.mesTasks({ scope: 'all', q: focus || undefined, offset: 0, limit: 200 })
  } catch (reason: any) {
    if ([404, 405, 501].includes(reason?.response?.status))
      throw { legacyCatalogUnavailable: true, cause: reason }
    throw reason
  }
  const rows = page.items.filter(item => item.flowAvailable)
  const wanted = preferredTask ? await findTaskByName(preferredTask) : rows.find(item => props.focusGroup
    ? String(item.groupRef || '').toLocaleLowerCase() === props.focusGroup!.trim().toLocaleLowerCase()
    : props.focusTask
      ? item.name === props.focusTask
      : false) || (props.focusGroup || props.focusTask ? undefined : rows[0])
  const loaded = wanted ? await projectApi.mesTask(wanted.id) : null
  if (generation !== projectLoadGeneration) return
  pagedTaskDescriptions.value = new Map(rows.map(item => [item.id, item.description || '']))
  mesCatalog.items.value = rows
  mesCatalog.total.value = page.total
  mesCatalog.offset.value = page.offset
  proj.value = { channels: [], mesUpload: { tasks: loaded ? [loaded] : [] } }
  rev.value = projectApi.revision()
  dirty.value = false
  autoSaveBlocked.value = false
  saveError.value = ''
  if (wanted?.deviceId && wanted?.channelId)
    mesCatalog.setContext(wanted.deviceId, wanted.channelId)
  if (loaded) {
    pickTask(0)
    await nextTick()
    if (!preferredTask && props.focusNode) focusNodeById(props.focusNode)
  } else if (!preferredTask && props.focusGroup) pickTask(createTaskForGroup(props.focusGroup.trim()))
}
async function pickTaskEntry(entry: any) {
  if (taskLoadingKey.value === entry.key) return
  const generation = ++taskSelectionGeneration, owner = proj.value
  let index = entry.taskIndex
  if (index < 0) {
    if (pagedProjectMode.value && entry.key) {
      taskLoadingKey.value = entry.key
      try {
        const loaded = await projectApi.mesTask(entry.key)
        if (generation !== taskSelectionGeneration || proj.value !== owner) return
        index = tasks.value.length
        tasks.value.push(loaded)
        rev.value = projectApi.revision() || rev.value
      } catch (reason: any) {
        if (generation === taskSelectionGeneration) ElMessage.error(reason?.response?.data?.error || reason?.message || t('载入采集流程失败'))
        return
      } finally { if (generation === taskSelectionGeneration) taskLoadingKey.value = '' }
    } else index = createTaskForGroup(entry.groupRef)
    pickTask(index)
    return
  }
  pickTask(index)
}
// 本组还没采集任务时,自动新建一个绑到本组的任务(结构同「一键全开采集」),返回其下标
function createTaskForGroup(groupPath: string): number {
  const seg = groupPath.split('.')
  if (!proj.value.mesUpload) proj.value.mesUpload = { tasks: [] }
  const list = (proj.value.mesUpload.tasks ||= [])
  const baseName = seg[seg.length - 1] || '采集任务'
  let taskName = baseName
  for (let n = 2; list.some((t: any) => t.name === taskName); n++) taskName = `${baseName}-${n}`
  list.push({
    id: createCompactId(), name: taskName,
    groupRef: groupPath, deviceRef: seg.slice(0, 2).join('.'), flowAvailable: true, enabled: false,   // 显式进入流程设计即开放功能；仍默认停用，防止一保存就每周期狂发 MES
    collectIntervalMs: 1000, confirmRewrite: true,
    payload: { mode: 'DataRanges', dataRanges: [], fields: [], structs: [], append: {} }, steps: [], flow: { nodes: [], edges: [] },
  })
  return list.length - 1
}

const saving = ref(false)
const backgroundSaving = ref(false)
const saveBusy = computed(() => saving.value || backgroundSaving.value)
const batchSaveOpen = ref(false)
async function saveAllTasks(change: MesBatchPeriodChange, expectedRevision: string): Promise<void> {
  if (saveBusy.value) throw new Error(t('正在保存，请稍后重试。'))
  if (expectedRevision !== rev.value) throw new Error(t('工程配置已变化，请关闭窗口并重新载入采集流程后重试。'))
  const savedVersion = editVersion.value
  const owner = proj.value, generation = projectLoadGeneration
  saving.value = true
  try {
  await allowFlowPaint()
  if (proj.value !== owner || generation !== projectLoadGeneration) throw new Error(t('流程上下文已变化，本次操作已取消'))
  const drafts = cloneReactiveValue(tasks.value)
  validateTaskExtraFields(drafts)
  for (const draft of drafts)
    for (const node of draft.flow?.nodes || []) {
      delete node._interval; delete node._append
      if (node.type === 'Trigger') delete node.conditions
    }
    const result = await projectApi.mutate({
      kind: 'saveMesTasks', entityKind: 'project', entityIds: [], targetId: null,
      documentJson: JSON.stringify({ tasks: drafts, ...change }),
    }, expectedRevision)
    if (!result.applied) throw new Error(t('批量保存失败'))
    rev.value = result.revision || projectApi.revision() || rev.value
    if (change.collectIntervalMs !== undefined) {
      const selected = new Set(change.periodTaskIds)
      for (const item of tasks.value) if (selected.has(String(item.id))) item.collectIntervalMs = change.collectIntervalMs
    }
    if (editVersion.value === savedVersion) dirty.value = false
    autoSaveBlocked.value = false; saveError.value = ''
    await nextTick()
  } catch (reason) {
    saveError.value = (reason as any)?.response?.data?.error || (reason as Error).message
    if ((reason as any)?.response?.status === 409) autoSaveBlocked.value = true
    throw reason
  } finally { saving.value = false }
}
async function save(automatic = false): Promise<boolean> {
  if (!proj.value || !editable.value) return true   // 只读账号没有可保存的改动
  if (!task.value && orphanCleanupCount.value === 0 && staleUploadCleanupCount.value === 0) return true
  if (saveBusy.value) return false
  const savedVersion = editVersion.value
  const owner = proj.value, generation = projectLoadGeneration
  const activeSaving = automatic ? backgroundSaving : saving
  activeSaving.value = true
  saveError.value = ''
  try {
  await allowFlowPaint()
  if (proj.value !== owner || generation !== projectLoadGeneration) return false
  // Save every locally edited task, including drafts retained after switching tasks.
  const snapshot = cloneReactiveValue(proj.value.mesUpload)
  const savedTasks = snapshot.tasks || []
  for (const savedTask of savedTasks)
    for (const n of savedTask.flow?.nodes || []) {
      delete n._interval; delete n._append
      if (n.type === 'Trigger') delete n.conditions
    }
    validateTaskExtraFields(savedTasks)
    if (pagedProjectMode.value) {
      await projectApi.mutate(savedTasks.length === 1 ? {
        kind: 'replaceMesTask', entityKind: 'group', entityIds: [String(savedTasks[0].id || '')],
        targetId: null, documentJson: JSON.stringify(savedTasks[0]),
      } : {
        kind: 'saveMesTasks', entityKind: 'project', entityIds: [], targetId: null,
        documentJson: JSON.stringify({ tasks: savedTasks, periodTaskIds: [] }),
      }, rev.value)
      rev.value = projectApi.revision() || rev.value
    } else {
      const result = await api.putMesUpload(snapshot, rev.value, !automatic || orphanCleanupCount.value > 0)
      // 服务端直接返回保存后的 rev；不再为了拿 rev 重新下载整个工程。
      if (result.rev) rev.value = result.rev
    }
    orphanCleanupCount.value = 0
    staleUploadCleanupCount.value = 0
    if (editVersion.value === savedVersion) dirty.value = false
    autoSaveBlocked.value = false
    if (!automatic) ElMessage.success(t('已保存并热重载生效'))
    return true
  } catch (e: any) {
    saveError.value = e?.response?.data?.error || e.message || t('未知错误')
    if (e?.response?.status === 409) {
      autoSaveBlocked.value = true
      if (!automatic)
        ElMessageBox.confirm(saveError.value + '\n' + t('重新载入(丢弃未保存修改)?'), t('保存冲突'), { type: 'warning' }).then(() => load()).catch(() => {})
    } else if (!automatic) ElMessage.error(t('保存失败:') + saveError.value)
    return false
  } finally { activeSaving.value = false }
}

const autoSave = useTimedAutoSave({
  dirty,
  saving: saveBusy,
  changeVersion: editVersion,
  save: () => save(true),
  canSave: () => editable.value && !autoSaveBlocked.value && !batchSaveOpen.value,
  intervalMs: 30_000,
})
const autoSaveState = autoSave.state
const autoSavedText = autoSave.lastSavedText
function syncTaskEnabledFromProject(groupRef: string, enabled: boolean) {
  const normalizedRef = String(groupRef || '').trim().toLowerCase()
  if (!normalizedRef) return
  const current = tasks.value.find((item: any) =>
    String(item?.groupRef || '').trim().toLowerCase() === normalizedRef)
  if (!current || current.enabled === enabled || !editable.value) return
  current.enabled = enabled
  markDirty()
}
defineExpose({ flushAutoSave: autoSave.flush, syncTaskEnabledFromProject })

// ---------- 画布几何(与桌面版同一套) ----------
const NodeW = 170
// 运行态需要一行独立的状态栏，避免只靠边框颜色表达状态。
const nodeH = (n: any) => Math.max(78, 38 + outPorts(n).length * 22)
function outPorts(n: any): string[] {
  switch (n.type) {
    case 'Branch': return ['success', 'fail']
    case 'Condition': return [...(n.branches || []).map((_: any, i: number) => 'b' + i), 'default']
    case 'Switch': return [...(n.cases || []).map((_: any, i: number) => 'case' + i), 'default']
    default: return ['out']
  }
}
function portLabel(n: any, p: string): string {
  if (p === 'success') return '成'
  if (p === 'fail') return '败'
  if (p === 'default') return '默认'
  if (p.startsWith('case')) { const i = +p.slice(4); return '=' + ((n.cases || [])[i] ?? '?') }
  if (p[0] === 'b' && /^\d+$/.test(p.slice(1))) { const i = +p.slice(1); return (n.branches || [])[i]?.label || '条件' + (i + 1) }
  return ''
}
function portLocal(side: string, i: number, count: number, w: number, h: number) {
  const t = (i + 1) / (count + 1)
  switch (side) { case 'Left': return { x: 0, y: h * t }; case 'Top': return { x: w * t, y: 0 }; case 'Bottom': return { x: w * t, y: h }; default: return { x: w, y: h * t } }
}
const outLocal = (n: any, p: string) => { const ps = outPorts(n); let i = ps.indexOf(p); if (i < 0) i = 0; return portLocal(n.outSide || 'Right', i, ps.length, NodeW, nodeH(n)) }
const inLocal = (n: any) => portLocal(n.inSide || 'Left', 0, 1, NodeW, nodeH(n))
const outAnchor = (n: any, p: string) => { const l = outLocal(n, p); return { x: n.x + l.x, y: n.y + l.y } }
const inAnchor = (n: any) => { const l = inLocal(n); return { x: n.x + l.x, y: n.y + l.y } }
// 输出口标签摆放:左右侧=水平贴边;上下侧=竖排(writing-mode 竖向、字正立),否则多口挤一起横排看不清
function outLabel(n: any, p: string) {
  const l = outLocal(n, p), side = n.outSide || 'Right'
  if (side === 'Bottom') return { x: l.x, y: l.y - 10, anchor: 'end', vertical: true }    // 底部口:标签在口上方=框内,往上竖排
  if (side === 'Top') return { x: l.x, y: l.y + 10, anchor: 'start', vertical: true }      // 顶部口:标签在口下方=框内,往下竖排
  if (side === 'Left') return { x: l.x + 11, y: l.y + 3.5, anchor: 'start', vertical: false }
  return { x: l.x - 11, y: l.y + 3.5, anchor: 'end', vertical: false }   // Right
}
const sideDir = (s: string) => (s === 'Left' ? { x: -1, y: 0 } : s === 'Top' ? { x: 0, y: -1 } : s === 'Bottom' ? { x: 0, y: 1 } : { x: 1, y: 0 })
function edgeCtrl(f: any, p: string, t: any) {
  const a = outAnchor(f, p), b = inAnchor(t)
  const d = Math.max(45, Math.hypot(b.x - a.x, b.y - a.y) * 0.4)
  const d1 = sideDir(f.outSide || 'Right'), d2 = sideDir(t.inSide || 'Left')
  const c1 = { x: a.x + d1.x * d, y: a.y + d1.y * d }, c2 = { x: b.x + d2.x * d, y: b.y + d2.y * d }
  return { a, b, c1, c2 }
}
function edgePath(f: any, p: string, t: any): string {
  const { a, b, c1, c2 } = edgeCtrl(f, p, t)
  return `M ${a.x} ${a.y} C ${c1.x} ${c1.y}, ${c2.x} ${c2.y}, ${b.x} ${b.y}`
}
// 连线中点(贝塞尔 t=0.5 实际点,不是直线中点)——删除 ✕ 放这里才不会错位
function edgeMid(f: any, p: string, t: any) {
  const { a, b, c1, c2 } = edgeCtrl(f, p, t)
  return { x: (a.x + 3 * c1.x + 3 * c2.x + b.x) / 8, y: (a.y + 3 * c1.y + 3 * c2.y + b.y) / 8 }
}
const nodeById = (id: string) => flow.value?.nodes.find((n: any) => n.id === id)
// 旧工程里本版本不认识的节点类型：照常显示，只供查看和删除，网关会停用含此节点的任务。
const unsupportedNodeMeta = computed(() => ({ title: t('不支持的节点'), color: '#8A94A0', icon: '⊘', unsupported: true }))
const typeMeta: any = computed(() => ({
  ...Object.fromEntries((flow.value?.nodes || []).map((n: any) => [n.type, unsupportedNodeMeta.value])),
  Trigger: { title: t(triggerMode.value === 'InterfaceEvent' ? '后台接口事件' : triggerMode.value === 'Unsupported' ? '不支持的触发方式' : '采集周期'), color: '#2E7D32', icon: '⏱' },
  Condition: { title: t('条件'), color: '#00796B', icon: '☰' },
  Upload: { title: t('上报'), color: '#1565C0', icon: '☁' },
  Branch: { title: t('判成败'), color: '#EF6C00', icon: '⇄' },
  Switch: { title: t('值分流'), color: '#6A1B9A', icon: '⑃' },
  // 旧流程里的回写节点。它只写用户显式配的项，而新的「回写」会先按接口响应自动映射一批点位，
  // 两者行为不同，所以不自动改写已有流程：只从新建面板里拿掉，打开旧流程时照常显示和执行。
  Writeback: { title: t('回写(旧)'), color: '#7B1FA2', icon: '✎', legacy: true },
  ResponseWriteback: { title: t('回写'), color: '#5E35B1', icon: '↙' },
  FixedData: { title: t('固定值'), color: '#00897B', icon: '＝' },
  Delay: { title: t('延时'), color: '#546E7A', icon: '…' },
}))
const paletteMeta = computed(() => Object.fromEntries(Object.entries(typeMeta.value)
  .filter(([, meta]) => !(meta as any)?.legacy && !(meta as any)?.unsupported)))
const portFill = (p: string, n: any) => (p === 'success' ? '#1B7F37' : p === 'fail' ? '#C0392B' : typeMeta.value[n.type]?.color || '#888')
const edgeColor = (e: any) => (e.fromPort === 'success' ? '#1B7F37' : e.fromPort === 'fail' ? '#C0392B' : '#98A2AE')
const edgeMarker = (e: any) => e.fromPort === 'success' ? 'url(#mes-arrow-ok)' : e.fromPort === 'fail' ? 'url(#mes-arrow-fail)' : 'url(#mes-arrow)'

// ---------- 选择 / 拖动 / 平移缩放 ----------
const selIds = ref(new Set<string>())
const zoom = ref(1); const panX = ref(0); const panY = ref(0)
const CanvasExtent = 50_000   // 双向大画布；仅防止异常指针把坐标推到无限大，不再以 0 作为左/上边界
const zoomPct = computed(() => Math.round(zoom.value * 100))
function resetView() { zoom.value = 1; panX.value = 0; panY.value = 0 }
function zoomBy(delta: number) { zoom.value = Math.min(2, Math.max(.35, +(zoom.value + delta).toFixed(2))) }
const svgRef = ref<SVGSVGElement>()
const drag = reactive<any>({ mode: '', sx: 0, sy: 0, bases: null, marq: null })
const connecting = ref<any>(null)   // {id, port, ax, ay}(ax/ay=起点画布坐标,用于区分点一下还是拖)
const mouse = reactive({ x: 0, y: 0 })

function toCanvas(ev: MouseEvent) {
  const r = svgRef.value!.getBoundingClientRect()
  return { x: (ev.clientX - r.left - panX.value) / zoom.value, y: (ev.clientY - r.top - panY.value) / zoom.value }
}
function onWheel(ev: WheelEvent) {
  ev.preventDefault()
  const k = ev.deltaY < 0 ? 1.1 : 1 / 1.1
  const nz = Math.min(2.5, Math.max(0.3, zoom.value * k))
  const r = svgRef.value!.getBoundingClientRect()
  const cx = ev.clientX - r.left, cy = ev.clientY - r.top
  panX.value = cx - ((cx - panX.value) / zoom.value) * nz
  panY.value = cy - ((cy - panY.value) / zoom.value) * nz
  zoom.value = nz
}
function onCanvasDown(ev: MouseEvent) {
  if (connecting.value) { connecting.value = null; return }
  if (ev.ctrlKey) { const p = toCanvas(ev); drag.mode = 'marq'; drag.marq = { x1: p.x, y1: p.y, x2: p.x, y2: p.y }; return }
  selIds.value = new Set(); drag.mode = 'pan'; drag.sx = ev.clientX - panX.value; drag.sy = ev.clientY - panY.value
}
function onMove(ev: MouseEvent) {
  if (!drag.mode && !connecting.value) return
  const p = toCanvas(ev); mouse.x = p.x; mouse.y = p.y
  if (drag.mode === 'pan') { panX.value = ev.clientX - drag.sx; panY.value = ev.clientY - drag.sy }
  else if (drag.mode === 'node' && drag.bases) {
    const dx = p.x - drag.sx, dy = p.y - drag.sy
    for (const [n, b] of drag.bases) {
      n.x = Math.max(-CanvasExtent, Math.min(CanvasExtent, b.x + dx))
      n.y = Math.max(-CanvasExtent, Math.min(CanvasExtent, b.y + dy))
    }
  } else if (drag.mode === 'marq' && drag.marq) { drag.marq.x2 = p.x; drag.marq.y2 = p.y }
}
// 画布坐标下命中测试:该点落在哪个节点上(后画的在上层,倒序找)
function nodeAt(x: number, y: number): any | null {
  const ns = flow.value?.nodes || []
  for (let i = ns.length - 1; i >= 0; i--) {
    const n = ns[i]
    if (x >= n.x - 8 && x <= n.x + NodeW + 8 && y >= n.y - 8 && y <= n.y + nodeH(n) + 8) return n   // 松手判定放宽 8px(端口圈在边上)
  }
  return null
}
function onUp(ev: MouseEvent) {
  if (drag.mode === 'marq' && drag.marq) {
    const m = drag.marq
    const x1 = Math.min(m.x1, m.x2), x2 = Math.max(m.x1, m.x2), y1 = Math.min(m.y1, m.y2), y2 = Math.max(m.y1, m.y2)
    for (const n of flow.value?.nodes || [])
      if (n.x < x2 && n.x + NodeW > x1 && n.y < y2 && n.y + nodeH(n) > y1) selIds.value.add(n.id)
    selIds.value = new Set(selIds.value)
  }
  if (drag.mode === 'node' && drag.bases?.some(([n, b]: any) => n.x !== b.x || n.y !== b.y)) markDirty('移动节点')
  drag.mode = ''; drag.bases = null; drag.marq = null
  // 拖线松手:不管落在什么元素上(端口圈/文字/连线/✕),一律按坐标找节点完成 —— 事件被谁吃掉都不影响
  const c = connecting.value
  if (c) {
    const p = toCanvas(ev)
    const moved = Math.hypot(p.x - c.ax, p.y - c.ay) > 6
    if (c.mode === 'source') {                              // 拖源端:松在某节点上(不必精确对准输出口)→ 改这条线的起点
      let hp = portAt(p.x, p.y)
      if (!hp) { const n = nodeAt(p.x, p.y); if (n) hp = { node: n, port: nearestOutPort(n, p) } }   // 没对准端口但落在节点上 → 取该节点最近的输出口
      if (hp && hp.node.id !== c.toId) finishReconnectSource(hp.node.id, hp.port)
      else if (moved) connecting.value = null               // 松在纯空白 → 取消(还原)
    } else {
      const hit = nodeAt(p.x, p.y)
      if (hit && hit.id !== c.id) finishConnect(hit.id)      // 松在目标上 → 连上(拖放式)
      else if (moved && !hit) connecting.value = null         // 拖了一段却松在空白 → 取消
      /* 没拖动(原地点一下端口)→ 保持连线中,支持「点端口→再点目标」的点选式 */
    }
  }
}
// 完成连线(去重:一个出口一条线)
function finishConnect(toId: string) {
  const c = connecting.value
  if (!c || c.id === toId) { connecting.value = null; return }
  flow.value.edges = flow.value.edges.filter((e: any) => !(e.from === c.id && e.fromPort === c.port))
  flow.value.edges.push({ from: c.id, fromPort: c.port, to: toId })
  markDirty('连线'); connecting.value = null
}
function onNodeDown(n: any, ev: MouseEvent) {
  ev.stopPropagation()
  if (connecting.value) { finishConnect(n.id); return }   // 点端口→点目标(click 式连线)
  if (ev.ctrlKey) { selIds.value.has(n.id) ? selIds.value.delete(n.id) : selIds.value.add(n.id); selIds.value = new Set(selIds.value) }
  else if (!selIds.value.has(n.id)) selIds.value = new Set([n.id])
  if (!editable.value) return   // 只读：只选中，不拖动
  const p = toCanvas(ev)
  drag.mode = 'node'; drag.sx = p.x; drag.sy = p.y
  drag.bases = [...(flow.value.nodes as any[])].filter((x) => selIds.value.has(x.id)).map((x) => [x, { x: x.x, y: x.y }])
}
// 端口按住拖到目标松开(drag 式连线)——在目标节点上松鼠标即连上
function onNodeUp(n: any, ev: MouseEvent) {
  if (connecting.value && connecting.value.mode !== 'source' && connecting.value.id !== n.id) { ev.stopPropagation(); finishConnect(n.id) }
}
function startConnect(n: any, p: string, ev: MouseEvent) {
  ev.stopPropagation()
  // 连线进行中又按到某节点的端口:目标是别的节点 → 这就是"点目标"完成连线(以前这里重新武装,导致点到端口圈就连不上)
  if (connecting.value && connecting.value.id !== n.id) { finishConnect(n.id); return }
  const a = toCanvas(ev)
  connecting.value = { id: n.id, port: p, ax: a.x, ay: a.y }
}
// 抓已有连线的两端点改接:
// 目标端(源固定,拖到新节点)——抓线身 或 目标端手柄:
function startRerouteTarget(e: any, ev: MouseEvent) {
  ev.stopPropagation()
  const f = nodeById(e.from); const a = outAnchor(f, e.fromPort)
  connecting.value = { mode: 'target', id: e.from, port: e.fromPort, ax: a.x, ay: a.y, reroute: e }
}
const startReroute = startRerouteTarget   // 抓线身 = 改目标端(沿用原交互)
// 源端(目标固定,拖到别的节点的输出口)——改这条线的起点:
function startRerouteSource(e: any, ev: MouseEvent) {
  ev.stopPropagation()
  const t = nodeById(e.to); const b = inAnchor(t)
  connecting.value = { mode: 'source', toId: e.to, ax: b.x, ay: b.y, reroute: e }
}
// 落在节点上但没精确对准输出口时,取该节点里离落点最近的输出口(让「改起点」也能像「改终点」那样松在节点上即可)
function nearestOutPort(n: any, p: { x: number; y: number }): string {
  const ps = outPorts(n); if (!ps.length) return 'out'
  let best = ps[0], bd = Infinity
  for (const port of ps) { const a = outAnchor(n, port); const d = Math.hypot(p.x - a.x, p.y - a.y); if (d < bd) { bd = d; best = port } }
  return best
}
// 输出口命中测试(源端拖放的落点)
function portAt(x: number, y: number): { node: any; port: string } | null {
  const ns = flow.value?.nodes || []
  for (let i = ns.length - 1; i >= 0; i--) {
    const n = ns[i]
    for (const p of outPorts(n)) { const a = outAnchor(n, p); if (Math.hypot(x - a.x, y - a.y) <= 13) return { node: n, port: p } }
  }
  return null
}
// 改起点:删旧边 + 按新出口去重 + 连回原目标
function finishReconnectSource(fromId: string, fromPort: string) {
  const c = connecting.value
  if (!c || fromId === c.toId) { connecting.value = null; return }
  flow.value.edges = flow.value.edges.filter((e: any) => e !== c.reroute && !(e.from === fromId && e.fromPort === fromPort))
  flow.value.edges.push({ from: fromId, fromPort, to: c.toId })
  markDirty('改接连线'); connecting.value = null
}
// 立方贝塞尔在参数 t 处的实际点
function cubicAt(a: any, c1: any, c2: any, b: any, t: number) {
  const u = 1 - t, w0 = u * u * u, w1 = 3 * u * u * t, w2 = 3 * u * t * t, w3 = t * t * t
  return { x: w0 * a.x + w1 * c1.x + w2 * c2.x + w3 * b.x, y: w0 * a.y + w1 * c1.y + w2 * c2.y + w3 * b.y }
}
// 两端手柄位置:取贝塞尔曲线上「靠近两端」的实际点(t=0.14 / 0.86)——手柄一定落在线上。
// (原来按端口方向偏 16px:曲线一拐弯,点就飘到线外了,正是你看到的"点没在线上"。)
function edgeEnds(e: any) {
  const f = nodeById(e.from), t = nodeById(e.to)
  const { a, b, c1, c2 } = edgeCtrl(f, e.fromPort, t)
  return { src: cubicAt(a, c1, c2, b, 0.14), tgt: cubicAt(a, c1, c2, b, 0.86) }
}
function delEdge(e: any) { flow.value.edges.splice(flow.value.edges.indexOf(e), 1); markDirty('删除连线') }

// 键盘:Del 删,Ctrl+S 保存,Ctrl+A/C/X/V
let clip: { nodes: any[]; edges: any[] } | null = null
let pasteN = 0
const canvasBusy = ref('')
async function runCanvasWork(label: string, work: () => void) {
  if (canvasBusy.value) return
  const owner = flow.value, version = editVersion.value, generation = projectLoadGeneration, selection = [...selIds.value].join('|')
  canvasBusy.value = t(label)
  try {
    await allowFlowPaint()
    if (flow.value !== owner || editVersion.value !== version || generation !== projectLoadGeneration || [...selIds.value].join('|') !== selection) return
    work()
  } catch (reason: any) { ElMessage.error(reason?.message || t('画布操作失败')) }
  finally { canvasBusy.value = '' }
}
function onKey(ev: KeyboardEvent) {
  if ((ev.ctrlKey || ev.metaKey) && ev.key.toLowerCase() === 's') {
    ev.preventDefault()
    if (editable.value) void save(false)
    return
  }
  const t = ev.target as HTMLElement
  if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.isContentEditable)) return
  if (!flow.value) return
  if (ev.ctrlKey && ev.key.toLowerCase() === 'a') { selIds.value = new Set(flow.value.nodes.map((n: any) => n.id)); ev.preventDefault(); return }
  if (!editable.value) return   // 只读：不删、不复制粘贴
  if (ev.key === 'Delete') { delSelected(); ev.preventDefault() }
  else if (ev.ctrlKey && ev.key.toLowerCase() === 'c') { void runCanvasWork('正在复制节点…', copySel); ev.preventDefault() }
  else if (ev.ctrlKey && ev.key.toLowerCase() === 'x') { void runCanvasWork('正在剪切节点…', () => { copySel(); delSelected() }); ev.preventDefault() }
  else if (ev.ctrlKey && ev.key.toLowerCase() === 'v') { void runCanvasWork('正在粘贴节点…', paste); ev.preventDefault() }
}
// 鼠标在画布外松开时 SVG 收不到 mouseup，拖动状态会一直挂着(节点跟着鼠标走、画布像卡住)，这里兜底结束。
function onWindowMouseUp(ev: MouseEvent) { if (drag.mode) onUp(ev) }
onMounted(() => window.addEventListener('keydown', onKey))
onUnmounted(() => window.removeEventListener('keydown', onKey))
onMounted(() => window.addEventListener('mouseup', onWindowMouseUp))
onUnmounted(() => window.removeEventListener('mouseup', onWindowMouseUp))
function copySel() {
  if (!selIds.value.size) return
  const ns = flow.value.nodes.filter((n: any) => selIds.value.has(n.id))
  clip = { nodes: JSON.parse(JSON.stringify(ns)), edges: JSON.parse(JSON.stringify(flow.value.edges.filter((e: any) => selIds.value.has(e.from) && selIds.value.has(e.to)))) }
  pasteN = 0
  ElMessage.success(t('已复制 {count} 个节点', { count: ns.length }))
}
function paste() {
  if (!clip) return
  pasteN++
  const map: any = {}
  const added: string[] = []
  for (const src of clip.nodes) {
    const n = JSON.parse(JSON.stringify(src))
    map[src.id] = n.id = Math.random().toString(36).slice(2, 10)
    n.x += 30 * pasteN; n.y += 30 * pasteN
    flow.value.nodes.push(n); added.push(n.id)
  }
  for (const e of clip.edges) if (map[e.from] && map[e.to]) flow.value.edges.push({ from: map[e.from], fromPort: e.fromPort, to: map[e.to] })
  selIds.value = new Set(added); markDirty('粘贴节点')
}
// 节点右上角悬停出现的 ✕：直接删掉这一个节点和连到它的线，不用先选中再去属性面板里删
function delNode(n: any) {
  flow.value.edges = flow.value.edges.filter((e: any) => e.from !== n.id && e.to !== n.id)
  flow.value.nodes = flow.value.nodes.filter((item: any) => item.id !== n.id)
  if (selIds.value.has(n.id)) { const next = new Set(selIds.value); next.delete(n.id); selIds.value = next }
  markDirty('删除节点')
}
function delSelected() {
  if (!selIds.value.size) return
  flow.value.edges = flow.value.edges.filter((e: any) => !selIds.value.has(e.from) && !selIds.value.has(e.to))
  flow.value.nodes = flow.value.nodes.filter((n: any) => !selIds.value.has(n.id))
  selIds.value = new Set(); markDirty('删除节点')
}

// ---------- 加节点 / 模板 / 排布 ----------
function newNode(type: string) {
  if (!typeMeta.value[type] || typeMeta.value[type].unsupported) return
  const n: any = { id: Math.random().toString(36).slice(2, 10), type, title: typeMeta.value[type].title, x: 60 + (flow.value.nodes.length % 5) * 40, y: 60 + (flow.value.nodes.length % 5) * 40,
    inSide: 'Left', outSide: 'Right', conditions: [], payload: { mode: 'FieldMapping', dataRanges: [], fields: [], structs: [], append: {} },
    http: { url: '', method: 'POST', timeoutSec: 30, successKey: 'httpStatusCode', successValue: '200', parameterDicName: 'parameterDic', bodyTemplate: '' },
    captures: [], writeBack: { fromResponse: [], manual: [] }, delayMs: 1000, switchTag: '', cases: ['1', '2'], continuous: false,
    branches: [], judgeField: '', judgeOp: 'Eq', judgeValue: '', judgeConds: [], judgeCombine: 'All', fixedValues: [] }
  if (type === 'Condition') n.branches = [{ label: '条件1', conditions: [] }]
  // 上报节点的「配置数据」默认就是当前 DB，用户再按需缩小
  if (type === 'Upload') bindBlankTemplateUploads([n], task.value?.groupRef || '')
  flow.value.nodes.push(n)
  // 用加入流程后的那个节点(响应式)，生成的字段才会立刻显示在面板上
  if (type === 'Upload') void fillMappingFromRanges([flow.value.nodes[flow.value.nodes.length - 1]])
  selIds.value = new Set([n.id]); markDirty('添加节点')
}
function autoLayout(operation = '自动排布') {
  const ns = flow.value.nodes, es = flow.value.edges
  const depth: any = {}; const roots = ns.filter((n: any) => n.type === 'Trigger')
  const q = roots.map((r: any) => [r.id, 0])
  // 按广度优先取每个节点第一次被走到的层级。流程允许连线绕回前面的节点(环)，
  // 以前按“更深就刷新”会在环上无限加深，整个页面卡死只能刷新。
  const outgoing = new Map<string, string[]>()
  for (const e of es) { const list = outgoing.get(e.from); if (list) list.push(e.to); else outgoing.set(e.from, [e.to]) }
  for (const [id] of q) depth[id] = 0
  for (let head = 0; head < q.length; head++) {
    const [id, d] = q[head]
    for (const to of outgoing.get(id) || []) if (depth[to] === undefined) { depth[to] = d + 1; q.push([to, d + 1]) }
  }
  const byDepth: any = {}
  for (const n of ns) { const d = depth[n.id] ?? 0; (byDepth[d] ||= []).push(n) }
  for (const d of Object.keys(byDepth)) byDepth[d].forEach((n: any, i: number) => { n.x = 50 + +d * (NodeW + 90); n.y = 40 + i * 120 })
  markDirty(operation)
}
// ---------- MES 流程模板(印章语义:存为/套用/管理) ----------
const tplPanelOpen = ref(false)
const templateBusy = ref(false)
async function runTemplateOperation(work: () => Promise<void>) {
  if (templateBusy.value) return
  const generation = projectLoadGeneration
  templateBusy.value = true
  try { await allowFlowPaint(); if (generation === projectLoadGeneration) await work() }
  catch (reason: any) { ElMessage.error(reason?.response?.data?.error || reason?.message || t('模板操作失败')) }
  finally { templateBusy.value = false }
}
const templateTargets = shallowRef<any>(null)
async function refreshTemplateTargets() {
  templateTargets.value = await api.flowTemplateTargets()
}
// 画布有未保存修改时,存模板/套用前必须先「保存并应用」(提取与套用都作用于服务端工程)
async function ensureSaved(): Promise<boolean> {
  if (!dirty.value) return true
  const ok = await autoSave.flush()
  if (!ok) ElMessage.error(t('自动保存失败，暂不能继续：') + (saveError.value || t('请稍后重试')))
  return ok && !dirty.value
}
async function saveAsFlowTemplate() {
  return runTemplateOperation(async () => {
  if (!task.value) return
  const owner = task.value
  if (!(await ensureSaved())) return
  if (task.value !== owner) return
  let name = ''
  try { name = ((await ElMessageBox.prompt(t('模板名(工程内唯一):'), t('当前任务存为流程模板'), { inputValue: task.value.name || '' })).value || '').trim() } catch { return }
  if (!name) return
  if (task.value !== owner) return
  try {
    await api.flowTemplateExtract(owner.id || owner.name, name)
    ElMessage.success(t('已存为流程模板「{name}」——在 模板 ▾ → 流程模板 里套用到其他组', { name }))
    if (task.value !== owner) return
    const keep = owner.name; await load(keep)
    const gi = tasks.value.findIndex((t: any) => t.name === keep); if (gi >= 0) pickTask(gi)   // 服务端工程已变:刷新 rev 防后续保存 409
  } catch (e: any) { ElMessage.error(t('保存模板失败:') + (e?.response?.data?.error || e.message)) }
  })
}
async function openTplPanel() {
  return runTemplateOperation(async () => {
  const owner = task.value
  if (!(await ensureSaved())) return
  if (task.value !== owner) return
  try {
    await refreshTemplateTargets()
    if (task.value !== owner) return
    tplPanelOpen.value = true
  } catch (reason: any) { ElMessage.error(reason?.message || t('载入采集流程失败')) }
  })
}
async function onTplApplied(taskName: string) {
  return runTemplateOperation(async () => {
  await load(taskName)
  const owner = task.value
  if (tplPanelOpen.value) await refreshTemplateTargets()
  if (task.value !== owner) return
  const gi = tasks.value.findIndex((t: any) => t.name === taskName)
  if (gi >= 0) {
    pickTask(gi)   // 套用完直接落到新任务上 review
    if (bindBlankTemplateUploads(flow.value?.nodes || [], task.value?.groupRef || '')) { markDirty(); void fillMappingFromRanges(flow.value?.nodes || []) }
  }
  })
}
async function onTplRefreshed() {
  return runTemplateOperation(async () => {
  const keep = task.value?.name; await load(keep)
  const owner = task.value
  if (tplPanelOpen.value) await refreshTemplateTargets()
  if (task.value !== owner) return
  const gi = tasks.value.findIndex((t: any) => t.name === keep); if (gi >= 0) pickTask(gi)
  })
}

async function applyTemplate(kind: string) {
  if (kind === '_manage') { openTplPanel(); return }
  if (!editable.value) return
  if (kind === '_save') { saveAsFlowTemplate(); return }
  if (templateBusy.value) return
  templateBusy.value = true
  const owner = task.value, version = editVersion.value
  try {
  const labels: Record<string, string> = {
    blank: '从空白自定义流程',
    handshake: '标准握手上报',
    timer: '定时上报',
    writeback: '上报+回写',
  }
  const label = t(labels[kind] || kind)
  const replacing = (flow.value?.nodes?.length || 0) > 0
  try {
    await ElMessageBox.confirm(
      replacing ? t('套用「{label}」将替换当前全部节点和连线，继续？', { label }) : t('确认套用「{label}」？', { label }),
      t('套用流程模板'),
      {
        type: replacing ? 'warning' : 'info',
        confirmButtonText: t('确认套用'),
        cancelButtonText: t('取消'),
      },
    )
  } catch { return }
  await allowFlowPaint()
  if (task.value !== owner || editVersion.value !== version) return
  const mk = (type: string, title: string) => { newNode(type); const n = flow.value.nodes[flow.value.nodes.length - 1]; n.title = title; return n }
  flow.value.nodes = []; flow.value.edges = []; selIds.value = new Set()
  const trigger = mk('Trigger', t('采集周期'))
  if (kind === 'blank') { selIds.value = new Set([trigger.id]) }
  else if (kind === 'timer') { const u = mk('Upload', t('上报')); flow.value.edges.push({ from: trigger.id, fromPort: 'out', to: u.id }) }
  else {
    const c = mk('Condition', t('条件')); c.branches = [{ label: t('有请求'), conditions: [] }]
    const u = mk('Upload', t('上报'))
    flow.value.edges.push({ from: trigger.id, fromPort: 'out', to: c.id }, { from: c.id, fromPort: 'b0', to: u.id })
    if (kind === 'handshake') {
      const b = mk('Branch', t('判成败')); const w1 = mk('ResponseWriteback', t('成功回写')); const w2 = mk('ResponseWriteback', t('失败回写'))
      flow.value.edges.push({ from: u.id, fromPort: 'out', to: b.id }, { from: b.id, fromPort: 'success', to: w1.id }, { from: b.id, fromPort: 'fail', to: w2.id })
    } else { const w = mk('ResponseWriteback', t('回写')); flow.value.edges.push({ from: u.id, fromPort: 'out', to: w.id }) }
  }
  bindBlankTemplateUploads(flow.value.nodes, task.value?.groupRef || '')
  void fillMappingFromRanges(flow.value.nodes)
  autoLayout('套用流程模板')
  } finally { templateBusy.value = false }
}

function conditionArrayOption(condition: any): any | null {
  if (condition?.source && condition.source !== 'Tag') return null
  const segments = String(condition?.tagId || '').split('.').filter(Boolean)
  if (segments.length < 4) return null
  const channel = (proj.value?.channels || []).find((item: any) => item.name === segments[0])
  const device = (channel?.devices || []).find((item: any) => item.name === segments[1])
  let groups = device?.groups || []
  const groupName = segments[segments.length - 2]
  const tagName = segments[segments.length - 1]
  for (const segment of segments.slice(2, -1)) {
    const group = groups.find((item: any) => item.name === segment)
    if (!group) return null
    groups = group.subGroups || []
    if (segment === groupName) {
      const tag = (group.tags || []).find((item: any) => item.name === tagName)
      if (!tag?.array) return null
      return { scope: 'Array', array: tag.array, arrayMembers: [] }
    }
  }
  return null
}
function onConditionTagChange(condition: any) {
  const option = conditionArrayOption(condition)
  if (option) {
    condition.arrayQuantifier ||= 'Any'
    condition.arrayStart = [...(option.array?.lowerBounds || option.array?.dimensions?.map(() => 0) || [])]
    condition.arrayLengths = [...(option.array?.dimensions || [])]
    condition.arrayMemberPath ||= ''
  } else {
    condition.arrayStart = []
    condition.arrayLengths = []
    condition.arrayMemberPath = ''
  }
  md()
}
function conditionDimensionMin(condition: any, dimension: number) {
  return Number(conditionArrayOption(condition)?.array?.lowerBounds?.[dimension] ?? 0)
}
function conditionDimensionMax(condition: any, dimension: number) {
  const option = conditionArrayOption(condition)
  return conditionDimensionMin(condition, dimension) + Number(option?.array?.dimensions?.[dimension] || 1) - 1
}
function conditionLengthMax(condition: any, dimension: number) {
  return conditionDimensionMax(condition, dimension)
    - Number(condition.arrayStart?.[dimension] ?? conditionDimensionMin(condition, dimension)) + 1
}

// ---------- 运行态高亮 ----------
const live = ref(true)
const liveStates = ref<Record<string, any>>({})
const traceEvent = ref<any>(null)
const traceFromId = ref('')
let traceQueue: any[] = []
let traceTimer: any
let traceClearTimer: any
let traceCursor = ''
let traceLastNode = ''

function resetLiveTrace() {
  traceQueue = []; traceCursor = ''; traceLastNode = ''
  traceEvent.value = null; traceFromId.value = ''
  clearTimeout(traceTimer); clearTimeout(traceClearTimer)
  traceTimer = null; traceClearTimer = null
}
const traceKey = (e: any) => `${e?.tsUtc || ''}|${e?.nodeId || ''}|${e?.state || ''}`
function playTrace() {
  if (traceTimer || !live.value || !traceQueue.length) return
  clearTimeout(traceClearTimer); traceClearTimer = null
  const e = traceQueue.shift()
  const previous = traceEvent.value?.nodeId || traceLastNode
  traceFromId.value = previous && previous !== e.nodeId ? previous : ''
  traceEvent.value = e
  traceLastNode = e.nodeId || traceLastNode
  // 高频采集一轮会连续产生多个事件；约 0.3s/步既能看清顺序，也不会越播越滞后。
  const hold = e.state === 'fail' ? 850 : 300
  traceTimer = setTimeout(() => {
    traceTimer = null
    if (traceQueue.length) playTrace()
    else traceClearTimer = setTimeout(() => {
      traceEvent.value = null; traceFromId.value = ''
    }, 1200)
  }, hold)
}
function syncTrace(events: any[]) {
  if (!events?.length) return
  const last = events[events.length - 1]
  if (!traceCursor) {
    traceCursor = traceKey(last)
    const occurred = Date.parse(last.tsUtc || '')
    if (Number.isFinite(occurred) && Date.now() - occurred < 5000) {
      traceQueue.push(last); playTrace()
    }
    return
  }
  const cursorAt = events.map(traceKey).lastIndexOf(traceCursor)
  const fresh = cursorAt >= 0 ? events.slice(cursorAt + 1) : events.slice(-1)
  traceCursor = traceKey(last)
  if (!fresh.length) return
  traceQueue.push(...fresh.slice(-24))
  if (traceQueue.length > 24) traceQueue = traceQueue.slice(-24)
  playTrace()
}
function startLive() { syncLiveState() }
function stopLive() { resetLiveTrace() }
function selectedRuntimeTask(candidates: any[]) {
  if (!task.value) return null
  return findMesRuntimeTask(candidates, {
    id: String(task.value.id || ''),
    name: String(task.value.name || ''),
    groupRef: String(task.value.groupRef || ''),
  })
}
function restartTaskRealtime() {
  taskRealtime.stop()
  scopedTransitionEvents.value = []
  const taskId = String(task.value?.id || '')
  activeTaskStreamKey.value = `mes-flow:${taskId}`
  const projectRevision = realtimeProjectRevision.value
  if (!live.value || task.value?.enabled !== true || !taskId || !projectRevision) return
  taskRealtime.start({
    protocolVersion: 1,
    clientId: `mes-flow-${taskId}-${Date.now().toString(36)}`,
    projectRevision,
    tagIds: [],
    topics: mesFlowTopics(taskId),
    includeSnapshot: false,
  })
}
let fallbackPolling = false
async function pollLiveFallback() {
  if (fallbackPolling || !live.value || task.value?.enabled !== true) return
  if (taskRealtimeStatus.value === 'live' && selectedRuntimeTask([...realtimeMesTasks.value.values()])) return
  fallbackPolling = true
  try {
    const snapshot = await api.mes()
    fallbackMesTasks.value = Array.isArray(snapshot?.tasks) ? snapshot.tasks : []
  } catch { /* SignalR 正在重连时保留最后一次成功快照，避免运行颜色闪回空白。 */ }
  finally { fallbackPolling = false }
}
function syncLiveState() {
  if (!live.value || task.value?.enabled !== true) {
    if (Object.keys(liveStates.value).length) liveStates.value = {}
    resetLiveTrace()
    return
  }
  const taskIdentity = {
    id: String(task.value.id || ''),
    name: String(task.value.name || ''),
    groupRef: String(task.value.groupRef || ''),
  }
  const streamedTask = findMesRuntimeTask([...realtimeMesTasks.value.values()], taskIdentity)
  const runtimeTask = streamedTask || findMesRuntimeTask(fallbackMesTasks.value, taskIdentity)
  const map: any = {}
  const nodeTypes = new Map((flow.value?.nodes || []).map((node: any) => [node.id, node.type]))
  for (const n of runtimeTask?.nodes || []) map[n.id] = presentMesRuntimeNode(String(nodeTypes.get(n.id) || ''), n)
  liveStates.value = map
  const events = scopedTransitionEvents.value
    .filter((entry: any) => entry.kind === 'mes-flow-transition'
      && (entry.payload?.taskId === taskIdentity.id || entry.payload?.taskName === taskIdentity.name))
    .map((entry: any) => entry.payload?.value)
    .filter(Boolean)
  syncTrace(events)
}
watch([realtimeMesTasks, fallbackMesTasks, task, live], syncLiveState)
watch(taskRealtimeStatus, () => { syncLiveState(); void pollLiveFallback() })
watch(
  () => [task.value?.id, task.value?.enabled, realtimeProjectRevision.value, live.value],
  () => { restartTaskRealtime(); void pollLiveFallback() },
  { immediate: true },
)
watch(scopedTransitionEvents, syncLiveState)
// 严重度颜色(与 App.vue 全局 --sev-* token 同值;SVG 属性不支持 var(),这里镜像一份)
const SEV = { fail: '#C0392B', stuck: '#E67E22', slow: '#D9970D', ok: '#1B7F37', info: '#8A94A0' }
// 重试中(连续失败>0 且还没成功)= 仍在失败:节点红色不因重试窗口闪烁
const failing = (s: any) => !!s && (s.state === 'fail' || (s.failStreak > 0 && s.state !== 'ok' && s.state !== 'idle'))
function liveStroke(n: any): string | null {
  const s = liveStates.value[n.id]; if (!s) return null
  if (failing(s)) return SEV.fail
  if (s.stuck) return SEV.stuck                       // 引擎判定:超时(手工阈值优先,否则 P95×3)
  if (s.state === 'wait') return SEV.slow
  if (s.state === 'run') return SEV.ok
  if (s.state === 'ok') return s.ageMs < 3000 ? SEV.ok : null
  return null
}
function liveEdge(e: any): { stroke: string; flow: boolean; state: string } | null {
  const f = liveStates.value[e.from], t2 = liveStates.value[e.to]
  const tracing = traceFromId.value === e.from && traceEvent.value?.nodeId === e.to
  if (!f && !t2 && !tracing) return null
  const col = (s: any) => (!s ? null : failing(s) ? SEV.fail : s.stuck ? SEV.stuck : s.state === 'wait' ? SEV.slow : s.state === 'ok' || s.state === 'run' ? SEV.ok : null)
  const cf = col(f), ct = col(t2)
  const traceState = tracing ? traceEvent.value?.state : ''
  const stroke = tracing
    ? (traceState === 'fail' ? SEV.fail : traceState === 'wait' ? SEV.slow : SEV.ok)
    : (ct || cf || edgeColor(e))
  const flowing = tracing || (!!f && (f.state === 'run' || (f.state === 'ok' && f.ageMs < 3000)))
  return { stroke, flow: flowing, state: traceState || t2?.state || f?.state || 'idle' }
}
const liveInfo = (n: any) => liveStates.value[n.id]?.info || ''

type LiveVisual = { state: string; label: string; icon: string; color: string; fill: string; age: string; active: boolean }
const fmtRunTime = (ms: number) => {
  if (!ms || ms < 0) return ''
  if (ms < 1000) return `${Math.max(1, Math.round(ms))}ms`
  if (ms < 60000) return `${(ms / 1000).toFixed(ms < 10000 ? 1 : 0)}s`
  return `${Math.floor(ms / 60000)}m${Math.floor((ms % 60000) / 1000)}s`
}
function nodeVisual(n: any): LiveVisual | null {
  const snapshot = liveStates.value[n.id]
  const replay = traceEvent.value?.nodeId === n.id ? traceEvent.value : null
  if (!snapshot && !replay) return null
  const state = replay?.state || snapshot?.state || 'idle'
  const ageMs = replay ? Number(replay.durMs || 0) : Number(snapshot?.ageMs || 0)
  if (!replay && snapshot?.stuck) return { state: 'stuck', label: t('执行卡住'), icon: '!', color: SEV.stuck, fill: '#FFF8EE', age: fmtRunTime(ageMs), active: true }
  if ((!replay && failing(snapshot)) || state === 'fail') {
    const category = snapshot?.detail?.category
    const label = category === 'plc-communication' ? t('PLC 通信异常')
      : category === 'mes-no-response' ? t('MES 无响应')
        : category === 'business-ng' ? t('业务 NG') : t('执行失败')
    return { state: 'fail', label, icon: '×', color: SEV.fail, fill: '#FFF5F4', age: fmtRunTime(ageMs), active: true }
  }
  if (state === 'run') return { state: 'run', label: t(n.type === 'Trigger' ? '本轮处理中' : '正在执行'), icon: '▶', color: '#0C8A53', fill: '#F0FBF6', age: replay ? '' : fmtRunTime(ageMs), active: true }
  // 条件节点每个采集周期判断一次，时间是“距上次判断”，不是累计等了多久
  if (state === 'wait') return { state: 'wait', label: t(n.type === 'Trigger' ? triggerMode.value === 'InterfaceEvent' ? '等待后台接口消息' : '等待下一采集周期' : '等待条件'), icon: '◷', color: SEV.slow, fill: '#FFFAEF', age: n.type === 'Condition' && !replay && ageMs ? t('{time}前判断', { time: fmtRunTime(ageMs) }) : fmtRunTime(ageMs), active: !!replay }
  if (state === 'ok') return { state: 'ok', label: t(n.type === 'Trigger' ? '已触发' : (replay || ageMs < 5000 ? '本轮完成' : '上次成功')), icon: '✓', color: SEV.ok, fill: '#F6FBF7', age: replay && ageMs ? t('耗时 {time}', { time: fmtRunTime(ageMs) }) : (ageMs < 60000 ? t('{time}前', { time: fmtRunTime(ageMs) }) : ''), active: !!replay }
  return { state: 'idle', label: t('待命'), icon: '•', color: SEV.info, fill: '#FFFFFF', age: '', active: false }
}
const liveSummary = computed(() => {
  const r = { run: 0, wait: 0, ok: 0, bad: 0 }
  for (const s of Object.values(liveStates.value)) {
    if (failing(s) || s.stuck) r.bad++
    else if (s.state === 'run') r.run++
    else if (s.state === 'wait') r.wait++
    else if (s.state === 'ok') r.ok++
  }
  return r
})
const traceHeadline = computed(() => {
  const e = traceEvent.value
  if (e) {
    const n = nodeById(e.nodeId)
    return `${n?.title || typeMeta.value[n?.type]?.title || e.nodeId} · ${nodeVisual(n || { id: e.nodeId, type: '' })?.label || e.state}`
  }
  if (liveSummary.value.bad) return t('{count} 个节点异常', { count: liveSummary.value.bad })
  if (liveSummary.value.run) return t('{count} 个节点正在执行', { count: liveSummary.value.run })
  if (liveSummary.value.wait) return t('流程正在等待条件或下一周期')
  return t('等待下一轮执行')
})

// ---------- 节点就地标注:不用点开,框下直接小字「状态+时长 / 为什么」 ----------
// 触发等待(≥3s)、卡住、失败/重试中 才显示;正常节点不加字,画布保持干净。
// 注意语义:普通等待(在等活干)是产线常态、不是异常 → 灰色纯信息;只有 卡住(执行超时)/失败 才用警示色
function nodeBadge(n: any): { head: string; whys: string[]; color: string } | null {
  const s = liveStates.value[n.id]; if (!s) return null
  const isBad = failing(s) || s.stuck
  if (!isBad && !(s.state === 'wait' && s.ageMs >= 3000)) return null
  let head: string, color: string
  if (failing(s)) { head = (s.state === 'fail' ? t('✘ 失败') : t('✘ 重试中（已失败 {count} 次）', { count: s.failStreak })) + (s.ageMs ? ' ' + fmtAge(s.ageMs) : ''); color = SEV.fail }
  else if (s.stuck) { head = t('⚠ 卡住 {time}', { time: fmtAge(s.ageMs) }); color = SEV.stuck }
  else { head = t('⏳ 等待 {time}', { time: fmtAge(s.ageMs) }); color = SEV.info }
  return { head, whys: whysOf(s), color }
}
// 原因:等待=全部未命中条件(最多列 2 行,超出给计数;带分支标签);HTTP=状态码;回写=哪个点写失败
function whysOf(s: any): string[] {
  const d = s.detail
  if (d?.kind === 'wait') {
    const miss = (d.conds || []).filter((c: any) => !c.met)
    const fmt = (c: any) => {
      const pre = c.branch ? c.branch + ':' : ''
      if (c.op === '变化') return truncTxt(`${pre}${lastSeg(c.tagId)}=${c.actual}(等变化)`, 30)
      return truncTxt(`${pre}${lastSeg(c.tagId)}=${c.actual}(需${c.op}${c.expect})`, 30)
    }
    const lines = miss.slice(0, 2).map(fmt)
    if (miss.length > 2 && lines.length) lines[lines.length - 1] += ` …共${miss.length}项`
    if (lines.length) return lines
  }
  if (d?.kind === 'http') return [truncTxt(d.category === 'mes-no-response' ? t('MES 无响应') : d.category === 'business-ng' ? t('业务 NG') : `HTTP ${d.statusCode}`, 30)]
  if (d?.kind === 'write') {
    // 只列失败的点：第一行给总数，第二行列前两个点名；原因在右侧面板逐点展开
    const items = d.items || []
    const total = d.total || items.length
    if (items.length) return [
      truncTxt(t('回写失败 {count} 点', { count: total }), 30),
      truncTxt(items.slice(0, 2).map((it: any) => lastSeg(it.tagId)).join('、') + (total > 2 ? ' …' : ''), 30),
    ]
  }
  return s.info ? [truncTxt(s.info, 30)] : []
}
const lastSeg = (t: string) => { const i = (t || '').lastIndexOf('.'); return i >= 0 ? t.slice(i + 1) : t }
const truncTxt = (t: string, n: number) => (t.length > n ? t.slice(0, n) + '…' : t)
const fmtAge = (ms: number) => { const s = Math.floor(ms / 1000); return s < 60 ? s + 's' : s < 3600 ? Math.floor(s / 60) + 'm' + (s % 60) + 's' : Math.floor(s / 3600) + 'h' + Math.floor((s % 3600) / 60) + 'm' }

// ---------- 异常导航:徽章计数 + 循环跳转(类 IDE"下一个错误",不用肉眼扫图) ----------
const anomalyIds = computed<string[]>(() =>
  (flow.value?.nodes || [])
    .filter((n: any) => { const s = liveStates.value[n.id]; return s && (failing(s) || s.stuck) })
    .map((n: any) => n.id))
let cycleIdx = 0
function cycleAnomaly() {
  const ids = anomalyIds.value
  if (!ids.length) return
  cycleIdx = cycleIdx % ids.length
  focusNodeById(ids[cycleIdx])
  cycleIdx++
}
// 平移/缩放把节点居中 + 选中(选中即展开右侧归因面板)
function focusNodeById(id: string) {
  const n = nodeById(id)
  if (!n || !svgRef.value) return
  const r = svgRef.value.getBoundingClientRect()
  const z = Math.max(0.8, Math.min(1.2, zoom.value))   // 聚焦时把缩放拉回可读区间
  zoom.value = z
  panX.value = r.width / 2 - (n.x + NodeW / 2) * z
  panY.value = r.height / 2 - (n.y + nodeH(n) / 2) * z
  selIds.value = new Set([id])
}
// 选中节点的实时状态(有异常/归因时,右侧配置面板顶部展示归因)
const selLive = computed<any>(() => {
  if (!live.value || !selNode.value) return null
  const s = liveStates.value[selNode.value.id]
  return s && (failing(s) || s.stuck || s.detail) ? s : null
})

// ---------- 执行轨迹时间线 ----------
const timelineOpen = ref(false)
const triggerId = computed<string>(() => (flow.value?.nodes || []).find((n: any) => n.type === 'Trigger')?.id || '')

// ---------- 配置面板(选中单个节点) ----------
const selNode = computed(() => (selIds.value.size === 1 ? nodeById([...selIds.value][0]) : null))
const UploadFieldPageSize = 50
const UploadStructPageSize = 10
const uploadMappingSearch = ref('')
const uploadFieldPage = ref(1)
const uploadStructPage = ref(1)
type UploadEditorRow = { item: any; index: number }
const selectedUploadFields = computed<any[]>(() => selNode.value?.type === 'Upload' ? (selNode.value.payload?.fields || []) : [])
const selectedUploadStructs = computed<any[]>(() => selNode.value?.type === 'Upload' ? (selNode.value.payload?.structs || []) : [])
const uploadFieldMatches = computed<number[] | null>(() => {
  const query = uploadMappingSearch.value.trim().toLowerCase()
  if (!query) return null
  const result: number[] = []
  selectedUploadFields.value.forEach((item: any, index: number) => {
    if ([item?.tagId, item?.fieldName, item?.parameterCode].some((value) => String(value || '').toLowerCase().includes(query)))
      result.push(index)
  })
  return result
})
const uploadStructMatches = computed<number[] | null>(() => {
  const query = uploadMappingSearch.value.trim().toLowerCase()
  if (!query) return null
  const result: number[] = []
  selectedUploadStructs.value.forEach((item: any, index: number) => {
    if ([item?.baseGroup, item?.aliasName].some((value) => String(value || '').toLowerCase().includes(query))
      || (item?.members || []).some((member: any) => [member?.tagId, member?.fieldName].some(value => String(value || '').toLowerCase().includes(query)))) result.push(index)
  })
  return result
})
const uploadFieldCount = computed(() => uploadFieldMatches.value?.length ?? selectedUploadFields.value.length)
const uploadStructCount = computed(() => uploadStructMatches.value?.length ?? selectedUploadStructs.value.length)
const visibleUploadFieldRows = computed(() => {
  const start = (uploadFieldPage.value - 1) * UploadFieldPageSize
  const indices = uploadFieldMatches.value?.slice(start, start + UploadFieldPageSize)
    ?? Array.from({ length: Math.min(UploadFieldPageSize, Math.max(0, selectedUploadFields.value.length - start)) }, (_, offset) => start + offset)
  return indices.map((index): UploadEditorRow => ({ item: selectedUploadFields.value[index], index }))
})
const visibleUploadStructRows = computed(() => {
  const start = (uploadStructPage.value - 1) * UploadStructPageSize
  const indices = uploadStructMatches.value?.slice(start, start + UploadStructPageSize)
    ?? Array.from({ length: Math.min(UploadStructPageSize, Math.max(0, selectedUploadStructs.value.length - start)) }, (_, offset) => start + offset)
  return indices.map((index): UploadEditorRow => ({ item: selectedUploadStructs.value[index], index }))
})
watch(() => selNode.value?.id, () => {
  uploadMappingSearch.value = ''
  uploadFieldPage.value = 1
  uploadStructPage.value = 1
})
watch(uploadMappingSearch, () => {
  uploadFieldPage.value = 1
  uploadStructPage.value = 1
})
watch(uploadFieldCount, (count) => {
  uploadFieldPage.value = Math.min(uploadFieldPage.value, Math.max(1, Math.ceil(count / UploadFieldPageSize)))
})
watch(uploadStructCount, (count) => {
  uploadStructPage.value = Math.min(uploadStructPage.value, Math.max(1, Math.ceil(count / UploadStructPageSize)))
})

// 判成败/响应回写只能读取已经执行过的上游 Upload 响应。候选从反向连线中
// 自动汇总成功键与捕获字段；保留手工输入以兼容服务端动态返回的字段。
const upstreamResponseFields = computed<string[]>(() => {
  const selected = selNode.value
  if (!selected || !flow.value) return []
  const nodeMap = new Map((flow.value.nodes || []).map((node: any) => [node.id, node]))
  const incoming = new Map<string, string[]>()
  for (const edge of (flow.value.edges || [])) {
    const list = incoming.get(edge.to) || []
    list.push(edge.from)
    incoming.set(edge.to, list)
  }

  const fields = new Set<string>()
  const visited = new Set<string>()
  const pending = [...(incoming.get(selected.id) || [])]
  while (pending.length) {
    const id = pending.shift()!
    if (visited.has(id)) continue
    visited.add(id)
    const node: any = nodeMap.get(id)
    if (!node) continue
    if (node.type === 'Upload') {
      const successKey = (node.http?.successKey || '').trim()
      if (successKey) fields.add(successKey)
      // 接口返回格式：从上报节点保存的响应样例展开出全部字段，下游不用再逐字手填
      for (const field of parseResponseFields(node.http?.responseSample || '').fields) fields.add(field.path)
      for (const mapping of (node.http?.responseMappings || [])) {
        const targetPath = String(mapping.targetPath || '').trim()
        if (targetPath) fields.add(targetPath)
      }
      for (const capture of (node.captures || [])) {
        const responseField = (capture.responseField || '').trim()
        if (responseField) fields.add(responseField)
      }
    }
    pending.push(...(incoming.get(id) || []))
  }
  return [...fields]
})
// In=值命中列表任一(2,5,7——同一点不同工况等不同值);Changed=值发生变化即满足(锁存,不需要固定目标值)
const opDisp = [ { v: 'Eq', l: '=' }, { v: 'Ne', l: '≠' }, { v: 'Gt', l: '>' }, { v: 'Ge', l: '≥' }, { v: 'Lt', l: '<' }, { v: 'Le', l: '≤' }, { v: 'In', l: '∈任一' }, { v: 'Changed', l: '变化' }, { v: 'Regex', l: '正则' } ]
const sides = [ { v: 'Left', l: '左' }, { v: 'Right', l: '右' }, { v: 'Top', l: '上' }, { v: 'Bottom', l: '下' } ]
function md() { markDirty() }
/** 现场接口的典型返回：注意 httpStatusCode 是 0 而不是 200，成功判定要用 isSuccess。 */
const ResponseSampleTemplate = `{
  "httpStatusCode": 0,
  "isSuccess": true,
  "backResult": {
    "result": 1,
    "errorCode": 1,
    "errorMessage": "OK",
    "packCode": "PBCPTG00311100102G9H550015",
    "trayNumber": 40,
    "nextStation": 0,
    "ngStationCode": null
  },
  "errorMessage": "OK"
}`
/** 空框时直接把结构摆出来：流程没跑过就没有实际响应可读，用户得能一眼看出该填什么。 */
const ResponseSamplePlaceholder =
  '粘贴接口返回的 JSON，例如：\n'
  + '{"isSuccess": true,\n'
  + '  "backResult": { "result": 1, "errorCode": 1, "packCode": "PBCPTG0031…" },\n'
  + '  "errorMessage": "OK" }'

function fillResponseSampleTemplate(node: any) {
  node.http.responseSample = ResponseSampleTemplate
  responseSampleHint.value = ''
  md()
}

/** 这些是早期的报文/响应配置，日常用不到。只在本节点确实配过值时才显示，避免隐形生效。 */
function hasLegacyUploadConfig(node: any): boolean {
  return Boolean((node?.http?.bodyTemplate || '').trim())
    || Boolean((node?.http?.jsonTemplate || '').trim())
    || Boolean(node?.payload?.extraFields?.length)
    || Boolean(node?.http?.responseMappings?.length)
}


// ---------- 接口返回格式：响应字段来自真实响应，而不是逐字手填 ----------
const loadingResponseSample = ref(false)
const responseSampleHint = ref('')
/** 当前上报节点的响应样例解析出的字段，供本节点的「捕获变量」直接选。 */
const ownResponseFields = computed<string[]>(() =>
  parseResponseFields(selNode.value?.http?.responseSample || '').fields.map(field => field.path))
const responseSampleStatus = computed(() => {
  const sample = (selNode.value?.http?.responseSample || '').trim()
  if (!sample) return { text: t('还没填返回格式：流程没跑过就没有实际响应可读，可以先点「填入示例」照格式改，或直接粘贴接口文档里的返回 JSON'), error: false }
  const { fields, error } = parseResponseFields(sample)
  if (error) return { text: t('返回格式解析失败：{error}', { error }), error: true }
  return { text: t('已识别 {count} 个响应字段，下游节点可直接选择', { count: fields.length }), error: false }
})
/**
 * 从日志中心取本任务最近一次 MES 上报的响应报文。
 * 报文本来就保存着(category=interface、module=mes、feature=http_upload 带 payloadId)，
 * 这里只是读回来，不会重新调用 MES 接口——配置期误触发一次真实报工是不可接受的。
 */
async function loadLatestResponseSample(node: any) {
  loadingResponseSample.value = true
  responseSampleHint.value = ''
  try {
    const to = new Date()
    const from = new Date(to.getTime() - 7 * 24 * 60 * 60 * 1000)
    const result = await api.logCenterLogs({
      category: 'interface', module: 'mes', feature: 'http_upload',
      taskId: String(task.value?.id || ''),
      from: from.toISOString(), to: to.toISOString(), page: 1, pageSize: 50,
    })
    const withPayload = (result.items || []).filter(entry => entry.payloadId)
    const title = String(node.title || '').trim()
    // 优先取本节点的上报;同一流程里可能有多个上报节点,拿错节点的响应会把字段配歪
    const entry = withPayload.find(item => title && String(item.attributes?.nodeName || '') === title)
      ?? withPayload[0]
    if (!entry?.payloadId) {
      responseSampleHint.value = t('最近 7 天没有找到这个任务的上报记录，可先手动粘贴一段响应')
      return
    }
    const exchange = await api.logPayloadExchange(entry.payloadId)
    const body = extractResponseBody(String(exchange.response || ''))
    if (!body) {
      responseSampleHint.value = t('这次上报没有返回正文（超时或断线），换一条记录或手动粘贴')
      return
    }
    node.http.responseSample = body
    md()
    responseSampleHint.value = t('已读取 {time} 的实际响应', { time: new Date(entry.timestamp).toLocaleString() })
  } catch (error: any) {
    responseSampleHint.value = t('读取失败：{error}', { error: error?.message || String(error) })
  } finally {
    loadingResponseSample.value = false
  }
}

function ensureHttp(n: any) {
  n.http ||= {}
  if (!Number.isFinite(+n.http.timeoutSec) || +n.http.timeoutSec <= 0) n.http.timeoutSec = 30
  return true
}

const rangeDialogOpen = ref(false)
const editingRangeNode = ref<any>(null)
const editingRangeTask = shallowRef<any>(null)
type UploadDataMode = 'ranges' | 'mapping'
function uploadMode(node: any): UploadDataMode {
  const mode = String(node?.payload?.mode || '').toLowerCase()
  if (mode === 'dataranges') return 'ranges'
  if (mode === 'fieldmapping') return 'mapping'
  // 旧工程没有 mode：存在数据范围时按范围模式，否则按字段/结构映射模式。
  return node?.payload?.dataRanges?.length ? 'ranges' : 'mapping'
}
const isRangeMode = (node: any) => uploadMode(node) === 'ranges'
// 报文里小数的位数与取法(后端 MesNumberFormat)；无穷大/无穷小由网关固定改成 ±99999999
function payloadNumber(node: any) { return node?.payload?.number || { decimals: null, rounding: 'Round' } }
function setPayloadNumber(node: any, patch: { decimals?: number | null; rounding?: string }) {
  ensureUploadPayload(node)
  node.payload.number = { ...payloadNumber(node), ...patch }
  md()
}
function ensureUploadPayload(node: any) {
  node.payload ||= {}
  node.payload.dataRanges ||= []
  node.payload.fields ||= []
  node.payload.structs ||= []
  node.payload.append ||= {}
  node.payload.extraFields ||= []
  node.http ||= {}
  node.http.bodyTemplate ??= ''
  node.http.jsonTemplate ??= ''
  node.http.responseMappings ??= []
}
function uploadRangeSummary(node: any): string {
  if (isRangeMode(node)) {
    const ranges = node?.payload?.dataRanges || []
    if (!ranges.length) return '尚未配置上报数据'
    const devices = new Set(ranges.map((range: any) => String(range.sourcePath || '').split('.').slice(0, 2).join('.')))
    return `${devices.size} PLC · ${ranges.length} 个数据范围`
  }
  const fields = node?.payload?.fields?.length || 0
  const structs = node?.payload?.structs?.length || 0
  if (usesAutomaticPayload(node?.payload, node?.http) && hasParameterDictionary(node?.payload)) return t('原始结构及 parameterDic 字典 · {fields} 字段 · {structs} 结构体', { fields, structs })
  return fields || structs ? `字段/结构映射 · ${fields} 字段 · ${structs} 结构体` : '尚未配置上报数据'
}
async function switchUploadMode(node: any, target: UploadDataMode): Promise<boolean> {
  ensureUploadPayload(node)
  const nextMode = target === 'ranges' ? 'DataRanges' : 'FieldMapping'
  if (node.payload.mode !== nextMode) {
    node.payload.mode = nextMode
    md()
  }
  return true
}
async function openRangeDialog(node: any) {
  const owner = task.value
  // 以前按「数据范围」嵌套格式上报的节点：重新配置数据后改为字段/结构映射(平铺，和旧版一样)，报文格式会变，先确认
  if (isRangeMode(node) && (node.payload?.dataRanges?.length || 0) > 0) {
    try {
      await ElMessageBox.confirm(t('本节点现在按 设备 → DB 嵌套格式上报。重新配置数据后会改为字段/结构映射(平铺格式，和旧版一样)，需要和接口方确认。继续？'),
        t('改为字段/结构映射'), { type: 'warning' })
    } catch { return }
  }
  if (task.value !== owner || selNode.value !== node) return
  editingRangeNode.value = node
  editingRangeTask.value = owner
  rangeDialogOpen.value = true
}
// 配置数据选中的内容 → 字段/结构映射：普通点成字段，子组成对象，按下标命名的子组成数组；已改过的保留
const mappingGenerating = ref(false)
async function applyRanges(ranges: any[]) {
  const node = editingRangeNode.value, owner = editingRangeTask.value
  if (!node || owner !== task.value || node !== selNode.value) return
  ensureUploadPayload(node)
  const previous = isRangeMode(node) ? [] : node.payload.dataRanges || []
  mappingGenerating.value = true
  try {
    const generated = await api.mesFieldMapping({ scopeGroup: task.value?.groupRef || '', ranges })
    if (task.value !== owner) return
    if (isRangeMode(node)) { node.payload.fields = []; node.payload.structs = [] }
    mergeGeneratedMapping(node.payload, generated, previous, ranges)
    node.payload.dataRanges = ranges
    node.payload.mode = 'FieldMapping'
    md()
  } catch (error: any) {
    ElMessage.error(t('生成字段失败：') + (error?.response?.data?.error || error?.message || ''))
  } finally { mappingGenerating.value = false }
}
// 新建或套用模板后还没有字段的上报节点：按配置数据(默认当前 DB)自动生成字段/结构映射
async function fillMappingFromRanges(nodes: any[]) {
  const owner = task.value
  for (const node of nodes || []) {
    if (node?.type !== 'Upload' || isRangeMode(node)) continue
    const payload = node.payload || {}
    if (!(payload.dataRanges || []).length || (payload.fields || []).length || (payload.structs || []).length) continue
    try {
      const generated = await api.mesFieldMapping({ scopeGroup: owner?.groupRef || '', ranges: payload.dataRanges })
      if (task.value !== owner) return
      mergeGeneratedMapping(payload, generated, [], payload.dataRanges)
      md()
    } catch { /* 用户可以再点「配置数据」重新生成 */ }
  }
}

// 字段/结构映射模式的“本组全部点”必须沿用当前映射模式。
// 这里只遍历任务绑定的真实标记组，不扩大到设备或通道。
async function groupTagPaths(current: () => boolean = () => true): Promise<{ path: string; name: string }[]> {
  const groupPath = String(task.value?.groupRef || '').trim()
  if (!groupPath || !proj.value) return []
  if (pagedProjectMode.value) {
    const result: { path: string; name: string }[] = []
    const roots = [groupPath]
    const visited = new Set<string>()
    for (const root of roots) {
      if (visited.has(root)) continue
      visited.add(root)
      let cursor: string | undefined
      do {
        if (!current()) return []
        const page = await api.projectTagScope({ root, cursor, limit: 1000 })
        if (!current()) return []
        result.push(...page.definitions.map(tag => ({ path: tag.id, name: tag.name })))
        if (!cursor) roots.push(...page.groups.filter(group => group.kind === 'Group' && group.path.startsWith(groupPath + '.')).map(group => group.path))
        cursor = page.nextCursor || undefined
      } while (cursor)
    }
    return result
  }
  const segments = groupPath.split('.')
  const channel = (proj.value.channels || []).find((item: any) => item.name === segments[0])
  const device = (channel?.devices || []).find((item: any) => item.name === segments[1])
  if (!device) return []

  let groups = device.groups || []
  let selected: any = null
  for (let index = 2; index < segments.length; index++) {
    selected = groups.find((item: any) => item.name === segments[index])
    if (!selected) return []
    groups = selected.subGroups || []
  }

  const result: { path: string; name: string }[] = []
  const pending: { group: any; path: string }[] = selected ? [{ group: selected, path: groupPath }] : []
  while (pending.length) {
    const { group, path } = pending.pop()!
    for (const tag of group.tags || []) {
      result.push({ path: `${path}.${tag.name}`, name: tag.name })
      if (result.length % 512 === 0) { await allowFlowPaint(); if (!current()) return [] }
    }
    for (const child of [...(group.subGroups || [])].reverse()) pending.push({ group: child, path: `${path}.${child.name}` })
  }
  return result
}

async function fillCurrentMappingFromGroup(node: any, tags: { path: string; name: string }[], current: () => boolean): Promise<{ count: number; structs: number; members: number }> {
  if (!tags.length) return { count: 0, structs: 0, members: 0 }
  ensureUploadPayload(node)
  const previous = node.payload
  const draft = { payload: { ...previous, fields: [] as any[], structs: [] as any[] } }
  let processed = 0
  for (const tag of tags) {
    draft.payload.fields.push({ tagId: tag.path, fieldName: tag.name })
    if (++processed % 512 === 0) { await allowFlowPaint(); assertMappingOwner(current) }
  }
  const detected = await detectStructs(draft, true, current)
  assertMappingOwner(current)
  preserveAutoDetectedMappings(previous, draft.payload)
  assertMappingOwner(current)
  node.payload.fields = draft.payload.fields; node.payload.structs = draft.payload.structs
  return { count: tags.length, structs: detected.made, members: detected.mem }
}

const mappingWork = ref<{ kind: 'fill' | 'detect'; node: any; text: string } | null>(null)
const fillMappingDialog = shallowRef<{
  node: any; owner: any; version: number; generation: number; payload: any
  apply: (payload: any) => void; setOpen: (open: boolean) => void
} | null>(null)
let mappingGeneration = 0
async function allowFlowPaint() {
  await nextTick()
  await new Promise<void>(resolve => requestAnimationFrame(() => setTimeout(resolve, 0)))
}
function assertMappingOwner(current: () => boolean) { if (!current()) throw new Error(t('流程上下文已变化，本次操作已取消')) }
watch([task, selNode], () => {
  mappingGeneration++; mappingWork.value = null
  fillMappingDialog.value = null
  rangeDialogOpen.value = false; editingRangeNode.value = null; editingRangeTask.value = null
}, { flush: 'sync' })
onUnmounted(() => { mappingGeneration++; mappingWork.value = null; fillMappingDialog.value = null })
async function fillAllFields(n: any) {
  if (!editable.value || mappingWork.value || fillMappingDialog.value || selNode.value !== n) return
  const owner = task.value, version = editVersion.value, generation = ++mappingGeneration
  const current = () => generation === mappingGeneration && task.value === owner && selNode.value === n && editVersion.value === version
  mappingWork.value = { kind: 'fill', node: n, text: t('正在读取本组点位…') }
  try {
  await allowFlowPaint()
  if (!current()) return
  const tags = await groupTagPaths(current)
  if (!current()) return
  if (!tags.length) { ElMessage.info(t('没找到本组的点(检查任务的标记组绑定)')); return }
  mappingWork.value = { kind: 'fill', node: n, text: t('正在整理 {count} 个点位和结构体…', { count: tags.length }) }
  await allowFlowPaint()
  if (!current()) return
  const draft = { payload: cloneReactiveValue(n.payload || {}) }
  await fillCurrentMappingFromGroup(draft, tags, current)
  if (!current()) return
  fillMappingDialog.value = { node: n, owner, version, generation, payload: draft.payload,
    apply: payload => applyFillMapping(payload, generation),
    setOpen: open => { if (!open && fillMappingDialog.value?.generation === generation) fillMappingDialog.value = null },
  }
  } catch (reason: any) { if (generation === mappingGeneration) ElMessage.error(reason?.response?.data?.error || reason?.message || t('读取本组点位失败')) }
  finally { if (generation === mappingGeneration) mappingWork.value = null }
}
function applyFillMapping(payload: any, generation: number) {
  const draft = fillMappingDialog.value
  if (!editable.value || !draft || draft.generation !== generation) return
  if (mappingGeneration !== generation || task.value !== draft.owner || selNode.value !== draft.node || editVersion.value !== draft.version) {
    fillMappingDialog.value = null
    ElMessage.info(t('流程上下文已变化，本次操作已取消'))
    return
  }
  try {
    const candidate = cloneReactiveValue(payload)
    validateExtraFields(candidate, usesAutomaticPayload(candidate, draft.node.http))
    draft.node.payload.fields = candidate.fields || []
    draft.node.payload.structs = candidate.structs || []
    fillMappingDialog.value = null
    markDirty('设置本组上报字段和结构体')
    ElMessage.success(t('上报字段和结构体已应用，可继续在右侧修改'))
  } catch (error: any) { ElMessage.error(error?.message || t('上报字段配置无效')) }
}
function clearUploadMappings(n: any, part: 'fields' | 'structs') {
  if (!editable.value || selNode.value !== n || n?.type !== 'Upload' || mappingWork.value || fillMappingDialog.value || mappingGenerating.value) return
  ensureUploadPayload(n)
  if (!(n.payload[part]?.length)) return
  n.payload[part] = []
  markDirty(part === 'fields' ? '清除上报字段' : '清除上报结构体')
}
async function detectStructsWithFeedback(n: any) {
  if (!editable.value || mappingWork.value || fillMappingDialog.value || selNode.value !== n) return
  const owner = task.value, version = editVersion.value, generation = ++mappingGeneration
  const current = () => generation === mappingGeneration && task.value === owner && selNode.value === n && editVersion.value === version
  mappingWork.value = { kind: 'detect', node: n, text: t('正在识别结构体…') }
  try {
    await allowFlowPaint()
    if (generation !== mappingGeneration || task.value !== owner || selNode.value !== n || editVersion.value !== version) return
    await detectStructs(n, false, current)
  } catch (reason: any) { if (generation === mappingGeneration) ElMessage.error(reason?.message || t('结构体识别失败')) }
  finally { if (generation === mappingGeneration) mappingWork.value = null }
}
// 一键识别结构体:把平铺字段里「组内子组」的点(如 …CodeStruct.Code1..Code13)聚成结构体(默认值数组),
// 并从平铺里移出(引擎 IsCoveredByStruct 本就抑制,移出只为界面清爽)。用 groupRef 区分:直属点=顶层字段,不动。
async function detectStructs(n: any, silent = false, current: () => boolean = () => true): Promise<{ made: number; mem: number }> {
  const gr = task.value?.groupRef as string
  if (!gr) { if (!silent) ElMessage.info(t('任务未绑定标记组，无法识别')); return { made: 0, mem: 0 } }
  const byPrefix: Record<string, { alias: string; members: Map<string, any>; fields: any[]; elems: Set<string>; indices: Set<number> }> = {}
  const byPlain: Record<string, { member: string; field: any }[]> = {}
  const nestedArrays = new Set<string>()
  let processed = 0
  const checkpoint = async () => { await allowFlowPaint(); assertMappingOwner(current) }
  const asMember = (field: any, member: string) => ({ ...field, tagId: member, fieldName: field.fieldName === member ? '' : field.fieldName || '' })
  const contract = (field: any) => JSON.stringify([field.fieldName || field.tagId, Number(field.scale ?? 1), field.parameterCode || '', field.format || ''])
  for (const f of (n.payload.fields || [])) {
    if (++processed % 512 === 0) await checkpoint()
    const id = String(f.tagId || '')
    if (!id.startsWith(gr + '.')) continue
    const cut = id.lastIndexOf('.')
    const parent = id.slice(0, cut)                 // 叶子元素组路径(直接挂该成员点)
    const member = id.slice(cut + 1)
    if (parent === gr) continue                     // 直属点,不并入结构体
    const indexed = resolveIndexedElementPath(parent)
    // 内层仅一个元素、或外层没有直属点，也必须保留数组层级。
    let ancestor = parent.slice(0, parent.lastIndexOf('.'))
    while (ancestor.startsWith(gr + '.')) {
      const outer = resolveIndexedElementPath(ancestor)
      if (outer) { nestedArrays.add(outer.baseGroup); if (indexed) nestedArrays.add(indexed.baseGroup) }
      ancestor = ancestor.slice(0, ancestor.lastIndexOf('.'))
    }
    if (indexed) {                                  // 兼容 OCVStruct[1] 与 DB 导入后的 CellData_1
      const b = (byPrefix[indexed.baseGroup] ||= { alias: indexed.aliasName, members: new Map(), fields: [], elems: new Set(), indices: new Set() })
      b.elems.add(parent)
      b.indices.add(indexed.index)
      const mapped = asMember(f, member), existing = b.members.get(member)
      if (existing && contract(existing) !== contract(mapped)) throw new Error(`数组 ${indexed.baseGroup} 的 ${member} 在不同元素中配置不同，无法自动合并。`)
      if (!existing) b.members.set(member, mapped)
      b.fields.push(f)
    } else {                                        // 普通子组 → 值数组(List,原逻辑)
      ;(byPlain[parent] ||= []).push({ member, field: f })
    }
  }
  const structs = [...(n.payload.structs || [])]
  const existingBaseGroups = new Set<string>(structs.map((item: any) => String(item?.baseGroup || '').toLowerCase()))
  const fieldsToRemove = new Set<any>()
  let made = 0, mem = 0
  for (const base of Object.keys(byPrefix)) {       // 索引子组 → 枚举数组结构体(引擎运行时扫叶子组)
    if (++processed % 512 === 0) await checkpoint()
    const b = byPrefix[base]
    if ((b.elems.size < 2 && !nestedArrays.has(base)) || existingBaseGroups.has(base.toLowerCase())) continue
    const sortedIndices = [...b.indices].sort((left, right) => left - right)
    if (sortedIndices.some((value, index) => index > 0 && value !== sortedIndices[index - 1] + 1)) continue
    const members: any[] = []
    for (const member of b.members.values()) { members.push(member); if (++processed % 512 === 0) await checkpoint() }
    structs.push({ kind: 'Array', baseGroup: base, aliasName: b.alias, countMode: 'Enum', startIndex: sortedIndices[0], fixedCount: sortedIndices.length, members })
    existingBaseGroups.add(base.toLowerCase())
    for (const field of b.fields) { fieldsToRemove.add(field); if (++processed % 512 === 0) await checkpoint() }
    made++; mem += b.fields.length
  }
  for (const base of Object.keys(byPlain)) {        // 普通子组 → 值数组(List)
    if (++processed % 512 === 0) await checkpoint()
    const items = byPlain[base]
    if (items.length < 2 || existingBaseGroups.has(base.toLowerCase())) continue
    const members: any[] = []
    for (const item of items) { members.push(asMember(item.field, item.member)); fieldsToRemove.add(item.field); if (++processed % 512 === 0) await checkpoint() }
    structs.push({ kind: 'List', baseGroup: base, aliasName: base.slice(base.lastIndexOf('.') + 1) || base, countMode: 'Fixed', startIndex: 0, fixedCount: items.length, members })
    existingBaseGroups.add(base.toLowerCase())
    made++; mem += items.length
  }
  const fields: any[] = []
  for (const field of n.payload.fields || []) { if (!fieldsToRemove.has(field)) fields.push(field); if (++processed % 512 === 0) await checkpoint() }
  const nested = nestDetectedStructs(structs, gr)
  if (JSON.stringify(nested) !== JSON.stringify(structs)) made = Math.max(made, nested.length)
  assertMappingOwner(current)
  n.payload.fields = fields; n.payload.structs = nested
  if (made && !silent) md()
  if (!silent) {
    if (made) ElMessage.success(t('识别出 {structs} 个结构体（索引子组=枚举数组、其余=值数组；已从平铺移出 {members} 个成员）', { structs: made, members: mem }))
    else ElMessage.info(t('没发现可识别的子组结构（需子组下至少 2 个点）'))
  }
  return { made, mem }
}

// ---------- 校验流程(与桌面 ValidateFlow 同规则) ----------
function validateFlow() {
  const ns = flow.value?.nodes || [], es = flow.value?.edges || []
  const p: string[] = []
  try { validateTaskExtraFields(task.value ? [task.value] : []) } catch (error) { p.push(String(error)) }
  const nm = (n: any) => n.title || typeMeta.value[n.type]?.title || n.type
  if (!ns.some((n: any) => n.type === 'Trigger')) p.push(t('缺少「采集周期」入口节点。'))
  if (!ns.some((n: any) => ['Upload', 'Writeback', 'ResponseWriteback'].includes(n.type))) p.push(t('缺少执行动作节点（上报或回写）。'))
  if (triggerMode.value === 'Unsupported') p.push(t('触发方式「{mode}」本版本不支持，网关会停用此任务。', { mode: task.value?.trigger?.mode }))
  for (const n of ns) if (n.type === 'FixedData' && fixedValueErrors(n.fixedValues || []).some(Boolean)) p.push(t('「{name}」里有固定值填得不对(字段名为空、重复或值和类型不符)。', { name: nm(n) }))
  for (const n of ns) if (n.type === 'Upload' && fixedValueErrors(uploadFixedRows(n.payload)).some(Boolean)) p.push(t('「{name}」里有固定值填得不对(字段名为空、重复或值和类型不符)。', { name: nm(n) }))
  for (const n of ns) if (typeMeta.value[n.type]?.unsupported) p.push(t('节点「{name}」的类型「{type}」本版本不支持，网关会停用含此节点的任务，请删除该节点。', { name: nm(n), type: n.type }))
  const trig = ns.find((n: any) => n.type === 'Trigger')
  if (trig) {
    const reach = new Set<string>([trig.id]); const q = [trig.id]
    while (q.length) { const id = q.shift()!; for (const e of es) if (e.from === id && !reach.has(e.to)) { reach.add(e.to); q.push(e.to) } }
    for (const n of ns) if (n.type !== 'Trigger' && !reach.has(n.id)) p.push(t('节点「{name}」没有连到流程里（从触发走不到）。', { name: nm(n) }))
  }
  for (const n of ns) if (n.type === 'Upload' && !(n.payload?.fields?.length) && !(n.payload?.dataRanges?.length) && !(n.payload?.structs?.length) && !(n.payload?.extraFields?.length) && !Object.keys(n.payload?.append || {}).length && !n.http?.bodyTemplate && !n.http?.jsonTemplate)
    p.push(t('「{name}」没有上报内容（填字段或写报文模板）。', { name: nm(n) }))
  for (const n of ns) for (const port of outPorts(n)) {
    if (es.some((e: any) => e.from === n.id && e.fromPort === port)) continue
    if (n.type === 'Branch' && (port === 'success' || port === 'fail')) p.push(t('「{name}」的{result}出口没连线，该情况会静默结束。', { name: nm(n), result: t(port === 'success' ? '成功' : '失败') }))
    else if (n.type === 'Condition' && /^b\d+$/.test(port)) p.push(t('「{name}」的分支「{branch}」没连线。', { name: nm(n), branch: portLabel(n, port) }))
    else if (n.type === 'Switch' && port.startsWith('case')) p.push(t('「{name}」的值「{value}」出口没连线。', { name: nm(n), value: portLabel(n, port) }))
  }
  // 环检测(有环运行时每次触发每节点只跑一次,提示确认)
  const state: any = {}
  const dfs = (id: string): boolean => {
    if (state[id] === 1) return true
    if (state[id] === 2) return false
    state[id] = 1
    for (const e of es) if (e.from === id && dfs(e.to)) return true
    state[id] = 2; return false
  }
  if (ns.some((n: any) => dfs(n.id))) p.push(t('流程里存在环（连线绕回前面的节点）；运行时每个节点一次触发只跑一次，请确认是否有意。'))
  if (!p.length) ElMessage.success(t('校验通过：流程结构完整，保存后即生效。'))
  else ElMessageBox.alert('• ' + p.join('\n• '), t('校验发现 {count} 个问题', { count: p.length }), { type: 'warning', customStyle: { whiteSpace: 'pre-wrap' } as any })
}
</script>

<template>
  <div v-if="projectLoading" role="status" class="hint">{{ $t('正在加载采集流程…') }}</div>
  <div class="fwrap" v-if="proj">
    <!-- 左:任务列表 -->
    <div class="card side">
      <div class="task-sidebar-fixed">
        <div class="sidehead">
          <div>
            <span class="eyebrow">MES FLOW</span>
            <div class="ttl2">{{ $t('采集任务') }}</div>
          </div>
          <div class="sidehead-actions">
            <span class="taskcount">{{ taskCountText }}</span>
          </div>
        </div>
        <div v-if="taskEntries.length" class="task-filters">
          <div class="task-scope-filters" role="group" :aria-label="$t('任务范围')">
            <button v-for="option in taskScopeOptions" :key="option.key" type="button" class="task-scope-filter"
                    :class="{ active: taskScopeFilter === option.key }" :aria-pressed="taskScopeFilter === option.key"
                    @click="taskScopeFilter = option.key">{{ $t(option.label) }}</button>
          </div>
          <el-input v-model="taskSearch" class="task-search" clearable size="small" :placeholder="$t('搜索任务名称或数据块名称')">
            <template #prefix><el-icon><Search /></el-icon></template>
          </el-input>
          <div class="task-state-filters" role="group" :aria-label="$t('任务状态')">
            <button v-for="option in taskStateOptions" :key="option.key" type="button" class="task-state-filter"
                    :class="{ active: taskStateFilter === option.key }" :aria-pressed="taskStateFilter === option.key"
                    @click="taskStateFilter = option.key">{{ $t(option.label) }}</button>
          </div>
        </div>
      </div>
      <div class="task-list-scroll">
        <div v-if="mesCatalog.loading.value || taskLoadingKey" role="status" class="hint">{{ $t('正在加载任务列表或流程…') }}</div>
        <div v-for="entry in filteredTaskEntries" :key="entry.key" class="titem" :class="{ on: entry.taskIndex >= 0 && entry.taskIndex === taskIdx }" @click="pickTaskEntry(entry)">
          <span class="tstate" :class="{ on: entry.enabled }"></span>
          <span class="tcopy">
            <span class="nmline">
              <span class="nm">{{ entry.name }}</span>
              <span v-if="entry.description" class="taskdescription" :title="entry.description">{{ entry.description }}</span>
            </span>
            <small>{{ entry.groupRef || '未绑定数据块' }}<template v-if="entry.tagCount"> · {{ entry.tagCount }} 点</template></small>
          </span>
          <!-- 列表里每一行都是已保存的任务，启用状态直接取自任务目录：没点开过的也显示真实状态 -->
          <span class="tstatus" data-testid="task-status">{{ taskLoadingKey === entry.key ? $t('加载中…') : entry.enabled ? $t('运行') : $t('停用') }}</span>
        </div>
        <div v-if="taskEntries.length && !filteredTaskEntries.length" class="task-filter-empty">
          <span>{{ $t('没有匹配的采集任务') }}</span>
          <button type="button" @click="clearTaskFilters">{{ $t('清除筛选') }}</button>
        </div>
        <el-empty v-else-if="!taskEntries.length" :description="$t('没有已开启采集流程的任务')" :image-size="60" />
      </div>
    </div>

    <!-- 中:画布 -->
    <div class="card cv" v-if="task">
      <div class="cvhead">
        <div class="flowidentity">
          <span class="eyebrow">{{ $t('当前 PLC 数据块') }}</span>
          <strong>{{ task.name }}</strong>
          <small>{{ task.groupRef }}</small>
          <span v-if="taskDescription" class="flowdescription" :title="taskDescription">{{ taskDescription }}</span>
        </div>
        <el-tag :type="task.enabled ? 'success' : 'info'" effect="light" round>{{ task.enabled ? '运行中' : '尚未启用' }}</el-tag>
      </div>
      <div class="bar flowbar">
        <div class="actionset primaryset">
          <template v-if="editable">
          <el-button size="small" type="primary" :loading="saving" :disabled="saveBusy" @click="save(false)">{{ $t('保存并应用') }}</el-button>
          <el-button size="small" :disabled="saveBusy" @click="batchSaveOpen = true">{{ $t('流程批量管理') }}</el-button>
          <el-dropdown @command="newNode" trigger="click">
            <el-button size="small">{{ $t('＋ 添加节点') }}<el-icon><ArrowDown /></el-icon></el-button>
            <template #dropdown><el-dropdown-menu>
              <el-dropdown-item v-for="(m, k) in paletteMeta" :key="k" :command="k">{{ (m as any).icon }} {{ (m as any).title }}</el-dropdown-item>
            </el-dropdown-menu></template>
          </el-dropdown>
          </template>
          <el-dropdown v-if="design" @command="applyTemplate" trigger="click" :teleported="true">
            <el-button size="small" :loading="templateBusy" :disabled="templateBusy">{{ $t('流程模板') }}<el-icon><ArrowDown /></el-icon></el-button>
            <template #dropdown><el-dropdown-menu>
              <template v-if="editable">
              <el-dropdown-item command="blank">{{ $t('从空白自定义流程') }}</el-dropdown-item>
              <el-dropdown-item command="handshake">{{ $t('标准握手上报(条件→上报→判成败→回写)') }}</el-dropdown-item>
              <el-dropdown-item command="timer">{{ $t('定时上报(无握手)') }}</el-dropdown-item>
              <el-dropdown-item command="writeback">{{ $t('上报+回写(不判成败)') }}</el-dropdown-item>
              <el-dropdown-item divided command="_save">{{ $t('💾 当前任务存为流程模板…') }}</el-dropdown-item>
              </template>
              <el-dropdown-item command="_manage">{{ $t('📦 流程模板(套用到组 / 批量 / 管理)…') }}</el-dropdown-item>
            </el-dropdown-menu></template>
          </el-dropdown>
          <el-button v-if="editable" size="small" :loading="canvasBusy === $t('正在自动排布…')" :disabled="!!canvasBusy" @click="runCanvasWork('正在自动排布…', () => autoLayout())">{{ $t('自动排布') }}</el-button>
          <el-button size="small" :loading="canvasBusy === $t('正在校验流程…')" :disabled="!!canvasBusy" @click="runCanvasWork('正在校验流程…', validateFlow)">{{ $t('校验') }}</el-button>
          <el-popover v-if="editable" v-model:visible="historyOpen" placement="bottom-start" :width="340" trigger="click">
            <template #reference>
              <el-button size="small" data-test="flow-history">{{ $t('操作历史') }}<span v-if="flowHistory.entries.value.length">（{{ flowHistory.entries.value.length }}）</span></el-button>
            </template>
            <div class="flow-history">
              <div class="flow-history-title">{{ $t('最近 5 次操作（仅本次打开页面期间）') }}</div>
              <div v-if="flowHistory.oversized.value" class="flow-history-empty">{{ $t('流程内容过大，为节省内存不记录操作历史') }}</div>
              <div v-else-if="!flowHistory.entries.value.length" class="flow-history-empty">{{ $t('还没有操作记录') }}</div>
              <div v-for="entry in flowHistory.entries.value" :key="entry.id" class="flow-history-row" data-test="flow-history-row">
                <span class="flow-history-time">{{ historyTime(entry.at) }}</span>
                <span class="flow-history-label" :title="$t(entry.label)">{{ $t(entry.label) }}</span>
                <el-button link type="primary" size="small" :disabled="!!canvasBusy" @click="restoreHistory(entry)">{{ $t('回退到此前') }}</el-button>
              </div>
            </div>
          </el-popover>
          <span v-if="canvasBusy || templateBusy" role="status" class="hint">{{ canvasBusy || $t('正在处理流程模板…') }}</span>
          <div class="task-run-control" :title="$t('与工程配置右键的“采集上报”开关联动')">
            <span>{{ $t('开启运行') }}</span>
            <el-switch
              :loading="taskToggleBusy"
              class="task-run-switch"
              :model-value="task.enabled === true"
              :disabled="saveBusy || !isMesFlowAvailable(task) || !editable"
              @change="toggleCurrentTaskEnabled"
            />
          </div>
          <div class="task-run-control" :title="$t('全局默认：上报点位是否只在条件满足时按需读取。单个任务可在触发节点里覆盖。')">
            <span>{{ $t('分层采集(全局)') }}</span>
            <el-switch
              class="task-tier-switch"
              data-test="mes-global-tier"
              :model-value="globalTieredAcquisition"
              :disabled="saveBusy || !editable"
              @change="globalTieredAcquisition = $event === true; md()"
            />
          </div>
        </div>
        <div class="actionset viewset">
          <el-button-group>
            <el-button size="small" :title="$t('缩小')" @click="zoomBy(-.1)">−</el-button>
            <el-button size="small" class="zoomvalue" :title="$t('复位视图')" @click="resetView">{{ zoomPct }}%</el-button>
            <el-button size="small" :title="$t('放大')" @click="zoomBy(.1)">＋</el-button>
          </el-button-group>
          <el-checkbox v-model="live" size="small">{{ $t('运行状态') }}</el-checkbox>
          <el-button size="small" :type="timelineOpen ? 'primary' : 'default'" plain @click="timelineOpen = !timelineOpen">{{ $t('时间线') }}</el-button>
          <template v-if="editable">
          <el-tag v-if="saving" type="info" effect="light" round>{{ $t('保存中…') }}</el-tag>
          <el-tag v-else-if="autoSaveBlocked" type="danger" effect="light" round :title="saveError">{{ $t('自动保存冲突') }}</el-tag>
          <el-tag v-else-if="dirty" :type="autoSaveState === 'error' ? 'danger' : 'warning'" effect="light" round
                  :title="saveError || '有改动时每 30 秒在后台静默保存一次，不阻塞编辑操作'">
            {{ autoSaveState === 'error' ? '自动保存失败，等待重试' : '待后台保存（每 30 秒）' }}
          </el-tag>
          <el-tag v-else type="success" effect="plain" round :title="$t('有改动时每 30 秒在后台静默保存一次，不阻塞编辑操作')">
            {{ autoSavedText ? `已自动保存 ${autoSavedText}` : '自动保存已开启' }}
          </el-tag>
          </template>
        </div>
        <div v-if="editable" class="flowhint">{{ $t('拖动端口连线 · 拖动连线可改接 · Ctrl+点或框选多选 · Ctrl+C/V · Del 删除') }}</div>
      </div>
      <div class="paperwrap">
      <div v-if="live" class="livehud" pointer-events="none">
        <div class="livehudhead">
          <span class="livebeacon" :class="{ busy: !!traceEvent || liveSummary.run > 0 }"></span>
          <strong>{{ $t('实时流转') }}</strong>
          <span class="liveheadline">{{ traceHeadline }}</span>
        </div>
        <div class="livecounts">
          <span class="lrun"><i></i>执行 {{ liveSummary.run }}</span>
          <span class="lwait"><i></i>等待 {{ liveSummary.wait }}</span>
          <span class="lok"><i></i>完成 {{ liveSummary.ok }}</span>
          <span v-if="liveSummary.bad" class="lbad"><i></i>异常 {{ liveSummary.bad }}</span>
        </div>
      </div>
      <!-- 异常徽章:画布角落计数,点击在异常节点间循环聚焦(不用肉眼扫图找红点);无异常时隐藏 -->
      <div v-if="live && anomalyIds.length" class="abadge" :title="$t('点击循环跳到下一个异常节点')" @click="cycleAnomaly">
        ⚠ {{ anomalyIds.length }} 异常 · 下一个
      </div>
      <svg ref="svgRef" class="paper" @mousedown="onCanvasDown" @mousemove="onMove" @mouseup="onUp" @wheel="onWheel">
        <defs>
          <pattern id="mes-grid-small" width="20" height="20" patternUnits="userSpaceOnUse">
            <path d="M 20 0 L 0 0 0 20" fill="none" stroke="#dfe6ef" stroke-width=".7" />
          </pattern>
          <pattern id="mes-grid" width="100" height="100" patternUnits="userSpaceOnUse">
            <rect width="100" height="100" fill="url(#mes-grid-small)" />
            <path d="M 100 0 L 0 0 0 100" fill="none" stroke="#cbd6e4" stroke-width="1" />
          </pattern>
          <marker id="mes-arrow" markerWidth="8" markerHeight="8" refX="7" refY="4" orient="auto" markerUnits="strokeWidth"><path d="M0,0 L8,4 L0,8 Z" fill="#98A2AE" /></marker>
          <marker id="mes-arrow-ok" markerWidth="8" markerHeight="8" refX="7" refY="4" orient="auto" markerUnits="strokeWidth"><path d="M0,0 L8,4 L0,8 Z" fill="#1B7F37" /></marker>
          <marker id="mes-arrow-fail" markerWidth="8" markerHeight="8" refX="7" refY="4" orient="auto" markerUnits="strokeWidth"><path d="M0,0 L8,4 L0,8 Z" fill="#C0392B" /></marker>
          <filter id="mes-node-shadow" x="-20%" y="-30%" width="140%" height="170%"><feDropShadow dx="0" dy="3" stdDeviation="4" flood-color="#24364b" flood-opacity=".14" /></filter>
          <filter id="mes-node-selected" x="-25%" y="-35%" width="150%" height="180%"><feDropShadow dx="0" dy="4" stdDeviation="6" flood-color="#1565C0" flood-opacity=".28" /></filter>
        </defs>
        <g :transform="`translate(${panX},${panY}) scale(${zoom})`">
          <rect :x="-CanvasExtent" :y="-CanvasExtent" :width="CanvasExtent * 2" :height="CanvasExtent * 2"
                fill="url(#mes-grid)" pointer-events="none" />
          <!-- 连线 -->
          <g v-for="(e, ei) in flow.edges" :key="'e' + ei" class="edgegroup">
            <template v-if="nodeById(e.from) && nodeById(e.to)">
              <path v-if="live && liveEdge(e)?.flow"
                    :d="edgePath(nodeById(e.from), e.fromPort, nodeById(e.to))" fill="none"
                    :stroke="liveEdge(e)!.stroke" stroke-width="9" stroke-linecap="round"
                    opacity=".14" pointer-events="none" class="edgeglow" />
              <path :d="edgePath(nodeById(e.from), e.fromPort, nodeById(e.to))" fill="none"
                    :stroke="(live && liveEdge(e)) ? liveEdge(e)!.stroke : edgeColor(e)"
                    :stroke-width="live && liveEdge(e)?.flow ? 3.4 : 2.2" stroke-linecap="round" pointer-events="none"
                    :marker-end="edgeMarker(e)"
                    :opacity="connecting && connecting.reroute === e ? 0.15 : 1"
                    :class="{ edgeflow: live && liveEdge(e)?.flow }" />
              <circle v-if="live && liveEdge(e)?.flow" r="4.2" :fill="liveEdge(e)!.stroke"
                      stroke="#fff" stroke-width="1.8" pointer-events="none" class="flowdot">
                <animateMotion dur=".72s" repeatCount="indefinite"
                               :path="edgePath(nodeById(e.from), e.fromPort, nodeById(e.to))" />
              </circle>
              <!-- 抓线改接:粗透明命中带,按住线拖到别的节点松手=把这条线的目标改到新节点(拖到空白=还原) -->
              <path v-if="editable" :d="edgePath(nodeById(e.from), e.fromPort, nodeById(e.to))" fill="none" stroke="transparent"
                    stroke-width="16" class="ewire" @mousedown.stop="startReroute(e, $event)" />
              <!-- 两端可拖手柄移到「节点之上」的独立分组(见下方),否则源端会被输出口命中圈挡住抓不到 -->

              <g v-if="editable" class="edel" @mousedown.stop="delEdge(e)">
                <circle :cx="edgeMid(nodeById(e.from), e.fromPort, nodeById(e.to)).x"
                        :cy="edgeMid(nodeById(e.from), e.fromPort, nodeById(e.to)).y" r="8" fill="#B0B4BA" />
                <text :x="edgeMid(nodeById(e.from), e.fromPort, nodeById(e.to)).x"
                      :y="edgeMid(nodeById(e.from), e.fromPort, nodeById(e.to)).y + 3.5" text-anchor="middle" fill="#fff" font-size="10" pointer-events="none">✕</text>
                <circle :cx="edgeMid(nodeById(e.from), e.fromPort, nodeById(e.to)).x"
                        :cy="edgeMid(nodeById(e.from), e.fromPort, nodeById(e.to)).y" r="12" fill="transparent" />
              </g>
            </template>
          </g>
          <!-- 连线预览:目标模式 源锚点→鼠标;源模式 鼠标→目标锚点 -->
          <path v-if="connecting && connecting.mode !== 'source' && nodeById(connecting.id)" :d="`M ${outAnchor(nodeById(connecting.id), connecting.port).x} ${outAnchor(nodeById(connecting.id), connecting.port).y} L ${mouse.x} ${mouse.y}`"
                stroke="#1565C0" stroke-width="2" stroke-dasharray="5,4" fill="none" pointer-events="none" />
          <path v-if="connecting && connecting.mode === 'source' && nodeById(connecting.toId)" :d="`M ${mouse.x} ${mouse.y} L ${inAnchor(nodeById(connecting.toId)).x} ${inAnchor(nodeById(connecting.toId)).y}`"
                stroke="#1565C0" stroke-width="2" stroke-dasharray="5,4" fill="none" pointer-events="none" />
          <!-- 节点(texts/visual 都 pointer-events:none,只有 rect 与端口命中圈接收事件,防止文字挡住拖线) -->
          <g v-for="n in flow.nodes" :key="n.id" :transform="`translate(${n.x},${n.y})`"
             :class="['node', live && nodeVisual(n) ? 'node-' + nodeVisual(n)!.state : '']"
             @mousedown="onNodeDown(n, $event)" @mouseup="onNodeUp(n, $event)">
            <rect v-if="live && nodeVisual(n)?.active" x="-5" y="-5" :width="NodeW + 10" :height="nodeH(n) + 10" rx="15"
                  fill="none" :stroke="nodeVisual(n)!.color" stroke-width="2" class="nodehalo" pointer-events="none" />
            <rect :width="NodeW" :height="nodeH(n)" rx="10"
                  :fill="live && nodeVisual(n) ? nodeVisual(n)!.fill : '#fff'"
                  :filter="selIds.has(n.id) ? 'url(#mes-node-selected)' : 'url(#mes-node-shadow)'"
                  :stroke="(live && liveStroke(n)) || (selIds.has(n.id) ? typeMeta[n.type].color : '#D8DCE2')"
                  :stroke-width="selIds.has(n.id) || (live && liveStroke(n)) ? 2.5 : 1.2" />
            <rect :width="NodeW" height="46" rx="10" :fill="typeMeta[n.type].color" opacity=".07" pointer-events="none" />
            <rect :width="NodeW" height="6" rx="6" :fill="typeMeta[n.type].color" pointer-events="none" />
            <circle cx="25" cy="28" r="14" :fill="typeMeta[n.type].color" opacity=".14" pointer-events="none" />
            <text x="25" y="33" text-anchor="middle" :fill="typeMeta[n.type].color" font-size="14" font-weight="700" pointer-events="none">{{ typeMeta[n.type].icon }}</text>
            <text x="47" y="26" font-size="13" font-weight="700" fill="#1f2d3d" pointer-events="none">{{ (n.title || typeMeta[n.type].title).slice(0, 9) }}</text>
            <text x="47" y="41" font-size="10" fill="#7f8b99" pointer-events="none">{{ n.type === 'Upload' ? uploadRangeSummary(n).slice(0, 19) : n.type === 'FixedData' ? $t('{count} 个固定值', { count: n.fixedValues?.length || 0 }) : typeMeta[n.type].title }}</text>
            <line x1="10" :x2="NodeW - 10" :y1="nodeH(n) - 26" :y2="nodeH(n) - 26" stroke="#DDE5EC" stroke-width="1" pointer-events="none" />
            <g v-if="live && nodeVisual(n)" pointer-events="none" class="nodestatus">
              <circle cx="16" :cy="nodeH(n) - 13" r="5" :fill="nodeVisual(n)!.color"
                      :class="{ statedot: nodeVisual(n)!.state === 'run' }" />
              <text x="27" :y="nodeH(n) - 9" font-size="10.5" font-weight="700"
                    :fill="nodeVisual(n)!.color">{{ nodeVisual(n)!.icon }} {{ nodeVisual(n)!.label }}</text>
              <text :x="NodeW - 10" :y="nodeH(n) - 9" text-anchor="end" font-size="9.5"
                    fill="#718091">{{ nodeVisual(n)!.age }}</text>
              <rect v-if="nodeVisual(n)!.state === 'run'" x="7" :y="nodeH(n) - 4" :width="NodeW - 14" height="3"
                    rx="1.5" :fill="nodeVisual(n)!.color" class="nodeprogress" />
            </g>
            <g v-else-if="live" pointer-events="none" class="nodestatus idle">
              <circle cx="16" :cy="nodeH(n) - 13" r="4" fill="#A7B1BC" />
              <text x="27" :y="nodeH(n) - 9" font-size="10" fill="#8A96A3">{{ $t('等待运行数据') }}</text>
            </g>
            <g v-else pointer-events="none" class="nodestatus idle">
              <circle cx="16" :cy="nodeH(n) - 13" r="4" fill="#A7B1BC" />
              <text x="27" :y="nodeH(n) - 9" font-size="10" fill="#8A96A3">{{ $t('运行状态已关闭') }}</text>
            </g>
            <title>{{ liveInfo(n) }}</title>
            <!-- 就地标注:框下小字 —— 状态+时长 / 为什么(全部未命中条件≤2行+计数、错误码),不用点开。
                 普通等待=灰色信息;卡住=橙;失败=红 -->
            <g v-if="live && nodeBadge(n)" pointer-events="none">
              <text :x="NodeW / 2" :y="nodeH(n) + 15" text-anchor="middle" font-size="10.5" font-weight="700"
                    :fill="nodeBadge(n)!.color">{{ nodeBadge(n)!.head }}</text>
              <text v-for="(w, wi) in nodeBadge(n)!.whys" :key="wi" :x="NodeW / 2" :y="nodeH(n) + 28 + wi * 13"
                    text-anchor="middle" font-size="10" :fill="nodeBadge(n)!.color">{{ w }}</text>
            </g>
            <!-- 输入口(pointer-events none:让点击/松开落到节点上完成连线) -->
            <circle v-if="n.type !== 'Trigger'" :cx="inLocal(n).x" :cy="inLocal(n).y" r="6" fill="#98A2AE" stroke="#fff" stroke-width="2" pointer-events="none" />
            <!-- 输出口:可视圈(不接收事件)+ 标签(不接收)+ 更大的透明命中圈(接收,拖线起点) -->
            <g v-for="p in outPorts(n)" :key="p">
              <circle :cx="outLocal(n, p).x" :cy="outLocal(n, p).y" r="6.5" :fill="portFill(p, n)" stroke="#fff" stroke-width="2" pointer-events="none" />
              <text v-if="portLabel(n, p)" :x="outLabel(n, p).x" :y="outLabel(n, p).y" :text-anchor="outLabel(n, p).anchor"
                    :style="outLabel(n, p).vertical ? 'writing-mode:vertical-rl;text-orientation:upright' : ''"
                    font-size="10" :fill="portFill(p, n)" pointer-events="none">{{ portLabel(n, p) }}</text>
              <circle v-if="editable" :cx="outLocal(n, p).x" :cy="outLocal(n, p).y" r="11" fill="transparent" class="port"
                      @mousedown="startConnect(n, p, $event)" />
            </g>
            <!-- 右上角删除:鼠标移到节点上才出现,点一下删掉这个节点 -->
            <g v-if="editable" class="ndel" data-testid="node-delete" @mousedown.stop @mouseup.stop @click.stop="delNode(n)">
              <title>{{ $t('删除该节点') }}</title>
              <circle :cx="NodeW - 4" cy="4" r="12" fill="transparent" />
              <circle :cx="NodeW - 4" cy="4" r="8" fill="#B0B4BA" stroke="#fff" stroke-width="1.5" />
              <text :x="NodeW - 4" y="7.5" text-anchor="middle" fill="#fff" font-size="10" pointer-events="none">✕</text>
            </g>
          </g>
          <!-- 连线两端手柄:放在「节点之上」,保证两端(改起点/改终点)都能可靠抓取,不被输出口命中圈挡住 -->
          <g v-for="e in flow.edges" :key="'h' + e.from + e.fromPort + e.to">
            <template v-if="editable && nodeById(e.from) && nodeById(e.to)">
              <circle :cx="edgeEnds(e).src.x" :cy="edgeEnds(e).src.y" r="5" :fill="edgeColor(e)" stroke="#fff" stroke-width="1.5" class="ehandle" pointer-events="none" />
              <circle :cx="edgeEnds(e).src.x" :cy="edgeEnds(e).src.y" r="12" fill="transparent" class="ehit" @mousedown.stop="startRerouteSource(e, $event)"><title>{{ $t('拖动改这条线的起点(拖到别的节点)') }}</title></circle>
              <circle :cx="edgeEnds(e).tgt.x" :cy="edgeEnds(e).tgt.y" r="5" :fill="edgeColor(e)" stroke="#fff" stroke-width="1.5" class="ehandle" pointer-events="none" />
              <circle :cx="edgeEnds(e).tgt.x" :cy="edgeEnds(e).tgt.y" r="12" fill="transparent" class="ehit" @mousedown.stop="startRerouteTarget(e, $event)"><title>{{ $t('拖动改这条线的终点(拖到别的节点)') }}</title></circle>
            </template>
          </g>
          <!-- 框选矩形 -->
          <rect v-if="drag.marq" :x="Math.min(drag.marq.x1, drag.marq.x2)" :y="Math.min(drag.marq.y1, drag.marq.y2)"
                :width="Math.abs(drag.marq.x2 - drag.marq.x1)" :height="Math.abs(drag.marq.y2 - drag.marq.y1)"
                fill="rgba(21,101,192,.12)" stroke="#1565C0" stroke-dasharray="4,3" />
        </g>
      </svg>
      <div class="canvasstatus"><span>{{ task.groupRef }}</span><b>{{ zoomPct }}%</b></div>
      </div>
    </div>
    <div class="card cv" v-else><el-empty :description="$t('左侧选择一个采集任务')" /></div>

    <!-- 执行轨迹时间线(可开关):按轮次回放 触发→上报→回写,点行聚焦节点 -->
    <div class="card tlcard" v-if="timelineOpen && task">
      <MesTimeline :task-id="task.id" :task-name="task.name" :nodes="flow?.nodes || []" :trigger-id="triggerId"
                   :runtime-events="scopedTransitionEvents" @focus="focusNodeById" />
    </div>

    <!-- MES 流程模板面板:套用(单/批量)+ 报告 + 导出导入 -->
    <FlowTemplatePanel v-if="tplPanelOpen && templateTargets" :catalog="templateTargets" :editable="editable" @close="tplPanelOpen = false"
                       @applied="onTplApplied" @refreshed="onTplRefreshed" />

    <MesDataRangeDialog v-if="proj" v-model="rangeDialogOpen" :project="proj" :scope-group="task?.groupRef || ''"
                        :ranges="editingRangeNode?.payload?.dataRanges || []" @apply="applyRanges" />

    <!-- 选中节点后才显示配置面板；取消选择时释放画布空间。 -->
    <div v-if="selNode || selIds.size > 1" class="card cfg">
      <template v-if="selNode">
        <div class="cfghead">
          <span class="cfgicon" :style="{ color: typeMeta[selNode.type].color, background: typeMeta[selNode.type].color + '18' }">{{ typeMeta[selNode.type].icon }}</span>
          <span><small>{{ $t('节点配置') }}</small><strong>{{ typeMeta[selNode.type].title }}</strong></span>
        </div>
        <!-- 归因面板:选中的节点有异常(fail/卡住/带归因)时自动展示"为什么" -->
        <MesNodeDetail v-if="selLive" :status="selLive" />
        <!-- 只读账号：配置照常显示，输入框全部禁用，增删按钮隐藏 -->
        <el-form label-width="86px" label-position="left" size="small" :disabled="!editable">
          <el-form-item :label="$t('标题')"><el-input v-model="selNode.title" @input="md" /></el-form-item>
          <el-form-item :label="$t('连线口')">
            {{ $t('入') }} <el-select v-if="selNode.type !== 'Trigger'" v-model="selNode.inSide" style="width:64px;margin:0 6px" @change="md">
              <el-option v-for="s in sides" :key="s.v" :value="s.v" :label="s.l" /></el-select>
            {{ $t('出') }} <el-select v-model="selNode.outSide" style="width:64px;margin-left:6px" @change="md">
              <el-option v-for="s in sides" :key="s.v" :value="s.v" :label="s.l" /></el-select>
          </el-form-item>

          <template v-if="selNode.type === 'Trigger'">
            <MesInterfaceListenerEditor v-if="triggerMode === 'InterfaceEvent'" :key="task.id" :editable="editable" v-model="task.listener" :scope-group="task?.groupRef || ''" @change="md" />
            <div v-else-if="triggerMode === 'Unsupported'" class="unsupported-note" data-testid="unsupported-trigger-note">
              <strong>{{ $t('不支持的触发方式') }}：{{ task.trigger?.mode }}</strong>
              <span>{{ $t('本版本不支持该触发方式，网关会停用此任务。') }}</span>
            </div>
            <template v-else>
              <el-form-item :label="$t('采集周期ms')"><el-input-number v-model="collectIntervalMs" :min="50" :max="3600000" controls-position="right" @change="md" /></el-form-item>
              <div class="hint">{{ $t('采集周期只控制流程运行节拍；需要按 PLC 点位判断时，请连接独立的「条件」节点。') }}</div>
            </template>
            <el-form-item :label="$t('上报点位采集')">
              <select aria-label="MES tiered acquisition" v-model="tieredAcquisition" class="mes-tiered-mode" :disabled="!editable" @change="md">
                <option value="inherit">{{ $t('跟随全局') }}（{{ globalTieredAcquisition ? $t('分层') : $t('常驻') }}）</option>
                <option value="on">{{ $t('分层：条件满足后按需读取') }}</option>
                <option value="off">{{ $t('常驻：上报点位持续轮询') }}</option>
              </select>
            </el-form-item>
            <div class="hint">{{ $t('分层只让判定点按采集周期轮询，上报点位在条件满足时整批读一次，拿到的是触发时刻的实时值，PLC 负载最低。改成常驻后这些点一直采集，缓存随时可用（QC / 历史 / 北向转发），但 PLC 流量大很多。') }}</div>
          </template>

          <template v-if="selNode.type === 'Condition'">
            <div class="hint">{{ $t('多分支:每分支一组条件(全满足);从上到下第一个满足的走对应出口,否则走「默认」。') }}</div>
            <div class="hint">{{ $t('选择 PLC / 设备点位，设置比较条件和值。点位质量异常不会让“不等于”成立。') }}</div>
            <div v-for="(br, bi) in selNode.branches" :key="bi" class="brbox">
              <div class="brhead">
                <el-input v-model="br.label" size="small" :placeholder="$t('分支名(=出口标签)')" @input="md" />
                <el-button v-if="editable" size="small" text type="danger" @click="selNode.branches.splice(bi, 1); md()">{{ $t('删分支') }}</el-button>
              </div>
              <div v-for="(c, ci) in br.conditions" :key="ci" class="condition-row">
                <div class="crow">
                  <MesParameterPicker v-if="!c.source || c.source === 'Tag'" v-model="c.tagId" :scope-group="task?.groupRef || ''" kind="tag" access="read" :placeholder="$t('点位完整路径')" size="small" @change="onConditionTagChange(c)" />
                  <el-input v-else v-model="c.path" size="small" placeholder="data.status" :aria-label="$t('条件字段路径')" @input="md" />
                  <el-select v-model="c.op" size="small" style="width:78px" @change="md"><el-option v-for="o in opDisp" :key="o.v" :value="o.v" :label="o.l" /></el-select>
                  <el-input v-if="c.op !== 'Changed'" v-model="c.value" size="small" style="width:70px" :placeholder="c.op === 'In' ? '1,2,5' : ''" @input="md" />
                  <el-button v-if="editable" size="small" text type="danger" @click="br.conditions.splice(ci, 1); md()">{{ $t('删') }}</el-button>
                </div>
                <div v-if="conditionArrayOption(c)" class="array-condition-config">
                  <div class="array-condition-head">
                    <span>{{ $t('数组判断') }}</span>
                    <el-select v-model="c.arrayQuantifier" size="small" style="width:100px" @change="md">
                      <el-option value="Any" :label="$t('任一元素')" />
                      <el-option value="All" :label="$t('全部元素')" />
                    </el-select>
                    <el-select v-if="conditionArrayOption(c)?.arrayMembers?.length" v-model="c.arrayMemberPath"
                      clearable size="small" style="flex:1" :placeholder="$t('完整结构体元素')" @change="md">
                      <el-option v-for="member in conditionArrayOption(c)?.arrayMembers || []" :key="member.path"
                        :value="member.path" :label="`${member.label} · ${member.path}`" />
                    </el-select>
                  </div>
                  <div v-for="(_, dimension) in c.arrayStart" :key="dimension" class="array-condition-dimension">
                    <span>{{ $t('第 {dimension} 维', { dimension: dimension + 1 }) }}</span>
                    <el-input-number v-model="c.arrayStart[dimension]" size="small"
                      :min="conditionDimensionMin(c, dimension)" :max="conditionDimensionMax(c, dimension)" controls-position="right" @change="md" />
                    <span>{{ $t('数量') }}</span>
                    <el-input-number v-model="c.arrayLengths[dimension]" size="small" :min="1"
                      :max="conditionLengthMax(c, dimension)" controls-position="right" @change="md" />
                  </div>
                </div>
              </div>
              <el-button v-if="editable" size="small" text @click="br.conditions.push({ source: 'Tag', path: '', tagId: '', op: 'Eq', value: '1', arrayQuantifier: 'Any', arrayStart: [], arrayLengths: [], arrayMemberPath: '' }); md()">{{ $t('+ 条件') }}</el-button>
            </div>
            <el-button v-if="editable" size="small" @click="selNode.branches.push({ label: '条件' + (selNode.branches.length + 1), conditions: [] }); md()">{{ $t('+ 分支') }}</el-button>
          </template>

          <div v-if="typeMeta[selNode.type]?.unsupported" class="unsupported-note" data-testid="unsupported-node-note">
            <strong>{{ $t('不支持的节点') }}：{{ selNode.type }}</strong>
            <span>{{ $t('本版本不支持该节点类型，网关会停用含此节点的任务，请删除该节点。') }}</span>
          </div>

          <template v-if="selNode.type === 'Upload' && ensureHttp(selNode)">
            <el-form-item :label="$t('上报地址')"><el-input v-model="selNode.http.url" :placeholder="$t('留空用工程默认')" @input="md" /></el-form-item>
            <el-form-item :label="$t('等待超时(秒)')">
              <el-input-number v-model="selNode.http.timeoutSec" :min="1" :max="600" controls-position="right" style="width:100%" @change="md" />
            </el-form-item>
            <div class="hint">{{ $t('默认 30 秒；超时会取消本次连接，并从超时结束后进入下一采集周期。') }}</div>
            <div class="upload-data-box">
              <div class="upload-data-copy"><strong>{{ $t('上报数据') }}</strong><small>{{ uploadRangeSummary(selNode) }}</small></div>
              <el-button v-if="editable" size="small" type="primary" :disabled="!!mappingWork || mappingGenerating" @click="openRangeDialog(selNode)">{{ $t('配置数据') }}</el-button>
            </div>
            <div v-if="isRangeMode(selNode)" class="upload-mode legacy-mode" data-testid="range-format-note">
              <span>{{ $t('本节点按 设备 → DB 嵌套格式上报(以前的数据范围方式)，照常运行；点「配置数据」可改为字段/结构映射。') }}</span>
            </div>
            <div v-if="mappingGenerating" class="hint" role="status">{{ $t('正在按配置数据生成字段…') }}</div>
            <el-collapse class="upload-collapse">
              <el-collapse-item name="response" :title="`响应与成功判定 · ${selNode.http.successKey || 'httpStatusCode'} = ${selNode.http.successValue || '200'}`">
            <el-form-item :label="$t('成功键/值')">
              <el-input v-model="selNode.http.successKey" style="width:44%" @input="md" /> =
              <el-input v-model="selNode.http.successValue" style="width:38%" @input="md" />
            </el-form-item>
            <div class="hint">{{ $t('上报不拦路：不管判定成败都继续走后续节点，用「判成败」节点分流。网络失败和 HTTP 非 2xx 仍会拦住并等下轮重试。') }}</div>
            <div class="response-format">
              <div class="response-format-head">
                <strong>{{ $t('接口返回格式') }}</strong>
                <span v-if="editable" class="response-format-actions">
                  <el-button size="small" text @click="fillResponseSampleTemplate(selNode)">{{ $t('填入示例') }}</el-button>
                  <el-button size="small" text type="primary" :loading="loadingResponseSample"
                    @click="loadLatestResponseSample(selNode)">{{ $t('从最近一次实际响应读取') }}</el-button>
                </span>
              </div>
              <el-input v-model="selNode.http.responseSample" type="textarea" :rows="4" spellcheck="false"
                :placeholder="ResponseSamplePlaceholder"
                @input="md" />
              <div class="hint" :class="{ 'format-error': responseSampleStatus.error }">{{ responseSampleStatus.text }}</div>
              <div v-if="responseSampleHint" class="hint">{{ responseSampleHint }}</div>
              <div class="hint">{{ $t('只用于解析字段供下拉选择，不会发起请求；字段路径与运行时取值一致。') }}</div>
            </div>
              </el-collapse-item>
              <el-collapse-item name="payload" :title="`${$t('报文高级设置')} · ${$t('字段/结构映射')} · ${$t('固定值')} ${uploadFixedRows(selNode.payload).length}`">
            <!-- 字段/结构映射映射的就是「配置数据」里选中的内容：逐项改名、选择是否上报 -->
            <MesPayloadMappingEditor v-if="isRangeMode(selNode)" :key="selNode.id" :payload="selNode.payload" :scope-group="task?.groupRef || ''" @change="md" />
            <template v-if="!isRangeMode(selNode)">
            <!-- 搜索不算修改：单独包一层不禁用的表单，只读账号也能搜 -->
            <el-form :disabled="false" @submit.prevent><div class="mapping-filter">
              <el-input v-model="uploadMappingSearch" clearable size="small" :placeholder="$t('搜索上报字段、JSON 名称、结构路径或成员…')" />
              <span>{{ $t('共 {count} 个字段', { count: uploadFieldCount }) }} · {{ $t('共 {count} 个结构体', { count: uploadStructCount }) }}</span>
            </div></el-form>
            <div class="hint">{{ $t('自动生成报文时保留原始格式；填写参数码的点位进入 parameterDic，key 就是参数码。自定义模板沿用模板内容。') }}</div>
            <div v-for="{ item: f, index: fi } in visibleUploadFieldRows" :key="fi" class="crow">
              <MesParameterPicker v-model="f.tagId" :scope-group="task?.groupRef || ''" kind="tag" access="read" :placeholder="$t('点位路径')" size="small" @change="md" />
              <el-input v-model="f.fieldName" :aria-label="$t('JSON 字段名')" :placeholder="$t('字段名')" size="small" style="width:76px" @input="md" />
              <el-input v-model="f.parameterCode" :aria-label="$t('参数码（填写后转字典）')" :title="$t('填写后使用该参数码作为 parameterDic 的 key；留空保留原始字段。')" :placeholder="$t('参数码')" size="small" style="width:64px" @input="md" />
              <el-button v-if="editable" size="small" text type="danger" @click="selNode.payload.fields.splice(fi, 1); md()">{{ $t('删') }}</el-button>
            </div>
            <el-pagination v-if="uploadFieldCount > UploadFieldPageSize" v-model:current-page="uploadFieldPage" size="small" background layout="prev, pager, next" :page-size="UploadFieldPageSize" :total="uploadFieldCount" class="mapping-pagination" />
            <template v-if="editable">
            <el-button size="small" text @click="selNode.payload.fields.push({ tagId: '', fieldName: '' }); md()">{{ $t('+ 字段') }}</el-button>
            <el-button size="small" text type="primary" :loading="mappingWork?.kind === 'fill'" :disabled="!!mappingWork" @click="fillAllFields(selNode)">{{ $t('填入本组全部点') }}</el-button>
            <el-button size="small" text type="danger" data-testid="upload-clear-fields" :disabled="!selNode.payload.fields.length || !!mappingWork || !!fillMappingDialog"
              :title="$t('清空当前上报节点的全部普通字段')" @click="clearUploadMappings(selNode, 'fields')">{{ $t('一键清除字段') }}</el-button>
            </template>
            <span v-if="mappingWork" role="status" class="hint">{{ mappingWork.text }}</span>
            <div class="hint" style="margin-top:8px">{{ $t('结构体上报(数组/对象,如按单体/工位循环 OCV[1..N].成员):') }}</div>
            <!-- 结构体卡片可以一层层套子结构体，和 DB/UDT 里的结构一致 -->
            <MesStructCard v-for="{ item: st, index: si } in visibleUploadStructRows" :key="'st' + si" :struct="st" :editable="editable"
              :scope-group="task?.groupRef || ''" @change="md" @remove="selNode.payload.structs.splice(si, 1); md()" />
            <el-pagination v-if="uploadStructCount > UploadStructPageSize" v-model:current-page="uploadStructPage" size="small" background layout="prev, pager, next" :page-size="UploadStructPageSize" :total="uploadStructCount" class="mapping-pagination" />
            <template v-if="editable">
            <el-button size="small" text type="primary" @click="(selNode.payload.structs ||= []).push({ kind: 'List', baseGroup: '', aliasName: 'items', countMode: 'Fixed', startIndex: 0, fixedCount: 1, members: [] }); md()">{{ $t('+ 结构体') }}</el-button>
            <el-button size="small" text type="danger" data-testid="upload-clear-structs" :disabled="!selNode.payload.structs.length || !!mappingWork || !!fillMappingDialog"
              :title="$t('清空当前上报节点的全部结构体及其子结构体')" @click="clearUploadMappings(selNode, 'structs')">{{ $t('一键清除结构体') }}</el-button>
            <el-button size="small" text type="success" :loading="mappingWork?.kind === 'detect'" :disabled="!!mappingWork" @click="detectStructsWithFeedback(selNode)">{{ $t('一键识别结构体') }}</el-button>
            </template>
            </template>
            <div class="hint" style="margin-top:6px">{{ $t('固定值(如 WorkStationCode/工位号)：和点位字段放在 JSON 同一层，按类型输出：') }}</div>
            <MesFixedValuesEditor :editable="editable" :model-value="uploadFixedRows(selNode.payload)" @update:model-value="setUploadFixedRows(selNode.payload, $event)" @change="md" />
            <div class="hint" style="margin-top:8px">{{ $t('小数：无穷大写成 99999999、无穷小写成 -99999999，不是数字(NaN)写成 null；位数留空按原值上报。') }}</div>
            <div class="crow number-format-row" data-testid="number-format">
              <span class="lbl">{{ $t('小数位数') }}</span>
              <el-input-number :model-value="payloadNumber(selNode).decimals ?? undefined" :min="0" :max="10" :step="1" step-strictly
                :value-on-clear="null" :placeholder="$t('不限')" size="small" controls-position="right" style="width:96px"
                @update:model-value="setPayloadNumber(selNode, { decimals: $event ?? null })" />
              <span class="lbl">{{ $t('取法') }}</span>
              <el-select :model-value="payloadNumber(selNode).rounding || 'Round'" :disabled="payloadNumber(selNode).decimals == null"
                size="small" style="width:110px" @update:model-value="setPayloadNumber(selNode, { rounding: $event })">
                <el-option value="Round" :label="$t('四舍五入')" />
                <el-option value="Truncate" :label="$t('直接舍去')" />
                <el-option value="Ceiling" :label="$t('进一')" />
              </el-select>
            </div>
            <!-- 下面几项日常配置用不到：新建节点不显示，只有老工程里确实配过值的才露出来，
                 免得配置还在后端生效、界面上却看不见。 -->
            <div v-if="hasLegacyUploadConfig(selNode)" class="legacy-upload-config">
              <div class="hint legacy-title">{{ $t('旧配置（仅因为本节点已经配过才显示，清空后自动收起）') }}</div>
              <el-form-item v-if="!isRangeMode(selNode) && selNode.http.bodyTemplate" :label="$t('报文模板')" style="margin-top:6px"><el-input v-model="selNode.http.bodyTemplate" type="textarea" :rows="3" :placeholder="$t('留空=自动拼;{字段名}/{变量名} 占位')" @input="md" /></el-form-item>
              <MesJsonEditor v-if="selNode.http.jsonTemplate" v-model="selNode.http.jsonTemplate" :disabled="!editable" @change="md" />
              <MesExtraFieldsEditor v-if="selNode.payload.extraFields?.length" v-model="selNode.payload.extraFields" :editable="editable" :payload="selNode.payload" :scope-group="task?.groupRef || ''" @change="md" />
              <template v-if="selNode.http.responseMappings?.length">
                <div class="hint">{{ $t('响应映射生成内部字段，供判定、变量捕获和 PLC 回写使用；现在可以直接用「接口返回格式」里解析出的字段路径。') }}</div>
                <MesJsonMappingEditor v-model="selNode.http.responseMappings" :editable="editable" @change="md" />
              </template>
            </div>
              </el-collapse-item>
              <el-collapse-item name="captures" :title="`响应变量 · ${(selNode.captures || []).length} 个`">
            <div class="hint">{{ $t('捕获变量(响应字段 → 变量,供后续回写/模板 {变量名})；字段写完整路径，如 backResult.errorCode：') }}</div>
            <div v-for="(c, ci) in selNode.captures" :key="'cp' + ci" class="crow">
              <MesResponseFieldPicker v-model="c.responseField" :options="ownResponseFields" :placeholder="$t('响应字段')" size="small" @change="md" />
              <el-input v-model="c.varName" :placeholder="$t('变量名')" size="small" @input="md" />
              <el-button v-if="editable" size="small" text type="danger" @click="selNode.captures.splice(ci, 1); md()">{{ $t('删') }}</el-button>
            </div>
            <el-button v-if="editable" size="small" text @click="(selNode.captures ||= []).push({ responseField: '', varName: '' }); md()">{{ $t('+ 捕获') }}</el-button>
              </el-collapse-item>
            </el-collapse>
          </template>

          <template v-if="selNode.type === 'Branch'">
            <el-form-item :label="$t('满足条件')">
              <el-select v-model="selNode.judgeCombine" @change="md">
                <el-option value="All" :label="$t('全部满足(且)')" /><el-option value="Any" :label="$t('任一满足(或)')" />
              </el-select>
            </el-form-item>
            <div class="hint">{{ $t('留空=用上报的成功判定；字段候选自动来自上游上报节点，也可直接输入服务端动态字段:') }}</div>
            <div v-for="(c, ci) in selNode.judgeConds" :key="ci" class="crow">
              <MesResponseFieldPicker v-model="c.field" :options="upstreamResponseFields" :placeholder="$t('如 backResult.errorCode')" size="small" @change="md" />
              <el-select v-model="c.op" size="small" style="width:66px" @change="md"><el-option v-for="o in opDisp" :key="o.v" :value="o.v" :label="o.l" /></el-select>
              <el-input v-model="c.value" :placeholder="$t('期望值')" size="small" style="width:70px" @input="md" />
              <el-button v-if="editable" size="small" text type="danger" @click="selNode.judgeConds.splice(ci, 1); md()">{{ $t('删') }}</el-button>
            </div>
            <el-button v-if="editable" size="small" text @click="selNode.judgeConds.push({ field: '', op: 'Eq', value: '' }); md()">{{ $t('+ 条件') }}</el-button>
          </template>

          <template v-if="selNode.type === 'Switch'">
            <el-form-item :label="$t('判值点')"><MesParameterPicker v-model="selNode.switchTag" :scope-group="task?.groupRef || ''" kind="tag" access="read" :placeholder="$t('点位完整路径')" style="width:100%" @change="md" /></el-form-item>
            <div class="hint">{{ $t('分支值(每个值一个出口,不命中走默认):') }}</div>
            <div v-for="(c, ci) in selNode.cases" :key="ci" class="crow">
              <el-input v-model="selNode.cases[ci]" size="small" @input="md" />
              <el-button v-if="editable" size="small" text type="danger" @click="selNode.cases.splice(ci, 1); md()">{{ $t('删') }}</el-button>
            </div>
            <el-button v-if="editable" size="small" text @click="selNode.cases.push(''); md()">{{ $t('+ 值') }}</el-button>
          </template>

          <template v-if="selNode.type === 'Writeback' || selNode.type === 'ResponseWriteback'">
            <div v-if="selNode.type === 'ResponseWriteback'" class="direct-writeback-note">
              <strong>{{ $t('回写接口响应到 PLC') }}</strong>
              <div class="wb-sample">
                <div class="wb-col-head">{{ $t('接口返回长这样') }}</div>
                <div class="wb-col-head">{{ $t('响应字段就这么填') }}</div>
                <code>{</code><i></i>
                <code>&nbsp;&nbsp;"isSuccess": true,</code><code class="wb-path">isSuccess</code>
                <code>&nbsp;&nbsp;"backResult": {</code><i></i>
                <code>&nbsp;&nbsp;&nbsp;&nbsp;"errorCode": 1,</code><code class="wb-path">backResult.errorCode</code>
                <code>&nbsp;&nbsp;&nbsp;&nbsp;"packCode": "PB…"</code><code class="wb-path">backResult.packCode</code>
                <code>&nbsp;&nbsp;}</code><i></i>
                <code>}</code><i></i>
              </div>
              <span>{{ $t('不区分业务成败都会执行；要分 OK/NG 写不同的值，在上报和回写中间加一个「判成败」节点。') }}</span>
            </div>
            <div class="hint">{{ $t('自定义覆盖（可选）：响应字段 → PLC 点位。数组用下标，如 items[0].code。') }}</div>
            <div v-for="(w, wi) in selNode.writeBack.fromResponse" :key="'r' + wi" class="crow writeback-row response-write-row">
              <MesResponseFieldPicker v-model="w.responseField" :options="upstreamResponseFields" :placeholder="$t('如 backResult.errorCode')" size="small" @change="md" />
              <MesParameterPicker v-model="w.tagId" :scope-group="task?.groupRef || ''" kind="tag" access="write" :placeholder="$t('写到 PLC 点位(完整路径)')" size="small" @change="md" />
              <el-button v-if="editable" size="small" text type="danger" @click="selNode.writeBack.fromResponse.splice(wi, 1); md()">{{ $t('删') }}</el-button>
            </div>
            <el-button v-if="editable" size="small" text @click="selNode.writeBack.fromResponse.push({ responseField: '', tagId: '' }); md()">{{ $t('+ 响应回写') }}</el-button>
            <div class="hint" style="margin-top:6px">{{ $t('固定/复制写(固定值 或 从别的点复制值,如 清 MESFlag=0 / 复制 SN):') }}</div>
            <div v-for="(w, wi) in selNode.writeBack.manual" :key="'m' + wi" class="crow writeback-row manual-write-row">
              <MesParameterPicker v-model="w.tagId" :scope-group="task?.groupRef || ''" kind="tag" access="write" :placeholder="$t('写到 PLC 点位(完整路径)')" size="small" @change="md" />
              <el-input v-model="w.value" :placeholder="$t('固定值')" size="small" @input="md" />
              <MesParameterPicker v-model="w.copyFromTagId" :scope-group="task?.groupRef || ''" kind="tag" access="read" :placeholder="$t('或从其他 PLC 点位复制')" size="small" @change="md" />
              <el-button v-if="editable" size="small" text type="danger" @click="selNode.writeBack.manual.splice(wi, 1); md()">{{ $t('删') }}</el-button>
            </div>
            <el-button v-if="editable" size="small" text @click="selNode.writeBack.manual.push({ tagId: '', value: '' }); md()">{{ $t('+ 固定写') }}</el-button>
            <div class="wb-policy">
              <el-checkbox
                :model-value="selNode.writeBack.confirm !== false"
                @update:model-value="(v: any) => { selNode.writeBack.confirm = !!v; md() }"
              >{{ $t('写后确认') }}</el-checkbox>
              <span class="wb-policy-hint">{{ $t('写完回读 PLC 核对实际值，不一致就重写。关掉后只要协议返回成功就算写成功——快，但 PLC 里到底是什么值没人核对过。') }}</span>
              <template v-if="selNode.writeBack.confirm !== false">
                <div class="wb-policy-row">
                  <span>{{ $t('回读不一致时最多重写') }}</span>
                  <el-input-number
                    :model-value="selNode.writeBack.retries ?? 10"
                    :min="0" :max="50" size="small" controls-position="right"
                    @update:model-value="(v: any) => { selNode.writeBack.retries = Number(v ?? 0); md() }"
                  />
                  <span>{{ $t('次') }}</span>
                </div>
                <span class="wb-policy-hint">{{ $t('写满这么多次仍对不上就判失败并记录明细。填 0 表示写一次就算数，回读不一致直接判失败。') }}</span>
              </template>
              <span v-if="task && task.confirmRewrite === false" class="wb-policy-warn">{{ $t('当前任务已关闭「写后读回确认」总开关，本节点的写后确认不会生效。') }}</span>
            </div>
          </template>

          <template v-if="selNode.type === 'FixedData'">
            <div class="hint">{{ $t('不调接口，把下面的固定值拼成 JSON：后面接上报时，和 DB 的字段放在同一层一起发给接口(同名时以固定值为准，不放进结构体)；后面没有上报时，当作接口返回，接「判成败」「回写」照常用，回写把填了写回点位的值写回 PLC。') }}</div>
            <MesFixedValuesEditor v-model="selNode.fixedValues" write-back :editable="editable" :scope-group="task?.groupRef || ''" @change="md" />
          </template>
          <template v-if="selNode.type === 'Delay'">
            <el-form-item :label="$t('延时ms')"><el-input-number v-model="selNode.delayMs" :min="0" :max="600000" controls-position="right" @change="md" /></el-form-item>
          </template>
        </el-form>
        <el-button v-if="editable" size="small" type="danger" plain style="margin-top:8px" @click="delSelected">{{ $t('删除该节点') }}</el-button>
      </template>
      <template v-else-if="selIds.size > 1"><el-empty :description="`已选 ${selIds.size} 个节点(Ctrl+C 复制 / Del 删除 / 拖动整体移动)`" :image-size="70" /></template>
    </div>
    <MesUploadMappingDialog v-if="fillMappingDialog" :key="fillMappingDialog.generation" :model-value="true" :payload="fillMappingDialog.payload"
      :scope-group="fillMappingDialog.owner?.groupRef || ''" :editable="editable"
      @update:model-value="fillMappingDialog.setOpen" @apply="fillMappingDialog.apply" />
    <MesBatchSaveDialog v-if="editable" v-model="batchSaveOpen" :revision="rev" :tasks="tasks" :save-batch="saveAllTasks" />
  </div>
</template>

<style scoped>
.wb-policy { margin-top: 10px; padding: 9px 10px; border: 1px solid #e1e8f1; border-radius: 8px; background: rgba(255,255,255,.7); display: flex; flex-direction: column; gap: 5px; }
.wb-policy-row { display: flex; align-items: center; gap: 7px; font-size: 12px; color: #43566c; }
.wb-policy-hint { font-size: 11px; line-height: 1.55; color: #7b8da1; }
.wb-policy-warn { font-size: 11px; line-height: 1.55; color: #c45656; }
.mes-tiered-mode { width:100%; padding:6px 8px; border:1px solid var(--el-border-color); border-radius:4px; background:var(--el-bg-color); color:var(--el-text-color-primary); font:inherit; }
.fwrap { display: flex; gap: 10px; height: calc(100vh - 210px); min-height: 500px; color: #243447; }
.fwrap > .card { border-color: #dfe6ef; box-shadow: 0 8px 24px rgba(31, 45, 61, .06); }
.side { width: 280px; flex: 0 0 280px; display: flex; flex-direction: column; min-height: 0; overflow: hidden; padding: 12px; background: linear-gradient(180deg, #fbfcfe 0%, #f6f8fb 100%); }
.cv { flex: 1; display: flex; flex-direction: column; min-width: 0; padding: 0; overflow: hidden; }
.cfg { width: 520px; overflow: auto; padding: 12px 14px 18px; background: #fbfcfe; }
.task-sidebar-fixed { flex: 0 0 auto; }
.task-list-scroll { flex: 1 1 auto; min-height: 0; overflow-x: hidden; overflow-y: auto; overscroll-behavior: contain; scrollbar-gutter: stable; margin-right: -4px; padding-right: 4px; }
.sidehead { display: flex; align-items: center; justify-content: space-between; padding: 2px 4px 10px; border-bottom: 1px solid #e6ebf2; margin-bottom: 8px; }
.sidehead-actions { display: flex; flex-direction: column; align-items: flex-end; gap: 4px; }
.sidehead-actions :deep(.el-checkbox) { height: 18px; margin-right: 0; color: #53677d; font-size: 10px; }
.sidehead-actions :deep(.el-checkbox__label) { padding-left: 4px; font-size: 10px; }
.eyebrow { display: block; color: #7b8da1; font-size: 9px; font-weight: 800; letter-spacing: 1.3px; line-height: 1.3; }
.taskcount { min-width: 24px; height: 24px; padding: 0 6px; border-radius: 12px; background: #e8f1fd; color: #1769aa; font-size: 12px; font-weight: 700; display: inline-flex; align-items: center; justify-content: center; }
.task-filters { margin: 0 0 10px; padding: 9px; border: 1px solid #e1e8f1; border-radius: 10px; background: rgba(255, 255, 255, .86); box-shadow: 0 3px 12px rgba(39, 65, 94, .05); }
.task-search { width: 100%; }
.task-search :deep(.el-input__wrapper) { border-radius: 8px; box-shadow: 0 0 0 1px #d8e2ed inset; }
.task-search :deep(.el-input__wrapper.is-focus) { box-shadow: 0 0 0 1px #6da6e3 inset, 0 0 0 3px rgba(64, 158, 255, .10); }
.task-scope-filters { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 4px; margin-bottom: 8px; padding: 3px; border: 1px solid #dce6f0; border-radius: 8px; background: #f5f8fb; }
.task-scope-filter { min-width: 0; height: 27px; padding: 0 6px; border: 0; border-radius: 6px; background: transparent; color: #61758a; font-size: 11px; cursor: pointer; transition: color .15s, background .15s, box-shadow .15s; }
.task-scope-filter:hover { color: #1769aa; background: rgba(255, 255, 255, .8); }
.task-scope-filter.active { color: #fff; background: linear-gradient(135deg, #3c8fd8, #2976bd); box-shadow: 0 2px 6px rgba(41, 118, 189, .24); font-weight: 700; }
.task-state-filters { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 4px; margin-top: 8px; padding: 3px; border-radius: 8px; background: #eef3f8; }
.task-state-filter { min-width: 0; height: 25px; padding: 0 3px; border: 0; border-radius: 6px; background: transparent; color: #6f7f90; font-size: 10.5px; line-height: 25px; cursor: pointer; white-space: nowrap; transition: color .15s, background .15s, box-shadow .15s; }
.task-state-filter:hover { color: #2f6fae; background: rgba(255, 255, 255, .72); }
.task-state-filter.active { color: #1769aa; background: #fff; box-shadow: 0 1px 5px rgba(45, 82, 120, .14); font-weight: 700; }
.task-filter-empty { display: flex; flex-direction: column; align-items: center; gap: 8px; margin: 18px 4px; padding: 18px 10px; border: 1px dashed #d6e0eb; border-radius: 10px; color: #8a98a8; font-size: 11px; text-align: center; background: rgba(255, 255, 255, .55); }
.task-filter-empty button { padding: 3px 10px; border: 0; border-radius: 12px; background: #e8f1fd; color: #1769aa; font-size: 10.5px; cursor: pointer; }
.task-filter-empty button:hover { background: #d9eaff; }
.cvhead { display: flex; align-items: center; justify-content: space-between; gap: 12px; padding: 12px 16px 10px; border-bottom: 1px solid #e7ecf2; background: linear-gradient(135deg, #fff 0%, #f6f9fd 100%); }
.flowidentity { min-width: 0; flex: 1; display: grid; grid-template-columns: auto minmax(120px, auto) minmax(0, 1fr); column-gap: 10px; align-items: baseline; }
.flowidentity .eyebrow { grid-column: 1 / -1; margin-bottom: 2px; }
.flowidentity strong { font-size: 15px; color: #1f2d3d; }
.flowidentity small { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: #7e8b99; font-size: 11px; }
.flowdescription { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: #60758a; font-size: 11.5px; }
.bar { display: flex; align-items: center; gap: 6px; flex-wrap: wrap; }
.flowbar { padding: 9px 12px 7px; margin: 0; border-bottom: 1px solid #e3e9f1; background: #f9fbfd; }
.actionset { display: flex; align-items: center; gap: 6px; flex-wrap: wrap; }
.primaryset { padding-right: 10px; border-right: 1px solid #dfe5ed; }
.task-run-control { display: inline-flex; align-items: center; gap: 7px; height: 28px; padding: 0 9px; border: 1px solid #cfe0ef; border-radius: 7px; background: #f4f9ff; color: #36556f; font-size: 12px; font-weight: 600; white-space: nowrap; }
.viewset { margin-left: auto; }
.zoomvalue { min-width: 54px; font-variant-numeric: tabular-nums; color: #526579; }
.flowhint { flex: 1 0 100%; color: #8b98a7; font-size: 10.5px; line-height: 1.4; padding: 1px 2px 0; }
.paperwrap { flex: 1; position: relative; display: flex; min-height: 0; }
.paper { flex: 1; background: #f4f7fb; cursor: grab; user-select: none; text-rendering: geometricPrecision; }
.livehud { position: absolute; top: 10px; left: 12px; z-index: 5; min-width: 310px; padding: 8px 10px 7px; border: 1px solid rgba(205, 216, 227, .92); border-radius: 10px; background: rgba(255, 255, 255, .94); box-shadow: 0 5px 18px rgba(35, 52, 72, .10); backdrop-filter: blur(8px); pointer-events: none; }
.livehudhead { display: flex; align-items: center; gap: 7px; min-width: 0; }
.livehudhead strong { color: #27394c; font-size: 11.5px; white-space: nowrap; }
.liveheadline { min-width: 0; max-width: 290px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: #647589; font-size: 10.5px; }
.livebeacon { width: 8px; height: 8px; flex: none; border-radius: 50%; background: #8ea0b2; box-shadow: 0 0 0 3px rgba(142, 160, 178, .14); }
.livebeacon.busy { background: #0c8a53; box-shadow: 0 0 0 3px rgba(12, 138, 83, .14); animation: beacon 1.25s ease-out infinite; }
.livecounts { display: flex; align-items: center; gap: 13px; margin: 6px 0 0 15px; color: #738195; font-size: 9.5px; font-variant-numeric: tabular-nums; }
.livecounts span { display: inline-flex; align-items: center; gap: 4px; white-space: nowrap; }
.livecounts i { width: 6px; height: 6px; border-radius: 50%; background: currentColor; }
.livecounts .lrun { color: #0c8a53; }
.livecounts .lwait { color: #d9970d; }
.livecounts .lok { color: #4f8f61; }
.livecounts .lbad { color: #c0392b; font-weight: 700; }
.canvasstatus { position: absolute; right: 12px; bottom: 10px; z-index: 4; max-width: min(70%, 520px); display: flex; align-items: center; gap: 10px; padding: 5px 9px; border: 1px solid rgba(205, 215, 227, .9); border-radius: 7px; background: rgba(255, 255, 255, .88); box-shadow: 0 2px 8px rgba(31, 45, 61, .08); backdrop-filter: blur(6px); pointer-events: none; color: #788799; font-size: 10.5px; }
.canvasstatus span { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.canvasstatus b { color: #315a85; font-variant-numeric: tabular-nums; }
.abadge { position: absolute; top: 10px; right: 10px; z-index: 5; background: var(--sev-fail); color: #fff; font-size: 12px; font-weight: 700;
  padding: 4px 10px; border-radius: 14px; cursor: pointer; box-shadow: 0 2px 6px rgba(192,57,43,.35); user-select: none; }
.abadge:hover { filter: brightness(1.08); }
.tlcard { width: 300px; overflow: hidden; display: flex; flex-direction: column; }
.node { cursor: move; }
.node rect, .node circle, .node path { shape-rendering: geometricPrecision; }
.node text { text-rendering: geometricPrecision; }
.nodehalo { transform-box: fill-box; transform-origin: center; animation: nodehalo 1.35s ease-out infinite; }
.node-fail .nodehalo, .node-stuck .nodehalo { animation-duration: .9s; }
.nodestatus text { letter-spacing: .1px; }
.statedot { transform-box: fill-box; transform-origin: center; animation: statedot 1s ease-in-out infinite; }
.nodeprogress { transform-box: fill-box; transform-origin: left center; animation: nodeprogress 1.15s ease-in-out infinite; }
.port { cursor: crosshair; }
.ewire { cursor: grab; }
.ewire:active { cursor: grabbing; }
.ehit { cursor: grab; }
.ehit:active { cursor: grabbing; }
.edel { cursor: pointer; opacity: .12; transition: opacity .16s; }
.ndel { cursor: pointer; opacity: 0; pointer-events: none; transition: opacity .16s; }
.node:hover .ndel { opacity: .95; pointer-events: auto; }
.ndel:hover circle:nth-of-type(2) { fill: #C0392B; }
.edgegroup:hover .edel { opacity: .9; }
.edel:hover circle { fill: #C0392B; }
.titem { display: flex; align-items: center; gap: 8px; padding: 9px 8px; border: 1px solid transparent; border-radius: 8px; cursor: pointer; margin-bottom: 5px; transition: background .15s, border-color .15s, transform .15s; }
.titem:hover { background: #fff; border-color: #dde6f0; transform: translateX(1px); }
.titem.on { background: #fff; border-color: #a9c9ed; box-shadow: 0 3px 10px rgba(21, 101, 192, .09); }
.tstate { width: 8px; height: 8px; border-radius: 50%; background: #aab4bf; box-shadow: 0 0 0 3px rgba(170, 180, 191, .13); flex: none; }
.tstate.on { background: var(--sev-ok); box-shadow: 0 0 0 3px rgba(27, 127, 55, .12); }
.tcopy { min-width: 0; flex: 1; display: flex; flex-direction: column; gap: 2px; }
.nmline { min-width: 0; display: flex; align-items: baseline; gap: 5px; }
.titem .nm { min-width: 0; max-width: 55%; flex: 0 1 auto; font-size: 12.5px; font-weight: 650; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: #2d3f52; }
.taskdescription { min-width: 0; flex: 1; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: #6f8193; font-size: 10.5px; }
.tcopy small { color: #8a98a8; font-size: 9.5px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.tstatus { color: #8492a1; font-size: 10px; flex: none; }
.titem.on .tstatus { color: #3977b7; }
.ttl2 { font-weight: 700; margin-top: 1px; color: #243447; }
.cfghead { position: sticky; top: -12px; z-index: 4; display: flex; align-items: center; gap: 9px; margin: -2px -2px 12px; padding: 10px 2px 9px; border-bottom: 1px solid #e5eaf1; background: rgba(251, 252, 254, .95); backdrop-filter: blur(7px); }
.cfgicon { width: 30px; height: 30px; border-radius: 8px; display: inline-flex; align-items: center; justify-content: center; font-size: 16px; font-weight: 700; }
.cfghead > span:last-child { display: flex; flex-direction: column; }
.cfghead small { color: #8a98a8; font-size: 9.5px; }
.cfghead strong { color: #27384a; font-size: 13px; }
.upload-data-box { display:flex; align-items:center; justify-content:space-between; flex-wrap:wrap; gap:8px 10px; margin:9px 0 4px; padding:10px; border:1px solid #cfe0f3; border-radius:8px; background:#f3f8fe; }
.upload-data-copy { min-width:120px; flex:1; display:flex; flex-direction:column; gap:3px; }
.upload-data-box strong { color:#27415d; font-size:12px; }
.upload-data-box small { color:#667a91; font-size:11px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
.upload-mode { display:flex; align-items:center; justify-content:space-between; color:#8793a1; font-size:10.5px; margin-bottom:5px; }
.mapping-filter { display:flex; align-items:center; gap:8px; margin:2px 0 8px; }
.mapping-filter .el-input { flex:1; min-width:0; }
.mapping-filter span { flex:none; color:#7d8b9b; font-size:10px; white-space:nowrap; }
.mapping-pagination { justify-content:flex-end; margin:6px 0; }
.upload-collapse { margin-top:4px; border-top:1px solid #e1e7ee; }
.upload-collapse :deep(.el-collapse-item__header) { min-height:36px; height:auto; color:#40556b; font-size:12px; font-weight:650; line-height:1.3; padding:7px 0; }
.upload-collapse :deep(.el-collapse-item__content) { padding:4px 0 10px; }
.hint { color: #888; font-size: 12px; margin: 6px 0 4px; line-height: 1.5; }
/* 顶部对齐：点位选择器选中后会在输入框下方补一行完整路径，行高随之变大。
   居中对齐会把同行其它控件顶到它的半腰上，看起来就是控件错位、说明文字压到下一行。 */
.crow { display: flex; gap: 4px; margin-bottom: 4px; align-items: flex-start; }
.crow :deep(.el-input:first-child) { flex: 1; min-width: 0; }
.crow :deep(.el-button) { flex-shrink: 0; white-space: nowrap; padding: 4px 6px; }
.append-row { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1.4fr) auto; }
.append-row :deep(.el-input) { width: 100%; min-width: 0; }
.condition-row { margin-bottom:6px; }
.array-condition-config { margin:2px 0 7px; padding:7px; border:1px solid #dbe8f5; border-radius:6px; background:#f3f8fd; }
.array-condition-head,.array-condition-dimension { display:flex; align-items:center; gap:6px; margin-bottom:5px; color:#607080; font-size:11px; }
.array-condition-head > span,.array-condition-dimension > span:first-child { min-width:54px; }
.array-condition-dimension :deep(.el-input-number) { width:94px; }
.unsupported-note { display: flex; flex-direction: column; gap: 4px; margin: 2px 0 10px; padding: 9px 10px; border: 1px solid #f3d19e; border-radius: 8px; background: #fdf6ec; }
.unsupported-note strong { color: #b26a00; font-size: 12px; }
.unsupported-note span { color: #7a5a2b; font-size: 11px; line-height: 1.5; }
.direct-writeback-note { display: flex; flex-direction: column; gap: 3px; margin: 2px 0 10px; padding: 9px 10px; border: 1px solid #d9cff4; border-radius: 8px; background: #f7f4ff; }
.direct-writeback-note strong { color: #553497; font-size: 12px; }
.response-format { margin: 8px 0 2px; padding: 8px 9px; border: 1px solid #dbe5f2; border-radius: 7px; background: #f6f9fd; }
.response-format-head { display: flex; align-items: center; justify-content: space-between; gap: 8px; margin-bottom: 5px; }
.response-format-head strong { color: #2f5d8f; font-size: 12px; }
.response-format-actions { display: flex; align-items: center; gap: 2px; flex: none; }
.wb-sample { display: grid; grid-template-columns: auto 1fr; column-gap: 0; row-gap: 0; margin: 6px 0 8px; padding: 9px 11px; border: 1px solid #e3dcf6; border-radius: 6px; background: #fdfcff; font-size: 11px; line-height: 20px; }
.wb-sample > :nth-child(2n) { border-left: 1px solid #ded5f2; padding-left: 16px; }
.wb-sample > :nth-child(2n+1) { padding-right: 16px; }
.wb-sample .wb-col-head { color: #8d80b5; font-size: 10.5px; margin-bottom: 4px; }
.wb-sample code { font-family: Consolas, monospace; color: #5c6b7f; white-space: pre; }
.wb-sample .wb-path { color: #553497; font-weight: 600; }
.response-format .hint.format-error { color: #d05a45; }
.legacy-upload-config { margin-top: 10px; padding: 8px 9px; border: 1px dashed #d8dde5; border-radius: 7px; background: #fafbfc; }
.legacy-upload-config .legacy-title { margin: 0 0 4px; color: #97a1ad; }
.direct-writeback-note span { color: #6f6486; font-size: 11px; line-height: 1.5; }
.writeback-row { gap: 7px; margin-bottom: 7px; }
.response-write-row > :first-child { flex: 0 1 190px; min-width: 140px; }
.response-write-row > :nth-child(2) { flex: 1.45 1 250px; min-width: 190px; }
.manual-write-row > :first-child { flex: 1.25 1 190px; min-width: 145px; }
.manual-write-row > :nth-child(2) { flex: 0 1 96px; width: 96px; min-width: 78px; }
.manual-write-row > :nth-child(3) { flex: 1.25 1 190px; min-width: 145px; }
.structcard { border: 1px solid #e2e6eb; border-radius: 6px; padding: 8px; margin: 4px 0 6px; background: #fafbfc; }
.structhd { display: flex; gap: 6px; align-items: flex-start; margin-bottom: 6px; }
.structrow { display: flex; gap: 6px; align-items: flex-start; margin-bottom: 6px; }
.structrow .lbl { color: #666; font-size: 12px; white-space: nowrap; line-height: 24px; }
.grow { flex: 1; }
.brbox { border: 1px solid #e6e9ee; border-radius: 6px; padding: 8px; margin-bottom: 8px; background: #fafbfd; }
.brhead { display: flex; align-items: center; gap: 8px; margin-bottom: 6px; }
.brhead :deep(.el-input) { flex: 1; min-width: 0; }
.brhead :deep(.el-button) { flex-shrink: 0; white-space: nowrap; padding: 4px 6px; }
.edgeflow { stroke-dasharray: 10 7; animation: dashmove .65s linear infinite; }
.edgeglow { animation: edgeglow 1.2s ease-in-out infinite; }
.flowdot { filter: drop-shadow(0 1px 2px rgba(20, 70, 45, .35)); }
@keyframes dashmove { from { stroke-dashoffset: 17; } to { stroke-dashoffset: 0; } }
@keyframes edgeglow { 0%, 100% { opacity: .08; } 50% { opacity: .2; } }
@keyframes beacon { 0% { box-shadow: 0 0 0 2px rgba(12, 138, 83, .24); } 100% { box-shadow: 0 0 0 8px rgba(12, 138, 83, 0); } }
@keyframes nodehalo { 0% { opacity: .5; transform: scale(1); } 100% { opacity: 0; transform: scale(1.045); } }
@keyframes statedot { 0%, 100% { opacity: 1; transform: scale(1); } 50% { opacity: .5; transform: scale(.72); } }
@keyframes nodeprogress { 0% { opacity: .35; transform: scaleX(.12); } 55% { opacity: 1; transform: scaleX(1); } 100% { opacity: .25; transform: scaleX(1); } }
@media (prefers-reduced-motion: reduce) {
  .livebeacon.busy, .nodehalo, .statedot, .nodeprogress, .edgeflow, .edgeglow { animation: none; }
}
@media (max-width: 1280px) {
  .side { width: 220px; flex-basis: 220px; }
  .cfg { width: 440px; }
  .flowidentity small { max-width: 360px; }
}
.flow-history { display: flex; flex-direction: column; gap: 4px; }
.flow-history-title { font-size: 12px; color: #888; margin-bottom: 2px; }
.flow-history-empty { font-size: 12px; color: #aaa; padding: 6px 0; }
.flow-history-row { display: flex; align-items: center; gap: 8px; font-size: 12px; }
.flow-history-time { color: #999; font-variant-numeric: tabular-nums; }
.flow-history-label { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
</style>
