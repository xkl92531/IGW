<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { api, type ProjectParameterOption, type ProjectParameterOptionsResponse } from '../api'
import { findExactParameterOption, mergeCurrentParameterOptions } from '../utils/mesParameterOptions'
import { createMesParameterRequestCoordinator } from '../utils/mesParameterRequest'
import { useI18n } from '../i18n'

const { t } = useI18n()

type PickerKind = 'tag' | 'group'
type PickerAccess = 'read' | 'write'
type PickerSize = 'small' | 'default' | 'large'
type PickerOption = ProjectParameterOption & {
  missing?: boolean
  pending?: boolean
  disabled?: boolean
  shortLabel?: string
}

const props = withDefaults(defineProps<{
  modelValue?: string
  scopeGroup: string
  kind?: PickerKind
  access?: PickerAccess
  placeholder?: string
  size?: PickerSize
}>(), {
  kind: 'tag',
  access: 'read',
  placeholder: undefined,
  size: 'default',
})

const emit = defineEmits<{
  'update:modelValue': [value: string]
  change: [value: string]
}>()

const value = computed({
  get: () => props.modelValue || '',
  set: (next: string) => { if (canSelect(next || '')) emit('update:modelValue', next || '') },
})
const options = ref<ProjectParameterOption[]>([])
const loading = ref(false)
const error = ref('')
const truncated = ref(false)
const opened = ref(false)
const lastQuery = ref('')
const currentOption = ref<ProjectParameterOption | null>(null)
const currentValidationCompleted = ref(false)

const coordinator = createMesParameterRequestCoordinator<ProjectParameterOptionsResponse>((query, signal) =>
  api.parameterOptions({
    q: query,
    scopeGroup: props.scopeGroup,
    kind: props.kind,
    access: props.access,
    limit: 100,
  }, signal))

const visibleOptions = computed(() => (mergeCurrentParameterOptions(
  options.value,
  props.modelValue || '',
  props.kind,
  currentOption.value,
  currentValidationCompleted.value,
) as PickerOption[]).map((item) => ({
  ...item,
  shortLabel: item.label,
  disabled: props.access === 'write' && (item.missing || item.pending || item.kind !== 'tag' ||
    !item.value.toLowerCase().startsWith(`${props.scopeGroup.trim().toLowerCase()}.`)),
  // SelectV2 使用 label 渲染收起状态；必须直接显示完整路径，不能让同名点位再次混淆。
  label: item.fullPath || item.value,
})))
const selectedPath = computed(() => String(props.modelValue || '').trim())
const selectedAddress = computed(() => {
  const selected = visibleOptions.value.find((item) => item.value.toLocaleLowerCase() === selectedPath.value.toLocaleLowerCase())
  return selected?.address || ''
})
// SelectV2 默认保留固定高度，候选少时会出现大块空白。
// 按实际行数收缩，超过 4 行再使用内部滚动。
const dropdownHeight = computed(() => Math.min(Math.max(visibleOptions.value.length, 1) * 88, 352))

function apiErrorMessage(reason: any) {
  return reason?.response?.data?.error || reason?.message || t('加载工程点位失败')
}

function request(query: string, immediate = false) {
  lastQuery.value = query
  loading.value = true
  error.value = ''
  coordinator.search(query, (result) => {
    options.value = result.items || []
    const exact = findExactParameterOption(options.value, props.modelValue || '')
    if (exact) {
      currentOption.value = exact
      currentValidationCompleted.value = true
    } else if (query.localeCompare((props.modelValue || '').trim(), undefined, { sensitivity: 'accent' }) === 0) {
      currentOption.value = null
      currentValidationCompleted.value = true
    }
    truncated.value = !!result.truncated
    loading.value = false
  }, (reason) => {
    error.value = apiErrorMessage(reason)
    loading.value = false
  }, immediate)
}

function remoteSearch(query: string) {
  request((query || '').trim(), false)
}

function onVisibleChange(visible: boolean) {
  opened.value = visible
  if (visible) {
    // A populated picker used to issue both a broad list query and an exact validation query.
    // Search the current path once; typing or clearing immediately replaces it through the coordinator.
    request((props.modelValue || '').trim(), true)
  }
  else { coordinator.cancel(); loading.value = false }
}

function retry() {
  request(lastQuery.value, true)
}

function canSelect(next: string) {
  if (props.access !== 'write' || !next) return true
  // The server returns only writable candidates for access=write. Never accept a created,
  // missing or unverified write target, including a forged select component event.
  const candidate = visibleOptions.value.find(item => item.value.toLowerCase() === next.toLowerCase())
  return !!candidate && !candidate.disabled
}

function onChange(next: unknown) {
  const path = typeof next === 'string' ? next : String(next ?? '')
  if (canSelect(path)) emit('change', path)
}

function scopeLabel(scope: ProjectParameterOption['scope']) {
  return t(scope === 'CurrentGroup' ? '当前 DB' : scope === 'CurrentDevice' ? '当前 PLC' : '其他 PLC')
}

watch(() => [props.modelValue, props.scopeGroup, props.kind, props.access], () => {
  coordinator.cancel()
  loading.value = false
  currentOption.value = null
  currentValidationCompleted.value = false
  options.value = []
  error.value = ''
  truncated.value = false
  if (opened.value) {
    request((props.modelValue || '').trim(), true)
  }
}, { immediate: true })

onBeforeUnmount(() => {
  coordinator.cancel()
})
</script>

<template>
  <div class="mes-parameter-picker">
    <el-select-v2
      v-model="value"
      :options="visibleOptions"
      :placeholder="placeholder || $t('选择或输入工程参数')"
      :size="size"
      :loading="loading"
      :no-data-text="$t(error ? '候选加载失败，可继续手工输入' : '未找到工程点位')"
      filterable
      remote
      :allow-create="access !== 'write'"
      default-first-option
      clearable
      :height="dropdownHeight"
      :item-height="88"
      :remote-method="remoteSearch"
      popper-class="mes-parameter-dropdown"
      @visible-change="onVisibleChange"
      @change="onChange"
    >
      <template #default="{ item }">
        <div class="parameter-option" :class="{ missing: item.missing }" :title="[item.description, item.fullPath].filter(Boolean).join('\n')">
          <div class="parameter-option-main">
            <strong>{{ item.shortLabel || item.label }}</strong>
            <span v-if="item.missing" class="missing-badge">{{ $t('当前工程中不存在') }}</span>
            <span v-else-if="item.pending" class="pending-badge">{{ $t('待核对') }}</span>
            <span v-else class="scope-badge" :class="item.scope">{{ scopeLabel(item.scope) }}</span>
          </div>
          <small class="parameter-option-description">{{ item.description || $t(item.missing ? '已确认当前工程中没有该完整路径，请重新选择' : item.pending ? '正在核对当前工程中的完整点位路径' : '暂无点位说明') }}</small>
          <small class="parameter-option-path">{{ item.fullPath }}</small>
          <small v-if="item.address" class="parameter-option-address">{{ $t('实际 PLC 地址') }}：{{ item.address }}</small>
        </div>
      </template>
    </el-select-v2>
    <div v-if="selectedPath" class="selected-parameter-details">
      <div class="selected-parameter-path">{{ selectedPath }}</div>
      <div v-if="selectedAddress" class="selected-parameter-address">{{ $t('实际 PLC 地址') }}：{{ selectedAddress }}</div>
    </div>
    <div v-if="error" class="picker-feedback error">
      <span>{{ error }}</span>
      <button type="button" @click="retry">{{ $t('重新加载候选') }}</button>
    </div>
    <div v-else-if="truncated" class="picker-feedback">{{ $t('结果较多，继续输入以缩小范围') }}</div>
  </div>
</template>

<style scoped>
.mes-parameter-picker { flex: 1; min-width: 0; }
.mes-parameter-picker :deep(.el-select-v2) { width: 100%; }
.parameter-option { width: 100%; min-width: 0; box-sizing: border-box; padding: 3px 0; line-height: 1.2; }
.parameter-option-main { display: flex; align-items: center; gap: 7px; min-width: 0; }
.parameter-option strong { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: #243447; }
.parameter-option small { display: block; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.parameter-option-description { margin-top: 2px; color: #5f6f82; font-size: 12px; }
.parameter-option-path, .parameter-option-address { margin-top: 1px; color: #7d8997; font-size: 10.5px; line-height: 14px; white-space: normal; overflow: visible; text-overflow: clip; overflow-wrap: anywhere; max-height: 28px; }
.parameter-option-address { color: #546a82; }
.scope-badge, .missing-badge, .pending-badge { flex: none; border-radius: 9px; padding: 1px 6px; font-size: 10px; line-height: 16px; }
.scope-badge { color: #2870b8; background: #eaf3ff; }
.scope-badge.CurrentDevice { color: #6b55a5; background: #f0ecff; }
.scope-badge.OtherDevice { color: #6b7280; background: #f0f2f5; }
.missing-badge { color: #b56a00; background: #fff2d8; }
.pending-badge { color: #607080; background: #edf1f5; }
.parameter-option.missing small { color: #b27a2d; }
.picker-feedback { margin-top: 4px; color: #8492a6; font-size: 11px; line-height: 18px; }
.picker-feedback.error { display: flex; align-items: center; justify-content: space-between; gap: 8px; color: #d05a45; }
.picker-feedback button { padding: 0; border: 0; color: #409eff; background: transparent; cursor: pointer; font: inherit; white-space: nowrap; }
.selected-parameter-details { margin-top: 3px; color: #6f7f91; font-size: 10.5px; line-height: 14px; overflow-wrap: anywhere; }
.selected-parameter-path, .selected-parameter-address { white-space: normal; word-break: break-word; }
.selected-parameter-address { color: #496987; }
:global(.mes-parameter-dropdown) { width: min(680px, calc(100vw - 32px)) !important; max-width: calc(100vw - 32px); }
:global(.mes-parameter-dropdown .el-select-dropdown),
:global(.mes-parameter-dropdown .el-vl__wrapper),
:global(.mes-parameter-dropdown .el-select-dropdown__list),
:global(.mes-parameter-dropdown .el-vl__window) { width: 100% !important; box-sizing: border-box; }
:global(.mes-parameter-dropdown .el-select-dropdown__item) { width: 100% !important; box-sizing: border-box; height: 88px; padding: 4px 12px; line-height: normal; }
</style>
