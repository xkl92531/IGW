import { describe, expect, it, vi } from 'vitest'
import { useProjectMutations } from './useProjectMutations'
import { useProjectSelection } from './useProjectSelection'
import type { ProjectMutationRequest } from './types'

describe('useProjectMutations', () => {
  it('updates revision before invalidation and clears only moved IDs', async () => {
    const events: string[] = []
    const selection = useProjectSelection()
    selection.setContext('tag', 'group-1', '"project-7"')
    selection.replace(['t1', 't2', 't3', 't4'])
    const snapshot = selection.freeze('cut', ['t1', 't2', 't3'])
    const mutate = vi.fn(async (_command: ProjectMutationRequest, _revision?: string) => ({
      applied: true,
      revision: '"project-8"',
    }))
    const mutations = useProjectMutations({ mutate }, selection, {
      onRevision: revision => events.push(`revision:${revision}`),
      invalidate: () => events.push('invalidate'),
    })

    await mutations.move(snapshot, 'group-2')

    expect(mutate.mock.calls[0][0].entityIds).toEqual(['t1', 't2', 't3'])
    expect(mutate.mock.calls[0][1]).toBe('"project-7"')
    expect(events).toEqual(['revision:"project-8"', 'invalidate'])
    expect(selection.selectedIds.value).toEqual(['t4'])
  })

  it('keeps selection and exposes reload guidance after revision conflict', async () => {
    const selection = useProjectSelection()
    selection.setContext('tag', 'group-1', '"project-7"')
    selection.replace(['t1', 't2', 't3'])
    const snapshot = selection.freeze('cut')
    const mutate = vi.fn().mockRejectedValue({ response: { status: 409 } })
    const mutations = useProjectMutations({ mutate }, selection)

    await expect(mutations.move(snapshot, 'group-2')).rejects.toBeTruthy()

    expect(selection.selectedIds.value).toEqual(['t1', 't2', 't3'])
    expect(mutations.reloadMessage.value).toContain('重新加载')
  })

  it('copies the complete frozen selection without clearing source selection', async () => {
    const selection = useProjectSelection()
    selection.setContext('tag', 'group-1', '"project-7"')
    selection.replace(['t1', 't2', 't3'])
    const snapshot = selection.freeze('copy')
    const mutate = vi.fn(async (_command: ProjectMutationRequest, _revision?: string) => ({ applied: true, revision: '"project-8"' }))
    const mutations = useProjectMutations({ mutate }, selection)

    await mutations.paste(snapshot, 'group-2')

    expect(mutate.mock.calls[0][0]).toMatchObject({ kind: 'copyEntities', entityIds: ['t1', 't2', 't3'], targetId: 'group-2' })
    expect(selection.selectedIds.value).toEqual(['t1', 't2', 't3'])
  })
})
