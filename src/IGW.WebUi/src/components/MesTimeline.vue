<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { api } from '../api'
import { useRealtimeStore } from '../stores/realtime'

/** 执行轨迹时间线:节点流转事件(内存环形缓冲,最近 256 条),按"轮次"分组(一次触发到下一次触发前)。
 *  点击行 → 聚焦画布对应节点。回答"刚才发生了什么"。 */
const props = defineProps<{ taskId?: string; taskName: string; nodes: any[]; triggerId?: string; runtimeEvents?: any[] }>()
const emit = defineEmits<{ (e: 'focus', nodeId: string): void }>()

const realtime = useRealtimeStore()
const runtimeEvents = computed(() => props.runtimeEvents ?? realtime.runtimeEvents)
const history = ref<any[]>([])
const loading = ref(false)
const historyFailed = ref(false)
let historyGeneration = 0
const taskKey = computed(() => props.taskId || props.taskName)

watch(taskKey, async key => {
  const generation = ++historyGeneration
  history.value = []
  loading.value = !!key; historyFailed.value = false
  if (!key) return
  try {
    const loaded = await api.mesTimeline(key)
    if (generation === historyGeneration) history.value = Array.isArray(loaded) ? loaded : []
  } catch { if (generation === historyGeneration) historyFailed.value = true }
  finally { if (generation === historyGeneration) loading.value = false }
}, { immediate: true })
onBeforeUnmount(() => { historyGeneration++ })

const events = computed<any[]>(() => {
  const key = taskKey.value
  if (!key) return []
  const streamed = runtimeEvents.value
    .filter((entry: any) => entry.kind === 'mes-flow-transition'
      && (entry.payload?.taskId === key || entry.payload?.taskName === key))
    .map((entry: any) => entry.payload?.value)
    .filter(Boolean)
  const merged = new Map<string, any>()
  for (const event of [...history.value, ...streamed])
    merged.set(`${event.nodeId}|${event.state}|${event.tsUtc}|${event.durMs}`, event)
  return [...merged.values()]
    .sort((left, right) => Date.parse(left.tsUtc || '') - Date.parse(right.tsUtc || ''))
    .slice(-256)
})

const titleOf = (id: string) => {
  const n = (props.nodes || []).find((x: any) => x.id === id)
  return n?.title || n?.type || id
}
// 轮次分组:遇到触发节点 ok = 新一轮开始;最新轮在最上
const rounds = computed(() => {
  const out: { no: number; rows: any[] }[] = []
  let cur: any[] = []
  let no = 0
  for (const e of events.value) {
    if (props.triggerId && e.nodeId === props.triggerId && e.state === 'ok') {
      if (cur.length) out.push({ no: ++no, rows: cur })
      cur = [e]
    } else cur.push(e)
  }
  if (cur.length) out.push({ no: ++no, rows: cur })
  return out.reverse()
})
const stateColor = (e: any) =>
  e.state === 'fail' ? 'var(--sev-fail)' : e.state === 'wait' ? 'var(--sev-slow)' : e.state === 'ok' ? 'var(--sev-ok)' : 'var(--sev-info)'
const stateText: any = { ok: '✔', fail: '✘', wait: '⏳', run: '▶', idle: '·' }
const fmtTime = (ts: string) => { try { return new Date(ts).toLocaleTimeString('zh-CN', { hour12: false }) } catch { return ts } }
const fmtDur = (ms: number) => !ms ? '' : ms < 1000 ? `${ms}ms` : ms < 60000 ? `${(ms / 1000).toFixed(1)}s` : `${Math.floor(ms / 60000)}m${Math.floor((ms % 60000) / 1000)}s`
</script>

<template>
  <div class="tl">
    <div class="hd">{{ $t('执行轨迹') }} <span class="sub">{{ $t('最近 {count} 条(自启动,重启清空)', { count: events.length }) }}</span></div>
    <p v-if="loading" role="status">{{ $t('正在加载执行轨迹…') }}</p>
    <p v-if="historyFailed" role="alert">{{ $t('历史轨迹加载失败，实时事件仍会继续显示') }}</p>
    <el-empty v-if="!rounds.length && !loading && !historyFailed" :description="$t('暂无流转事件')" :image-size="60" />
    <div v-else class="scroll">
      <div v-for="r in rounds" :key="r.no" class="round">
        <div class="rhd">{{ $t('第 {number} 轮', { number: r.no }) }}</div>
        <div v-for="(e, i) in r.rows" :key="i" class="row" @click="emit('focus', e.nodeId)">
          <span class="t">{{ fmtTime(e.tsUtc) }}</span>
          <span class="mk" :style="{ color: stateColor(e) }">{{ stateText[e.state] || e.state }}</span>
          <span class="nm" :style="e.state === 'fail' ? 'color:var(--sev-fail);font-weight:600' : ''">{{ titleOf(e.nodeId) }}</span>
          <span class="inf" v-if="e.info" :title="e.info">{{ e.info }}</span>
          <span class="dur">{{ fmtDur(e.durMs) }}</span>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.tl { display: flex; flex-direction: column; height: 100%; min-height: 0; }
.hd { font-weight: 700; font-size: 13px; margin-bottom: 6px; }
.sub { color: #aaa; font-weight: 400; font-size: 11px; margin-left: 4px; }
.scroll { flex: 1; overflow: auto; min-height: 0; }
.round { margin-bottom: 8px; }
.rhd { color: #999; font-size: 11px; border-bottom: 1px dashed #e6e9ee; margin-bottom: 3px; padding-bottom: 2px; }
.row { display: flex; align-items: center; gap: 6px; font-size: 12px; padding: 2px 4px; border-radius: 4px; cursor: pointer; }
.row:hover { background: #f2f5f9; }
.t { color: #999; font-variant-numeric: tabular-nums; flex-shrink: 0; }
.mk { width: 14px; text-align: center; flex-shrink: 0; }
.nm { font-weight: 500; flex-shrink: 0; max-width: 88px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.inf { color: #aaa; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; flex: 1; min-width: 0; }
.dur { color: #b58900; font-variant-numeric: tabular-nums; flex-shrink: 0; }
</style>
