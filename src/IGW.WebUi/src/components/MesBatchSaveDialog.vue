<script setup lang="ts">
import { computed, onBeforeUnmount, ref, shallowRef, watch } from 'vue'
import { projectApi } from '../features/project/api'
import type { ProjectMesTaskRowDto } from '../features/project/types'
import { useI18n } from '../i18n'

export interface MesBatchPeriodChange { periodTaskIds: string[]; collectIntervalMs?: number }
const props = defineProps<{
  modelValue: boolean
  revision: string
  tasks: any[]
  saveBatch: (change: MesBatchPeriodChange, revision: string) => Promise<void>
}>()
const emit = defineEmits<{ 'update:modelValue': [value: boolean] }>()
const { t } = useI18n()
const rows = shallowRef<ProjectMesTaskRowDto[]>([])
const total = ref(0)
const page = ref(1)
const pageSize = 50
const search = ref('')
const loading = ref(false)
const saving = ref(false)
const selecting = ref(false)
let dialogSession = 0
const loaded = ref(false)
const error = ref('')
const success = ref('')
const period = ref<number | undefined>(200)
const selected = shallowRef(new Map<string, string>())
const busy = computed(() => loading.value || saving.value)
const draftById = computed(() => new Map(props.tasks.map(task => [String(task.id), task])))
const displayRows = computed(() => rows.value.map(row => {
  const draft = draftById.value.get(row.id)
  return { ...row, name: draft?.name ?? row.name, enabled: draft?.enabled ?? row.enabled,
    collectIntervalMs: draft?.collectIntervalMs ?? row.collectIntervalMs }
}))
const validPeriod = computed(() => Number.isInteger(period.value) && period.value! >= 50 && period.value! <= 3600000)
let controller: AbortController | null = null
let generation = 0

function cancelLoad() { generation++; controller?.abort(); controller = null; loading.value = false; selecting.value = false }
function checkRevision() {
  if (projectApi.revision() !== props.revision)
    throw new Error(t('工程配置已变化，请关闭窗口并重新载入采集流程后重试。'))
}
function failure(reason: any) {
  return reason?.response?.data?.detail || reason?.response?.data?.error || reason?.message || t('批量保存失败')
}
async function load() {
  cancelLoad()
  const current = generation
  const request = new AbortController()
  controller = request; loading.value = true; error.value = ''; loaded.value = false
  try {
    const result = await projectApi.mesTasks({ scope: 'all', q: search.value.trim() || undefined,
      offset: (page.value - 1) * pageSize, limit: pageSize }, request.signal)
    if (current !== generation) return
    checkRevision()
    rows.value = result.items; total.value = result.total; loaded.value = true
  } catch (reason) { if (current === generation && !request.signal.aborted) error.value = failure(reason) }
  finally { if (current === generation) loading.value = false }
}
function query() { if (busy.value) return; page.value = 1; void load() }
function toggle(row: ProjectMesTaskRowDto, checked: unknown) {
  const next = new Map(selected.value)
  if (checked === true) next.set(row.id, `${row.deviceRef || ''} · ${row.name}`)
  else next.delete(row.id)
  selected.value = next
}
async function selectFiltered() {
  if (busy.value) return
  cancelLoad()
  selecting.value = true
  const current = generation
  const request = new AbortController()
  controller = request; loading.value = true; error.value = ''
  try {
    const next = new Map(selected.value)
    let offset = 0
    while (true) {
      const result = await projectApi.mesTasks({ scope: 'all', q: search.value.trim() || undefined, offset, limit: 200 }, request.signal)
      if (current !== generation) return
      checkRevision()
      for (const row of result.items) next.set(row.id, `${row.deviceRef || ''} · ${row.name}`)
      offset += result.items.length
      if (offset >= result.total) break
      if (!result.items.length) throw new Error(t('流程列表未完整加载，请重新查询。'))
    }
    selected.value = next
  } catch (reason) { if (current === generation && !request.signal.aborted) error.value = failure(reason) }
  finally { if (current === generation) { loading.value = false; selecting.value = false } }
}
async function save(changePeriod: boolean) {
  if (busy.value || !loaded.value || (changePeriod && (!selected.value.size || !validPeriod.value))) return
  saving.value = true; error.value = ''; success.value = ''
  const session = dialogSession
  const change: MesBatchPeriodChange = { periodTaskIds: changePeriod ? [...selected.value.keys()] : [] }
  if (changePeriod) change.collectIntervalMs = period.value
  try {
    await props.saveBatch(change, props.revision)
    if (session !== dialogSession || !props.modelValue) return
    success.value = changePeriod
      ? t('全部流程已保存，已更新 {count} 个流程的采集周期。', { count: change.periodTaskIds.length })
      : t('全部流程已保存，各流程周期和启用状态保持原配置。')
    await load()
  } catch (reason) { if (session === dialogSession && props.modelValue) error.value = failure(reason) }
  finally { saving.value = false }
}
watch(() => props.modelValue, open => {
  dialogSession++
  if (!open) { cancelLoad(); return }
  page.value = 1; search.value = ''; selected.value = new Map(); success.value = ''; void load()
}, { immediate: true })
onBeforeUnmount(cancelLoad)
</script>

<template>
  <el-dialog :model-value="modelValue" :title="t('流程批量管理')" width="920px" top="5vh" append-to-body
    :close-on-click-modal="!busy" :close-on-press-escape="!busy" :show-close="!busy"
    @update:model-value="!busy && emit('update:modelValue', $event)">
    <p class="batch-hint">{{ t('保存全部会提交本窗口已编辑的流程配置；批量修改周期只影响勾选项，不改变启用状态。') }}</p>
    <p v-if="busy" role="status" class="batch-hint">{{ t(saving ? '正在保存流程，请稍候…' : selecting ? '正在选择全部筛选结果…' : '正在加载流程列表…') }}</p>
    <div class="batch-search">
      <el-input v-model="search" :placeholder="t('搜索流程名称、设备或组路径')" :aria-label="t('搜索流程')" :disabled="busy" @keyup.enter="query" />
      <el-button :loading="loading && !selecting" :disabled="busy" @click="query">{{ t('查询') }}</el-button>
      <span>{{ t('匹配 {count} 个流程', { count: total }) }}</span>
    </div>
    <div class="batch-actions">
      <el-button :loading="selecting" :disabled="busy || !loaded || !total" @click="selectFiltered">{{ t('全选筛选结果') }}</el-button>
      <el-button :disabled="busy || !selected.size" @click="selected = new Map()">{{ t('清空选择') }}</el-button>
      <span>{{ t('已选 {count} 个（跨页保留）', { count: selected.size }) }}</span>
    </div>
    <el-alert v-if="error" :title="error" type="error" :closable="false" show-icon />
    <el-alert v-if="success" :title="success" type="success" :closable="false" show-icon />
    <el-table :data="displayRows" row-key="id" height="min(340px, 36vh)" size="small" v-loading="loading" class="batch-table">
      <el-table-column width="46"><template #default="{ row }"><el-checkbox :model-value="selected.has(row.id)" :aria-label="t('选择流程 {name}', { name: row.name })" :disabled="busy" @change="toggle(row, $event)" /></template></el-table-column>
      <el-table-column prop="name" :label="t('流程名称')" min-width="150" show-overflow-tooltip />
      <el-table-column prop="deviceRef" :label="t('设备')" min-width="135" show-overflow-tooltip />
      <el-table-column prop="groupRef" :label="t('组路径')" min-width="240" show-overflow-tooltip />
      <el-table-column :label="t('当前周期')" width="120"><template #default="{ row }">{{ row.collectIntervalMs ?? '—' }} ms</template></el-table-column>
      <el-table-column :label="t('状态')" width="85"><template #default="{ row }">{{ row.enabled ? t('运行') : t('停用') }}</template></el-table-column>
    </el-table>
    <el-pagination v-model:current-page="page" :page-size="pageSize" :total="total" :disabled="busy" layout="prev, pager, next" @current-change="load" />
    <div class="batch-period">
      <span>{{ t('选中流程统一周期') }}</span>
      <!-- InputNumber caches aria-disabled on mount; rebuild after loading so keyboard and accessibility state agree. -->
      <el-input-number :key="String(busy)" v-model="period" :min="50" :max="3600000" :precision="0" :disabled="busy" :aria-label="t('批量采集周期')" controls-position="right" /><span>ms</span>
      <el-button type="primary" :loading="saving" :disabled="busy || !loaded || !selected.size || !validPeriod" @click="save(true)">{{ t('修改选中周期并保存全部') }}</el-button>
    </div>
    <p class="batch-hint">{{ t('已经丢失的未保存修改无法自动恢复；请勾选对应流程，填写正确周期后保存。') }}</p>
    <template #footer>
      <el-button :disabled="busy" @click="emit('update:modelValue', false)">{{ t('关闭') }}</el-button>
      <el-button type="primary" :loading="saving" :disabled="busy || !loaded" @click="save(false)">{{ t('保存全部流程（保留各自周期）') }}</el-button>
    </template>
  </el-dialog>
</template>

<style scoped>
.batch-hint { color: #64748b; font-size: 12px; line-height: 1.6; margin: 0 0 12px; }
.batch-search,.batch-actions,.batch-period { display: flex; align-items: center; flex-wrap: wrap; gap: 8px; margin-bottom: 12px; }
.batch-search .el-input { max-width: 360px; }.batch-search span,.batch-actions span { font-size: 12px; color: #64748b; }
.batch-table { margin: 8px 0; }.el-pagination { justify-content: flex-end; margin-bottom: 16px; }
.batch-period { padding: 12px; border: 1px solid #dce6f3; background: #f5f9ff; border-radius: 6px; }
</style>
