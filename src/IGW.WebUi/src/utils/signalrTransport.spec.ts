import { afterEach, expect, it, vi } from 'vitest'
import { SignalrTransport } from './signalrTransport'
import { setSessionToken } from './sessionToken'

function fakeConnection() {
  let observer: any
  const connection = {
    state: 'Disconnected',
    start: vi.fn(async () => { connection.state = 'Connected' }),
    stop: vi.fn(async () => { connection.state = 'Disconnected' }),
    invoke: vi.fn(async () => ({ good: true })),
    stream: vi.fn(() => ({ subscribe(next: any) { observer = next; return { dispose } } })),
  }
  const dispose = vi.fn()
  return { connection, dispose, send: (frame: any) => observer.next(frame) }
}
afterEach(() => { localStorage.clear(); vi.useRealTimers() })

function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>(done => { resolve = done })
  return { promise, resolve }
}

it('never sends a write when connection startup completes after the request deadline', async () => {
  vi.useFakeTimers()
  const fake = fakeConnection()
  const startup = deferred<void>()
  fake.connection.start.mockImplementation(async () => { await startup.promise; fake.connection.state = 'Connected' })
  const transport = new SignalrTransport(() => fake.connection as any)
  const result = transport.request('write', { id: 'A', value: 4 }).catch(error => error)
  await vi.advanceTimersByTimeAsync(30_000)
  expect(await result).toMatchObject({ status: 504 })
  startup.resolve()
  await vi.advanceTimersByTimeAsync(0)
  expect(fake.connection.invoke).not.toHaveBeenCalled()
  await transport.dispose()
})

it('cancels a stream during shared startup without cancelling another request', async () => {
  vi.useFakeTimers()
  const fake = fakeConnection()
  const startup = deferred<void>()
  fake.connection.start.mockImplementation(async () => { await startup.promise; fake.connection.state = 'Connected' })
  const transport = new SignalrTransport(() => fake.connection as any)
  const controller = new AbortController()
  const iterator = transport.frames({ protocolVersion: 1, clientId: 'qc', projectRevision: 'r', tagIds: [], topics: [], includeSnapshot: true }, controller.signal)
  let finished = false
  const frame = iterator.next().then(value => { finished = true; return value })
  const request = transport.request('health')
  controller.abort()
  await vi.advanceTimersByTimeAsync(0)
  expect(finished).toBe(true)
  expect(await frame).toMatchObject({ done: true })
  expect(fake.connection.stop).not.toHaveBeenCalled()
  startup.resolve()
  expect(await request).toEqual({ good: true })
  expect(fake.connection.start).toHaveBeenCalledOnce()
  expect(fake.connection.stream).not.toHaveBeenCalled()
  await transport.dispose()
})

it('holds the lease for a sent invocation after its caller times out', async () => {
  vi.useFakeTimers()
  const fake = fakeConnection()
  const reply = deferred<{ good: boolean }>()
  fake.connection.invoke.mockImplementation(() => reply.promise)
  const transport = new SignalrTransport(() => fake.connection as any)
  const result = transport.request('write', { id: 'A', value: 4 }).catch(error => error)
  await vi.advanceTimersByTimeAsync(30_000)
  expect(await result).toMatchObject({ status: 504 })
  await vi.advanceTimersByTimeAsync(15_000)
  expect(fake.connection.stop).not.toHaveBeenCalled()
  expect(fake.connection.invoke).toHaveBeenCalledOnce()
  reply.resolve({ good: true })
  await vi.advanceTimersByTimeAsync(15_000)
  expect(fake.connection.stop).toHaveBeenCalledOnce()
})

it('starts a fresh connection after an abandoned startup is disposed', async () => {
  vi.useFakeTimers()
  const old = fakeConnection()
  const fresh = fakeConnection()
  const startup = deferred<void>()
  old.connection.start.mockImplementation(async () => { await startup.promise; old.connection.state = 'Connected' })
  const factory = vi.fn().mockReturnValueOnce(old.connection).mockReturnValueOnce(fresh.connection)
  const transport = new SignalrTransport(factory)
  const controller = new AbortController()
  const abandoned = transport.request('write', { id: 'A', value: 4 }, '', controller.signal).catch(error => error)
  controller.abort()
  expect(await abandoned).toMatchObject({ name: 'AbortError' })
  await vi.advanceTimersByTimeAsync(15_000)
  expect(old.connection.stop).toHaveBeenCalledOnce()
  let completed = false
  const active = transport.request('health').then(value => { completed = true; return value })
  await vi.advanceTimersByTimeAsync(0)
  expect(completed).toBe(true)
  expect(await active).toEqual({ good: true })
  expect(fresh.connection.start).toHaveBeenCalledOnce()
  startup.resolve()
  await vi.advanceTimersByTimeAsync(0)
  expect(old.connection.invoke).not.toHaveBeenCalled()
  await transport.dispose()
})

it('shares a connection for streams and writes, passing credentials in the invocation only', async () => {
  const fake = fakeConnection()
  localStorage.setItem('igw_auth', 'test-credential')
  const transport = new SignalrTransport(() => fake.connection as any)
  const controller = new AbortController()
  const request = { protocolVersion: 1 as const, clientId: 'qc', projectRevision: 'r', tagIds: ['A'], topics: [], includeSnapshot: true }
  const iterator = transport.frames(request, controller.signal)[Symbol.asyncIterator]()
  const pending = iterator.next()
  await vi.waitFor(() => expect(fake.connection.stream).toHaveBeenCalled())
  const hello = { event: 'hello', id: 's:1', data: '{}' }
  fake.send(hello)
  expect((await pending).value).toEqual(hello)
  expect(await transport.request('write', { id: 'A', value: 4 }, 'r')).toEqual({ good: true })
  expect(fake.connection.invoke).toHaveBeenCalledWith('Request', 'test-credential', 'write', { id: 'A', value: 4 }, 'r')
  expect(fake.connection.start).toHaveBeenCalledTimes(1)
  controller.abort()
  await iterator.return?.(undefined)
  expect(fake.dispose).toHaveBeenCalledOnce()
  await transport.dispose()
})

it('preserves permission/revision errors and never retries a rejected write', async () => {
  const fake = fakeConnection()
  fake.connection.invoke.mockRejectedValue(new Error('An unexpected error: igw:409:project_changed'))
  const transport = new SignalrTransport(() => fake.connection as any)
  await expect(transport.request('write', { id: 'A', value: 4 }, 'old')).rejects.toMatchObject({ response: { status: 409, data: { error: 'project_changed' } } })
  expect(fake.connection.invoke).toHaveBeenCalledOnce()
  await transport.dispose()
})

it('connects with the web login token and reconnects after switching accounts', async () => {
  const first = fakeConnection()
  const second = fakeConnection()
  const factory = vi.fn().mockReturnValueOnce(first.connection).mockReturnValueOnce(second.connection)
  const transport = new SignalrTransport(factory)
  setSessionToken('viewer-token')
  await transport.request('values', { ids: ['A'] })
  await transport.request('values', { ids: ['A'] })
  expect(factory).toHaveBeenCalledTimes(1)
  expect(factory).toHaveBeenLastCalledWith('viewer-token')

  // 退出后换管理员登录：旧连接带的是旧账号凭证，不能继续用来写值
  setSessionToken('admin-token')
  await transport.request('write', { id: 'A', value: 1 })
  expect(first.connection.stop).toHaveBeenCalled()
  expect(factory).toHaveBeenLastCalledWith('admin-token')
  expect(second.connection.invoke).toHaveBeenCalledWith('Request', '', 'write', { id: 'A', value: 1 }, '')
  await transport.dispose()
})
