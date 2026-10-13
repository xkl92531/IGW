import { beforeEach, describe, expect, it, vi } from 'vitest'

const axiosMock = vi.hoisted(() => ({
  get: vi.fn(() => Promise.resolve({ data: { name: 'P' }, headers: { 'x-igw-rev': 'rev-2' } })),
  post: vi.fn(() => Promise.resolve({ data: { quality: 'Good' } })),
  put: vi.fn(() => Promise.resolve({ data: { quality: 'Good' } })),
}))
const realtimeMock = vi.hoisted(() => ({ request: vi.fn(async () => ({ quality: 'Good' })) }))
vi.mock('./utils/signalrTransport', () => ({ realtimeTransport: realtimeMock }))

vi.mock('axios', () => ({
  default: {
    create: () => ({
      get: axiosMock.get,
      post: axiosMock.post,
      put: axiosMock.put,
      interceptors: {
        request: { use: vi.fn() },
        response: { use: vi.fn() },
      },
    }),
  },
}))

import { api, setActiveProjectRevision } from './api'

describe('PLC write project revision precondition', () => {
  beforeEach(() => {
    setActiveProjectRevision('')
    axiosMock.get.mockClear()
    axiosMock.post.mockClear()
    axiosMock.put.mockClear()
    realtimeMock.request.mockClear()
  })

  it('adds the revision learned from project read to scalar and array writes', async () => {
    await api.project()
    await api.write('Line.Plc.Tag', 7)
    await api.writeArraySlice('Line.Plc.Array', [0], [1], [7])

    expect(realtimeMock.request).toHaveBeenCalledWith(
      'write', { id: 'Line.Plc.Tag', value: 7 }, 'rev-2', undefined,
    )
    expect(realtimeMock.request).toHaveBeenCalledWith(
      'writeArraySlice', { id: 'Line.Plc.Array', start: [0], length: [1], values: [7] }, 'rev-2', undefined,
    )
    expect(axiosMock.post).not.toHaveBeenCalled()
    expect(axiosMock.put).not.toHaveBeenCalled()
  })

  it('passes an empty revision before a project revision is known', async () => {
    await api.write('Line.Plc.Tag', 1)

    expect(realtimeMock.request).toHaveBeenCalledWith(
      'write', { id: 'Line.Plc.Tag', value: 1 }, '', undefined,
    )
    expect(axiosMock.post).not.toHaveBeenCalled()
  })
})
