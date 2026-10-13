import { describe, expect, it } from 'vitest'
import { nestDetectedStructs, preserveAutoDetectedMappings } from './mesStructMapping'

const root = 'PLC10.PLC10.P0180A.DB2118_OutData3(P180)'
const array = (baseGroup: string, aliasName: string, members: any[] = [], startIndex = 1, fixedCount = 2) =>
  ({ kind: 'Array', baseGroup, aliasName, countMode: 'Enum', startIndex, fixedCount, members })
const members = [{ tagId: 'BBWeldWid_Min', fieldName: 'width', scale: 2 }, { tagId: 'BBTestResult', fieldName: '', scale: 1 }]

describe('nest automatic structure mappings by their actual parent elements', () => {
  it.each(['bracket', 'underscore', 'numeric'])('places the cell array inside each slot (%s)', style => {
    const element = (index: number) => style === 'bracket' ? `Slot_Data[${index}]` : style === 'underscore' ? `Slot_Data_${index}` : `Slot_Data.${index}`
    const input = [array(`${root}.Slot_Data`, 'Slot Data', [{ tagId: 'StructCount' }]),
      ...[1, 2].map(index => array(`${root}.${element(index)}.Cell_Date_1st`, 'Cell_Date_1st', members))]
    const before = JSON.stringify(input)
    const result = nestDetectedStructs(input)
    expect(result).toHaveLength(1)
    expect(result[0].members).toEqual([{ tagId: 'StructCount' }])
    expect(result[0].structs).toHaveLength(1)
    expect(result[0].structs[0]).toMatchObject({ baseGroup: 'Cell_Date_1st', aliasName: 'Cell_Date_1st', startIndex: 1, fixedCount: 2, members })
    expect(JSON.stringify(input)).toBe(before)
  })

  it('recursively attaches three levels without embedding a fixed parent index', () => {
    const input = [array(`${root}.Slot_Data`, 'slots')]
    for (const slot of [1, 2]) {
      input.push(array(`${root}.Slot_Data[${slot}].Cells`, 'cells'))
      for (const cell of [1, 2]) input.push(array(`${root}.Slot_Data[${slot}].Cells[${cell}].Measures`, 'measures', members))
    }
    const result = nestDetectedStructs(input)
    expect(result).toHaveLength(1)
    expect(result[0].structs[0].baseGroup).toBe('Cells')
    expect(result[0].structs[0].structs[0]).toMatchObject({ baseGroup: 'Measures', aliasName: 'measures', members })
  })

  it('discovers an outer array even if its only contents are nested arrays', () => {
    const result = nestDetectedStructs([1, 2].map(slot => array(`${root}.Slot_Data[${slot}].Cells`, 'cells', members)))
    expect(result).toHaveLength(1)
    expect(result[0]).toMatchObject({ kind: 'Array', baseGroup: `${root}.Slot_Data`, startIndex: 1, fixedCount: 2, members: [] })
    expect(result[0].structs[0].baseGroup).toBe('Cells')
  })

  it('refuses to silently overwrite different per-element interface aliases', () => {
    const input = [array(`${root}.Slot_Data`, 'slots'), array(`${root}.Slot_Data[1].Cells`, 'first', members), array(`${root}.Slot_Data[2].Cells`, 'second', members)]
    const before = JSON.stringify(input)
    expect(() => nestDetectedStructs(input)).toThrow(/不同|冲突/)
    expect(JSON.stringify(input)).toBe(before)
  })

  it('does not absorb a separately configured array outside the parent selection', () => {
    const result = nestDetectedStructs([array(`${root}.Slot_Data`, 'slots', [], 2, 1), array(`${root}.Slot_Data[1].Cells`, 'first', members)])
    expect(result).toHaveLength(2)
  })

  it('keeps a single-element outer array but never creates parents outside the selected task group', () => {
    const scope = 'Line.PLC.OP090_1'
    const result = nestDetectedStructs([array(`${scope}.Slot_Data[3].Cells`, 'cells', members)], scope)
    expect(result).toHaveLength(1)
    expect(result[0]).toMatchObject({ baseGroup: `${scope}.Slot_Data`, startIndex: 3, fixedCount: 1 })
    expect(result[0].structs[0].baseGroup).toBe('Cells')
    const ordinary = nestDetectedStructs([array(`${scope}.Cells`, 'cells', members)], scope)
    expect(ordinary[0].baseGroup).toBe(`${scope}.Cells`)
  })

  it('preserves nested aliases and member scales when refilling a generated mapping', () => {
    const previous = { structs: [array(`${root}.Slot_Data`, 'slots', [])] } as any
    previous.structs[0].structs = [array('Cell_Date_1st', 'cells', members)]
    const detected = { structs: [array(`${root}.Slot_Data`, 'Slot_Data', [])] } as any
    detected.structs[0].structs = [array('Cell_Date_1st', 'Cell_Date_1st', [{ tagId: 'BBWeldWid_Min', fieldName: '', scale: 1 }])]
    preserveAutoDetectedMappings(previous, detected)
    expect(detected.structs[0].aliasName).toBe('slots')
    expect(detected.structs[0].structs[0]).toMatchObject({ aliasName: 'cells', members: [{ tagId: 'BBWeldWid_Min', fieldName: 'width', scale: 2 }] })
  })

  it('never extends a child selected in only one slot to all selected slots', () => {
    const input = [array(`${root}.Slot_Data`, 'slots'), array(`${root}.Slot_Data[1].Cells`, 'cells', members)]
    expect(() => nestDetectedStructs(input)).toThrow(/部分|不完整|不同/)
    const shared = array(`${root}.Slot_Data`, 'slots') as any
    shared.structs = [array('Cells', 'cells', members)]
    expect(nestDetectedStructs([shared])[0].structs).toEqual(shared.structs)
  })

  it('rejects conflicting legacy contracts instead of preserving the last slot across every slot', () => {
    const previous = { structs: [array(`${root}.Slot_Data[1].Cells`, 'first', [{ tagId: 'BBWeldWid_Min', fieldName: 'one', scale: 2 }]),
      array(`${root}.Slot_Data[2].Cells`, 'second', [{ tagId: 'BBWeldWid_Min', fieldName: 'two', scale: 3 }])] }
    const detected = { structs: [array(`${root}.Slot_Data`, 'slots')] } as any
    detected.structs[0].structs = [array('Cells', 'Cells', [{ tagId: 'BBWeldWid_Min' }])]
    expect(() => preserveAutoDetectedMappings(previous, detected)).toThrow(/不同|冲突/)
  })

  it('treats numbered array names as names rather than their own element indices', () => {
    const previous = { structs: [array(`${root}.Measures_1`, 'first', members), array(`${root}.Measures_2`, 'second', members)] }
    const detected = { structs: [array(`${root}.Measures_1`, 'Measures_1', members), array(`${root}.Measures_2`, 'Measures_2', members)] }
    preserveAutoDetectedMappings(previous, detected)
    expect(detected.structs.map(item => item.aliasName)).toEqual(['first', 'second'])
  })

  it('carries compatible ordinary-field contracts into nested members when refilling', () => {
    const previous = { fields: [1, 2].map(slot => ({ tagId: `${root}.Slot_Data[${slot}].Cells[5].Value`, fieldName: 'wireValue', scale: 2, format: 'F2', parameterCode: 'V' })) }
    const detected = { structs: [array(`${root}.Slot_Data`, 'slots')] } as any
    detected.structs[0].structs = [array('Cells', 'cells', [{ tagId: 'Value', fieldName: '' }], 5, 1)]
    preserveAutoDetectedMappings(previous, detected)
    expect(detected.structs[0].structs[0].members[0]).toMatchObject({ tagId: 'Value', fieldName: 'wireValue', scale: 2, format: 'F2', parameterCode: 'V' })
  })

  it('retains the selected dictionary setting when detecting nested structures again', () => {
    const previous = { structs: [{ ...array(`${root}.Slot_Data`, 'slots'), asDictionary: true }] }
    const detected = { structs: [{ ...array(`${root}.Slot_Data`, 'Slot_Data'), asDictionary: false }] }
    preserveAutoDetectedMappings(previous, detected)
    expect(detected.structs[0].asDictionary).toBe(true)
  })
})
