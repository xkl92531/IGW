import { describe, expect, it, vi } from 'vitest'
import { useTagPage } from './useTagPage'
import type { ProjectPageDto, ProjectTagQuery, ProjectTagRowDto } from './types'

function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>((done) => { resolve = done })
  return { promise, resolve }
}

const page = (name: string): ProjectPageDto<ProjectTagRowDto> => ({
  items: Array.from({ length: 100 }, (_, index) => ({
    id: `${name}-${index}`, name, actualPath: `P.D.G.${name}-${index}`, address: `${index}`,
    dataType: 'int16', access: 'readWrite', scanRateMs: 100, description: null,
    engUnit: null, stringLength: null, array: null, metadata: null, rowVersion: 1,
  })),
  total: 1_000_000, offset: 0, limit: 100,
})

describe('useTagPage', () => {
  it('aborts stale searches and never keeps more than the returned page', async () => {
    vi.useFakeTimers()
    const first = deferred<ProjectPageDto<ProjectTagRowDto>>()
    const second = deferred<ProjectPageDto<ProjectTagRowDto>>()
    const requests: { query: ProjectTagQuery, signal?: AbortSignal }[] = []
    const tags = vi.fn((query: ProjectTagQuery, signal?: AbortSignal) => {
      requests.push({ query, signal })
      return query.q === 'A' ? first.promise : second.promise
    })
    const state = useTagPage({ tags }, { pageSize: 100, debounceMs: 200 })

    state.search('A')
    await vi.advanceTimersByTimeAsync(200)
    state.search('B')
    expect(requests[0].signal?.aborted).toBe(true)
    await vi.advanceTimersByTimeAsync(200)
    second.resolve(page('B'))
    await Promise.resolve()
    first.resolve(page('A'))
    await Promise.resolve()

    expect(state.items.value).toHaveLength(100)
    expect(state.items.value[0].name).toBe('B')
    expect(state.total.value).toBe(1_000_000)
    expect(requests.map(request => request.query.q)).toEqual(['A', 'B'])
    state.dispose()
    vi.useRealTimers()
  })

  it('loads device-direct tags and never leaks the previous owner filter', async () => {
    const requests: { query: ProjectTagQuery }[] = []
    const tags = vi.fn((query: ProjectTagQuery) => {
      requests.push({ query })
      return Promise.resolve(page('D'))
    })
    const state = useTagPage({ tags }, { pageSize: 100 })
    const settle = () => new Promise(done => setTimeout(done, 0))

    state.selectDevice('device-1')
    await settle()
    expect(requests[0].query).toMatchObject({ deviceId: 'device-1' })
    expect(requests[0].query.groupId).toBeUndefined()

    state.selectGroup('group-1')
    await settle()
    expect(requests[1].query).toMatchObject({ groupId: 'group-1' })
    expect(requests[1].query.deviceId).toBeUndefined()
    state.dispose()
  })
})
