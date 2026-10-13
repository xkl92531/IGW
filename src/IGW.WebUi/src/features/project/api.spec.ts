import type { AxiosInstance } from 'axios'
import { describe, expect, it, vi } from 'vitest'
import { createProjectApi } from './api'
import type { ProjectMutationRequest } from './types'

describe('projectApi', () => {
  it('passes the complete tag query and cancellation signal', async () => {
    const signal = new AbortController().signal
    const get = vi.fn().mockResolvedValue({
      data: { items: [], total: 1_000_000, offset: 200, limit: 200 },
      headers: { etag: '"project-7"' },
    })
    const client = createProjectApi({ get } as unknown as AxiosInstance)

    const page = await client.tags({ groupId: 'group-1', q: 'flag', offset: 200, limit: 200 }, signal)

    expect(get).toHaveBeenCalledWith('/api/v1/project/tags', {
      params: { groupId: 'group-1', q: 'flag', offset: 200, limit: 200 },
      signal,
    })
    expect(page.total).toBe(1_000_000)
    expect(client.revision()).toBe('"project-7"')
  })

  it('sends the complete frozen mutation with If-Match and advances revision', async () => {
    const post = vi.fn().mockResolvedValue({
      data: { applied: true, revision: '"project-8"' },
      headers: { etag: '"project-8"' },
    })
    const client = createProjectApi({ post } as unknown as AxiosInstance)
    const command: ProjectMutationRequest = {
      kind: 'moveEntities',
      entityKind: 'tag',
      entityIds: ['t1', 't2', 't3'],
      targetId: 'group-2',
      documentJson: null,
    }

    await client.mutate(command, '"project-7"')

    expect(post).toHaveBeenCalledWith('/api/v1/project/mutations', command, {
      headers: { 'If-Match': '"project-7"' },
      timeout: 600_000,
    })
    expect(post.mock.calls[0][1].entityIds).toEqual(['t1', 't2', 't3'])
    expect(client.revision()).toBe('"project-8"')
  })

  it('loads one MES task document without requesting the full project', async () => {
    const get = vi.fn().mockResolvedValue({
      data: { id: 'task-1', name: '进站', flow: { nodes: [], edges: [] } },
      headers: { etag: '"project-7"' },
    })
    const client = createProjectApi({ get } as unknown as AxiosInstance)

    const task = await client.mesTask('task-1')

    expect(task.id).toBe('task-1')
    expect(get).toHaveBeenCalledWith('/api/v1/project/mes-tasks/task-1', { signal: undefined })
    expect(get).not.toHaveBeenCalledWith('/api/v1/project', expect.anything())
    expect(client.revision()).toBe('"project-7"')
  })

  it('loads only gateway setting sections', async () => {
    const get = vi.fn().mockResolvedValue({
      data: { settings: { restPort: 48020 }, opcUa: { enabled: true, port: 4840 } },
      headers: { etag: '"project-4"' },
    })
    const client = createProjectApi({ get } as unknown as AxiosInstance)

    const settings = await client.settings()

    expect(settings.settings.restPort).toBe(48020)
    expect(get).toHaveBeenCalledWith('/api/v1/project/settings', { signal: undefined })
    expect(client.revision()).toBe('"project-4"')
  })

  it('uses multipart import and exposes export as an unbuffered browser URL', async () => {
    const post = vi.fn()
      .mockResolvedValueOnce({ data: { token: 'token-1', fileHash: 'sha256', compatibility: { issues: [] } }, headers: {} })
      .mockResolvedValueOnce({ data: { applied: true, revision: '"project-9"', compatibility: { issues: [] } }, headers: { etag: '"project-9"' } })
    const get = vi.fn()
    const client = createProjectApi({ post, get } as unknown as AxiosInstance)
    const file = new File(['{}'], 'project.json', { type: 'application/json' })

    await client.inspectImport(file)
    await client.commitImport(file, 'token-1', 'sha256', 'secret')

    expect(post.mock.calls[0][0]).toBe('/api/v1/project/import/inspect')
    expect(post.mock.calls[0][1]).toBeInstanceOf(FormData)
    expect(post.mock.calls[1][0]).toBe('/api/v1/project/import/commit')
    expect(post.mock.calls[1][1]).toBeInstanceOf(FormData)
    expect(client.exportUrl({ includePortableSecrets: true })).toBe('/api/v1/project/export?includePortableSecrets=true')
    expect(get).not.toHaveBeenCalled()
  })

  it('raises the per-request timeout for large project imports', async () => {
    const post = vi.fn()
      .mockResolvedValueOnce({ data: { token: 'token-1', fileHash: 'sha256', compatibility: { issues: [] } }, headers: {} })
      .mockResolvedValueOnce({ data: { applied: true, revision: '"project-9"', compatibility: { issues: [] } }, headers: {} })
    const client = createProjectApi({ post } as unknown as AxiosInstance)
    const file = new File(['{}'], 'project.json', { type: 'application/json' })

    await client.inspectImport(file)
    await client.commitImport(file, 'token-1', 'sha256')

    // 全局 axios 超时只有 8s；300MB 级工程 inspect 实测约 8s、commit 更久，
    // 不显式抬高会被前端提前中止，服务端随请求取消回滚导入。
    expect(post.mock.calls[0][2]?.timeout).toBeGreaterThanOrEqual(120_000)
    expect(post.mock.calls[1][2]?.timeout).toBeGreaterThanOrEqual(600_000)
  })
})
