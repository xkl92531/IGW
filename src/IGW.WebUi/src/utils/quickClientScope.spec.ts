import { describe, expect, it } from 'vitest'
import { loadFirstPopulatedScope, type QuickClientScopeNode } from './quickClientScope'

describe('Quick Client initial scope selection', () => {
  it('descends through empty parent groups and opens the first group that contains points', async () => {
    const root: QuickClientScopeNode = { path: 'SIM.PLC', children: [] }
    const catalog: Record<string, { itemCount: number, children: string[] }> = {
      'SIM.PLC': { itemCount: 0, children: ['SIM.PLC.Empty', 'SIM.PLC.Live'] },
      'SIM.PLC.Empty': { itemCount: 0, children: [] },
      'SIM.PLC.Live': { itemCount: 2, children: [] },
    }

    const selected = await loadFirstPopulatedScope(root, async node => {
      const scope = catalog[node.path]
      node.children = scope.children.map(path => ({ path, children: [] }))
      return scope.itemCount
    })

    expect(selected?.path).toBe('SIM.PLC.Live')
  })

  it('keeps an already populated exact scope selected without loading children', async () => {
    const calls: string[] = []
    const root: QuickClientScopeNode = {
      path: 'SIM.PLC.Live',
      children: [{ path: 'SIM.PLC.Live.Nested', children: [] }],
    }

    const selected = await loadFirstPopulatedScope(root, async node => {
      calls.push(node.path)
      return 2
    })

    expect(selected).toBe(root)
    expect(calls).toEqual(['SIM.PLC.Live'])
  })

  it('bounds empty-scope traversal and does not revisit a cycle', async () => {
    const root: QuickClientScopeNode = { path: 'Scope.0', children: [] }
    const calls: string[] = []

    const selected = await loadFirstPopulatedScope(root, async node => {
      calls.push(node.path)
      const segments = node.path.split('.')
      const index = Number(segments[segments.length - 1])
      node.children = index === 299
        ? [root]
        : [{ path: `Scope.${index + 1}`, children: [] }]
      return 0
    })

    expect(selected).toBeNull()
    expect(calls).toHaveLength(256)
    expect(new Set(calls).size).toBe(calls.length)
  })
})
