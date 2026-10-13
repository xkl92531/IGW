import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, shallowMount } from '@vue/test-utils'
import { ElMessage } from 'element-plus'
import Points from './Points.vue'
import { api } from '../api'

vi.mock('vue-router', () => ({ useRoute: () => ({ query: {} }) }))
vi.mock('../utils/realtimeStream', () => ({ RealtimeStreamClient: class {
  start() {} stop() {} requestSnapshot() {}
} }))
vi.mock('../api', () => ({ api: {
  projectTagScope: vi.fn(async () => ({
    projectRevision: 'test', realtimeRevision: 'test', root: 'Line.Plc', groups: [], total: 1,
    definitions: [{ id: 'Line.Plc.DataAccept', name: 'DataAccept', address: 'flag', dataType: 'Bool', access: 'ReadWrite' }],
  })),
  tagScanTimings: vi.fn(async () => ({ items: [] })),
  write: vi.fn(),
} }))

const tagId = 'Line.Plc.DataAccept'
const receipt = { id: tagId, written: true, normalizedValue: true, quality: 'Good', statusCode: 0, statusHex: '0x00000000' }
const wrappers: ReturnType<typeof shallowMount>[] = []

async function openWriter(initial: boolean) {
  const wrapper = shallowMount(Points, { props: { q: 'Line.Plc' }, global: {
    directives: { loading: () => {} },
    stubs: ['ElAutoResizer', 'ElTreeV2', 'ElTableV2', 'ElTableColumn', 'ElTable', 'ElInput', 'ElButton', 'ElDialog'],
  } })
  wrappers.push(wrapper)
  await flushPromises()
  const vm = wrapper.vm as any
  vm.onRealtimeFrame({ event: 'hello', data: JSON.stringify({ projectRevision: 'test' }) })
  vm.applyVal(vm.gridRows[0], { id: tagId, value: initial, good: true })
  vm.openWrite(vm.gridRows[0])
  vm.wVal = 'true'
  return vm
}

beforeEach(() => {
  vi.useFakeTimers()
  vi.spyOn(ElMessage, 'success').mockImplementation(() => ({ close() {} }))
  vi.spyOn(ElMessage, 'warning').mockImplementation(() => ({ close() {} }))
  vi.spyOn(ElMessage, 'error').mockImplementation(() => ({ close() {} }))
})

afterEach(async () => {
  wrappers.splice(0).forEach(wrapper => wrapper.unmount())
  await flushPromises()
  vi.useRealTimers()
  vi.restoreAllMocks()
  vi.mocked(api.write).mockReset()
})

describe('Quick Client write completion', () => {
  it('displays a new delta before a large snapshot drains and never restores its older snapshot value', async () => {
    const ids = Array.from({ length: 1000 }, (_, index) => `Line.Plc.T${index}`)
    vi.mocked(api.projectTagScope).mockResolvedValueOnce({
      projectRevision: 'test', realtimeRevision: 'test', root: 'Line.Plc', groups: [], total: ids.length,
      definitions: ids.map(id => ({ id, name: id, address: id, dataType: 'Bool', access: 'ReadWrite' })),
    } as any)
    const vm = await openWriter(false)
    const frame = (event: string, payload: unknown) => vm.onRealtimeFrame({ event, data: JSON.stringify(payload) })
    frame('snapshot-begin', { snapshotId: 's', totalValues: ids.length, chunkCount: 1 })
    frame('snapshot-chunk', { snapshotId: 's', chunkIndex: 0, values: ids.map(id => ({ id, value: false, good: true })) })
    frame('snapshot-complete', { snapshotId: 's' })
    const id = ids[999]
    frame('tag-delta', { values: [{ id, value: true, good: true }] })
    vm.flushUiValues()
    expect(vm.gridRows.find((item: any) => item.itemId === id).value).toBe('true')
    for (let i = 0; i < 5; i++) vm.flushUiValues()
    expect(vm.gridRows.find((item: any) => item.itemId === id).value).toBe('true')
    expect(vm.gridRows.filter((item: any) => item.value === '—')).toHaveLength(0)
  })

  it.each(['unchanged', 'missing', 'coalesced'] as const)(
    'finishes a backend-verified write immediately when the target stream value is %s', async scenario => {
      const initial = scenario === 'unchanged'
      const vm = await openWriter(initial)
      vi.mocked(api.write).mockResolvedValueOnce(receipt)
      void vm.doWrite()
      if (scenario === 'unchanged')
        vm.applyVal(vm.gridRows[0], { id: tagId, value: true, good: true })
      if (scenario === 'coalesced') {
        // A PLC handshake may reset before the next UI frame. The display keeps the latest value.
        for (const value of [true, false]) vm.onRealtimeFrame({ event: 'tag-delta', data: JSON.stringify({
          values: [{ id: tagId, value, good: true }],
        }) })
        vm.flushUiValues()
      }
      await flushPromises()

      expect(api.write).toHaveBeenCalledExactlyOnceWith(tagId, true)
      expect(vm.writing).toBe(false)
      expect(vm.wOpen).toBe(false)
      expect(ElMessage.success).toHaveBeenCalledWith('写入并回读确认成功')
      expect(vm.gridRows[0].value).toBe(String(initial))
      await vi.advanceTimersByTimeAsync(45_000)
      expect(ElMessage.error).not.toHaveBeenCalled()
    },
  )

  it('reports protocol acknowledgement without claiming PLC readback or waiting for a stream change', async () => {
    const vm = await openWriter(false)
    vi.mocked(api.write).mockResolvedValueOnce({ ...receipt, quality: 'GoodUnverified', statusCode: 0x00b80000, statusHex: '0x00B80000' })
    void vm.doWrite()
    await flushPromises()
    expect(vm.writing).toBe(false)
    expect(vm.wOpen).toBe(false)
    expect(ElMessage.success).not.toHaveBeenCalled()
    expect(ElMessage.warning).toHaveBeenCalledWith('协议已接受写入，未回读验证 PLC 实际值')
    expect(vm.gridRows[0].value).toBe('false')
  })

  it('does not let a matching stream value hide failure of the current write request', async () => {
    const vm = await openWriter(false)
    let reject!: (reason: unknown) => void
    vi.mocked(api.write).mockReturnValueOnce(new Promise((_, fail) => { reject = fail }))
    const pending = vm.doWrite()
    vm.applyVal(vm.gridRows[0], { id: tagId, value: true, good: true })
    await flushPromises()
    expect(vm.writing).toBe(true)
    expect(ElMessage.success).not.toHaveBeenCalled()
    reject(new Error('BadNotVerified'))
    await pending
    expect(vm.writing).toBe(false)
    expect(vm.wOpen).toBe(true)
    expect(ElMessage.error).toHaveBeenCalledWith('写入失败:BadNotVerified')
    expect(ElMessage.success).not.toHaveBeenCalled()
  })

  it('does not claim verified success for a receipt without an authoritative result', async () => {
    const vm = await openWriter(false)
    vi.mocked(api.write).mockResolvedValueOnce({ id: tagId, normalizedValue: true })
    void vm.doWrite()
    await flushPromises()
    expect(vm.writing).toBe(false)
    expect(vm.wOpen).toBe(true)
    expect(ElMessage.success).not.toHaveBeenCalled()
    expect(ElMessage.error).toHaveBeenCalled()
  })
})
