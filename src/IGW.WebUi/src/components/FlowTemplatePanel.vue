<script setup lang="ts">
import { ref, computed, onMounted, onBeforeUnmount, nextTick } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { api, type FlowTemplateTargetCatalogDto } from '../api'
import { useI18n } from '../i18n'

const { t: tr } = useI18n()

/** MES 流程模板面板:列表/套用(单组·批量)/报告/导出导入/删除。
 *  印章语义:套用即拷贝断开;缺失点保留+报告不静默;批量原子(任一目标非法整体不写)。 */

// editable=false：只读账号，只能查看和导出模板，不能导入、套用、删除
const props = withDefaults(defineProps<{ catalog: FlowTemplateTargetCatalogDto; editable?: boolean }>(), { editable: true })
const emit = defineEmits<{ (e: 'close'): void; (e: 'applied', taskName: string): void; (e: 'refreshed'): void }>()

const list = ref<any[]>([])
const loading = ref(false)
const operation = ref<{ kind: string; name: string } | null>(null)
const busy = computed(() => loading.value || applying.value || !!operation.value)
let disposed = false
onBeforeUnmount(() => { disposed = true })
async function runOperation(kind: string, name: string, work: () => Promise<void>) {
  if (busy.value) return
  operation.value = { kind, name }
  try { await nextTick(); await new Promise(resolve => setTimeout(resolve, 0)); if (!disposed) await work() }
  finally { operation.value = null }
}
const bodies = ref<Record<string, any>>({})   // 模板名 → flow(nodes/edges),画缩略图用
async function reload() {
  if (loading.value || disposed) return
  loading.value = true
  try {
    const templates = await api.flowTemplates()
    if (disposed) return
    list.value = templates
    // 并行拉各模板完整体(模板数少),流程图缩略预览
    await Promise.all(list.value.map(async (t: any) => {
      try { const d = await api.flowTemplateExport(t.name); if (!disposed) bodies.value[t.name] = d?.task?.flow || { nodes: [], edges: [] } } catch {}
    }))
  } catch (e: any) { ElMessage.error(tr('载入失败:') + (e?.response?.data?.error || e.message)) }
  finally { loading.value = false }
}
onMounted(reload)

// ---- 流程图缩略预览:按画布几何(节点 170×~60)等比缩进 260×120 视口 ----
const typeColor: any = { Trigger: '#2E7D32', Condition: '#00796B', Upload: '#1565C0', Branch: '#EF6C00', Switch: '#6A1B9A', Writeback: '#7B1FA2', ResponseWriteback: '#5E35B1', Delay: '#546E7A' }
const typeIcon: any = { Trigger: '⏱', Condition: '☰', Upload: '☁', Branch: '⇄', Switch: '⑃', Writeback: '✎', ResponseWriteback: '↙', Delay: '…' }
const VW = 260, VH = 120, NW = 170, NH = 60
function mini(name: string) {
  const f = bodies.value[name]
  if (!f?.nodes?.length) return null
  const xs = f.nodes.map((n: any) => n.x || 0), ys = f.nodes.map((n: any) => n.y || 0)
  const minX = Math.min(...xs), minY = Math.min(...ys)
  const spanX = Math.max(...xs) + NW - minX || 1, spanY = Math.max(...ys) + NH - minY || 1
  const s = Math.min((VW - 14) / spanX, (VH - 14) / spanY, 0.6)
  const px = (VW - spanX * s) / 2, py = (VH - spanY * s) / 2   // 居中
  const nodes = f.nodes.map((n: any) => ({
    id: n.id, type: n.type, title: n.title || '',
    x: px + ((n.x || 0) - minX) * s, y: py + ((n.y || 0) - minY) * s,
    w: Math.max(NW * s, 18), h: Math.max(NH * s, 13),
  }))
  const pos: any = {}; for (const n of nodes) pos[n.id] = n
  const edges = (f.edges || []).map((e: any) => {
    const a = pos[e.from], b = pos[e.to]
    return a && b ? { x1: a.x + a.w / 2, y1: a.y + a.h / 2, x2: b.x + b.w / 2, y2: b.y + b.h / 2 } : null
  }).filter(Boolean)
  return { nodes, edges, showText: NW * s >= 52 }
}

// ---- 目标组统一来自 MES 流程目录，不额外按层级筛选；任务记录存在不等于已经配置流程。 ----
const mainGroups = computed<{ path: string; hasTask: boolean; hasFlow: boolean; tpl?: string }[]>(() => {
  return (props.catalog?.groups || []).map(group => ({
    path: group.path,
    hasTask: group.hasTask,
    hasFlow: group.hasFlow,
    tpl: group.templateRef || undefined,
  }))
})
const deviceTemplates = computed<string[]>(() => props.catalog?.deviceTemplates || [])

// ---- 套用(单组 / 批量) ----
const applyFor = ref('')            // 正在套用的模板名(展开套用区)
const mode = ref<'single' | 'bulk'>('single')
const targetGroup = ref('')
const targetGroups = ref<string[]>([])
const tplFilter = ref('')           // 批量:按设备模板实例过滤
const applying = ref(false)
const reports = ref<any[]>([])      // 套用报告(单=1 份,批量=N 份)

const candidates = computed(() =>
  mainGroups.value.filter((g) => !tplFilter.value || mode.value === 'single' || g.tpl === tplFilter.value))

function startApply(name: string) {
  if (busy.value) return
  applyFor.value = applyFor.value === name ? '' : name
  reports.value = []; targetGroup.value = ''; targetGroups.value = []; tplFilter.value = ''
}
function selectAllFree() {
  targetGroups.value = candidates.value.filter((g) => !g.hasFlow).map((g) => g.path)
}
async function doApply() {
  if (busy.value) return
  applying.value = true
  const owner = props.catalog, selectedTemplate = applyFor.value, selectedMode = mode.value, selectedTarget = targetGroup.value, selectedTargets = [...targetGroups.value]
  try {
  reports.value = []
  let hasTask = false
  let hasFlow = false
  if (mode.value === 'single') {
    if (!targetGroup.value) { ElMessage.warning(tr('请选择目标组')); return }
    const selected = mainGroups.value.find((g) => g.path === targetGroup.value)
    hasTask = selected?.hasTask === true
    hasFlow = selected?.hasFlow === true
    try {
      await ElMessageBox.confirm(
        hasFlow
          ? tr('目标「{target}」已有采集流程，确认后将使用模板「{template}」覆盖。继续？', { target: targetGroup.value, template: applyFor.value })
          : tr('确认将模板「{template}」套用到「{target}」？', { template: applyFor.value, target: targetGroup.value }),
        tr(hasFlow ? '覆盖现有流程' : '套用流程模板'),
        {
          type: hasFlow ? 'warning' : 'info',
          confirmButtonText: tr(hasFlow ? '确认覆盖' : '确认套用'),
          cancelButtonText: tr('取消'),
        },
      )
    } catch { return }
  } else if (!targetGroups.value.length) {
    ElMessage.warning(tr('请勾选目标组'))
    return
  }

  if (disposed || owner !== props.catalog) return
  try {
    if (selectedMode === 'single') {
      const r = await api.flowTemplateApply(selectedTemplate, selectedTarget, hasTask)
      if (disposed) return
      reports.value = [r.report]
      ElMessage.success(hasFlow
        ? tr('已用模板覆盖任务「{task}」（已停用，请在画布检查后重新启用）', { task: r.task })
        : tr('已套用生成任务「{task}」（默认停用，请在画布检查后启用）', { task: r.task }))
      emit('applied', r.task)
    } else {
      const r = await api.flowTemplateApplyBulk(selectedTemplate, selectedTargets)
      if (disposed) return
      reports.value = r.reports || []
      ElMessage.success(tr('已批量生成 {count} 个任务（默认停用）', { count: r.count }))
      emit('refreshed')
    }
  } catch (e: any) {
    const d = e?.response?.data
    if (d?.groups?.length) {
      // 批量原子拒绝:逐组错误
      ElMessageBox.alert(d.groups.map((g: any) => `${g.group || tr('(全局)')}:${g.message}`).join('\n'), tr('批量套用未执行'), { type: 'error' })
    } else ElMessage.error(tr('套用失败:') + (d?.error || e.message))
  }
  } finally { applying.value = false }
}

// ---- 导出 / 导入 / 删除 ----
async function exportTpl(name: string) {
  return runOperation('export', name, async () => {
  try {
    const data = await api.flowTemplateExport(name)
    if (disposed) return
    const blob = new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' })
    const a = document.createElement('a')
    a.href = URL.createObjectURL(blob); a.download = `流程模板-${name}.json`; a.click()
    URL.revokeObjectURL(a.href)
  } catch (e: any) { ElMessage.error(tr('导出失败:') + (e?.response?.data?.error || e.message)) }
  })
}
const fileRef = ref<HTMLInputElement>()
async function importTpl(ev: Event) {
  const f = (ev.target as HTMLInputElement).files?.[0]
  if (!f) return
  return runOperation('import', '', async () => {
  try {
    await api.flowTemplateImport(await f.text())
    ElMessage.success(tr('导入成功'))
    await reload(); emit('refreshed')
  } catch (e: any) { ElMessage.error(tr('导入失败:') + (e?.response?.data?.error || e.message)) }
  finally { if (fileRef.value) fileRef.value.value = '' }
  })
}
async function delTpl(name: string) {
  return runOperation('delete', name, async () => {
  try { await ElMessageBox.confirm(tr('删除流程模板「{name}」？已套用生成的任务不受影响（印章语义）。', { name }), tr('删除确认'), { type: 'warning' }) } catch { return }
  try { await api.flowTemplateDelete(name); ElMessage.success(tr('已删除')); await reload(); emit('refreshed') }
  catch (e: any) { ElMessage.error(tr('删除失败:') + (e?.response?.data?.error || e.message)) }
  })
}

const issueText: any = { missing: '目标组缺此点(字段已保留,画布可改)', external: '组外引用(原样保留,请人工确认)' }
</script>

<template>
  <el-dialog :model-value="true" :title="$t('MES 流程模板(套用即拷贝,与模板断开)')" width="760px"
             append-to-body :z-index="5200" :close-on-click-modal="!busy" :close-on-press-escape="!busy" :show-close="!busy" @closed="emit('close')">
    <p v-if="busy" role="status" class="tip">{{ $t(loading ? '正在加载流程模板…' : applying ? '正在套用流程模板…' : '正在处理流程模板…') }}</p>
    <div v-if="editable" class="bar">
      <el-button size="small" :loading="operation?.kind === 'import'" :disabled="busy" @click="fileRef?.click()">{{ $t('导入模板 JSON…') }}</el-button>
      <input ref="fileRef" type="file" accept=".json" style="display:none" @change="importTpl" />
      <span class="tip">{{ $t('在画布「模板 ▾ → 当前任务存为流程模板」新增模板') }}</span>
    </div>

    <el-empty v-if="!loading && !list.length" :description="$t('还没有流程模板 — 在画布把配好的任务「存为流程模板」')" :image-size="70" />

    <div v-for="t in list" :key="t.name" class="tpl">
      <div class="row">
        <!-- 流程图缩略预览(画布同款配色;悬停节点看全名) -->
        <svg v-if="mini(t.name)" class="minisvg" :viewBox="`0 0 260 120`">
          <line v-for="(e, i) in mini(t.name)!.edges" :key="'e' + i" :x1="e.x1" :y1="e.y1" :x2="e.x2" :y2="e.y2"
                stroke="#b9c3cf" stroke-width="1.4" />
          <g v-for="n in mini(t.name)!.nodes" :key="n.id">
            <rect :x="n.x" :y="n.y" :width="n.w" :height="n.h" rx="4"
                  :fill="(typeColor[n.type] || '#607D8B') + '1f'" :stroke="typeColor[n.type] || '#607D8B'" stroke-width="1.2">
              <title>{{ typeIcon[n.type] }} {{ n.title || n.type }}</title>
            </rect>
            <text v-if="mini(t.name)!.showText" :x="n.x + n.w / 2" :y="n.y + n.h / 2 + 3" text-anchor="middle"
                  :fill="typeColor[n.type] || '#607D8B'" font-size="9">{{ (n.title || n.type).slice(0, 6) }}</text>
          </g>
        </svg>
        <div v-else class="minisvg miniempty">{{ $t('(空流程)') }}</div>
        <div class="info">
          <div><b>{{ t.name }}</b></div>
          <div class="meta">{{ $t('{nodes} 节点 · {fields} 字段/结构体', { nodes: t.nodeCount, fields: t.fieldCount }) }}</div>
          <div class="meta">{{ $t('源组 {group}', { group: t.sourceGroupRef }) }}</div>
          <div v-if="t.description" class="meta desc">{{ t.description }}</div>
        </div>
        <span class="ops">
          <el-button v-if="editable" size="small" type="primary" plain :disabled="busy" @click="startApply(t.name)">{{ applyFor === t.name ? '收起' : '套用…' }}</el-button>
          <el-button size="small" text :loading="operation?.kind === 'export' && operation.name === t.name" :disabled="busy" @click="exportTpl(t.name)">{{ $t('导出') }}</el-button>
          <el-button v-if="editable" size="small" text type="danger" :loading="operation?.kind === 'delete' && operation.name === t.name" :disabled="busy" @click="delTpl(t.name)">{{ $t('删除') }}</el-button>
        </span>
      </div>

      <!-- 套用区:选目标组(单 / 批量) -->
      <div v-if="editable && applyFor === t.name" class="apply">
        <el-radio-group v-model="mode" size="small" :disabled="busy">
          <el-radio-button value="single">{{ $t('套用到一个组') }}</el-radio-button>
          <el-radio-button value="bulk">{{ $t('批量套用') }}</el-radio-button>
        </el-radio-group>

        <template v-if="mode === 'single'">
          <el-select v-model="targetGroup" :disabled="busy" filterable :placeholder="$t('选择目标组')" size="small" style="width: 320px">
            <el-option v-for="g in mainGroups" :key="g.path" :value="g.path"
                       :label="g.path + (g.hasFlow ? $t('（已有流程，将覆盖）') : '')" />
          </el-select>
        </template>
        <template v-else>
          <el-select v-model="tplFilter" :disabled="busy" clearable :placeholder="$t('按设备模板过滤(可选)')" size="small" style="width: 200px">
            <el-option v-for="n in deviceTemplates" :key="n" :value="n" :label="'设备模板:' + n" />
          </el-select>
          <el-select v-model="targetGroups" :disabled="busy" multiple filterable collapse-tags :max-collapse-tags="6"
                     :placeholder="$t('勾选目标组(可多选)')" size="small" style="width: 380px">
            <el-option v-for="g in candidates" :key="g.path" :value="g.path" :disabled="g.hasFlow"
                       :label="g.path + (g.hasFlow ? $t('(已有流程)') : '')" />
          </el-select>
          <el-button size="small" text type="primary" :disabled="busy" @click="selectAllFree">{{ $t('全选无流程组({count})', { count: candidates.filter(g => !g.hasFlow).length }) }}</el-button>
        </template>

        <el-button size="small" type="primary" :loading="applying" @click="doApply">{{ $t('执行套用') }}</el-button>

        <!-- 套用报告:missing/external 不静默 -->
        <div v-for="r in reports" :key="r.group" class="report">
          <div class="rhead">✔ {{ r.group }} → 任务「{{ r.taskName }}」<template v-if="!r.issues?.length">{{ $t('(全部引用就绪)') }}</template></div>
          <div v-for="(i, idx) in r.issues" :key="idx" class="issue">
            <el-tag size="small" :type="i.kind === 'missing' ? 'danger' : 'warning'" effect="plain">{{ i.kind === 'missing' ? '缺失' : '组外' }}</el-tag>
            <code>{{ i.ref }}</code>
            <span class="ikey">({{ i.key }})</span>
            <span class="itip">{{ issueText[i.kind] }}</span>
          </div>
        </div>
      </div>
    </div>
  </el-dialog>
</template>

<style scoped>
.bar { display: flex; align-items: center; gap: 10px; margin-bottom: 10px; }
.tip { color: #999; font-size: 12px; }
.tpl { border: 1px solid #e9edf2; border-radius: 8px; padding: 10px 12px; margin-bottom: 8px; }
.row { display: flex; align-items: center; gap: 12px; }
.minisvg { width: 260px; height: 120px; flex: none; background: #fafbfd; border: 1px solid #edf0f4; border-radius: 6px; }
.miniempty { display: flex; align-items: center; justify-content: center; color: #bbb; font-size: 12px; }
.info { flex: 1; min-width: 0; display: flex; flex-direction: column; gap: 2px; }
.meta { color: #888; font-size: 12px; }
.desc { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.ops { margin-left: auto; display: flex; gap: 2px; flex: none; }
.apply { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; margin-top: 10px; padding-top: 10px; border-top: 1px dashed #e5e9ef; }
.report { width: 100%; background: #f8fafc; border-radius: 6px; padding: 8px 10px; margin-top: 6px; font-size: 12px; }
.rhead { font-weight: 600; margin-bottom: 4px; }
.issue { display: flex; align-items: center; gap: 6px; padding: 2px 0; }
.issue code { background: #eef1f5; border-radius: 4px; padding: 0 6px; }
.ikey { color: #aaa; }
.itip { color: #888; }
</style>
