export interface MesRangeLeafTag {
  relativePath: string
  tagId: string
}

export interface MesRangeOption {
  sourcePath: string
  devicePath: string
  label: string
  scope: 'Group' | 'Struct' | 'Array' | 'Tag'
  tagCount: number
  defaultOutputPath: string
  children: MesRangeOption[]
  leafTags: MesRangeLeafTag[]
  searchText: string
  array?: any
  arrayMembers?: { path: string; label: string; dataType: string }[]
}

export interface MesRangeIndex {
  roots: MesRangeOption[]
  all: MesRangeOption[]
  bySourcePath: Map<string, MesRangeOption>
}

export interface MesTaskBoundary {
  sourcePath: string
  devicePath: string
  label: string
  tagCount: number
}

export interface MesTopologyReconcileResult {
  removedTasks: any[]
  clearedPayloads: number
  clearedRanges: number
}

const clone = <T>(value: T): T => JSON.parse(JSON.stringify(value))
const join = (parent: string, name: string) => parent ? `${parent}.${name}` : name
const automaticOutputPath = (sourcePath: string) => sourcePath.split('.').slice(1).join('.')

interface IndexedGroupElement {
  index: number
  group: any
}

export interface MesScalarTagPath {
  path: string
  name: string
}

interface IndexedGroupArray {
  label: string
  elements: IndexedGroupElement[]
  consumed: Set<any>
}

function tagPointCount(tag: any): number {
  if (!tag?.array) return 1
  return (tag.array.dimensions || []).reduce(
    (count: number, size: number) => count * Math.max(0, Number(size)), 1)
}

function groupPointCount(group: any): number {
  return (group?.tags || []).reduce((sum: number, tag: any) => sum + tagPointCount(tag), 0)
    + (group?.subGroups || []).reduce((sum: number, child: any) => sum + groupPointCount(child), 0)
}

function pureIndex(name: unknown): number | null {
  const value = String(name ?? '').trim()
  if (!/^-?\d+$/.test(value)) return null
  const parsed = Number(value)
  return Number.isSafeInteger(parsed) ? parsed : null
}

function namedIndex(name: unknown): { label: string; index: number } | null {
  const value = String(name ?? '').trim()
  let match = /^(.*)\[(-?\d+)\]$/.exec(value)
  if (!match) match = /^(.*)_(-?\d+)$/.exec(value)
  if (!match || !match[1]) return null
  const index = Number(match[2])
  return Number.isSafeInteger(index) ? { label: match[1], index } : null
}

function groupSchemaSignature(group: any): string {
  const tags = (group?.tags || []).map((tag: any) => ({
    name: String(tag?.name || ''),
    type: String(tag?.dataType || ''),
    dimensions: (tag?.array?.dimensions || []).map(Number),
    lowerBounds: (tag?.array?.lowerBounds || []).map(Number),
    struct: tag?.array?.struct || null,
  }))
  const groups = (group?.subGroups || []).map((child: any) => ({
    name: String(child?.name || ''),
    schema: groupSchemaSignature(child),
  }))
  return JSON.stringify({ tags, groups })
}

function normalizeIndexedElements(elements: IndexedGroupElement[]): IndexedGroupElement[] | null {
  if (elements.length < 2) return null
  const sorted = [...elements].sort((left, right) => left.index - right.index)
  const schema = groupSchemaSignature(sorted[0].group)
  for (let i = 0; i < sorted.length; i++) {
    if (i > 0 && sorted[i].index !== sorted[i - 1].index + 1) return null
    if (groupSchemaSignature(sorted[i].group) !== schema) return null
  }
  return sorted
}

function numericContainerArray(group: any): IndexedGroupArray | null {
  const children = group?.subGroups || []
  if ((group?.tags || []).length || children.length < 2) return null
  const elements: IndexedGroupElement[] = []
  for (const child of children) {
    const index = pureIndex(child?.name)
    if (index === null) return null
    elements.push({ index, group: child })
  }
  const normalized = normalizeIndexedElements(elements)
  return normalized ? { label: String(group.name || ''), elements: normalized, consumed: new Set(children) } : null
}

function namedSiblingArrays(groups: any[]): Map<any, IndexedGroupArray> {
  const candidates = new Map<string, IndexedGroupElement[]>()
  for (const group of groups || []) {
    const parsed = namedIndex(group?.name)
    if (!parsed) continue
    const bucket = candidates.get(parsed.label) || []
    bucket.push({ index: parsed.index, group })
    candidates.set(parsed.label, bucket)
  }
  const result = new Map<any, IndexedGroupArray>()
  for (const [label, candidate] of candidates) {
    const elements = normalizeIndexedElements(candidate)
    if (!elements) continue
    const array = { label, elements, consumed: new Set(elements.map((item) => item.group)) }
    result.set(elements[0].group, array)
  }
  return result
}

function buildGroupArrayMembers(group: any, prefix = ''): { path: string; label: string; dataType: string }[] {
  const result: { path: string; label: string; dataType: string }[] = []
  for (const tag of group?.tags || []) {
    const path = prefix ? `${prefix}.${tag.name}` : tag.name
    result.push({ path, label: tag.jsonName || tag.name, dataType: tag.dataType || '' })
  }
  const siblings = namedSiblingArrays(group?.subGroups || [])
  const consumed = new Set<any>()
  for (const child of group?.subGroups || []) {
    if (consumed.has(child)) continue
    const numeric = numericContainerArray(child)
    const sibling = siblings.get(child)
    const nested = numeric || sibling
    if (nested) {
      nested.consumed.forEach((item) => consumed.add(item))
      const name = numeric ? child.name : nested.label
      const path = prefix ? `${prefix}.${name}` : name
      result.push({ path, label: name, dataType: 'Array' })
      continue
    }
    const path = prefix ? `${prefix}.${child.name}` : child.name
    result.push(...buildGroupArrayMembers(child, path))
  }
  return result
}

function buildIndexedArrayOption(
  array: IndexedGroupArray,
  sourcePath: string,
  devicePath: string,
  deviceName: string,
): MesRangeOption {
  const first = array.elements[0]
  return {
    sourcePath,
    devicePath,
    label: array.label,
    scope: 'Array',
    tagCount: array.elements.reduce((sum, element) => sum + groupPointCount(element.group), 0),
    defaultOutputPath: automaticOutputPath(sourcePath),
    children: [],
    leafTags: [],
    searchText: `${sourcePath} ${deviceName} ${array.label} array 数组`.toLowerCase(),
    array: {
      dimensions: [array.elements.length],
      lowerBounds: [first.index],
      groupBacked: true,
    },
    arrayMembers: buildGroupArrayMembers(first.group),
  }
}

export function countMesGroupTags(group: any): number {
  return (group?.tags || []).length
    + (group?.subGroups || []).reduce((sum: number, child: any) => sum + countMesGroupTags(child), 0)
}

export function isMesTaskBoundaryGroup(
  driverId: string,
  group: any,
  depth: number,
  ancestorHasDirectTags: boolean,
  parentIsPrimaryBoundary = false,
): boolean {
  if (countMesGroupTags(group) <= 0) return false
  if (parentIsPrimaryBoundary) return true
  if (driverId === 'OpcUa.Client') return depth === 0
  return (group?.tags || []).length > 0 && !ancestorHasDirectTags
}

function findGroup(groups: any[], path: string): any | null {
  let current = groups
  let found: any = null
  for (const segment of path.split('.').filter(Boolean)) {
    found = (current || []).find((group: any) => group.name === segment)
    if (!found) return null
    current = found.subGroups || []
  }
  return found
}

function removePath(groups: any[], path: string): void {
  const segments = path.split('.').filter(Boolean)
  if (!segments.length) return
  let current = groups
  for (let i = 0; i < segments.length; i++) {
    const groupIndex = current.findIndex((group: any) => group.name === segments[i])
    if (groupIndex < 0) return
    const group = current[groupIndex]
    if (i === segments.length - 1) {
      current.splice(groupIndex, 1)
      return
    }
    if (i === segments.length - 2) {
      const tagIndex = (group.tags || []).findIndex((tag: any) => tag.name === segments[i + 1])
      if (tagIndex >= 0) group.tags.splice(tagIndex, 1)
      else removePath(group.subGroups || [], segments.slice(i + 1).join('.'))
      return
    }
    current = group.subGroups || []
  }
}

function effectiveGroups(project: any, device: any): any[] {
  if (!device?.templateRef) return clone(device?.groups || [])
  const template = (project?.templates || []).find((item: any) =>
    String(item.name).toLowerCase() === String(device.templateRef).toLowerCase())
  const groups = clone(template?.groups || [])
  const delta = device.delta || {}
  for (const mask of delta.masks || []) removePath(groups, mask)
  for (const add of delta.adds || []) {
    const target = add.groupPath ? findGroup(groups, add.groupPath) : null
    if (add.groupPath && !target) continue
    const tags = add.groupPath ? (target.tags ||= []) : (device.tags ||= [])
    if (add.tag && !tags.some((tag: any) => tag.name === add.tag.name)) tags.push(clone(add.tag))
  }
  return groups
}

/** 按任务绑定的完整组路径读取工程属性说明；模板实例先展开后再解析。 */
export function mesGroupDescription(project: any, sourcePath: string): string {
  const segments = String(sourcePath || '').split('.').filter(Boolean)
  if (segments.length < 3) return ''
  const channel = (project?.channels || []).find((item: any) => item?.name === segments[0])
  const device = (channel?.devices || []).find((item: any) => item?.name === segments[1])
  if (!device) return ''
  const group = findGroup(effectiveGroups(project, device), segments.slice(2).join('.'))
  return String(group?.description || '').trim()
}

function buildGroupOption(
  group: any,
  channelName: string,
  deviceName: string,
  parentPath: string,
  depth: number,
): MesRangeOption {
  const sourcePath = join(parentPath, group.name)
  const devicePath = `${channelName}.${deviceName}`
  const numericArray = numericContainerArray(group)
  if (numericArray) return buildIndexedArrayOption(numericArray, sourcePath, devicePath, deviceName)

  const children: MesRangeOption[] = []
  const siblingArrays = namedSiblingArrays(group.subGroups || [])
  const consumed = new Set<any>()
  for (const child of group.subGroups || []) {
    if (consumed.has(child)) continue
    const array = siblingArrays.get(child)
    if (array) {
      array.consumed.forEach((item) => consumed.add(item))
      children.push(buildIndexedArrayOption(array, `${sourcePath}.${array.label}`, devicePath, deviceName))
    } else {
      children.push(buildGroupOption(child, channelName, deviceName, sourcePath, depth + 1))
    }
  }
  for (const tag of group.tags || []) {
    if (!tag?.array) continue
    const arrayPath = `${sourcePath}.${tag.name}`
    const dimensions = (tag.array.dimensions || []).map(Number)
    const tagCount = dimensions.reduce((total: number, value: number) => total * Math.max(0, value), 1)
    children.push({
      sourcePath: arrayPath,
      devicePath,
      label: tag.name,
      scope: 'Array',
      tagCount,
      defaultOutputPath: automaticOutputPath(arrayPath),
      children: [],
      leafTags: [],
      searchText: `${arrayPath} ${deviceName} ${tag.name} array 数组`.toLowerCase(),
      array: clone(tag.array),
      arrayMembers: buildArrayMembers(tag.array.struct),
    })
  }
  const leafTags: MesRangeLeafTag[] = []
  for (const tag of group.tags || [])
    leafTags.push({ relativePath: tag.name, tagId: `${sourcePath}.${tag.name}` })
  for (const child of children)
    for (const tag of child.leafTags)
      leafTags.push({ relativePath: `${child.label}.${tag.relativePath}`, tagId: tag.tagId })
  return {
    sourcePath,
    devicePath,
    label: group.name,
    scope: depth === 0 ? 'Group' : 'Struct',
    tagCount: groupPointCount(group),
    defaultOutputPath: automaticOutputPath(sourcePath),
    children,
    leafTags,
    searchText: `${sourcePath} ${deviceName} ${group.name}`.toLowerCase(),
  }
}

export function buildMesRangeIndex(project: any): MesRangeIndex {
  const roots: MesRangeOption[] = []
  const all: MesRangeOption[] = []
  const bySourcePath = new Map<string, MesRangeOption>()
  const collect = (option: MesRangeOption) => {
    all.push(option)
    bySourcePath.set(option.sourcePath, option)
    option.children.forEach(collect)
  }
  for (const channel of project?.channels || []) for (const device of channel.devices || []) {
    const parent = `${channel.name}.${device.name}`
    for (const group of effectiveGroups(project, device)) {
      const option = buildGroupOption(group, channel.name, device.name, parent, 0)
      roots.push(option)
      collect(option)
    }
  }
  return { roots, all, bySourcePath }
}

/**
 * Recursively collects ordinary tags below the MES task's owning main group.
 * Logical arrays (native array tags, numeric containers, and KEP-style indexed
 * sibling groups) are deliberately omitted so bulk field mapping never expands
 * an array into hundreds or thousands of scalar rows.
 */
export function collectMesScalarTagPaths(project: any, groupRef: string): MesScalarTagPath[] {
  const segments = String(groupRef || '').split('.').filter(Boolean)
  if (segments.length < 3) return []
  const channel = (project?.channels || []).find((item: any) => item.name === segments[0])
  const device = (channel?.devices || []).find((item: any) => item.name === segments[1])
  if (!device) return []
  const group = findGroup(effectiveGroups(project, device), segments.slice(2).join('.'))
  if (!group) return []

  const output: MesScalarTagPath[] = []
  const walk = (current: any, currentPath: string) => {
    if (numericContainerArray(current)) return
    for (const tag of current?.tags || []) {
      if (tag?.array || !tag?.name) continue
      output.push({ path: `${currentPath}.${tag.name}`, name: tag.name })
    }

    const siblingArrays = namedSiblingArrays(current?.subGroups || [])
    const consumed = new Set<any>()
    for (const child of current?.subGroups || []) {
      if (consumed.has(child)) continue
      const siblingArray = siblingArrays.get(child)
      if (siblingArray) {
        siblingArray.consumed.forEach((item) => consumed.add(item))
        continue
      }
      if (numericContainerArray(child)) continue
      walk(child, `${currentPath}.${child.name}`)
    }
  }

  walk(group, segments.join('.'))
  return output
}

/**
 * MES 流程绑定边界：
 * - PLC 驱动按“本组直接有点，且祖先组没有直接点”识别真实 DB 一级边界；
 * - OPC UA 没有 DB 语义，以设备下一级顶层目录作为一级边界；
 * - 一级边界的直接子组作为二级边界。二级任务绑定自己的完整路径，范围只覆盖本级和后代。
 */
export function buildMesTaskBoundaries(project: any): MesTaskBoundary[] {
  const output: MesTaskBoundary[] = []
  const walk = (group: any, parentPath: string, devicePath: string, driverId: string, depth: number,
    ancestorHasDirectTags: boolean, parentIsPrimaryBoundary: boolean) => {
    const sourcePath = join(parentPath, group.name)
    const hasDirectTags = (group.tags || []).length > 0
    const primaryBoundary = isMesTaskBoundaryGroup(driverId, group, depth, ancestorHasDirectTags, false)
    if (primaryBoundary || parentIsPrimaryBoundary) {
      output.push({ sourcePath, devicePath, label: group.name, tagCount: countMesGroupTags(group) })
    }
    for (const child of group.subGroups || [])
      walk(child, sourcePath, devicePath, driverId, depth + 1,
        ancestorHasDirectTags || hasDirectTags, primaryBoundary)
  }
  for (const channel of project?.channels || []) for (const device of channel.devices || []) {
    const devicePath = `${channel.name}.${device.name}`
    for (const group of effectiveGroups(project, device))
      walk(group, devicePath, devicePath, channel.driverId || '', 0, false, false)
  }
  return output
}

function buildArrayMembers(schema: any, prefix = ''): { path: string; label: string; dataType: string }[] {
  const result: { path: string; label: string; dataType: string }[] = []
  for (const field of schema?.fields || []) {
    const path = prefix ? `${prefix}.${field.name}` : field.name
    if (field.array) continue
    if (field.struct) result.push(...buildArrayMembers(field.struct, path))
    else result.push({ path, label: field.jsonName || field.name, dataType: field.dataType || '' })
  }
  return result
}

export function normalizeMesTaskBoundaryRefs(project: any): number {
  const tasks = project?.mesUpload?.tasks
  if (!Array.isArray(tasks) || !tasks.length) return 0
  const validPaths = new Set(buildMesRangeIndex(project).all.map((option) => option.sourcePath.toLowerCase()))
  let changed = 0
  const boundaries = buildMesTaskBoundaries(project)
  const boundaryPaths = new Set(boundaries.map((boundary) => boundary.sourcePath.toLowerCase()))
  for (const boundary of boundaries) {
    const root = boundary.sourcePath.toLowerCase()
    const exact = tasks.some((task: any) => String(task?.groupRef || '').trim().toLowerCase() === root)
    if (exact) continue
    const descendants = tasks.filter((task: any) => {
      const ref = String(task?.groupRef || '').trim().toLowerCase()
      return validPaths.has(ref) && !boundaryPaths.has(ref) && ref.startsWith(`${root}.`)
    })
    // 一个旧子流程可无损提升到正确主目录；多个子流程无法自动合并，必须原样保留给用户处理。
    if (descendants.length !== 1) continue
    descendants[0].groupRef = boundary.sourcePath
    descendants[0].deviceRef = boundary.devicePath
    changed++
  }
  return changed
}

/**
 * 删除已经失去工程树绑定目标的 MES 任务。
 * 空 groupRef 是旧版未绑定任务，继续保留；非空 groupRef 必须精确指向当前工程中的组。
 * 模板实例通过 buildMesRangeIndex 的 effectiveGroups 展开后参与判断。
 */
export function pruneOrphanMesTasks(project: any): any[] {
  const tasks = project?.mesUpload?.tasks
  if (!Array.isArray(tasks) || !tasks.length) return []
  const paths = new Map(buildMesRangeIndex(project).all.map((option) =>
    [option.sourcePath.toLowerCase(), option] as const))
  const removed: any[] = []
  for (let i = tasks.length - 1; i >= 0; i--) {
    const task = tasks[i]
    const groupRef = String(task?.groupRef || '').trim()
    if (!groupRef) continue
    const option = paths.get(groupRef.toLowerCase())
    if (!option) {
      removed.unshift(...tasks.splice(i, 1))
      continue
    }
    // 路径大小写或设备引用曾被旧版本写错时，一并规范化，避免状态归属再次漂移。
    task.groupRef = option.sourcePath
    task.deviceRef = option.devicePath
  }
  return removed
}

/**
 * 在工程树结构变化后整理 MES 引用：
 * - 任务绑定的整个目录消失：删除该任务；
 * - 任务目录仍在，但某个上报配置引用了已消失的子结构/排除点：
 *   保留任务、画布、连线、HTTP 和回写配置，仅清空该 payload 的数据范围。
 */
export function reconcileMesTaskTopology(project: any): MesTopologyReconcileResult {
  const removedTasks = pruneOrphanMesTasks(project)
  const index = buildMesRangeIndex(project)
  const bySourcePath = new Map(index.all.map((option) => [option.sourcePath.toLowerCase(), option] as const))
  let clearedPayloads = 0
  let clearedRanges = 0

  const reconcilePayload = (payload: any) => {
    if (!payload || !Array.isArray(payload.dataRanges) || !payload.dataRanges.length) return
    const mode = String(payload.mode || '').trim().toLowerCase()
    if (mode && mode !== 'dataranges') return
    const stale = payload.dataRanges.some((range: any) => {
      const option = bySourcePath.get(String(range?.sourcePath || '').trim().toLowerCase())
      if (!option) return true
      if (option.scope === 'Array') return !isValidArrayRange(range, option)
      const leaves = new Set(option.leafTags.map((tag) => tag.relativePath.toLowerCase()))
      return (range?.excludes || []).some((excluded: any) =>
        !leaves.has(String(excluded || '').trim().toLowerCase()))
    })
    if (!stale) return
    clearedPayloads++
    clearedRanges += payload.dataRanges.length
    payload.dataRanges = []
  }

  for (const task of project?.mesUpload?.tasks || []) {
    reconcilePayload(task?.payload)
    for (const node of task?.flow?.nodes || [])
      if (String(node?.type || '').toLowerCase() === 'upload') reconcilePayload(node.payload)
  }

  return { removedTasks, clearedPayloads, clearedRanges }
}

export function mesArrayLengthConfigError(range: any, rank: number): string | null {
  const mode = range.arrayLengthMode || 'Fixed'
  if (mode === 'Tag') {
    if (range.arrayLengths?.length) return '手动填写和 PLC 自动获取长度只能使用一种'
    if (range.arrayLengthTagIds?.length !== rank || range.arrayLengthTagIds.some((id: any) => !String(id || '').trim()))
      return '每个数组维度必须选择一个长度点位'
  } else if (mode !== 'Fixed') return '数组长度来源无效'
  else if (range.arrayLengthTagIds?.length) return '手动填写和 PLC 自动获取长度只能使用一种'
  return null
}

export function mesConfiguredArrayLengths(range: any, dimensions: number[], lower: number[], start: number[]): number[] {
  return range.arrayLengthMode === 'Tag'
    ? dimensions.map((length, i) => lower[i] + length - start[i])
    : range.arrayLengths?.length ? range.arrayLengths.map(Number) : dimensions
}

export function setMesArrayLengthMode(range: any, mode: 'Fixed' | 'Tag', lengths: number[]): void {
  range.arrayLengthMode = mode
  range.arrayLengths = mode === 'Fixed' ? [...lengths] : []
  range.arrayLengthTagIds = mode === 'Tag' ? lengths.map(() => '') : []
}

function isValidArrayRange(range: any, option: MesRangeOption): boolean {
  if ((range?.excludes || []).length) return false
  const dimensions = (option.array?.dimensions || []).map(Number)
  const lowerBounds = (option.array?.lowerBounds || []).length
    ? option.array.lowerBounds.map(Number)
    : dimensions.map(() => 0)
  const start = (range?.arrayStart || []).length ? range.arrayStart.map(Number) : lowerBounds
  if (mesArrayLengthConfigError(range, dimensions.length)) return false
  const lengths = mesConfiguredArrayLengths(range, dimensions, lowerBounds, start)
  if (!dimensions.length || start.length !== dimensions.length || lengths.length !== dimensions.length) return false
  if (dimensions.some((length: number, index: number) => !Number.isSafeInteger(start[index])
    || !Number.isSafeInteger(lengths[index]) || lengths[index] <= 0
    || start[index] < lowerBounds[index]
    || start[index] + lengths[index] > lowerBounds[index] + length)) return false
  return !range.arrayMemberPath || (option.arrayMembers || []).some((member) => member.path === range.arrayMemberPath)
}

export function searchMesRangeOptions(index: MesRangeIndex, query: string): MesRangeOption[] {
  const normalized = query.trim().toLowerCase()
  return normalized ? index.all.filter((option) => option.searchText.includes(normalized)) : index.roots
}

const containsPath = (parent: string, child: string) => child === parent || child.startsWith(`${parent}.`)

export function validateRangeSelection(ranges: any[], option: MesRangeOption): string | null {
  for (const selected of ranges || []) {
    if (containsPath(selected.sourcePath, option.sourcePath)) return '已被上级范围包含'
    if (containsPath(option.sourcePath, selected.sourcePath)) return '该范围已包含已选择的下级范围'
  }
  return null
}

export function validateMesRanges(ranges: any[], index: MesRangeIndex): string | null {
  for (let i = 0; i < ranges.length; i++) {
    const current = ranges[i]
    const option = index.bySourcePath.get(current.sourcePath)
    if (!option) return `数据来源不存在:${current.sourcePath}`
    const overlap = validateRangeSelection(ranges.filter((_: any, n: number) => n !== i), option)
    if (overlap) return `${current.sourcePath}:${overlap}`
    const output = String(current.outputPath || option.defaultOutputPath).trim()
    if (!output) return `${current.sourcePath}:输出路径为空`
    for (let j = 0; j < i; j++) {
      const other = String(ranges[j].outputPath || index.bySourcePath.get(ranges[j].sourcePath)?.defaultOutputPath || '').trim()
      if (containsPath(output, other) || containsPath(other, output)) return `输出路径冲突:${other} / ${output}`
    }
    const leaves = new Set(option.leafTags.map((tag) => tag.relativePath))
    for (const excluded of current.excludes || [])
      if (!leaves.has(excluded)) return `${current.sourcePath}:排除字段不存在:${excluded}`
    if (option.scope === 'Array') {
      const dimensions = (option.array?.dimensions || []).map(Number)
      const lowerBounds = (option.array?.lowerBounds || []).length
        ? option.array.lowerBounds.map(Number)
        : dimensions.map(() => 0)
      const start = (current.arrayStart || []).length ? current.arrayStart.map(Number) : lowerBounds
      const lengthError = mesArrayLengthConfigError(current, dimensions.length)
      if (lengthError) return `${current.sourcePath}:${lengthError}`
      const lengths = mesConfiguredArrayLengths(current, dimensions, lowerBounds, start)
      if (!dimensions.length || start.length !== dimensions.length || lengths.length !== dimensions.length)
        return `${current.sourcePath}:数组切片维度不一致`
      for (let dimension = 0; dimension < dimensions.length; dimension++) {
        if (!Number.isSafeInteger(start[dimension]) || !Number.isSafeInteger(lengths[dimension]) || lengths[dimension] <= 0)
          return `${current.sourcePath}:数组切片必须使用整数且数量大于零`
        if (start[dimension] < lowerBounds[dimension]
          || start[dimension] + lengths[dimension] > lowerBounds[dimension] + dimensions[dimension])
          return `${current.sourcePath}:第 ${dimension + 1} 维切片超出数组范围`
      }
      if (current.arrayMemberPath && !(option.arrayMembers || []).some((member) => member.path === current.arrayMemberPath))
        return `${current.sourcePath}:数组成员不存在:${current.arrayMemberPath}`
    }
  }
  return null
}

export function rangeSummary(ranges: any[], index: MesRangeIndex): string {
  if (!ranges?.length) return '尚未配置上报数据'
  const devices = new Set<string>()
  let tags = 0
  for (const range of ranges) {
    const option = index.bySourcePath.get(range.sourcePath)
    if (!option) continue
    devices.add(option.devicePath)
    tags += option.scope === 'Array' && (range.arrayLengths || []).length
      ? range.arrayLengths.reduce((total: number, length: number) => total * Math.max(0, Number(length)), 1)
      : Math.max(0, option.tagCount - (range.excludes?.length || 0))
  }
  return `${devices.size} PLC · ${ranges.length} 个数据范围 · 约 ${tags} 点`
}

export function migrateMesRangeRefs(root: any, oldPath: string, newPath: string): void {
  const rewrite = (value: string) => value === oldPath
    ? newPath
    : value.startsWith(`${oldPath}.`) ? newPath + value.slice(oldPath.length) : value
  const walk = (value: any) => {
    if (!value || typeof value !== 'object') return
    if (Array.isArray(value)) { value.forEach(walk); return }
    if (Array.isArray(value.dataRanges)) for (const range of value.dataRanges) {
      const next = rewrite(String(range.sourcePath || ''))
      if (next === range.sourcePath) continue
      range.sourcePath = next
      if ((range.outputPathMode || 'Auto') === 'Auto') range.outputPath = automaticOutputPath(next)
    }
    for (const child of Object.values(value)) walk(child)
  }
  walk(root)
}

const MES_REFERENCE_KEYS = new Set([
  'groupref', 'deviceref', 'tagid', 'switchtag', 'basegroup', 'counttagid',
  'copyfromtagid', 'sourcepath', 'mesflagtagid', 'plcflagtagid', 'arraylengthtagids',
])

// A business source JSON path is not a PLC path unless its source is Tag.
function isMesReferenceKey(value: any, key: string): boolean {
  if (key === 'sourcepath' && ('targetPath' in value || 'TargetPath' in value)) return false
  const source = value.source ?? value.Source
  if (key === 'tagid' && source !== undefined && String(source).toLowerCase() !== 'tag') return false
  return MES_REFERENCE_KEYS.has(key) || key === 'path' && String(value.source ?? value.Source ?? '').toLowerCase() === 'tag'
}

/**
 * 工程树路径改变后统一迁移 MES 引用。只处理明确的工程路径字段，不接触 OPC UA NodeId、
 * PLC 地址、响应字段名和用户自定义 JSON 名称。
 */
export function migrateMesReferences(root: any, oldPath: string, newPath: string): number {
  const source = String(oldPath || '').trim().replace(/\.+$/, '')
  const target = String(newPath || '').trim().replace(/\.+$/, '')
  if (!source || !target || source === target) return 0

  const rewrite = (value: string) => value === source
    ? target
    : value.startsWith(`${source}.`) || value.startsWith(`${source}[`) ? target + value.slice(source.length) : value
  let changed = 0
  const walk = (value: any) => {
    if (!value || typeof value !== 'object') return
    if (Array.isArray(value)) { value.forEach(walk); return }
    for (const key of Object.keys(value)) {
      const child = value[key]
      if (key.toLowerCase() === 'arraylengthtagids' && Array.isArray(child)) {
        value[key] = child.map((id) => {
          if (typeof id !== 'string') return id
          const next = rewrite(id)
          if (next !== id) changed++
          return next
        })
      } else if (isMesReferenceKey(value, key.toLowerCase()) && typeof child === 'string') {
        const next = rewrite(child)
        if (next !== child) {
          value[key] = next
          changed++
          if (key.toLowerCase() === 'sourcepath' && String(value.outputPathMode || 'Auto').toLowerCase() === 'auto')
            value.outputPath = automaticOutputPath(next)
        }
      } else {
        walk(child)
      }
    }
  }
  walk(root)
  return changed
}

/**
 * 恢复旧版本已保存的“移动前路径”。仅接受工程树中唯一的后缀匹配；同名候选超过一个时不修改，
 * 从而避免在多 PLC/多 DB 工程里猜错目标。
 */
export function repairMovedMesReferences(project: any): number {
  const index = buildMesRangeIndex(project)
  const groups = new Map<string, string>()
  const tags = new Map<string, string>()
  for (const option of index.all) {
    groups.set(option.sourcePath.toLowerCase(), option.sourcePath)
    for (const tag of option.leafTags) tags.set(tag.tagId.toLowerCase(), tag.tagId)
  }

  const uniqueSuffix = (value: string, candidates: Map<string, string>, minSegments: number) => {
    const exact = candidates.get(value.toLowerCase())
    if (exact) return exact
    const parts = value.split('.').filter(Boolean)
    for (let length = parts.length - 1; length >= minSegments; length--) {
      const suffix = parts.slice(-length).join('.').toLowerCase()
      const matches = [...candidates.entries()]
        .filter(([key]) => key === suffix || key.endsWith(`.${suffix}`))
        .map(([, canonical]) => canonical)
      if (matches.length === 1) return matches[0]
    }
    return ''
  }

  let changed = 0
  for (const task of project?.mesUpload?.tasks || []) {
    const oldGroupRef = String(task?.groupRef || '').trim()
    const repairedGroup = oldGroupRef ? uniqueSuffix(oldGroupRef, groups, 1) : ''
    if (repairedGroup && repairedGroup !== oldGroupRef) {
      task.groupRef = repairedGroup
      const segments = repairedGroup.split('.')
      if (segments.length >= 2) task.deviceRef = segments.slice(0, 2).join('.')
      changed++
    }
    const currentGroup = String(task?.groupRef || '').trim()

    const resolve = (value: string, candidates: Map<string, string>, minSegments: number) => {
      const exact = candidates.get(value.toLowerCase())
      if (exact) return exact
      if (oldGroupRef && currentGroup &&
          (value === oldGroupRef || value.startsWith(`${oldGroupRef}.`))) {
        const candidate = currentGroup + value.slice(oldGroupRef.length)
        const canonical = candidates.get(candidate.toLowerCase())
        if (canonical) return canonical
      }
      return uniqueSuffix(value, candidates, minSegments)
    }

    const walk = (value: any) => {
      if (!value || typeof value !== 'object') return
      if (Array.isArray(value)) { value.forEach(walk); return }
      for (const key of Object.keys(value)) {
        const child = value[key]
        const normalizedKey = key.toLowerCase()
        if (normalizedKey === 'groupref' || normalizedKey === 'deviceref') continue
        if (normalizedKey === 'arraylengthtagids' && Array.isArray(child)) {
          value[key] = child.map((id) => {
            if (typeof id !== 'string' || !id) return id
            const next = resolve(id, tags, 2)
            if (next && next !== id) { changed++; return next }
            return id
          })
        } else if (isMesReferenceKey(value, normalizedKey) && typeof child === 'string' && child) {
          const isGroup = normalizedKey === 'basegroup' || normalizedKey === 'sourcepath'
          const next = resolve(child, isGroup ? groups : tags, isGroup ? 1 : 2)
          if (next && next !== child) {
            value[key] = next
            if (normalizedKey === 'sourcepath' && String(value.outputPathMode || 'Auto').toLowerCase() === 'auto')
              value.outputPath = automaticOutputPath(next)
            changed++
          }
        } else {
          walk(child)
        }
      }
    }
    walk(task)
  }
  return changed
}

/** 套用骨架后，仅为完全空白的上报节点绑定当前 DB；已有映射和自定义报文绝不覆盖。 */
export function bindBlankTemplateUploads(nodes: any[], groupRef: string): number {
  const sourcePath = String(groupRef || '').trim()
  if (!sourcePath) return 0
  let changed = 0
  for (const node of nodes || []) {
    if (String(node?.type || '').toLowerCase() !== 'upload') continue
    const payload = node.payload ||= { mode: 'DataRanges', dataRanges: [], fields: [], structs: [], append: {} }
    if ((payload.dataRanges || []).length || (payload.fields || []).length || (payload.structs || []).length) continue
    if (String(node.http?.bodyTemplate || '').trim()) continue
    // 配置数据默认就是当前 DB；报文按字段/结构映射(平铺，和旧版一样)，字段由配置数据自动生成
    payload.mode = 'FieldMapping'
    payload.dataRanges = [{
      sourcePath,
      scope: 'Group',
      outputPath: automaticOutputPath(sourcePath),
      outputPathMode: 'Auto',
      includeMode: 'All',
      excludes: [],
      offlineMode: 'Null',
    }]
    changed++
  }
  return changed
}
