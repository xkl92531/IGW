import { flushPromises, shallowMount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { afterEach, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({ scope: vi.fn() }))
vi.mock('../api', () => ({ api: {
  projectTagScope: mocks.scope, tagScanTimings: vi.fn().mockResolvedValue([]),
  readValues: vi.fn().mockResolvedValue({ values: [], goodCount: 0 }),
} }))
vi.mock('vue-router', () => ({ useRoute: () => ({ query: { q: 'OldChannel.OldDevice' } }) }))
import Points from './Points.vue'

afterEach(() => vi.resetAllMocks())
it('reloads a renamed active group under a global root using its exact path', async () => {
  const selected = 'PLC_Test.PLC10.DB2050_HeatBeat/Time'
  const children: Record<string, string[]> = { '': ['AnotherPLC', 'PLC_Test'], PLC_Test: ['PLC_Test.PLC10'], 'PLC_Test.PLC10': [selected] }
  mocks.scope.mockImplementation(async ({ root }: { root?: string }) => {
    const path = root || ''
    const populated = path === selected || path === 'AnotherPLC'
    return { projectRevision: 'r2', realtimeRevision: 'rt2', root: path,
      groups: (children[path] || []).map(child => ({ name: child, path: child, kind: 'Group' })),
      definitions: populated ? [{ id: `${path}.HeatBeat`, name: 'HeatBeat', address: 'D0', dataType: 'Int16', access: 'Read' }] : [],
      nextCursor: null, total: populated ? 1 : 0,
    }
  })
  const wrapper = shallowMount(Points, { props: { q: '', preferredScope: selected, suspended: true }, global: {
    plugins: [ElementPlus], stubs: { ElAutoResizer: { template: '<div />' }, ArrayValueViewer: true },
  } })
  try {
    await flushPromises()
    await vi.waitFor(() => expect((wrapper.vm as any).getActiveScopePath()).toBe(selected))
    expect(mocks.scope.mock.calls.map(([params]) => params.root || '')).toEqual(['', 'PLC_Test', 'PLC_Test.PLC10', selected])
  } finally { wrapper.unmount() }
})

it('clears ancestor rows when the previously selected exact group no longer exists', async () => {
  mocks.scope.mockImplementation(async ({ root }: { root?: string }) => {
    const path = root || ''
    return { projectRevision: 'r2', realtimeRevision: 'rt2', root: path, groups: [],
      definitions: [{ id: `${path}.ParentValue`, name: 'ParentValue', address: 'D0', dataType: 'Int16', access: 'Read' }],
      nextCursor: null, total: 1,
    }
  })
  const wrapper = shallowMount(Points, { props: { q: 'PLC_Test.PLC10', preferredScope: 'PLC_Test.PLC10.Missing', suspended: true }, global: {
    plugins: [ElementPlus], stubs: { ElAutoResizer: { template: '<div />' }, ArrayValueViewer: true },
  } })
  try {
    await flushPromises()
    const state = (wrapper.vm.$ as any).setupState
    expect(state.curGroup).toBeNull()
    expect(state.itemCount).toBe(0)
  } finally { wrapper.unmount() }
})

it('keeps a manual selection when an older preferred-scope request finishes late', async () => {
  const parent = 'PLC_Test.PLC10', selected = `${parent}.Selected`, other = `${parent}.Other`
  let finishOld!: (value: any) => void
  const response = (path: string, populated = false) => ({ projectRevision: 'r2', realtimeRevision: 'rt2', root: path,
    groups: path === parent ? [selected, other].map(path => ({ name: path, path, kind: 'Group' })) : [],
    definitions: populated ? [{ id: `${path}.Value`, name: 'Value', address: 'D0', dataType: 'Int16', access: 'Read' }] : [],
    nextCursor: null, total: populated ? 1 : 0,
  })
  mocks.scope.mockImplementation(({ root }: { root?: string }) => root === selected
    ? new Promise(resolve => { finishOld = resolve }) : Promise.resolve(response(root || '', root === other)))
  const wrapper = shallowMount(Points, { props: { q: parent, preferredScope: selected, suspended: true }, global: {
    plugins: [ElementPlus], stubs: { ElAutoResizer: { template: '<div />' }, ArrayValueViewer: true },
  } })
  try {
    await vi.waitFor(() => expect(finishOld).toBeTypeOf('function'))
    const state = (wrapper.vm.$ as any).setupState
    state.pickGroup(state.treeData[0].children[0].children.find((node: any) => node.path === other))
    await vi.waitFor(() => expect(state.curGroup?.items[0]?.itemId).toBe(`${other}.Value`))
    finishOld(response(selected, true)); await flushPromises()
    expect(state.curGroup?.items[0]?.itemId).toBe(`${other}.Value`)
  } finally { wrapper.unmount() }
})
