import { expect, it } from 'vitest'
import { addressByte, displayValue, hasNestedMembers, schemaChildren, valueChildren } from './structureTree'
import type { ArrayDef } from './arrayTags'

// 与 ST430 数据收集 DB 同样的结构：Array[0..10] of MES_GlueData { GlueData: Struct { CellNumber, CellGlueData: Array[1..5] of Struct } }
const cell = { id: 'c', name: 'Cell', sizeBytes: 8, fields: [
  { name: 'GlueLength_L', dataType: 'Float', byteOffset: 0 }, { name: 'GlueWidth1_L', dataType: 'Float', byteOffset: 4 }] }
const glue = { id: 'g', name: 'GlueData', sizeBytes: 42, fields: [
  { name: 'CellNumber', dataType: 'Int16', byteOffset: 0 },
  { name: 'CellGlueData', dataType: 'Struct', byteOffset: 2, array: { elementType: 'Struct', dimensions: [5], lowerBounds: [1], struct: cell } }] }
const definition: ArrayDef = { elementType: 'Struct', dimensions: [11], lowerBounds: [0],
  struct: { id: 'u', name: 'MES_GlueData', sizeBytes: 42, fields: [{ name: 'GlueData', dataType: 'Struct', byteOffset: 0, struct: glue }] } }

it('recognizes nested structures that need the tree view', () => {
  expect(hasNestedMembers(definition)).toBe(true)
  expect(hasNestedMembers({ elementType: 'Struct', dimensions: [2], lowerBounds: [0], struct: cell })).toBe(false)
})

it('expands values level by level with TIA indices', () => {
  const element = { GlueData: { CellNumber: 3, CellGlueData: [{ GlueLength_L: 1.5, GlueWidth1_L: 2 }, { GlueLength_L: 0, GlueWidth1_L: 0 }] } }
  const [module] = valueChildren('0', element, { struct: definition.struct })
  expect(module).toMatchObject({ label: 'GlueData', hasChildren: true })
  expect(displayValue(module!.value)).toBe('{ 2 项 }')
  const members = valueChildren(module!.key, module!.value, module!)
  expect(members.map((row) => row.label)).toEqual(['CellNumber', 'CellGlueData'])
  const cells = valueChildren(members[1]!.key, members[1]!.value, members[1]!)
  expect(cells.map((row) => row.label)).toEqual(['[1]', '[2]'])            // 下界 1
  expect(valueChildren(cells[0]!.key, cells[0]!.value, cells[0]!).map((row) => `${row.label}=${row.value}`))
    .toEqual(['GlueLength_L=1.5', 'GlueWidth1_L=2'])
})

it('describes the structure with TIA offsets for the project list', () => {
  const base = addressByte('DB4306.DBB124')
  expect(base).toBe(124)
  const [module] = schemaChildren('tag', definition.struct, base)
  expect(module).toMatchObject({ label: 'GlueData', type: 'Struct', offset: '124.0', hasChildren: true })
  const members = schemaChildren(module!.key, module!.struct, module!.baseByte ?? null)
  expect(members.map((row) => [row.label, row.type, row.offset])).toEqual([
    ['CellNumber', 'Int16', '124.0'], ['CellGlueData', 'Array[1..5] of Struct', '126.0']])
  expect(schemaChildren('x', members[1]!.struct, members[1]!.baseByte ?? null).map((row) => row.offset)).toEqual(['126.0', '130.0'])
  // 优化 DB 没有绝对地址，只显示相对偏移
  expect(schemaChildren('tag', definition.struct, addressByte('ST430_DB.CellModeGlueDataArray'))[0]!.offset).toBe('+0.0')
})
