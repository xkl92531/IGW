import { describe, expect, it } from 'vitest'
import { buildMesRangeIndex, collectMesScalarTagPaths, validateMesRanges, migrateMesReferences, setMesArrayLengthMode } from './mesDataRanges'

const project = {
  channels: [{
    name: 'Line',
    devices: [{
      name: 'Plc',
      groups: [{
        name: 'DB1',
        tags: [{
          name: 'Cells',
          array: {
            elementType: 'Struct', dimensions: [2, 3], lowerBounds: [1, -2],
            struct: { fields: [{ name: 'IsNG', jsonName: 'isNg', dataType: 'Int16' }] },
          },
        }],
        subGroups: [],
      }],
    }],
  }],
}

describe('MES direct array ranges', () => {
  it('switches length sources exclusively and migrates PLC count references on rename', () => {
    const range: any = { arrayLengths: [2, 3], arrayLengthTagIds: [] }
    setMesArrayLengthMode(range, 'Tag', [2, 3])
    expect(range.arrayLengths).toEqual([])
    range.arrayLengthTagIds = ['Old.DB.Rows', 'Old.DB.Columns']
    expect(migrateMesReferences(range, 'Old.DB', 'New.DB')).toBe(2)
    expect(range.arrayLengthTagIds).toEqual(['New.DB.Rows', 'New.DB.Columns'])
    setMesArrayLengthMode(range, 'Fixed', [2, 3])
    expect(range.arrayLengthTagIds).toEqual([])
    expect(range.arrayLengths).toEqual([2, 3])
  })
  it('accepts automatic lengths at a nonzero start and rejects mixed sources', () => {
    const index = buildMesRangeIndex(project)
    const range = {
      sourcePath: 'Line.Plc.DB1.Cells', scope: 'Array', outputPath: 'cells',
      arrayStart: [2, -1], arrayLengths: [], arrayLengthMode: 'Tag',
      arrayLengthTagIds: ['Line.Plc.DB1.Rows', 'Line.Plc.DB1.Columns'],
    }
    expect(validateMesRanges([range], index)).toBeNull()
    expect(validateMesRanges([{ ...range, arrayLengths: [1, 1] }], index)).toContain('只能使用一种')
    expect(validateMesRanges([{ ...range, arrayLengthTagIds: [''] }], index)).toContain('长度点位')
  })
  it('indexes one logical array without expanding element tags', () => {
    const index = buildMesRangeIndex(project)
    const array = index.bySourcePath.get('Line.Plc.DB1.Cells')
    expect(array?.scope).toBe('Array')
    expect(array?.tagCount).toBe(6)
    expect(array?.array?.dimensions).toEqual([2, 3])
    expect(array?.children).toEqual([])
  })

  it('validates multidimensional PLC lower-bound slices', () => {
    const index = buildMesRangeIndex(project)
    const valid = [{
      sourcePath: 'Line.Plc.DB1.Cells', outputPath: 'cells', excludes: [],
      arrayStart: [1, -1], arrayLengths: [2, 2],
    }]
    expect(validateMesRanges(valid, index)).toBeNull()
    expect(validateMesRanges([{ ...valid[0], arrayStart: [2, 0], arrayLengths: [2, 2] }], index))
      .toContain('切片超出')
  })
})

describe('MES main-group scalar collection', () => {
  it('recursively collects ordinary points but skips every logical array subtree', () => {
    const projectWithNestedArrays = {
      channels: [{
        name: 'Line',
        devices: [{
          name: 'Plc',
          groups: [{
            name: 'OP200Data',
            tags: [
              { name: 'DataReady' },
              { name: 'Samples', array: { dimensions: [20], lowerBounds: [0] } },
            ],
            subGroups: [
              { name: 'General', tags: [{ name: 'StationCode' }], subGroups: [] },
              {
                name: 'Bolts', tags: [], subGroups: [
                  { name: '0', tags: [{ name: 'Torque' }], subGroups: [] },
                  { name: '1', tags: [{ name: 'Torque' }], subGroups: [] },
                ],
              },
              { name: 'Results_0', tags: [{ name: 'Code' }], subGroups: [] },
              { name: 'Results_1', tags: [{ name: 'Code' }], subGroups: [] },
              {
                name: 'Nested', tags: [{ name: 'Status' }], subGroups: [{
                  name: 'Measurements', tags: [], subGroups: [
                    { name: '1', tags: [{ name: 'Value' }], subGroups: [] },
                    { name: '2', tags: [{ name: 'Value' }], subGroups: [] },
                  ],
                }],
              },
            ],
          }, {
            name: 'OP201Data',
            tags: [{ name: 'MustNotCrossMainGroup' }],
            subGroups: [],
          }],
        }],
      }],
    }

    expect(collectMesScalarTagPaths(projectWithNestedArrays, 'Line.Plc.OP200Data')).toEqual([
      { path: 'Line.Plc.OP200Data.DataReady', name: 'DataReady' },
      { path: 'Line.Plc.OP200Data.General.StationCode', name: 'StationCode' },
      { path: 'Line.Plc.OP200Data.Nested.Status', name: 'Status' },
    ])
  })
})
