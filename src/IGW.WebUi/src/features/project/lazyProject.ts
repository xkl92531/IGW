import type { LazyCountsDto, ProjectLazyChildrenDto, ProjectMutationRequest } from './types'
import { createCompactId } from '../../utils/runtimeId'

/**
 * 工程配置的局部保存：不上传整个工程，而是把“载入时的样子”和“现在的样子”逐个节点比较，生成局部修改：
 * 先删除(只删最上层，下级由数据库级联)，再按父先子后的顺序新增/更新。
 * 大工程(超过 5 万点)还会按需载入设备/组的下级；小工程一次载入全部内容，同样按局部修改保存。
 */

export type LazyEntityKind = 'channel' | 'device' | 'group' | 'tag'
type ContainerKind = 'device' | 'group'

interface BaselineEntry { kind: LazyEntityKind; parentId: string | null; json: string }
interface Visit { kind: LazyEntityKind; def: any; parentId: string | null }

export interface LazyProjectState {
  /** 尚未载入的设备/组：下级数量。 */
  counts: Map<string, LazyCountsDto>
  /** 已载入直接下级的设备/组。 */
  loaded: Set<string>
  baseline: Map<string, BaselineEntry>
  mesBaseline: Map<string, string>
  sectionBaseline: Record<string, string>
}

/** 可以局部保存的工程级设置段；其余工程级属性在大工程里只读。 */
const PARTIAL_SECTIONS = ['settings', 'opcUa', 'history'] as const
const READONLY_KEYS = ['name', 'description', 'mqttPublish', 'security', 'computedTags', 'schedules', 'templates', 'kepProperties']

export function createLazyState(): LazyProjectState {
  return { counts: new Map(), loaded: new Set(), baseline: new Map(), mesBaseline: new Map(), sectionBaseline: {} }
}

/** 节点自身的内容(不含下级)，与服务端局部修改接口要求的文档一致。 */
export function headerJson(kind: LazyEntityKind, def: any): string {
  if (kind === 'tag') return JSON.stringify(def)
  const { devices: _devices, groups: _groups, subGroups: _subGroups, tags: _tags, ...header } = def
  return JSON.stringify(header)
}

function* walkContainer(owner: any, groups: any[] | undefined, tags: any[] | undefined): Generator<Visit> {
  for (const group of groups || []) {
    yield { kind: 'group', def: group, parentId: owner.id || null }
    yield* walkContainer(group, group.subGroups, group.tags)
  }
  for (const tag of tags || []) yield { kind: 'tag', def: tag, parentId: owner.id || null }
}

/** 父先子后遍历工程里当前存在的全部节点。 */
export function* walkProject(project: any): Generator<Visit> {
  for (const channel of project?.channels || []) {
    yield { kind: 'channel', def: channel, parentId: null }
    for (const device of channel.devices || []) {
      yield { kind: 'device', def: device, parentId: channel.id || null }
      yield* walkContainer(device, device.groups, device.tags)
    }
  }
}

function mesTaskJson(task: any): string { return JSON.stringify(task) }

function sectionJson(project: any, key: string): string {
  if (key === 'mesUpload') {
    const { tasks: _tasks, ...rest } = project?.mesUpload || {}
    return JSON.stringify(rest)
  }
  return JSON.stringify(project?.[key] ?? null)
}

function readonlyJson(project: any): string {
  return JSON.stringify([...READONLY_KEYS.map(key => project?.[key] ?? null), sectionJson(project, 'mesUpload')])
}

/** 以当前内存中的工程为保存基准(载入骨架后、每次保存成功后调用)。 */
export function captureBaseline(state: LazyProjectState, project: any) {
  state.baseline.clear()
  for (const visit of walkProject(project))
    if (visit.def.id) state.baseline.set(visit.def.id, { kind: visit.kind, parentId: visit.parentId, json: headerJson(visit.kind, visit.def) })
  state.mesBaseline = new Map((project?.mesUpload?.tasks || []).map((task: any) => [String(task.id), mesTaskJson(task)]))
  state.sectionBaseline = Object.fromEntries([
    ...PARTIAL_SECTIONS.map(key => [key, sectionJson(project, key)]),
    ['readonly', readonlyJson(project)],
  ])
}

/** 某个节点的内容已与服务端一致(例如从服务端同步了设备认证设置)：更新它的保存基准，不影响其它未保存修改。 */
export function refreshBaselineNode(state: LazyProjectState, kind: LazyEntityKind, def: any, parentId: string | null) {
  if (def?.id) state.baseline.set(def.id, { kind, parentId, json: headerJson(kind, def) })
}

/** 与基准相比删掉了 MES 任务(局部修改接口不支持删除任务，需要整包保存)。 */
export function removedMesTasks(state: LazyProjectState, project: any): boolean {
  const current = new Set((project?.mesUpload?.tasks || []).map((task: any) => String(task.id)))
  for (const id of state.mesBaseline.keys()) if (!current.has(id)) return true
  return false
}

export function needsLoad(state: LazyProjectState, def: any): boolean {
  return !!def?.id && state.counts.has(def.id) && !state.loaded.has(def.id)
}

/** 未载入的设备/组有没有子组(决定树上是否显示展开箭头)。 */
export function hasUnloadedSubGroups(state: LazyProjectState, def: any): boolean {
  return needsLoad(state, def) && (state.counts.get(def.id)?.groups || 0) > 0
}

/** 未载入节点的总点数；已载入时返回 undefined，由调用方按实际内容统计。 */
export function unloadedTagTotal(state: LazyProjectState, def: any): number | undefined {
  return needsLoad(state, def) ? state.counts.get(def.id)?.totalTags ?? 0 : undefined
}

function markLoadedDeep(state: LazyProjectState, group: any) {
  group.subGroups ||= []
  group.tags ||= []
  state.loaded.add(group.id)
  state.baseline.set(group.id, state.baseline.get(group.id) ?? { kind: 'group', parentId: null, json: headerJson('group', group) })
  for (const sub of group.subGroups) {
    state.baseline.set(sub.id, { kind: 'group', parentId: group.id, json: headerJson('group', sub) })
    markLoadedDeep(state, sub)
  }
  for (const tag of group.tags) state.baseline.set(tag.id, { kind: 'tag', parentId: group.id, json: headerJson('tag', tag) })
}

/** 把服务端返回的下级内容挂到设备/组上。deep=true 时下级是完整子树。 */
export function applyLazyChildren(state: LazyProjectState, kind: ContainerKind, owner: any, children: ProjectLazyChildrenDto, deep = false) {
  const groups = children.groups || []
  if (kind === 'device') owner.groups = groups
  else owner.subGroups = groups
  owner.tags = children.tags || []
  for (const [id, counts] of Object.entries(children.lazy || {})) state.counts.set(id, counts)
  state.loaded.add(owner.id)
  for (const group of groups) {
    group.subGroups ||= []
    group.tags ||= []
    state.baseline.set(group.id, { kind: 'group', parentId: owner.id, json: headerJson('group', group) })
    if (deep) markLoadedDeep(state, group)
  }
  for (const tag of owner.tags) state.baseline.set(tag.id, { kind: 'tag', parentId: owner.id, json: headerJson('tag', tag) })
}

/**
 * 新建/粘贴的节点补上 ID。复制粘贴会让下级沿用原节点 ID：
 * 同一 ID 出现多次时，保留仍在原父节点下的那一个，其余改用新 ID，避免“复制”变成“移动”。
 */
export function assignLazyIds(state: LazyProjectState, project: any) {
  const byId = new Map<string, Visit[]>()
  for (const visit of walkProject(project)) {
    if (!visit.def.id) continue
    const list = byId.get(visit.def.id)
    if (list) list.push(visit)
    else byId.set(visit.def.id, [visit])
  }
  const keepers = new Map<string, any>()
  for (const [id, visits] of byId) {
    if (visits.length < 2) continue
    const base = state.baseline.get(id)
    keepers.set(id, (visits.find(visit => base && visit.parentId === base.parentId) ?? visits[0]).def)
  }
  // 逐个遍历(父节点换新 ID 后，子节点记录的父 ID 随之更新)。
  for (const visit of walkProject(project)) {
    const id = visit.def.id
    if (!id) visit.def.id = createCompactId()
    else if (keepers.has(id) && keepers.get(id) !== visit.def) visit.def.id = createCompactId()
  }
  renewCrossDeviceMoves(state, project)
}

/** 保存基准里某个节点所属的设备 ID(不在基准里时为 null)。 */
function baselineDeviceOf(state: LazyProjectState, id: string): string | null {
  let current: string | null = id
  for (let guard = 0; current && guard < 256; guard++) {
    const entry = state.baseline.get(current)
    if (!entry) return null
    if (entry.kind === 'device') return current
    current = entry.parentId
  }
  return null
}

/**
 * 跨设备移动的组(拖放、剪切粘贴)整棵子树改用新 ID，按“删除旧的 + 新建”保存：
 * 服务端局部修改只改被移动组自身的所属设备，下级组和点仍会留在原设备上。
 */
function renewCrossDeviceMoves(state: LazyProjectState, project: any) {
  const visit = (deviceId: string, groups: any[] | undefined) => {
    for (const group of groups || []) {
      const before = group.id ? baselineDeviceOf(state, group.id) : null
      if (before && before !== deviceId) renewSubtreeIds(group)
      else visit(deviceId, group.subGroups)
    }
  }
  for (const channel of project?.channels || [])
    for (const device of channel.devices || [])
      if (device.id) visit(device.id, device.groups)
}

/** 整棵子树改用新 ID(跨设备移动时按“删除旧的 + 新建”保存)。 */
export function renewSubtreeIds(group: any) {
  group.id = createCompactId()
  for (const sub of group.subGroups || []) renewSubtreeIds(sub)
  for (const tag of group.tags || []) tag.id = createCompactId()
}

export class LazySaveUnsupportedError extends Error {}

/** 与基准比较，得到需要提交的局部修改。调用前应先 assignLazyIds。 */
export function buildLazyMutations(state: LazyProjectState, project: any): ProjectMutationRequest[] {
  const current = new Map<string, Visit>()
  for (const visit of walkProject(project)) current.set(visit.def.id, visit)

  const deletes: Record<LazyEntityKind, string[]> = { tag: [], group: [], device: [], channel: [] }
  for (const [id, entry] of state.baseline)
    if (!current.has(id) && (entry.parentId === null || current.has(entry.parentId))) deletes[entry.kind].push(id)

  const mutations: ProjectMutationRequest[] = []
  for (const kind of ['tag', 'group', 'device', 'channel'] as const)
    if (deletes[kind].length)
      mutations.push({ kind: 'deleteEntities', entityKind: kind, entityIds: deletes[kind], targetId: null, documentJson: null })

  for (const visit of current.values()) {
    const json = headerJson(visit.kind, visit.def)
    const base = state.baseline.get(visit.def.id)
    if (base && base.json === json && base.parentId === visit.parentId) continue
    mutations.push({ kind: 'upsertEntity', entityKind: visit.kind, entityIds: [visit.def.id], targetId: visit.parentId, documentJson: json })
  }

  const changedSections = PARTIAL_SECTIONS.filter(key => state.sectionBaseline[key] !== sectionJson(project, key))
  if (changedSections.length)
    mutations.push({
      kind: 'replaceProjectSections', entityKind: 'project', entityIds: changedSections, targetId: null,
      documentJson: JSON.stringify(Object.fromEntries(changedSections.map(key => [key, project[key]]))),
    })
  if (state.sectionBaseline.readonly !== undefined && state.sectionBaseline.readonly !== readonlyJson(project))
    throw new LazySaveUnsupportedError('大工程模式下暂不支持修改工程名称、说明、IoT 发布、MES 全局设置等工程属性，请撤销这些修改后再保存。')

  const changedTasks = (project?.mesUpload?.tasks || []).filter((task: any) => state.mesBaseline.get(String(task.id)) !== mesTaskJson(task))
  if (changedTasks.length)
    mutations.push({
      kind: 'saveMesTasks', entityKind: 'project', entityIds: [], targetId: null,
      documentJson: JSON.stringify({ tasks: changedTasks, periodTaskIds: [] }),
    })
  return mutations
}

/** 多个修改合成一个批量修改(服务端同一事务)，只有一个时直接提交。 */
export function combineLazyMutations(mutations: ProjectMutationRequest[]): ProjectMutationRequest | null {
  if (!mutations.length) return null
  if (mutations.length === 1) return mutations[0]
  return { kind: 'batch', entityKind: 'project', entityIds: [], targetId: null, documentJson: JSON.stringify(mutations) }
}
