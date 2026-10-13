<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted, watch } from 'vue'
import { onBeforeRouteLeave, useRoute, useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { api } from '../api'
import MesFlow from './MesFlow.vue'
import { reconcileMonitorRuntime, sameMonitorPresentation } from '../utils/deviceRuntime'
import { filterEntriesByStations, retainExistingStations, toggleStationSelection } from '../utils/monitorStations'
import {
  connectionDiagnosticOptions,
  filterConnectionDiagnostics,
  type ConnectionDiagnosticsSnapshot,
} from '../utils/connectionDiagnostics'
import MoveTagDialog from '../components/MoveTagDialog.vue'
import { useI18n } from '../i18n'
import { formatArrayBytes, selectArrayHealthDevices } from '../utils/arrayHealth'
import { useRealtimeStore } from '../stores/realtime'
import { usePagedRows } from '../composables/usePagedRows'
import { canEdit } from '../auth'

const { t } = useI18n()

/** 监控中心(异常中心):通道健康色块 + 异常工作队列(严重度排序、根因折叠、搜索直达、点击跳画布)。
 *  设计原则(ISA-18.2):红只留给"需要现在处理";不按时间流水刷屏;一台设备离线波及 N 任务只报 1 条根因。 */

const route = useRoute()
const router = useRouter()
const data = ref<any>(null)
const realtime = useRealtimeStore()
const runtimeDevices = computed<any[]>(() => [...realtime.deviceStates.values()])
const anomalySource = ref<any>(null)
const diagnosticData = ref<ConnectionDiagnosticsSnapshot>({ active: [], recovered: [] })
const diagnosticState = ref<'active' | 'recovered' | 'all'>('active')
const diagnosticQuery = ref('')
const diagnosticCategory = ref('')
const diagnosticProtocol = ref('')
const q = ref('')            // 服务端搜索(设备/任务/节点/信息)
const selectedChannels = ref(new Set<string>()) // 工位多选；空集合=显示全部
const stationQuery = ref('') // 顶部工位快速过滤(与下方异常全文搜索相互独立)
const showAllChannels = ref(false)
const expanded = ref(new Set<string>())
let qTimer: any
let detailTimer: ReturnType<typeof setInterval> | undefined
let inflight = false
const initialLoading = ref(true)
const loadError = ref('')

async function load() {
  if (inflight) return
  inflight = true
  try {
    const [anomalies, diagnostics, faults] = await Promise.all([
      api.anomalies(q.value || undefined),
      api.connectionDiagnostics().catch(() => ({ active: [], recovered: [] })),
      api.tagFaults().catch(() => ({ active: [], recovered: [], trackedCount: 0, overflowCount: 0 })),
    ])
    tagFaultData.value = faults
    loadError.value = ''
    anomalySource.value = anomalies
    const next = reconcileMonitorRuntime(anomalies, runtimeDevices.value, q.value)
    const retained = retainExistingStations(selectedChannels.value, next.channels)
    if (retained.size !== selectedChannels.value.size
      || [...retained].some((name) => !selectedChannels.value.has(name)))
      selectedChannels.value = retained
    if (!sameMonitorPresentation(data.value, next)) data.value = next
    if (!sameMonitorPresentation(diagnosticData.value, diagnostics)) diagnosticData.value = diagnostics
  } catch (e: any) { loadError.value = e?.message || t('加载监控状态失败') }
  finally { inflight = false; initialLoading.value = false }
}
function applyRealtimePresentation() {
  if (!anomalySource.value) return
  const next = reconcileMonitorRuntime(anomalySource.value, runtimeDevices.value, q.value)
  if (!sameMonitorPresentation(data.value, next)) data.value = next
}
watch(runtimeDevices, applyRealtimePresentation)
// 内部数据通道背压(/health.dataplane):只表示网关进程内队列，不代表 PLC 或客户端在线。
// 常态只显示“无积压/无丢弃”，避免把固定内部消费者数量误解成外部订阅数。
const hp = ref<any>(null)
const dpOpen = ref(false)
const DP_SUB_NAMES: Record<string, string> = {
  alarms: '告警处理',
  'opcua-push': 'OPC UA 数据推送',
  computed: '计算点处理',
  history: '历史记录',
  mqtt: 'MQTT 发布',
}
async function loadHealth() {
  try {
    const next = await api.health()
    if (!sameMonitorPresentation(hp.value?.dataplane, (next as any)?.dataplane)) hp.value = next
  } catch {}
}
const dp = computed<any>(() => (hp.value as any)?.dataplane || null)
const DEPTH_WARN = 5000   // 订阅队列滞后预警线(队列容量 1 万的一半)
const dpIssues = computed<any[]>(() => {
  const d = dp.value
  if (!d) return []
  const list: any[] = []
  if (d.busDropped > 0)
    list.push({ label: `内部总线丢弃 ${d.busDropped}`, tip: '内部工作队列满，累计发生数据丢弃' })
  for (const s of d.subs || [])
    if (s.dropped > 0 || s.depth >= DEPTH_WARN)
      list.push({ label: `${DP_SUB_NAMES[s.name] || s.name || '内部消费者'} · ${s.dropped > 0 ? `丢 ${s.dropped}` : ''}${s.dropped > 0 && s.depth >= DEPTH_WARN ? ' · ' : ''}${s.depth >= DEPTH_WARN ? `积压 ${s.depth}` : ''}`, tip: `内部消费队列 · 当前积压 ${s.depth} · 累计丢弃 ${s.dropped}` })
  return list
})
function onSearch() { clearTimeout(qTimer); qTimer = setTimeout(load, 300) }
onMounted(() => {
  void load()
  void loadHealth()
  // 异常聚合、连接历史和数据平面指标是低频详情；设备/MES 当前态由 SignalR 推送。
  detailTimer = setInterval(() => { void load(); void loadHealth() }, 10_000)
})
onUnmounted(() => { clearInterval(detailTimer); clearTimeout(qTimer) })

const entries = computed<any[]>(() => {
  const list = data.value?.entries || []
  return filterEntriesByStations(list, selectedChannels.value)
})
const tagFaultData = ref<any>({ active: [], recovered: [], trackedCount: 0, overflowCount: 0 })
const tagFaultState = ref<'active' | 'recovered' | 'all'>('active')
const tagFaultQuery = ref('')
// 点位级故障：按点位说明「为什么读不了、该怎么改」，和上面的连接诊断互补。
const tagFaults = computed<any[]>(() => {
  const source = tagFaultState.value === 'recovered'
    ? tagFaultData.value.recovered || []
    : tagFaultState.value === 'all'
      ? [...(tagFaultData.value.active || []), ...(tagFaultData.value.recovered || [])]
      : tagFaultData.value.active || []
  const keyword = tagFaultQuery.value.trim().toLowerCase()
  const byStation = source.filter((row: any) => !selectedChannels.value.size || selectedChannels.value.has(row.channel))
  if (!keyword) return byStation
  return byStation.filter((row: any) => [row.tagId, row.address, row.cause, row.suggestion, row.quality]
    .some((field: string) => String(field || '').toLowerCase().includes(keyword)))
})
const moveDialogOpen = ref(false)
const movingTagPath = ref('')
// 移动点位走工程变更(/api/v1/project/mutations)，后端按「工程配置」的修改权限判断，不是监控中心
const projectEditable = computed(() => canEdit('project'))
function openMove(row: any) { if (!projectEditable.value) return; movingTagPath.value = row.tagId; moveDialogOpen.value = true }
const tagFaultPage = usePagedRows(tagFaults, {
  resetOn: () => [tagFaultState.value, tagFaultQuery.value, [...selectedChannels.value].join('|')],
})
const diagnosticOptions = computed(() => connectionDiagnosticOptions(diagnosticData.value))
const diagnostics = computed(() => filterConnectionDiagnostics(diagnosticData.value, {
  state: diagnosticState.value,
  query: diagnosticQuery.value,
  category: diagnosticCategory.value,
  protocol: diagnosticProtocol.value,
  channels: selectedChannels.value,
}))
// 百台以上设备同时异常时只渲染当前页(每 10 秒刷新一次，整表重画会卡住页面)。
const diagnosticPage = usePagedRows(diagnostics, {
  resetOn: () => [diagnosticState.value, diagnosticQuery.value, diagnosticCategory.value, diagnosticProtocol.value, [...selectedChannels.value].join('|')],
})
const diagnosticCounts = computed(() => ({
  active: diagnosticData.value.active?.length || 0,
  recovered: diagnosticData.value.recovered?.length || 0,
}))
// 工位/通道多时，异常必须稳定排在前面；同级按异常数降序，再按名称排序，刷新时不会乱跳。
const channelRank: Record<string, number> = { offline: 0, fail: 1, stuck: 2, slow: 3, ok: 4 }
const channels = computed<any[]>(() => [...(data.value?.channels || [])].sort((a: any, b: any) => {
  const rank = (channelRank[a.level] ?? 3) - (channelRank[b.level] ?? 3)
  if (rank) return rank
  const count = Number(b.count || 0) - Number(a.count || 0)
  return count || String(a.name || '').localeCompare(String(b.name || ''), 'zh-CN')
}))
const filteredChannels = computed<any[]>(() => {
  const keyword = stationQuery.value.trim().toLocaleLowerCase('zh-CN')
  if (!keyword) return channels.value
  return channels.value.filter((c: any) => String(c.name || '').toLocaleLowerCase('zh-CN').includes(keyword))
})
const channelSummary = computed(() => {
  const bad = channels.value.filter((c: any) => c.level !== 'ok').length
  return { total: channels.value.length, bad, ok: channels.value.length - bad }
})
const selectedCount = computed(() => selectedChannels.value.size)
const arrayHealthDevices = computed(() => selectArrayHealthDevices(runtimeDevices.value, selectedChannels.value))
const hiddenSelectedCount = computed(() => {
  if (!stationQuery.value.trim() || !selectedChannels.value.size) return 0
  const visible = new Set(filteredChannels.value.map((channel: any) => channel.name))
  return [...selectedChannels.value].filter((name) => !visible.has(name)).length
})

const sevText: any = { offline: '设备离线', fail: '失败', stuck: '卡住', slow: '慢于基线' }
const sevVar = (s: string) => `var(--sev-${s === 'offline' ? 'fail' : s})`
const sevBgVar = (s: string) => `var(--sev-${s === 'offline' ? 'fail' : s}-bg)`
const lvlVar = (l: string) => l === 'ok' ? 'var(--sev-ok)' : sevVar(l)

function fmtDur(ms: number): string {
  if (!ms || ms < 1000) return ms ? `${ms}ms` : ''
  const s = Math.floor(ms / 1000)
  if (s < 60) return `${s} 秒`
  const m = Math.floor(s / 60)
  if (m < 60) return `${m} 分 ${s % 60} 秒`
  return `${Math.floor(m / 60)} 时 ${m % 60} 分`
}
function fmtDiagnosticTime(value?: string | null): string {
  if (!value) return '—'
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString()
}
function diagnosticScope(scope: string): string {
  return ({ Transport: t('网络传输'), Session: t('协议会话'), DataAccess: t('数据访问') } as Record<string, string>)[scope] || scope
}
function retryText(row: any): string {
  if (!row.active) return t('已恢复')
  if (!row.retrying) return t('等待处理')
  return row.nextRetryMs > 0
    ? t('{seconds} 秒后自动重试', { seconds: Math.max(1, Math.ceil(row.nextRetryMs / 1000)) })
    : t('正在自动重试')
}
function diagnosticKey(row: any): string {
  return `${row.active ? 'active' : 'recovered'}|${row.device}|${row.firstOccurredAt}|${row.recoveredAt || ''}`
}
function keyOf(e: any) { return e.severity + '|' + (e.device || '') + '|' + (e.taskId || '') + '|' + (e.nodeId || '') }
function toggle(e: any) {
  const k = keyOf(e)
  expanded.value.has(k) ? expanded.value.delete(k) : expanded.value.add(k)
  expanded.value = new Set(expanded.value)
}
// 监控中心内嵌流程画布。task/node 写入 URL，因此刷新、复制链接及旧 /mes 深链都能准确复现定位。
const flowTask = computed(() => String(route.query.task || ''))
const flowNode = computed(() => String(route.query.node || ''))
const monitorFlowRef = ref<any>()
const flowOpen = computed({
  get: () => !!flowTask.value,
  set: (open: boolean) => {
    if (!open) {
      const query = { ...route.query }
      delete query.task; delete query.node
      router.replace({ path: '/monitor', query })
    }
  },
})
function locate(e: any) {
  if (!e.taskName) return
  router.push({ path: '/monitor', query: { ...route.query, task: e.taskName, node: e.nodeId || '' } })
}
async function beforeFlowClose(done: () => void) {
  const ok = !monitorFlowRef.value || await monitorFlowRef.value.flushAutoSave()
  if (ok) done()
  else ElMessage.error(t('采集流程自动保存失败，已阻止关闭，请检查页面提示后重试'))
}
onBeforeRouteLeave(async () => {
  if (monitorFlowRef.value && !(await monitorFlowRef.value.flushAutoSave())) {
    ElMessage.error(t('采集流程尚未保存，已取消离开页面'))
    return false
  }
  return true
})
function pickChannel(name: string) { selectedChannels.value = toggleStationSelection(selectedChannels.value, name) }
function clearStationFilter() { selectedChannels.value = new Set() }
function isStationSelected(name: string) { return selectedChannels.value.has(name) }
function onStationSearch() {
  showAllChannels.value = false
}
</script>

<template>
  <div v-loading="initialLoading" :element-loading-text="t('正在加载监控状态…')">
    <el-alert v-if="loadError" :title="loadError" type="error" :closable="false" />
    <!-- 工位健康总览:固定高度的响应式网格,异常优先;多工位时不再无限向下撑高页面。 -->
    <section class="stationpanel">
      <div class="stationhead">
        <div class="stationtitle"><strong>{{ $t('工位状态') }}</strong><small>{{ $t('异常优先 · 点击卡片可多选') }}</small></div>
        <el-input v-model="stationQuery" class="stationsearch" size="small" clearable :placeholder="$t('搜索工位名称…')"
                  @input="onStationSearch" @clear="onStationSearch" />
        <el-button v-if="selectedCount" class="clearstation" size="small" type="primary" plain @click="clearStationFilter">{{ $t('清空筛选') }}</el-button>
        <el-button size="small" type="primary" plain @click="router.push('/performance')">{{ $t('采集性能详情') }}</el-button>
        <div class="stationstats">
          <span v-if="selectedCount" class="sselected">{{ $t('已选 {count} 个', { count: selectedCount }) }}</span>
          <span v-if="hiddenSelectedCount" class="shidden">{{ $t('其中 {count} 个被搜索隐藏', { count: hiddenSelectedCount }) }}</span>
          <span v-if="stationQuery" class="smatch">{{ $t('匹配 {matched}/{total}', { matched: filteredChannels.length, total: channelSummary.total }) }}</span>
          <span v-if="channelSummary.bad" class="sbad">{{ $t('异常 {count}', { count: channelSummary.bad }) }}</span>
          <span class="sok">{{ $t('正常 {count}', { count: channelSummary.ok }) }}</span>
          <span class="stotal">{{ $t('共 {count}', { count: channelSummary.total }) }}</span>
        </div>
        <el-button v-if="filteredChannels.length > 12" size="small" text type="primary" @click="showAllChannels = !showAllChannels">
          {{ showAllChannels ? $t('收起') : $t('展开全部 {count}', { count: filteredChannels.length }) }}
        </el-button>
      </div>
      <div class="tiles" :class="{ expanded: showAllChannels }">
        <div v-for="c in filteredChannels" :key="c.name" class="tile" :class="{ on: isStationSelected(c.name), bad: c.level !== 'ok' }"
             :style="{
               borderColor: isStationSelected(c.name) ? undefined : lvlVar(c.level),
               background: isStationSelected(c.name) ? undefined : (c.level === 'ok' ? 'var(--sev-ok-bg)' : sevBgVar(c.level)),
             }"
             :title="`${isStationSelected(c.name) ? $t('已选 · ') : ''}${c.name} · ${$t('在线 {count}', { count: c.online || 0 })}${c.standby ? ` · ${$t('待机 {count}', { count: c.standby })}` : ''} · ${$t('共 {count}', { count: c.devices })}${c.count ? ` · ${$t('异常 {count}', { count: c.count })}` : ` · ${$t('正常')}`}`"
             role="button" tabindex="0" :aria-pressed="isStationSelected(c.name)"
             @click="pickChannel(c.name)" @keydown.enter.prevent="pickChannel(c.name)" @keydown.space.prevent="pickChannel(c.name)">
          <div class="tname">
            <span class="dot" :style="{ background: lvlVar(c.level) }"></span><span class="stationname">{{ c.name }}</span>
            <span v-if="isStationSelected(c.name)" class="selectedmark" aria-hidden="true">{{ $t('✓ 已选') }}</span>
          </div>
          <div class="tmeta">
            <span>{{ $t('在线 {count}', { count: c.online || 0 }) }}<template v-if="c.standby"> · {{ $t('待机 {count}', { count: c.standby }) }}</template> / {{ c.devices }}</span>
            <span v-if="c.count" class="tcount" :style="{ color: lvlVar(c.level) }">{{ $t('{count} 个异常', { count: c.count }) }}</span>
            <span v-else class="tok">{{ $t('正常') }}</span>
          </div>
        </div>
        <div v-if="!filteredChannels.length" class="tile tempty">{{ $t(stationQuery ? '未找到匹配工位' : '暂无工位') }}</div>
      </div>
    </section>

    <section class="card connectiondiag">
      <div class="diaghead">
        <div class="diagtitle">
          <strong>{{ $t('PLC 连接诊断') }}</strong>
          <small>{{ $t('活动异常实时更新；已恢复记录仅保留到本次 Runtime 退出') }}</small>
        </div>
        <div class="diagcounts">
          <span class="activecount">{{ $t('当前 {count}', { count: diagnosticCounts.active }) }}</span>
          <span>{{ $t('已恢复 {count}', { count: diagnosticCounts.recovered }) }}</span>
        </div>
      </div>
      <div class="diagfilters">
        <el-segmented v-model="diagnosticState" :options="[
          { label: $t('当前异常'), value: 'active' },
          { label: $t('已恢复'), value: 'recovered' },
          { label: $t('全部'), value: 'all' },
        ]" size="small" />
        <el-input v-model="diagnosticQuery" clearable class="diagsearch"
                  :placeholder="$t('搜索设备、原因、建议或原始错误…')" />
        <el-select v-model="diagnosticCategory" clearable class="diagselect" :placeholder="$t('全部类别')">
          <el-option v-for="option in diagnosticOptions.categories" :key="option.value"
                     :label="$t(option.label)" :value="option.value" />
        </el-select>
        <el-select v-model="diagnosticProtocol" clearable class="diagselect" :placeholder="$t('全部协议')">
          <el-option v-for="protocol in diagnosticOptions.protocols" :key="protocol" :label="protocol" :value="protocol" />
        </el-select>
        <el-tag v-if="selectedCount" closable effect="plain" @close="clearStationFilter">
          {{ $t('已按 {count} 个工位筛选', { count: selectedCount }) }}
        </el-tag>
      </div>

      <el-table v-if="diagnostics.length" :data="diagnosticPage.pagedRows.value" :row-key="diagnosticKey" max-height="360" class="diagtable">
        <el-table-column type="expand" width="42">
          <template #default="{ row }">
            <div class="diagexpand">
              <div><b>{{ $t('判断原因') }}</b><span>{{ row.cause }}</span></div>
              <div><b>{{ $t('处理建议') }}</b><span>{{ row.suggestion }}</span></div>
              <div><b>{{ $t('原始错误') }}</b><code>{{ row.rawError || '—' }}</code></div>
              <div><b>{{ $t('首次发生') }}</b><span>{{ fmtDiagnosticTime(row.firstOccurredAt) }}</span></div>
              <div v-if="row.recoveredAt"><b>{{ $t('恢复时间') }}</b><span>{{ fmtDiagnosticTime(row.recoveredAt) }}</span></div>
            </div>
          </template>
        </el-table-column>
        <el-table-column :label="$t('状态')" width="86">
          <template #default="{ row }">
            <el-tag :type="row.active ? (row.availability === 'PartialOnline' ? 'warning' : 'danger') : 'success'" effect="light" size="small">
              {{ $t(row.active ? (row.availability === 'PartialOnline' ? '部分在线' : '异常中') : '已恢复') }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column prop="category" :label="$t('异常类别')" min-width="140">
          <template #default="{ row }"><b class="diagcategory">{{ $t(row.category) }}</b></template>
        </el-table-column>
        <el-table-column prop="device" :label="$t('设备')" min-width="190" show-overflow-tooltip />
        <el-table-column prop="protocol" :label="$t('协议')" min-width="120" show-overflow-tooltip />
        <el-table-column :label="$t('阶段')" width="94">
          <template #default="{ row }">{{ diagnosticScope(row.scope) }}</template>
        </el-table-column>
        <el-table-column prop="cause" :label="$t('判断原因')" min-width="260" show-overflow-tooltip />
        <el-table-column :label="$t('次数')" width="74" align="right">
          <template #default="{ row }">{{ row.occurrenceCount }}</template>
        </el-table-column>
        <el-table-column :label="$t('最近发生')" width="165">
          <template #default="{ row }">{{ fmtDiagnosticTime(row.lastOccurredAt) }}</template>
        </el-table-column>
        <el-table-column :label="$t('自动处理')" min-width="135">
          <template #default="{ row }"><span :class="{ retrying: row.active && row.retrying }">{{ retryText(row) }}</span></template>
        </el-table-column>
      </el-table>
      <el-pagination v-if="diagnosticPage.total.value > diagnosticPage.pageSize.value" class="diagpager" small background
                     layout="total, prev, pager, next" :total="diagnosticPage.total.value"
                     :page-size="diagnosticPage.pageSize.value" v-model:current-page="diagnosticPage.page.value" />
      <el-empty v-else-if="!diagnostics.length" :image-size="64" :description="$t(diagnosticState === 'active' ? '当前没有 PLC 连接异常' : '没有符合筛选条件的连接记录')" />
    </section>

    <!-- 点位级故障:连接诊断回答「设备为什么连不上」,这里回答「哪些点位读不了、为什么、怎么改」 -->
    <section class="card tagfaults">
      <div class="diaghead">
        <div class="diagtitle">
          <strong>{{ $t('点位异常') }}</strong>
          <small>{{ $t('按点位给出原因与处理建议；已恢复记录仅保留到本次 Runtime 退出') }}</small>
        </div>
        <div class="diagcounts">
          <span class="activecount">{{ $t('当前 {count}', { count: (tagFaultData.active || []).length }) }}</span>
          <span>{{ $t('已恢复 {count}', { count: (tagFaultData.recovered || []).length }) }}</span>
        </div>
      </div>
      <div class="diagfilters">
        <el-segmented v-model="tagFaultState" :options="[
          { label: $t('当前异常'), value: 'active' },
          { label: $t('已恢复'), value: 'recovered' },
          { label: $t('全部'), value: 'all' },
        ]" size="small" />
        <el-input v-model="tagFaultQuery" clearable class="diagsearch"
                  :placeholder="$t('搜索点位路径、地址、原因或建议…')" />
        <el-tag v-if="tagFaultData.overflowCount" type="warning" effect="plain">
          {{ $t('另有 {count} 个点位未逐条记录（超出单设备上限）', { count: tagFaultData.overflowCount }) }}
        </el-tag>
      </div>

      <el-table v-if="tagFaults.length" :data="tagFaultPage.pagedRows.value" row-key="tagId" max-height="360" class="diagtable">
        <el-table-column type="expand" width="42">
          <template #default="{ row }">
            <div class="diagexpand">
              <div><b>{{ $t('点位路径') }}</b><code>{{ row.tagId }}</code></div>
              <div><b>{{ $t('PLC 地址') }}</b><code>{{ row.address }}</code></div>
              <div><b>{{ $t('为什么不对') }}</b><span>{{ row.cause }}</span></div>
              <div><b>{{ $t('需要怎么做') }}</b><span>{{ row.suggestion }}</span></div>
              <div><b>{{ $t('数据类型') }}</b><span>{{ row.dataType }}</span></div>
              <div><b>{{ $t('质量码') }}</b><code>{{ row.quality }} {{ row.qualityHex }}</code></div>
              <div><b>{{ $t('首次发生') }}</b><span>{{ fmtDiagnosticTime(row.firstSeenAt) }}</span></div>
              <div v-if="row.recoveredAt"><b>{{ $t('恢复时间') }}</b><span>{{ fmtDiagnosticTime(row.recoveredAt) }}</span></div>
            </div>
          </template>
        </el-table-column>
        <el-table-column :label="$t('状态')" width="86">
          <template #default="{ row }">
            <el-tag :type="row.active ? 'danger' : 'success'" effect="light" size="small">
              {{ $t(row.active ? '异常中' : '已恢复') }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column prop="tagId" :label="$t('点位路径')" min-width="280" show-overflow-tooltip />
        <el-table-column prop="address" :label="$t('PLC 地址')" min-width="200" show-overflow-tooltip />
        <el-table-column prop="quality" :label="$t('质量')" width="140" show-overflow-tooltip />
        <el-table-column prop="cause" :label="$t('为什么不对')" min-width="300" show-overflow-tooltip />
        <el-table-column :label="$t('次数')" width="74" align="right">
          <template #default="{ row }">{{ row.occurrenceCount }}</template>
        </el-table-column>
        <el-table-column :label="$t('隔离')" width="74">
          <template #default="{ row }">
            <el-tag v-if="row.isolated" size="small" type="info" effect="plain">{{ $t('已隔离') }}</el-tag>
            <span v-else>—</span>
          </template>
        </el-table-column>
        <el-table-column :label="$t('最近发生')" width="165">
          <template #default="{ row }">{{ fmtDiagnosticTime(row.lastSeenAt) }}</template>
        </el-table-column>
        <el-table-column v-if="projectEditable" :label="$t('操作')" width="110" fixed="right">
          <template #default="{ row }">
            <el-button link type="primary" size="small" @click="openMove(row)">{{ $t('移动到…') }}</el-button>
          </template>
        </el-table-column>
      </el-table>
      <el-pagination v-if="tagFaultPage.total.value > tagFaultPage.pageSize.value" class="diagpager" small background
                     layout="total, prev, pager, next" :total="tagFaultPage.total.value"
                     :page-size="tagFaultPage.pageSize.value" v-model:current-page="tagFaultPage.page.value" />
      <el-empty v-else :image-size="64" :description="$t(tagFaultState === 'active' ? '当前没有异常点位' : '没有符合筛选条件的点位记录')" />
      <MoveTagDialog v-if="projectEditable" v-model="moveDialogOpen" :tag-path="movingTagPath" @moved="load()" />
    </section>

    <section v-if="arrayHealthDevices.length" class="card arrayhealth">
      <div class="arrayhealth-head">
        <div>
          <strong>{{ $t('逻辑数组质量与容量') }}</strong>
          <small>{{ $t('部分元素 Bad 不会把设备判为离线；失败范围按 PLC 实际索引显示') }}</small>
        </div>
        <span>{{ $t('{count} 台设备', { count: arrayHealthDevices.length }) }}</span>
      </div>
      <el-table :data="arrayHealthDevices" max-height="300" size="small" class="arrayhealth-table">
        <el-table-column prop="name" :label="$t('设备')" min-width="190" show-overflow-tooltip />
        <el-table-column :label="$t('数组/元素')" width="132">
          <template #default="{ row }">{{ row.stats.arrayTags }} / {{ row.stats.arrayElements.toLocaleString() }}</template>
        </el-table-column>
        <el-table-column :label="$t('Good / Uncertain / Bad')" min-width="210">
          <template #default="{ row }">
            <span class="arraygood">{{ row.stats.arrayGood.toLocaleString() }}</span>
            <span class="arraysep">/</span>
            <span class="arrayuncertain">{{ row.stats.arrayUncertain.toLocaleString() }}</span>
            <span class="arraysep">/</span>
            <span class="arraybad">{{ row.stats.arrayBad.toLocaleString() }}</span>
          </template>
        </el-table-column>
        <el-table-column :label="$t('分块/容量')" width="140">
          <template #default="{ row }">{{ row.stats.arrayBlocks }} / {{ formatArrayBytes(row.stats.arrayBytes) }}</template>
        </el-table-column>
        <el-table-column prop="stats.arrayFailedRanges" :label="$t('失败索引范围')" min-width="300" show-overflow-tooltip>
          <template #default="{ row }"><span :class="row.stats.arrayBad ? 'arraybad' : 'arraynone'">{{ row.stats.arrayFailedRanges || $t('无') }}</span></template>
        </el-table-column>
        <el-table-column :label="$t('周期 P95 / P99')" width="145">
          <template #default="{ row }">{{ row.stats.actualP95PeriodMs || 0 }} / {{ row.stats.actualP99PeriodMs || 0 }} ms</template>
        </el-table-column>
      </el-table>
    </section>

    <!-- 内部数据通道:只展示网关内部队列健康，不代表 PLC 在线状态 -->
    <div v-if="dp" class="card dpstrip">
      <b :title="$t('网关进程内部的数据分发队列，不代表 PLC 在线')">{{ $t('内部数据通道') }}</b>
      <template v-if="!dpIssues.length">
        <span class="dpok">{{ $t('✓ 传输正常') }}</span>
        <span class="dpmeta">{{ $t('无积压 · 无丢弃') }}</span>
      </template>
      <template v-else>
        <span v-for="(it, i) in dpIssues" :key="i" class="dpchip bad" :title="it.tip">{{ it.label }}</span>
      </template>
      <span class="dpscope">{{ $t('仅反映网关内部队列，不代表 PLC 在线') }}</span>
      <span class="dphint" @click="dpOpen = !dpOpen">{{ $t(dpOpen ? '收起' : '详情') }}</span>
    </div>
    <div v-if="dp && dpOpen" class="card dpdetail">
      <div class="dprow">
        <span class="dplabel">{{ $t('内部工作队列') }}</span>
        <span v-for="s in dp.shardList" :key="s.id" class="dpchip" :class="{ bad: s.dropped > 0 }"
              :title="`工作队列 ${s.id} · 当前积压 ${s.depth} · 累计丢弃 ${s.dropped}`">
          #{{ s.id }} 积压{{ s.depth }}<template v-if="s.dropped"> · 丢{{ s.dropped }}</template>
        </span>
      </div>
      <div class="dprow">
        <span class="dplabel">{{ $t('内部消费者') }}</span>
        <span v-for="(s, i) in dp.subs" :key="s.name + i" class="dpchip" :class="{ bad: s.dropped > 0 }"
              :title="`网关内部消费者 · 当前积压 ${s.depth} · 累计丢弃 ${s.dropped}`">
          {{ DP_SUB_NAMES[s.name] || s.name || '未命名内部消费者' }}{{ s.kind === 'points' ? ` · 监听${s.points}点` : ' · 全量' }} · 积压{{ s.depth }}<template v-if="s.dropped"> · 丢{{ s.dropped }}</template>
        </span>
      </div>
    </div>

    <div class="card">
      <div class="bar">
        <el-input v-model="q" :placeholder="$t('搜索:设备 / 任务 / 节点 / 点位…')" clearable style="width: 300px"
                  @input="onSearch" @clear="onSearch" />
        <el-tag v-if="selectedCount" closable @close="clearStationFilter" effect="plain">{{ $t('工位筛选：{count} 个', { count: selectedCount }) }}</el-tag>
        <span class="legend">
          <i :style="{ background: 'var(--sev-fail)' }"></i>{{ $t('失败/离线') }}
          <i :style="{ background: 'var(--sev-stuck)' }"></i>{{ $t('卡住') }}
          <i :style="{ background: 'var(--sev-slow)' }"></i>{{ $t('慢于基线') }}
        </span>
        <span class="cnt" v-if="entries.length">{{ $t('{count} 条（按严重度 × 波及 × 时长排序）', { count: entries.length }) }}</span>
      </div>

      <el-empty v-if="!entries.length" :description="$t(selectedCount ? '所选工位当前无异常' : '全部正常')" :image-size="90">
        <template #description><span :class="selectedCount ? 'filterempty' : 'allok'">{{ $t(selectedCount ? '所选工位当前无异常' : '✓ 全部正常，当前没有异常') }}</span></template>
      </el-empty>

      <div v-else class="list">
        <template v-for="e in entries" :key="keyOf(e)">
          <div class="row" :style="{ borderLeftColor: sevVar(e.severity) }">
            <span class="sev" :style="{ background: sevBgVar(e.severity), color: sevVar(e.severity) }">{{ sevText[e.severity] || e.severity }}</span>
            <div class="body">
              <div class="title">
                {{ e.title }}
                <el-tag v-if="e.impactedCount" size="small" type="danger" effect="plain" style="margin-left: 6px">{{ $t('波及 {count} 个任务节点', { count: e.impactedCount }) }}</el-tag>
              </div>
              <div class="info" v-if="e.info">{{ e.info }}</div>
            </div>
            <span class="dur" v-if="e.ageMs">{{ $t('已持续 {time}', { time: fmtDur(e.ageMs) }) }}</span>
            <el-button v-if="e.taskName" size="small" type="primary" plain @click="locate(e)">{{ $t('定位画布') }}</el-button>
            <el-button v-if="e.impacted && e.impacted.length" size="small" text @click="toggle(e)">
              {{ $t(expanded.has(keyOf(e)) ? '收起' : '展开') }}
            </el-button>
          </div>
          <!-- 根因折叠的子项 -->
          <template v-if="e.impacted && expanded.has(keyOf(e))">
            <div v-for="k in e.impacted" :key="keyOf(k)" class="row kid" :style="{ borderLeftColor: sevVar(k.severity) }">
              <span class="sev" :style="{ background: sevBgVar(k.severity), color: sevVar(k.severity) }">{{ sevText[k.severity] }}</span>
              <div class="body">
                <div class="title">{{ k.title }}</div>
                <div class="info" v-if="k.info">{{ k.info }}</div>
              </div>
              <span class="dur" v-if="k.ageMs">{{ fmtDur(k.ageMs) }}</span>
              <el-button size="small" type="primary" plain @click="locate(k)">{{ $t('定位画布') }}</el-button>
            </div>
          </template>
        </template>
      </div>
    </div>

    <el-dialog v-model="flowOpen" :title="`${$t('MES 流程监控')} — ${flowTask}`" width="96%" top="3vh"
               append-to-body class="monitorflowdlg" destroy-on-close :before-close="beforeFlowClose">
      <MesFlow v-if="flowOpen" ref="monitorFlowRef" :key="flowTask + '|' + flowNode" :focus-task="flowTask" :focus-node="flowNode" />
    </el-dialog>
  </div>
</template>

<style scoped>
.connectiondiag { margin-bottom: 12px; padding: 12px 14px 14px; }
.arrayhealth { margin-bottom: 12px; padding: 12px 14px 14px; }
.arrayhealth-head { display: flex; align-items: baseline; gap: 12px; margin-bottom: 9px; color: #26384a; }
.arrayhealth-head > div { display: flex; align-items: baseline; gap: 9px; min-width: 0; }
.arrayhealth-head small { color: #8a98a8; font-size: 11px; }
.arrayhealth-head > span { margin-left: auto; color: #637083; font-size: 11px; }
.arrayhealth-table { width: 100%; border: 1px solid #e8edf3; border-radius: 7px; overflow: hidden; }
.arraygood { color: var(--sev-ok); font-weight: 700; }
.arrayuncertain { color: var(--sev-slow); font-weight: 700; }
.arraybad { color: var(--sev-fail); font-weight: 700; }
.arraynone, .arraysep { color: #9aa6b2; margin: 0 4px; }
.diaghead { display: flex; align-items: center; gap: 16px; margin-bottom: 10px; }
.diagtitle { display: flex; align-items: baseline; gap: 9px; min-width: 0; }
.diagtitle strong { color: #26384a; font-size: 14px; }
.diagtitle small { color: #8a98a8; font-size: 11px; }
.diagcounts { display: flex; gap: 7px; margin-left: auto; font-size: 11px; color: #637083; }
.diagcounts span { padding: 3px 8px; border-radius: 10px; background: #f1f4f7; }
.diagcounts .activecount { color: var(--sev-fail); background: var(--sev-fail-bg); font-weight: 700; }
.diagfilters { display: flex; align-items: center; gap: 8px; margin-bottom: 10px; flex-wrap: wrap; }
.diagsearch { width: min(320px, 100%); }
.diagselect { width: 155px; }
.diagtable { width: 100%; border: 1px solid #e8edf3; border-radius: 7px; overflow: hidden; }
.diagpager { margin-top: 8px; justify-content: flex-end; }
.connectiondiag :deep(.el-empty) { padding: 10px 0 4px; }
.connectiondiag :deep(.el-empty__image) { width: 42px; }
.connectiondiag :deep(.el-empty__description) { margin-top: 6px; }
.diagcategory { color: #b42318; font-size: 12px; }
.retrying { color: #a46700; font-weight: 600; }
.diagexpand { display: grid; gap: 7px; padding: 4px 22px 9px 62px; color: #536273; }
.diagexpand > div { display: grid; grid-template-columns: 78px minmax(0, 1fr); gap: 10px; align-items: start; }
.diagexpand b { color: #33465b; font-size: 12px; }
.diagexpand span, .diagexpand code { white-space: pre-wrap; overflow-wrap: anywhere; font-size: 12px; }
.diagexpand code { color: #8f2e24; padding: 6px 8px; border-radius: 5px; background: #fff4f2; font-family: Consolas, monospace; }
@media (max-width: 900px) {
  .diaghead { align-items: flex-start; flex-wrap: wrap; }
  .diagcounts { margin-left: 0; }
  .diagselect { width: min(100%, 180px); }
  .diagexpand { padding-left: 20px; }
}
.stationpanel { margin-bottom: 12px; padding: 10px 12px 11px; border: 1px solid #e3e8ef; border-radius: 9px; background: #fff; box-shadow: 0 4px 16px rgba(31, 45, 61, .04); }
.stationhead { display: flex; align-items: center; gap: 12px; min-height: 28px; margin-bottom: 8px; }
.stationtitle { display: flex; align-items: baseline; gap: 8px; min-width: 0; }
.stationtitle strong { color: #26384a; font-size: 13px; }
.stationtitle small { color: #8a98a8; font-size: 10.5px; white-space: nowrap; }
.stationsearch { width: 190px; flex: none; }
.stationstats { display: flex; align-items: center; gap: 6px; margin-left: auto; font-size: 10.5px; font-variant-numeric: tabular-nums; }
.stationstats span { padding: 2px 7px; border-radius: 9px; white-space: nowrap; }
.stationstats .sbad { color: var(--sev-fail); background: var(--sev-fail-bg); font-weight: 700; }
.stationstats .sok { color: var(--sev-ok); background: var(--sev-ok-bg); }
.stationstats .stotal { color: #718092; background: #f1f4f7; }
.stationstats .smatch { color: #1769aa; background: #e8f2fd; font-weight: 700; }
.stationstats .sselected { color: #0b5cad; background: #e6f2ff; font-weight: 700; }
.stationstats .shidden { color: #8a5a00; background: #fff3d6; }
.tiles { display: grid; grid-template-columns: repeat(auto-fill, minmax(165px, 1fr)); gap: 7px; max-height: 172px; overflow-y: auto; padding: 1px 4px 2px 1px; scrollbar-width: thin; scrollbar-color: #c5cfda transparent; }
.tiles.expanded { max-height: none; overflow: visible; }
.dpstrip { display: flex; align-items: center; gap: 12px; padding: 7px 14px; margin-bottom: 12px; font-size: 12px; color: #666; flex-wrap: wrap; }
.dpstrip b { font-size: 13px; color: #333; }
.dpok { color: var(--sev-ok); font-weight: 600; }
.dpmeta { color: #999; }
.dpscope { color: #8a98a8; font-size: 11px; }
.dphint { margin-left: auto; color: #1565c0; cursor: pointer; user-select: none; }
.dpdetail { display: flex; flex-direction: column; gap: 8px; padding: 8px 14px; margin: -6px 0 12px; font-size: 12px; color: #666; }
.dprow { display: flex; align-items: center; gap: 6px; flex-wrap: wrap; }
.dplabel { color: #7b8794; margin-right: 4px; min-width: 86px; font-weight: 600; }
.dpchip { background: #f4f6f9; border-radius: 10px; padding: 1px 9px; white-space: nowrap; }
.dpchip.bad { background: var(--sev-fail-bg); color: var(--sev-fail); font-weight: 700; }
.tile { min-width: 0; border: 1.4px solid; border-radius: 7px; padding: 8px 10px; cursor: pointer; background: #fff; transition: box-shadow .15s, transform .15s, background-color .15s, border-color .15s, color .15s; }
.tile:hover { box-shadow: 0 3px 9px rgba(31,45,61,.10); transform: translateY(-1px); }
.tile:focus-visible { outline: 2px solid #6ea8ff; outline-offset: 2px; }
.tile.on { background: #253858; border-color: #253858; color: #fff; box-shadow: 0 5px 12px rgba(37,56,88,.20); }
.tile.on.bad { border-left-color: #6ea8ff; }
.tile.on .dot { box-shadow: 0 0 0 2px rgba(255,255,255,.28); }
.tile.on .tmeta { color: #c6d2e3; }
.tile.on .tcount, .tile.on .tok { color: #fff !important; }
.tile.bad { border-left-width: 4px; padding-left: 8px; }
.tempty { color: #999; cursor: default; }
.tname { min-width: 0; font-weight: 700; font-size: 12.5px; display: flex; align-items: center; gap: 6px; }
.stationname { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.selectedmark { margin-left: auto; color: #d9e8ff; font-size: 10px; font-weight: 700; white-space: nowrap; }
.dot { width: 8px; height: 8px; flex: none; border-radius: 50%; display: inline-block; }
.tmeta { display: flex; justify-content: space-between; gap: 8px; margin-top: 4px; font-size: 10.5px; color: #6d7987; }
.tcount { font-weight: 700; }
@media (max-width: 980px) {
  .stationhead { flex-wrap: wrap; }
  .stationstats { margin-left: 0; }
  .stationsearch { width: min(100%, 240px); }
}
.tok { color: var(--sev-ok); }
.bar { display: flex; align-items: center; gap: 10px; margin-bottom: 10px; flex-wrap: wrap; }
.legend { color: #888; font-size: 12px; display: flex; align-items: center; gap: 5px; margin-left: 6px; }
.legend i { width: 10px; height: 10px; border-radius: 2px; display: inline-block; margin-left: 8px; }
.cnt { color: #999; font-size: 12px; margin-left: auto; }
.allok { color: var(--sev-ok); font-weight: 600; }
.filterempty { color: #5d6b7a; font-weight: 600; }
.list { display: flex; flex-direction: column; gap: 6px; }
.row { display: flex; align-items: center; gap: 10px; padding: 9px 12px; border: 1px solid #e9edf2; border-left-width: 4px; border-radius: 6px; background: #fff; }
.row.kid { margin-left: 34px; background: #fbfcfd; }
.sev { font-size: 12px; font-weight: 700; padding: 2px 8px; border-radius: 4px; white-space: nowrap; }
.body { flex: 1; min-width: 0; }
.title { font-weight: 600; font-size: 13px; }
.info { color: #888; font-size: 12px; margin-top: 2px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; max-width: 720px; }
.dur { color: #a06a00; font-size: 12px; white-space: nowrap; font-variant-numeric: tabular-nums; }
:global(.monitorflowdlg .el-dialog__body) { padding: 6px 12px 12px; }
:global(.monitorflowdlg .el-dialog__header) { margin-right: 0; padding-bottom: 8px; }
:global(.monitorflowdlg .fwrap) { height: 84vh !important; }
</style>
