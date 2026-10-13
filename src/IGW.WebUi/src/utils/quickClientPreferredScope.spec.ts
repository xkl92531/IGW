import { describe, expect, it } from 'vitest'
import { loadExactScope, type QuickClientScopeNode } from './quickClientScope'

describe('restore the exact QC scope after a project rename', () => {
  it('loads only ancestors of the selected group and preserves literal slash names', async () => {
    const selected: QuickClientScopeNode = { path: 'PLC_Test.PLC10.DB2050_HeatBeat/Time', children: [] }
    const device = { path: 'PLC_Test.PLC10', children: [selected] }
    const channel = { path: 'PLC_Test', children: [device] }
    const other = { path: 'Other', children: [] }
    const root = { path: '', children: [other, channel] }
    const loaded: string[] = []
    const actual = await loadExactScope(root, async node => { loaded.push(node.path); return node === selected || node === other ? 1 : 0 }, selected.path)
    expect(actual).toBe(selected)
    expect(loaded).toEqual(['', 'PLC_Test', 'PLC_Test.PLC10', selected.path])
  })

  it('selects the longest exact prefix when device names contain dots', async () => {
    const selected = { path: 'C.PLC10.1.DB', children: [] }
    const root = { path: 'C', children: [{ path: 'C.PLC10', children: [] }, { path: 'C.PLC10.1', children: [selected] }] }
    const loaded: string[] = []
    expect(await loadExactScope(root, async node => { loaded.push(node.path); return 0 }, selected.path)).toBe(selected)
    expect(loaded).toEqual(['C', 'C.PLC10.1', selected.path])
  })

  it('does not select another scope when the requested path is gone or outside the root', async () => {
    const root = { path: 'C', children: [{ path: 'C.Other', children: [] }] }
    expect(await loadExactScope(root, async () => 1, 'C.Missing')).toBeNull()
    expect(await loadExactScope(root, async () => 1, 'Another.Plc.DB')).toBeNull()
  })
})
