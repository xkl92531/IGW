import { createPinia } from 'pinia'
import { flushPromises, shallowMount, type VueWrapper } from '@vue/test-utils'
import { ElMessage, ElMessageBox } from 'element-plus'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { clearSession, session } from '../auth'
import type { ProjectMutationRequest } from '../features/project/types'

const mocks = vi.hoisted(() => ({
  project: vi.fn(), shell: vi.fn(), summary: vi.fn(), lazyChildren: vi.fn(), mutate: vi.fn(),
  preview: vi.fn(), online: vi.fn(), putProject: vi.fn(),
}))
vi.mock('../api', async importOriginal => {
  const original = await importOriginal<typeof import('../api')>()
  return { ...original, api: { ...original.api,
    project: mocks.project, putProject: mocks.putProject,
    siemensDbPreview: mocks.preview, siemensDataBlocks: mocks.online,
    health: vi.fn().mockResolvedValue({ tags: 10, devices: 1 }),
    drivers: vi.fn().mockResolvedValue([]), networkAdapters: vi.fn().mockResolvedValue([]),
    uiPreferences: vi.fn().mockResolvedValue({ projectAutoSave: false }),
  } }
})
vi.mock('../features/project/api', async importOriginal => {
  const original = await importOriginal<typeof import('../features/project/api')>()
  return { ...original, projectApi: { ...original.projectApi,
    project: mocks.project, shell: mocks.shell, summary: mocks.summary,
    lazyChildren: mocks.lazyChildren, mutate: mocks.mutate,
  } }
})
vi.mock('vue-router', () => ({ useRouter: () => ({ push: vi.fn() }), onBeforeRouteLeave: vi.fn() }))

import Project from './Project.vue'

const copy = <T,>(value: T): T => JSON.parse(JSON.stringify(value))
const tag = (id: string, name: string, address = 'DB1.DBW0', extra = {}) => ({
  id, name, address, dataType: 'int16', isArray: false, array: null, stringLength: null,
  description: '', access: 'readWrite', scanRateMs: 100, metadata: null,
  deadband: 0, deadbandType: 'none', ...extra,
})
const group = (id: string, name: string, tags: any[] = [], subGroups: any[] = [], extra = {}) => ({
  id, name, description: null, tags, subGroups, ...extra,
})
const fieldSettings = {
  access: 'read', scanRateMs: 1500, scaling: { enabled: true, rawLow: 0, rawHigh: 100, engLow: 0, engHigh: 10 },
  deadband: 2, deadbandType: 'absolute', alarm: { enabled: true, hi: 90 },
  engUnit: 'kPa', textEncoding: 'utf8', kepProperties: { 'custom.permission': 'readOnly' },
  writeConfirmation: { mode: 'confirmationTag', confirmationTagId: 'C.D.DB1.Ack', expectedValue: '1' },
  metadata: { local: 'keep', 'array.sourceFormat': 'Siemens DB', 'array.sourceAddress': 'DB1.DBW0' },
}
function sampleProject() {
  return {
    name: '工程', settings: {}, opcUa: {}, history: {}, mqttPublish: {},
    mesUpload: { tasks: [{ id: 'mes1', groupRef: 'C.D.DB1', enabled: true, flow: { nodes: [{ id: 'flow1' }] } }] },
    channels: [{ id: 'c1', name: 'C', driverId: 'Siemens.S7', settings: {}, devices: [{
      id: 'd1', name: 'D', endpoint: '10.0.0.1', settings: {}, tags: [], groups: [
        group('g1', 'DB1', [
          tag('t1', 'Value', 'DB1.DBW0', { ...fieldSettings, isArray: true, array: { dimensions: [2] }, stringLength: 20 }),
          tag('keep1', 'Unmentioned'),
        ], [
          group('g2', 'Struct', [tag('t2', 'Inner'), tag('keep2', 'Hidden')], [], { description: 'old', kepProperties: { custom: true } }),
          group('keepg', 'LocalOnly', [tag('keep3', 'Local')]),
        ], { description: '现场 DB 说明', kepProperties: { flowEnabled: true } }),
        group('other', 'DB2', [tag('otherT', 'Value', 'DB2.DBW0')]),
      ],
    }] }],
  }
}
// This is the REST parser's output shape, including defaults that must NOT reset existing site settings.
function preview() {
  return {
    dbName: 'DB1', dbNumber: 1, dbNumberKnown: true, optimized: false, skipped: [], tagCount: 5,
    root: group('', 'root', [
      tag('', 'Value', 'DB1.DBD4', { dataType: 'float', description: 'new comment', scaling: null, alarm: null, kepProperties: null, writeConfirmation: null }),
      tag('', 'New', 'DB1.DBW8'),
    ], [
      group('', 'Struct', [tag('', 'Inner', 'DB1.DBW10'), tag('', 'NewNested', 'DB1.DBW12')], [], { description: 'new struct comment' }),
      group('', 'Struct[1]', [tag('', 'Element', 'DB1.DBW14')]),
    ]),
  }
}

function mutations(command: ProjectMutationRequest): ProjectMutationRequest[] {
  return command.kind === 'batch' ? JSON.parse(command.documentJson!) : [command]
}
function nodes(project: any): Map<string, any> {
  const result = new Map<string, any>()
  const walk = (node: any) => {
    if (node.id) result.set(node.id, node)
    for (const key of ['channels', 'devices', 'groups', 'subGroups', 'tags'])
      for (const child of node[key] || []) walk(child)
  }
  walk(project)
  return result
}
const wrappers: VueWrapper[] = []
async function mountProject(lazy = false) {
  let persisted: any = sampleProject()
  mocks.summary.mockResolvedValue({ totalTagCount: lazy ? 100_000 : 10, fullProjectSupported: !lazy })
  mocks.project.mockImplementation(async () => ({ proj: copy(persisted), rev: 'r1' }))
  mocks.shell.mockImplementation(async () => {
    const project = copy(persisted)
    project.channels[0].devices[0].groups = []
    return { project, lazy: { d1: { groups: 2, tags: 0, totalTags: 10 } }, rev: 'r1' }
  })
  mocks.lazyChildren.mockImplementation(async (_kind, id, deep) => {
    const node = nodes(persisted).get(id)
    const groups = copy(node.groups || node.subGroups || [])
    const lazyCounts: Record<string, any> = {}
    if (!deep) for (const child of groups) {
      lazyCounts[child.id] = { groups: child.subGroups.length, tags: child.tags.length, totalTags: 5 }
      child.subGroups = []; child.tags = []
    }
    return { groups, tags: copy(node.tags), lazy: lazyCounts, rev: 'r1' }
  })
  // Only the HTTP storage boundary is doubled. Project.vue's real saveApply/lazy diff builds every command.
  mocks.mutate.mockImplementation(async (command: ProjectMutationRequest) => {
    const saved = copy(persisted)
    for (const mutation of mutations(command)) {
      if (mutation.kind !== 'upsertEntity') throw new Error(`Unexpected destructive mutation: ${mutation.kind}`)
      const index = nodes(saved)
      const document = JSON.parse(mutation.documentJson!)
      const existing = index.get(document.id)
      if (existing) {
        // UpsertEntity deserializes a complete TagDef, not a JSON merge-patch: omitted nullable fields clear.
        if (mutation.entityKind === 'tag') for (const key of Object.keys(existing)) delete existing[key]
        Object.assign(existing, document)
      } else {
        const parent = index.get(mutation.targetId!)
        if (mutation.entityKind === 'tag') parent.tags.push(document)
        else (parent.groups || parent.subGroups).push({ ...document, tags: [], subGroups: [] })
      }
    }
    persisted = saved
    return { revision: 'r1' }
  })
  const wrapper = shallowMount(Project, { global: {
    plugins: [createPinia()], mocks: { $t: (key: string) => key },
    directives: { loading: {} },
    stubs: { ElAutoResizer: { template: '<div />' }, ElTableColumn: { template: '<div />' },
      ElTag: true, ElTreeV2: true, ElEmpty: true, ElButton: true, ElTable: true, ElAlert: true,
      ElDialog: true, ElOption: true, ElSelect: true, ElInput: true, ElSwitch: true,
      ElFormItem: true, ElForm: true, ElInputNumber: true, ElSkeleton: true,
    },
  } })
  wrappers.push(wrapper)
  const state = (wrapper.vm.$ as any).setupState
  await vi.waitFor(() => expect(state.proj).toBeTruthy())
  return { state, persisted: () => copy(persisted) }
}
function select(state: any, id = 'd1') {
  const walk = (list: any[]): any => {
    for (const node of list) {
      if (node.ref?.id === id) return node
      const match = walk(node.children || [])
      if (match) return match
    }
  }
  // Set the selection directly so the import itself, not a prior click's request, must load lazy children.
  state.sel = walk(state.treeData)
  expect(state.sel).toBeTruthy()
}
async function importFile(state: any, content = 'DATA_BLOCK DB1\nVAR\nValue : REAL;\nEND_VAR\nEND_DATA_BLOCK') {
  const input = { files: [{ text: async () => content }], value: 'test.db' }
  await state.onDbFile({ target: input } as unknown as Event)
  expect(input.value).toBe('')
}

beforeEach(() => {
  session.account = { username: 'admin', displayName: '', isAdmin: true, mustChangePassword: false, disabled: false, createdUtc: '', permissions: {} }
  mocks.preview.mockImplementation(async () => copy(preview()))
  vi.spyOn(ElMessageBox, 'alert').mockResolvedValue('confirm' as any)
  vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue('confirm' as any)
  vi.spyOn(ElMessage, 'error').mockImplementation(() => ({ close: () => {} }))
})
afterEach(() => {
  wrappers.splice(0).forEach(wrapper => wrapper.unmount())
  clearSession(); vi.resetAllMocks(); vi.restoreAllMocks()
})

describe('Siemens DB file import merge through the project entry and lazy mutations', () => {
  it.each([false, true])('updates by name, adds missing nodes, keeps omitted nodes and site configuration (lazy=%s)', async lazy => {
    const { state, persisted } = await mountProject(lazy)
    select(state)
    await importFile(state)
    const device = state.proj.channels[0].devices[0]
    expect(device.groups.map((g: any) => g.name)).toEqual(['DB1', 'DB2'])
    const db = device.groups[0]
    expect(db).toMatchObject({ id: 'g1', description: '现场 DB 说明', kepProperties: { flowEnabled: true } })
    expect(db.tags.map((t: any) => t.name)).toEqual(['Value', 'Unmentioned', 'New'])
    expect(db.tags[0]).toMatchObject({ ...fieldSettings, metadata: { local: 'keep' }, id: 't1', address: 'DB1.DBD4', dataType: 'float', description: 'new comment', isArray: false })
    expect(db.tags[0].array).toBeUndefined()
    expect(db.tags[0].stringLength).toBeUndefined()
    expect(db.tags[0].metadata).toEqual({ local: 'keep' })
    expect(db.subGroups.map((g: any) => g.name)).toEqual(['Struct', 'LocalOnly', 'Struct[1]'])
    expect(db.subGroups[0]).toMatchObject({ id: 'g2', description: 'new struct comment', kepProperties: { custom: true } })
    expect(db.subGroups[0].tags.map((t: any) => t.name)).toEqual(['Inner', 'Hidden', 'NewNested'])
    expect(db.subGroups[0].tags[0]).toMatchObject({ id: 't2', address: 'DB1.DBW10' })
    if (lazy) {
      expect(device.groups[1]).toMatchObject({ id: 'other', name: 'DB2' })
      expect(mocks.lazyChildren).not.toHaveBeenCalledWith('group', 'other', true)
    } else expect(device.groups[1]).toEqual(sampleProject().channels[0].devices[0].groups[1])
    expect(state.proj.mesUpload).toEqual(sampleProject().mesUpload)
    const alerts = vi.mocked(ElMessageBox.alert).mock.calls
    expect(String(alerts[alerts.length - 1]?.[0])).toMatch(/新增.*3.*更新.*2/)

    expect(await state.saveApply(true)).toBe(true)
    const changes = mutations(mocks.mutate.mock.calls[0][0])
    expect(changes.every(change => change.kind === 'upsertEntity')).toBe(true)
    expect(changes.map(change => change.entityIds[0])).toEqual(expect.arrayContaining(['t1', 't2', 'g2']))
    expect(changes.some(change => ['keep1', 'keep2', 'other', 'otherT'].includes(change.entityIds[0]))).toBe(false)
    expect(mocks.putProject).not.toHaveBeenCalled()
    const saved = persisted()
    const ids = [...nodes(saved).keys()]
    expect(ids.length).toBe(16)
    expect(new Set(ids).size).toBe(ids.length)
    expect(saved.mesUpload).toEqual(sampleProject().mesUpload)

    await state.load()
    select(state)
    await importFile(state)
    expect(await state.saveApply(true)).toBe(true)
    expect(mocks.mutate).toHaveBeenCalledTimes(1) // Second import emits no diff after save/reopen.
    expect(persisted()).toEqual(saved)
    expect(mocks.online).not.toHaveBeenCalled()
  })

  it('merges into the selected DB itself, matching names case-insensitively without renaming bound paths', async () => {
    const { state } = await mountProject()
    const source = preview()
    source.dbName = 'db1'; source.root.tags[0].name = 'value'; source.root.subGroups[0].name = 'struct'
    mocks.preview.mockResolvedValue(source)
    select(state, 'g1')
    await importFile(state)
    const db = state.proj.channels[0].devices[0].groups[0]
    expect(db.tags[0]).toMatchObject({ id: 't1', name: 'Value', address: 'DB1.DBD4' })
    expect(db.subGroups.map((g: any) => g.name)).toEqual(['Struct', 'LocalOnly', 'Struct[1]'])
    const once = JSON.stringify(state.proj)
    await importFile(state)
    expect(JSON.stringify(state.proj)).toBe(once)
  })

  it('merges nested points when the DB is already loaded but its child groups are still lazy', async () => {
    const { state, persisted } = await mountProject(true)
    const device = state.proj.channels[0].devices[0]
    await state.ensureLoaded(device)
    await state.ensureLoaded(device.groups[0])
    expect(device.groups[0].subGroups[0].tags).toEqual([])
    select(state, 'g1')
    await importFile(state)
    expect(device.groups[0].subGroups[0].tags.map((tag: any) => tag.name)).toEqual(['Inner', 'Hidden', 'NewNested'])
    expect(await state.saveApply(true)).toBe(true)
    expect(persisted().channels[0].devices[0].groups[0].subGroups[0].tags[0]).toMatchObject({ id: 't2', address: 'DB1.DBW10' })
    expect(mutations(mocks.mutate.mock.calls[0][0]).every(change => change.kind === 'upsertEntity')).toBe(true)
  })

  it('keeps the original import target if selection changes while preview is in flight', async () => {
    const { state } = await mountProject()
    select(state)
    let finish!: (result: any) => void
    mocks.preview.mockReturnValue(new Promise(resolve => { finish = resolve }))
    const importing = importFile(state)
    await vi.waitFor(() => expect(mocks.preview).toHaveBeenCalled())
    select(state, 'other')
    finish(preview())
    await importing
    const device = state.proj.channels[0].devices[0]
    expect(device.groups[0].tags[0]).toMatchObject({ id: 't1', address: 'DB1.DBD4' })
    expect(device.groups[1]).toEqual(sampleProject().channels[0].devices[0].groups[1])
  })

  it('waits for an in-flight shallow DB load and then completes all nested loads before merging', async () => {
    const { state } = await mountProject(true)
    const device = state.proj.channels[0].devices[0]
    await state.ensureLoaded(device)
    const db = device.groups[0]
    const loadChildren = mocks.lazyChildren.getMockImplementation()!
    const shallowResult = await loadChildren('group', 'g1', false)
    const deepResult = await loadChildren('group', 'g1', true)
    let finishShallow!: (value: any) => void
    let finishDeep!: (value: any) => void
    const shallow = new Promise(resolve => { finishShallow = resolve })
    const deep = new Promise(resolve => { finishDeep = resolve })
    mocks.lazyChildren.mockImplementation((kind, id, isDeep) => id === 'g1'
      ? (isDeep ? deep : shallow) : loadChildren(kind, id, isDeep))
    const browsing = state.ensureLoaded(db)
    select(state)
    const importing = importFile(state)
    await vi.waitFor(() => expect(mocks.preview).toHaveBeenCalled())
    await flushPromises()
    finishShallow(shallowResult)
    await browsing
    finishDeep(deepResult)
    await importing
    expect(db.subGroups[0].tags[0]).toMatchObject({ id: 't2', name: 'Inner', address: 'DB1.DBW10' })
    expect(db.subGroups[0].tags.map((tag: any) => tag.name)).toEqual(['Inner', 'Hidden', 'NewNested'])
    expect(await state.saveApply(true)).toBe(true)
    const changes = mutations(mocks.mutate.mock.calls[0][0])
    expect(changes.filter(change => change.entityKind === 'tag').map(change => change.entityIds[0])).toContain('t2')
  })

  it('does not reuse a pending request from the previous project generation with the same device ID', async () => {
    const { state } = await mountProject(true)
    const oldDevice = state.proj.channels[0].devices[0]
    const loadChildren = mocks.lazyChildren.getMockImplementation()!
    const children = await loadChildren('device', 'd1', false)
    let finishOld!: (value: any) => void
    let finishNew!: (value: any) => void
    const oldResponse = new Promise(resolve => { finishOld = resolve })
    const newResponse = new Promise(resolve => { finishNew = resolve })
    let deviceRequests = 0
    mocks.lazyChildren.mockImplementation((kind, id, deep) => id === 'd1'
      ? (++deviceRequests === 1 ? oldResponse : newResponse) : loadChildren(kind, id, deep))
    const oldLoad = state.ensureLoaded(oldDevice)
    await state.load()
    const device = state.proj.channels[0].devices[0]
    expect(device).not.toBe(oldDevice)
    select(state)
    const importing = importFile(state)
    await vi.waitFor(() => expect(mocks.preview).toHaveBeenCalled())
    await flushPromises()
    finishOld(children)
    await oldLoad
    await flushPromises()
    // An old request's cleanup must not evict the new generation's pending request either.
    const anotherReader = state.ensureLoaded(device)
    finishNew(children)
    await Promise.all([importing, anotherReader])
    expect(deviceRequests).toBe(2)
    expect(device.groups.map((group: any) => group.name)).toEqual(['DB1', 'DB2'])
    expect(device.groups[0].tags[0]).toMatchObject({ id: 't1', address: 'DB1.DBD4' })
    expect(device.groups[0].subGroups[0].tags[0]).toMatchObject({ id: 't2', address: 'DB1.DBW10' })
  })

  it('does not merge if an overlapping shallow load fails', async () => {
    const { state } = await mountProject(true)
    const device = state.proj.channels[0].devices[0]
    await state.ensureLoaded(device)
    const before = JSON.stringify(state.proj)
    const loadChildren = mocks.lazyChildren.getMockImplementation()!
    let failShallow!: (error: Error) => void
    const shallow = new Promise((_resolve, reject) => { failShallow = reject })
    mocks.lazyChildren.mockImplementation((kind, id, deep) => id === 'g1' && !deep
      ? shallow : loadChildren(kind, id, deep))
    const browsing = state.ensureLoaded(device.groups[0]).catch(() => {})
    select(state)
    const importing = importFile(state)
    await vi.waitFor(() => expect(mocks.preview).toHaveBeenCalled())
    await flushPromises()
    failShallow(new Error('shallow load failed'))
    await Promise.all([browsing, importing])
    expect(JSON.stringify(state.proj)).toBe(before)
    expect(state.dirty).toBe(false)
  })

  it('does not import into a detached target after it is removed while preview is in flight', async () => {
    const { state } = await mountProject()
    select(state, 'g1')
    const device = state.proj.channels[0].devices[0]
    const removed = device.groups[0]
    const original = JSON.stringify(removed)
    let finish!: (result: any) => void
    mocks.preview.mockReturnValue(new Promise(resolve => { finish = resolve }))
    const importing = importFile(state)
    await vi.waitFor(() => expect(mocks.preview).toHaveBeenCalled())
    device.groups.splice(0, 1)
    const remaining = JSON.stringify(state.proj)
    finish(preview())
    await importing
    expect(JSON.stringify(removed)).toBe(original)
    expect(JSON.stringify(state.proj)).toBe(remaining)
    expect(state.dirty).toBe(false)
  })

  it.each(['empty', 'no DB', 'zero tags', 'parse error'])('%s leaves the project unchanged', async mode => {
    const { state } = await mountProject()
    select(state)
    const before = JSON.stringify(state.proj)
    if (mode === 'zero tags') mocks.preview.mockResolvedValue({ ...preview(), tagCount: 0, root: group('', 'root') })
    if (mode === 'parse error') mocks.preview.mockRejectedValue(new Error('ARRAY dimension invalid'))
    await importFile(state, mode === 'empty' ? '' : mode === 'no DB' ? 'TYPE UDT END_TYPE' : undefined)
    expect(JSON.stringify(state.proj)).toBe(before)
    expect(state.dirty).toBe(false)
    expect(mocks.mutate).not.toHaveBeenCalled()
  })

  it('does not apply any partial import when a nested group/tag name conflicts', async () => {
    const { state } = await mountProject()
    select(state)
    const before = JSON.stringify(state.proj)
    const source = preview()
    source.root.subGroups.push(group('', 'Unmentioned', [tag('', 'Conflict')]))
    mocks.preview.mockResolvedValue(source)
    await importFile(state)
    expect(JSON.stringify(state.proj)).toBe(before)
    expect(state.dirty).toBe(false)
    expect(ElMessage.error).toHaveBeenCalled()
  })

  it('does not import into an unloaded target if loading fails', async () => {
    const { state } = await mountProject(true)
    select(state)
    mocks.lazyChildren.mockRejectedValue(new Error('offline'))
    const before = JSON.stringify(state.proj)
    await importFile(state)
    expect(JSON.stringify(state.proj)).toBe(before)
    expect(state.dirty).toBe(false)
  })
})
