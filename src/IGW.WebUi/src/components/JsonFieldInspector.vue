<script setup lang="ts">
import { computed, ref } from 'vue'
import { inspectJsonFields, type JsonField } from '../utils/jsonFields'
import { useI18n } from '../i18n'
const { t } = useI18n()
const props = defineProps<{ value: unknown; selectable?: boolean; editable?: boolean }>()
const emit = defineEmits<{ select: [field: JsonField]; edit: [field: JsonField]; remove: [field: JsonField] }>()
const collapsed = ref(new Set<string>())
const fields = computed(() => {
  try { return { rows: inspectJsonFields(props.value), error: '' } }
  catch (error) { return { rows: [], error: String(error) } }
})
const visible = computed(() => fields.value.rows.filter(row => !row.ancestors.some(path => collapsed.value.has(path))))
function toggle(path: string) { const next = new Set(collapsed.value); if (next.has(path)) next.delete(path); else next.add(path); collapsed.value = next }
function display(field: JsonField) {
  if (field.type === 'array') return `[${(field.value as unknown[]).length}]`
  if (field.type === 'object') return `{${Object.keys(field.value as object).length}}`
  return JSON.stringify(field.value)
}
</script>
<template>
  <div class="json-field-inspector">
    <div v-if="fields.error" role="alert">{{ t('JSON 预览错误') }}: {{ fields.error }}</div>
    <template v-else>
      <div class="field-tools"><button type="button" @click="collapsed = new Set()">{{ t('展开全部') }}</button><button type="button" @click="collapsed = new Set(fields.rows.filter(row => row.expandable).map(row => row.path))">{{ t('折叠全部') }}</button></div>
      <div class="field-scroll"><table>
        <thead><tr><th>{{ t('JSON 字段路径') }}</th><th>{{ t('JSON 字段类型') }}</th><th>{{ t('JSON 字段值') }}</th><th v-if="selectable || editable">{{ t('操作') }}</th></tr></thead>
        <tbody><tr v-for="field in visible" :key="field.path" :data-json-path="field.path">
          <td><div class="field-path" :style="{ paddingLeft: `${Math.min(field.ancestors.length, 8) * 12}px` }"><button v-if="field.expandable" type="button" data-action="toggle-field" :aria-expanded="!collapsed.has(field.path)" :aria-label="field.path" @click="toggle(field.path)">{{ collapsed.has(field.path) ? '▸' : '▾' }}</button><code>{{ field.path }}</code></div></td>
          <td><code>{{ field.type }}</code></td><td><code class="field-value">{{ display(field) }}</code></td>
          <td v-if="selectable || editable" class="field-actions"><button v-if="selectable" type="button" data-action="select-field" @click="emit('select', field)">{{ t('添加映射') }}</button><template v-if="editable"><button type="button" data-action="edit-field" @click="emit('edit', field)">{{ t('编辑') }}</button><button v-if="field.path !== '$'" type="button" data-action="remove-field" @click="emit('remove', field)">{{ t('删除') }}</button></template></td>
        </tr></tbody>
      </table></div>
    </template>
  </div>
</template>
<style scoped>
.json-field-inspector { margin:8px 0; font-size:12px; } .field-tools { display:flex; gap:6px; margin-bottom:5px; } .field-scroll { max-height:320px; overflow:auto; border:1px solid var(--el-border-color); border-radius:4px; } table { border-collapse:collapse; width:100%; text-align:left; } th,td { padding:6px 8px; border-bottom:1px solid var(--el-border-color-lighter); vertical-align:top; } th { position:sticky; top:0; background:var(--el-fill-color-light); z-index:1; } .field-path { display:flex; align-items:flex-start; gap:4px; } .field-path code { overflow-wrap:anywhere; min-width:90px; } .field-value { white-space:pre-wrap; overflow-wrap:anywhere; } .field-actions { white-space:nowrap; } button { cursor:pointer; font-size:12px; color:var(--el-color-primary); background:var(--el-bg-color); border:1px solid var(--el-border-color); border-radius:3px; padding:2px 5px; } .field-actions button + button { margin-left:4px; } [role=alert] { color:var(--el-color-danger); }
</style>
