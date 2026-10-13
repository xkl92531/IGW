import { beforeEach, describe, expect, it, vi } from 'vitest'

const axiosMock = vi.hoisted(() => ({
  post: vi.fn(() => Promise.resolve({ data: { quality: 'Good' } })),
}))
const realtimeMock = vi.hoisted(() => ({ request: vi.fn(async () => ({ quality: 'Good' })) }))
vi.mock('./utils/signalrTransport', () => ({ realtimeTransport: realtimeMock }))

vi.mock('axios', () => ({
  default: {
    create: () => ({
      post: axiosMock.post,
      interceptors: {
        request: { use: vi.fn() },
        response: { use: vi.fn() },
      },
    }),
  },
}))

import { api, setActiveProjectRevision } from './api'

describe('QC write request', () => {
  beforeEach(() => {
    axiosMock.post.mockClear()
    realtimeMock.request.mockReset().mockResolvedValue({ quality: 'Good' })
    setActiveProjectRevision('')
  })

  it('sends the exact scalar target and value through the shared realtime transport', async () => {
    expect(await api.write('PLC8.PLC8.OP130.StationEntrance.MESFlag', 1)).toEqual({ quality: 'Good' })
    expect(realtimeMock.request).toHaveBeenCalledWith(
      'write', { id: 'PLC8.PLC8.OP130.StationEntrance.MESFlag', value: 1 }, '', undefined,
    )
    expect(axiosMock.post).not.toHaveBeenCalled()
  })

  it('propagates an uncertain write result without retrying or falling back to HTTP', async () => {
    const failure = new Error('connection closed before reply')
    realtimeMock.request.mockRejectedValueOnce(failure)
    await expect(api.write('Line.Plc.Tag', 1)).rejects.toBe(failure)
    expect(realtimeMock.request).toHaveBeenCalledOnce()
    expect(axiosMock.post).not.toHaveBeenCalled()
  })
})
