import { ref } from 'vue'
import type { ProjectMutationRequest, ProjectMutationResponse } from './types'
import type { ProjectClipboardSnapshot, ProjectSelection } from './useProjectSelection'

interface ProjectMutationApi {
  mutate(command: ProjectMutationRequest, revision?: string): Promise<ProjectMutationResponse>
}

interface ProjectMutationHooks {
  onRevision?: (revision: string) => void
  invalidate?: (entityIds: readonly string[], targetId: string | null) => void
}

function responseStatus(reason: unknown): number | undefined {
  if (!reason || typeof reason !== 'object' || !('response' in reason)) return undefined
  const response = (reason as { response?: unknown }).response
  if (!response || typeof response !== 'object' || !('status' in response)) return undefined
  return Number((response as { status?: unknown }).status)
}

export function useProjectMutations(
  api: ProjectMutationApi,
  selection: ProjectSelection,
  hooks: ProjectMutationHooks = {},
) {
  const saving = ref(false)
  const reloadMessage = ref('')

  async function execute(
    command: ProjectMutationRequest,
    expectedRevision: string,
    clearMovedIds: readonly string[] = [],
  ) {
    saving.value = true
    reloadMessage.value = ''
    try {
      const result = await api.mutate(command, expectedRevision)
      selection.revision.value = result.revision
      hooks.onRevision?.(result.revision)
      hooks.invalidate?.(command.entityIds, command.targetId)
      if (clearMovedIds.length) selection.remove(clearMovedIds)
      return result
    } catch (reason) {
      if (responseStatus(reason) === 409)
        reloadMessage.value = '工程已被其他操作更新，请重新加载后重试；当前选择已保留。'
      throw reason
    } finally {
      saving.value = false
    }
  }

  async function move(snapshot: ProjectClipboardSnapshot, targetId: string) {
    if (snapshot.mode !== 'cut') throw new Error('复制操作需要创建新实体，不能按移动执行。')
    return execute({
      kind: 'moveEntities',
      entityKind: snapshot.kind,
      entityIds: snapshot.entityIds,
      targetId,
      documentJson: null,
    }, snapshot.sourceRevision, snapshot.entityIds)
  }

  async function copy(snapshot: ProjectClipboardSnapshot, targetId: string) {
    if (snapshot.mode !== 'copy') throw new Error('剪切操作不能按复制执行。')
    return execute({
      kind: 'copyEntities',
      entityKind: snapshot.kind,
      entityIds: snapshot.entityIds,
      targetId,
      documentJson: null,
    }, snapshot.sourceRevision)
  }

  function paste(snapshot: ProjectClipboardSnapshot, targetId: string) {
    return snapshot.mode === 'cut' ? move(snapshot, targetId) : copy(snapshot, targetId)
  }

  return { saving, reloadMessage, execute, move, copy, paste }
}
