import { describe, expect, it } from 'vitest'
import { indexAt, linearOffset, normalizeArrayDef, pageSlice } from './arrayTags'

describe('array tag helpers', () => {
  it('maps non-zero multidimensional indices to row-major offsets', () => {
    expect(linearOffset([2, 3], [1, -2], [2, 0])).toBe(5)
    expect(indexAt(5, [2, 3], [1, -2])).toEqual([2, 0])
  })

  it('normalizes omitted lower bounds without mutating project input', () => {
    const source = { elementType: 'int16', dimensions: [2, 3], lowerBounds: [] }
    const normalized = normalizeArrayDef(source)
    expect(normalized.lowerBounds).toEqual([0, 0])
    expect(source.lowerBounds).toEqual([])
  })

  it('converts a flat page into a bounded multidimensional slice', () => {
    expect(pageSlice(4, 3, [2, 3], [1, -2])).toEqual({ start: [2, -1], length: [1, 2] })
  })
})
