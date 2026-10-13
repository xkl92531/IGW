import { computed, shallowRef } from 'vue'
import type { ProjectSummaryDto, ProjectTreeNodeDto } from './types'

interface ProjectTreeApi {
  revision(): string
  summary(signal?: AbortSignal): Promise<ProjectSummaryDto>
  children(kind: ProjectTreeNodeDto['kind'], id: string, signal?: AbortSignal): Promise<ProjectTreeNodeDto[]>
}

export function useProjectTree(api: ProjectTreeApi) {
  const root = shallowRef<ProjectTreeNodeDto | null>(null)
  const revision = shallowRef('')
  const branches = shallowRef(new Map<string, readonly ProjectTreeNodeDto[]>())
  const controllers = new Set<AbortController>()

  function syncRevision() {
    const next = api.revision()
    if (next === revision.value) return
    revision.value = next
    branches.value = new Map()
    for (const controller of controllers) controller.abort()
    controllers.clear()
  }

  async function request<T>(run: (signal: AbortSignal) => Promise<T>) {
    const controller = new AbortController()
    controllers.add(controller)
    try {
      return await run(controller.signal)
    } finally {
      controllers.delete(controller)
    }
  }

  async function loadRoot() {
    const summary = await request(signal => api.summary(signal))
    syncRevision()
    root.value = summary.root
    return summary.root
  }

  async function expand(parent: ProjectTreeNodeDto) {
    syncRevision()
    const key = `${revision.value}:${parent.kind}:${parent.id}`
    const cached = branches.value.get(key)
    if (cached) return cached

    const children = await request(signal => api.children(parent.kind, parent.id, signal))
    syncRevision()
    const currentKey = `${revision.value}:${parent.kind}:${parent.id}`
    branches.value = new Map(branches.value).set(currentKey, children)
    return children
  }

  function collapse(_parent: ProjectTreeNodeDto) {
    // Collapsing is visual only. The branch remains cached for the active revision.
  }

  function dispose() {
    for (const controller of controllers) controller.abort()
    controllers.clear()
    branches.value = new Map()
  }

  return {
    root,
    revision,
    cachedBranchCount: computed(() => branches.value.size),
    loadRoot,
    expand,
    collapse,
    syncRevision,
    dispose,
  }
}
