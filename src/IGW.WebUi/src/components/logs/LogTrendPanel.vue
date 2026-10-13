<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from '../../i18n'
import type { LogHistogramBucketDto } from '../../api'

const props = defineProps<{ buckets: LogHistogramBucketDto[] }>()
const emit = defineEmits<{ select: [bucket: LogHistogramBucketDto] }>()
const { locale, t } = useI18n()
const enabled = ref({ total: true, warnings: true, errors: true })
const width = 760, height = 112, inset = 12
const maximum = computed(() => Math.max(1, ...props.buckets.flatMap(bucket => [bucket.total, bucket.warnings, bucket.errors])))
function x(index: number) { return props.buckets.length < 2 ? width / 2 : inset + index * (width - inset * 2) / (props.buckets.length - 1) }
function y(value: number) { return height - inset - Math.max(0, value) * (height - inset * 2) / maximum.value }
function points(series: 'total' | 'warnings' | 'errors') { return props.buckets.map((bucket, index) => `${x(index)},${y(bucket[series])}`).join(' ') }
function activate(event: KeyboardEvent, bucket: LogHistogramBucketDto) {
  if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); emit('select', bucket) }
}
function label(bucket: LogHistogramBucketDto) {
  return new Date(bucket.start).toLocaleTimeString(locale.value === 'en-US' ? 'en-US' : 'zh-CN', { hour12: false, hour: '2-digit', minute: '2-digit' })
}
</script>

<template>
  <section class="trend-panel">
    <header><div><b>{{ t('事件趋势') }}</b><span>{{ t('点击时间点可缩小查询范围') }}</span></div>
      <div class="series-toggles">
        <button v-for="series in (['total','warnings','errors'] as const)" :key="series" type="button"
          :data-series="series" :class="[series, { off: !enabled[series] }]" @click="enabled[series] = !enabled[series]">
          <i />{{ t(series === 'total' ? '全部' : series === 'warnings' ? '警告' : '错误') }}
        </button>
      </div>
    </header>
    <div v-if="buckets.length" class="chart-wrap">
      <svg :viewBox="`0 0 ${width} ${height}`" role="img" :aria-label="t('日志趋势')" preserveAspectRatio="none">
        <path v-for="line in [1,2,3]" :key="line" :d="`M ${inset} ${line*height/4} H ${width-inset}`" class="grid-line" />
        <polyline v-if="enabled.total" class="series-total" :points="points('total')" />
        <polyline v-if="enabled.warnings" class="series-warnings" :points="points('warnings')" />
        <polyline v-if="enabled.errors" class="series-errors" :points="points('errors')" />
        <g v-for="(bucket,index) in buckets" :key="bucket.start" class="trend-bucket" role="button" tabindex="0"
          :aria-label="`${label(bucket)} · ${bucket.total}`" @click="emit('select', bucket)" @keydown="activate($event, bucket)">
          <circle :cx="x(index)" :cy="y(bucket.total)" r="8" class="hit" /><circle :cx="x(index)" :cy="y(bucket.total)" r="2.5" class="dot" />
        </g>
      </svg>
    </div>
    <div v-else class="no-trend">{{ t('当前时间范围内没有趋势数据') }}</div>
  </section>
</template>

<style scoped>
.trend-panel { border:1px solid #dfe6ef; border-radius:12px; background:#fff; overflow:hidden; }
header { display:flex; align-items:center; justify-content:space-between; gap:12px; padding:11px 14px 6px; }header b { display:block; color:#29384b; font-size:13px; }header span { color:#8a96a6; font-size:11px; }
.series-toggles { display:flex; gap:5px; }.series-toggles button { display:flex; align-items:center; gap:5px; padding:4px 7px; border:0; border-radius:5px; background:transparent; color:#5f6d7e; font-size:11px; cursor:pointer; }.series-toggles button.off { opacity:.38; }.series-toggles i { width:7px; height:7px; border-radius:50%; background:#367cc4; }.series-toggles .warnings i { background:#d99a1b; }.series-toggles .errors i { background:#c84a3d; }
.chart-wrap { height:112px; padding:0 6px 8px; }.chart-wrap svg { width:100%; height:100%; overflow:visible; }.grid-line { fill:none; stroke:#edf1f5; stroke-width:1; vector-effect:non-scaling-stroke; }
polyline { fill:none; stroke-width:2; stroke-linecap:round; stroke-linejoin:round; vector-effect:non-scaling-stroke; }.series-total { stroke:#367cc4; }.series-warnings { stroke:#d99a1b; }.series-errors { stroke:#c84a3d; }.hit { fill:transparent; cursor:pointer; }.dot { fill:#367cc4; pointer-events:none; }.trend-bucket:focus .hit { fill:#d9eafd; stroke:#367cc4; vector-effect:non-scaling-stroke; }
.no-trend { display:grid; min-height:86px; place-items:center; color:#98a3b2; font-size:12px; }
</style>
