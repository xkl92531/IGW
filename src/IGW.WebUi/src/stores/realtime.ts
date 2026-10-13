import { defineStore } from 'pinia'
import { ref, shallowRef, triggerRef } from 'vue'
import type { RealtimeFrame } from '../utils/realtimeFrame'

export type RealtimeConnectionState =
  | 'idle' | 'connecting' | 'live' | 'reconnecting' | 'stale'
  | 'resyncing' | 'project-changed' | 'capacity-limited' | 'unauthorized' | 'configuration-error'
  | 'runtime-unavailable'

export interface RealtimeTagValue {
  id: string
  value: unknown
  valueKind?: string
  quality?: string
  qualityCode?: number
  good?: boolean
  sourceTs?: string
  serverTs?: string
}

interface SnapshotState {
  id: string
  totalValues: number
  chunkCount: number
  receivedChunks: Set<number>
  values: Map<string, RealtimeTagValue>
  bufferedDeltas: RealtimeTagValue[][]
}

export interface RealtimeApplyResult {
  valuesChanged: boolean
  snapshotCompleted?: boolean
  values?: RealtimeTagValue[]
  runtimeKind?: string
  runtimeEvent?: any
}

const unchanged = (): RealtimeApplyResult => ({ valuesChanged: false })

export class RealtimeStateStore {
  values = new Map<string, RealtimeTagValue>()
  deviceStates = new Map<string, any>()
  mesTasks = new Map<string, any>()
  mesEnabled = false
  status: RealtimeConnectionState = 'idle'
  runtimeEvents: unknown[] = []
  private snapshot: SnapshotState | null = null

  constructor(public projectRevision: string) {}

  apply(frame: RealtimeFrame): RealtimeApplyResult {
    let payload: any
    try { payload = frame.data ? JSON.parse(frame.data) : {} }
    catch { this.status = 'resyncing'; return unchanged() }

    switch (frame.event) {
      case 'hello':
        if (payload.projectRevision && payload.projectRevision !== this.projectRevision) {
          this.status = 'project-changed'
          return unchanged()
        }
        this.status = 'live'
        return unchanged()
      case 'snapshot-begin':
        this.snapshot = {
          id: String(payload.snapshotId || ''),
          totalValues: Number(payload.totalValues || 0),
          chunkCount: Number(payload.chunkCount || 0),
          receivedChunks: new Set(),
          values: new Map(),
          bufferedDeltas: [],
        }
        this.status = 'resyncing'
        return unchanged()
      case 'snapshot-chunk': {
        const current = this.snapshot
        if (!current || current.id !== payload.snapshotId) return this.invalidateSnapshot()
        const index = Number(payload.chunkIndex)
        if (!Number.isInteger(index) || index < 0 || index >= current.chunkCount) return this.invalidateSnapshot()
        for (const value of valuesOf(payload)) current.values.set(value.id, value)
        current.receivedChunks.add(index)
        return unchanged()
      }
      case 'snapshot-complete': {
        const current = this.snapshot
        if (!current || current.id !== payload.snapshotId
          || current.receivedChunks.size !== current.chunkCount
          || current.values.size !== current.totalValues) return this.invalidateSnapshot()
        const next = current.values
        for (const delta of current.bufferedDeltas) applyValues(next, delta)
        this.values = next
        this.snapshot = null
        this.status = 'live'
        return { valuesChanged: true, snapshotCompleted: true }
      }
      case 'tag-delta': {
        const values = valuesOf(payload)
        if (this.snapshot) {
          this.snapshot.bufferedDeltas.push(values)
          return unchanged()
        }
        applyValues(this.values, values)
        return { valuesChanged: values.length > 0, values }
      }
      case 'runtime-event': {
        let retainedState = false
        if (payload.kind === 'device-state' && payload.payload?.name) {
          this.deviceStates.set(payload.payload.name, payload.payload)
          retainedState = true
        } else if (payload.kind === 'mes-task-state' && payload.payload?.id) {
          this.mesTasks.set(payload.payload.id, payload.payload)
          retainedState = true
        } else if (payload.kind === 'mes-task-removed' && payload.payload?.id) {
          this.mesTasks.delete(payload.payload.id)
          retainedState = true
        } else if (payload.kind === 'mes-availability') {
          this.mesEnabled = !!payload.payload?.enabled
          retainedState = true
        }
        // 高频状态已经按实体合并保存；有业务顺序的 MES 流程事件才进入事件队列，
        // 避免百万点/多设备状态刷新挤掉用户正在查看的流程轨迹。
        if (!retainedState) {
          this.runtimeEvents.push(payload)
          if (this.runtimeEvents.length > 1_000) this.runtimeEvents.splice(0, this.runtimeEvents.length - 1_000)
        }
        return {
          valuesChanged: false,
          runtimeKind: String(payload.kind || ''),
          runtimeEvent: payload,
        }
      }
      case 'reset-required':
        return this.invalidateSnapshot()
      case 'project-changed':
        this.snapshot = null
        this.status = 'project-changed'
        return unchanged()
      default:
        return unchanged()
    }
  }

  setConnectionState(state: RealtimeConnectionState) { this.status = state }

  clearMesTasks() { this.mesTasks.clear() }

  private invalidateSnapshot(): RealtimeApplyResult {
    this.snapshot = null
    this.status = 'resyncing'
    return unchanged()
  }
}

function valuesOf(payload: any): RealtimeTagValue[] {
  if (!Array.isArray(payload?.values)) return []
  return payload.values.filter((value: any) => value && typeof value.id === 'string')
}

function applyValues(target: Map<string, RealtimeTagValue>, values: RealtimeTagValue[]) {
  for (const value of values) target.set(value.id, value)
}

export function createRealtimeStore(projectRevision: string) {
  return new RealtimeStateStore(projectRevision)
}

export const useRealtimeStore = defineStore('realtime', () => {
  let engine = createRealtimeStore('')
  const projectRevision = ref('')
  const status = ref<RealtimeConnectionState>('idle')
  const values = shallowRef(engine.values)
  const runtimeEvents = shallowRef(engine.runtimeEvents)
  const deviceStates = shallowRef(engine.deviceStates)
  const mesTasks = shallowRef(engine.mesTasks)
  const mesEnabled = ref(engine.mesEnabled)
  const streamStates = shallowRef(new Map<string, RealtimeConnectionState>([['runtime', 'idle']]))

  function updateStreamState(streamKey: string, state: RealtimeConnectionState) {
    streamStates.value.set(streamKey, state)
    triggerRef(streamStates)
    if (streamKey === 'runtime') status.value = state
  }

  function configure(revision: string) {
    projectRevision.value = revision
    engine = createRealtimeStore(revision)
    status.value = engine.status
    values.value = engine.values
    runtimeEvents.value = engine.runtimeEvents
    deviceStates.value = engine.deviceStates
    mesTasks.value = engine.mesTasks
    mesEnabled.value = engine.mesEnabled
    streamStates.value = new Map([['runtime', 'idle']])
  }

  function apply(frame: RealtimeFrame, streamKey = 'runtime') {
    const previousPrimaryState = engine.status
    const applied = engine.apply(frame)
    const frameState = engine.status
    if (streamKey === 'runtime') updateStreamState(streamKey, frameState)
    else {
      engine.status = previousPrimaryState
      if (frame.event === 'hello') updateStreamState(streamKey, 'live')
      else if (frame.event === 'project-changed') updateStreamState(streamKey, 'project-changed')
      else if (frame.event === 'reset-required') updateStreamState(streamKey, 'resyncing')
    }
    mesEnabled.value = engine.mesEnabled
    if (applied.valuesChanged) {
      values.value = engine.values
      triggerRef(values)
    } else if (frame.event === 'runtime-event') {
      if (applied.runtimeKind === 'device-state') {
        deviceStates.value = engine.deviceStates
        triggerRef(deviceStates)
      } else if (applied.runtimeKind === 'mes-task-state' || applied.runtimeKind === 'mes-task-removed') {
        mesTasks.value = engine.mesTasks
        triggerRef(mesTasks)
      } else if (applied.runtimeKind !== 'mes-availability') {
        runtimeEvents.value = engine.runtimeEvents
        triggerRef(runtimeEvents)
      }
    }
    return applied
  }

  function setConnectionState(state: RealtimeConnectionState, streamKey = 'runtime') {
    if (streamKey === 'runtime') engine.setConnectionState(state)
    updateStreamState(streamKey, state)
  }

  function clearMesTasks() {
    engine.clearMesTasks()
    mesTasks.value = engine.mesTasks
    triggerRef(mesTasks)
  }

  return { projectRevision, status, streamStates, values, deviceStates, mesTasks, mesEnabled, runtimeEvents, configure, apply, setConnectionState, clearMesTasks }
})
