import { describe, expect, it } from 'vitest'
import { migrateMesReferences } from './mesDataRanges'

describe('MES references when moving a logical array tag', () => {
  it('moves element aliases in structures, conditions and writebacks without changing response names', () => {
    const oldPath = 'C.D.Source.Cells', newPath = 'C.D.Target.Cells'
    const task = { flow: { nodes: [
      { type: 'Upload', payload: { fields: [{ tagId: `${oldPath}[2].K`, fieldName: `${oldPath}[2].K` }],
        structs: [{ baseGroup: `${oldPath}[0]`, aliasName: 'cells' }] } },
      { type: 'Condition', branches: [{ conditions: [
        { source: 'Tag', path: `${oldPath}[2,-1].K` }, { source: 'Response', path: `${oldPath}[2].K` },
      ] }] },
      { type: 'ResponseWriteback', writeBack: {
        fromResponse: [{ tagId: `${oldPath}[2].K`, responseField: `${oldPath}[2].K` }],
        manual: [{ tagId: 'C.D.Result', copyFromTagId: `${oldPath}[2].K` }],
      } },
    ] } }
    expect(migrateMesReferences(task, oldPath, newPath)).toBe(5)
    const [upload, condition, write] = task.flow.nodes
    expect(upload.payload!.fields[0]).toEqual({ tagId: `${newPath}[2].K`, fieldName: `${oldPath}[2].K` })
    expect(upload.payload!.structs[0].baseGroup).toBe(`${newPath}[0]`)
    expect(condition.branches![0].conditions.map(item => item.path)).toEqual([`${newPath}[2,-1].K`, `${oldPath}[2].K`])
    expect(write.writeBack!.fromResponse[0]).toEqual({ tagId: `${newPath}[2].K`, responseField: `${oldPath}[2].K` })
    expect(write.writeBack!.manual[0].copyFromTagId).toBe(`${newPath}[2].K`)
  })

  it('keeps similarly prefixed point names unchanged', () => {
    const refs = [
      { tagId: 'C.D.Source.Cells2[0].K' }, { tagId: 'C.D.Source.Cells_2[0].K' },
      { tagId: 'C.D.Source.Cells[0].K' }, { tagId: 'C.D.Source.Cells.K' }, { tagId: 'C.D.Source.Cells' },
    ]
    expect(migrateMesReferences(refs, 'C.D.Source.Cells', 'C.D.Target.Cells')).toBe(3)
    expect(refs.map(item => item.tagId)).toEqual([
      'C.D.Source.Cells2[0].K', 'C.D.Source.Cells_2[0].K',
      'C.D.Target.Cells[0].K', 'C.D.Target.Cells.K', 'C.D.Target.Cells',
    ])
  })
})
