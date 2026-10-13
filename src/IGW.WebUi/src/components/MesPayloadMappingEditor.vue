<script setup lang="ts">
import { computed, onBeforeUnmount, reactive, ref, shallowRef, watch } from 'vue'
import { api, type MesPayloadNodeDto, type MesPayloadPreviewResponse } from '../api'

type PreviewRange = MesPayloadPreviewResponse['ranges'][number]
import { useI18n } from '../i18n'

/**
 * 「报文高级设置」里的字段/结构映射：映射的就是「配置数据」选中的内容。
 * 服务端把数据范围编译成报文结构(普通点、结构体、数组分得清清楚楚)，这里逐项改字段名、勾选是否上报。
 * 改名记在 range.names(按原名的结构路径)；不上报：数据块/结构体下的普通点记在 range.excludes(连采集一起省掉)，
 * 数组元素里的成员记在 range.omit(只影响报文)。数组元素的成员改一次对所有元素生效。
 */
const { t } = useI18n()
const props = defineProps<{ payload: any; scopeGroup?: string }>()
const emit = defineEmits<{ change: [] }>()

// 结构和冲突提示分开存：只改名字或勾选时结构不变，树数据保持同一份，展开的数组/结构体不会被收起
const structure = shallowRef<PreviewRange[] | null>(null)
const previewError = ref<string | null>(null)
const loading = ref(false)
const failed = ref('')
const filter = ref('')
const treeRefs = ref<any[]>([])
// 正在输入的字段名：失焦或回车时才写回配置(输入过程中不被重新渲染冲掉)
const drafts = reactive<Record<string, string>>({})
let controller: AbortController | null = null
let timer: ReturnType<typeof setTimeout> | null = null

const ranges = computed<any[]>(() => props.payload?.dataRanges || [])
// 结构只和来源有关；改名、排除变化时也重取一次，用来检查改名后有没有重复字段
const signature = computed(() => JSON.stringify(ranges.value.map((range) => [
  range.sourcePath, range.scope, range.outputPath, range.arrayStart, range.arrayLengths, range.arrayMemberPath,
  range.names, range.excludes, range.omit,
])))
watch(signature, () => {
  if (timer) clearTimeout(timer)
  timer = setTimeout(() => void load(), 300)
}, { immediate: true })
onBeforeUnmount(() => { controller?.abort(); if (timer) clearTimeout(timer) })

async function load() {
  controller?.abort()
  if (!ranges.value.length) { structure.value = null; previewError.value = null; failed.value = ''; return }
  const request = new AbortController()
  controller = request
  loading.value = true
  try {
    const result = await api.mesPayloadPreview({ scopeGroup: props.scopeGroup, ranges: ranges.value }, request.signal)
    if (request.signal.aborted) return
    if (JSON.stringify(structure.value) !== JSON.stringify(result.ranges)) structure.value = result.ranges
    previewError.value = result.error || null
    failed.value = ''
  } catch (error: any) {
    if (!request.signal.aborted) failed.value = error?.response?.data?.error || error?.message || t('加载失败')
  } finally { if (controller === request) loading.value = false }
}

interface Row { id: string; label: string; node: MesPayloadNodeDto; rangeIndex: number; children: Row[] }
function toRows(node: MesPayloadNodeDto, rangeIndex: number): Row {
  return { id: `${rangeIndex}|${node.key}`, label: node.name, node, rangeIndex, children: node.children.map((child) => toRows(child, rangeIndex)) }
}
const sections = computed(() => (structure.value || []).map((item, index) => {
  const range = ranges.value[index]
  // 数据块/结构体范围直接列成员；单个点或数组范围本身就是一项
  const rows = item.root.kind === 'object' && item.root.key === ''
    ? item.root.children.map((child) => toRows(child, index))
    : [toRows(item.root, index)]
  return { item, range, rows, fields: countLeaves(item.root) }
}))
function countLeaves(node: MesPayloadNodeDto): number {
  return node.kind === 'value' ? 1 : node.children.reduce((sum, child) => sum + countLeaves(child), 0) || 1
}
const renamedCount = computed(() => ranges.value.reduce((sum, range) => sum + Object.keys(range.names || {}).length, 0))
const excludedCount = computed(() => ranges.value.reduce((sum, range) => sum + (range.excludes?.length || 0) + (range.omit?.length || 0), 0))

watch(filter, (value) => { for (const tree of treeRefs.value) tree?.filter(value) })
function filterRow(query: string, data: Row) {
  const q = query.trim().toLowerCase()
  return !q || data.label.toLowerCase().includes(q) || nameOf(data).toLowerCase().includes(q)
    || String(data.node.tagId || '').toLowerCase().includes(q)
}

function isRangeRoot(row: Row) { return row.node.key === '' }
function nameOf(row: Row): string {
  const range = ranges.value[row.rangeIndex]
  if (isRangeRoot(row)) return String(range?.outputPath || '').split('.').pop() || row.node.name
  return range?.names?.[row.node.key] ?? ''
}
function rename(row: Row, value: string) {
  const range = ranges.value[row.rangeIndex]
  if (!range) return
  const name = value.trim()
  if (isRangeRoot(row)) {
    // 单个点或整个数组：键名就是输出路径的最后一段
    const segments = String(range.outputPath || '').split('.').filter(Boolean)
    if (!name || !segments.length) return
    segments[segments.length - 1] = name
    range.outputPath = segments.join('.')
    range.outputPathMode = 'Custom'
  } else if (!name || name === row.node.name) {
    if (range.names) { delete range.names[row.node.key]; if (!Object.keys(range.names).length) delete range.names }
  } else {
    range.names = { ...(range.names || {}), [row.node.key]: name }
  }
  emit('change')
}
function commitName(row: Row) {
  const value = drafts[row.id]
  delete drafts[row.id]
  if (value !== undefined) rename(row, value)
}
function canToggle(row: Row) { return row.node.excludable || !!row.node.omittable }
// 普通点记在 excludes，数组元素成员记在 omit
function toggleList(row: Row) { return row.node.excludable ? 'excludes' : 'omit' }
function isUploaded(row: Row) { return !(ranges.value[row.rangeIndex]?.[toggleList(row)] || []).includes(row.node.key) }
function setUploaded(row: Row, uploaded: boolean) {
  const range = ranges.value[row.rangeIndex]
  if (!range) return
  const field = toggleList(row)
  const list: string[] = range[field] ||= []
  const at = list.indexOf(row.node.key)
  if (!uploaded && at < 0) list.push(row.node.key)
  if (uploaded && at >= 0) list.splice(at, 1)
  if (field === 'omit' && !list.length) delete range.omit
  emit('change')
}
function kindText(node: MesPayloadNodeDto) {
  if (node.kind === 'object') return t('结构体')
  if (node.kind === 'array') return t('数组 ×{count}', { count: node.length ?? 0 }) + (node.children.length ? ` · ${t('结构体元素')}` : node.dataType ? ` · ${node.dataType}` : '')
  return node.dataType || t('点')
}
// 按全部节点(含可展开的成员)估高度，展开数组/结构体时不会被遮住；超过 420px 靠虚拟滚动
function countRows(rows: Row[]): number { return rows.reduce((sum, row) => sum + 1 + countRows(row.children), 0) }
function treeHeight(rows: Row[]) { return Math.min(420, Math.max(1, countRows(rows)) * 32 + 8) }
</script>

<template>
  <div class="mapping-editor" data-testid="payload-mapping">
    <div class="mapping-head">
      <strong>{{ $t('字段/结构映射') }}</strong>
      <small>{{ $t('映射的就是「配置数据」里选中的内容；留空沿用点名，改名后报文用新名字。') }}</small>
    </div>
    <el-empty v-if="!ranges.length" :image-size="50" :description="$t('先点「配置数据」选择要上报的数据')" />
    <template v-else>
      <!-- 搜索不算修改：包一层不禁用的表单，外层只读时也能搜 -->
      <el-form :disabled="false" @submit.prevent><div class="mapping-tools">
        <el-input v-model="filter" size="small" clearable :placeholder="$t('搜索字段…')" />
        <span>{{ $t('改名 {renamed} · 不上报 {excluded}', { renamed: renamedCount, excluded: excludedCount }) }}</span>
      </div></el-form>
      <div v-if="loading && !structure" class="hint" role="status">{{ $t('正在读取报文结构…') }}</div>
      <el-alert v-if="failed" type="error" :closable="false" :title="failed" />
      <el-alert v-if="previewError" type="error" :closable="false" :title="previewError" data-testid="mapping-error" />
      <section v-for="(section, index) in sections" :key="section.item.sourcePath" class="mapping-section">
        <div class="section-head">
          <b>{{ section.item.outputPath }}</b>
          <small :title="section.item.sourcePath">{{ section.item.sourcePath }} · {{ $t('{count} 个字段', { count: section.fields }) }}</small>
        </div>
        <el-tree-v2 :ref="(el: any) => (treeRefs[index] = el)" :data="section.rows" :props="{ value: 'id', label: 'label', children: 'children' }"
          :height="treeHeight(section.rows)" :item-size="32" :filter-method="filterRow" :expand-on-click-node="false">
          <template #default="{ data }">
            <div class="map-row" :class="{ off: canToggle(data) && !isUploaded(data) }" data-testid="mapping-row">
              <el-checkbox v-if="canToggle(data)" :model-value="isUploaded(data)" size="small" :title="$t('上报')"
                @change="setUploaded(data, !!$event)" @click.stop />
              <span v-else class="check-space" />
              <span class="orig" :title="data.node.tagId || data.node.key">{{ data.node.name }}</span>
              <b class="kind" :class="data.node.kind">{{ kindText(data.node) }}</b>
              <el-input :model-value="drafts[data.id] ?? nameOf(data)" size="small" class="rename" :placeholder="data.node.name"
                :aria-label="$t('报文字段名')" @update:model-value="drafts[data.id] = String($event)"
                @change="commitName(data)" @click.stop />
            </div>
          </template>
        </el-tree-v2>
      </section>
    </template>
  </div>
</template>

<style scoped>
.mapping-editor { display:grid; gap:8px; }
.mapping-head { display:grid; gap:2px; }
.mapping-head small,.hint { color:#8793a1; font-size:12px; line-height:1.5; }
.mapping-tools { display:flex; align-items:center; gap:10px; }
.mapping-tools .el-input { flex:1; }
.mapping-tools span { color:#8793a1; font-size:12px; white-space:nowrap; }
.mapping-section { border:1px solid #dfe6ee; border-radius:8px; padding:6px 4px 4px; }
.section-head { display:flex; align-items:baseline; gap:8px; padding:0 6px 4px; min-width:0; }
.section-head small { color:#8996a5; font-size:11px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
.map-row { display:flex; align-items:center; gap:6px; flex:1; min-width:0; padding-right:6px; }
.map-row.off .orig,.map-row.off .rename { opacity:.45; }
.check-space { width:14px; flex:none; }
.orig { min-width:0; flex:1; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; font-size:12px; color:#25384c; }
.kind { flex:none; font-size:10px; font-weight:600; border-radius:8px; padding:1px 6px; background:#eef2f6; color:#5b6b7c; }
.kind.object { background:#eaf3ff; color:#2f6db0; }
.kind.array { background:#fff2df; color:#a15c00; }
.rename { width:140px; flex:none; }
</style>
