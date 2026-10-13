import { describe, expect, it } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { createRealtimeStore, useRealtimeStore } from './realtime'

const frame = (event: string, sequence: number, data: unknown) => ({
  event,
  id: `stream-1:${sequence}`,
  data: JSON.stringify(data),
})

describe('atomic realtime store', () => {
  it('keeps the primary runtime connection state independent from page-scoped streams', () => {
    setActivePinia(createPinia())
    const store = useRealtimeStore()
    store.configure('rev-1')
    store.setConnectionState('stale', 'runtime')
    store.setConnectionState('live', 'mes-status')

    expect(store.status).toBe('stale')
    expect(store.streamStates.get('mes-status')).toBe('live')
  })

  it('publishes no partial snapshot and replays buffered deltas after completion', () => {
    const store = createRealtimeStore('rev-1')
    const begin = store.apply(frame('snapshot-begin', 1, { snapshotId: 'snap-1', totalValues: 2, chunkCount: 2 }))
    const firstChunk = store.apply(frame('snapshot-chunk', 2, { snapshotId: 'snap-1', chunkIndex: 0, values: [{ id: 'A', value: 1 }] }))
    const buffered = store.apply(frame('tag-delta', 3, { values: [{ id: 'A', value: 2 }] }))
    expect(store.values.size).toBe(0)
    expect(begin.valuesChanged).toBe(false)
    expect(firstChunk.valuesChanged).toBe(false)
    expect(buffered.valuesChanged).toBe(false)

    store.apply(frame('snapshot-chunk', 4, { snapshotId: 'snap-1', chunkIndex: 1, values: [{ id: 'B', value: 3 }] }))
    const complete = store.apply(frame('snapshot-complete', 5, { snapshotId: 'snap-1', totalValues: 2, chunkCount: 2 }))

    expect(complete.valuesChanged).toBe(true)
    expect(complete.snapshotCompleted).toBe(true)
    expect(store.values.get('A')?.value).toBe(2)
    expect(store.values.get('B')?.value).toBe(3)
  })

  it('rolls back an incomplete snapshot on reset-required', () => {
    const store = createRealtimeStore('rev-1')
    store.apply(frame('tag-delta', 1, { values: [{ id: 'A', value: 1 }] }))
    store.apply(frame('snapshot-begin', 2, { snapshotId: 'snap-2', totalValues: 1, chunkCount: 1 }))
    store.apply(frame('snapshot-chunk', 3, { snapshotId: 'snap-2', chunkIndex: 0, values: [{ id: 'A', value: 9 }] }))
    store.apply(frame('reset-required', 4, { reason: 'backpressure' }))

    expect(store.values.get('A')?.value).toBe(1)
    expect(store.status).toBe('resyncing')
  })

  it('retains current device and MES state without evicting ordered flow events', () => {
    const store = createRealtimeStore('rev-1')
    store.apply(frame('runtime-event', 1, {
      kind: 'device-state', payload: { name: 'Line.PLC', state: 'Connected' },
    }))
    store.apply(frame('runtime-event', 2, {
      kind: 'mes-task-state', payload: { id: 'task-1', ok: 8 },
    }))
    const transition = store.apply(frame('runtime-event', 3, {
      kind: 'mes-flow-transition', payload: { taskId: 'task-1', value: { nodeId: 'upload' } },
    }))

    expect(store.deviceStates.get('Line.PLC')?.state).toBe('Connected')
    expect(store.mesTasks.get('task-1')?.ok).toBe(8)
    expect(store.runtimeEvents).toHaveLength(1)
    expect(transition.runtimeKind).toBe('mes-flow-transition')
    expect(transition.runtimeEvent?.payload?.taskId).toBe('task-1')
  })

  it('returns the already parsed delta values to the page consumer', () => {
    const store = createRealtimeStore('rev-1')
    const applied = store.apply(frame('tag-delta', 1, { values: [{ id: 'A', value: 7 }] }))

    expect(applied.valuesChanged).toBe(true)
    expect(applied.values).toEqual([{ id: 'A', value: 7 }])
  })

  it('removes retained MES task state when a server tombstone arrives', () => {
    const store = createRealtimeStore('rev-1')
    store.apply(frame('runtime-event', 1, {
      kind: 'mes-task-state', payload: { id: 'task-1', ok: 8 },
    }))

    store.apply(frame('runtime-event', 2, {
      kind: 'mes-task-removed', payload: { id: 'task-1' },
    }))

    expect(store.mesTasks.has('task-1')).toBe(false)
  })
})
