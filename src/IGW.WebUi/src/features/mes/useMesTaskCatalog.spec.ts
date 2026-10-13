import { describe, expect, it, vi } from 'vitest'
import { useMesTaskCatalog } from './useMesTaskCatalog'
import type { ProjectMesTaskQuery, ProjectMesTaskRowDto } from '../project/types'

const row = (id: string, enabled: boolean, flowAvailable = true): ProjectMesTaskRowDto => ({
  id, name: id, groupId: `group-${id}`, groupRef: `Channel.Device.${id}`, deviceRef: 'Channel.Device',
  enabled, flowAvailable, rowVersion: 1,
})

describe('useMesTaskCatalog', () => {
  it('defaults to current device, switches only to current channel, and never renders disabled-flow groups', async () => {
    const queries: ProjectMesTaskQuery[] = []
    const mesTasks = vi.fn(async (query: ProjectMesTaskQuery) => {
      queries.push(query)
      return { items: [row('running', true), row('stopped', false), row('hidden', true, false)], total: 3, offset: 0, limit: 200 }
    })
    const catalog = useMesTaskCatalog({ mesTasks }, { deviceId: 'device-1', channelId: 'channel-1' })

    await catalog.load()
    expect(queries[0]).toMatchObject({ scope: 'device', deviceId: 'device-1' })
    expect(catalog.items.value.map(item => item.id)).toEqual(['running', 'stopped'])

    await catalog.setScope('channel')
    expect(queries[1]).toMatchObject({ scope: 'channel', channelId: 'channel-1' })
    expect(queries[1].deviceId).toBeUndefined()
  })

  it('sends name and running/stopped filters to the repository without mutating SSE state', async () => {
    const mesTasks = vi.fn(async (query: ProjectMesTaskQuery) => ({
      items: [row(query.enabled === false ? 'stopped' : 'running', query.enabled !== false)], total: 1, offset: 0, limit: 200,
    }))
    const runtime = new Map<string, unknown>([['running', { state: 'executing' }]])
    const catalog = useMesTaskCatalog({ mesTasks }, { deviceId: 'device-1', channelId: 'channel-1', runtime })

    catalog.search.value = 'station'
    await catalog.setState('running')

    expect(mesTasks.mock.calls[0][0]).toMatchObject({ q: 'station', enabled: true })
    expect(catalog.viewItems.value[0].runtime).toEqual({ state: 'executing' })
    expect(catalog.items.value[0].enabled).toBe(true)

    await catalog.setState('stopped')
    expect(mesTasks.mock.calls[1][0].enabled).toBe(false)
    catalog.dispose()
  })
})
