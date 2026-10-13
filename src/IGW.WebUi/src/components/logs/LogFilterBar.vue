<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import type { LogFilters } from '../../utils/logCenter'
import type { LogFilterOptionsDto } from '../../api'
import { useI18n } from '../../i18n'

type GroupOption = LogFilterOptionsDto['groups'][number]
const emptyOptions: LogFilterOptionsDto = { protocols: [], channels: [], devices: [], groups: [] }
const props = defineProps<{ modelValue: LogFilters; loading?: boolean; options?: LogFilterOptionsDto }>()
const emit = defineEmits<{ 'update:modelValue': [value: LogFilters]; apply: []; reset: [] }>()
const { t } = useI18n()
const opts = computed(() => props.options || emptyOptions)
function patch(changes: Partial<LogFilters>) { emit('update:modelValue', { ...props.modelValue, ...changes }) }

// 只留协议、通道、设备、标记组和时间。协议缩小通道，通道缩小设备，三者一起缩小标记组提示；
// 下拉选了位置，就不再用工程配置「查看日志」从地址带来的工程位置(path)。
const protocolOf = (channel: string) => opts.value.channels.find(item => item.name === channel)?.protocol || ''
const channels = computed(() => opts.value.channels.filter(item => !props.modelValue.protocol || item.protocol === props.modelValue.protocol))
const devices = computed(() => opts.value.devices.filter(item => props.modelValue.channel
  ? item.channel === props.modelValue.channel
  : !props.modelValue.protocol || protocolOf(item.channel) === props.modelValue.protocol))
function inScope(group: GroupOption) {
  const { protocol, channel, device } = props.modelValue
  if (device) return group.device === device
  if (channel) return group.channel === channel
  return !protocol || protocolOf(group.channel) === protocol
}
function setProtocol(value: string) {
  const keep = !value || protocolOf(props.modelValue.channel) === value
  patch({ protocol: value || '', ...(keep ? {} : { channel: '', device: '' }), path: '' })
}
function setChannel(value: string) {
  const device = opts.value.devices.find(item => item.path === props.modelValue.device)
  patch({ channel: value || '', device: device && device.channel === value ? props.modelValue.device : '', path: '' })
}
function setDevice(value: string) {
  const device = opts.value.devices.find(item => item.path === value)
  patch({ device: value || '', channel: device?.channel || props.modelValue.channel, path: '' })
}
function suggestGroups(text: string, done: (items: GroupOption[]) => void) {
  const query = (text || '').trim().toLowerCase()
  done(opts.value.groups.filter(group => inScope(group)
    && (!query || group.name.toLowerCase().includes(query) || group.path.toLowerCase().includes(query))).slice(0, 50))
}
// 选中提示里的组：按它绑定的任务查；自己输入：同时模糊匹配任务名称和接口 URL，不猜测输入是不是完整 URL。
function pickGroup(group: GroupOption) { patch({ group: group.name, task: group.taskId, path: '' }) }
function typeGroup(value: string) { patch({ group: value || '', task: '', path: '' }) }

// 时间面板打开时不跟着外面变：日志页每 10 秒把实时窗口往前滑，正在选的时间会被冲掉；关掉面板后再跟上
const pickerOpen = ref(false)
const timeRange = ref<Date[]>([])
const syncTimeRange = () => { timeRange.value = [new Date(props.modelValue.from), new Date(props.modelValue.to)] }
watch(() => [props.modelValue.from, props.modelValue.to], () => { if (!pickerOpen.value) syncTimeRange() }, { immediate: true })
function pickTime(value: Date[] | null) {
  if (value?.length !== 2) return
  timeRange.value = value
  patch({ from: value[0].toISOString(), to: value[1].toISOString() })
}
function pickerVisible(open: boolean) {
  pickerOpen.value = open
  if (!open) syncTimeRange()
}
</script>

<template>
  <section class="filter-bar" :aria-label="t('日志筛选')">
    <el-select data-filter="protocol" :model-value="modelValue.protocol" clearable filterable :placeholder="t('协议')" @update:model-value="setProtocol">
      <el-option v-for="item in opts.protocols" :key="item.id" :label="item.name" :value="item.id" />
    </el-select>
    <el-select data-filter="channel" :model-value="modelValue.channel" clearable filterable :placeholder="t('通道')" @update:model-value="setChannel">
      <el-option v-for="item in channels" :key="item.name" :label="item.name" :value="item.name" />
    </el-select>
    <el-select data-filter="device" :model-value="modelValue.device" clearable filterable :placeholder="t('设备')" @update:model-value="setDevice">
      <el-option v-for="item in devices" :key="item.path" :label="modelValue.channel ? item.name : `${item.channel} / ${item.name}`" :value="item.path" />
    </el-select>
    <el-autocomplete data-filter="group" :model-value="modelValue.group" clearable :fetch-suggestions="suggestGroups" value-key="name"
      :placeholder="t('标记组名称 / 接口 URL')" :trigger-on-focus="true" @update:model-value="typeGroup" @select="pickGroup" @keyup.enter="emit('apply')">
      <template #default="{ item }"><div class="group-option"><b>{{ item.name }}</b><small>{{ item.path }}</small></div></template>
    </el-autocomplete>
    <el-date-picker class="time-control" :model-value="timeRange" type="datetimerange"
      @update:model-value="pickTime" @visible-change="pickerVisible" :start-placeholder="t('开始时间')" :end-placeholder="t('结束时间')"
      range-separator="→" unlink-panels />
    <div class="filter-actions">
      <el-button @click="emit('reset')">{{ t('重置') }}</el-button>
      <el-button type="primary" :loading="loading" @click="emit('apply')">{{ t('查询日志') }}</el-button>
    </div>
  </section>
</template>

<style scoped>
.filter-bar { display:grid; grid-template-columns:repeat(3,minmax(130px,170px)) minmax(180px,1fr) minmax(330px,420px) auto; gap:9px; align-items:center;
  padding:12px; border:1px solid #dfe6ef; border-radius:12px; background:#fff; }
.filter-bar :deep(.el-date-editor), .filter-bar :deep(.el-autocomplete), .filter-bar :deep(.el-select) { width:100%; }
.filter-actions { display:flex; gap:7px; justify-content:flex-end; white-space:nowrap; }
.group-option { display:flex; flex-direction:column; line-height:1.35; padding:3px 0; }
.group-option small { color:#8a96a3; font-size:11px; }
@media (max-width:1280px) {
  .filter-bar { grid-template-columns:repeat(3,minmax(0,1fr)); }
  .time-control { grid-column:span 2; }
  .filter-actions { grid-column:1 / -1; }
}
@media (max-width:720px) {
  .filter-bar { grid-template-columns:1fr; }
  .time-control, .filter-actions { grid-column:1; }
  .filter-actions { justify-content:stretch; } .filter-actions :deep(.el-button) { flex:1; }
}
</style>
