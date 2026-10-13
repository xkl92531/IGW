import type { AxiosInstance, AxiosResponse } from 'axios'
import { http, setActiveProjectRevision } from '../../api'
import { withAccessToken } from '../../utils/sessionToken'
import type {
  ProjectEntityDocumentDto,
  ProjectExportOptions,
  ProjectImportPreviewDto,
  ProjectImportResultDto,
  ProjectLazyChildrenDto,
  ProjectMesTaskQuery,
  ProjectMesTaskDocumentDto,
  ProjectMesTaskRowDto,
  ProjectMutationRequest,
  ProjectMutationResponse,
  ProjectNodeKind,
  ProjectPageDto,
  ProjectSummaryDto,
  ProjectSettingsDocumentDto,
  ProjectShellDto,
  ProjectTagQuery,
  ProjectTagRowDto,
  ProjectTreeNodeDto,
  ProjectPathMatchDto,} from './types'

function responseRevision(response: AxiosResponse<unknown>): string {
  return String(response.headers?.etag ?? response.headers?.ETag ?? '').trim()
}

export function createProjectApi(client: AxiosInstance) {
  let activeRevision = ''

  function captureRevision(response: AxiosResponse<unknown>) {
    const revision = responseRevision(response)
    if (!revision) return
    activeRevision = revision
    setActiveProjectRevision(revision)
  }

  return {
    revision: () => activeRevision,

    async summary(signal?: AbortSignal) {
      const response = await client.get<ProjectSummaryDto>('/api/v1/project/summary', { signal })
      captureRevision(response)
      return response.data
    },

    async settings(signal?: AbortSignal) {
      const response = await client.get<ProjectSettingsDocumentDto>('/api/v1/project/settings', { signal })
      captureRevision(response)
      return response.data
    },

    async children(kind: ProjectNodeKind, id: string, signal?: AbortSignal) {
      const response = await client.get<ProjectTreeNodeDto[]>(
        `/api/v1/project/tree/${encodeURIComponent(kind)}/${encodeURIComponent(id)}/children`,
        { signal },
      )
      captureRevision(response)
      return response.data
    },

    /** 按完整路径解析节点 ID(监控中心把配错位置的点位挪走时要用)。 */
    async resolvePaths(paths: string[], signal?: AbortSignal) {
      const response = await client.post<ProjectPathMatchDto[]>('/api/v1/project/resolve-paths', { paths }, { signal })
      return response.data
    },

    /** 大工程骨架：通道、设备（不含组与点位）+ 各设备的下级数量。 */
    async shell(signal?: AbortSignal) {
      const response = await client.get<ProjectShellDto>('/api/v1/project/shell', { signal, timeout: 120_000 })
      captureRevision(response)
      return { ...response.data, rev: responseRevision(response) }
    },

    /** 设备或组的直接下级：子组表头 + 本层点位；deep=true 时为完整子树。 */
    async lazyChildren(kind: 'device' | 'group', id: string, deep = false, signal?: AbortSignal) {
      const response = await client.get<ProjectLazyChildrenDto>(
        `/api/v1/project/children/${kind}/${encodeURIComponent(id)}`,
        { params: deep ? { deep: true } : undefined, signal, timeout: 120_000 },
      )
      captureRevision(response)
      return { ...response.data, rev: responseRevision(response) }
    },

    async tags(query: ProjectTagQuery, signal?: AbortSignal) {
      const response = await client.get<ProjectPageDto<ProjectTagRowDto>>('/api/v1/project/tags', {
        params: query,
        signal,
      })
      captureRevision(response)
      return response.data
    },

    async mesTasks(query: ProjectMesTaskQuery, signal?: AbortSignal) {
      const response = await client.get<ProjectPageDto<ProjectMesTaskRowDto>>('/api/v1/project/mes-tasks', {
        params: query,
        signal,
      })
      captureRevision(response)
      return response.data
    },

    async mesTask(id: string, signal?: AbortSignal) {
      const response = await client.get<ProjectMesTaskDocumentDto>(
        `/api/v1/project/mes-tasks/${encodeURIComponent(id)}`,
        { signal },
      )
      captureRevision(response)
      return response.data
    },

    async entity(kind: ProjectNodeKind, id: string, signal?: AbortSignal) {
      const response = await client.get<ProjectEntityDocumentDto>(
        `/api/v1/project/entity/${encodeURIComponent(kind)}/${encodeURIComponent(id)}`,
        { signal },
      )
      captureRevision(response)
      return response.data
    },

    async mutate(command: ProjectMutationRequest, revision = activeRevision) {
      const response = await client.post<ProjectMutationResponse>('/api/v1/project/mutations', command, {
        headers: { 'If-Match': revision },
        // 大工程保存后服务端要重建运行时，不能沿用默认 8 秒超时。
        timeout: 600_000,
      })
      captureRevision(response)
      return response.data
    },

    async inspectImport(file: File) {
      const form = new FormData()
      form.append('file', file)
      const response = await client.post<ProjectImportPreviewDto>('/api/v1/project/import/inspect', form, { timeout: 120_000 })
      return response.data
    },

    async commitImport(file: File, token: string, fileHash: string, migrationPassword?: string) {
      const form = new FormData()
      form.append('file', file)
      form.append('token', token)
      form.append('fileHash', fileHash)
      if (migrationPassword) form.append('migrationPassword', migrationPassword)
      const response = await client.post<ProjectImportResultDto>('/api/v1/project/import/commit', form, { timeout: 600_000 })
      captureRevision(response)
      return response.data
    },

    exportUrl(options: ProjectExportOptions) {
      const params = new URLSearchParams({
        includePortableSecrets: String(options.includePortableSecrets),
      })
      return withAccessToken(`/api/v1/project/export?${params.toString()}`)
    },
  }
}

export type ProjectApi = ReturnType<typeof createProjectApi>
export const projectApi = createProjectApi(http)
