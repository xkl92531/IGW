<script setup lang="ts">
import { ref, computed, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { paintProjectFeedback } from '../features/project/useProjectOperationFeedback'
import { api } from '../api'
import { useI18n } from '../i18n'
import { dataTypeEditorValue, editableDataTypesForDriver } from '../utils/siemensDataTypes'

const { t: tr } = useI18n()

/** 模板点位编辑器:在工作副本上就地改点(地址/类型/访问/扫描率)、增/删根层点;保存前预告「将重启 N 台引用实例」(6.6)。
 *  改点经服务端 PUT + DeviceSig 传播到全部引用实例(下次应用重启)——「改一次模板 = 改全场设备」。 */
const props = defineProps<{ modelValue: boolean; template: any; proj: any; drivers: any[] }>()
const emit = defineEmits<{ (e: 'update:modelValue', v: boolean): void; (e: 'changed'): void }>()

const TYPES = computed(() => editableDataTypesForDriver(props.template?.driverId))
const ACCESS = [{ v: 'read', l: '只读' }, { v: 'write', l: '只写' }, { v: 'readWrite', l: '读写' }]

const wt = ref<any>(null)   // 工作副本(深拷贝;取消即丢弃)
const busy = ref(false)
const preparing = ref(false)
const confirming = ref(false)
let preparation = 0

// immediate:因组件由父 v-if 挂载,挂载时 modelValue 已为 true,普通 watch 不触发 → 作业副本会为空
function normalizeTagTypes(target: any) {
  for (const tag of target?.tags || []) {
    tag.dataType = dataTypeEditorValue(tag.dataType)
    if (tag.array?.elementType) tag.array.elementType = dataTypeEditorValue(tag.array.elementType)
  }
  for (const group of target?.groups || target?.subGroups || []) normalizeTagTypes(group)
}
watch(() => props.modelValue, async (open) => {
  const request = ++preparation
  if (!open || !props.template) { preparing.value = false; return }
  preparing.value = true
  try {
    await paintProjectFeedback()
    if (request !== preparation || !props.modelValue) return
    wt.value = JSON.parse(JSON.stringify(props.template))
    normalizeTagTypes(wt.value)
  } catch (reason: any) { ElMessage.error(reason?.message || String(reason)) }
  finally { if (request === preparation) preparing.value = false }
}, { immediate: true })

// 扁平化为可编辑行(row.tag 指向工作副本里的实际点对象,row.list 为其所属数组,支持就地改/删)
const rows = computed<any[]>(() => {
  const t = wt.value; if (!t) return []
  const out: any[] = []
  const walkTags = (tags: any[], pp: string) => { for (const tag of tags || []) out.push({ path: pp ? pp + '.' + tag.name : tag.name, tag, list: tags }) }
  const walkGroups = (groups: any[], pp: string) => { for (const g of groups || []) { const gp = pp ? pp + '.' + g.name : g.name; walkTags(g.tags, gp); walkGroups(g.subGroups, gp) } }
  walkTags(t.tags, ''); walkGroups(t.groups, '')
  return out
})

const instanceCount = computed<number>(() =>
  (props.proj?.channels || []).reduce((n: number, c: any) =>
    n + (c.devices || []).filter((d: any) => (d.templateRef || '').toLowerCase() === (props.template?.name || '').toLowerCase()).length, 0))

function addPoint() {
  (wt.value.tags ||= []).push({ id: '', name: '新点' + (wt.value.tags.length + 1), address: '', dataType: 'int16', access: 'read', scanRateMs: 100, deadband: 0, deadbandType: 'none' })
}
function delRow(row: any) { row.list.splice(row.list.indexOf(row.tag), 1) }

async function save() {
  if (busy.value || preparing.value || confirming.value) return
  const t = wt.value; if (!t) return
  if (rows.value.some((r) => !r.tag.name?.trim())) { ElMessage.warning(tr('存在未命名的点')); return }
  const names = rows.value.map((r) => r.path.toLowerCase())
  if (new Set(names).size !== names.length) { ElMessage.warning(tr('存在重复的点路径')); return }
  const n = instanceCount.value
  if (n > 0) {
    confirming.value = true
    try { await ElMessageBox.confirm(tr('本次改动将重启 {count} 台引用该模板的设备（差异保留）。确定应用？', { count: n }), tr('将重启设备'), { type: 'warning', confirmButtonText: tr('应用并重启 {count} 台', { count: n }), cancelButtonText: tr('取消') }) }
    catch { return }
    finally { confirming.value = false }
  }
  busy.value = true
  try {
    await paintProjectFeedback()
    await api.templateUpdate(props.template.name, JSON.stringify(t))
    emit('changed')
    ElMessage.success(n > 0 ? tr('模板已更新，{count} 台实例将跟进', { count: n }) : tr('模板已更新'))
    emit('update:modelValue', false)
  } catch (e: any) { ElMessage.error(tr('保存失败:') + (e?.response?.data?.error || e.message)) }
  finally { busy.value = false }
}
function close() { if (!busy.value) emit('update:modelValue', false) }
</script>

<template>
  <el-dialog :model-value="modelValue" @update:model-value="close" width="820" top="6vh" append-to-body
             :close-on-click-modal="false" :close-on-press-escape="!busy" :show-close="!busy" :title="`${$t('编辑模板点位')} · ${template?.name || ''}`">
    <p v-if="preparing" role="status">{{ $t('正在准备模板编辑器…') }}</p>
    <div v-else class="bar">
      <span>{{ $t('驱动') }} <b>{{ drivers.find(d=>d.driverId===template?.driverId)?.displayName || template?.driverId }}</b></span>
      <span class="sep">·</span>
      <span>{{ $t('{count} 点', { count: rows.length }) }}</span>
      <span v-if="instanceCount" class="warn">· {{ $t('被 {count} 台实例引用，保存会重启它们', { count: instanceCount }) }}</span>
      <span class="grow" />
      <el-button size="small" type="primary" plain @click="addPoint">{{ $t('＋ 加点(根层)') }}</el-button>
    </div>

    <el-table v-if="!preparing" v-loading="busy" :element-loading-text="$t('正在保存模板并应用…')" :data="rows" size="small" border max-height="52vh">
      <el-table-column :label="$t('路径')" min-width="150" show-overflow-tooltip>
        <template #default="{ row }">
          <el-input v-model="row.tag.name" size="small" :title="row.path" />
        </template>
      </el-table-column>
      <el-table-column :label="$t('地址')" width="150">
        <template #default="{ row }"><el-input v-model="row.tag.address" size="small" :placeholder="$t('如 40001')" /></template>
      </el-table-column>
      <el-table-column :label="$t('类型')" width="120">
        <template #default="{ row }">
          <el-select v-model="row.tag.dataType" size="small"><el-option v-for="t in TYPES" :key="t.value" :value="t.value" :label="t.label" /></el-select>
        </template>
      </el-table-column>
      <el-table-column :label="$t('访问')" width="96">
        <template #default="{ row }">
          <el-select v-model="row.tag.access" size="small"><el-option v-for="a in ACCESS" :key="a.v" :value="a.v" :label="a.l" /></el-select>
        </template>
      </el-table-column>
      <el-table-column :label="$t('扫描ms')" width="100">
        <template #default="{ row }"><el-input-number v-model="row.tag.scanRateMs" size="small" :min="10" :max="3600000" controls-position="right" style="width:92px" /></template>
      </el-table-column>
      <el-table-column label="" width="56">
        <template #default="{ row }"><el-button size="small" link type="danger" @click="delRow(row)">{{ $t('删') }}</el-button></template>
      </el-table-column>
      <template #empty><span style="color:#999">{{ $t('还没有点,点右上「加点」') }}</span></template>
    </el-table>

    <p v-if="busy" role="status" aria-live="polite">{{ $t('正在保存模板并应用…') }}</p>
    <template #footer>
      <el-button :disabled="busy" @click="close">{{ $t('取消') }}</el-button>
      <el-button type="primary" :loading="busy || preparing" :disabled="confirming" @click="save">{{ $t('保存并应用') }}</el-button>
    </template>
  </el-dialog>
</template>

<style scoped>
.bar { display: flex; align-items: center; gap: 6px; font-size: 13px; margin-bottom: 8px; }
.bar .sep { color: #ccc; }
.bar .grow { flex: 1; }
.bar .warn { color: #B8860B; }
</style>
