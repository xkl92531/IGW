export interface ConnectionDiagnosticItem {
  device: string
  channel: string
  protocol: string
  code: string
  category: string
  scope: string
  state: string
  availability?: string
  cause: string
  suggestion: string
  rawError: string
  firstOccurredAt: string
  lastOccurredAt: string
  recoveredAt?: string | null
  occurrenceCount: number
  retrying: boolean
  nextRetryMs: number
  active: boolean
}

export interface ConnectionDiagnosticsSnapshot {
  active: ConnectionDiagnosticItem[]
  recovered: ConnectionDiagnosticItem[]
}

export interface ConnectionDiagnosticFilter {
  state?: 'all' | 'active' | 'recovered'
  query?: string
  category?: string
  protocol?: string
  channels?: Set<string>
}

function values(source?: ConnectionDiagnosticsSnapshot | null): ConnectionDiagnosticItem[] {
  return [...(source?.active || []), ...(source?.recovered || [])]
}

export function filterConnectionDiagnostics(
  source: ConnectionDiagnosticsSnapshot | null | undefined,
  filter: ConnectionDiagnosticFilter,
): ConnectionDiagnosticItem[] {
  const state = filter.state || 'all'
  const keyword = (filter.query || '').trim().toLocaleLowerCase('zh-CN')
  const channels = filter.channels || new Set<string>()
  return values(source)
    .filter((item) => state === 'all' || (state === 'active' ? item.active : !item.active))
    .filter((item) => !filter.category || item.code === filter.category)
    .filter((item) => !filter.protocol || item.protocol === filter.protocol)
    .filter((item) => channels.size === 0 || channels.has(item.channel))
    .filter((item) => !keyword || [
      item.device, item.channel, item.protocol, item.code, item.category,
      item.cause, item.suggestion, item.rawError,
    ].some((value) => String(value || '').toLocaleLowerCase('zh-CN').includes(keyword)))
    .sort((left, right) => {
      if (left.active !== right.active) return left.active ? -1 : 1
      return Date.parse(right.lastOccurredAt || '') - Date.parse(left.lastOccurredAt || '')
    })
}

export function connectionDiagnosticOptions(source?: ConnectionDiagnosticsSnapshot | null) {
  const items = values(source)
  const categoryMap = new Map<string, string>()
  for (const item of items)
    if (item.code && !categoryMap.has(item.code)) categoryMap.set(item.code, item.category || item.code)
  return {
    categories: [...categoryMap.entries()]
      .map(([value, label]) => ({ value, label }))
      .sort((left, right) => left.value.localeCompare(right.value)),
    protocols: [...new Set(items.map((item) => item.protocol).filter(Boolean))].sort((a, b) => a.localeCompare(b)),
  }
}
