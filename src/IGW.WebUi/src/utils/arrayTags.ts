export interface StructFieldDef {
  name: string
  jsonName?: string | null
  description?: string | null
  dataType: string
  byteOffset: number
  bitOffset?: number
  stringLength?: number | null
  array?: ArrayDef | null
  struct?: StructDef | null
}

export interface StructDef {
  id: string
  name: string
  sizeBytes: number
  fields: StructFieldDef[]
}

export interface ArrayDef {
  elementType: string
  dimensions: number[]
  lowerBounds: number[]
  elementStrideBytes?: number
  elementStrideBits?: number
  stringLength?: number | null
  struct?: StructDef | null
  sourceShape?: string | null
}

export interface ArraySliceVector { start: number[]; length: number[] }

function assertShape(dimensions: readonly number[], lowerBounds: readonly number[]) {
  if (!dimensions.length || dimensions.length !== lowerBounds.length)
    throw new Error('数组维度与下界数量不一致')
  if (dimensions.some((dimension) => !Number.isSafeInteger(dimension) || dimension <= 0))
    throw new Error('数组维度必须是正整数')
}

export function normalizeArrayDef(source: Partial<ArrayDef>): ArrayDef {
  const dimensions = [...(source.dimensions || [1])]
  const lowerBounds = source.lowerBounds?.length
    ? [...source.lowerBounds]
    : dimensions.map(() => 0)
  assertShape(dimensions, lowerBounds)
  return {
    elementType: source.elementType || 'int16',
    dimensions,
    lowerBounds,
    elementStrideBytes: source.elementStrideBytes || 0,
    elementStrideBits: source.elementStrideBits || 0,
    stringLength: source.stringLength ?? null,
    struct: source.struct || null,
    sourceShape: source.sourceShape || null,
  }
}

export function elementCount(dimensions: readonly number[]): number {
  return dimensions.reduce((total, dimension) => {
    const next = total * dimension
    if (!Number.isSafeInteger(next)) throw new Error('数组元素总数超出浏览器安全范围')
    return next
  }, 1)
}

export function linearOffset(
  dimensions: readonly number[], lowerBounds: readonly number[], indices: readonly number[],
): number {
  assertShape(dimensions, lowerBounds)
  if (indices.length !== dimensions.length) throw new Error('索引数量必须与数组维度一致')
  let offset = 0
  for (let i = 0; i < dimensions.length; i++) {
    const normalized = indices[i] - lowerBounds[i]
    if (!Number.isSafeInteger(indices[i]) || normalized < 0 || normalized >= dimensions[i])
      throw new Error(`第 ${i + 1} 维索引超出范围`)
    offset = offset * dimensions[i] + normalized
  }
  return offset
}

export function indexAt(
  offset: number, dimensions: readonly number[], lowerBounds: readonly number[],
): number[] {
  assertShape(dimensions, lowerBounds)
  const count = elementCount(dimensions)
  if (!Number.isSafeInteger(offset) || offset < 0 || offset >= count) throw new Error('线性索引超出范围')
  const result = new Array<number>(dimensions.length)
  for (let i = dimensions.length - 1; i >= 0; i--) {
    result[i] = (offset % dimensions[i]) + lowerBounds[i]
    offset = Math.floor(offset / dimensions[i])
  }
  return result
}

/**
 * 将扁平分页定位到最后一维内的一个连续矩形切片。分页不会跨行，避免把百万点数组展开成请求列表。
 */
export function pageSlice(
  offset: number, size: number, dimensions: readonly number[], lowerBounds: readonly number[],
): ArraySliceVector {
  const start = indexAt(offset, dimensions, lowerBounds)
  const length = dimensions.map(() => 1)
  const last = dimensions.length - 1
  const usedInLastDimension = start[last] - lowerBounds[last]
  length[last] = Math.max(1, Math.min(Math.max(1, Math.floor(size)), dimensions[last] - usedInLastDimension))
  return { start, length }
}

export function parseIntegerVector(text: string, label: string): number[] {
  const values = text.split(',').map((part) => part.trim()).filter(Boolean).map(Number)
  if (!values.length || values.some((value) => !Number.isSafeInteger(value)))
    throw new Error(`${label}必须是逗号分隔的整数`)
  return values
}

export function qualityClass(code: number): 'Good' | 'Uncertain' | 'Bad' {
  const severity = code >>> 30
  return severity === 0 ? 'Good' : severity === 1 ? 'Uncertain' : 'Bad'
}
