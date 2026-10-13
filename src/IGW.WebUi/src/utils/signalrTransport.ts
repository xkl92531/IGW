import { HubConnectionBuilder, HubConnectionState, HttpTransportType, LogLevel, type HubConnection } from '@microsoft/signalr'
import type { RealtimeStreamRequest } from './realtimeStream'
import type { RealtimeFrame } from './realtimeFrame'
import { getSessionToken } from './sessionToken'

export class RealtimeRpcError extends Error {
  readonly response: { status: number; data: Record<string, unknown> }
  constructor(readonly status: number, readonly code: string, data?: Record<string, unknown>) {
    super(String(data?.error || code))
    this.response = { status, data: data || { error: code } }
  }
}
export function normalizeRealtimeError(error: unknown): Error {
  if (error instanceof RealtimeRpcError) return error
  if (typeof error === 'object' && error !== null && 'name' in error && error.name === 'AbortError')
    return error as Error
  const match = /igw:(\d{3}):([\w-]+)(?::([A-Za-z0-9+/=]+))?/.exec(String((error as any)?.message || error))
  if (!match) return error instanceof Error ? error : new Error(String(error))
  let data: Record<string, unknown> | undefined
  if (match[3]) {
    try { data = JSON.parse(new TextDecoder().decode(Uint8Array.from(atob(match[3]), char => char.charCodeAt(0)))) } catch { /* status/code still available */ }
  }
  return new RealtimeRpcError(Number(match[1]), match[2], data)
}
const auth = () => typeof localStorage === 'undefined' ? '' : localStorage.getItem('igw_auth') || ''
const aborted = () => new DOMException('Aborted', 'AbortError')

// 网页登录凭证随连接带上(?access_token=)：网关按账号判断能不能写 PLC 值(「只能看」的账号不能写)。
export function createRealtimeConnection(sessionToken: string): HubConnection {
  return new HubConnectionBuilder()
    .withUrl('/hubs/igw-realtime', { transport: HttpTransportType.WebSockets, skipNegotiation: true, accessTokenFactory: () => sessionToken })
    .configureLogging(LogLevel.Error)
    .build()
}

/** One physical connection per browser context; independent streams own cancellable leases. */
interface ConnectionEntry {
  connection: HubConnection
  /** 建连接时的网页登录凭证；换账号登录后空闲的旧连接要重建。 */
  sessionToken: string
  starting?: Promise<HubConnection>
  users: number
  idleTimer?: ReturnType<typeof setTimeout>
}
interface ConnectionLease {
  ready: Promise<HubConnection>
  release(): void
}
export class SignalrTransport {
  private entry?: ConnectionEntry
  constructor(private readonly factory: (sessionToken: string) => HubConnection = createRealtimeConnection) {}

  private acquire(): ConnectionLease {
    const sessionToken = getSessionToken()
    if (this.entry && !this.entry.users && this.entry.sessionToken !== sessionToken) void this.disposeEntry(this.entry).catch(() => {})
    const entry = this.entry ??= { connection: this.factory(sessionToken), sessionToken, users: 0 }
    entry.users++
    clearTimeout(entry.idleTimer)
    if (entry.connection.state !== HubConnectionState.Connected && !entry.starting) {
      entry.starting = Promise.resolve().then(() => entry.connection.start())
        .then(() => entry.connection).finally(() => { entry.starting = undefined })
    }
    let released = false
    return {
      ready: entry.starting ?? Promise.resolve(entry.connection),
      release: () => {
        if (released) return
        released = true
        entry.users--
        if (!entry.users) entry.idleTimer = setTimeout(() => {
          if (!entry.users) void this.disposeEntry(entry).catch(() => {})
        }, 15_000)
      },
    }
  }
  async request<T = any>(operation: string, args: unknown = {}, revision = '', signal?: AbortSignal): Promise<T> {
    if (signal?.aborted) throw aborted()
    let timer: ReturnType<typeof setTimeout> | undefined
    let onAbort: (() => void) | undefined
    let lease: ConnectionLease | undefined
    let invocation: Promise<T> | undefined
    let cancelled: Error | undefined
    try {
      const cancellation = new Promise<never>((_, reject) => {
        const cancel = (error: Error) => { cancelled = error; reject(error) }
        onAbort = () => cancel(aborted())
        signal?.addEventListener('abort', onAbort, { once: true })
        timer = setTimeout(() => cancel(new RealtimeRpcError(504, 'realtime_request_timeout')), 30_000)
      })
      lease = this.acquire()
      const connection = await Promise.race([lease.ready, cancellation])
      if (cancelled) throw cancelled
      // Never send an expired operation or replay an invocation after a connection error.
      invocation = connection.invoke<T>('Request', auth(), operation, args, revision)
      // A sent request still owns the connection until its actual reply/error, even if
      // its caller stops waiting. Releasing early can stop other pending driver work.
      void invocation.then(lease.release, lease.release)
      return await Promise.race([invocation, cancellation])
    } catch (error) { throw normalizeRealtimeError(error) }
    finally {
      clearTimeout(timer)
      if (onAbort) signal?.removeEventListener('abort', onAbort)
      if (!invocation) lease?.release()
    }
  }

  async *frames(request: RealtimeStreamRequest, signal: AbortSignal): AsyncGenerator<RealtimeFrame> {
    if (signal.aborted) return
    const queue: RealtimeFrame[] = []
    let bytes = 0
    let done = false
    let failure: unknown
    let wake: (() => void) | undefined
    let subscription: { dispose(): void } | undefined
    let lease: ConnectionLease | undefined
    let resolveCancellation!: () => void
    const cancellation = new Promise<undefined>(resolve => { resolveCancellation = () => resolve(undefined) })
    const disposeSubscription = () => { const current = subscription; subscription = undefined; current?.dispose() }
    const cancel = () => { done = true; disposeSubscription(); resolveCancellation(); wake?.() }
    signal.addEventListener('abort', cancel, { once: true })
    try {
      lease = this.acquire()
      const connection = await Promise.race([lease.ready, cancellation])
      if (!connection || signal.aborted) return
      subscription = connection.stream<RealtimeFrame>('Stream', auth(), request).subscribe({
        next: frame => {
          if (done || signal.aborted) return
          // Bound browser ingress; a slow renderer resynchronizes instead of growing without limit.
          if (queue.length >= 512 || bytes + frame.data.length * 2 > 16 * 1024 * 1024) {
            failure = new Error('realtime_receive_overflow')
            cancel()
            return
          }
          queue.push(frame); bytes += frame.data.length * 2; wake?.()
        },
        error: error => { failure = normalizeRealtimeError(error); done = true; wake?.() },
        complete: () => { done = true; wake?.() },
      })
      while (!signal.aborted) {
        if (failure) throw failure
        const frame = queue.shift()
        if (frame) { bytes -= frame.data.length * 2; yield frame; continue }
        if (done) break
        await new Promise<void>(resolve => { wake = resolve })
        wake = undefined
      }
    } finally {
      signal.removeEventListener('abort', cancel)
      disposeSubscription()
      lease?.release()
    }
  }
  async dispose() {
    if (this.entry) await this.disposeEntry(this.entry)
  }
  private async disposeEntry(entry: ConnectionEntry) {
    clearTimeout(entry.idleTimer)
    if (this.entry === entry) this.entry = undefined
    await entry.connection.stop()
  }
}
export const realtimeTransport = new SignalrTransport()
