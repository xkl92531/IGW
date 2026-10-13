import { expect, it } from 'vitest'
import { fixedValueErrors, setUploadFixedRows, uploadFixedRows } from './mesFixedValues'

it('checks names and values against their type like the gateway does', () => {
  expect(fixedValueErrors([
    { name: 'A', dataType: 'Text', value: 'x' },
    { name: 'B', dataType: 'Int', value: '3' },
    { name: 'C', dataType: 'Int', value: '1.5' },
    { name: 'D', dataType: 'Decimal', value: 'abc' },
    { name: 'E', dataType: 'Bool', value: 'yes' },
    { name: 'A', dataType: 'Text', value: '' },
    { name: ' ', dataType: 'Text', value: '' },
  ])).toEqual(['', '', '不是整数', '不是数字', '不是布尔值', '字段名重复', '字段名不能为空'])
})

it('shows old text-only append fields in the same list and moves them into typed fixed values on edit', () => {
  const payload: any = { append: { WorkStation: 'OP051' }, fixedValues: [{ name: 'LineNo', dataType: 'Int', value: '3' }] }
  const rows = uploadFixedRows(payload)
  expect(rows).toEqual([{ name: 'WorkStation', dataType: 'Text', value: 'OP051' }, { name: 'LineNo', dataType: 'Int', value: '3' }])
  setUploadFixedRows(payload, [...rows, { name: 'Passed', dataType: 'Bool', value: 'true' }])
  expect(payload.append).toEqual({})
  expect(payload.fixedValues.map((row: any) => row.name)).toEqual(['WorkStation', 'LineNo', 'Passed'])
})
