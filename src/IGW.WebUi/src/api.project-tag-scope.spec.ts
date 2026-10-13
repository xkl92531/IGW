import { beforeEach, describe, expect, it, vi } from 'vitest'

const axiosMock = vi.hoisted(() => ({
  get: vi.fn(),
}))

vi.mock('axios', () => ({
  default: {
    create: () => ({
      get: axiosMock.get,
      interceptors: {
        request: { use: vi.fn() },
        response: { use: vi.fn() },
      },
    }),
  },
}))

import { api } from './api'

describe('project tag scope compatibility', () => {
  beforeEach(() => axiosMock.get.mockReset())

  it('uses projectRevision for realtime streams when an older runtime omits realtimeRevision', async () => {
    axiosMock.get.mockResolvedValue({
      data: {
        projectRevision: 'legacy-project-revision',
        root: 'Line.PLC.Group',
        groups: [],
        definitions: [{
          id: 'Line.PLC.Group.Value',
          name: 'Value',
          address: 'RAMP',
          dataType: 'Int16',
          access: 'ReadWrite',
        }],
        nextCursor: null,
        total: 1,
      },
    })

    const scope = await api.projectTagScope({ root: 'Line.PLC.Group', limit: 1_000 })

    expect(scope.realtimeRevision).toBe('legacy-project-revision')
  })
})
