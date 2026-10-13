export interface RuntimeLogFilters {
  level: string
  search: string
  date: string | null
  limit: number
}

export type RuntimeLogRefreshMode = 'initial' | 'manual' | 'silent'

export interface RuntimeLogViewportSnapshot {
  scrollTop: number
  scrollLeft: number
}

interface RuntimeLogScrollHost {
  scrollTop: number
  scrollLeft: number
}

interface RuntimeLogTableRoot {
  querySelector(selector: string): RuntimeLogScrollHost | null
}

export function levelPresentation(level: string): { type: 'danger' | 'warning' | 'info'; label: string } {
  if (level === 'ERR' || level === 'CRT') return { type: 'danger', label: '错误' }
  if (level === 'WRN') return { type: 'warning', label: '警告' }
  return { type: 'info', label: '信息' }
}

export function runtimeLogQueryParams(filters: RuntimeLogFilters): Record<string, string | number> {
  const result: Record<string, string | number> = { limit: Math.max(1, Math.min(2000, filters.limit || 300)) }
  const level = filters.level.trim()
  const search = filters.search.trim()
  const date = (filters.date ?? '').trim()
  if (level) result.level = level
  if (search) result.search = search
  if (date) result.date = date
  return result
}

export function runtimeLogRefreshPolicy(mode: RuntimeLogRefreshMode) {
  const silent = mode === 'silent'
  return {
    showLoading: !silent,
    showError: !silent,
    preserveViewport: silent,
  }
}

export function runtimeLogRetentionDraft(currentDraft: number, serverValue: number, editing: boolean) {
  return editing ? currentDraft : serverValue
}

export function captureRuntimeLogViewport(root: RuntimeLogTableRoot | null | undefined): RuntimeLogViewportSnapshot | null {
  const host = root?.querySelector('.el-scrollbar__wrap')
  return host ? { scrollTop: host.scrollTop, scrollLeft: host.scrollLeft } : null
}

export function restoreRuntimeLogViewport(
  root: RuntimeLogTableRoot | null | undefined,
  snapshot: RuntimeLogViewportSnapshot | null,
) {
  if (!snapshot) return
  const host = root?.querySelector('.el-scrollbar__wrap')
  if (!host) return
  host.scrollTop = snapshot.scrollTop
  host.scrollLeft = snapshot.scrollLeft
}
