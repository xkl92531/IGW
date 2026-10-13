<script setup lang="ts">
import { computed } from 'vue'

type PickerSize = 'small' | 'default' | 'large'

const props = withDefaults(defineProps<{
  modelValue?: string
  options?: string[]
  placeholder?: string
  size?: PickerSize
}>(), {
  options: () => [],
  placeholder: undefined,
  size: 'default',
})

const emit = defineEmits<{
  'update:modelValue': [value: string]
  change: [value: string]
}>()

const value = computed({
  get: () => props.modelValue || '',
  set: (next: string) => emit('update:modelValue', next || ''),
})

const visibleOptions = computed(() => {
  const values = new Set(
    props.options
      .map((item) => (item || '').trim())
      .filter(Boolean),
  )
  const current = (props.modelValue || '').trim()
  if (current) values.add(current)
  return [...values].map((item) => ({ value: item, label: item }))
})

function onChange(next: unknown) {
  emit('change', typeof next === 'string' ? next : String(next ?? ''))
}
</script>

<template>
  <el-select-v2
    v-model="value"
    :options="visibleOptions"
    :placeholder="placeholder || $t('如 backResult.isNg')"
    :size="size"
    filterable
    allow-create
    default-first-option
    clearable
    popper-class="mes-response-field-dropdown"
    :no-data-text="$t('在上报节点填「接口返回格式」就能选，也可直接输入完整路径')"
    @change="onChange"
  />
</template>

<style scoped>
.el-select-v2 { flex: 1; min-width: 0; width: 100%; }
:global(.mes-response-field-dropdown) { min-width: min(320px, calc(100vw - 32px)) !important; }
</style>
