import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, shallowMount } from '@vue/test-utils'
import Points from './Points.vue'
import ArrayValueViewer from '../components/ArrayValueViewer.vue'
import { api } from '../api'

vi.mock('vue-router', () => ({ useRoute: () => ({ query: {} }) }))
vi.mock('../utils/realtimeStream', () => ({ RealtimeStreamClient: class {
  start() {} stop() {} requestSnapshot() {}
} }))
vi.mock('../api', () => ({ api: {
  projectTagScope: vi.fn(async () => ({
    projectRevision: 'test', realtimeRevision: 'test', root: 'Line.Plc', groups: [], total: 2,
    definitions: [
      { id: 'Line.Plc.Flag', name: 'Flag', address: 'flag', dataType: 'Bool', access: 'ReadWrite' },
      { id: 'Line.Plc.Arr', name: 'Arr', address: 'arr', dataType: 'Int16', access: 'ReadWrite',
        array: { elementType: 'int16', dimensions: [4], lowerBounds: [0] } },
    ],
  })),
  tagScanTimings: vi.fn(async () => ({ items: [] })),
  readValues: vi.fn(async () => ({ values: [], goodCount: 0 })),
  write: vi.fn(),
} }))

const wrappers: ReturnType<typeof shallowMount>[] = []
async function mountPoints(props: Record<string, unknown>) {
  const wrapper = shallowMount(Points, { props: { q: 'Line.Plc', ...props }, global: {
    directives: { loading: () => {} },
    stubs: ['ElAutoResizer', 'ElTreeV2', 'ElTableV2', 'ElTableColumn', 'ElTable', 'ElInput', 'ElButton', 'ElDialog'],
  } })
  wrappers.push(wrapper)
  await flushPromises()
  return { wrapper, vm: wrapper.vm as any }
}
const contextEvent = () => ({ preventDefault: vi.fn(), clientX: 10, clientY: 10 }) as unknown as MouseEvent

afterEach(async () => {
  wrappers.splice(0).forEach(wrapper => wrapper.unmount())
  await flushPromises()
  vi.mocked(api.write).mockReset()
})

describe('Quick Client 只能看', () => {
  it('隐藏写入按钮和右键“同步写入”，双击/写入入口不打开写值框，也不会调用写入接口', async () => {
    const { wrapper, vm } = await mountPoints({ editable: false })
    const flag = vm.gridRows.find((item: any) => item.itemId === 'Line.Plc.Flag')
    expect(wrapper.find('button[title="写入选中项"]').exists()).toBe(false)
    expect(wrapper.find('[data-test="qc-refresh"]').exists()).toBe(true)

    vm.onItemCtx(flag, null, contextEvent())
    const labels = vm.menu.items.filter((item: any) => !item.sep).map((item: any) => item.label)
    expect(labels).toContain('从 PLC 读取此点')
    expect(labels).not.toContain('同步写入…')

    vm.openWrite(flag)
    expect(vm.wOpen).toBe(false)
    vm.wItem = flag; vm.wVal = 'true'
    await vm.doWrite()
    expect(api.write).not.toHaveBeenCalled()
  })

  it('数组切片区不允许写入', async () => {
    const { wrapper, vm } = await mountPoints({ editable: false })
    vm.curRow = vm.gridRows.find((item: any) => item.itemId === 'Line.Plc.Arr')
    await flushPromises()
    expect(wrapper.findComponent(ArrayValueViewer).props('writable')).toBe(false)
  })

  it('默认(可修改)保留写入', async () => {
    const { wrapper, vm } = await mountPoints({})
    const flag = vm.gridRows.find((item: any) => item.itemId === 'Line.Plc.Flag')
    expect(wrapper.find('button[title="写入选中项"]').exists()).toBe(true)
    vm.onItemCtx(flag, null, contextEvent())
    expect(vm.menu.items.map((item: any) => item.label)).toContain('同步写入…')
    vm.openWrite(flag)
    expect(vm.wOpen).toBe(true)
    vm.curRow = vm.gridRows.find((item: any) => item.itemId === 'Line.Plc.Arr')
    await flushPromises()
    expect(wrapper.findComponent(ArrayValueViewer).props('writable')).toBe(true)
  })
})
