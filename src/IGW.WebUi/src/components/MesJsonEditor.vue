<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from '../i18n'
import { renderJsonTemplate } from '../utils/mesJsonPreview'
import { changeJsonField, jsonFieldType, parseJsonFieldValue, type JsonField, type JsonFieldType } from '../utils/jsonFields'
import JsonFieldInspector from './JsonFieldInspector.vue'
const { t } = useI18n()
const props = withDefaults(defineProps<{ modelValue?: string; label?: string; context?: unknown; contextError?: string; disabled?: boolean; argumentArray?: boolean }>(), { modelValue: '', label: '' })
const emit = defineEmits<{ 'update:modelValue': [value: string]; change: [] }>()
const sample = ref('{"input":{"data":{"serialNo":"DEMO-001","count":2}},"mapped":{},"fields":{"Torque":12.5},"vars":{}}')
const source = computed(() => {
  try { if (props.contextError) throw new Error(props.contextError); return { value: props.context !== undefined ? props.context : JSON.parse(sample.value), error: '' } }
  catch (error) { return { value: null, error: String(error) } }
})
const template = computed(() => {
  try { return { value: JSON.parse(props.modelValue.trim() || (props.argumentArray ? '[]' : '{}')), error: '' } }
  catch (error) { return { value: null, error: String(error) } }
})
const preview = computed(() => {
  if (source.value.error) return { value: null, text: '', error: source.value.error }
  if (!props.modelValue.trim()) return { value: null, text: '', error: '' }
  try {
    const rendered = renderJsonTemplate(props.modelValue, source.value.value)
    if (props.argumentArray && !Array.isArray(rendered)) throw new Error(t('SignalR 模板必须是 JSON 参数数组'))
    return { value: rendered, text: JSON.stringify(rendered, null, 2), error: '' }
  }
  catch (error) { return { value: null, text: '', error: error instanceof Error ? error.message : String(error) } }
})
function update(event: Event) { emit('update:modelValue', (event.target as HTMLTextAreaElement).value); emit('change') }
const fieldPath = ref('')
const fieldType = ref<JsonFieldType>('string')
const fieldValue = ref('')
const editingPath = ref<string | null>(null)
const fieldError = ref('')
function resetField() { editingPath.value = null; fieldPath.value = ''; fieldType.value = 'string'; fieldValue.value = ''; fieldError.value = '' }
function editField(field: JsonField) { editingPath.value = field.path; fieldPath.value = field.path; fieldType.value = jsonFieldType(field.value); fieldValue.value = field.type === 'string' ? String(field.value) : JSON.stringify(field.value, null, 2); fieldError.value = '' }
function changeField(operation: 'add' | 'replace' | 'remove', path: string, value?: unknown) {
  if (props.disabled) return
  if (template.value.error) throw new Error(template.value.error)
  const next = changeJsonField(template.value.value, path, value, operation)
  if (props.argumentArray && !Array.isArray(next)) throw new Error(t('SignalR 模板必须是 JSON 参数数组'))
  emit('update:modelValue', JSON.stringify(next, null, 2)); emit('change'); resetField()
}
function applyField() {
  try { changeField(editingPath.value === null ? 'add' : 'replace', fieldPath.value, parseJsonFieldValue(fieldType.value, fieldValue.value)) }
  catch (error) { fieldError.value = String(error) }
}
function removeField(field: JsonField) { try { changeField('remove', field.path) } catch (error) { fieldError.value = String(error) } }
</script>
<template>
  <div class="mes-json-editor">
    <div v-if="!disabled" class="json-guide">
      <strong>{{ t('发送内容：把业务数据组织成接口要求的 JSON') }}</strong>
      <p>{{ argumentArray ? t('SignalR 调用需要参数数组。例如方法接收一个对象参数时，填写 [{"station":"OP010"}]。方法名在上方单独配置；动态值只能引用本区域列出的变量。') : context === undefined ? t('例如后台需要 torque 字段，可填写 {"torque":"${fields.Torque}"}。字段名按后台接口要求填写，值可用固定内容或当前上下文提供的变量。') : t('例如填写 {"station":"OP010"} 发送固定工位号。动态值只能引用本区域列出的变量；展开本地预览可查看可用字段。') }}</p>
      <p>{{ t('配置方法：直接编辑下方 JSON，或展开“模板字段编辑”逐项添加；再用“本地 JSON 预览”检查替换变量后实际生成的内容。保存流程后才会生效。') }}</p>
    </div>
    <label>{{ label || t('自定义 JSON 模板') }}<textarea :aria-label="label || t('自定义 JSON 模板')" :value="modelValue" :disabled="disabled" rows="4" spellcheck="false" @input="update" /></label>
    <details v-if="!disabled" class="structured-editor"><summary>{{ t('模板字段编辑（写入配置）') }}</summary>
      <div v-if="template.error" role="alert">{{ t('JSON 预览错误') }}: {{ template.error }}</div>
      <JsonFieldInspector v-else data-inspector="template" :value="template.value" editable @edit="editField" @remove="removeField" />
      <div class="hint">{{ t('添加字段使用点分路径和数组索引，例如 data.count、items[0].ok。字符串值可填写 ${input.data.count}。编辑或删除字段会更新模板配置。') }}</div>
      <div class="field-form">
        <label>{{ t('JSON 字段路径') }}<input v-model="fieldPath" :readonly="editingPath !== null" :aria-label="t('JSON 字段路径')" placeholder="data.count"></label>
        <label>{{ t('JSON 字段类型') }}<select v-model="fieldType" :aria-label="t('JSON 字段类型')"><option v-for="type in ['string','number','boolean','null','object','array']" :key="type">{{ type }}</option></select></label>
      </div>
      <label>{{ t('JSON 字段值') }}<textarea v-model="fieldValue" :disabled="fieldType === 'null'" :aria-label="t('JSON 字段值')" rows="2" spellcheck="false" /></label>
      <div class="field-buttons"><button type="button" data-action="apply-field" :disabled="Boolean(template.error)" @click="applyField">{{ editingPath === null ? t('添加字段') : t('应用字段修改') }}</button><button v-if="editingPath !== null" type="button" @click="resetField">{{ t('取消') }}</button></div>
      <div v-if="fieldError" role="alert">{{ t('JSON 字段编辑错误') }}: {{ fieldError }}</div>
    </details>
    <div v-if="context === undefined" class="hint">{{ t('完整占位符保留数值、布尔和对象类型，例如 "${fields.Torque}"。支持 input、mapped、fields、vars；旧报文模板仍可保留。') }}</div>
    <div v-else class="hint">{{ t('完整字符串占位符保留原生 JSON 类型；嵌入文本的占位符转为字符串。可用路径以本区域上下文说明为准。') }}</div>
    <details v-if="!disabled"><summary>{{ t('本地 JSON 预览（不发送请求）') }}</summary>
      <div class="hint">{{ t('样例仅用于本地预览，不保存到生产配置；不会发送请求或下发设备命令。') }}</div>
      <label v-if="context === undefined">{{ t('样例上下文 JSON') }}<textarea v-model="sample" :aria-label="t('样例上下文 JSON')" rows="4" spellcheck="false" /></label>
      <template v-if="!source.error"><div>{{ t('输入上下文字段') }}</div><JsonFieldInspector data-inspector="context" :value="source.value" /></template>
      <div v-if="preview.error" role="alert">{{ t('JSON 预览错误') }}: {{ preview.error }}</div>
      <template v-else><template v-if="preview.text"><div>{{ t('渲染输出字段') }}</div><JsonFieldInspector data-inspector="rendered" :value="preview.value" /></template><pre>{{ preview.text || t('留空时使用原有报文配置') }}</pre></template>
    </details>
  </div>
</template>
<style scoped>
.mes-json-editor { width:100%; margin:10px 0; } label { display:block; } textarea { display:block; box-sizing:border-box; width:100%; resize:vertical; font:12px/1.5 monospace; padding:7px; border:1px solid var(--el-border-color); border-radius:4px; color:var(--el-text-color-primary); background:var(--el-bg-color); } .hint, summary { font-size:12px; margin:5px 0; color:var(--el-text-color-secondary); } summary { cursor:pointer; } pre { white-space:pre-wrap; overflow-wrap:anywhere; } [role=alert] { color:var(--el-color-danger); }
.field-form { display:flex; gap:8px; } .field-form label:first-child { flex:1; } .field-form input { width:100%; box-sizing:border-box; } .field-form input, select { padding:5px; color:var(--el-text-color-primary); background:var(--el-bg-color); border:1px solid var(--el-border-color); border-radius:4px; } .field-buttons { display:flex; gap:6px; margin:6px 0; } button { cursor:pointer; } .structured-editor { padding:6px; border:1px solid var(--el-border-color); border-radius:4px; margin:6px 0; }
.json-guide{padding:10px 12px;background:var(--el-fill-color-light);border-radius:6px;font-size:12px;line-height:1.8;margin-bottom:10px}.json-guide p{margin:5px 0}
</style>
