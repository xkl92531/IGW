<script setup lang="ts">
import { computed } from 'vue'
import type { LogCenterEntryDto } from '../../api'
import { formatLogDuration, severityTone } from '../../utils/logCenter'
import { buildLogPresentation, logFeatureLabel, logModuleLabel } from '../../utils/logPresentation'
import { useI18n } from '../../i18n'

const props = defineProps<{ items: LogCenterEntryDto[]; loading?: boolean }>()
const emit = defineEmits<{ select: [entry: LogCenterEntryDto] }>()
const { locale, t } = useI18n()
const displayItems = computed(() => props.items.map(entry => ({ entry, presentation: buildLogPresentation(entry) })))
function time(value: string) {
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString(locale.value === 'en-US' ? 'en-US' : 'zh-CN', { hour12: false })
}
</script>

<template>
  <section class="time-corridor" :class="{ loading }" :aria-label="t('日志时间走廊')">
    <button v-for="(row,index) in displayItems" :key="`${row.entry.timestamp}-${row.entry.traceId || index}`" type="button"
      class="event-row" :class="severityTone(row.entry.level)" @click="emit('select', row.entry)">
      <span class="rail"><i /></span>
      <span class="when">{{ time(row.entry.timestamp) }}</span>
      <span class="event-main">
        <span class="event-head">
          <b class="module">{{ t(logModuleLabel(row.presentation.module)) }}</b>
          <b class="feature">{{ t(logFeatureLabel(row.presentation.feature)) }}</b>
          <em>{{ row.entry.level }}</em>
          <small v-if="Number(row.entry.attributes?.igw_scan_count) > 1" data-testid="scan-summary">{{ t('{count} 次 · 平均 {avg} · 最大 {max}', { count: Number(row.entry.attributes.igw_scan_count), avg: formatLogDuration(row.entry.attributes.igw_duration_ms), max: formatLogDuration(row.entry.attributes.igw_max_duration_ms) }) }}</small>
          <small v-else-if="row.entry.attributes?.igw_duration_ms !== undefined">{{ formatLogDuration(row.entry.attributes.igw_duration_ms) }}</small>
        </span>
        <span class="message">{{ row.entry.message }}</span>
        <span v-if="row.presentation.task || row.presentation.interfaceOperation || row.presentation.plcScope || row.presentation.source" class="context-grid">
          <span v-if="row.presentation.task"><i>{{ t('任务') }}</i><b>{{ row.presentation.task }}</b></span>
          <span v-if="row.presentation.interfaceOperation"><i>{{ t('接口') }}</i><b>{{ row.presentation.interfaceOperation }}</b></span>
          <span v-if="row.presentation.plcScope"><i>{{ t('采集范围') }}</i><b>{{ row.presentation.plcScope }}</b></span>
          <span v-if="row.presentation.source"><i>{{ t('采集来源') }}</i><b>{{ row.presentation.source }}</b></span>
        </span>
      </span>
      <span class="open" aria-hidden="true">›</span>
    </button>
    <div v-if="!items.length && !loading" class="empty"><span>⌁</span><b>{{ t('没有符合当前条件的日志') }}</b><small>{{ t('调整时间范围或清除部分筛选条件') }}</small></div>
  </section>
</template>

<style scoped>
.time-corridor { min-height:300px; border:1px solid #dfe6ef; border-radius:12px; background:#fff; overflow:hidden; }.time-corridor.loading { opacity:.68; }
.event-row { display:grid; grid-template-columns:20px 160px minmax(0,1fr) 20px; align-items:stretch; width:100%; min-height:92px; padding:0 12px 0 4px; border:0; border-bottom:1px solid #edf1f5; background:#fff; color:#334256; font:inherit; text-align:left; cursor:pointer; }.event-row:last-child { border-bottom:0; }.event-row:hover,.event-row:focus-visible { background:#f7faff; outline:none; }
.rail { position:relative; display:flex; justify-content:center; }.rail::before { position:absolute; top:0; bottom:0; width:1px; background:#dce4ed; content:''; }.rail i { z-index:1; width:7px; height:7px; margin-top:19px; border:3px solid #fff; border-radius:50%; background:#7893b2; box-shadow:0 0 0 1px #7893b2; }.warning .rail i { background:#d99a1b; box-shadow:0 0 0 1px #d99a1b; }.error .rail i { background:#c84a3d; box-shadow:0 0 0 1px #c84a3d; }
.when { padding:16px 10px; color:#738095; font:11px/1.5 Consolas,monospace; }.event-main { min-width:0; padding:12px 8px; }.event-head { display:flex; min-width:0; align-items:center; gap:7px; margin-bottom:5px; }.event-head b { font-size:11px; }.event-head .module { color:#235a92; }.event-head .feature { padding:2px 6px; border-radius:4px; background:#eaf2fb; color:#3f658a; font-weight:600; }.event-head em { padding:1px 5px; border-radius:4px; background:#eef2f6; color:#697789; font-size:10px; font-style:normal; }.warning .event-head em { background:#fff4d8; color:#906100; }.error .event-head em { background:#fde8e5; color:#a5382d; }.event-head small { margin-left:auto; color:#526b86; font:11px Consolas,monospace; }
.message { display:block; overflow:hidden; color:#26374b; font-size:13px; text-overflow:ellipsis; white-space:nowrap; }.context-grid { display:flex; min-width:0; flex-wrap:wrap; gap:5px 14px; margin-top:7px; color:#758397; font-size:11px; }.context-grid>span { display:flex; min-width:0; max-width:100%; align-items:center; gap:5px; }.context-grid i { flex:none; color:#98a3b1; font-style:normal; }.context-grid b { overflow:hidden; color:#586b81; font-weight:500; text-overflow:ellipsis; white-space:nowrap; }.open { display:grid; place-items:center; color:#9aa6b5; font-size:22px; }
.empty { display:flex; min-height:300px; align-items:center; justify-content:center; flex-direction:column; color:#8a96a6; }.empty span { font-size:38px; }.empty b { margin:8px 0 4px; color:#607086; font-size:14px; }.empty small { font-size:12px; }
@media (max-width:780px) { .event-row { grid-template-columns:18px minmax(0,1fr) 16px; }.when { grid-column:2; padding:10px 8px 0; }.event-main { grid-column:2; padding-top:3px; }.rail { grid-row:1 / span 2; }.open { grid-column:3; grid-row:1 / span 2; }.context-grid { flex-direction:column; gap:4px; } }
</style>
