<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from '../i18n'

const { t } = useI18n()

/** 节点归因面板:等待类=逐条件"期望 vs 实际"(凶手条件高亮);HTTP 失败=状态码/响应/重试;回写失败=下发 vs 读回。
 *  数据来自 /api/v1/mes 快照的 node.detail(仅异常态携带),不发起额外请求。 */
const props = defineProps<{ status: any }>()

const d = computed<any>(() => props.status?.detail || null)
const stateText = computed(() => {
  const s = props.status
  if (!s) return ''
  if (d.value?.category === 'plc-communication') return t('PLC 通信异常')
  if (d.value?.category === 'mes-no-response') return t('MES 无响应')
  if (d.value?.category === 'business-ng') return t('业务 NG')
  if (d.value?.kind === 'data-quality') return t('上报成功 · 部分数据')
  if (d.value?.kind === 'write' && s.state !== 'fail') return t('回写失败')
  if (s.state === 'fail') return t('失败')
  if (s.failStreak > 0 && s.state !== 'ok' && s.state !== 'idle') return t('失败重试中(已失败 {count} 次)', { count: s.failStreak })
  if (s.stuck) return t('疑似卡住')
  if (s.state === 'wait') return t('等待中')
  if (s.state === 'run') return t('执行中')
  return s.state
})
const headColor = computed(() => {
  const s = props.status
  if (d.value?.kind === 'data-quality') return '#b26a00'
  if (d.value?.kind === 'write') return 'var(--sev-fail)'
  if (s?.state === 'fail' || (s?.failStreak > 0 && s?.state !== 'ok' && s?.state !== 'idle')) return 'var(--sev-fail)'
  if (s?.stuck) return 'var(--sev-stuck)'
  return 'var(--sev-slow)'
})
function fmtDur(ms: number): string {
  if (!ms || ms < 1000) return ms ? `${ms}ms` : ''
  const s = Math.floor(ms / 1000)
  if (s < 60) return `${s} 秒`
  const m = Math.floor(s / 60)
  if (m < 60) return `${m} 分 ${s % 60} 秒`
  return `${Math.floor(m / 60)} 时 ${m % 60} 分`
}
const shortTag = (t: string) => { const i = t.lastIndexOf('.'); return i >= 0 ? t.slice(i + 1) : t }

// 回写失败：只列失败点，点一行展开该点的下发/读回/状态/原因
const writeTotal = computed<number>(() => d.value?.total || d.value?.items?.length || 0)
const expandedWrite = ref<string | null>(null)
watch(() => props.status?.id, () => { expandedWrite.value = null })
const toggleWrite = (tagId: string) => { expandedWrite.value = expandedWrite.value === tagId ? null : tagId }
const statusHex = (code: number) => '0x' + ((code ?? 0) >>> 0).toString(16).toUpperCase().padStart(8, '0')
</script>

<template>
  <div class="panel" :class="{ 'quality-panel': d?.kind === 'data-quality' }" v-if="status">
    <div class="head" :style="{ color: headColor }">
      ⚠ {{ stateText }}<span v-if="status.ageMs"> · {{ $t('已持续 {time}', { time: fmtDur(status.ageMs) }) }}</span>
      <span v-if="status.baselineMs" class="base">({{ $t('基线 {time}', { time: fmtDur(status.baselineMs) }) }})</span>
    </div>

    <!-- 等待归因:逐条件核对,未命中 = 等待原因,高亮;条件节点多分支时带分支标签 -->
    <template v-if="d?.kind === 'wait'">
      <div v-for="(c, i) in d.conds" :key="i" class="cond" :class="{ miss: !c.met }">
        <span class="mk">{{ c.met ? '✔' : '✘' }}</span>
        <span v-if="c.branch" class="brch">{{ c.branch }}</span>
        <span class="tag" :title="c.tagId">{{ shortTag(c.tagId) }}</span>
        <span class="exp">{{ c.op === '变化' ? '等值变化' : `需 ${c.op} ${c.expect}` }}</span>
        <span class="act" :class="{ bad: c.bad }">{{ $t('当前:') }}{{ c.actual }}</span>
      </div>
    </template>

    <!-- HTTP 已成功，但采集来源存在离线范围或异常点位 -->
    <template v-else-if="d?.kind === 'data-quality'">
      <div class="quality-summary">
        <span class="quality-chip">{{ $t('离线数据范围 {count}', { count: d.offlineSources?.length || 0 }) }}</span>
        <span class="quality-chip">{{ $t('质量异常点位 {count}', { count: d.badTags?.length || 0 }) }}</span>
      </div>
      <div v-if="d.offlineSources?.length" class="quality-group">
        <div class="quality-title">{{ $t('离线数据范围（报文中整段为 null）') }}</div>
        <div v-for="source in d.offlineSources.slice(0, 6)" :key="source" class="quality-row mono" :title="source">{{ source }}</div>
        <div v-if="d.offlineSources.length > 6" class="quality-more">{{ $t('另有 {count} 项', { count: d.offlineSources.length - 6 }) }}</div>
      </div>
      <div v-if="d.badTags?.length" class="quality-group">
        <div class="quality-title">{{ $t('质量异常点位（字符串上报 Bad，其他类型上报 null）') }}</div>
        <div v-for="tag in d.badTags.slice(0, 6)" :key="tag" class="quality-row mono" :title="tag">{{ tag }}</div>
        <div v-if="d.badTags.length > 6" class="quality-more">{{ $t('另有 {count} 项', { count: d.badTags.length - 6 }) }}</div>
      </div>
    </template>

    <!-- HTTP 上报失败归因 -->
    <template v-else-if="d?.kind === 'http'">
      <div class="kv"><span class="k">{{ $t('异常类别') }}</span><span class="v">{{ d.category === 'mes-no-response' ? $t('MES 无响应') : d.category === 'business-ng' ? $t('业务 NG') : $t('HTTP 传输异常') }}</span></div>
      <div class="kv"><span class="k">{{ $t('HTTP 状态') }}</span><span class="v">{{ d.statusCode === 0 ? $t('未收到响应') : d.statusCode }}</span></div>
      <div class="kv"><span class="k">{{ $t('地址') }}</span><span class="v mono" :title="d.url">{{ d.url }}</span></div>
      <div class="kv"><span class="k">{{ $t('已尝试') }}</span><span class="v">{{ d.attempt }} 次</span></div>
      <div class="k" style="margin-top:4px">{{ $t('响应/错误:') }}</div>
      <pre class="snip">{{ d.bodySnippet || '(空)' }}</pre>
    </template>

    <!-- 回写失败归因:下发 vs 读回 -->
    <template v-else-if="d?.kind === 'write'">
      <div class="write-summary" data-test="write-fail-summary">
        {{ $t('回写失败 {count} 点', { count: writeTotal }) }}
        <span v-if="writeTotal > (d.items?.length || 0)" class="write-more">{{ $t('（仅显示前 {count} 点）', { count: d.items.length }) }}</span>
        <span class="write-hint">{{ $t('点击查看原因') }}</span>
      </div>
      <div class="write-list">
        <template v-for="(it, i) in d.items" :key="it.tagId + i">
          <div class="cond miss clickable" data-test="write-fail-row" @click="toggleWrite(it.tagId)">
            <span class="mk">✘</span>
            <span class="tag" :title="it.tagId">{{ shortTag(it.tagId) }}</span>
            <span v-if="it.stage" class="stage">{{ it.stage }}</span>
            <span class="why" :title="it.error">{{ it.error || it.statusName }}</span>
            <span class="caret">{{ expandedWrite === it.tagId ? '▾' : '▸' }}</span>
          </div>
          <div v-if="expandedWrite === it.tagId" class="write-detail" data-test="write-fail-detail">
            <div class="kv"><span class="k">{{ $t('点位') }}</span><span class="v mono" :title="it.tagId">{{ it.tagId }}</span></div>
            <div class="kv"><span class="k">{{ $t('下发值') }}</span><span class="v mono">{{ it.written === '' ? '(空)' : it.written }}</span></div>
            <div class="kv"><span class="k">{{ $t('读回值') }}</span><span class="v mono">{{ it.readBack === '' ? $t('(未读到)') : it.readBack }}</span></div>
            <div class="kv"><span class="k">{{ $t('失败阶段') }}</span><span class="v">{{ it.stage || '-' }}</span></div>
            <div class="kv"><span class="k">{{ $t('状态码') }}</span><span class="v mono">{{ it.statusName }} ({{ statusHex(it.statusCode) }})</span></div>
            <div class="kv"><span class="k">{{ $t('已尝试') }}</span><span class="v">{{ $t('试 {count} 次', { count: it.attempts }) }}</span></div>
            <div class="k" style="margin-top:4px">{{ $t('原因') }}</div>
            <pre class="snip">{{ it.error || it.statusName || '-' }}</pre>
          </div>
        </template>
      </div>
    </template>

    <div v-else class="plain">{{ status.info || '(无更多信息)' }}</div>
  </div>
</template>

<style scoped>
.panel { border: 1px solid #f0d9c8; background: #fffaf5; border-radius: 6px; padding: 10px 12px; margin-bottom: 10px; }
.panel.quality-panel { border-color: #f0cf8b; background: #fffaf0; }
.head { font-weight: 700; font-size: 13px; margin-bottom: 8px; }
.base { color: #999; font-weight: 400; font-size: 12px; margin-left: 4px; }
.cond { display: flex; align-items: center; gap: 8px; padding: 4px 6px; border-radius: 4px; font-size: 12px; }
.cond .mk { color: var(--sev-ok); font-weight: 700; width: 14px; }
.cond.miss { background: var(--sev-fail-bg); }
.cond.miss .mk { color: var(--sev-fail); }
.brch { background: #eef2f7; color: #556; border-radius: 3px; padding: 0 5px; font-size: 11px; white-space: nowrap; }
.tag { font-weight: 600; max-width: 130px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.exp { color: #666; }
.act { color: #333; font-weight: 600; }
.act.bad { color: var(--sev-fail); }
.try { color: #999; margin-left: auto; }
.kv { display: flex; gap: 8px; font-size: 12px; margin-bottom: 3px; }
.kv .k { color: #888; width: 62px; flex-shrink: 0; }
.kv .v { font-weight: 600; min-width: 0; overflow: hidden; text-overflow: ellipsis; }
.mono { font-family: Consolas, monospace; font-weight: 400; }
.k { color: #888; font-size: 12px; }
.snip { background: #f5f6f8; border-radius: 4px; padding: 6px 8px; font-size: 11px; max-height: 120px; overflow: auto; white-space: pre-wrap; word-break: break-all; margin: 4px 0 0; }
.plain { color: #666; font-size: 12px; }
.write-summary { font-size: 12px; font-weight: 600; color: var(--sev-fail); margin-bottom: 6px; display: flex; gap: 6px; flex-wrap: wrap; align-items: baseline; }
.write-more { color: #999; font-weight: 400; }
.write-hint { color: #999; font-weight: 400; margin-left: auto; }
.write-list { max-height: 320px; overflow: auto; }
.cond.clickable { cursor: pointer; margin-bottom: 2px; }
.cond.clickable:hover { filter: brightness(0.97); }
.stage { background: #fde2e2; color: #a33; border-radius: 3px; padding: 0 5px; font-size: 11px; white-space: nowrap; }
.why { color: #555; min-width: 0; flex: 1; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.caret { color: #999; }
.write-detail { border-left: 2px solid var(--sev-fail); background: #fff; padding: 6px 8px; margin: 0 0 6px 20px; }
.quality-summary { display: flex; gap: 8px; flex-wrap: wrap; margin-bottom: 8px; }
.quality-chip { border: 1px solid #edc36d; background: #fff3d6; color: #8b5700; border-radius: 12px; padding: 2px 8px; font-size: 11px; font-weight: 600; }
.quality-group + .quality-group { margin-top: 8px; }
.quality-title { color: #85621e; font-size: 12px; font-weight: 600; margin-bottom: 3px; }
.quality-row { color: #55431d; font-size: 11px; padding: 2px 6px; border-left: 2px solid #e1b75d; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.quality-more { color: #9a7a39; font-size: 11px; margin-top: 3px; }
</style>
