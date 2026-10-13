export type IndexedGroupName = {
  baseName: string
  index: number
  style: 'bracket' | 'underscore'
}

export type IndexedElementPath = {
  baseGroup: string
  aliasName: string
  index: number
  style: 'bracket' | 'underscore' | 'numeric'
}

/** 兼容 PLC 原生 Name[index] 和西门子 DB 导入后的 Name_index。 */
export function parseIndexedGroupName(name: string): IndexedGroupName | null {
  const bracket = String(name || '').match(/^(.+)\[(-?\d+)\]$/)
  if (bracket) return { baseName: bracket[1], index: Number(bracket[2]), style: 'bracket' }
  const underscore = String(name || '').match(/^(.+)_(-?\d+)$/)
  if (underscore) return { baseName: underscore[1], index: Number(underscore[2]), style: 'underscore' }
  return null
}

/** 根据实际完整路径识别元素组；纯数字元素必须以其父目录作为数组路径。 */
export function resolveIndexedElementPath(path: string): IndexedElementPath | null {
  const value = String(path || '').trim().replace(/\.+$/, '')
  const dot = value.lastIndexOf('.')
  const parent = dot >= 0 ? value.slice(0, dot) : ''
  const groupName = dot >= 0 ? value.slice(dot + 1) : value
  if (/^-?\d+$/.test(groupName)) {
    const index = Number(groupName)
    if (!Number.isSafeInteger(index) || !parent) return null
    const aliasName = parent.slice(parent.lastIndexOf('.') + 1)
    return { baseGroup: parent, aliasName, index, style: 'numeric' }
  }
  const parsed = parseIndexedGroupName(groupName)
  if (!parsed) return null
  return {
    baseGroup: parent ? `${parent}.${parsed.baseName}` : parsed.baseName,
    aliasName: parsed.baseName,
    index: parsed.index,
    style: parsed.style,
  }
}

/** 把旧版按绝对路径生成的数组恢复为递归结构；子结构路径相对当前元素。 */
export function nestDetectedStructs(source: any[], scopeRoot = ''): any[] {
  // Vue 配置可能是 reactive proxy，不能直接 structuredClone。
  const clone = (item: any): any => ({ ...item,
    members: (item.members || []).map((member: any) => ({ ...member })),
    ...(item.structs ? { structs: item.structs.map(clone) } : {}),
  })
  const items = source.map(clone)
  const byPath = new Map<string, any>(items.map(item => [String(item.baseGroup || '').toLowerCase(), item]))
  const ancestors = (path: string): { element: string; parsed: IndexedElementPath }[] => {
    const result: { element: string; parsed: IndexedElementPath }[] = []
    let dot = path.lastIndexOf('.')
    while (dot > 0) {
      const element = path.slice(0, dot)
      if (scopeRoot && !element.startsWith(scopeRoot + '.')) break
      const parsed = resolveIndexedElementPath(element)
      if (parsed) result.push({ element, parsed })
      dot = element.lastIndexOf('.')
    }
    return result
  }
  // 外层只有子数组时，没有直属字段可供原识别器建立外层数组。
  const missing = new Map<string, { parsed: IndexedElementPath; indices: Set<number> }>()
  for (const item of items) for (const { parsed } of ancestors(String(item.baseGroup || ''))) {
    const key = parsed.baseGroup.toLowerCase()
    if (byPath.has(key)) continue
    const group = missing.get(key) || { parsed, indices: new Set<number>() }
    group.indices.add(parsed.index)
    missing.set(key, group)
  }
  for (const [key, group] of missing) {
    const indices = [...group.indices].sort((a, b) => a - b)
    if (!indices.length || indices.some((value, index) => index > 0 && value !== indices[index - 1] + 1)) continue
    const item = { kind: 'Array', baseGroup: group.parsed.baseGroup, aliasName: group.parsed.aliasName,
      countMode: 'Enum', startIndex: indices[0], fixedCount: indices.length, members: [] }
    byPath.set(key, item)
    items.push(item)
  }
  const removed = new Set<any>()
  const sharedChildren = new Map<any, Set<string>>(items.map(item => [item,
    new Set<string>((item.structs || []).map((child: any) => String(child.baseGroup).toLowerCase()))]))
  const attached = new Map<any, Map<string, Set<number>>>()
  // 先折叠最深层，使每个父元素的完整子树参与兼容性检查。
  for (const item of [...items].sort((a, b) => String(b.baseGroup).length - String(a.baseGroup).length)) {
    for (const { element, parsed } of ancestors(String(item.baseGroup || ''))) {
      const parent = byPath.get(parsed.baseGroup.toLowerCase())
      if (!parent || parent.kind !== 'Array') continue
      const start = Number(parent.startIndex ?? 0), count = Number(parent.fixedCount ?? 0)
      if (parsed.index < start || (count > 0 && parsed.index >= start + count)) break
      const child = { ...item, baseGroup: String(item.baseGroup).slice(element.length + 1) }
      const children: any[] = parent.structs ||= []
      const existing = children.find(entry => String(entry.baseGroup).toLowerCase() === child.baseGroup.toLowerCase())
      if (existing) {
        if (JSON.stringify(existing) !== JSON.stringify(child))
          throw new Error(`数组 ${parent.baseGroup} 的 ${child.baseGroup} 在不同元素中配置不同，无法自动合并；请先统一成员、名称及数量配置。`)
      } else children.push(child)
      const childKey = child.baseGroup.toLowerCase()
      const coverage = attached.get(parent) || new Map<string, Set<number>>()
      const indices = coverage.get(childKey) || new Set<number>()
      indices.add(parsed.index)
      coverage.set(childKey, indices)
      attached.set(parent, coverage)
      removed.add(item)
      break
    }
  }
  for (const [parent, children] of attached) for (const [child, indices] of children) {
    if (!sharedChildren.get(parent)?.has(child) && Number(parent.fixedCount) > indices.size)
      throw new Error(`数组 ${parent.baseGroup} 的 ${child} 仅在部分选中元素中配置，无法自动推广到其他元素；请补齐各元素映射。`)
  }
  return items.filter(item => !removed.has(item))
}

/**
 * 重新从 PLC 组自动生成上报映射时，只更新点位结构，不覆盖用户的接口契约。
 * FieldName/AliasName 同时用于发送 JSON 和响应反向回写，丢失后会导致返回值无法匹配 PLC。
 */
export function preserveAutoDetectedMappings(previous: any, detected: any): void {
  const visit = (structs: any[], parent: string, action: (item: any, path: string) => void) => {
    for (const item of structs || []) {
      const path = parent ? `${parent}.${item.baseGroup}` : String(item.baseGroup || '')
      action(item, path)
      visit(item.structs, item.kind === 'Array' ? `${path}[]` : path, action)
    }
  }
  const arrayBases = new Set<string>()
  for (const payload of [previous, detected]) visit(payload?.structs, '', (item, path) => {
    if (item.kind === 'Array') arrayBases.add(path.toLowerCase())
  })
  const canonical = (path: string) => {
    const parts = path.split('.'), normalized: string[] = [], actual: string[] = []
    parts.forEach((part, index) => {
      const parsed = parseIndexedGroupName(part)
      const parent = normalized.join('.'), actualParent = actual.join('.')
      // 只归一已知父数组的元素段；Measures_1 这样的数组自身名称必须保持原样。
      if (index < parts.length - 1 && /^-?\d+$/.test(part) &&
          (arrayBases.has(parent.toLowerCase()) || arrayBases.has(actualParent.toLowerCase())))
        normalized[normalized.length - 1] += '[]'
      else if (index < parts.length - 1 && parsed &&
          (arrayBases.has(`${parent}.${parsed.baseName}`.toLowerCase()) || arrayBases.has(`${actualParent}.${parsed.baseName}`.toLowerCase())))
        normalized.push(`${parsed.baseName}[]`)
      else normalized.push(part)
      actual.push(part)
    })
    return normalized.join('.').toLowerCase()
  }
  const oldFields = new Map<string, any[]>()
  const addOldField = (field: any, path: string) => {
    const key = canonical(path), variants = oldFields.get(key) || []
    variants.push(field); oldFields.set(key, variants)
  }
  for (const field of previous?.fields || []) addOldField(field, String(field.tagId || ''))
  const oldStructs = new Map<string, any[]>()
  visit(previous?.structs, '', (item, path) => {
    const key = canonical(path), variants = oldStructs.get(key) || []
    variants.push(item)
    oldStructs.set(key, variants)
    for (const member of item.members || []) addOldField(member, `${path}${item.kind === 'Array' ? '[]' : ''}.${member.tagId}`)
  })
  const fieldContract = (member: any) => JSON.stringify({
    fieldName: member.fieldName || String(member.tagId || '').split('.').pop(), scale: Number(member.scale ?? 1),
    parameterCode: member.parameterCode || '', format: member.format || '',
  })
  const contract = (item: any) => JSON.stringify({ aliasName: item.aliasName || '', asDictionary: item.asDictionary === true,
    members: (item.members || []).map((member: any) => [String(member.tagId || '').toLowerCase(), fieldContract(member)]),
  })
  const copies: { target: any; source: any }[] = [], aliases: { target: any; source: any }[] = []
  const preserveField = (target: any, variants: any[] | undefined, path: string) => {
    if (!variants?.length) return
    const expected = fieldContract(variants[0])
    if (variants.some(entry => fieldContract(entry) !== expected))
      throw new Error(`点位 ${path} 在不同元素中配置不同，无法自动保留名称和成员转换配置。`)
    copies.push({ target, source: variants[0] })
  }
  for (const field of detected?.fields || []) preserveField(field, oldFields.get(canonical(String(field.tagId || ''))), field.tagId)
  visit(detected?.structs, '', (item, path) => {
    const variants = oldStructs.get(canonical(path))
    const old = variants?.[0]
    if (old && variants!.some(entry => contract(entry) !== contract(old)))
      throw new Error(`结构 ${path} 在旧映射的不同元素中配置不同，无法自动保留；请先统一名称和成员转换配置。`)
    if (old) aliases.push({ target: item, source: old })
    const oldMembers = new Map<string, any>((old?.members || []).map((member: any) =>
      [String(member?.tagId || '').toLowerCase(), member]))
    for (const member of item.members || []) {
      const oldMember = oldMembers.get(String(member?.tagId || '').toLowerCase())
      const memberPath = `${path}${item.kind === 'Array' ? '[]' : ''}.${member.tagId}`
      preserveField(member, oldMember ? [oldMember] : oldFields.get(canonical(memberPath)), memberPath)
    }
  })
  // 全部冲突校验成功后再修改，调用者不会得到部分覆盖的结果。
  for (const { target, source } of aliases) {
    if (typeof source.aliasName === 'string' && source.aliasName.trim()) target.aliasName = source.aliasName
    if (typeof source.asDictionary === 'boolean') target.asDictionary = source.asDictionary
  }
  for (const { target, source } of copies) {
    for (const key of ['fieldName', 'parameterCode', 'format'])
      if (typeof source[key] === 'string') target[key] = source[key]
    if (Number.isFinite(Number(source.scale))) target.scale = Number(source.scale)
  }
}

function memberSignature(item: any): string {
  return JSON.stringify((item?.members || []).map((member: any) => ({
    tagId: String(member?.tagId || ''),
    fieldName: String(member?.fieldName || ''),
    scale: Number(member?.scale ?? 1),
  })))
}

/**
 * 修复旧版自动识别结果：旧版会把 CellData_0…CellData_N 各自保存为 List，
 * 这里仅迁移满足旧版自动生成特征且下标连续、成员完全一致的配置，避免改动用户手工结构。
 */
export function upgradeLegacyImportedStructs(payload: any): number {
  const structs: any[] = Array.isArray(payload?.structs) ? payload.structs : []
  const candidates = new Map<string, { position: number; item: any; elementName: string; parsed: IndexedElementPath }[]>()
  structs.forEach((item, position) => {
    if (item?.kind !== 'List' || item?.countMode !== 'Fixed' || item?.takeIndex === true) return
    const baseGroup = String(item?.baseGroup || '')
    const parsed = resolveIndexedElementPath(baseGroup)
    if (!parsed) return
    if (Number(item?.fixedCount || 0) !== (item?.members || []).length || !(item?.members || []).length) return
    const elementName = baseGroup.slice(baseGroup.lastIndexOf('.') + 1)
    const list = candidates.get(parsed.baseGroup) || []
    list.push({ position, item, elementName, parsed })
    candidates.set(parsed.baseGroup, list)
  })

  let upgraded = 0
  const replacements = new Map<any, any>()
  const removed = new Set<any>()
  for (const group of candidates.values()) {
    if (group.length < 2) continue
    group.sort((a, b) => a.parsed.index - b.parsed.index)
    if (group.some((entry, index) => index > 0 && entry.parsed.index !== group[index - 1].parsed.index + 1)) continue
    const signature = memberSignature(group[0].item)
    if (group.some((entry) => memberSignature(entry.item) !== signature)) continue
    // JSON 名称只是输出契约，不能反向参与 PLC 路径识别。
    // 旧配置若把任一元素的自动名称改成同一个后台字段名（如 Structures），迁移时必须保留。
    const customAliases = [...new Set(group
      .filter((entry) => String(entry.item?.aliasName || '') !== entry.elementName)
      .map((entry) => String(entry.item?.aliasName || '').trim())
      .filter(Boolean))]
    if (customAliases.length > 1) continue

    const first = group[0]
    const replacement = {
      ...first.item,
      kind: 'Array',
      baseGroup: first.parsed.baseGroup,
      aliasName: customAliases[0] || first.parsed.aliasName,
      countMode: 'Enum',
      startIndex: first.parsed.index,
      fixedCount: group.length,
      members: (first.item.members || []).map((member: any) => ({ ...member })),
    }
    const firstByPosition = group.reduce((best, entry) => entry.position < best.position ? entry : best)
    replacements.set(firstByPosition.item, replacement)
    for (const entry of group) if (entry.item !== firstByPosition.item) removed.add(entry.item)
    upgraded += group.length
  }
  if (upgraded) payload.structs = structs.flatMap((item) => {
    if (replacements.has(item)) return [replacements.get(item)]
    return removed.has(item) ? [] : [item]
  })

  const fields: any[] = Array.isArray(payload?.fields) ? payload.fields : []
  const fieldCandidates = new Map<string, {
    parsed: IndexedElementPath
    elements: Map<number, { position: number; member: string; field: any }[]>
  }>()
  fields.forEach((field, position) => {
    const tagId = String(field?.tagId || '')
    const dot = tagId.lastIndexOf('.')
    if (dot <= 0) return
    const parent = tagId.slice(0, dot)
    const member = tagId.slice(dot + 1)
    const parsed = resolveIndexedElementPath(parent)
    if (!parsed || !member) return
    const candidate = fieldCandidates.get(parsed.baseGroup) || { parsed, elements: new Map() }
    const entries = candidate.elements.get(parsed.index) || []
    entries.push({ position, member, field })
    candidate.elements.set(parsed.index, entries)
    fieldCandidates.set(parsed.baseGroup, candidate)
  })

  const removedFields = new Set<any>()
  const addedStructs: any[] = []
  for (const [baseGroup, candidate] of fieldCandidates) {
    if (payload.structs?.some((item: any) => String(item?.baseGroup || '').toLowerCase() === baseGroup.toLowerCase())) continue
    const indices = [...candidate.elements.keys()].sort((left, right) => left - right)
    if (indices.length < 2 || indices.some((value, index) => index > 0 && value !== indices[index - 1] + 1)) continue
    const firstEntries = [...(candidate.elements.get(indices[0]) || [])].sort((left, right) => left.position - right.position)
    if (!firstEntries.length || new Set(firstEntries.map((entry) => entry.member.toLowerCase())).size !== firstEntries.length) continue

    const signature = (entry: { member: string; field: any }) => JSON.stringify({
      member: entry.member.toLowerCase(),
      fieldName: String(entry.field?.fieldName || ''),
      parameterCode: String(entry.field?.parameterCode || ''),
      scale: Number(entry.field?.scale ?? 1),
      format: String(entry.field?.format || ''),
    })
    const expected = new Map(firstEntries.map((entry) => [entry.member.toLowerCase(), signature(entry)]))
    let compatible = true
    for (const index of indices) {
      const entries = candidate.elements.get(index) || []
      if (entries.length !== firstEntries.length || entries.some((entry) => expected.get(entry.member.toLowerCase()) !== signature(entry))) {
        compatible = false
        break
      }
    }
    if (!compatible) continue

    addedStructs.push({
      kind: 'Array',
      baseGroup,
      aliasName: candidate.parsed.aliasName,
      countMode: 'Enum',
      startIndex: indices[0],
      fixedCount: indices.length,
      members: firstEntries.map(({ member, field }) => ({ ...field, tagId: member })),
    })
    for (const entries of candidate.elements.values())
      for (const entry of entries) removedFields.add(entry.field)
    upgraded += indices.length
  }
  if (removedFields.size) {
    payload.fields = fields.filter((field) => !removedFields.has(field))
    payload.structs = [...(payload.structs || []), ...addedStructs]
  }
  return upgraded
}
