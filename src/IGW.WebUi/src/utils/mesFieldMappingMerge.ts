/**
 * 「配置数据」生成的字段/结构映射合并进上报节点，操作方式和旧版字段映射一致：
 * - 已有的字段(按点位)、结构体(按基准组)原样保留，手工改过的字段名、参数码、成员、别名都不动；
 * - 新选进来的点和结构体追加在后面；
 * - 上一次选中过、这次取消了的数据，对应的字段和结构体移除；
 * - 不在任何数据范围里的字段和结构体(手工加的)一律保留。
 * 数组如果在配置数据里单独选了起止，按这次的起始和数量更新。
 * 结构体里可以套子结构体(structs)；以前生成的拍平结构体(成员写成 Inner.X、没有子结构体)
 * 这次生成出嵌套结构时，换成嵌套的，保留改过的 JSON 字段名。
 */
import { preserveAutoDetectedMappings } from './mesStructMapping'

export interface GeneratedMapping { fields: any[]; structs: any[] }

const lower = (value: unknown) => String(value || '').trim().toLowerCase()
function inside(path: string, ranges: any[]) {
  const target = lower(path)
  return ranges.some((range) => {
    const root = lower(range?.sourcePath)
    return root && (target === root || target.startsWith(root + '.') || target.startsWith(root + '['))
  })
}

export function mergeGeneratedMapping(payload: any, generated: GeneratedMapping, previousRanges: any[], ranges: any[]) {
  const fieldIds = new Set(generated.fields.map((field) => lower(field.tagId)))
  const structBases = new Set(generated.structs.map((item) => lower(item.baseGroup)))
  const keepField = (field: any) => fieldIds.has(lower(field.tagId)) || !inside(field.tagId, previousRanges)
  const keepStruct = (item: any) => structBases.has(lower(item.baseGroup)) || !inside(item.baseGroup, previousRanges)

  const fields = (payload.fields || []).filter(keepField)
  const haveFields = new Set(fields.map((field: any) => lower(field.tagId)))
  for (const field of generated.fields) if (!haveFields.has(lower(field.tagId))) fields.push({ ...field })

  const structs = (payload.structs || []).filter(keepStruct)
  const byBase = new Map(structs.map((item: any) => [lower(item.baseGroup), item]))
  const updates: { existing: any; replacement: any }[] = []
  for (const item of generated.structs) {
    const existing: any = byBase.get(lower(item.baseGroup))
    if (!existing || (!(existing.structs?.length) && item.structs?.length)) {
      const replacement = structuredClone(item)
      preserveAutoDetectedMappings(payload, { structs: [replacement] })
      if (existing) {
        if (existing.kind === 'Array' && replacement.kind === 'Array')
          for (const key of ['countMode', 'startIndex', 'fixedCount', 'countTagId', 'takeIndex'])
            if (key in existing) replacement[key] = existing[key]
        const selected = ranges.find(range => lower(range.sourcePath) === lower(item.baseGroup) && range.arrayLengths?.length)
        if (selected && replacement.kind === 'Array') { replacement.startIndex = item.startIndex; replacement.fixedCount = item.fixedCount }
        updates.push({ existing, replacement })
      } else { structs.push(replacement); byBase.set(lower(item.baseGroup), replacement) }
      continue
    }
    // 配置数据里单独选了这个数组并设了起止：以这次为准
    const selected = ranges.find((range) => lower(range.sourcePath) === lower(item.baseGroup) && range.arrayLengths?.length)
    if (selected && existing.kind === 'Array') updates.push({ existing, replacement: { startIndex: item.startIndex, fixedCount: item.fixedCount } })
  }
  // 子数组契约全部验证成功后，才更新原有 reactive 结构，冲突不会造成半次迁移。
  for (const { existing, replacement } of updates) Object.assign(existing, replacement)
  payload.fields = fields
  payload.structs = structs
}

export class ItemIdMappingError extends Error {
  constructor(message: string, readonly path: string) { super(message) }
}

export function itemIdDictionaryConflict(payload: any): string | undefined {
  const roots = [
    ...Object.keys(payload?.append || {}),
    ...(payload?.fixedValues || []).map((field: any) => field?.name),
    ...(payload?.structs || []).map((item: any) => item?.aliasName),
  ]
  const root = roots.find(name => lower(name) === 'parameterdic')
  if (root !== undefined) return String(root)
  return (payload?.extraFields || []).find((field: any) => lower(String(field?.name || '').split('.')[0]) === 'parameterdic')?.name
}

// 与后端 IsCoveredByStruct 一致：对象成员、下标元素和下划线枚举元素都由结构体接管。
function covers(base: string, path: string): boolean {
  const root = lower(base), target = lower(path)
  return !!root && (target === root || target.startsWith(root + '.') || target.startsWith(root + '[') || target.startsWith(root + '_'))
}

/** 仅用于切换 ItemId：不迁移旧结构、不修改输入；冲突时整次转换失败。 */
export function createItemIdMapping(payload: any, generated: GeneratedMapping, scopeGroup: string): GeneratedMapping {
  const retained: any[] = (payload.structs || []).filter((item: any) => inside(item.baseGroup, [{ sourcePath: scopeGroup }]))
  const bases = retained.map(item => String(item.baseGroup))
  const covered = (path: string) => bases.some(base => covers(base, path))
  const containsRetained = (path: string) => bases.some(base => covers(path, base))
  const overlap = (path: string): never => { throw new ItemIdMappingError('结构映射在 {path} 处重叠，无法安全切换为键值对。', path) }

  // 只能裁剪新生成的对象树；不能裁剪共享元素定义的数组，也不能改旧结构来消除重叠。
  function prune(item: any, path: string): any | null {
    if (covered(path)) return null
    if (!containsRetained(path)) return structuredClone(item)
    if (lower(item.kind) !== 'object') return overlap(path)
    const copy = structuredClone(item)
    copy.members = (copy.members || []).filter((member: any) => {
      const memberPath = `${path}.${member.tagId}`
      if (covered(memberPath)) return false
      if (containsRetained(memberPath)) return overlap(memberPath)
      return true
    })
    copy.structs = (copy.structs || []).flatMap((child: any) => {
      const kept = prune(child, `${path}.${child.baseGroup}`)
      return kept ? [kept] : []
    })
    return copy.members.length || copy.structs.length ? copy : null
  }

  const structs = structuredClone(retained)
  for (const item of generated.structs) {
    const kept = prune(item, String(item.baseGroup))
    if (kept) structs.push(kept)
  }
  const available = generated.fields.filter(field => {
    if (covered(field.tagId)) return false
    if (containsRetained(field.tagId)) return overlap(field.tagId)
    return true
  })
  const ids = new Set(available.map(field => lower(field.tagId)))
  const byId = new Map<string, any>()
  for (const field of payload.fields || []) {
    const id = lower(field.tagId)
    if (!ids.has(id)) continue
    const first = byId.get(id)
    if (!first) byId.set(id, structuredClone(field))
    else if (Number(first.scale ?? 1) !== Number(field.scale ?? 1) || (first.format ?? '') !== (field.format ?? ''))
      throw new ItemIdMappingError('点位 {path} 存在不同转换配置，无法切换为键值对。', field.tagId)
  }
  // 生成结果只是默认映射，不能覆盖已保留字段的转换配置或别名。
  for (const field of available) if (!byId.has(lower(field.tagId))) byId.set(lower(field.tagId), structuredClone(field))
  const fields = [...byId.values()]
  const conflict = itemIdDictionaryConflict({ ...payload, structs })
  if (conflict) throw new ItemIdMappingError('外层字段 {path} 与 parameterDic 字典冲突。', conflict)
  const keys = new Set<string>()
  for (const item of structs) {
    if (keys.has(item.aliasName)) overlap(item.baseGroup)
    keys.add(item.aliasName)
  }
  return { fields, structs }
}
