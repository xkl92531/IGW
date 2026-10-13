import { mount, flushPromises } from '@vue/test-utils'
import { beforeEach, expect, it, vi } from 'vitest'
import ElementPlus from 'element-plus'
import MesDataRangeDialog from './MesDataRangeDialog.vue'
import { i18n } from '../i18n'

const api = vi.hoisted(() => ({ mesRangeChildren: vi.fn(), parameterOptions: vi.fn() }))
vi.mock('../api', () => ({ api }))

const block = { sourcePath: 'Line.PLC.DB10', name: 'DB10', scope: 'Group', tagCount: 3, hasChildren: true,
  defaultOutputPath: 'PLC.DB10', dimensions: [], lowerBounds: [], members: [] }
const counter = { sourcePath: 'Line.PLC.DB10.Counter', name: 'Counter', scope: 'Tag', dataType: 'Int16', tagCount: 1,
  hasChildren: false, defaultOutputPath: 'PLC.DB10.Counter', dimensions: [], lowerBounds: [], members: [] }
const cells = { sourcePath: 'Line.PLC.DB10.Cells', name: 'Cells', scope: 'Array', tagCount: 4, hasChildren: false,
  defaultOutputPath: 'PLC.DB10.Cells', shapeKind: 'Rectangular', dimensions: [4], lowerBounds: [1], members: [] }
const mountDialog = (ranges: any[] = []) => mount(MesDataRangeDialog, {
  props: { modelValue: true, ranges, scopeGroup: 'Line.PLC.DB10' },
  global: { plugins: [ElementPlus], stubs: { teleport: true }, config: { globalProperties: { $t: i18n.t } as any } },
})

beforeEach(() => {
  api.mesRangeChildren.mockReset().mockImplementation(async (params: any) => {
    if (params.q) return { items: [block, counter, cells].filter(item => item.sourcePath.toLowerCase().includes(params.q.toLowerCase())), total: 3 }
    return params.parent ? { items: [counter, cells], total: 2 } : { items: [block], total: 1 }
  })
})

it('selects the task data block by default when nothing is configured yet', async () => {
  const wrapper = mountDialog()
  await flushPromises()
  const state = (wrapper.vm.$ as any).setupState
  expect(state.draft).toHaveLength(1)
  expect(state.draft[0]).toMatchObject({ sourcePath: 'Line.PLC.DB10', scope: 'Group', outputPath: 'PLC.DB10' })
  wrapper.unmount()
})

it('adds a single tag and an array (bounds recognized) instead of the whole block', async () => {
  const wrapper = mountDialog([{ sourcePath: 'Line.PLC.DB10.Counter', scope: 'Tag', outputPath: 'PLC.DB10.Counter' }])
  await flushPromises()
  const state = (wrapper.vm.$ as any).setupState
  expect(state.draft).toHaveLength(1)                      // 已有配置时不再自动加整块
  state.addOption(state.knownOptions.get('Line.PLC.DB10.Counter'))
  expect(state.draft).toHaveLength(1)                      // 重复添加被忽略
  await state.search(true)
  state.query = 'cells'
  await state.search(true)
  state.addOption(state.searchResults[0])
  expect(state.draft[1]).toMatchObject({ sourcePath: 'Line.PLC.DB10.Cells', scope: 'Array', arrayStart: [1], arrayLengths: [4] })
  state.apply()
  const applied = wrapper.emitted('apply')![0]![0] as any[]
  expect(applied.map(range => range.scope)).toEqual(['Tag', 'Array'])
  wrapper.unmount()
})

it('refuses a tag that is already covered by its selected block', async () => {
  const wrapper = mountDialog([{ sourcePath: 'Line.PLC.DB10', scope: 'Group', outputPath: 'PLC.DB10' }])
  await flushPromises()
  const state = (wrapper.vm.$ as any).setupState
  state.addOption({ ...counter, label: 'Counter', children: [], leafTags: [], searchText: '' })
  expect(state.draft).toHaveLength(1)
  wrapper.unmount()
})

it('browses from channels down to another device and DB and adds a tag from there', async () => {
  const other = { sourcePath: 'Line2.PLC2.DB20.Code', name: 'Code', scope: 'Tag', dataType: 'Int16', tagCount: 1,
    hasChildren: false, defaultOutputPath: 'PLC2.DB20.Code', dimensions: [], lowerBounds: [], members: [] }
  const level = (sourcePath: string, scope: string) => ({ sourcePath, name: sourcePath.split('.').pop(), scope, tagCount: 1,
    hasChildren: true, defaultOutputPath: '', dimensions: [], lowerBounds: [], members: [] })
  api.mesRangeChildren.mockImplementation(async (params: any) => {
    if (params.scopeGroup) return { items: [block], total: 1 }
    const children: Record<string, any[]> = {
      '': [level('Line', 'Channel'), level('Line2', 'Channel')],
      Line2: [level('Line2.PLC2', 'Device')],
      'Line2.PLC2': [level('Line2.PLC2.DB20', 'Group')],
      'Line2.PLC2.DB20': [other],
    }
    const items = children[params.parent || ''] || []
    return { items, total: items.length }
  })
  const wrapper = mountDialog()
  await flushPromises()
  const state = (wrapper.vm.$ as any).setupState
  expect(state.expandedKeys).toEqual(['Line', 'Line.PLC', 'Line.PLC.DB10'])   // 默认展开到当前 DB
  const load = (parent?: string) => new Promise<any[]>(resolve =>
    state.loadNode(parent ? { level: 1, data: { id: parent } } : { level: 0 }, resolve))
  const channels = await load()
  expect(channels.map(node => node.option.scope)).toEqual(['Channel', 'Channel'])
  state.addOption(channels[1].option)                                       // 通道、设备不能直接添加
  expect(state.draft).toHaveLength(1)
  await load('Line2'); await load('Line2.PLC2')
  const tags = await load('Line2.PLC2.DB20')
  state.addOption(tags[0].option)
  expect(state.draft.map((range: any) => range.sourcePath)).toEqual(['Line.PLC.DB10', 'Line2.PLC2.DB20.Code'])
  expect(api.mesRangeChildren).toHaveBeenCalledWith({ parent: 'Line2.PLC2.DB20', cursor: undefined, limit: 200 })
  wrapper.unmount()
})
