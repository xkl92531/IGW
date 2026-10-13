import type { OpcUaBrowsedTag } from '../api'
import type { ImportedTag } from './kepTagImport'
import { dataTypeKey } from './siemensDataTypes.ts'

export interface OpcUaImportAliases {
  rootGroupName: string
  folderNames: Record<string, string>
  tagNames: Record<string, string>
}

export function opcUaDataTypeName(value: string | number): string {
  const key = dataTypeKey(value)
  const names: Record<string, string> = { bytearray: 'byteArray', wstring: 'wString', lbcd: 'lBcd', s5time: 's5Time', datetime: 'dateTime' }
  return names[key] || (/^\d+$/.test(key) ? 'double' : key || 'double')
}

/** Older browse endpoints serialized nested enum values as numbers. */
function normalizeArrayTypes(source: any): any {
  if (!source) return null
  const result = JSON.parse(JSON.stringify(source))
  const visit = (value: any) => {
    if (!value || typeof value !== 'object') return
    if (value.elementType != null) value.elementType = opcUaDataTypeName(value.elementType)
    if (value.dataType != null) value.dataType = opcUaDataTypeName(value.dataType)
    for (const child of Object.values(value)) visit(child)
  }
  visit(result)
  return result
}

export function sourcePathSegments(tag: Pick<OpcUaBrowsedTag, 'path' | 'pathSegments'>): string[] {
  if (Array.isArray(tag.pathSegments) && tag.pathSegments.length)
    return tag.pathSegments.map((part) => String(part).trim()).filter(Boolean)
  return String(tag.path || '').split('.').map((part) => part.trim()).filter(Boolean)
}

export function defaultOpcUaGroupName(segment: string): string {
  return String(segment || '').replace(/\[\s*(-?\d+)\s*\]/g, '_$1').trim()
}

export function structuredOpcUaTagPath(tag: Pick<OpcUaBrowsedTag, 'name' | 'path' | 'pathSegments'>): {
  folderSegments: string[]
  tagName: string
} {
  const folderSegments = sourcePathSegments(tag)
  const nameSegments = String(tag.name || '').split('.').map((part) => part.trim()).filter(Boolean)
  if (nameSegments.length <= 1)
    return { folderSegments, tagName: nameSegments[0] || String(tag.name || '').trim() }
  return {
    folderSegments: [...folderSegments, ...nameSegments.slice(0, -1)],
    tagName: nameSegments[nameSegments.length - 1],
  }
}

export function opcUaDeviceRoots(tags: Array<Pick<OpcUaBrowsedTag, 'name' | 'path' | 'pathSegments'>>): string[] {
  const roots: string[] = []
  const seen = new Set<string>()
  for (const tag of tags || []) {
    const root = structuredOpcUaTagPath(tag).folderSegments[0]?.trim()
    const key = root?.toLocaleLowerCase()
    if (!root || !key || seen.has(key)) continue
    seen.add(key)
    roots.push(root)
  }
  return roots
}

export const folderAliasKey = (segments: string[]) => segments.join('\u0000')

export function opcUaTagAliasKey(tag: Pick<OpcUaBrowsedTag, 'address' | 'name' | 'path' | 'pathSegments'>): string {
  const structuredPath = structuredOpcUaTagPath(tag)
  return [String(tag.address || ''), ...structuredPath.folderSegments, structuredPath.tagName].join('\u0000')
}

export function opcUaTagAccess(value: string | number | undefined): string {
  if (typeof value === 'number') return value === 2 ? 'readWrite' : value === 1 ? 'write' : 'read'
  const key = String(value || '').trim().toLowerCase().replace(/[\s_-]+/g, '')
  if (key === 'readwrite' || key === 'rw' || key === 'r/w') return 'readWrite'
  if (key === 'write' || key === 'writeonly' || key === 'w') return 'write'
  return 'read'
}

export function toImportedOpcUaTag(tag: OpcUaBrowsedTag, aliases: OpcUaImportAliases, sourceDeviceRoot = ''): ImportedTag {
  const structuredPath = structuredOpcUaTagPath(tag)
  const sourceSegments = structuredPath.folderSegments
  const prefix: string[] = []
  const targetSegments: string[] = []
  for (let index = 0; index < sourceSegments.length; index++) {
    const segment = sourceSegments[index]
    prefix.push(segment)
    if (index === 0 && sourceDeviceRoot && segment.toLocaleLowerCase() === sourceDeviceRoot.toLocaleLowerCase())
      continue
    targetSegments.push(String(aliases.folderNames[folderAliasKey(prefix)] ?? defaultOpcUaGroupName(segment)).trim())
  }
  const root = String(aliases.rootGroupName || '').trim()
  return {
    groupPath: root ? [root, ...targetSegments] : targetSegments,
    name: String(aliases.tagNames[opcUaTagAliasKey(tag)] ?? aliases.tagNames[tag.address] ?? structuredPath.tagName).trim(),
    address: tag.address,
    dataType: opcUaDataTypeName(tag.dataType),
    isArray: !!tag.isArray,
    array: normalizeArrayTypes(tag.array),
    stringLength: tag.stringLength ?? null,
    metadata: tag.metadata ? { ...tag.metadata } : null,
    access: opcUaTagAccess(tag.access),
    scanRateMs: 100,
    description: '',
    row: 0,
  }
}

const normalizedName = (value: unknown) => String(value ?? '').trim().toLocaleLowerCase()
const cloneTarget = <T>(value: T): T => JSON.parse(JSON.stringify(value))

export function flattenLegacyOpcUaDeviceRoot(target: any, sourceDeviceRoot: string): {
  target: any
  changed: boolean
  error?: string
} {
  const result = cloneTarget(target || { tags: [], groups: [] })
  const groupKey = 'groups' in result ? 'groups' : 'subGroups'
  const groups = (result[groupKey] ||= []) as any[]
  const rootIndex = groups.findIndex((group) => normalizedName(group?.name) === normalizedName(sourceDeviceRoot))
  if (rootIndex < 0) return { target: result, changed: false }

  const wrapper = groups[rootIndex]
  const siblings = groups.filter((_, index) => index !== rootIndex)
  const targetTags = (result.tags ||= []) as any[]
  const wrapperTags = (wrapper.tags || []) as any[]
  const wrapperGroups = (wrapper.subGroups || []) as any[]
  const siblingGroupNames = new Set(siblings.map((group) => normalizedName(group?.name)))
  const targetTagNames = new Set(targetTags.map((tag) => normalizedName(tag?.name)))
  const wrapperTagNames = new Set<string>()
  const wrapperGroupNames = new Set<string>()

  for (const group of wrapperGroups) {
    const key = normalizedName(group?.name)
    if (!key) continue
    if (wrapperGroupNames.has(key) || siblingGroupNames.has(key) || targetTagNames.has(key))
      return { target: result, changed: false, error: `无法展开设备根：目标中已存在同名结构「${group.name}」` }
    wrapperGroupNames.add(key)
  }
  for (const tag of wrapperTags) {
    const key = normalizedName(tag?.name)
    if (!key) continue
    if (wrapperTagNames.has(key) || targetTagNames.has(key) || siblingGroupNames.has(key) || wrapperGroupNames.has(key))
      return { target: result, changed: false, error: `无法展开设备根：目标中已存在同名点位「${tag.name}」` }
    wrapperTagNames.add(key)
  }

  result[groupKey] = [...siblings, ...wrapperGroups]
  result.tags = [...targetTags, ...wrapperTags]
  return { target: result, changed: true }
}

export function validateOpcUaAliases(aliases: OpcUaImportAliases): string[] {
  const errors: string[] = []
  const root = String(aliases.rootGroupName || '').trim()
  if (root.includes('.')) errors.push('导入根组名称不能包含“.”')
  for (const value of Object.values(aliases.folderNames)) {
    const name = String(value ?? '').trim()
    if (!name) errors.push('结构目标名称不能为空')
    else if (name.includes('.')) errors.push('结构目标名称不能包含“.”')
  }
  for (const value of Object.values(aliases.tagNames)) {
    const name = String(value ?? '').trim()
    if (!name) errors.push('点位目标名称不能为空')
    else if (name.includes('.')) errors.push('点位目标名称不能包含“.”')
  }
  return [...new Set(errors)]
}

export function replaceImportedTarget(target: any, importedTarget: any): void {
  target.tags = importedTarget?.tags || []
  if ('groups' in target) target.groups = importedTarget?.groups || []
  else target.subGroups = importedTarget?.subGroups || []
}
