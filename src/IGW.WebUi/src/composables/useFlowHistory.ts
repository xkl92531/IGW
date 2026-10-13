import { ref, type Ref } from 'vue'

/** 一条操作记录：before 是这次操作之前的流程快照，回退即恢复到它。 */
export interface FlowHistoryEntry {
  id: number
  label: string
  at: number
  before: string
}

interface OwnerState {
  baseline: string
  entries: FlowHistoryEntry[]
}

interface Pending {
  owner: object
  entry: FlowHistoryEntry
  timer: ReturnType<typeof setTimeout>
  lastAt: number
}

export interface FlowHistoryOptions<T extends object> {
  /** 当前正在编辑的任务；null 表示没有可记录的流程。 */
  owner: Ref<T | null | undefined>
  /** 读取任务流程的序列化快照(节点 + 连线)。 */
  snapshot: (owner: T) => string
  /** 把快照写回任务流程。 */
  apply: (owner: T, snapshot: string) => void
  limit?: number
  /** 同名操作在这个间隔内连续发生时合并成一条(连续输入、连续拖动)。 */
  mergeMs?: number
  /** 操作后延迟这么久再取快照：大流程序列化有成本，连续编辑只取一次。 */
  commitDelayMs?: number
  now?: () => number
  /** 单份快照超过这个字符数就不记录历史，避免超大流程(上万字段)占用大量内存。 */
  maxSnapshotChars?: number
}

/**
 * 采集流程的操作历史：每个任务各自保留最近 limit 次改动，任意一条都能回退到它之前的状态；
 * 回退本身也记为一次操作，因此回退错了还能再回退回去。只在内存里保存，刷新页面即清空。
 */
export function useFlowHistory<T extends object>(options: FlowHistoryOptions<T>) {
  const limit = options.limit ?? 5
  const mergeMs = options.mergeMs ?? 1500
  const commitDelayMs = options.commitDelayMs ?? 400
  const now = options.now ?? Date.now
  const maxSnapshotChars = options.maxSnapshotChars ?? 4_000_000
  /** 当前任务的流程太大，未记录操作历史。 */
  const oversized = ref(false)
  const states = new WeakMap<object, OwnerState>()
  const entries = ref<FlowHistoryEntry[]>([])
  let pending: Pending | null = null
  let nextId = 1

  function stateOf(owner: T): OwnerState {
    let state = states.get(owner)
    if (!state) {
      state = { baseline: options.snapshot(owner), entries: [] }
      states.set(owner, state)
    }
    return state
  }

  function publish() {
    const owner = options.owner.value
    const state = owner ? states.get(owner) : undefined
    entries.value = state ? [...state.entries].reverse() : []
    oversized.value = !!state && state.baseline.length > maxSnapshotChars
  }

  /** 任务切换或载入后调用：以当前内容作为后续第一次改动的“之前”。 */
  function track() {
    flush()
    const owner = options.owner.value
    if (owner) stateOf(owner)
    publish()
  }

  function commit() {
    const current = pending
    if (!current) return
    pending = null
    clearTimeout(current.timer)
    const owner = current.owner as T
    const state = stateOf(owner)
    const after = options.snapshot(owner)
    if (after === current.entry.before) return
    if (after.length > maxSnapshotChars || current.entry.before.length > maxSnapshotChars) {
      state.entries = []
      state.baseline = after
      if (options.owner.value === owner) publish()
      return
    }
    state.entries.push(current.entry)
    if (state.entries.length > limit) state.entries.splice(0, state.entries.length - limit)
    state.baseline = after
    if (options.owner.value === owner) publish()
  }

  function flush() { commit() }

  /** 一次改动已经发生后调用。 */
  function record(label: string) {
    const owner = options.owner.value
    if (!owner) return
    const at = now()
    if (pending && pending.owner === owner && pending.entry.label === label && at - pending.lastAt <= mergeMs) {
      pending.lastAt = at
      clearTimeout(pending.timer)
      pending.timer = setTimeout(commit, commitDelayMs)
      return
    }
    commit()
    const state = stateOf(owner)
    pending = {
      owner,
      entry: { id: nextId++, label, at, before: state.baseline },
      lastAt: at,
      timer: setTimeout(commit, commitDelayMs),
    }
  }

  /** 回退到某条操作之前；返回是否真的改动了流程。 */
  function restore(entryId: number, label: string): boolean {
    flush()
    const owner = options.owner.value
    if (!owner) return false
    const state = stateOf(owner)
    const target = state.entries.find(entry => entry.id === entryId)
    if (!target) return false
    const current = options.snapshot(owner)
    if (current === target.before) return false
    options.apply(owner, target.before)
    state.entries.push({ id: nextId++, label, at: now(), before: current })
    if (state.entries.length > limit) state.entries.splice(0, state.entries.length - limit)
    state.baseline = options.snapshot(owner)
    publish()
    return true
  }

  function dispose() {
    if (pending) clearTimeout(pending.timer)
    pending = null
  }

  return { entries, oversized, track, record, flush, restore, dispose }
}
