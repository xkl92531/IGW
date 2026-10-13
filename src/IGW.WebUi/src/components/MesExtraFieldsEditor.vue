<script setup lang="ts">
import { computed, ref } from 'vue'
import MesParameterPicker from './MesParameterPicker.vue'
import { previewExtraFields, validateExtraFields, type MesExtraField } from '../utils/mesExtraFields'
import { useI18n } from '../i18n'
const { t } = useI18n()
// editable=false：只读账号，隐藏增删
const props = withDefaults(defineProps<{ modelValue?: MesExtraField[]; scopeGroup?: string; payload?: any; editable?: boolean }>(), { modelValue: () => [], scopeGroup: '', editable: true })
const emit = defineEmits<{ 'update:modelValue': [value: MesExtraField[]]; change: [] }>()
function replace(rows: MesExtraField[]) { emit('update:modelValue', rows); emit('change') }
function update(index: number, key: keyof MesExtraField, value: unknown) { replace(props.modelValue.map((row, i) => i === index ? { ...row, [key]: value } : row)) }
const error = computed(() => { try { validateExtraFields({ ...props.payload, extraFields: props.modelValue }); return '' } catch (e) { return String(e) } })
const sample = ref('{"input":{"orderNo":"ORDER-001"},"tags":{}}')
const preview = computed(() => { try { return JSON.stringify(previewExtraFields(props.modelValue, JSON.parse(sample.value)), null, 2) } catch (e) { return String(e) } })
</script>
<template>
  <section class="extra-fields">
    <strong>{{ t('业务字段') }}</strong>
    <p>{{ t('固定值按类型填写；点位选择实时采集值；Input 读取原始接口消息。必填字段缺失或类型不符会阻止发送并保留事件。') }}</p>
    <div v-for="(row, index) in modelValue" :key="index" class="extra-row">
      <el-input :model-value="row.name" :aria-label="t('输出字段路径')" placeholder="product.code" @update:model-value="update(index, 'name', $event)" />
      <el-select :model-value="row.dataType" :aria-label="t('数据类型')" @update:model-value="update(index, 'dataType', $event)"><el-option v-for="type in ['string', 'int32', 'double', 'bool', 'json']" :key="type" :value="type" :label="type" /></el-select>
      <el-select :model-value="row.source" :aria-label="t('字段来源')" @update:model-value="update(index, 'source', $event)"><el-option v-for="source in ['Fixed', 'Tag', 'Input']" :key="source" :value="source" :label="source" /></el-select>
      <el-checkbox :model-value="row.required" @update:model-value="update(index, 'required', $event)">{{ t('必填') }}</el-checkbox>
      <el-button v-if="editable" text type="danger" @click="replace(modelValue.filter((_, i) => i !== index))">{{ t('删除') }}</el-button>
      <MesParameterPicker v-if="row.source === 'Tag'" class="value" :model-value="row.path" :scope-group="scopeGroup" kind="tag" access="read" :placeholder="t('点位完整路径')" @update:model-value="update(index, 'path', $event)" />
      <el-input v-else class="value" :model-value="row.source === 'Fixed' ? row.value : row.path" :aria-label="row.source === 'Fixed' ? t('固定值') : t('源 JSON 路径')" :placeholder="row.source === 'Fixed' ? t('固定值') : 'orderNo'" @update:model-value="update(index, row.source === 'Fixed' ? 'value' : 'path', $event)" />
    </div>
    <el-button v-if="editable" size="small" @click="replace([...modelValue, { name: '', dataType: 'string', source: 'Fixed', value: '', path: '', required: false }])">{{ t('添加业务字段') }}</el-button>
    <p v-if="error" role="alert">{{ error }}</p>
    <details><summary>{{ t('业务字段样例预览') }}</summary><p>{{ t('此处使用样例数据，不读取设备；模板可用 ${fields.product.code} 引用业务字段。') }}</p><el-input v-model="sample" type="textarea" :rows="5" :aria-label="t('样例上下文 JSON')" /><pre>{{ preview }}</pre></details>
  </section>
</template>
<style scoped>
.extra-fields { margin:12px 0; padding:10px; border:1px solid var(--el-border-color); border-radius:6px; }
p, details { font-size:12px; line-height:1.6; } .extra-row { display:grid; grid-template-columns:minmax(80px, 1fr) 86px 86px auto auto; gap:6px; margin:10px 0; } .value { grid-column:1 / -1; min-width:0; } pre { white-space:pre-wrap; overflow-wrap:anywhere; } [role=alert] { color:var(--el-color-danger); }
</style>
