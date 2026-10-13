import { describe, expect, it, vi } from 'vitest'
import { flushPromises, shallowMount } from '@vue/test-utils'
import { ElMessage } from 'element-plus'
import Points from './Points.vue'
import { api } from '../api'

vi.mock('vue-router', () => ({ useRoute: () => ({ query: {} }) }))
vi.mock('../utils/realtimeStream', () => ({ RealtimeStreamClient: class {
  start() {} stop() {} requestSnapshot() {}
} }))
const writeControl = vi.hoisted(() => ({ write: vi.fn() }))
vi.mock('../api', () => ({ api: { write: writeControl.write, projectTagScope: vi.fn(async () => ({
  realtimeRevision: 'test', groups: [], total: 3, definitions: [
    { id: 'Line.Plc.DB.GlueAmount', dataType: 'Float', access: 'Read', address: 'amount' },
    { id: 'Line.Plc.DB.TimeStamp', dataType: 'Byte', access: 'Read', address: 'timestamp',
      array: { elementType: 1, dimensions: [8], lowerBounds: [0] } },
    { id: 'Line.Plc.DB.Flags', dataType: 'Bool', access: 'Read', address: 'flags',
      array: { elementType: 0, dimensions: [2], lowerBounds: [0] } },
  ],
})) } }))

describe('Quick Client type compatibility', () => {
  it('permits configured read-write tags and blocks only read-only tags before sending a write', async () => {
    vi.mocked(api.projectTagScope).mockResolvedValueOnce({
      projectRevision: 'test', realtimeRevision: 'test', root: 'Line.Plc.DB', groups: [], total: 3,
      definitions: ['Read', 'Write', 'ReadWrite'].map(access => ({
        id: `Line.Plc.DB.${access}`, name: access, address: access, dataType: 'Int16', access,
      })),
    })
    const info = vi.spyOn(ElMessage, 'info').mockImplementation(() => ({ close() {} }))
    const wrapper = shallowMount(Points, {
      props: { q: 'Line.Plc.DB' },
      global: { stubs: ['ElAutoResizer', 'ElTreeV2', 'ElTableV2', 'ElTableColumn', 'ElTable', 'ElInput', 'ElButton', 'ElDialog'] },
    })
    try {
      await flushPromises()
      const vm = wrapper.vm as any
      expect(vm.gridRows.map((row: any) => row.writable)).toEqual([false, true, true])
      vm.openWrite(vm.gridRows[0])
      expect(vm.wOpen).toBe(false)
      expect(info).toHaveBeenCalledWith('Line.Plc.DB.Read 为只读')
      vm.openWrite(vm.gridRows[2])
      expect(vm.wOpen).toBe(true)
      expect(vm.wItem.itemId).toBe('Line.Plc.DB.ReadWrite')
    } finally { wrapper.unmount(); info.mockRestore() }
  })

  it('loads every item when an array definition contains numeric enum values', async () => {
    const error = vi.spyOn(ElMessage, 'error')
    const wrapper = shallowMount(Points, {
      props: { q: 'Line.Plc.DB' },
      global: { stubs: ['ElAutoResizer', 'ElTreeV2', 'ElTableV2', 'ElTableColumn', 'ElTable', 'ElInput', 'ElButton', 'ElDialog'] },
    })
    await flushPromises()
    expect(error).not.toHaveBeenCalled()
    const rows = (wrapper.vm as any).gridRows
    expect(rows.map((row: any) => row.dataType)).toEqual(['Float', 'Byte[8]', 'Boolean[2]'])
    expect(rows).toHaveLength(3)
    wrapper.unmount()
    error.mockRestore()
  })

  it('keeps the canonical actual value that arrives before the verified write receipt', async () => {
    let finishWrite!: (value: unknown) => void
    writeControl.write.mockReturnValueOnce(new Promise(resolve => { finishWrite = resolve }))
    vi.mocked(api.projectTagScope).mockResolvedValueOnce({
      projectRevision: 'test', realtimeRevision: 'test', root: 'Line.Plc.DB', groups: [], total: 1,
      definitions: [{ id: 'Line.Plc.DB.Delay', name: 'Delay', address: 'delay', dataType: 'Time', access: 'ReadWrite' }],
    })
    const success = vi.spyOn(ElMessage, 'success').mockImplementation(() => ({ close() {} }))
    const wrapper = shallowMount(Points, {
      props: { q: 'Line.Plc.DB' },
      global: { stubs: ['ElAutoResizer', 'ElTreeV2', 'ElTableV2', 'ElTableColumn', 'ElTable', 'ElInput', 'ElButton', 'ElDialog'] },
    })
    try {
      await flushPromises()
      const vm = wrapper.vm as any
      vm.openWrite(vm.gridRows[0])
      vm.wVal = 'T#1h'
      const pending = vm.doWrite()
      await Promise.resolve()
      vm.applyVal(vm.gridRows[0], { good: true, value: 'T#3600000ms' })
      finishWrite({ id: 'Line.Plc.DB.Delay', written: 'T#1h', normalizedValue: 'T#3600000ms', quality: 'Good', statusCode: 0, statusHex: '0x00000000' })
      await pending
      expect(success).toHaveBeenCalledWith('写入并回读确认成功')
      expect(vm.gridRows[0].value).toBe('T#3600000ms')
    } finally { wrapper.unmount(); success.mockRestore() }
  })
})
