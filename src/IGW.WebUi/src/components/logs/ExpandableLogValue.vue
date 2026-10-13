<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { useI18n } from '../../i18n'

const props = withDefaults(defineProps<{ value: string; threshold?: number; alwaysCopy?: boolean; startExpanded?: boolean }>(), {
  threshold: 160,
  alwaysCopy: false,
  startExpanded: false,
})
const { t } = useI18n()
const expanded = ref(props.startExpanded)
const copied = ref(false)
const viewerOpen = ref(false)
const viewerPage = ref(0)
const search = ref('')
const currentMatch = ref(0)
const viewerText = ref<HTMLTextAreaElement>()
let copiedTimer: ReturnType<typeof setTimeout> | undefined
const LargeCharacterThreshold = 12_000
const LargeLineThreshold = 200
const LinesPerPage = 400
const MaxSearchResults = 2_000
const rawLineCount = computed(() => countLines(props.value))
const isVeryLarge = computed(() => props.value.length > LargeCharacterThreshold || rawLineCount.value > LargeLineThreshold)
const formatted = computed(() => isVeryLarge.value && !viewerOpen.value ? props.value : formatForReading(props.value))
const formattedLineStarts = computed(() => lineStarts(formatted.value))
const formattedLineCount = computed(() => formattedLineStarts.value.length)
const pageCount = computed(() => Math.max(1, Math.ceil(formattedLineCount.value / LinesPerPage)))
const pageStart = computed(() => formattedLineStarts.value[viewerPage.value * LinesPerPage] ?? 0)
const pageEnd = computed(() => formattedLineStarts.value[(viewerPage.value + 1) * LinesPerPage] ?? formatted.value.length)
const pageContent = computed(() => formatted.value.slice(pageStart.value, pageEnd.value))
const compactContent = computed(() => isVeryLarge.value ? `${props.value.slice(0, 1_200)}…` : formatted.value)
const isLong = computed(() => isVeryLarge.value || formatted.value.length > props.threshold || formattedLineCount.value > 3)
const matches = computed(() => findMatches(formatted.value, search.value, MaxSearchResults))
const matchLabel = computed(() => {
  if (!search.value) return ''
  if (!matches.value.length) return t('未找到匹配内容')
  return matches.value.length === MaxSearchResults
    ? t('至少 {count} 个匹配', { count: MaxSearchResults })
    : t('{count} 个匹配', { count: matches.value.length })
})

watch(search, () => {
  currentMatch.value = 0
  if (matches.value.length) jumpToMatch(0)
})

watch(() => props.value, () => {
  expanded.value = props.startExpanded
  closeLargeViewer()
})

async function copyCompleteValue() {
  let clipboardCopied = false
  if (navigator.clipboard?.writeText) {
    try { await navigator.clipboard.writeText(props.value); clipboardCopied = true }
    catch { /* Fall back for embedded browsers that deny the Clipboard API. */ }
  }
  if (!clipboardCopied) fallbackCopy(props.value)
  copied.value = true
  clearTimeout(copiedTimer)
  copiedTimer = setTimeout(() => { copied.value = false }, 1600)
}

function fallbackCopy(value: string) {
  const input = document.createElement('textarea')
  input.value = value
  input.style.position = 'fixed'
  input.style.opacity = '0'
  document.body.appendChild(input)
  input.select()
  document.execCommand('copy')
  input.remove()
}

function openLargeViewer() {
  viewerPage.value = 0
  search.value = ''
  currentMatch.value = 0
  viewerOpen.value = true
}

function closeLargeViewer() {
  viewerOpen.value = false
  search.value = ''
}

function movePage(offset: number) {
  viewerPage.value = Math.min(pageCount.value - 1, Math.max(0, viewerPage.value + offset))
}

function moveMatch(offset: number) {
  if (!matches.value.length) return
  currentMatch.value = (currentMatch.value + offset + matches.value.length) % matches.value.length
  jumpToMatch(currentMatch.value)
}

async function jumpToMatch(index: number) {
  const offset = matches.value[index]
  if (offset === undefined) return
  const starts = formattedLineStarts.value
  let low = 0
  let high = starts.length - 1
  while (low <= high) {
    const middle = (low + high) >>> 1
    if (starts[middle] <= offset) low = middle + 1
    else high = middle - 1
  }
  viewerPage.value = Math.floor(Math.max(0, high) / LinesPerPage)
  await nextTick()
  const relative = offset - pageStart.value
  viewerText.value?.focus()
  viewerText.value?.setSelectionRange(relative, relative + search.value.length)
}

function downloadCompleteValue() {
  const blob = new Blob([props.value], { type: 'text/plain;charset=utf-8' })
  const url = URL.createObjectURL(blob)
  const anchor = document.createElement('a')
  anchor.href = url
  anchor.download = `igw-log-content-${new Date().toISOString().replace(/[:.]/g, '-')}.${looksLikeJson(props.value) ? 'json' : 'txt'}`
  document.body.appendChild(anchor)
  anchor.click()
  anchor.remove()
  URL.revokeObjectURL(url)
}

function formatForReading(value: string): string {
  const trimmed = value.trim()
  if (!(trimmed.startsWith('{') || trimmed.startsWith('['))) return value
  try { return JSON.stringify(JSON.parse(trimmed), null, 2) }
  catch { return value }
}

function looksLikeJson(value: string): boolean {
  const trimmed = value.trim()
  return trimmed.startsWith('{') || trimmed.startsWith('[')
}

function countLines(value: string): number {
  let count = 1
  for (let index = 0; index < value.length; index++) if (value.charCodeAt(index) === 10) count++
  return count
}

function lineStarts(value: string): number[] {
  const starts = [0]
  for (let index = 0; index < value.length; index++) if (value.charCodeAt(index) === 10) starts.push(index + 1)
  return starts
}

function findMatches(value: string, needle: string, limit: number): number[] {
  const query = needle.trim().toLocaleLowerCase()
  if (!query) return []
  const content = value.toLocaleLowerCase()
  const result: number[] = []
  let offset = 0
  while (result.length < limit) {
    const found = content.indexOf(query, offset)
    if (found < 0) break
    result.push(found)
    offset = found + Math.max(1, query.length)
  }
  return result
}

onBeforeUnmount(() => clearTimeout(copiedTimer))
</script>

<template>
  <div class="expandable-log-value">
    <pre data-testid="log-value-content" :class="expanded && isLong && !isVeryLarge ? 'expanded' : isLong ? 'collapsed' : 'short'">{{ compactContent }}</pre>
    <div v-if="isLong || alwaysCopy" class="value-actions">
      <span v-if="isLong">{{ t('{count} 个字符', { count: value.length }) }}</span>
      <button v-if="isVeryLarge" data-testid="open-large-log-viewer" type="button" @click="openLargeViewer">
        {{ t('打开大内容查看器') }}
      </button>
      <button v-else-if="isLong" data-testid="toggle-log-value" type="button" @click="expanded = !expanded">
        {{ t(expanded ? '收起全文' : '展开全文') }}
      </button>
      <button data-testid="copy-log-value" type="button" @click="copyCompleteValue">
        {{ t(copied ? '已复制' : '复制全文') }}
      </button>
    </div>
    <Teleport to="body">
      <div v-if="viewerOpen" data-testid="large-log-viewer" class="large-viewer-backdrop" @click.self="closeLargeViewer" @keydown.esc.stop="closeLargeViewer">
        <section class="large-viewer" role="dialog" aria-modal="true" :aria-label="t('大内容查看器')">
          <header>
            <div><strong>{{ t('大内容查看器') }}</strong><span>{{ t('{characters} 个字符 · {lines} 行', { characters: value.length, lines: formattedLineCount }) }}</span></div>
            <button type="button" :aria-label="t('关闭')" @click="closeLargeViewer">×</button>
          </header>
          <div class="viewer-toolbar">
            <label><span>{{ t('搜索内容') }}</span><input v-model="search" data-testid="large-log-search" type="search" :placeholder="t('输入标记名、字段或值')" /></label>
            <span class="match-status">{{ matchLabel }}</span>
            <button type="button" :disabled="!matches.length" @click="moveMatch(-1)">{{ t('上一处') }}</button>
            <button type="button" :disabled="!matches.length" @click="moveMatch(1)">{{ t('下一处') }}</button>
            <button type="button" @click="copyCompleteValue">{{ t(copied ? '已复制' : '复制全文') }}</button>
            <button data-testid="download-large-log" type="button" @click="downloadCompleteValue">{{ t('下载原文') }}</button>
          </div>
          <textarea ref="viewerText" data-testid="large-log-page" readonly spellcheck="false" :value="pageContent" />
          <footer>
            <span>{{ t('每段最多 {count} 行，只渲染当前段', { count: LinesPerPage }) }}</span>
            <div>
              <button type="button" :disabled="viewerPage === 0" @click="movePage(-1)">{{ t('上一段') }}</button>
              <b>{{ t('第 {page} / {total} 段', { page: viewerPage + 1, total: pageCount }) }}</b>
              <button type="button" :disabled="viewerPage >= pageCount - 1" @click="movePage(1)">{{ t('下一段') }}</button>
            </div>
          </footer>
        </section>
      </div>
    </Teleport>
  </div>
</template>

<style scoped>
.expandable-log-value { min-width:0; }
pre { min-width:0; margin:0; color:inherit; font:inherit; line-height:1.6; white-space:pre-wrap; overflow-wrap:anywhere; word-break:break-word; }
pre.collapsed { display:-webkit-box; overflow:hidden; -webkit-box-orient:vertical; -webkit-line-clamp:3; }
pre.expanded { max-height:min(52vh,520px); overflow:auto; padding:9px 10px; border:1px solid #dfe7f0; border-radius:7px; background:#fbfcfe; }
.value-actions { display:flex; align-items:center; justify-content:flex-end; gap:10px; margin-top:5px; color:#8a96a6; font:11px/1.4 system-ui,sans-serif; }
.value-actions button { padding:0; border:0; background:transparent; color:#236fbd; font:inherit; cursor:pointer; }
.value-actions button:hover,.value-actions button:focus-visible { color:#0b4f96; text-decoration:underline; outline:none; }
/* 从事件详情抽屉里打开：必须高于 App.vue 统一强制的 .el-overlay(8000)，低于下拉/提示(8500 起) */
.large-viewer-backdrop { position:fixed; inset:0; z-index:8200; display:grid; place-items:center; padding:18px; background:rgba(18,31,48,.58); }
.large-viewer { display:grid; grid-template-rows:auto auto minmax(0,1fr) auto; width:min(1500px,96vw); height:min(900px,94vh); overflow:hidden; border-radius:12px; background:#fff; box-shadow:0 24px 80px rgba(4,19,38,.34); color:#304156; font:13px/1.5 system-ui,sans-serif; }
.large-viewer>header { display:flex; align-items:center; justify-content:space-between; gap:16px; padding:14px 18px; border-bottom:1px solid #e2e8f0; }
.large-viewer>header div { display:flex; align-items:baseline; gap:12px; }.large-viewer>header strong { font-size:16px; color:#1d3148; }.large-viewer>header span { color:#7d8998; font-size:12px; }.large-viewer>header button { border:0; background:transparent; color:#64748b; font-size:25px; cursor:pointer; }
.viewer-toolbar { display:flex; align-items:flex-end; gap:8px; padding:10px 18px; border-bottom:1px solid #e2e8f0; background:#f7f9fc; }.viewer-toolbar label { display:grid; gap:3px; min-width:min(420px,38vw); }.viewer-toolbar label span { color:#68778a; font-size:11px; }.viewer-toolbar input { width:100%; height:32px; padding:0 10px; border:1px solid #cfd9e5; border-radius:6px; background:#fff; color:#26384d; outline:none; }.viewer-toolbar input:focus { border-color:#409eff; box-shadow:0 0 0 2px rgba(64,158,255,.13); }.match-status { min-width:92px; color:#718096; font-size:11px; }.viewer-toolbar button,.large-viewer footer button { height:30px; padding:0 11px; border:1px solid #cfd9e5; border-radius:6px; background:#fff; color:#315171; cursor:pointer; white-space:nowrap; }.viewer-toolbar button:disabled,.large-viewer footer button:disabled { cursor:not-allowed; opacity:.45; }
.large-viewer textarea { box-sizing:border-box; width:100%; height:100%; min-height:0; resize:none; padding:14px 18px; border:0; background:#0f1b2b; color:#d9e7f5; font:12px/1.62 Consolas,'Courier New',monospace; outline:none; white-space:pre; overflow:auto; }
.large-viewer footer { display:flex; align-items:center; justify-content:space-between; gap:12px; padding:10px 18px; border-top:1px solid #e2e8f0; color:#718096; font-size:11px; }.large-viewer footer div { display:flex; align-items:center; gap:10px; }.large-viewer footer b { min-width:92px; text-align:center; color:#41546a; }
@media (max-width:760px) { .large-viewer-backdrop { padding:0; }.large-viewer { width:100vw; height:100vh; border-radius:0; }.viewer-toolbar { align-items:stretch; flex-wrap:wrap; }.viewer-toolbar label { width:100%; min-width:0; }.match-status { flex:1; }.large-viewer footer { align-items:flex-start; flex-direction:column; }.large-viewer footer div { width:100%; justify-content:space-between; } }
</style>
