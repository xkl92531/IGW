import type { RealtimeConnectionState } from '../stores/realtime'
import type { RealtimeFrame } from './realtimeFrame'
import { realtimeTransport, RealtimeRpcError } from './signalrTransport'

export interface RealtimeStreamRequest {
  protocolVersion: 1
  clientId: string
  projectRevision: string
  tagIds: string[]
  topics: string[]
  includeSnapshot: boolean
  heartbeatSeconds?: number
}

export interface RealtimeStreamClientOptions {
  frames?: (request: RealtimeStreamRequest, signal: AbortSignal) => AsyncIterable<RealtimeFrame>
  onFrame?: (frame: RealtimeFrame) => void
  onState?: (state: RealtimeConnectionState) => void
  onError?: (error: unknown) => void
  jitter?: () => number
  staleAfterMs?: number
}

const RETRY_MS = [1_000, 2_000, 5_000, 10_000, 30_000]
const CAPACITY_RETRY_MS = 30_000
const DEFAULT_STALE_AFTER_MS = 20_000

class TerminalStreamError extends Error {
  constructor(public state: RealtimeConnectionState) { super(state) }
}

class CapacityStreamError extends Error {
  constructor() { super('capacity-limited') }
}

export class RealtimeHttpError extends Error {
  constructor(public readonly status: number, public readonly code?: string) {
    super(`realtime_http_${status}${code ? `: ${code}` : ''}`)
  }
}

export class RealtimeStreamClient {
  private readonly frames: (request: RealtimeStreamRequest, signal: AbortSignal) => AsyncIterable<RealtimeFrame>
  private readonly onFrame: (frame: RealtimeFrame) => void
  private readonly onState: (state: RealtimeConnectionState) => void
  private readonly onError: (error: unknown) => void
  private readonly jitter: () => number
  private readonly staleAfterMs: number
  private generation = 0
  private controller: AbortController | null = null
  private retryTimer: ReturnType<typeof setTimeout> | null = null
  private retryResolve: (() => void) | null = null
  private staleTimer: ReturnType<typeof setTimeout> | null = null
  private currentRequest: RealtimeStreamRequest | null = null
  state: RealtimeConnectionState = 'idle'

  constructor(options: RealtimeStreamClientOptions = {}) {
    this.frames = options.frames ?? ((request, signal) => realtimeTransport.frames(request, signal))
    this.onFrame = options.onFrame ?? (() => {})
    this.onState = options.onState ?? (() => {})
    this.onError = options.onError ?? (() => {})
    this.jitter = options.jitter ?? (() => 0.8 + Math.random() * 0.4)
    this.staleAfterMs = options.staleAfterMs ?? DEFAULT_STALE_AFTER_MS
  }

  start(request: RealtimeStreamRequest): void {
    this.stopInternal(false)
    this.currentRequest = { ...request, tagIds: [...request.tagIds], topics: [...request.topics] }
    const generation = ++this.generation
    this.setState('connecting')
    void this.run(generation, this.currentRequest)
  }

  requestSnapshot(): void {
    if (!this.currentRequest) return
    const request = { ...this.currentRequest, includeSnapshot: true }
    this.stopInternal(false)
    this.currentRequest = request
    const generation = ++this.generation
    this.setState('resyncing')
    void this.run(generation, request)
  }

  stop(): void {
    this.stopInternal(true)
    this.setState('idle')
  }

  private stopInternal(clearRequest: boolean) {
    this.generation++
    this.controller?.abort()
    this.controller = null
    if (this.retryTimer) clearTimeout(this.retryTimer)
    this.retryTimer = null
    this.retryResolve?.()
    this.retryResolve = null
    this.clearStaleTimer()
    if (clearRequest) this.currentRequest = null
  }

  private async run(generation: number, request: RealtimeStreamRequest) {
    let retry = 0
    while (generation === this.generation) {
      let capacityLimited = false
      try {
        this.armStaleTimer(generation)
        await this.connectOnce(generation, request, () => { retry = 0 })
        if (generation !== this.generation) return
      } catch (error) {
        if (generation !== this.generation) return
        if (error instanceof RealtimeRpcError) {
          if (error.status === 409) error = new TerminalStreamError('project-changed')
          else if (error.status === 400) error = new TerminalStreamError('configuration-error')
          else if (error.status === 401 || error.status === 403) error = new TerminalStreamError('unauthorized')
          else if (error.status === 503 && error.code === 'realtime_stream_unavailable')
            error = new TerminalStreamError('runtime-unavailable')
          else if (error.status === 429) error = new CapacityStreamError()
        }
        if (error instanceof TerminalStreamError) {
          this.clearStaleTimer()
          this.setState(error.state)
          return
        }
        if (error instanceof CapacityStreamError) {
          capacityLimited = true
          this.setState('capacity-limited')
        } else if (!isAbortError(error) || this.state !== 'stale') this.onError(error)
      }

      if (generation !== this.generation) return
      if (!capacityLimited) this.setState(this.state === 'stale' ? 'stale' : 'reconnecting')
      const baseDelay = capacityLimited
        ? CAPACITY_RETRY_MS
        : RETRY_MS[Math.min(retry++, RETRY_MS.length - 1)]
      const delay = Math.round(baseDelay * this.jitter())
      await this.waitForRetry(delay, generation)
    }
  }

  private async connectOnce(generation: number, request: RealtimeStreamRequest, onLive: () => void) {
    const controller = new AbortController()
    this.controller = controller
    let completedNaturally = false
    try {
      let streamId = ''
      let lastSequence = 0
      for await (const frame of this.frames(request, controller.signal)) {
        if (generation !== this.generation || controller.signal.aborted) return
        this.armStaleTimer(generation)
        const identity = parseIdentity(frame.id)
        if (!identity) throw new Error('invalid_event_id')
        if (!streamId) {
          if (frame.event !== 'hello' || identity.sequence !== 1) throw new Error('hello_must_be_first')
          streamId = identity.streamId
        }
        if (identity.streamId !== streamId) continue
        if (identity.sequence <= lastSequence) continue
        if (lastSequence > 0 && identity.sequence !== lastSequence + 1) {
          this.setState('resyncing')
          throw new Error('event_sequence_gap')
        }
        lastSequence = identity.sequence

        if (frame.event === 'hello') {
          const hello = JSON.parse(frame.data)
          if (hello.streamId !== streamId || hello.projectRevision !== request.projectRevision)
            throw new TerminalStreamError('project-changed')
          onLive()
          this.setState('live')
        }
        this.onFrame(frame)
        if (frame.event === 'project-changed') throw new TerminalStreamError('project-changed')
        if (frame.event === 'reset-required') {
          this.setState('resyncing')
          throw new Error('reset_required')
        }
      }
      completedNaturally = true
    } finally {
      if (!completedNaturally && !controller.signal.aborted) controller.abort()
      if (this.controller === controller) this.controller = null
    }
  }

  private armStaleTimer(generation: number) {
    this.clearStaleTimer()
    this.staleTimer = setTimeout(() => {
      if (generation !== this.generation) return
      this.setState('stale')
      this.controller?.abort()
    }, this.staleAfterMs)
  }

  private clearStaleTimer() {
    if (this.staleTimer) clearTimeout(this.staleTimer)
    this.staleTimer = null
  }

  private waitForRetry(milliseconds: number, generation: number) {
    return new Promise<void>((resolve) => {
      const finish = () => {
        if (this.retryTimer) clearTimeout(this.retryTimer)
        this.retryTimer = null
        this.retryResolve = null
        resolve()
      }
      this.retryResolve = finish
      this.retryTimer = setTimeout(() => {
        finish()
      }, milliseconds)
      if (generation !== this.generation) {
        finish()
      }
    })
  }

  private setState(state: RealtimeConnectionState) {
    if (this.state === state) return
    this.state = state
    this.onState(state)
  }
}

function isAbortError(error: unknown): boolean {
  return typeof error === 'object' && error !== null && 'name' in error && error.name === 'AbortError'
}

function parseIdentity(id: string): { streamId: string, sequence: number } | null {
  const separator = id.lastIndexOf(':')
  if (separator <= 0) return null
  const streamId = id.slice(0, separator)
  const sequence = Number(id.slice(separator + 1))
  return Number.isSafeInteger(sequence) && sequence > 0 ? { streamId, sequence } : null
}
