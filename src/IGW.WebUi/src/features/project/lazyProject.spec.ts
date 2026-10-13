import { describe, expect, it } from 'vitest'
import {
  applyLazyChildren,
  assignLazyIds,
  buildLazyMutations,
  captureBaseline,
  combineLazyMutations,
  createLazyState,
  hasUnloadedSubGroups,
  LazySaveUnsupportedError,
  needsLoad,
  removedMesTasks,
  unloadedTagTotal,
} from './lazyProject'

function shell() {
  return {
    name: '项目', settings: { restPort: 48020 }, opcUa: { enabled: true }, history: {}, mqttPublish: {},
    mesUpload: { tasks: [{ id: 'm1', groupRef: 'C.D.DB1', enabled: false }] },
    channels: [{ id: 'c1', name: 'C', driverId: 'Siemens.S7', settings: {}, devices: [
      { id: 'd1', name: 'D', endpoint: '10.0.0.1', settings: {}, groups: [], tags: [] },
    ] }],
  }
}

function loadedProject() {
  const project = shell()
  const state = createLazyState()
  state.counts.set('d1', { groups: 2, tags: 0, totalTags: 3 })
  captureBaseline(state, project)
  const device = project.channels[0].devices[0]
  applyLazyChildren(state, 'device', device, {
    groups: [{ id: 'g1', name: 'DB1' }, { id: 'g2', name: 'DB2' }],
    tags: [],
    lazy: { g1: { groups: 1, tags: 2, totalTags: 3 }, g2: { groups: 0, tags: 0, totalTags: 0 } },
  })
  const g1: any = device.groups[0]
  applyLazyChildren(state, 'group', g1, {
    groups: [{ id: 'g11', name: 'Sub' }],
    tags: [{ id: 't1', name: 'A', address: 'DB1.DBW0', dataType: 'int16' }, { id: 't2', name: 'B', address: 'DB1.DBW2', dataType: 'int16' }],
    lazy: { g11: { groups: 0, tags: 1, totalTags: 1 } },
  })
  return { project, state, device: device as any, g1 }
}

describe('大工程按需载入', () => {
  it('未载入的设备和组显示下级数量并标记需要载入', () => {
    const project = shell()
    const state = createLazyState()
    state.counts.set('d1', { groups: 2, tags: 0, totalTags: 12 })
    const device = project.channels[0].devices[0]
    expect(needsLoad(state, device)).toBe(true)
    expect(hasUnloadedSubGroups(state, device)).toBe(true)
    expect(unloadedTagTotal(state, device)).toBe(12)
  })

  it('没有修改时不产生任何保存请求', () => {
    const { project, state } = loadedProject()
    assignLazyIds(state, project)
    expect(buildLazyMutations(state, project)).toEqual([])
  })

  it('改名、新增、删除分别生成局部修改，删除只删最上层且排在最前', () => {
    const { project, state, device, g1 } = loadedProject()
    g1.name = 'DB1_new'
    g1.tags.push({ id: '', name: 'C', address: 'DB1.DBW4', dataType: 'int16' })
    device.groups.splice(1, 1)          // 删除 g2
    g1.subGroups.splice(0, 1)          // 删除 g11(未载入的下级由数据库级联删除)
    assignLazyIds(state, project)
    const mutations = buildLazyMutations(state, project)
    expect(mutations[0]).toMatchObject({ kind: 'deleteEntities', entityKind: 'group' })
    expect([...mutations[0].entityIds].sort()).toEqual(['g11', 'g2'])
    const upserts = mutations.filter(item => item.kind === 'upsertEntity')
    expect(upserts.map(item => [item.entityKind, item.targetId])).toEqual([['group', 'd1'], ['tag', 'g1']])
    expect(JSON.parse(upserts[0].documentJson!)).toEqual({ id: 'g1', name: 'DB1_new' })
    expect(upserts[1].entityIds[0]).toMatch(/^[0-9a-f]{32}$/)
  })

  it('复制粘贴出的下级沿用了原 ID 时改用新 ID，原节点保持不动', () => {
    const { project, state, device, g1 } = loadedProject()
    const copy = JSON.parse(JSON.stringify(g1))
    copy.id = ''
    copy.name = 'DB1_1'
    device.groups.push(copy)
    assignLazyIds(state, project)
    expect(g1.tags.map((tag: any) => tag.id)).toEqual(['t1', 't2'])
    expect(copy.tags.map((tag: any) => tag.id)).not.toContain('t1')
    const mutations = buildLazyMutations(state, project)
    expect(mutations.every(item => item.kind === 'upsertEntity')).toBe(true)
    expect(mutations.find(item => item.entityIds[0] === copy.id)?.targetId).toBe('d1')
    expect(mutations.filter(item => item.entityKind === 'tag').every(item => item.targetId !== 'g1')).toBe(true)
  })

  it('工程设置和采集流程只提交改动部分，名称等只读属性改动会被拒绝', () => {
    const { project, state } = loadedProject()
    ;(project.opcUa as any).port = 4841
    ;(project.mesUpload.tasks[0] as any).enabled = true
    const mutations = buildLazyMutations(state, project)
    expect(mutations.map(item => item.kind)).toEqual(['replaceProjectSections', 'saveMesTasks'])
    expect(mutations[0].entityIds).toEqual(['opcUa'])
    expect(JSON.parse(mutations[1].documentJson!).tasks).toHaveLength(1)
    project.name = '改名'
    expect(() => buildLazyMutations(state, project)).toThrow(LazySaveUnsupportedError)
  })

  it('多个修改合成一个批量修改', () => {
    expect(combineLazyMutations([])).toBeNull()
    const single = { kind: 'deleteEntities', entityKind: 'tag', entityIds: ['t1'], targetId: null, documentJson: null } as const
    expect(combineLazyMutations([single])).toBe(single)
    const batch = combineLazyMutations([single, single])!
    expect(batch.kind).toBe('batch')
    expect(JSON.parse(batch.documentJson!)).toHaveLength(2)
  })

  it('整棵子树载入后下级全部视为已载入', () => {
    const project = shell()
    const state = createLazyState()
    state.counts.set('d1', { groups: 1, tags: 0, totalTags: 1 })
    captureBaseline(state, project)
    const device: any = project.channels[0].devices[0]
    applyLazyChildren(state, 'device', device, {
      groups: [{ id: 'g1', name: 'DB1', subGroups: [{ id: 'g11', name: 'S', subGroups: [], tags: [{ id: 't1', name: 'A' }] }], tags: [] }],
      tags: [], lazy: {},
    }, true)
    expect(needsLoad(state, device.groups[0])).toBe(false)
    expect(needsLoad(state, device.groups[0].subGroups[0])).toBe(false)
    expect(buildLazyMutations(state, project)).toEqual([])
  })
})

describe('普通工程(全部载入)的局部保存', () => {
  function fullProject(): any {
    const project: any = shell()
    project.channels[0].devices = [
      { id: 'd1', name: 'D', endpoint: '10.0.0.1', settings: {}, tags: [], groups: [
        { id: 'g1', name: 'DB1', subGroups: [{ id: 'g11', name: 'Sub', subGroups: [], tags: [{ id: 't3', name: 'C', address: 'DB1.DBW4' }] }],
          tags: [{ id: 't1', name: 'A', address: 'DB1.DBW0' }, { id: 't2', name: 'B', address: 'DB1.DBW2' }] },
      ] },
      { id: 'd2', name: 'E', endpoint: '10.0.0.2', settings: {}, tags: [], groups: [] },
    ]
    return project
  }

  it('没有改动时不产生任何修改', () => {
    const project = fullProject()
    const state = createLazyState()
    captureBaseline(state, project)
    assignLazyIds(state, project)
    expect(combineLazyMutations(buildLazyMutations(state, project))).toBeNull()
  })

  it('只提交改动过的点', () => {
    const project = fullProject()
    const state = createLazyState()
    captureBaseline(state, project)
    project.channels[0].devices[0].groups[0].tags[1].address = 'DB1.DBW6'
    assignLazyIds(state, project)
    const mutations = buildLazyMutations(state, project)
    expect(mutations).toHaveLength(1)
    expect(mutations[0]).toMatchObject({ kind: 'upsertEntity', entityKind: 'tag', entityIds: ['t2'], targetId: 'g1' })
  })

  it('跨设备移动的组按删除旧组 + 整棵子树新建保存', () => {
    const project = fullProject()
    const state = createLazyState()
    captureBaseline(state, project)
    const [from, to] = project.channels[0].devices
    to.groups.push(from.groups.pop())
    assignLazyIds(state, project)
    const mutations = buildLazyMutations(state, project)
    expect(mutations[0]).toMatchObject({ kind: 'deleteEntities', entityKind: 'group', entityIds: ['g1'] })
    const moved = to.groups[0]
    expect(moved.id).not.toBe('g1')
    expect(moved.subGroups[0].id).not.toBe('g11')
    expect(moved.tags[0].id).not.toBe('t1')
    const created = mutations.filter(m => m.kind === 'upsertEntity').map(m => m.entityIds[0])
    expect(created).toEqual(expect.arrayContaining([moved.id, moved.subGroups[0].id, moved.tags[0].id, moved.subGroups[0].tags[0].id]))
    expect(mutations.find(m => m.entityIds[0] === moved.id)?.targetId).toBe('d2')
  })

  it('同一设备内移动的组保留 ID，只改父节点', () => {
    const project = fullProject()
    const state = createLazyState()
    captureBaseline(state, project)
    const device = project.channels[0].devices[0]
    const sub = device.groups[0].subGroups.pop()
    device.groups.push(sub)
    assignLazyIds(state, project)
    const mutations = buildLazyMutations(state, project)
    expect(mutations).toHaveLength(1)
    expect(mutations[0]).toMatchObject({ kind: 'upsertEntity', entityKind: 'group', entityIds: ['g11'], targetId: 'd1' })
  })

  it('删除了 MES 任务时需要整包保存', () => {
    const project = fullProject()
    const state = createLazyState()
    captureBaseline(state, project)
    expect(removedMesTasks(state, project)).toBe(false)
    project.mesUpload.tasks = []
    expect(removedMesTasks(state, project)).toBe(true)
  })
})
