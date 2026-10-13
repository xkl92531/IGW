import type { ArrayDef, StructDef, StructFieldDef } from './arrayTags'

/**
 * 结构体/数组按层展开的行：UDT 数组里套结构体、结构体里再套结构体数组时，
 * QC 和工程配置列表都按 TIA 那样一层层展开，平时收起。
 */
export interface StructureRow {
  key: string
  label: string
  /** QC：成员的值(对象/数组展开后才看里面) */
  value?: unknown
  /** 工程配置：类型、偏移 */
  type?: string
  offset?: string
  description?: string
  /** 工程配置：下一层结构在 DB 里的起始字节(数组取第一个元素)；null=优化 DB 只看相对偏移 */
  baseByte?: number | null
  hasChildren: boolean
  field?: StructFieldDef
  array?: ArrayDef | null
  struct?: StructDef | null
}

/** 数组元素里有没有嵌套的结构体或数组：没有时 QC 按列平铺就够看，有才用树形。 */
export function hasNestedMembers(def?: ArrayDef | null): boolean {
  return !!def?.struct?.fields?.some((field) => !!field.struct || !!field.array)
}

function isContainer(value: unknown): boolean {
  return value !== null && typeof value === 'object'
}

/** 值的简短显示：标量原样，对象/数组只显示项数(展开再看)。 */
export function displayValue(value: unknown): string {
  if (value === null || value === undefined) return '—'
  if (Array.isArray(value)) return `[${value.length}]`
  if (typeof value === 'object') return `{ ${Object.keys(value as object).length} 项 }`
  return String(value)
}

function arrayLabel(def: ArrayDef | null | undefined, index: number): string {
  // 值里的数组从 0 开始，按 PLC 下界换算成 TIA 里的下标(一维)
  const lower = def?.lowerBounds?.length === 1 ? Number(def.lowerBounds[0] || 0) : 0
  return `[${index + lower}]`
}

/** QC：展开一个值的下一层。对象按成员(按结构定义的顺序)，数组按元素。 */
export function valueChildren(parentKey: string, value: unknown, schema?: { struct?: StructDef | null; array?: ArrayDef | null }): StructureRow[] {
  if (Array.isArray(value)) {
    const element = schema?.array?.struct ?? null
    return value.map((item, index) => ({
      key: `${parentKey}/${index}`, label: arrayLabel(schema?.array, index), value: item,
      hasChildren: isContainer(item), struct: element,
    }))
  }
  if (!isContainer(value)) return []
  const record = value as Record<string, unknown>
  const fields = schema?.struct?.fields
  const names = fields?.length
    ? fields.map((field) => field.jsonName || field.name).filter((name) => name in record)
    : Object.keys(record)
  for (const name of Object.keys(record)) if (!names.includes(name)) names.push(name)
  return names.map((name) => {
    const field = fields?.find((item) => (item.jsonName || item.name) === name)
    const item = record[name]
    return {
      key: `${parentKey}/${name}`, label: name, value: item, hasChildren: isContainer(item),
      field, struct: field?.array ? field.array.struct ?? null : field?.struct ?? null, array: field?.array ?? null,
    }
  })
}

function typeLabel(field: StructFieldDef): string {
  if (field.array) {
    const bounds = field.array.dimensions.map((size, i) => `${field.array!.lowerBounds?.[i] ?? 0}..${(field.array!.lowerBounds?.[i] ?? 0) + size - 1}`)
    const element = field.array.struct ? 'Struct' : field.array.elementType
    return `Array[${bounds.join(', ')}] of ${element}`
  }
  if (field.struct) return 'Struct'
  return field.stringLength ? `${field.dataType}[${field.stringLength}]` : field.dataType
}

function offsetLabel(byte: number, bit?: number) {
  return `${byte}.${bit !== undefined && bit >= 0 ? bit : 0}`
}

/**
 * 工程配置：展开结构定义的下一层。偏移按 TIA 的写法(字节.位)，
 * baseByte 是这一层结构在 DB 里的起始字节(非优化 DB 才有意义；优化 DB 传 null 只显示相对偏移)。
 */
export function schemaChildren(parentKey: string, struct: StructDef | null | undefined, baseByte: number | null): StructureRow[] {
  return (struct?.fields || []).map((field) => {
    const nested = field.array ? field.array.struct ?? null : field.struct ?? null
    const absolute = baseByte === null ? null : baseByte + field.byteOffset
    return {
      key: `${parentKey}/${field.name}`, label: field.name, type: typeLabel(field),
      offset: absolute === null ? `+${offsetLabel(field.byteOffset, field.bitOffset)}` : offsetLabel(absolute, field.bitOffset),
      hasChildren: !!nested?.fields?.length, field, struct: nested, array: field.array ?? null, baseByte: absolute,
    }
  })
}

/** 非优化 DB 点位地址里的起始字节(DB4306.DBB124 → 124)；符号地址返回 null。 */
export function addressByte(address: string | undefined | null): number | null {
  const match = /\.DB[BWDX](\d+)/i.exec(String(address || ''))
  return match ? Number(match[1]) : null
}
