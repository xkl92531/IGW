import { describe, expect, it } from 'vitest'
import { useProjectSelection } from './useProjectSelection'

describe('useProjectSelection', () => {
  it('freezes all selected stable IDs independently from the visible page', () => {
    const selection = useProjectSelection()
    selection.setContext('tag', 'group-1', '"project-7"')
    selection.replace(['t1', 't2', 't3'])

    const copied = selection.freeze('copy')
    selection.replace(['t4'])

    expect(copied.entityIds).toEqual(['t1', 't2', 't3'])
    expect(Object.isFrozen(copied)).toBe(true)
    expect(Object.isFrozen(copied.entityIds)).toBe(true)
    expect(selection.selectedIds.value).toEqual(['t4'])
  })

  it('removes only IDs completed by a cut operation', () => {
    const selection = useProjectSelection()
    selection.setContext('tag', 'group-1', '"project-7"')
    selection.replace(['t1', 't2', 't3', 't4'])
    selection.remove(['t1', 't3'])
    expect(selection.selectedIds.value).toEqual(['t2', 't4'])
  })
})
