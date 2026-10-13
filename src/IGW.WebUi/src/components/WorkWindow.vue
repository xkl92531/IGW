<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted } from 'vue'

export type WorkWindowRect = { x: number; y: number; width: number; height: number }

const props = withDefaults(defineProps<{
  modelValue: boolean
  title: string
  minimized?: boolean
  maximized?: boolean
  rect: WorkWindowRect
  zIndex?: number
}>(), { minimized: false, maximized: false, zIndex: 1200 })

const emit = defineEmits<{
  'update:modelValue': [value: boolean]
  'update:minimized': [value: boolean]
  'update:maximized': [value: boolean]
  'update:rect': [value: WorkWindowRect]
  focus: []
  'request-close': []
}>()

const margin = 8
const titleHeight = 38
const bottomReserve = 42

function clampRect(rect: WorkWindowRect): WorkWindowRect {
  const maxWidth = Math.max(360, window.innerWidth - margin * 2)
  const maxHeight = Math.max(260, window.innerHeight - bottomReserve - margin * 2)
  const width = Math.min(Math.max(360, rect.width), maxWidth)
  const height = Math.min(Math.max(260, rect.height), maxHeight)
  return {
    x: Math.min(Math.max(margin, rect.x), Math.max(margin, window.innerWidth - width - margin)),
    y: Math.min(Math.max(margin, rect.y), Math.max(margin, window.innerHeight - bottomReserve - titleHeight)),
    width,
    height,
  }
}

const style = computed(() => {
  if (props.maximized) return {
    left: `${margin}px`, top: `${margin}px`, width: `calc(100vw - ${margin * 2}px)`,
    height: `calc(100vh - ${bottomReserve + margin * 2}px)`, zIndex: props.zIndex,
  }
  const rect = typeof window === 'undefined' ? props.rect : clampRect(props.rect)
  return { left: `${rect.x}px`, top: `${rect.y}px`, width: `${rect.width}px`, height: `${rect.height}px`, zIndex: props.zIndex }
})

function minimize() { emit('update:minimized', true); emit('update:maximized', false) }
function toggleMaximize() { emit('update:minimized', false); emit('update:maximized', !props.maximized); emit('focus') }
function requestClose() { emit('request-close') }

function beginDrag(e: PointerEvent) {
  if (props.maximized || (e.target as HTMLElement).closest('button')) return
  e.preventDefault(); emit('focus')
  const start = clampRect(props.rect)
  const sx = e.clientX; const sy = e.clientY
  const move = (ev: PointerEvent) => emit('update:rect', clampRect({ ...start, x: start.x + ev.clientX - sx, y: start.y + ev.clientY - sy }))
  const up = () => { window.removeEventListener('pointermove', move); window.removeEventListener('pointerup', up) }
  window.addEventListener('pointermove', move); window.addEventListener('pointerup', up)
}

type ResizeEdge = 'n' | 'e' | 's' | 'w' | 'ne' | 'se' | 'sw' | 'nw'
function beginResize(e: PointerEvent, edge: ResizeEdge) {
  if (props.maximized) return
  e.preventDefault(); e.stopPropagation(); emit('focus')
  const start = clampRect(props.rect)
  const sx = e.clientX; const sy = e.clientY
  const move = (ev: PointerEvent) => {
    const dx = ev.clientX - sx; const dy = ev.clientY - sy
    const viewportRight = window.innerWidth - margin
    const viewportBottom = window.innerHeight - bottomReserve - margin
    const startRight = start.x + start.width
    const startBottom = start.y + start.height
    let x = start.x; let y = start.y; let right = startRight; let bottom = startBottom
    if (edge.includes('w')) x = Math.max(margin, Math.min(startRight - 360, start.x + dx))
    if (edge.includes('e')) right = Math.min(viewportRight, Math.max(start.x + 360, startRight + dx))
    if (edge.includes('n')) y = Math.max(margin, Math.min(startBottom - 260, start.y + dy))
    if (edge.includes('s')) bottom = Math.min(viewportBottom, Math.max(start.y + 260, startBottom + dy))
    emit('update:rect', { x, y, width: right - x, height: bottom - y })
  }
  const up = () => { window.removeEventListener('pointermove', move); window.removeEventListener('pointerup', up) }
  window.addEventListener('pointermove', move); window.addEventListener('pointerup', up)
}

function onResize() { if (!props.maximized) emit('update:rect', clampRect(props.rect)) }
onMounted(() => window.addEventListener('resize', onResize))
onBeforeUnmount(() => window.removeEventListener('resize', onResize))
</script>

<template>
  <section v-show="modelValue && !minimized" class="work-window" :class="{ maximized }" :style="style" @pointerdown="emit('focus')">
    <header class="work-window-title" @pointerdown="beginDrag">
      <strong :title="title">{{ title }}</strong>
      <span class="work-window-spacer" />
      <slot name="actions" />
      <button type="button" :title="$t('缩小')" :aria-label="$t('缩小')" @click.stop="minimize">—</button>
      <button type="button" :title="$t(maximized ? '还原' : '最大化')" :aria-label="$t(maximized ? '还原' : '最大化')" @click.stop="toggleMaximize">{{ maximized ? '❐' : '□' }}</button>
      <button type="button" class="close" :title="$t('关闭')" :aria-label="$t('关闭')" @click.stop="requestClose">×</button>
    </header>
    <div v-show="!minimized" class="work-window-body"><slot /></div>
    <i v-for="edge in ['n','e','s','w','ne','se','sw','nw']" :key="edge"
       class="resize-handle" :class="`resize-${edge}`" @pointerdown="beginResize($event, edge as ResizeEdge)" />
  </section>
</template>

<style scoped>
.work-window { position: fixed; display: flex; flex-direction: column; min-width: 360px; min-height: 260px; overflow: hidden;
  border: 1px solid #cbd5e1; border-radius: 10px; background: #f8fafc; box-shadow: 0 18px 50px rgba(15,23,42,.23); }
.work-window-title { height: 38px; flex: 0 0 38px; display: flex; align-items: center; gap: 5px; padding: 0 7px 0 13px;
  color: #24364b; background: linear-gradient(180deg,#fff,#eef3f8); border-bottom: 1px solid #d7e0ea; cursor: move; user-select: none; }
.work-window-title strong { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: 14px; }
.work-window-spacer { flex: 1; }
.work-window-title button { width: 30px; height: 26px; padding: 0; border: 0; border-radius: 5px; background: transparent; color: #526579; font: 16px/26px "Segoe UI"; cursor: pointer; }
.work-window-title button:hover { background: #dfe8f2; color: #14365c; }
.work-window-title button.close:hover { background: #d9363e; color: #fff; }
.work-window-body { flex: 1; min-height: 0; overflow: hidden; background: #f5f7fa; }
.resize-handle { position: absolute; z-index: 8; display: block; }
.resize-n, .resize-s { left: 9px; right: 9px; height: 7px; cursor: ns-resize; }
.resize-n { top: -3px; } .resize-s { bottom: -3px; }
.resize-e, .resize-w { top: 9px; bottom: 9px; width: 7px; cursor: ew-resize; }
.resize-e { right: -3px; } .resize-w { left: -3px; }
.resize-ne, .resize-se, .resize-sw, .resize-nw { width: 13px; height: 13px; }
.resize-ne { top: -3px; right: -3px; cursor: nesw-resize; }
.resize-se { right: -3px; bottom: -3px; cursor: nwse-resize; }
.resize-sw { left: -3px; bottom: -3px; cursor: nesw-resize; }
.resize-nw { top: -3px; left: -3px; cursor: nwse-resize; }
.maximized .resize-handle { display: none; }
.maximized { border-radius: 7px; }
@media (max-width: 900px) { .work-window { min-width: 0; } }
</style>
