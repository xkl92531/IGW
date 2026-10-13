import { afterEach, expect, it, vi } from 'vitest'
import { flushPromises, shallowMount } from '@vue/test-utils'
import Points from './Points.vue'
import { api } from '../api'

vi.mock('vue-router', () => ({ useRoute: () => ({ query: {} }) }))
vi.mock('../utils/realtimeStream', () => ({ RealtimeStreamClient: class { start() {} stop() {} requestSnapshot() {} } }))
vi.mock('../api', () => ({ api: {
  projectTagScope: vi.fn(async () => ({ realtimeRevision: 'test', groups: [], total: 500,
    definitions: Array.from({ length: 500 }, (_, i) => ({ id: `Line.Plc.T${i}`, dataType: 'Float', access: 'Read', address: String(i) })) })),
  tagScanTimings: vi.fn(async () => ({ items: [{ tagId: 'Line.Plc.T10', device: 'Line.Plc', mode: 'polling',
    batch: { sequence: 5, startedAt: '2026-09-05T10:00:00Z', configuredPeriodMs: 100, actualIntervalMs: 101,
      totalMs: 23, driverMs: 20, localMs: 3, tagCount: 46, status: 'good' } }], capturedAt: '' })),
} }))
afterEach(() => { vi.useRealTimers(); vi.clearAllMocks() })

it('QC requests only visible rows and displays batch timing while pausing when minimized or hidden', async () => {
  vi.useFakeTimers()
  const original = Object.getOwnPropertyDescriptor(document, 'hidden')
  const originalHeight = Object.getOwnPropertyDescriptor(HTMLElement.prototype, 'clientHeight')
  Object.defineProperty(HTMLElement.prototype, 'clientHeight', { configurable: true, get: () => 332 })
  Object.defineProperty(document, 'hidden', { configurable: true, value: false })
  const wrapper = shallowMount(Points, { props: { q: 'Line.Plc' }, global: {
    stubs: ['ElAutoResizer', 'ElTreeV2', 'ElTableV2', 'ElTableColumn', 'ElTable', 'ElInput', 'ElButton', 'ElDialog'],
  } })
  try {
    await flushPromises()
    const vm = wrapper.vm as any
    const columnsBeforeTiming = vm.columns
    await vi.advanceTimersByTimeAsync(150)
    await flushPromises()
    expect(api.tagScanTimings).toHaveBeenCalledTimes(1)
    expect(vm.columns).not.toBe(columnsBeforeTiming)
    expect(vi.mocked(api.tagScanTimings).mock.calls[0]![0]).toHaveLength(15)
    const column = vm.columns.find((item: any) => item.key === 'scanTiming')
    const cell = column.cellRenderer({ rowData: { itemId: 'Line.Plc.T10' } })
    expect(cell.children).toBe('23.00 ms')
    expect(cell.props.title).toContain('同批共享耗时')
    expect(cell.props.title).toContain('46')
    expect(cell.props.title).toContain('20.00 / 3.00 ms')
    await wrapper.setProps({ suspended: true })
    await vi.advanceTimersByTimeAsync(10000)
    expect(api.tagScanTimings).toHaveBeenCalledTimes(1)
    await wrapper.setProps({ suspended: false })
    await vi.advanceTimersByTimeAsync(150)
    expect(api.tagScanTimings).toHaveBeenCalledTimes(2)
    Object.defineProperty(document, 'hidden', { configurable: true, value: true })
    document.dispatchEvent(new Event('visibilitychange'))
    await vi.advanceTimersByTimeAsync(10000)
    expect(api.tagScanTimings).toHaveBeenCalledTimes(2)
  } finally {
    wrapper.unmount()
    if (original) Object.defineProperty(document, 'hidden', original)
    else Reflect.deleteProperty(document, 'hidden')
    if (originalHeight) Object.defineProperty(HTMLElement.prototype, 'clientHeight', originalHeight)
    else Reflect.deleteProperty(HTMLElement.prototype, 'clientHeight')
  }
  await vi.advanceTimersByTimeAsync(10000)
  expect(api.tagScanTimings).toHaveBeenCalledTimes(2)
})
