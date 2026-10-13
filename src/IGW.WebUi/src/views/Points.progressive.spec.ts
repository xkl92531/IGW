import { afterEach, expect, it, vi } from 'vitest'
import { flushPromises, shallowMount } from '@vue/test-utils'
import Points from './Points.vue'
import { api } from '../api'
vi.mock('vue-router', () => ({ useRoute: () => ({ query: {} }) }))
vi.mock('../utils/realtimeStream', () => ({ RealtimeStreamClient: class { start() {} stop() {} requestSnapshot() {} } }))
vi.mock('../api', () => ({ api: { projectTagScope: vi.fn(), tagScanTimings: vi.fn(async () => ({ items: [] })) } }))
const definition = (id: string) => ({ id, name: id, dataType: 'Float', access: 'Read', address: id })
const mountPoints = () => shallowMount(Points, { props: { q: 'Line.Plc.DB' }, global: {
  stubs: ['ElAutoResizer', 'ElTreeV2', 'ElTableV2', 'ElTableColumn', 'ElTable', 'ElInput', 'ElButton', 'ElDialog'],
} })
afterEach(() => { vi.restoreAllMocks(); vi.clearAllMocks() })

it('shows the first page while a later definition page is still in flight', async () => {
  let finish!: (result: any) => void
  vi.mocked(api.projectTagScope).mockResolvedValueOnce({ realtimeRevision: 'r', groups: [], total: 2, definitions: [definition('A')], nextCursor: 'next' } as any)
    .mockReturnValueOnce(new Promise(resolve => { finish = resolve }))
  const wrapper = mountPoints()
  try {
    await flushPromises()
    expect((wrapper.vm as any).gridRows.map((row: any) => row.itemId)).toEqual(['A'])
    finish({ realtimeRevision: 'r', groups: [], total: 2, definitions: [definition('B')] })
    await flushPromises()
    expect((wrapper.vm as any).gridRows.map((row: any) => row.itemId)).toEqual(['A', 'B'])
  } finally { wrapper.unmount() }
})

it('keeps a million-point scope lazy and searches it on the server', async () => {
  vi.useFakeTimers()
  vi.mocked(api.projectTagScope)
    .mockResolvedValueOnce({ realtimeRevision: 'r', groups: [], total: 1_000_000,
      definitions: Array.from({ length: 1_000 }, (_, i) => definition(`T${i}`)), nextCursor: 'page-2' } as any)
    .mockResolvedValueOnce({ realtimeRevision: 'r', groups: [], total: 1,
      definitions: [definition('Target999999')], nextCursor: null } as any)
  const wrapper = mountPoints()
  try {
    await flushPromises()
    expect(api.projectTagScope).toHaveBeenCalledTimes(1)
    expect((wrapper.vm as any).gridRows).toHaveLength(1_000)

    await wrapper.get('[data-test="qc-scope-search"]').setValue('Target999999')
    await vi.advanceTimersByTimeAsync(300)
    await flushPromises()

    expect(api.projectTagScope).toHaveBeenLastCalledWith(
      expect.objectContaining({ root: 'Line.Plc.DB', q: 'Target999999', limit: 1_000 }),
      expect.any(AbortSignal),
    )
    expect((wrapper.vm as any).gridRows.map((row: any) => row.itemId)).toEqual(['Target999999'])
  } finally {
    wrapper.unmount()
    vi.useRealTimers()
  }
})

it('ignores an obsolete second page after the selected scope changes', async () => {
  let finishOldPage!: (result: any) => void
  vi.mocked(api.projectTagScope)
    .mockResolvedValueOnce({ realtimeRevision: 'old-r', groups: [], total: 2,
      definitions: [definition('OldA')], nextCursor: 'old-next' } as any)
    .mockReturnValueOnce(new Promise(resolve => { finishOldPage = resolve }))
    .mockResolvedValueOnce({ realtimeRevision: 'new-r', groups: [], total: 1,
      definitions: [definition('NewA')] } as any)
  const wrapper = mountPoints()
  try {
    await flushPromises()
    expect((wrapper.vm as any).gridRows.map((row: any) => row.itemId)).toEqual(['OldA'])

    await wrapper.setProps({ q: 'Line.Plc.NewDB' })
    await flushPromises()
    expect((wrapper.vm as any).gridRows.map((row: any) => row.itemId)).toEqual(['NewA'])

    finishOldPage({ realtimeRevision: 'old-r', groups: [], total: 2,
      definitions: [definition('OldLate')] })
    await flushPromises()
    expect((wrapper.vm as any).gridRows.map((row: any) => row.itemId)).toEqual(['NewA'])
  } finally { wrapper.unmount() }
})

it('does not apply a queued value from the previous stream after realtime restarts', async () => {
  const frames: FrameRequestCallback[] = []
  vi.spyOn(window, 'requestAnimationFrame').mockImplementation(callback => { frames.push(callback); return frames.length })
  vi.spyOn(window, 'cancelAnimationFrame').mockImplementation(() => {})
  vi.mocked(api.projectTagScope).mockResolvedValue({ realtimeRevision: 'r', groups: [], total: 1,
    definitions: [definition('A')] } as any)
  const wrapper = mountPoints()
  try {
    await flushPromises()
    const vm = wrapper.vm as any
    vm.queueUiValues([{ id: 'A', value: 'old', good: true, quality: 'Good' }])
    expect(frames).toHaveLength(1)

    vm.startRealtime()
    frames.shift()!(0)

    expect(vm.gridRows[0].value).toBe('—')
    expect(vm.gridRows[0].uc).toBe(0)
  } finally { wrapper.unmount() }
})

it('spreads a large SSE delta across frames without dropping the latest value', async () => {
  const frames: FrameRequestCallback[] = []
  vi.spyOn(window, 'requestAnimationFrame').mockImplementation(callback => { frames.push(callback); return frames.length })
  vi.spyOn(window, 'cancelAnimationFrame').mockImplementation(() => {})
  vi.mocked(api.projectTagScope).mockResolvedValue({ realtimeRevision: 'r', groups: [], total: 805,
    definitions: Array.from({ length: 805 }, (_, i) => definition(`T${i}`)) } as any)
  const wrapper = mountPoints()
  try {
    await flushPromises()
    const vm = wrapper.vm as any
    vm.queueUiValues(Array.from({ length: 805 }, (_, i) => ({ id: `T${i}`, value: i, good: true, quality: 'Good' })))
    frames.shift()!(0)
    expect(vm.gridRows.filter((row: any) => row.uc > 0).length).toBeLessThanOrEqual(400)
    vm.queueUiValues([{ id: 'T804', value: 999, good: true, quality: 'Good' }])
    while (frames.length) frames.shift()!(0)
    expect(vm.gridRows.every((row: any) => row.uc > 0)).toBe(true)
    expect(vm.gridRows[804].value).toBe('999')
  } finally { wrapper.unmount() }
})

it('applies a live delta before continuing a large snapshot', async () => {
  const frames: FrameRequestCallback[] = []
  vi.spyOn(window, 'requestAnimationFrame').mockImplementation(callback => { frames.push(callback); return frames.length })
  vi.spyOn(window, 'cancelAnimationFrame').mockImplementation(() => {})
  vi.mocked(api.projectTagScope).mockResolvedValue({ realtimeRevision: 'r', groups: [], total: 805,
    definitions: Array.from({ length: 805 }, (_, i) => definition(`T${i}`)) } as any)
  const wrapper = mountPoints()
  try {
    await flushPromises()
    const vm = wrapper.vm as any
    const snapshotValues = Array.from({ length: 805 }, (_, i) => ({ id: `T${i}`, value: i, good: true, quality: 'Good' }))
    vm.onRealtimeFrame({ event: 'snapshot-begin', data: JSON.stringify({ snapshotId: 's', totalValues: 805, chunkCount: 1 }) })
    vm.onRealtimeFrame({ event: 'snapshot-chunk', data: JSON.stringify({ snapshotId: 's', chunkIndex: 0, values: snapshotValues }) })
    vm.onRealtimeFrame({ event: 'snapshot-complete', data: JSON.stringify({ snapshotId: 's' }) })
    vm.onRealtimeFrame({ event: 'tag-delta', data: JSON.stringify({ values: [{ id: 'T804', value: 999, good: true, quality: 'Good' }] }) })

    frames.shift()!(0)

    expect(vm.gridRows[804].value).toBe('999')
    expect(vm.gridRows.filter((row: any) => row.uc > 0).length).toBeLessThanOrEqual(400)
  } finally { wrapper.unmount() }
})

it('refreshes the sample time without inflating update count when value and quality are unchanged', async () => {
  vi.mocked(api.projectTagScope).mockResolvedValue({ realtimeRevision: 'r', groups: [], total: 1,
    definitions: [definition('A')] } as any)
  const wrapper = mountPoints()
  try {
    await flushPromises()
    const vm = wrapper.vm as any
    const row = vm.gridRows[0]
    vm.applyVal(row, { id: 'A', value: 7, good: true, quality: 'Good', sourceTs: '2026-09-17T01:02:03.004Z' })
    const originalTime = row.ts

    vm.applyVal(row, { id: 'A', value: 7, good: true, quality: 'Good', sourceTs: '2026-09-17T01:02:04.005Z' })

    expect(row.ts).not.toBe(originalTime)
    expect(row.ts).toMatch(/:04\.005$/)
    expect(row.uc).toBe(1)
  } finally { wrapper.unmount() }
})
