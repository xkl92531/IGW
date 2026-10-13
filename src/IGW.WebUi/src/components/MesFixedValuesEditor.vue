<script setup lang="ts">
import { computed } from 'vue'
import MesParameterPicker from './MesParameterPicker.vue'
import { FixedValueTypes, fixedValueErrors, type MesFixedValue } from '../utils/mesFixedValues'
import { useI18n } from '../i18n'

const { t } = useI18n()
// writeBack=true 时每行多一个「写回点位」(固定值节点用)；editable=false 时隐藏增删(只读账号)
const props = withDefaults(defineProps<{ modelValue?: MesFixedValue[]; writeBack?: boolean; scopeGroup?: string; editable?: boolean }>(),
  { modelValue: () => [], writeBack: false, scopeGroup: '', editable: true })
const emit = defineEmits<{ 'update:modelValue': [value: MesFixedValue[]]; change: [] }>()

const errors = computed(() => fixedValueErrors(props.modelValue))
function replace(rows: MesFixedValue[]) { emit('update:modelValue', rows); emit('change') }
function update(index: number, patch: Partial<MesFixedValue>) {
  replace(props.modelValue.map((row, i) => {
    if (i !== index) return row
    const next = { ...row, ...patch }
    // 改成布尔时给一个合法的默认值，避免留着文本
    if (patch.dataType === 'Bool' && !['true', 'false', '1', '0'].includes(String(next.value).toLowerCase())) next.value = 'true'
    return next
  }))
}
function add() { replace([...props.modelValue, { name: '', dataType: 'Text', value: '', ...(props.writeBack ? { tagId: '' } : {}) }]) }
</script>

<template>
  <div class="fixed-values" data-testid="fixed-values">
    <div v-for="(row, index) in modelValue" :key="index" class="fixed-row" :class="{ 'with-tag': writeBack }">
      <el-input :model-value="row.name" size="small" :aria-label="t('字段名')" :placeholder="t('字段名')"
        @update:model-value="update(index, { name: $event })" />
      <el-select :model-value="row.dataType || 'Text'" size="small" :aria-label="t('类型')"
        @update:model-value="update(index, { dataType: $event })">
        <el-option v-for="type in FixedValueTypes" :key="type.value" :value="type.value" :label="t(type.label)" />
      </el-select>
      <el-select v-if="row.dataType === 'Bool'" :model-value="String(row.value).toLowerCase() === '1' ? 'true' : String(row.value).toLowerCase() === '0' ? 'false' : String(row.value).toLowerCase()"
        size="small" :aria-label="t('值')" @update:model-value="update(index, { value: $event })">
        <el-option value="true" label="true" /><el-option value="false" label="false" />
      </el-select>
      <el-input v-else :model-value="row.value" size="small" :aria-label="t('值')" :placeholder="t('值')"
        @update:model-value="update(index, { value: $event })" />
      <el-button v-if="editable" size="small" text type="danger" @click="replace(modelValue.filter((_, i) => i !== index))">{{ t('删') }}</el-button>
      <MesParameterPicker v-if="writeBack" class="tag" :model-value="row.tagId || ''" :scope-group="scopeGroup" kind="tag" access="write"
        :placeholder="t('写回 PLC 点位(可不填)')" size="small" @update:model-value="update(index, { tagId: $event })" />
      <small v-if="errors[index]" class="error" role="alert">{{ t(errors[index]) }}</small>
    </div>
    <el-button v-if="editable" size="small" text @click="add">{{ t('+ 固定值') }}</el-button>
  </div>
</template>

<style scoped>
.fixed-row { display:grid; grid-template-columns:minmax(70px, 1fr) 78px minmax(70px, 1fr) auto; gap:6px; align-items:center; margin-bottom:6px; }
.fixed-row .tag, .fixed-row .error { grid-column:1 / -1; min-width:0; }
.fixed-row.with-tag { padding-bottom:6px; border-bottom:1px dashed #e3e9f0; }
.error { color:var(--el-color-danger); font-size:12px; }
</style>
