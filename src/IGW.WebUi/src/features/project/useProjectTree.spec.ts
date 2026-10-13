import { describe, expect, it, vi } from 'vitest'
import { useProjectTree } from './useProjectTree'
import type { ProjectTreeNodeDto } from './types'

const node = (id: string, kind: ProjectTreeNodeDto['kind']): ProjectTreeNodeDto => ({
  id, kind, name: id, description: null, actualPath: id, hasChildren: true,
  directTagCount: 0, rowVersion: 1, schemaVersion: 1,
})

describe('useProjectTree', () => {
  it('loads only expanded branches, reuses cache, and invalidates it on revision change', async () => {
    let revision = '"project-1"'
    const children = vi.fn(async (_kind: ProjectTreeNodeDto['kind'], id: string) => [node(`${id}-child`, 'group')])
    const tree = useProjectTree({
      revision: () => revision,
      summary: vi.fn(async () => ({
        root: node('root', 'project'), totalTagCount: 1_000_000, fullProjectSupported: false,
        configurationDegraded: false, configurationDegradedMessage: null,
      })),
      children,
    })

    await tree.loadRoot()
    await tree.expand(node('device-1', 'device'))
    await tree.expand(node('group-1', 'group'))
    await tree.expand(node('device-1', 'device'))
    expect(children).toHaveBeenCalledTimes(2)

    revision = '"project-2"'
    tree.syncRevision()
    await tree.expand(node('device-1', 'device'))
    expect(children).toHaveBeenCalledTimes(3)
    expect(tree.cachedBranchCount.value).toBe(1)
  })
})
