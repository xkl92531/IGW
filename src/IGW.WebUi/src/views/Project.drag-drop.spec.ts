import { createPinia } from 'pinia'
import { mount, shallowMount, type VueWrapper } from '@vue/test-utils'
import ElementPlus, { ElMessage } from 'element-plus'
import { nextTick } from 'vue'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { clearSession, session } from '../auth'
import type { ProjectMutationRequest } from '../features/project/types'

const mocks = vi.hoisted(() => ({ project: vi.fn(), summary: vi.fn(), shell: vi.fn(), children: vi.fn(), mutate: vi.fn() }))
vi.mock('../api', async importOriginal => {
  const original = await importOriginal<typeof import('../api')>()
  return { ...original, api: { ...original.api, project: mocks.project,
    health: vi.fn().mockResolvedValue({ tags: 4, devices: 2 }), drivers: vi.fn().mockResolvedValue([]),
    networkAdapters: vi.fn().mockResolvedValue([]), uiPreferences: vi.fn().mockResolvedValue({ projectAutoSave: false }),
  } }
})
vi.mock('../features/project/api', async importOriginal => {
  const original = await importOriginal<typeof import('../features/project/api')>()
  return { ...original, projectApi: { ...original.projectApi,
    project: mocks.project, summary: mocks.summary, shell: mocks.shell, lazyChildren: mocks.children, mutate: mocks.mutate,
  } }
})
vi.mock('vue-router', () => ({ useRouter: () => ({ push: vi.fn() }), onBeforeRouteLeave: vi.fn() }))
import Project from './Project.vue'

const copy = (value: any) => JSON.parse(JSON.stringify(value))
const tag = (id: string, name: string) => ({ id, name, address: 'DB1.DBW0', dataType: 'int16', access: 'readWrite', scanRateMs: 200 })
const group = (id: string, name: string, tags: any[] = [], subGroups: any[] = []) => ({ id, name, tags, subGroups })
function fixture() {
  return { name: '工程', settings: {}, opcUa: {}, history: {}, mqttPublish: {},
    mesUpload: { tasks: [{ id: 'task', groupRef: 'C.D.Source', enabled: false,
      payload: { fields: [{ tagId: 'C.D.Source.A' }] }, flow: { nodes: [] } }] },
    channels: [{ id: 'c1', name: 'C', driverId: 'Siemens.S7', settings: {}, devices: [
      { id: 'd1', name: 'D', endpoint: '10.0.0.1', settings: {}, tags: [], groups: [
        group('g1', 'Source', [tag('t1', 'A'), tag('t2', 'B')], [group('nested', 'Nested')]),
        group('g2', 'Target', [tag('t3', 'A')]),
      ] },
      { id: 'd2', name: 'Other', endpoint: '10.0.0.2', settings: {}, tags: [], groups: [group('g3', 'Remote')] },
    ] }] }
}
const wrappers: VueWrapper[] = []
async function setup(lazy = false, readonly = false, renderControls = false) {
  const project = fixture()
  mocks.project.mockResolvedValue({ proj: copy(project), rev: 'r1' })
  mocks.summary.mockResolvedValue({ totalTagCount: lazy ? 100_000 : 4, fullProjectSupported: !lazy })
  const shell = copy(project)
  if (lazy) shell.channels[0].devices.forEach((device: any) => { device.groups = [] })
  mocks.shell.mockImplementation(async () => ({ project: copy(shell), rev: 'r1', lazy: lazy
    ? { d1: { groups: 2, tags: 0, totalTags: 3 }, d2: { groups: 1, tags: 0, totalTags: 1 } } : {} }))
  const find = (id: string) => {
    const visit = (node: any): any => node.id === id ? node
      : ['channels', 'devices', 'groups', 'subGroups', 'tags'].flatMap(key => node[key] || []).map(visit).find(Boolean)
    return visit(project)
  }
  mocks.children.mockImplementation(async (_kind: string, id: string, deep: boolean) => {
    const owner = find(id)
    const groups = copy(owner.groups || owner.subGroups || [])
    const lazyCounts: Record<string, any> = {}
    if (!deep) for (const child of groups) {
      lazyCounts[child.id] = { groups: child.subGroups.length, tags: child.tags.length, totalTags: 3 }
      child.tags = []; child.subGroups = []
    }
    return { groups, tags: copy(owner.tags), lazy: lazyCounts, rev: 'r1' }
  })
  mocks.mutate.mockResolvedValue({ revision: 'r1' })
  session.account = { username: readonly ? 'viewer' : 'admin', displayName: '', isAdmin: !readonly,
    mustChangePassword: false, disabled: false, createdUtc: '', permissions: {} }
  const wrapper = (renderControls ? mount : shallowMount)(Project, { global: {
    plugins: [createPinia(), ElementPlus], mocks: { $t: (key: string) => key },
    stubs: { ElAutoResizer: { template: renderControls ? '<div><slot :height="400" :width="400" /></div>' : '<div />' },
      ElTable: !renderControls, ElTableColumn: !renderControls, ElTreeV2: !renderControls, ElDialog: true },
  } })
  wrappers.push(wrapper)
  const state = (wrapper.vm.$ as any).setupState
  await vi.waitFor(() => expect(state.proj).toBeTruthy())
  return state
}
function node(state: any, id: string): any {
  const find = (nodes: any[]): any => {
    for (const value of nodes) { if (value.ref?.id === id) return value; const child = find(value.children || []); if (child) return child }
  }
  return find(state.treeData)
}
function event() {
  return { preventDefault: vi.fn(), stopPropagation: vi.fn(), dataTransfer: { setData: vi.fn(), effectAllowed: '', dropEffect: '' } } as unknown as DragEvent
}
async function dropAfterNativeDragEnd(state: any, target: any) {
  const pending = state.onHtml5Drop(event(), target)
  state.onDragEnd() // HTML5 always fires dragend before an asynchronous drop handler finishes.
  await pending
}
function commands() {
  const command = mocks.mutate.mock.calls[mocks.mutate.mock.calls.length - 1]?.[0] as ProjectMutationRequest
  return command?.kind === 'batch' ? JSON.parse(command.documentJson!) : [command]
}

beforeEach(() => {
  vi.spyOn(ElMessage, 'error').mockImplementation(() => ({ close: () => {} }))
  vi.spyOn(ElMessage, 'success').mockImplementation(() => ({ close: () => {} }))
})
afterEach(() => { wrappers.splice(0).forEach(wrapper => wrapper.unmount()); clearSession(); vi.resetAllMocks(); vi.restoreAllMocks() })

describe('project drag and drop uses the captured source across native dragend', () => {
  it('binds drag events on actual left tree rows and keeps the captured source after dragend', async () => {
    const state = await setup(false, false, true)
    state.expandedKeys = ['__proj', '__conn', node(state, 'c1').key, node(state, 'd1').key]
    await nextTick()
    state.treeRef.setExpandedKeys(state.expandedKeys)
    await nextTick()
    const wrapper = wrappers[wrappers.length - 1]!
    const rows = wrapper.findAll('.tn')
    const source = rows.find(row => row.find('.tnlabel').text().startsWith('Source'))!
    const target = rows.find(row => row.find('.tnlabel').text().startsWith('Target'))!
    expect(source.attributes('draggable')).toBe('true')
    const dataTransfer = event().dataTransfer
    await source.trigger('dragstart', { dataTransfer })
    await target.trigger('dragover', { dataTransfer })
    expect(target.classes()).toContain('drop-ok')
    await target.trigger('drop', { dataTransfer })
    await source.trigger('dragend', { dataTransfer })
    await vi.waitFor(() => expect(node(state, 'g2').ref.subGroups.map((item: any) => item.name)).toEqual(['Source']))
  })

  it('binds native drag events on real table cells and moves into a right-side group', async () => {
    const state = await setup(false, false, true)
    state.sel = node(state, 'd1')
    await nextTick()
    const wrapper = wrappers[wrappers.length - 1]!
    await vi.waitFor(() => expect(wrapper.findAll('.project-grid-drag-cell').length).toBeGreaterThan(0))
    const cells = wrapper.findAll('.project-grid-drag-cell')
    const source = cells.find(cell => cell.text() === '📁 Source')!
    const target = cells.find(cell => cell.text() === '📁 Target')!
    expect(source.attributes('draggable')).toBe('true')
    const dataTransfer = event().dataTransfer
    await source.trigger('dragstart', { dataTransfer })
    await target.trigger('dragover', { dataTransfer })
    expect(target.classes()).toContain('drop-ok')
    await target.trigger('drop', { dataTransfer })
    await source.trigger('dragend', { dataTransfer })
    await vi.waitFor(() => expect(node(state, 'g2').ref.subGroups.map((item: any) => item.name)).toEqual(['Source']))
  })

  it('moves a group from the left tree after native dragend and migrates MES references', async () => {
    const state = await setup()
    const source = node(state, 'g1'), target = node(state, 'g2')
    state.sel = source
    state.onDragStart(event(), source)
    await dropAfterNativeDragEnd(state, target)
    const device = state.proj.channels[0].devices[0]
    expect(device.groups.map((item: any) => item.name)).toEqual(['Target'])
    expect(target.ref.subGroups[0]).toBe(source.ref)
    expect(state.proj.mesUpload.tasks[0].groupRef).toBe('C.D.Target.Source')
    expect(state.proj.mesUpload.tasks[0].payload.fields[0].tagId).toBe('C.D.Target.Source.A')
    expect(state.pathOf(state.sel)).toBe('C.D.Target.Source')
  })

  it('moves a group back out to the device root', async () => {
    const state = await setup()
    const source = node(state, 'nested'), target = node(state, 'd1')
    state.onDragStart(event(), source)
    await dropAfterNativeDragEnd(state, target)
    expect(node(state, 'g1').ref.subGroups).toHaveLength(0)
    expect(target.ref.groups).toContain(source.ref)
  })

  it('moves a selected set of right-side tags into a left-side group and persists their destination', async () => {
    const state = await setup()
    state.sel = node(state, 'g1')
    const selected = state.view.rows.filter((row: any) => row.kind === 'tag')
    state.onSelChange(selected)
    state.onGridDragStart(event(), selected[0])
    await dropAfterNativeDragEnd(state, node(state, 'g2'))
    expect(state.sel.ref.tags).toHaveLength(0)
    expect(node(state, 'g2').ref.tags.map((item: any) => item.name)).toEqual(['A', 'A_1', 'B'])
    expect(selected[0].def.address).toBe('DB1.DBW0')
    expect(state.proj.mesUpload.tasks[0].payload.fields[0].tagId).toBe('C.D.Target.A_1')
    await state.saveApply(false)
    expect(commands()).toContainEqual(expect.objectContaining({ kind: 'upsertEntity', entityKind: 'tag', targetId: 'g2', entityIds: ['t1'] }))
  })

  it('moves a right-side group into another right-side group', async () => {
    const state = await setup()
    state.sel = node(state, 'd1')
    const source = state.view.rows.find((row: any) => row.def.id === 'g1')
    const target = state.view.rows.find((row: any) => row.def.id === 'g2')
    state.onGridDragStart(event(), source)
    const pending = state.onGridDrop(event(), target)
    state.onDragEnd(); await pending
    expect(node(state, 'g2').ref.subGroups.map((item: any) => item.name)).toEqual(['Source'])
  })

  it('moves a left-side group into the currently selected right-side device', async () => {
    const state = await setup()
    const source = node(state, 'g1')
    state.sel = node(state, 'd2')
    state.onDragStart(event(), source)
    await dropAfterNativeDragEnd(state, state.sel)
    expect(state.sel.ref.groups.map((item: any) => item.name)).toEqual(['Remote', 'Source'])
  })

  it('prevents cycles and clears cancelled drags', async () => {
    const state = await setup()
    const source = node(state, 'g1')
    state.onDragStart(event(), source)
    expect(state.canDropInto(node(state, 'nested'))).toBe(false)
    await dropAfterNativeDragEnd(state, node(state, 'nested'))
    expect(node(state, 'd1').ref.groups).toHaveLength(2)
    expect(state.dragData).toBeNull()
    expect(state.dropKey).toBe('')
  })

  it('does not move, rename, or dirty a tag dropped onto its existing parent', async () => {
    const state = await setup()
    state.sel = node(state, 'g1')
    const row = state.view.rows.find((row: any) => row.kind === 'tag')
    state.onGridDragStart(event(), row)
    await dropAfterNativeDragEnd(state, state.sel)
    expect(state.sel.ref.tags.map((item: any) => item.name)).toEqual(['A', 'B'])
    expect(state.dirty).toBe(false)
  })

  it('loads a lazy destination before adding a tag so existing target data survives', async () => {
    const state = await setup(true)
    await state.ensureLoaded(node(state, 'd1').ref)
    await state.ensureLoaded(node(state, 'g1').ref)
    state.sel = node(state, 'g1')
    const row = state.view.rows.find((row: any) => row.def.id === 't1')
    state.onGridDragStart(event(), row)
    await dropAfterNativeDragEnd(state, node(state, 'g2'))
    expect(node(state, 'g2').ref.tags.map((item: any) => item.name)).toEqual(['A', 'A_1'])
    expect(mocks.children).toHaveBeenCalledWith('group', 'g2', false)
    await state.saveApply(false)
    expect(commands()).not.toContainEqual(expect.objectContaining({ kind: 'deleteEntities', entityIds: ['t3'] }))
  })

  it('fully loads a lazy group before moving it to another device and preserves all descendants', async () => {
    const state = await setup(true)
    await state.ensureLoaded(node(state, 'd1').ref)
    const source = node(state, 'g1'), target = node(state, 'd2')
    state.onDragStart(event(), source)
    await dropAfterNativeDragEnd(state, target)
    const moved = target.ref.groups.find((group: any) => group.name === 'Source')
    expect(moved?.tags.map((tag: any) => tag.name)).toEqual(['A', 'B'])
    expect(moved?.subGroups.map((group: any) => group.name)).toEqual(['Nested'])
    expect(target.ref.groups.some((group: any) => group.name === 'Remote')).toBe(true)
    await state.saveApply(false)
    expect(commands()).toContainEqual(expect.objectContaining({ kind: 'deleteEntities', entityKind: 'group', entityIds: ['g1'] }))
    expect(commands()).toContainEqual(expect.objectContaining({ kind: 'upsertEntity', entityKind: 'group', targetId: 'd2', entityIds: [moved.id] }))
    expect(commands()).toContainEqual(expect.objectContaining({ kind: 'upsertEntity', entityKind: 'tag', targetId: moved.id, entityIds: [moved.tags[0].id] }))
  })

  it('keeps the source unchanged when loading the destination fails', async () => {
    const state = await setup(true)
    await state.ensureLoaded(node(state, 'd1').ref)
    await state.ensureLoaded(node(state, 'g1').ref)
    state.sel = node(state, 'g1')
    const row = state.view.rows.find((row: any) => row.def.id === 't1')
    mocks.children.mockRejectedValueOnce(new Error('destination unavailable'))
    state.onGridDragStart(event(), row)
    await dropAfterNativeDragEnd(state, node(state, 'g2'))
    expect(state.sel.ref.tags.map((item: any) => item.name)).toEqual(['A', 'B'])
    expect(state.dirty).toBe(false)
    expect(state.dragData).toBeNull()
    expect(ElMessage.error).toHaveBeenCalledWith('destination unavailable')
  })

  it('cancels a drop if loading the destination reloads a newer project revision', async () => {
    const state = await setup(true)
    await state.ensureLoaded(node(state, 'd1').ref)
    await state.ensureLoaded(node(state, 'g1').ref)
    const originalProject = state.proj
    const source = node(state, 'g1'), target = node(state, 'g2')
    state.sel = source
    const row = state.view.rows.find((row: any) => row.def.id === 't1')
    mocks.children.mockResolvedValueOnce({ groups: [], tags: [tag('t3', 'A')], lazy: {}, rev: 'r2' })
    state.onGridDragStart(event(), row)
    await dropAfterNativeDragEnd(state, target)
    expect(state.proj).not.toBe(originalProject)
    expect(source.ref.tags.map((item: any) => item.name)).toEqual(['A', 'B'])
    expect(target.ref.tags).toHaveLength(0)
    expect(state.dirty).toBe(false)
    expect(ElMessage.success).not.toHaveBeenCalled()
    expect(mocks.mutate).not.toHaveBeenCalled()
  })

  it('refuses tree or grid dragging for a read-only account', async () => {
    const state = await setup(false, true)
    const start = event()
    state.onDragStart(start, node(state, 'g1'))
    expect(start.preventDefault).toHaveBeenCalled()
    state.sel = node(state, 'g1')
    const gridStart = event()
    state.onGridDragStart(gridStart, state.view.rows.find((row: any) => row.kind === 'tag'))
    expect(gridStart.preventDefault).toHaveBeenCalled()
    expect(state.dragData).toBeNull()
  })
})
