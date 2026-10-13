import { computed, shallowRef } from 'vue'
import type { ProjectNodeKind } from './types'

export interface ProjectClipboardSnapshot {
  mode: 'copy' | 'cut'
  kind: ProjectNodeKind
  entityIds: readonly string[]
  sourceParentId: string
  sourceRevision: string
}

export function useProjectSelection() {
  const selectedIds = shallowRef<readonly string[]>([])
  const selectedKind = shallowRef<ProjectNodeKind>('tag')
  const parentId = shallowRef('')
  const revision = shallowRef('')

  function setContext(kind: ProjectNodeKind, nextParentId: string, nextRevision: string) {
    selectedKind.value = kind
    parentId.value = nextParentId
    revision.value = nextRevision
  }

  function replace(ids: readonly string[]) {
    selectedIds.value = [...new Set(ids.filter(Boolean))]
  }

  function toggle(id: string, selected?: boolean) {
    const next = new Set(selectedIds.value)
    const shouldSelect = selected ?? !next.has(id)
    if (shouldSelect) next.add(id)
    else next.delete(id)
    selectedIds.value = [...next]
  }

  function remove(ids: readonly string[]) {
    const removed = new Set(ids)
    selectedIds.value = selectedIds.value.filter(id => !removed.has(id))
  }

  function clear() {
    selectedIds.value = []
  }

  function freeze(mode: ProjectClipboardSnapshot['mode'], ids = selectedIds.value): ProjectClipboardSnapshot {
    const selected = new Set(selectedIds.value)
    const frozenIds = Object.freeze([...new Set(ids.filter(id => selected.has(id)))])
    if (!parentId.value || !revision.value || frozenIds.length === 0)
      throw new Error('没有可复制或剪切的稳定选择。')
    return Object.freeze({
      mode,
      kind: selectedKind.value,
      entityIds: frozenIds,
      sourceParentId: parentId.value,
      sourceRevision: revision.value,
    })
  }

  return {
    selectedIds,
    selectedCount: computed(() => selectedIds.value.length),
    selectedKind,
    parentId,
    revision,
    setContext,
    replace,
    toggle,
    remove,
    clear,
    freeze,
  }
}

export type ProjectSelection = ReturnType<typeof useProjectSelection>
