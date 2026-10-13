<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from '../i18n'
import { modbusRegisterTypes, modbusWireSummary, parseModbusAddress, validateModbusTag, type ModbusArea } from '../utils/protocolEditors'
const model = defineModel<any>({ required: true })
// editable=false：只看点位协议参数，输入框全部禁用
const props = withDefaults(defineProps<{ settings?: any; editable?: boolean }>(), { settings: () => ({}), editable: true })
const { t } = useI18n()
const parsed = computed(() => parseModbusAddress(model.value.address, props.settings.zeroBased))
const address = computed(() => parsed.value || { area: 'HR' as ModbusArea, offset: 0, bit: null })
const bitArea = computed(() => ['C', 'DI'].includes(address.value.area))
const readOnly = computed(() => ['DI', 'IR'].includes(address.value.area))
const types = computed(() => bitArea.value ? ['bool'] : modbusRegisterTypes)
const validation = computed(() => validateModbusTag(model.value, props.settings))
const wire = computed(() => modbusWireSummary(model.value, props.settings))
function change(patch: any = {}, addressPatch: any = {}) {
  if (!props.editable) return
  const next = { ...model.value, ...patch }
  const value = { ...address.value, ...addressPatch }
  if (['C', 'DI'].includes(value.area)) { next.dataType = 'bool'; value.bit = null }
  else if (next.dataType === 'bool') value.bit ??= 0
  else value.bit = null
  if (['DI', 'IR'].includes(value.area)) next.access = 'read'
  next.address = `${value.area}:${value.offset}${value.bit === null ? '' : `.${value.bit}`}`
  if (next.array) next.array = { ...next.array, elementType: next.dataType }
  model.value = next
}
function toggleArray(event: Event) {
  const enabled = (event.target as HTMLInputElement).checked
  change({ isArray: enabled, array: enabled ? { elementType: model.value.dataType, dimensions: [1], lowerBounds: [0], elementStrideBytes: 0, elementStrideBits: model.value.dataType === 'bool' ? 1 : 0 } : null })
}
</script>
<template>
  <section class="protocol-editor" aria-label="Modbus point editor">
    <p class="protocol-title">Modbus TCP · {{ t('区域与协议地址') }}</p>
    <label>{{ t('区域') }}<select aria-label="Modbus area" :disabled="!editable" :value="address.area" @change="change({}, { area: ($event.target as HTMLSelectElement).value })">
      <option value="C">{{ t('线圈') }} (Coils · 0x · FC01)</option>
      <option value="DI">{{ t('离散输入（只读）') }} (1x · FC02)</option>
      <option value="IR">{{ t('输入寄存器（只读）') }} (3x · FC04)</option>
      <option value="HR">{{ t('保持寄存器') }} (4x · FC03)</option>
    </select></label>
    <label>{{ t('协议偏移（从 0 开始）') }}<input aria-label="Modbus offset" :disabled="!editable" type="number" min="0" max="65535" :value="address.offset" @change="change({}, { offset: Number(($event.target as HTMLInputElement).value) })" /></label>
    <label>{{ t('数据类型') }}<select aria-label="Modbus data type" :disabled="!editable" :value="model.dataType" @change="change({ dataType: ($event.target as HTMLSelectElement).value })"><option v-for="type in types" :key="type" :value="type">{{ type }}</option></select></label>
    <label v-if="!bitArea && model.dataType === 'bool'">{{ t('寄存器位（0–15）') }}<input aria-label="Register bit" :disabled="!editable" type="number" min="0" max="15" :value="address.bit ?? 0" @change="change({}, { bit: Number(($event.target as HTMLInputElement).value) })" /></label>
    <label>{{ t('客户端访问') }}<select aria-label="Modbus access" :disabled="!editable" :value="model.access" @change="change({ access: ($event.target as HTMLSelectElement).value })"><option value="read">{{ t('只读') }}</option><option v-if="!readOnly" value="readWrite">{{ t('读/写') }}</option><option v-if="!readOnly" value="write">{{ t('只写') }}</option></select></label>
    <label>{{ t('连续元素') }}<input aria-label="Contiguous Modbus elements" :disabled="!editable" type="checkbox" :checked="!!model.array" @change="toggleArray" /></label>
    <label v-if="model.array && model.array.dimensions.length === 1">{{ t('元素数量') }}<input aria-label="Modbus element count" :disabled="!editable" type="number" min="1" max="65536" :value="model.array.dimensions[0]" @change="change({ array: { ...model.array, dimensions: [Number(($event.target as HTMLInputElement).value)] } })" /></label>
    <p v-if="model.array && model.array.dimensions.length > 1">{{ t('已保留现有多维连续数据配置') }}: {{ model.array.dimensions.join(' × ') }}</p>
    <p><code>{{ model.address }}</code> · {{ wire }}</p>
    <p class="protocol-note">{{ t('显式地址始终使用零基协议偏移，例如 HR:0。旧地址 40001 / 400001 按设备零基址设置解析，修改控件后转换为显式地址。') }}</p>
    <p v-if="validation" role="alert" class="protocol-error">{{ t(validation) }}</p>
  </section>
</template>
<style scoped>
.protocol-editor{width:100%;box-sizing:border-box;padding:14px 16px;margin-bottom:16px;border:1px solid var(--el-border-color);border-radius:6px;background:var(--el-fill-color-light);font-size:13px;line-height:1.5}.protocol-title{font-weight:600;margin:0 0 12px;color:var(--el-color-primary)}label{display:grid;grid-template-columns:175px minmax(0,1fr);align-items:center;gap:12px;margin-bottom:10px}select,input{box-sizing:border-box;width:100%;min-width:0;padding:6px 9px;border:1px solid var(--el-border-color);border-radius:4px;background:var(--el-bg-color);color:var(--el-text-color-primary);font:inherit}input[type=checkbox]{width:auto;justify-self:start}.protocol-note{color:var(--el-text-color-secondary);margin-bottom:0}.protocol-error{color:var(--el-color-danger)}code{font-weight:600}p{overflow-wrap:anywhere}
</style>
