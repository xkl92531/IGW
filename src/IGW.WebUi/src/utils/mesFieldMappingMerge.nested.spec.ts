import { expect, it } from 'vitest'
import { mergeGeneratedMapping } from './mesFieldMappingMerge'

const root = 'Line.PLC.DB'
const range = { sourcePath: root, scope: 'Group' }
const member = { tagId: 'Value', fieldName: 'width', scale: 2, format: 'F2', parameterCode: 'V' }
const generated = () => ({ fields: [], structs: [{ kind: 'Array', baseGroup: `${root}.Slots`, aliasName: 'Slots', countMode: 'Enum', startIndex: 1, fixedCount: 2,
  members: [{ tagId: 'StructCount', fieldName: '' }], structs: [{ kind: 'Array', baseGroup: 'Cells', aliasName: 'Cells', countMode: 'Enum', startIndex: 5, fixedCount: 1,
    members: [{ tagId: 'Value', fieldName: '' }] }] }] })

it('preserves direct and child member contracts when configuration data upgrades a flattened array', () => {
  const payload: any = { fields: [], structs: [{ kind: 'Array', baseGroup: `${root}.Slots`, aliasName: 'slots', countMode: 'Enum', startIndex: 1, fixedCount: 2,
    members: [{ tagId: 'StructCount', fieldName: 'count', scale: 2 }] },
    ...[1, 2].map(slot => ({ kind: 'Array', baseGroup: `${root}.Slots[${slot}].Cells`, aliasName: 'cells', members: [member] }))] }
  mergeGeneratedMapping(payload, generated(), [range], [range])
  expect(payload.structs).toHaveLength(1)
  expect(payload.structs[0]).toMatchObject({ aliasName: 'slots', members: [{ tagId: 'StructCount', fieldName: 'count', scale: 2 }] })
  expect(payload.structs[0].structs[0]).toMatchObject({ baseGroup: 'Cells', aliasName: 'cells', members: [member] })
})

it('preserves ordinary-field conversions when configuration data creates nested arrays', () => {
  const payload: any = { fields: [1, 2].map(slot => ({ ...member, tagId: `${root}.Slots[${slot}].Cells[5].Value` })), structs: [] }
  mergeGeneratedMapping(payload, generated(), [range], [range])
  expect(payload.fields).toEqual([])
  expect(payload.structs[0].structs[0].members[0]).toEqual(member)
})

it('keeps the entire configuration intact when old per-slot contracts conflict', () => {
  const payload: any = { fields: [], structs: [{ kind: 'Array', baseGroup: `${root}.Slots`, aliasName: 'slots', members: [{ tagId: 'StructCount' }] },
    ...[1, 2].map(slot => ({ kind: 'Array', baseGroup: `${root}.Slots[${slot}].Cells`, aliasName: slot === 1 ? 'first' : 'second', members: [member] }))] }
  const before = structuredClone(payload)
  expect(() => mergeGeneratedMapping(payload, generated(), [range], [range])).toThrow(/不同|冲突/)
  expect(payload).toEqual(before)
})
