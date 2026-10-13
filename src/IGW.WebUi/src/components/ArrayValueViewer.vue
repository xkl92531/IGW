<script setup lang="ts">
import { computed, onMounted, onBeforeUnmount, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { api, type ArrayInfoDto, type ArraySliceDto } from '../api'
import { indexAt, linearOffset, pageSlice, parseIntegerVector, qualityClass, type ArrayDef } from '../utils/arrayTags'
import { useI18n } from '../i18n'
import { parseArrayWriteInput, pointWriteHint } from '../utils/siemensDataTypes'
import { displayValue, hasNestedMembers, valueChildren } from '../utils/structureTree'

const props = defineProps<{
  tagId: string
  definition: ArrayDef
  writable: boolean
  suspended?: boolean
}>()
const { t } = useI18n()

const loading = ref(false)
const writing = ref(false)
let generation = 0
let disposed = false
const info = ref<ArrayInfoDto | null>(null)
const slice = ref<ArraySliceDto | null>(null)
const offset = ref(0)
const pageSize = ref(100)
const jumpText = ref('')
const error = ref('')
const editOpen = ref(false)
const editMode = ref<'element' | 'slice'>('element')
const editRow = ref<any>(null)
const editJson = ref('')

const rows = computed(() => (slice.value?.values || []).map((value, index) => {
  const indices = indexAt(offset.value + index, info.value?.dimensions || props.definition.dimensions,
    info.value?.lowerBounds || props.definition.lowerBounds)
  const code = Number(slice.value?.qualityCodes[index] || 0)
  return { key: indices.join(','), indices, value, qualityCode: code, quality: qualityClass(code) }
}))
// 元素里套结构体/数组时按树形显示：每个元素一行，点开看成员，成员还是结构体或数组可以继续点开
const nested = computed(() => hasNestedMembers(props.definition))
const structureColumns = computed(() => nested.value ? [] : props.definition.struct?.fields || [])
const treeRows = computed(() => rows.value.map((row) => ({
  ...row, label: `[${row.indices.join(', ')}]`, hasChildren: row.value !== null && typeof row.value === 'object',
})))
// 每次读取换一个 key：懒加载展开的子行会缓存，重新读取后要用新值重新展开
const sliceVersion = ref(0)
function loadChildren(row: any, _node: unknown, resolve: (rows: any[]) => void) {
  const schema = row.indices ? { struct: props.definition.struct, array: null } : { struct: row.struct, array: row.array }
  resolve(valueChildren(row.key, row.value, schema))
}
const pageEnd = computed(() => offset.value + rows.value.length)

async function load(targetOffset = offset.value) {
  if (props.suspended || !props.tagId) return
  const request = ++generation, tagId = props.tagId
  const current = () => !disposed && request === generation && tagId === props.tagId
  loading.value = true
  error.value = ''
  try {
    const nextInfo = await api.arrayInfo(tagId)
    if (!current()) return
    const bounded = Math.max(0, Math.min(targetOffset, Math.max(0, nextInfo.totalElements - 1)))
    const window = pageSlice(bounded, pageSize.value, nextInfo.dimensions, nextInfo.lowerBounds)
    const nextSlice = await api.arraySlice(tagId, window.start, window.length)
    if (!current()) return
    info.value = nextInfo; offset.value = bounded; slice.value = nextSlice; sliceVersion.value++
    if (!jumpText.value) jumpText.value = window.start.join(', ')
  } catch (reason: any) {
    if (!current()) return
    error.value = reason?.response?.data?.error || reason?.message || t('数组读取失败')
    slice.value = null
  } finally {
    if (current()) loading.value = false
  }
}

function jump() {
  if (!info.value) return
  try {
    const indices = parseIntegerVector(jumpText.value, t('索引'))
    load(linearOffset(info.value.dimensions, info.value.lowerBounds, indices))
  } catch (reason: any) { ElMessage.warning(reason?.message || t('索引无效')) }
}

function previousPage() { load(Math.max(0, offset.value - pageSize.value)) }
function nextPage() {
  if (!info.value || pageEnd.value >= info.value.totalElements) return
  load(pageEnd.value)
}

function openElement(row: any) {
  if (!props.writable || writing.value || loading.value) return
  editMode.value = 'element'; editRow.value = row
  editJson.value = JSON.stringify(row.value, null, 2)
  editOpen.value = true
}

function openSlice() {
  if (!props.writable || !slice.value || writing.value || loading.value) return
  editMode.value = 'slice'; editRow.value = null
  editJson.value = JSON.stringify(slice.value.values, null, 2)
  editOpen.value = true
}

async function saveWrite() {
  if (!props.writable || !slice.value || writing.value || loading.value) return
  const parsed = parseArrayWriteInput(editJson.value, info.value?.elementType || props.definition.elementType, editMode.value === 'slice')
  if (parsed.error) { ElMessage.warning(t(parsed.error)); return }
  const value = parsed.value
  const tagId = props.tagId
  writing.value = true
  try {
    if (editMode.value === 'element') {
      const start = editRow.value.indices as number[]
      await api.writeArraySlice(tagId, start, start.map(() => 1), value)
    } else {
      await api.writeArraySlice(tagId, slice.value.start, slice.value.length, value)
    }
    if (disposed || tagId !== props.tagId) return
    editOpen.value = false
    ElMessage.success(t('数组写入完成'))
    await load()
  } catch (reason: any) {
    ElMessage.error(t('数组写入失败：') + (reason?.response?.data?.error || reason?.message || t('未知错误')))
  } finally { writing.value = false }
}

watch(() => props.tagId, () => { generation++; offset.value = 0; jumpText.value = ''; info.value = null; slice.value = null; editOpen.value = false; loading.value = false; load(0) })
watch(() => props.suspended, (suspended) => { if (!suspended) load(); else { generation++; loading.value = false } })
onMounted(() => load(0))
onBeforeUnmount(() => { disposed = true; generation++ })
</script>

<template>
  <section class="array-viewer" v-loading="loading || writing" :element-loading-text="t(writing ? '正在写入 PLC…' : '正在读取数组…')" :aria-busy="loading || writing">
    <header>
      <div>
        <b>{{ $t('数组切片') }}</b>
        <span v-if="info">{{ info.elementType }} · [{{ info.lowerBounds.join(', ') }}] {{ $t('起') }} · {{ info.dimensions.join(' × ') }} · {{ $t('共') }} {{ info.totalElements.toLocaleString() }} {{ $t('项') }}</span>
      </div>
      <div class="quality" v-if="info">
        <i class="good" />Good {{ info.good.toLocaleString() }}
        <i class="uncertain" />Uncertain {{ info.uncertain.toLocaleString() }}
        <i class="bad" />Bad {{ info.bad.toLocaleString() }}
      </div>
    </header>
    <div class="array-tools">
      <span>{{ $t('跳到 PLC 索引') }}</span>
      <el-input v-model="jumpText" size="small" style="width:180px" :placeholder="$t('例如 1, -2')" @keyup.enter="jump" />
      <el-button size="small" @click="jump">{{ $t('定位') }}</el-button>
      <span>{{ $t('每页') }}</span>
      <el-select v-model="pageSize" size="small" style="width:88px" @change="load(offset)">
        <el-option :value="50" label="50" /><el-option :value="100" label="100" /><el-option :value="500" label="500" />
      </el-select>
      <el-button size="small" :disabled="offset <= 0" @click="previousPage">{{ $t('上一页') }}</el-button>
      <el-button size="small" :disabled="!info || pageEnd >= info.totalElements" @click="nextPage">{{ $t('下一页') }}</el-button>
      <span v-if="info" class="range">{{ offset + 1 }}–{{ pageEnd }} / {{ info.totalElements.toLocaleString() }}</span>
      <span class="grow" />
      <el-button size="small" @click="load()">{{ $t('刷新') }}</el-button>
      <el-button v-if="writable" size="small" type="primary" plain :disabled="!slice" @click="openSlice">{{ $t('写当前切片') }}</el-button>
    </div>
    <el-alert v-if="error" :title="error" type="error" :closable="false" show-icon />
    <el-table v-else-if="nested" :key="sliceVersion" :data="treeRows" size="small" height="100%" border row-key="key" lazy
      :load="loadChildren" :tree-props="{ children: 'children', hasChildren: 'hasChildren' }" data-testid="array-tree"
      @row-dblclick="(row: any) => row.indices && openElement(row)">
      <el-table-column :label="$t('PLC 索引 / 成员')" min-width="220" show-overflow-tooltip>
        <template #default="{ row }">{{ row.label }}</template>
      </el-table-column>
      <el-table-column :label="$t('值')" min-width="200" show-overflow-tooltip>
        <template #default="{ row }">{{ displayValue(row.value) }}</template>
      </el-table-column>
      <el-table-column label="Quality" width="105">
        <template #default="{ row }"><span v-if="row.quality" :class="row.quality.toLowerCase()">{{ row.quality }}</span></template>
      </el-table-column>
      <el-table-column v-if="writable" :label="$t('操作')" width="72">
        <template #default="{ row }"><el-button v-if="row.indices" text type="primary" size="small" @click="openElement(row)">{{ $t('写入') }}</el-button></template>
      </el-table-column>
    </el-table>
    <el-table v-else :data="rows" size="small" height="100%" border row-key="key" @row-dblclick="openElement">
      <el-table-column :label="$t('PLC 索引')" width="150">
        <template #default="{ row }">[{{ row.indices.join(', ') }}]</template>
      </el-table-column>
      <template v-if="structureColumns.length">
        <el-table-column v-for="field in structureColumns" :key="field.name" :label="field.jsonName || field.name" min-width="130" show-overflow-tooltip>
          <template #default="{ row }">{{ row.value?.[field.jsonName || field.name] ?? '—' }}</template>
        </el-table-column>
      </template>
      <el-table-column v-else :label="$t('值')" min-width="200" show-overflow-tooltip>
        <template #default="{ row }">{{ typeof row.value === 'object' ? JSON.stringify(row.value) : row.value ?? '—' }}</template>
      </el-table-column>
      <el-table-column label="Quality" width="105">
        <template #default="{ row }"><span :class="row.quality.toLowerCase()">{{ row.quality }}</span></template>
      </el-table-column>
      <el-table-column v-if="writable" :label="$t('操作')" width="72">
        <template #default="{ row }"><el-button text type="primary" size="small" @click="openElement(row)">{{ $t('写入') }}</el-button></template>
      </el-table-column>
    </el-table>

    <el-dialog v-model="editOpen" append-to-body :z-index="5400" width="620px"
      :close-on-click-modal="!writing" :close-on-press-escape="!writing" :show-close="!writing"
      :title="editMode === 'element' ? `${$t('写入元素')} [${editRow?.indices?.join(', ') || ''}]` : $t('写入当前数组切片')">
      <div class="write-hint">{{ $t('按 JSON 输入；字符串需带双引号，结构体需填写对象。写切片时元素数量必须与当前切片一致。') }}</div>
      <div v-if="pointWriteHint(info?.elementType || definition.elementType)" class="write-hint">{{ pointWriteHint(info?.elementType || definition.elementType) }}</div>
      <el-input v-model="editJson" :disabled="writing" type="textarea" :rows="12" spellcheck="false" />
      <p v-if="writing" role="status">{{ t('正在写入 PLC…') }}</p>
      <template #footer><el-button :disabled="writing" @click="editOpen=false">{{ $t('取消') }}</el-button><el-button type="primary" :loading="writing" @click="saveWrite">{{ $t('写入 PLC') }}</el-button></template>
    </el-dialog>
  </section>
</template>

<style scoped>
.array-viewer { height: 100%; min-height: 260px; display: grid; grid-template-rows: auto auto minmax(0, 1fr); gap: 7px; padding: 8px; background: #fff; }
header, .array-tools { display: flex; align-items: center; gap: 9px; }
header { justify-content: space-between; }
header > div:first-child { display: flex; align-items: baseline; gap: 9px; }
header span, .array-tools { color: #6f7d8f; font-size: 12px; }
.quality { display: flex; align-items: center; gap: 5px; font-size: 12px; color: #506074; }
.quality i { width: 8px; height: 8px; border-radius: 50%; margin-left: 5px; }
.quality .good { background: #22a65a; }.quality .uncertain { background: #d89b16; }.quality .bad { background: #d9473f; }
.array-tools { flex-wrap: wrap; }
.array-tools .grow { flex: 1; }.range { color: #45576d; }
.good { color: #168347; }.uncertain { color: #b87d00; }.bad { color: #c7362e; }
.write-hint { margin-bottom: 8px; color: #7b8796; font-size: 12px; }
</style>
