import type { ArrayDef } from './arrayTags'

export interface ImportedTag {
  groupPath: string[]
  name: string
  address: string
  dataType: string
  isArray: boolean
  array?: ArrayDef | null
  stringLength?: number | null
  metadata?: Record<string, string> | null
  access: string
  scanRateMs: number
  description: string
  row: number
  /** 同路径同地址重新浏览时只刷新服务器权限，不覆盖用户的扫描周期和说明。 */
  refreshAccessOnly?: boolean
}

export interface TagImportError {
  row: number
  message: string
}

export interface KepCsvParseResult {
  tags: ImportedTag[]
  errors: TagImportError[]
}

export interface TagImportConflict {
  path: string
  row: number
  kind: 'tag' | 'group' | 'duplicate'
  message: string
}

export interface TagImportPreview {
  tags: ImportedTag[]
  conflicts: TagImportConflict[]
  groupsToCreate: number
  skipped: number
}

export interface TagImportResult<T = any> {
  target: T
  imported: number
  overwritten: number
}

type TypeMapping = { dataType: string; isArray: boolean }

const TYPE_MAP: Record<string, TypeMapping> = {
  bool: { dataType: 'bool', isArray: false },
  boolean: { dataType: 'bool', isArray: false },
  char: { dataType: 'char', isArray: false },
  byte: { dataType: 'byte', isArray: false },
  short: { dataType: 'int16', isArray: false },
  int16: { dataType: 'int16', isArray: false },
  word: { dataType: 'uint16', isArray: false },
  uint16: { dataType: 'uint16', isArray: false },
  long: { dataType: 'int32', isArray: false },
  int32: { dataType: 'int32', isArray: false },
  dword: { dataType: 'uint32', isArray: false },
  uint32: { dataType: 'uint32', isArray: false },
  int64: { dataType: 'int64', isArray: false },
  uint64: { dataType: 'uint64', isArray: false },
  float: { dataType: 'float', isArray: false },
  real: { dataType: 'float', isArray: false },
  double: { dataType: 'double', isArray: false },
  string: { dataType: 'string', isArray: false },
  wstring: { dataType: 'wString', isArray: false },
  'string array': { dataType: 'string', isArray: true },
}

function parseCsvRecords(text: string): { rows: string[][]; error?: TagImportError } {
  const rows: string[][] = []
  let row: string[] = []
  let field = ''
  let quoted = false
  let rowNumber = 1

  const endsField = (ch: string | undefined) => ch === undefined || ch === ',' || ch === '\r' || ch === '\n'
  const finishRow = () => {
    row.push(field)
    if (row.some((value) => value.trim().length > 0)) rows.push(row)
    row = []
    field = ''
    rowNumber++
  }

  for (let i = 0; i < text.length; i++) {
    const ch = text[i]
    if (quoted) {
      if (ch !== '"') {
        field += ch
        continue
      }

      const next = text[i + 1]
      const afterNext = text[i + 2]
      if (next === '"') {
        if (endsField(afterNext)) {
          // KEP NodeId:内部双引号未转义，字段末尾形成 literal quote + closing quote。
          field += '"'
          quoted = false
          i++
        } else {
          // 标准 CSV 的双引号转义。
          field += '"'
          i++
        }
      } else if (endsField(next)) {
        quoted = false
      } else {
        // KEP NodeId 中间的单个双引号是地址内容，不是字段结束。
        field += '"'
      }
      continue
    }

    if (ch === '"' && field.length === 0) {
      quoted = true
    } else if (ch === ',') {
      row.push(field)
      field = ''
    } else if (ch === '\n') {
      finishRow()
    } else if (ch === '\r') {
      if (text[i + 1] === '\n') i++
      finishRow()
    } else {
      field += ch
    }
  }

  if (quoted) return { rows, error: { row: rowNumber, message: 'CSV 引号未闭合' } }
  if (row.length > 0 || field.length > 0) finishRow()
  return { rows }
}

const headerKey = (value: string) => value.replace(/^\uFEFF/, '').trim().toLowerCase().replace(/[^a-z0-9]/g, '')
const typeKey = (value: string) => value.trim().toLowerCase().replace(/\s+/g, ' ')

function mapAccess(value: string): string {
  const key = value.trim().toLowerCase().replace(/\s+/g, '')
  if (key === 'r/w' || key === 'rw' || key === 'readwrite') return 'readWrite'
  if (key === 'w' || key === 'write' || key === 'writeonly') return 'write'
  return 'read'
}

export function parseKepTagCsv(text: string): KepCsvParseResult {
  const parsed = parseCsvRecords(text)
  if (parsed.error) return { tags: [], errors: [parsed.error] }
  if (parsed.rows.length === 0) return { tags: [], errors: [{ row: 1, message: 'CSV 为空' }] }

  const headers = parsed.rows[0].map(headerKey)
  const indexOf = (...keys: string[]) => headers.findIndex((header) => keys.includes(header))
  const nameIndex = indexOf('tagname', 'name')
  const addressIndex = indexOf('address')
  const typeIndex = indexOf('datatype')
  const accessIndex = indexOf('clientaccess', 'access')
  const scanIndex = indexOf('scanrate', 'scanratems')
  const descriptionIndex = indexOf('description')
  const missing = [
    nameIndex < 0 ? 'Tag Name/Name' : '',
    addressIndex < 0 ? 'Address' : '',
    typeIndex < 0 ? 'Data Type/DataType' : '',
  ].filter(Boolean)
  if (missing.length > 0)
    return { tags: [], errors: [{ row: 1, message: `缺少必要列:${missing.join('、')}` }] }

  const tags: ImportedTag[] = []
  const errors: TagImportError[] = []
  for (let i = 1; i < parsed.rows.length; i++) {
    const cells = parsed.rows[i]
    const rowNumber = i + 1
    const fullName = (cells[nameIndex] ?? '').trim()
    if (!fullName) continue
    const parts = fullName.split('.').map((part) => part.trim())
    if (parts.some((part) => !part)) {
      errors.push({ row: rowNumber, message: `标记路径无效:${fullName}` })
      continue
    }

    const address = (cells[addressIndex] ?? '').trim()
    if (!address) {
      errors.push({ row: rowNumber, message: `标记 ${fullName} 缺少 Address` })
      continue
    }

    const rawType = (cells[typeIndex] ?? '').trim()
    const mapping = TYPE_MAP[typeKey(rawType)]
    if (!mapping) {
      errors.push({ row: rowNumber, message: `不支持的数据类型:${rawType || '(空)'}` })
      continue
    }

    const scan = Number(scanIndex >= 0 ? cells[scanIndex] : 100)
    tags.push({
      groupPath: parts.slice(0, -1),
      name: parts[parts.length - 1],
      address,
      dataType: mapping.dataType,
      isArray: mapping.isArray,
      metadata: mapping.isArray ? {
        'array.sourceFormat': 'KEP CSV',
        'array.sourceAddress': address,
        'kep.dataType': String(cells[typeIndex] ?? '').trim(),
      } : null,
      access: mapAccess(accessIndex >= 0 ? (cells[accessIndex] ?? '') : 'read'),
      scanRateMs: Number.isFinite(scan) && scan > 0 ? Math.round(scan) : 100,
      description: descriptionIndex >= 0 ? (cells[descriptionIndex] ?? '').trim() : '',
      row: rowNumber,
    })
  }
  return { tags, errors }
}

const normalizedPath = (parts: string[]) => parts.map((part) => part.trim().toLocaleLowerCase()).join('\u0000')

function collectTree(target: any) {
  const groups = new Set<string>()
  const tags = new Map<string, { address: string; access: string }>()
  const visit = (container: any, parent: string[]) => {
    for (const tag of container?.tags ?? [])
      tags.set(normalizedPath([...parent, String(tag.name ?? '')]), {
        address: String(tag.address ?? '').trim(),
        access: String(tag.access ?? 'read'),
      })
    const children = container?.groups ?? container?.subGroups ?? []
    for (const group of children) {
      const path = [...parent, String(group.name ?? '')]
      groups.add(normalizedPath(path))
      visit(group, path)
    }
  }
  visit(target, [])
  return { groups, tags }
}

export interface TagImportPreviewAccumulator {
  addExistingGroup(path: string[]): void
  addExistingTag(path: string[], tag: { address?: unknown; access?: unknown }): void
  add(tag: ImportedTag): void
  finish(): TagImportPreview
}

function createImportPreviewAccumulatorFromTree(tree: ReturnType<typeof collectTree>): TagImportPreviewAccumulator {
  const conflicts: TagImportConflict[] = []
  const importedPaths = new Set<string>()
  const accepted: ImportedTag[] = []
  let groupsToCreate = 0
  let skipped = 0

  const add = (tag: ImportedTag) => {
    const prefix: string[] = []
    let pathBlocked = false
    for (const segment of tag.groupPath) {
      prefix.push(segment)
      const key = normalizedPath(prefix)
      if (tree.tags.has(key)) {
        conflicts.push({ path: prefix.join('.'), row: tag.row, kind: 'group', message: '现有标记阻挡了导入组路径' })
        pathBlocked = true
        break
      }
      if (!tree.groups.has(key)) {
        tree.groups.add(key)
        groupsToCreate++
      }
    }

    const fullPath = [...tag.groupPath, tag.name]
    const key = normalizedPath(fullPath)
    if (pathBlocked || tree.groups.has(key)) {
      if (!pathBlocked)
      conflicts.push({ path: fullPath.join('.'), row: tag.row, kind: 'group', message: '同路径已存在标记组' })
    } else if (tree.tags.has(key)) {
      const duplicate = importedPaths.has(key)
      const existing = tree.tags.get(key)!
      if (!duplicate && existing.address === String(tag.address || '').trim()) {
        if (existing.access !== tag.access) {
          accepted.push({ ...tag, groupPath: [...tag.groupPath], refreshAccessOnly: true })
          tree.tags.set(key, { address: existing.address, access: tag.access })
          importedPaths.add(key)
        } else skipped++
        return
      }
      conflicts.push({
        path: fullPath.join('.'), row: tag.row,
        kind: duplicate ? 'duplicate' : 'tag',
        message: duplicate ? '导入文件内存在同路径重复标记' : '同路径已存在不同地址的标记',
      })
    }
    tree.tags.set(key, { address: String(tag.address || '').trim(), access: tag.access })
    importedPaths.add(key)
    accepted.push({ ...tag, groupPath: [...tag.groupPath] })
  }

  return {
    addExistingGroup: path => { tree.groups.add(normalizedPath(path)) },
    addExistingTag: (path, tag) => {
      tree.tags.set(normalizedPath(path), {
        address: String(tag.address ?? '').trim(),
        access: String(tag.access ?? 'read'),
      })
    },
    add,
    finish: () => ({ tags: accepted, conflicts, groupsToCreate, skipped }),
  }
}

/** Lets large import callers yield between tags without changing conflict semantics. */
export function createImportPreviewAccumulator(target: any): TagImportPreviewAccumulator {
  return createImportPreviewAccumulatorFromTree(collectTree(target))
}

export function createEmptyImportPreviewAccumulator(): TagImportPreviewAccumulator {
  return createImportPreviewAccumulatorFromTree({ groups: new Set<string>(), tags: new Map() })
}

export function buildImportPreview(target: any, imported: ImportedTag[]): TagImportPreview {
  const accumulator = createImportPreviewAccumulator(target)
  for (const tag of imported) accumulator.add(tag)
  return accumulator.finish()
}

function childGroups(container: any): any[] {
  if ('groups' in container) return (container.groups ||= [])
  return (container.subGroups ||= [])
}

const nameKey = (value: unknown) => String(value ?? '').trim().toLocaleLowerCase()

function indexContainer(container: any) {
  const groups = childGroups(container)
  const tags = (container.tags ||= [])
  return {
    groups,
    tags,
    groupByName: new Map<string, any>(groups.map((item: any) => [nameKey(item.name), item] as [string, any])),
    tagIndexByName: new Map<string, number>(tags.map((item: any, index: number) => [nameKey(item.name), index] as [string, number])),
  }
}

function toProjectTag(tag: ImportedTag) {
  return {
    id: '', name: tag.name, address: tag.address, dataType: tag.dataType, isArray: tag.isArray,
    array: tag.array || null,
    stringLength: tag.stringLength ?? null,
    metadata: tag.metadata ? { ...tag.metadata } : null,
    access: tag.access, scanRateMs: tag.scanRateMs, description: tag.description,
    deadband: 0, deadbandType: 'none',
  }
}

export function applyTagImport<T>(target: T, preview: TagImportPreview, overwriteConflicts: boolean): TagImportResult<T> {
  if (preview.conflicts.length > 0 && !overwriteConflicts)
    throw new Error(`存在 ${preview.conflicts.length} 个导入冲突，请确认覆盖后再应用。`)

  const clone = JSON.parse(JSON.stringify(target)) as any
  const indexes = new WeakMap<object, ReturnType<typeof indexContainer>>()
  const indexOf = (container: any) => {
    let index = indexes.get(container)
    if (!index) { index = indexContainer(container); indexes.set(container, index) }
    return index
  }
  let overwritten = 0
  for (const tag of preview.tags) {
    let container = clone
    for (const segment of tag.groupPath) {
      const index = indexOf(container)
      const segmentKey = nameKey(segment)
      if (index.tagIndexByName.has(segmentKey))
        throw new Error(`路径冲突:${[...tag.groupPath, tag.name].join('.')}`)
      let group = index.groupByName.get(segmentKey)
      if (!group) {
        group = { id: '', name: segment, tags: [], subGroups: [] }
        index.groups.push(group)
        index.groupByName.set(segmentKey, group)
      }
      container = group
    }

    const index = indexOf(container)
    const tagNameKey = nameKey(tag.name)
    if (index.groupByName.has(tagNameKey))
      throw new Error(`路径冲突:${[...tag.groupPath, tag.name].join('.')}`)
    const existingIndex = index.tagIndexByName.get(tagNameKey)
    if (existingIndex !== undefined) {
      index.tags[existingIndex] = tag.refreshAccessOnly
        ? { ...index.tags[existingIndex], access: tag.access }
        : toProjectTag(tag)
      overwritten++
    } else {
      index.tagIndexByName.set(tagNameKey, index.tags.length)
      index.tags.push(toProjectTag(tag))
    }
  }

  return { target: clone as T, imported: preview.tags.length, overwritten }
}
