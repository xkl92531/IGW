<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { api, type MesRangeNodeDto } from '../api'
import MesParameterPicker from './MesParameterPicker.vue'
import {
  validateRangeSelection,
  mesArrayLengthConfigError,
  mesConfiguredArrayLengths,
  setMesArrayLengthMode,
  type MesRangeOption,
} from '../utils/mesDataRanges'
import { cloneReactiveValue } from '../utils/reactiveClone'
import { useI18n } from '../i18n'

const { t } = useI18n()

// project 保留给调用方兼容，范围目录一律由服务端按层提供
const props = defineProps<{ modelValue: boolean; project?: any; ranges: any[]; scopeGroup?: string }>()
const emit = defineEmits<{ 'update:modelValue': [boolean]; apply: [any[]] }>()

const PAGE = 200
const draft = ref<any[]>([])
const view = ref<'selected' | 'add'>('selected')
const query = ref('')
const knownOptions = ref(new Map<string, MesRangeOption>())
const hydrating = ref(false)
const loadError = ref('')
const searchResults = ref<MesRangeOption[]>([])
const searchCursor = ref<string | null>(null)
const searchTotal = ref(0)
const searching = ref(false)
const treeRef = ref<any>()
const treeKey = ref(0)
let dialogGeneration = 0
let searchController: AbortController | null = null
let queryTimer: ReturnType<typeof setTimeout> | null = null

function nodeToOption(dto: MesRangeNodeDto): MesRangeOption & { dataType?: string | null; hasChildren: boolean } {
  return {
    sourcePath: dto.sourcePath,
    devicePath: dto.sourcePath.split('.').slice(0, 2).join('.'),
    label: dto.name,
    scope: dto.scope,
    tagCount: dto.tagCount,
    defaultOutputPath: dto.defaultOutputPath,
    children: [],
    leafTags: [],
    searchText: `${dto.sourcePath} ${dto.name}`.toLowerCase(),
    dataType: dto.dataType,
    hasChildren: dto.hasChildren,
    array: dto.scope === 'Array' ? {
      dimensions: [...(dto.dimensions || [])],
      lowerBounds: [...(dto.lowerBounds || [])],
      shapeKind: dto.shapeKind,
      existingElementCount: dto.tagCount,
    } : undefined,
    arrayMembers: (dto.members || []).map((member) => ({
      path: member.path,
      label: member.jsonName || member.path,
      dataType: member.dataType,
    })),
  }
}
function remember(options: MesRangeOption[]) {
  const map = new Map(knownOptions.value)
  options.forEach((option) => map.set(option.sourcePath, option))
  knownOptions.value = map
}

const summary = computed(() => {
  if (!draft.value.length) return t('尚未配置上报数据')
  const tags = draft.value.reduce((sum, range) => {
    const option = knownOptions.value.get(range.sourcePath)
    if (!option) return sum
    return sum + (option.scope === 'Array' && range.arrayLengths?.length
      ? range.arrayLengths.reduce((value: number, length: number) => value * Math.max(0, Number(length)), 1)
      : option.tagCount)
  }, 0)
  return `${draft.value.length} ${t('个数据范围')} · ${t('约 {count} 点', { count: tags })}`
})
const selectedPaths = computed(() => new Set(draft.value.map((range) => String(range.sourcePath).toLowerCase())))
// 数据块 = 通道.设备.组；任务可能绑定到更深的二级组
const blockOf = (path: string) => String(path || '').split('.').slice(0, 3).join('.')
const currentBlock = computed(() => blockOf(props.scopeGroup || ''))
// 树从通道开始，默认展开到任务绑定的数据块，别的设备、别的 DB 也能展开来选
const expandedKeys = computed(() => {
  const segments = String(props.scopeGroup || '').split('.').filter(Boolean)
  return segments.map((_, index) => segments.slice(0, index + 1).join('.'))
})
const isLevel = (option: any) => option?.scope === 'Channel' || option?.scope === 'Device'

watch(() => [props.modelValue, props.scopeGroup] as const, async ([open]) => {
  const generation = ++dialogGeneration
  searchController?.abort()
  if (queryTimer) clearTimeout(queryTimer)
  hydrating.value = false
  if (!open) return
  hydrating.value = true
  loadError.value = ''
  try {
    draft.value = cloneReactiveValue(props.ranges || [])
    view.value = 'selected'
    query.value = ''
    searchResults.value = []
    knownOptions.value = new Map()
    treeKey.value++
    for (const range of draft.value) {
      await loadExactOption(range.sourcePath, generation)
      if (generation !== dialogGeneration) return
    }
    // 配置数据默认就是当前 DB：还没有任何范围时自动选上任务绑定的数据块，用户再按需缩小或增加
    if (!draft.value.length && props.scopeGroup) {
      const root = await api.mesRangeChildren({ scopeGroup: props.scopeGroup, limit: 1 })
      if (generation !== dialogGeneration) return
      const option = root.items[0] ? nodeToOption(root.items[0]) : null
      if (option) { remember([option]); draft.value.push(makeRange(option)) }
    }
    for (const range of draft.value) normalizeArrayRange(range)
  } catch (error: any) {
    if (generation === dialogGeneration) loadError.value = error?.response?.data?.error || error?.message || t('加载失败')
  } finally { if (generation === dialogGeneration) hydrating.value = false }
}, { immediate: true })

// 已选范围可能在别的设备、别的 DB：按它自己所在的数据块查
async function loadExactOption(sourcePath: string, generation = dialogGeneration) {
  const block = blockOf(sourcePath)
  if (block.split('.').length < 3) return
  try {
    const response = await api.mesRangeChildren({ scopeGroup: block, q: sourcePath, limit: 50 })
    if (generation !== dialogGeneration || !props.modelValue) return
    const exact = response.items.find((item) => item.sourcePath.toLowerCase() === sourcePath.toLowerCase())
    if (exact) { remember([nodeToOption(exact)]); return }
    // 数据块本身、或任务绑定的二级组本身不在自己的子项里：按它为根取一次
    const root = await api.mesRangeChildren({ scopeGroup: sourcePath, limit: 1 })
    if (generation !== dialogGeneration || !props.modelValue) return
    if (root.items[0]?.sourcePath.toLowerCase() === sourcePath.toLowerCase()) remember([nodeToOption(root.items[0])])
  } catch { /* 保存时由服务端再做最终校验 */ }
}

function normalizeArrayRange(range: any) {
  const option = knownOptions.value.get(range.sourcePath)
  if (option?.scope !== 'Array') return
  if (!Array.isArray(range.arrayStart) || range.arrayStart.length !== option.array?.dimensions?.length)
    range.arrayStart = [...(option.array?.lowerBounds || option.array?.dimensions?.map(() => 0) || [])]
  range.arrayLengthMode ||= 'Fixed'
  range.arrayLengthTagIds ||= []
  if (range.arrayLengthMode !== 'Tag' && (!Array.isArray(range.arrayLengths) || range.arrayLengths.length !== option.array?.dimensions?.length))
    range.arrayLengths = [...(option.array?.dimensions || [])]
  range.arrayMemberPath ||= ''
}

// ---- 树：点一层才加载一层，每层分页；上万个点的数据块也不一次拉到浏览器 ----
const treeProps = { label: 'label', isLeaf: 'leaf' }
function toTreeData(option: MesRangeOption & { hasChildren?: boolean }) {
  return { id: option.sourcePath, label: option.label, leaf: option.scope === 'Tag' || !option.hasChildren, option }
}
function moreNode(parent: string, cursor: string, loaded: number, total: number) {
  return { id: `${parent}#more`, label: t('加载更多（已显示 {loaded} / {total}）', { loaded, total }), leaf: true, more: { parent, cursor, loaded, total } }
}
async function fetchChildren(parent: string | undefined, cursor?: string) {
  const response = await api.mesRangeChildren({ parent, cursor, limit: PAGE })
  const options = response.items.map(nodeToOption)
  remember(options)
  return { options, response }
}
async function loadNode(node: any, resolve: (data: any[]) => void) {
  if (!props.modelValue) return resolve([])
  try {
    if (node.level === 0) {
      const { options } = await fetchChildren(undefined)
      return resolve(options.map(toTreeData))
    }
    if (node.data?.more) return resolve([])
    const { options, response } = await fetchChildren(node.data.id)
    const data: any[] = options.map(toTreeData)
    if (response.nextCursor) data.push(moreNode(node.data.id, response.nextCursor, options.length, response.total))
    resolve(data)
  } catch (error: any) {
    loadError.value = error?.response?.data?.error || error?.message || t('加载失败')
    resolve([])
  }
}
async function loadMore(data: any) {
  const { parent, cursor, loaded } = data.more
  try {
    const { options, response } = await fetchChildren(parent, cursor)
    for (const option of options) treeRef.value?.insertBefore(toTreeData(option), data.id)
    treeRef.value?.remove(data.id)
    if (response.nextCursor)
      treeRef.value?.append(moreNode(parent, response.nextCursor, loaded + options.length, response.total), parent)
  } catch (error: any) {
    ElMessage.error(error?.response?.data?.error || error?.message || t('加载失败'))
  }
}
function onNodeClick(data: any, node: any) {
  if (data?.more) void loadMore(data)
  else if (isLevel(data?.option)) { if (node.expanded) node.collapse(); else node.expand() }
  else if (data?.option) addOption(data.option)
}

// ---- 搜索：在当前 DB 里按名称/路径找，点也能搜到；别的 DB 在树里展开选 ----
watch(query, () => {
  if (!props.modelValue) return
  if (queryTimer) clearTimeout(queryTimer)
  queryTimer = setTimeout(() => void search(true), 250)
})
async function search(reset: boolean) {
  const q = query.value.trim()
  searchController?.abort()
  if (!q || !currentBlock.value) { searchResults.value = []; searchCursor.value = null; return }
  const request = new AbortController()
  searchController = request
  searching.value = true
  try {
    const response = await api.mesRangeChildren({
      scopeGroup: currentBlock.value, q, cursor: reset ? undefined : searchCursor.value || undefined, limit: PAGE,
    }, request.signal)
    if (request.signal.aborted) return
    const options = response.items.map(nodeToOption)
    remember(options)
    searchResults.value = reset ? options : [...searchResults.value, ...options]
    searchCursor.value = response.nextCursor || null
    searchTotal.value = response.total
  } catch (error: any) {
    if (!request.signal.aborted) ElMessage.error(error?.response?.data?.error || error?.message || t('加载失败'))
  } finally { if (searchController === request) searching.value = false }
}
onBeforeUnmount(() => {
  dialogGeneration++
  searchController?.abort()
  if (queryTimer) clearTimeout(queryTimer)
})

function close() { emit('update:modelValue', false) }
function changeArrayLengthMode(range: any) {
  const dimensions = rangeOption(range)?.array?.dimensions || []
  setMesArrayLengthMode(range, range.arrayLengthMode,
    dimensions.map((_: number, dimension: number) => lengthMaximum(range, dimension)))
}
function makeRange(option: MesRangeOption) {
  const range: any = {
    sourcePath: option.sourcePath,
    scope: option.scope,
    outputPath: option.defaultOutputPath,
    outputPathMode: 'Auto',
    includeMode: 'All',
    excludes: [],
    offlineMode: 'Null',
  }
  if (option.scope === 'Array') {
    range.arrayStart = [...(option.array?.lowerBounds || option.array?.dimensions?.map(() => 0) || [])]
    range.arrayLengths = [...(option.array?.dimensions || [])]
    range.arrayLengthMode = 'Fixed'
    range.arrayLengthTagIds = []
    range.arrayMemberPath = ''
  }
  return range
}
function addOption(option: MesRangeOption) {
  if (isLevel(option)) return
  if (selectedPaths.value.has(option.sourcePath.toLowerCase())) return
  const error = validateRangeSelection(draft.value, option)
  if (error) { ElMessage.warning(t(error)); return }
  draft.value.push(makeRange(option))
  ElMessage.success(t('已添加：{name}', { name: option.label }))
}
function removeRange(range: any) { draft.value.splice(draft.value.indexOf(range), 1) }
function outputChanged(range: any) { range.outputPathMode = 'Custom' }
function resetOutput(range: any) {
  range.outputPathMode = 'Auto'
  range.outputPath = knownOptions.value.get(range.sourcePath)?.defaultOutputPath || ''
}
function rangeOption(range: any) { return knownOptions.value.get(range.sourcePath) }
function scopeLabel(scope: string) {
  return t(scope === 'Array' ? '数组' : scope === 'Struct' ? '结构体' : scope === 'Tag' ? '单个点'
    : scope === 'Channel' ? '通道' : scope === 'Device' ? '设备' : 'DB/数据组')
}
function dimensionMinimum(range: any, dimension: number) {
  return Number(rangeOption(range)?.array?.lowerBounds?.[dimension] ?? 0)
}
function dimensionMaximum(range: any, dimension: number) {
  const option = rangeOption(range)
  return dimensionMinimum(range, dimension) + Number(option?.array?.dimensions?.[dimension] || 1) - 1
}
function lengthMaximum(range: any, dimension: number) {
  return dimensionMaximum(range, dimension) - Number(range.arrayStart?.[dimension] ?? dimensionMinimum(range, dimension)) + 1
}
function apply() {
  if (hydrating.value) return
  const error = validateDraftRanges()
  if (error) { ElMessage.error(error); return }
  emit('apply', cloneReactiveValue(draft.value))
  close()
}

function validateDraftRanges(): string | null {
  for (let index = 0; index < draft.value.length; index++) {
    const range = draft.value[index]
    const option = knownOptions.value.get(range.sourcePath)
    if (!option) return `${t('数据来源不存在')}:${range.sourcePath}`
    const overlap = validateRangeSelection(draft.value.filter((_, position) => position !== index), option)
    if (overlap) return `${range.sourcePath}:${t(overlap)}`
    if (!String(range.outputPath || option.defaultOutputPath || '').trim()) return `${range.sourcePath}:${t('输出路径为空')}`
    if (option.scope !== 'Array') continue
    const dimensions = option.array?.dimensions || []
    const lower = option.array?.lowerBounds || dimensions.map(() => 0)
    const lengthError = mesArrayLengthConfigError(range, dimensions.length)
    if (lengthError) return `${range.sourcePath}:${t(lengthError)}`
    const lengths = mesConfiguredArrayLengths(range, dimensions, lower, range.arrayStart || [])
    if (range.arrayStart?.length !== dimensions.length || lengths.length !== dimensions.length)
      return `${range.sourcePath}:${t('数组切片维度不一致')}`
    for (let dimension = 0; dimension < dimensions.length; dimension++) {
      const start = Number(range.arrayStart[dimension])
      const length = Number(lengths[dimension])
      if (!Number.isInteger(start) || !Number.isInteger(length) || length <= 0 || start < lower[dimension]
        || start + length > lower[dimension] + dimensions[dimension])
        return `${range.sourcePath}:${t('数组切片超出范围')}`
    }
  }
  return null
}
</script>

<template>
  <el-dialog :model-value="modelValue" :title="$t('配置上报数据')" width="880px" append-to-body destroy-on-close @close="close">
    <div v-if="hydrating" role="status" class="hint">{{ $t('正在加载数据范围…') }}</div>
    <el-alert v-if="loadError" type="error" :closable="false" :title="loadError" />
    <div class="range-head">
      <div><strong>{{ $t('已选择的数据范围') }}</strong><small>{{ summary }}</small></div>
      <el-button v-if="view !== 'add'" type="primary" data-testid="range-add" @click="view = 'add'">{{ $t('添加数据范围') }}</el-button>
      <el-button v-else @click="view = 'selected'">{{ $t('返回已选范围') }}</el-button>
    </div>

    <div v-if="view === 'selected'" class="selected-list">
      <el-empty v-if="!draft.length" :description="$t('尚未选择数据：可以整个 DB，也可以只选其中的结构体、数组或单个点')" :image-size="64" />
      <div v-for="range in draft" :key="range.sourcePath" class="range-row" data-testid="range-row">
        <div class="range-main">
          <strong :title="range.sourcePath">{{ range.sourcePath }}</strong>
          <span class="scope">{{ scopeLabel(range.scope) }}</span>
          <span v-if="range.scope !== 'Tag'">{{ $t('{count} 点', { count: rangeOption(range)?.tagCount || 0 }) }}</span>
          <span v-if="(range.excludes?.length || 0) + (range.omit?.length || 0)">{{ $t('已排除 {count} 个字段', { count: (range.excludes?.length || 0) + (range.omit?.length || 0) }) }}</span>
          <span class="offline">{{ $t('来源离线时输出 null') }}</span>
        </div>
        <div class="range-output">
          <label>{{ $t('输出') }}</label>
          <el-input v-model="range.outputPath" size="small" @input="outputChanged(range)" />
          <el-button v-if="range.outputPathMode === 'Custom'" size="small" text @click="resetOutput(range)">{{ $t('恢复自动路径') }}</el-button>
        </div>
        <div v-if="range.scope === 'Array'" class="array-range-config">
          <div class="array-dimension">
            <label>{{ $t('长度来源') }}</label>
            <el-radio-group v-model="range.arrayLengthMode" size="small" @change="changeArrayLengthMode(range)">
              <el-radio-button value="Fixed">{{ $t('手动填写') }}</el-radio-button>
              <el-radio-button value="Tag">{{ $t('PLC 自动获取') }}</el-radio-button>
            </el-radio-group>
          </div>
          <div v-for="(_, dimension) in range.arrayStart" :key="dimension" class="array-dimension">
            <label>{{ $t('第 {dimension} 维', { dimension: dimension + 1 }) }}</label>
            <span>{{ $t('起始') }}</span>
            <el-input-number v-model="range.arrayStart[dimension]" size="small"
              :min="dimensionMinimum(range, dimension)" :max="dimensionMaximum(range, dimension)" controls-position="right" />
            <span>{{ $t('数量') }}</span>
            <MesParameterPicker v-if="range.arrayLengthMode === 'Tag'" v-model="range.arrayLengthTagIds[dimension]"
              :scope-group="range.sourcePath.slice(0, range.sourcePath.lastIndexOf('.'))" kind="tag" access="read"
              :placeholder="$t('选择整数长度点位')" size="small" />
            <el-input-number v-else v-model="range.arrayLengths[dimension]" size="small"
              :min="1" :max="lengthMaximum(range, dimension)" controls-position="right" />
          </div>
          <div v-if="rangeOption(range)?.arrayMembers?.length" class="array-member">
            <label>{{ $t('成员投影') }}</label>
            <el-select v-model="range.arrayMemberPath" size="small" clearable :placeholder="$t('完整结构体元素')">
              <el-option v-for="member in rangeOption(range)?.arrayMembers || []" :key="member.path"
                :value="member.path" :label="`${member.label} · ${member.path}`" />
            </el-select>
          </div>
          <small>{{ $t('按 PLC 实际下界选择切片；留空成员表示上传完整数组元素。') }}</small>
          <small v-if="range.arrayLengthMode === 'Tag'">{{ $t('每次上报读取长度点位；0 上报空数组，读取失败或长度越界则停止本次上报。') }}</small>
        </div>
        <div class="range-actions">
          <el-button size="small" text type="danger" @click="removeRange(range)">{{ $t('移除') }}</el-button>
        </div>
      </div>
      <p class="hint">{{ $t('字段改名、个别字段不上报，在上报节点的「报文高级设置」里逐项调整。') }}</p>
    </div>

    <div v-else class="add-pane">
      <el-input v-model="query" clearable :placeholder="$t('在当前 DB 里搜索结构体、数组或点的名称…')" data-testid="range-search" />
      <div v-if="query.trim()" class="search-results">
        <button v-for="option in searchResults" :key="option.sourcePath" class="source-card"
          :class="{ added: selectedPaths.has(option.sourcePath.toLowerCase()) }" @click="addOption(option)">
          <strong>{{ option.label }} <em>{{ scopeLabel(option.scope) }}</em></strong>
          <span :title="option.sourcePath">{{ option.sourcePath }}</span>
        </button>
        <el-empty v-if="!searching && !searchResults.length" :image-size="50" :description="$t('没有找到')" />
        <div class="page-actions">
          <span>{{ $t('已显示 {loaded} / {total} 项', { loaded: searchResults.length, total: searchTotal }) }}</span>
          <el-button v-if="searchCursor" :loading="searching" @click="search(false)">{{ $t('加载更多') }}</el-button>
        </div>
      </div>
      <el-tree v-else :key="treeKey" ref="treeRef" class="range-tree" lazy :load="loadNode" :props="treeProps" node-key="id"
        :default-expanded-keys="expandedKeys" :expand-on-click-node="false" @node-click="onNodeClick">
        <template #default="{ data }">
          <span v-if="data.more" class="tree-more">{{ data.label }}</span>
          <span v-else class="tree-range">
            <b class="badge" :class="data.option.scope">{{ scopeLabel(data.option.scope) }}</b>
            <strong>{{ data.label }}</strong>
            <small>{{ data.option.scope === 'Tag' ? data.option.dataType
              : data.option.scope === 'Array' ? `[${(data.option.array?.dimensions || []).join('×')}] · ${$t('{count} 点', { count: data.option.tagCount })}`
              : $t('{count} 点', { count: data.option.tagCount }) }}</small>
            <em v-if="isLevel(data.option)" />
            <em v-else-if="selectedPaths.has(data.id.toLowerCase())">{{ $t('已添加') }}</em>
            <el-button v-else size="small" text type="primary" @click.stop="addOption(data.option)">{{ $t('添加') }}</el-button>
          </span>
        </template>
      </el-tree>
      <p class="hint">{{ $t('从通道、设备一层层展开，默认展开到当前 DB，别的设备、别的 DB 也能选；点「添加」选中整个 DB、结构体、数组或单个点。数组会自动识别起止下标。') }}</p>
    </div>

    <template #footer>
      <el-button @click="close">{{ $t('取消') }}</el-button>
      <el-button type="primary" :disabled="hydrating" @click="apply">{{ $t('应用') }}</el-button>
    </template>
  </el-dialog>
</template>

<style scoped>
.range-head { display:flex; align-items:center; justify-content:space-between; margin-bottom:12px; }
.range-head > div { display:flex; flex-direction:column; gap:3px; }
.range-head small,.hint { color:#8793a1; font-size:12px; }
.selected-list { min-height:330px; max-height:520px; overflow:auto; }
.range-row { border:1px solid #dce4ee; border-radius:9px; padding:11px 12px; margin-bottom:9px; background:#fbfcfe; }
.range-main { display:flex; align-items:center; gap:10px; font-size:12px; color:#667587; }
.range-main strong { flex:1; color:#25384c; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
.range-main .scope { color:#2f6db0; background:#eaf3ff; border-radius:10px; padding:1px 7px; }
.offline { color:#996600; background:#fff5d8; border-radius:10px; padding:2px 7px; }
.range-output { display:flex; align-items:center; gap:8px; margin-top:9px; }
.range-output label { width:36px; color:#7b8795; font-size:12px; }
.range-output .el-input { flex:1; }
.range-actions { display:flex; justify-content:flex-end; margin-top:8px; }
.array-range-config { margin-top:9px; padding:9px 10px; border-radius:7px; background:#f2f7fc; }
.array-dimension,.array-member { display:flex; align-items:center; gap:7px; margin-bottom:7px; font-size:12px; color:#667587; }
.array-dimension label,.array-member label { width:64px; color:#34495e; }
.array-dimension .el-input-number { width:112px; }
.array-member .el-select { flex:1; }
.array-range-config > small { color:#8793a1; }
.add-pane .el-input { margin-bottom:10px; }
.range-tree { height:420px; overflow:auto; border:1px solid #e3e9f0; border-radius:8px; padding:4px 0; }
.range-tree :deep(.el-tree-node__content) { height:34px; }
.tree-range { display:flex; align-items:center; gap:8px; min-width:0; flex:1; padding-right:8px; }
.tree-range strong { color:#25384c; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
.tree-range small { color:#8996a5; font-size:11px; flex:1; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
.tree-range em { color:#1b7f37; font-size:11px; font-style:normal; }
.badge { font-weight:600; font-size:10px; border-radius:8px; padding:1px 6px; background:#eef2f6; color:#5b6b7c; flex:none; }
.badge.Tag { background:#eef6ea; color:#2f7a2c; }
.badge.Array { background:#fff2df; color:#a15c00; }
.badge.Struct { background:#eaf3ff; color:#2f6db0; }
.badge.Channel,.badge.Device { background:#f1edf8; color:#6a4a9c; }
.tree-more { color:#409eff; font-size:12px; }
.search-results { height:420px; overflow:auto; display:grid; align-content:start; grid-template-columns:1fr 1fr; gap:8px; }
.source-card { border:1px solid #d9e3ef; border-radius:8px; background:white; padding:9px 10px; text-align:left; cursor:pointer; }
.source-card:hover { border-color:#409eff; background:#f2f8ff; }
.source-card.added { border-color:#b7dfc3; background:#f3fbf5; }
.source-card strong,.source-card span { display:block; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
.source-card strong em { font-style:normal; font-weight:500; font-size:11px; color:#2f6db0; margin-left:4px; }
.source-card span { color:#8996a5; font-size:11px; margin-top:2px; }
.page-actions { grid-column:1 / -1; display:flex; align-items:center; justify-content:space-between; min-height:34px; color:#8793a1; font-size:12px; }
</style>
