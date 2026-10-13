import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { flushPromises, shallowMount } from '@vue/test-utils'
import Points from './Points.vue'
import { api } from '../api'

vi.mock('vue-router', () => ({ useRoute: () => ({ query: {} }) }))
vi.mock('../utils/realtimeStream', () => ({ RealtimeStreamClient: class { start() {} stop() {} requestSnapshot() {} } }))
vi.mock('../api', () => ({ api: {
  projectTagScope: vi.fn(async () => ({ realtimeRevision: 'test', groups: [], total: 2_501,
    definitions: [
      ...Array.from({ length: 2_500 }, (_, i) => ({ id: `Line.Plc.T${i}`, dataType: 'Int16', access: 'ReadWrite', address: String(i) })),
      { id: 'Line.Plc.Arr', dataType: 'Int16', access: 'Read', address: 'A', array: { elementType: 'Int16', dimensions: [4] } },
    ] })),
  tagScanTimings: vi.fn(async () => ({ items: [], capturedAt: '' })),
  readValues: vi.fn(async (ids: string[]) => ({
    goodCount: ids.length, sampleCount: ids.length, error: null, elapsedMs: 3,
    values: ids.map(id => ({ id, value: 42, quality: 'Good', qualityCode: 0, good: true, sourceTs: '2026-09-17T10:00:00Z' })),
  })),
} }))

const mountQc = () => shallowMount(Points, { props: { q: 'Line.Plc' }, global: {
  stubs: ['ElAutoResizer', 'ElTreeV2', 'ElTableV2', 'ElTableColumn', 'ElTable', 'ElInput', 'ElButton', 'ElDialog'],
} })

beforeEach(() => { try { localStorage.clear() } catch {} })
afterEach(() => { vi.useRealTimers(); vi.clearAllMocks() })

it('opening a scope reads the PLC once, then stays quiet until the timed refresh is set', async () => {
  vi.useFakeTimers()
  const wrapper = mountQc()
  await flushPromises()
  // 打开范围拉一次当前值(实时流是被动的，不拉就一直空着)
  const afterOpen = vi.mocked(api.readValues).mock.calls.length
  expect(afterOpen).toBeGreaterThan(0)
  await vi.advanceTimersByTimeAsync(120_000)
  expect(vi.mocked(api.readValues).mock.calls.length).toBe(afterOpen)   // 定时刷新为 0：之后不再自己读
  wrapper.unmount()
})

it('refresh reads the scalar points of the current scope in bounded chunks and shows the PLC values', async () => {
  const wrapper = mountQc()
  await flushPromises()
  const vm = wrapper.vm as any
  vi.mocked(api.readValues).mockClear()   // 打开范围时已经拉过一次
  await vm.refreshFromPlc()
  const calls = vi.mocked(api.readValues).mock.calls.map(call => call[0])
  expect(calls.map(ids => ids.length)).toEqual([2_000, 500])
  expect(calls.flat()).not.toContain('Line.Plc.Arr')
  const row = vm.gridRows.find((item: any) => item.itemId === 'Line.Plc.T7')
  expect(row.value).toBe('42')
  expect(row.good).toBe(true)
  wrapper.unmount()
})

it('a refresh already in progress is not started twice', async () => {
  let release!: () => void
  vi.mocked(api.readValues).mockImplementationOnce((ids: string[]) => new Promise(resolve => {
    release = () => resolve({ goodCount: 0, sampleCount: 0, error: null, elapsedMs: 1, values: [] } as any)
  }))
  const wrapper = mountQc()
  await flushPromises()
  const vm = wrapper.vm as any
  const first = vm.refreshFromPlc(['Line.Plc.T1'])
  await vm.refreshFromPlc(['Line.Plc.T2'])
  expect(api.readValues).toHaveBeenCalledTimes(1)
  release()
  await first
  wrapper.unmount()
})

it('timed refresh uses the milliseconds typed by the user and pauses while QC is minimized', async () => {
  vi.useFakeTimers()
  const wrapper = mountQc()
  await flushPromises()
  const vm = wrapper.vm as any
  vi.mocked(api.readValues).mockClear()   // 打开范围时已经拉过一次
  vm.applyAutoRefreshInput('5000')
  await flushPromises()
  await vi.advanceTimersByTimeAsync(4_999)
  expect(api.readValues).not.toHaveBeenCalled()
  await vi.advanceTimersByTimeAsync(1)
  await flushPromises()
  expect(api.readValues).toHaveBeenCalledTimes(2)   // 2500 点分两批，属于同一次刷新
  expect(localStorage.getItem('igw.qc.autoRefreshMs')).toBe('5000')

  await wrapper.setProps({ suspended: true })
  await vi.advanceTimersByTimeAsync(30_000)
  expect(api.readValues).toHaveBeenCalledTimes(2)

  await wrapper.setProps({ suspended: false })
  await vi.advanceTimersByTimeAsync(5_000)
  await flushPromises()
  expect(api.readValues).toHaveBeenCalledTimes(4)

  vm.applyAutoRefreshInput('0')
  await flushPromises()
  await vi.advanceTimersByTimeAsync(60_000)
  expect(api.readValues).toHaveBeenCalledTimes(4)
  wrapper.unmount()
})

it('clamps the typed period: blank and too small become the 50ms floor, 0 turns it off', async () => {
  const wrapper = mountQc()
  await flushPromises()
  const vm = wrapper.vm as any

  vm.applyAutoRefreshInput('10')
  expect(vm.autoRefreshMs).toBe(50)
  vm.applyAutoRefreshInput('250')
  expect(vm.autoRefreshMs).toBe(250)
  vm.applyAutoRefreshInput('')
  expect(vm.autoRefreshMs).toBe(0)
  vm.applyAutoRefreshInput('abc')
  expect(vm.autoRefreshMs).toBe(0)
  vm.applyAutoRefreshInput('99999999')
  expect(vm.autoRefreshMs).toBe(3_600_000)
  wrapper.unmount()
})

it('reading a single row does not read the whole scope', async () => {
  const wrapper = mountQc()
  await flushPromises()
  const vm = wrapper.vm as any
  await vm.refreshFromPlc(['Line.Plc.T3'])
  expect(api.readValues).toHaveBeenCalledWith(['Line.Plc.T3'])
  wrapper.unmount()
})
