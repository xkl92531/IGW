import { computed, onMounted, onUnmounted, ref, watch, type Ref } from 'vue'

export type AutoSaveState = 'idle' | 'pending' | 'saving' | 'saved' | 'error'

interface TimedAutoSaveOptions {
  dirty: Ref<boolean>
  saving: Ref<boolean>
  changeVersion: Ref<number>
  save: () => Promise<boolean>
  canSave?: () => boolean
  /** 保存周期；可传函数，设置变化后自动按新周期重新计时。 */
  intervalMs?: number | (() => number)
  /** 返回 false 时不做周期自动保存(手动保存和确定即保存不受影响)。 */
  enabled?: () => boolean
}

/**
 * 有修改时按固定周期保存。保存由页面以独立后台状态执行，不阻塞编辑交互。
 * 实际保存由页面提供，以便页面继续负责版本冲突、工程快照和热重载。
 */
export function useTimedAutoSave(options: TimedAutoSaveOptions) {
  const intervalMs = computed(() => {
    const value = typeof options.intervalMs === 'function' ? options.intervalMs() : options.intervalMs
    return Math.max(1_000, value ?? 30_000)
  })
  const state = ref<AutoSaveState>('idle')
  const lastSavedAt = ref<Date | null>(null)
  let intervalTimer: ReturnType<typeof setInterval> | undefined
  let stopped = false
  let activeRun: Promise<boolean> | null = null

  async function execute(force: boolean): Promise<boolean> {
    if (stopped || !options.dirty.value) return true
    if (options.saving.value) return false
    if (!force && options.canSave && !options.canSave()) return false
    state.value = 'saving'
    const ok = await options.save()
    if (ok) {
      lastSavedAt.value = new Date()
      state.value = options.dirty.value ? 'pending' : 'saved'
    } else {
      state.value = 'error'
    }
    return ok && !options.dirty.value
  }

  async function run(force = false): Promise<boolean> {
    if (activeRun) {
      await activeRun
      if (!options.dirty.value) return true
      return force ? run(true) : false
    }

    const current = execute(force)
    activeRun = current
    try { return await current }
    finally { if (activeRun === current) activeRun = null }
  }

  function markSaved() {
    lastSavedAt.value = new Date()
    state.value = options.dirty.value ? 'pending' : 'saved'
  }

  function onBeforeUnload(event: BeforeUnloadEvent) {
    if (!options.dirty.value) return
    event.preventDefault()
    event.returnValue = ''
  }

  watch(options.changeVersion, () => {
    if (options.dirty.value) state.value = 'pending'
  })
  watch(options.dirty, (value) => {
    state.value = value ? 'pending' : (lastSavedAt.value ? 'saved' : 'idle')
  })

  function restartTimer() {
    if (intervalTimer) clearInterval(intervalTimer)
    intervalTimer = setInterval(() => {
      if (options.enabled && !options.enabled()) return
      void run()
    }, intervalMs.value)
  }
  watch(intervalMs, () => { if (intervalTimer) restartTimer() })

  onMounted(() => {
    restartTimer()
    window.addEventListener('beforeunload', onBeforeUnload)
  })
  onUnmounted(() => {
    stopped = true
    if (intervalTimer) clearInterval(intervalTimer)
    window.removeEventListener('beforeunload', onBeforeUnload)
  })

  const lastSavedText = computed(() => lastSavedAt.value?.toLocaleTimeString('zh-CN', { hour12: false }) || '')

  return { state, lastSavedAt, lastSavedText, flush: () => run(true), markSaved }
}
