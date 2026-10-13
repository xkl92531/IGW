import type { TagScanTimingDto } from '../api'

export function visibleTimingIds(rows: { itemId: string }[], start: number, end: number): string[] {
  if (start < 0 || end < start) return []
  const from = Math.max(0, start - 5)
  return [...new Set(rows.slice(from, Math.min(end + 6, from + 200)).map(row => row.itemId))]
}

export function timingLabel(item?: TagScanTimingDto): string {
  if (item?.mode === 'subscription') return '订阅推送'
  return item?.batch ? `${item.batch.totalMs.toFixed(2)} ms` : '—'
}

/** One bounded viewport request at a time, with cancellation and generation isolation. */
export class QuickClientTiming {
  private generation = 0
  private timer?: ReturnType<typeof setTimeout>
  private controller?: AbortController
  private ids: string[] = []
  private key = ''
  private enabled = false
  private disposed = false
  private running = false
  constructor(
    private readonly fetcher: (ids: string[], signal: AbortSignal) => Promise<{ items: TagScanTimingDto[] }>,
    private readonly update: (items: TagScanTimingDto[], failed: boolean) => void,
  ) {}
  set(ids: string[], enabled: boolean) {
    if (this.disposed) return
    const bounded = [...new Set(ids)].slice(0, 200)
    const key = JSON.stringify(bounded)
    if (key === this.key && enabled === this.enabled) return
    this.ids = bounded; this.key = key; this.enabled = enabled
    ++this.generation
    clearTimeout(this.timer); this.controller?.abort()
    this.update([], false)
    if (enabled && bounded.length) this.schedule(150)
  }
  private schedule(delay: number) {
    clearTimeout(this.timer)
    this.timer = setTimeout(() => void this.run(this.generation), delay)
  }
  private async run(generation: number) {
    if (this.running || this.disposed || !this.enabled || !this.ids.length) return
    this.running = true
    const controller = new AbortController()
    this.controller = controller
    try {
      const result = await this.fetcher([...this.ids], controller.signal)
      if (generation === this.generation && !this.disposed) this.update(result.items, false)
    } catch {
      if (generation === this.generation && !this.disposed) this.update([], true)
    } finally {
      this.running = false
      if (this.enabled && this.ids.length && !this.disposed)
        this.schedule(generation === this.generation ? 2000 : 150)
    }
  }
  dispose() {
    this.disposed = true; ++this.generation
    clearTimeout(this.timer); this.controller?.abort()
  }
}
