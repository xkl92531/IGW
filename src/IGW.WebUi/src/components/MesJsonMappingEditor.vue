<script setup lang="ts">
import { computed, ref } from 'vue'
import type { MesJsonMapping } from '../api'
import { mapJson } from '../utils/mesJsonPreview'
import { useI18n } from '../i18n'
import JsonFieldInspector from './JsonFieldInspector.vue'
import type { JsonField } from '../utils/jsonFields'
const { t } = useI18n()
// editable=false：只读账号，输入框禁用、增删和「添加映射」隐藏，样例预览照常可用
const props = withDefaults(defineProps<{ modelValue?: MesJsonMapping[]; sample?: unknown; sampleError?: string; editable?: boolean }>(), { modelValue: () => [], editable: true })
const emit = defineEmits<{ 'update:modelValue': [value: MesJsonMapping[]]; change: [] }>()
const sampleText = ref('{"data":{"serialNo":"DEMO-001","count":2}}')
function update(index: number, key: keyof MesJsonMapping, event: Event) { const rows = props.modelValue.map(row => ({ ...row })); rows[index]![key] = (event.target as HTMLInputElement).value; emit('update:modelValue', rows); emit('change') }
function replace(rows: MesJsonMapping[]) { emit('update:modelValue', rows); emit('change') }
const source = computed(() => { try { if (props.sampleError) throw new Error(props.sampleError); return { value: props.sample !== undefined ? props.sample : JSON.parse(sampleText.value), error: '' } } catch (error) { return { value: null, error: String(error) } } })
const preview = computed(() => { try { if (source.value.error) throw new Error(source.value.error); const value = mapJson(source.value.value, props.modelValue); return { value, text: JSON.stringify(value, null, 2), error: '' } } catch (error) { return { value: null, text: '', error: String(error) } } })
function addField(field: JsonField) {
  const name = /^[A-Za-z_][\w-]*$/.test(field.name) ? field.name : 'value'
  let targetPath = name
  let suffix = 2
  while (props.modelValue.some(row => row.targetPath === targetPath || row.targetPath.startsWith(`${targetPath}.`) || row.targetPath.startsWith(`${targetPath}[`))) targetPath = `${name}${suffix++}`
  replace([...props.modelValue, { sourcePath: field.path, targetPath }])
}
</script>
<template><div class="mapping-editor">
  <div class="mapping-guide"><strong>{{ t('读取接口数据：原始 JSON 路径 → IGW 内部字段') }}</strong><p>{{ t('左侧填接口消息里的字段位置，右侧填在 IGW 中使用的名字。例如 data.serialNo → barcode，会把 data 中的 serialNo 读出来，命名为 barcode。映射后的字段如何使用，由所在流程节点决定。') }}</p><p>{{ t('不确定路径时，展开样例预览，粘贴接口 JSON，点击需要的字段添加映射。此处只配置读取规则，不会修改后台返回的原始消息，也不会直接写 PLC。') }}</p></div>
  <div v-for="(row, index) in modelValue" :key="index" class="mapping-row"><input :aria-label="t('源 JSON 路径')" :placeholder="t('源 JSON 路径')" :value="row.sourcePath" :disabled="!editable" @input="update(index, 'sourcePath', $event)"><span>→</span><input :aria-label="t('内部字段路径')" :placeholder="t('内部字段路径')" :value="row.targetPath" :disabled="!editable" @input="update(index, 'targetPath', $event)"><button v-if="editable" type="button" @click="replace(modelValue.filter((_, i) => i !== index))">{{ t('删除') }}</button></div>
  <button v-if="editable" type="button" @click="replace([...modelValue, { sourcePath: '', targetPath: '' }])">{{ t('添加 JSON 路径映射') }}</button>
  <details><summary>{{ t('本地路径映射预览') }}</summary><div class="hint">{{ t('样例仅用于本地预览，不保存到生产配置；不会发送请求或下发设备命令。') }}</div><textarea v-if="sample === undefined" v-model="sampleText" :aria-label="t('样例响应 JSON')" rows="3" /><div class="hint">{{ t('点击样例字段添加映射，再编辑内部字段路径。添加映射会更新配置。') }}</div><template v-if="!source.error"><div>{{ t('原始 JSON 字段') }}</div><JsonFieldInspector data-inspector="source" :value="source.value" :selectable="editable" @select="addField" /></template><div v-if="preview.error" role="alert">{{ t('JSON 预览错误') }}: {{ preview.error }}</div><template v-else><div>{{ t('映射输出字段') }}</div><JsonFieldInspector data-inspector="mapped" :value="preview.value" /><pre>{{ preview.text }}</pre></template></details>
</div></template>
<style scoped>
.mapping-row { display:flex; gap:5px; margin:5px 0; } input { min-width:0; width:40%; } textarea { width:100%; box-sizing:border-box; } details { margin:7px 0; font-size:12px; } pre { white-space:pre-wrap; overflow-wrap:anywhere; } [role=alert] { color:var(--el-color-danger); }
.mapping-guide{padding:10px 12px;background:var(--el-fill-color-light);border-radius:6px;font-size:12px;line-height:1.8;margin:10px 0}.mapping-guide p{margin:5px 0}
</style>
