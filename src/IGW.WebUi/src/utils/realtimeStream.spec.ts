import { afterEach, expect, it, vi } from 'vitest'
import { RealtimeStreamClient } from './realtimeStream'
import { RealtimeRpcError } from './signalrTransport'
import type { RealtimeFrame } from './realtimeFrame'
const request = { protocolVersion: 1 as const, clientId: 'qc', projectRevision: 'r', tagIds: ['A'], topics: [], includeSnapshot: true }
const hello = { event: 'hello', id: 's:1', data: JSON.stringify({ streamId: 's', projectRevision: 'r' }) }
afterEach(() => vi.useRealTimers())
it('dispatches native SignalR envelopes in order', async () => {
  const received: string[] = []
  const frames = vi.fn(async function* () { yield hello; yield { event: 'tag-delta', id: 's:2', data: '{"values":[]}' } })
  const client = new RealtimeStreamClient({ frames, onFrame: frame => received.push(frame.event), jitter: () => 1 })
  client.start(request)
  await vi.waitFor(() => expect(received).toEqual(['hello', 'tag-delta']))
  expect(frames).toHaveBeenCalledOnce()
  client.stop()
})
it.each([[400, 'configuration-error'], [401, 'unauthorized'], [403, 'unauthorized'], [409, 'project-changed']] as const)(
  'treats status %i as terminal %s', async (status, state) => {
    const frames = vi.fn(async function* (): AsyncGenerator<RealtimeFrame> { throw new RealtimeRpcError(status, 'denied') })
    const client = new RealtimeStreamClient({ frames, jitter: () => 1 })
    client.start(request)
    await vi.waitFor(() => expect(client.state).toBe(state))
    expect(frames).toHaveBeenCalledOnce()
    client.stop()
  })
it.each([[503, 1000], [429, 30000]])('backs off status %i for %i ms', async (status, delay) => {
  vi.useFakeTimers()
  const frames = vi.fn(async function* (): AsyncGenerator<RealtimeFrame> { throw new RealtimeRpcError(status, 'project_reloading') })
  const onError = vi.fn()
  const client = new RealtimeStreamClient({ frames, onError, jitter: () => 1 })
  client.start(request)
  await vi.advanceTimersByTimeAsync(0)
  if (status === 503) expect(onError).toHaveBeenCalledWith(expect.objectContaining({ status: 503, code: 'project_reloading' }))
  await vi.advanceTimersByTimeAsync(delay - 1)
  expect(frames).toHaveBeenCalledOnce()
  await vi.advanceTimersByTimeAsync(1)
  expect(frames).toHaveBeenCalledTimes(2)
  client.stop()
})
it('stops reconnecting when the runtime stream is unavailable', async () => {
  vi.useFakeTimers()
  const frames = vi.fn(async function* (): AsyncGenerator<RealtimeFrame> {
    throw new RealtimeRpcError(503, 'realtime_stream_unavailable')
  })
  const onError = vi.fn()
  const client = new RealtimeStreamClient({ frames, onError, jitter: () => 1 })

  client.start(request)
  await vi.advanceTimersByTimeAsync(0)

  expect(client.state).toBe('runtime-unavailable')
  expect(onError).not.toHaveBeenCalled()
  await vi.advanceTimersByTimeAsync(120_000)
  expect(frames).toHaveBeenCalledOnce()
  client.stop()
})
it('closes a sequence-gapped stream before resynchronizing', async () => {
  let closed = false
  const states: string[] = []
  const frames = async function* () { try { yield hello; yield { event: 'tag-delta', id: 's:3', data: '{}' } } finally { closed = true } }
  const client = new RealtimeStreamClient({ frames, onState: state => states.push(state), jitter: () => 1 })
  client.start(request)
  await vi.waitFor(() => expect(states).toContain('resyncing'))
  expect(closed).toBe(true)
  client.stop()
})
it('cancels stalled attempts and ignores frames after abort', async () => {
  vi.useFakeTimers()
  let cancelled = 0
  const frames = async function* (_request: unknown, signal: AbortSignal): AsyncGenerator<RealtimeFrame> {
    await new Promise<void>(resolve => signal.addEventListener('abort', () => { cancelled++; resolve() }, { once: true }))
    yield hello
  }
  const onFrame = vi.fn()
  const client = new RealtimeStreamClient({ frames, onFrame, jitter: () => 1, staleAfterMs: 100 })
  client.start(request)
  await vi.advanceTimersByTimeAsync(101)
  expect(cancelled).toBe(1)
  expect(client.state).toBe('stale')
  client.stop()
  await vi.advanceTimersByTimeAsync(5000)
  expect(onFrame).not.toHaveBeenCalled()
})
