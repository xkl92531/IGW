<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { paintProjectFeedback } from '../features/project/useProjectOperationFeedback'
import { ElMessage, ElMessageBox } from 'element-plus'
import { useI18n } from '../i18n'

const { t: tr } = useI18n()
const busy = ref(false)
const preparing = ref(false)
let preparation = 0

/** 实例与模板差异面板:逐条列 覆盖/追加/屏蔽(模板值 vs 实例值),支持单条还原、整体还原为纯模板;失效差异(悬空)标示。
 *  就地改传入的 device.delta,emit changed 让父 markDirty(保存走既有 saveApply,增量热重载仅重启本设备)。 */
const props = defineProps<{ modelValue: boolean; device: any; template: any }>()
const emit = defineEmits<{ (e: 'update:modelValue', v: boolean): void; (e: 'changed'): void }>()
watch(() => [props.modelValue, props.device, props.template], async () => {
  const request = ++preparation
  if (!props.modelValue) { preparing.value = false; return }
  preparing.value = true
  try {
    await paintProjectFeedback()
    if (request !== preparation || !props.modelValue) return
    void rows.value
  } finally { if (request === preparation) preparing.value = false }
}, { immediate: true })


// 模板内点路径集(组.子组.点),用于失效(悬空)判定
const tmplPaths = computed<Set<string>>(() => {
  const s = new Set<string>()
  const walk = (tags: any[], groups: any[], parent: string) => {
    for (const t of tags || []) if (t.name) s.add(parent ? parent + '.' + t.name : t.name)
    for (const g of groups || []) walk(g.tags, g.subGroups, parent ? parent + '.' + g.name : g.name)
  }
  if (props.template) walk(props.template.tags, props.template.groups, '')
  return s
})
function tmplPointAt(path: string): any {
  const segs = path.split('.')
  let tags = props.template?.tags || [], groups = props.template?.groups || []
  for (let i = 0; i < segs.length - 1; i++) {
    const g = (groups || []).find((x: any) => x.name === segs[i]); if (!g) return null
    tags = g.tags; groups = g.subGroups
  }
  return (tags || []).find((t: any) => t.name === segs[segs.length - 1]) || null
}

const rows = computed<any[]>(() => {
  const d = props.device?.delta || {}
  const out: any[] = []
  for (const o of d.overrides || []) {
    const tp = tmplPointAt(o.path)
    const parts: string[] = []
    if (o.address != null) parts.push(`地址 ${tp?.address ?? '—'} → ${o.address}`)
    if (o.scanRateMs != null) parts.push(`扫描 ${tp?.scanRateMs ?? '—'} → ${o.scanRateMs}ms`)
    if (o.dataType != null) parts.push(`类型 ${tp?.dataType ?? '—'} → ${o.dataType}`)
    if (o.access != null) parts.push(`访问 → ${o.access}`)
  if (o.scaling != null) parts.push(tr('缩放已改'))
    if (o.engUnit != null) parts.push(`单位 → ${o.engUnit}`)
    out.push({ kind: 'override', type: '覆盖', path: o.path, detail: parts.join('  ·  ') || '(无改动字段)', invalid: !tmplPaths.value.has(o.path), ref: o })
  }
  for (const m of d.masks || [])
    out.push({ kind: 'mask', type: '屏蔽', path: m, detail: '该模板点在此实例不生效', invalid: !tmplPaths.value.has(m), ref: m })
  for (const a of d.adds || []) {
    const p = a.groupPath ? a.groupPath + '.' + a.tag?.name : a.tag?.name
    out.push({ kind: 'add', type: '追加', path: p, detail: `实例专有点 · ${a.tag?.address || ''}`, invalid: false, ref: a })
  }
  return out
})

function typeColor(k: string) { return k === 'override' ? '#1565C0' : k === 'mask' ? '#B8860B' : '#1B7F37' }

function revertOne(row: any) {
  if (busy.value || preparing.value) return
  const d = props.device.delta
  if (row.kind === 'override') d.overrides.splice(d.overrides.indexOf(row.ref), 1)
  else if (row.kind === 'mask') d.masks.splice(d.masks.indexOf(row.ref), 1)
  else if (row.kind === 'add') d.adds.splice(d.adds.indexOf(row.ref), 1)
  emit('changed')
  ElMessage.success(tr('已还原该差异（记得保存并应用）'))
}

async function revertAll() {
  if (busy.value || preparing.value) return
  busy.value = true
  try {
  try { await ElMessageBox.confirm(tr('还原后该设备将完全跟随模板，保存后会重启此设备。确定？'), tr('整体还原'), { type: 'warning', confirmButtonText: tr('整体还原'), cancelButtonText: tr('取消') }) }
  catch { return }
  await paintProjectFeedback()
  props.device.delta = { overrides: [], adds: [], masks: [] }
  emit('changed')
  ElMessage.success(tr('已整体还原为纯模板（记得保存并应用）'))
  } finally { busy.value = false }
}

function close() { if (!busy.value) emit('update:modelValue', false) }
</script>

<template>
  <el-dialog :model-value="modelValue" @update:model-value="close" width="640" append-to-body top="8vh"
             :title="`${$t('与模板差异')} · ${device?.name || ''} → ${template?.name || ''}`">
    <p v-if="preparing" role="status">{{ $t('正在比较模板差异…') }}</p>
    <template v-else>
    <div v-if="!rows.length" class="empty">{{ $t('该实例与模板完全一致,没有差异。') }}</div>
    <template v-else>
      <div class="bar">
        <span>{{ $t('共') }} <b>{{ rows.length }}</b> {{ $t('处差异') }}<span v-if="rows.some(r=>r.invalid)" class="inv">{{ $t('(含失效项,建议清理)') }}</span></span>
        <span class="grow" />
        <el-button size="small" type="warning" plain :loading="busy" @click="revertAll">{{ $t('整体还原为纯模板') }}</el-button>
      </div>
      <el-table :data="rows" size="small" border max-height="360">
        <el-table-column :label="$t('类型')" width="70">
          <template #default="{ row }"><span :style="{ color: typeColor(row.kind), fontWeight: 600 }">{{ row.type }}</span></template>
        </el-table-column>
        <el-table-column :label="$t('点位')" min-width="150">
          <template #default="{ row }">
            <span :class="{ inval: row.invalid }">{{ row.path }}</span>
            <el-tag v-if="row.invalid" size="small" type="danger" effect="plain" style="margin-left:6px">{{ $t('已失效') }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column prop="detail" :label="$t('模板值 → 实例值')" min-width="200" show-overflow-tooltip />
        <el-table-column label="" width="72">
          <template #default="{ row }"><el-button size="small" link type="primary" @click="revertOne(row)">{{ $t('还原') }}</el-button></template>
        </el-table-column>
      </el-table>
    </template>
    </template>
    <p v-if="busy" role="status">{{ $t('正在还原模板差异…') }}</p>
    <template #footer><el-button type="primary" :disabled="busy" @click="close">{{ $t('关闭') }}</el-button></template>
  </el-dialog>
</template>

<style scoped>
.empty { color: #999; font-size: 13px; padding: 16px; text-align: center; }
.bar { display: flex; align-items: center; font-size: 13px; margin-bottom: 8px; }
.bar .grow { flex: 1; }
.bar .inv { color: #C0392B; margin-left: 6px; }
.inval { color: #C0392B; text-decoration: line-through; }
</style>
