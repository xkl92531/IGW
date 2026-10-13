import { createCompactId } from './runtimeId'

// 与现有点位导入的名称规则一致；保留已有名称拼写，避免改变 MES 等引用的路径。
const nameKey = (name: unknown) => String(name ?? '').trim().toLocaleLowerCase()
const clone = <T,>(value: T): T => JSON.parse(JSON.stringify(value))

function normalizeImportFields(tag: any) {
  // ProjectStore.Options 省略 null。局部 upsert 是完整点位文档：缺少可空属性会反序列化为 null，
  // 既能清除旧布局，也不会在保存重开后反复补入 null 产生伪差分。
  for (const field of ['array', 'stringLength', 'metadata', 'description'])
    if (tag[field] == null) delete tag[field]
  return tag
}

function mergeArrayLayout(before: any, incoming: any) {
  if (!incoming) return null
  const result = clone(incoming)
  const preserveStructSettings = (previous: any, next: any) => {
    if (!previous || !next) return
    if (previous.id) next.id = previous.id
    const fields = new Map<string, any>((previous.fields || []).map((field: any) => [nameKey(field.name), field]))
    for (const field of next.fields || []) {
      const old = fields.get(nameKey(field.name))
      if (!old) continue
      // JSON 别名由现场配置，不是 DB 的布局声明；不能被解析器的 null 默认值重置。
      if (Object.prototype.hasOwnProperty.call(old, 'jsonName')) field.jsonName = old.jsonName
      preserveStructSettings(old.struct, field.struct)
      preserveStructSettings(old.array?.struct, field.array?.struct)
    }
  }
  preserveStructSettings(before?.struct, result.struct)
  return result
}

export function findSiemensDbImportGroup(target: any, kind: 'device' | 'group', dbName: string): any | undefined {
  if (kind === 'group' && nameKey(target.name) === nameKey(dbName)) return target
  return (kind === 'device' ? target.groups : target.subGroups)?.find((group: any) => nameKey(group.name) === nameKey(dbName))
}

/** 仅供 Siemens DB 解析结果使用，不改变 JSON/CSV/OPC UA 导入语义。先构造合并结果，校验失败不改原树。 */
export function mergeSiemensDbImport(target: any, kind: 'device' | 'group', dbName: string, root: any) {
  let added = 0
  let updated = 0
  const existing = findSiemensDbImportGroup(target, kind, dbName)
  if (existing !== target && (target.tags || []).some((tag: any) => nameKey(tag.name) === nameKey(dbName)))
    throw new Error(`路径冲突:${dbName}`)

  const mergeGroup = (before: any, incoming: any, path: string, isRoot = false): any => {
    const result = before
      ? { ...before, tags: [...(before.tags || [])], subGroups: [...(before.subGroups || [])] }
      : { id: createCompactId(), name: isRoot ? dbName : incoming.name, description: incoming.description ?? null, tags: [], subGroups: [] }
    // root 是解析器的虚拟容器，没有 DB 本身的说明/现场设置。
    if (!isRoot && incoming.description != null) result.description = incoming.description
    const tagIndexes = new Map<string, number>(result.tags.map((tag: any, index: number) => [nameKey(tag.name), index]))
    const groupIndexes = new Map<string, number>(result.subGroups.map((group: any, index: number) => [nameKey(group.name), index]))
    const importedNames = new Set<string>()
    const checkName = (name: unknown) => {
      const key = nameKey(name)
      if (!key || importedNames.has(key)) throw new Error(`路径冲突:${path}.${String(name ?? '')}`)
      importedNames.add(key)
      return key
    }
    for (const tag of incoming.tags || []) {
      const key = checkName(tag.name)
      if (groupIndexes.has(key)) throw new Error(`路径冲突:${path}.${tag.name}`)
      const index = tagIndexes.get(key)
      if (index === undefined) {
        result.tags.push(normalizeImportFields({ ...clone(tag), id: createCompactId() }))
        added++
      } else {
        const previous = result.tags[index]
        // DB 源能定义的字段白名单。Access/ScanRate 等只是解析器默认值，不能覆盖现场配置。
        const next = { ...previous,
          address: tag.address, dataType: tag.dataType, description: tag.description,
          isArray: !!tag.isArray, array: mergeArrayLayout(previous.array, tag.array), stringLength: tag.stringLength ?? null,
        }
        const metadata = { ...(previous.metadata || {}) }
        for (const field of ['array.sourceFormat', 'array.sourceAddress']) {
          if (tag.metadata?.[field] !== undefined) metadata[field] = tag.metadata[field]
          else delete metadata[field]
        }
        next.metadata = Object.keys(metadata).length ? metadata : previous.metadata == null ? previous.metadata : null
        result.tags[index] = normalizeImportFields(next)
        updated++
      }
    }
    for (const group of incoming.subGroups || []) {
      const key = checkName(group.name)
      if (tagIndexes.has(key)) throw new Error(`路径冲突:${path}.${group.name}`)
      const index = groupIndexes.get(key)
      const merged = mergeGroup(index === undefined ? undefined : result.subGroups[index], group, `${path}.${group.name}`)
      if (index === undefined) result.subGroups.push(merged)
      else result.subGroups[index] = merged
    }
    return result
  }

  const merged = mergeGroup(existing, root, dbName, true)
  if (!added && !updated) return { group: existing, added, updated }
  if (existing) Object.assign(existing, merged)
  else (kind === 'device' ? (target.groups ||= []) : (target.subGroups ||= [])).push(merged)
  return { group: existing || merged, added, updated }
}
