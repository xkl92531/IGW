export type LogCategory = 'all' | 'collection' | 'interface' | 'state' | 'performance' | 'system'

export interface LogFilters {
  category: LogCategory
  from: string
  to: string
  /** 协议(通道驱动 Id)、通道名、设备路径(通道.设备)：日志中心的下拉框。 */
  protocol: string
  channel: string
  device: string
  database: string
  taskId: string
  module: string
  feature: string
  task: string
  interface: string
  level: string
  search: string
  traceId: string
  /** 工程路径(通道 / 通道.设备 / 组 / 点位)：查这一项及其下级的全部日志。 */
  path: string
  /** 标记组/接口输入框；选中提示用 task(绑定任务 Id)，自由输入用 taskOrInterface 按名称或 URL 模糊查。 */
  group: string
}

export interface DeviceObservation {
  status: string
  timestamp: string
}

export function createLogFilters(overrides: Partial<LogFilters> = {}, now = Date.now()): LogFilters {
  return {
    category: 'all',
    from: new Date(now - 60 * 60 * 1000).toISOString(),
    to: new Date(now).toISOString(),
    protocol: '', channel: '', device: '', database: '', taskId: '', module: '', feature: '', task: '', interface: '',
    level: '', search: '', traceId: '', path: '', group: '',
    ...overrides,
  }
}

export function buildLogQuery(filters: LogFilters, page: number, pageSize: number, now = Date.now()) {
  const from = Date.parse(filters.from)
  const to = Date.parse(filters.to)
  if (!Number.isFinite(from) || !Number.isFinite(to) || from >= to)
    throw new Error('Invalid time range')
  const maximumTo = now + 5 * 60 * 1000
  const boundedTo = Math.min(to, maximumTo)
  const result: Record<string, string | number> = {
    category: filters.category || 'all',
    from: new Date(from).toISOString(),
    to: new Date(boundedTo).toISOString(),
    page: Math.max(1, Math.min(10_000, Math.trunc(page) || 1)),
    pageSize: Math.max(1, Math.min(200, Math.trunc(pageSize) || 50)),
  }
  const optional: Array<[string, string]> = [
    ['database', filters.database], ['taskId', filters.taskId], ['module', filters.module], ['feature', filters.feature],
    ['interface', filters.interface], ['level', filters.level], ['search', filters.search], ['traceId', filters.traceId],
  ]
  for (const [key, value] of optional) {
    const trimmed = value.trim()
    if (trimmed) result[key] = trimmed.slice(0, 500)
  }
  // 位置只发最具体的一个：标记组(按绑定的任务) > 设备 > 通道 > 地址带来的工程位置 > 协议。
  // 任务的接口调用、回写日志没有协议/设备字段，几个条件叠加会把它们筛掉；通道、设备按工程路径查，连带提到它的点位值、写值日志。
  const location = (value: string) => value.trim().slice(0, 500)
  if (location(filters.task)) result.task = location(filters.task)
  else if (location(filters.group)) result.taskOrInterface = location(filters.group)
  else if (location(filters.device)) result.path = location(filters.device)
  else if (location(filters.channel)) result.path = location(filters.channel)
  else if (location(filters.path)) result.path = location(filters.path)
  else if (location(filters.protocol)) result.protocol = location(filters.protocol)
  return result
}

/** 工程配置右键「查看日志」：默认查最近 24 小时，打开日志中心后仍可改时间范围。 */
export const LOG_LOOKUP_HOURS = 24

/** 日志中心的链接：path=工程路径，taskId=采集流程任务，scope=日志分类。 */
export function logCenterLink(target: { path?: string; taskId?: string; scope?: LogCategory; hours?: number }) {
  const query: Record<string, string> = {}
  if (target.scope && target.scope !== 'all') query.scope = target.scope
  if (target.path) query.path = target.path
  if (target.taskId) query.taskId = target.taskId
  if (target.path || target.taskId) query.hours = String(target.hours ?? LOG_LOOKUP_HOURS)
  return { path: '/logs', query }
}

const CATEGORIES: LogCategory[] = ['all', 'collection', 'interface', 'state', 'performance', 'system']

/** 从日志中心地址里的参数得到初始筛选条件(其余条件用默认值)。 */
export function logFiltersFromQuery(query: Record<string, unknown>, now = Date.now()): Partial<LogFilters> {
  const text = (key: string) => {
    const value = query[key]
    return (Array.isArray(value) ? value[0] : value) == null ? '' : String(Array.isArray(value) ? value[0] : value).trim().slice(0, 500)
  }
  const result: Partial<LogFilters> = {}
  const scope = text('scope') as LogCategory
  if (CATEGORIES.includes(scope)) result.category = scope
  if (text('path')) result.path = text('path')
  if (text('taskId')) result.taskId = text('taskId')
  const hours = Number(text('hours'))
  if (Number.isFinite(hours) && hours > 0) {
    // 单次查询最多 31 天
    result.from = new Date(now - Math.min(hours, 31 * 24) * 3_600_000).toISOString()
    result.to = new Date(now).toISOString()
  }
  return result
}

export function observedDeviceState(observation: DeviceObservation, now = Date.now()): string {
  const observedAt = Date.parse(observation.timestamp)
  if (!Number.isFinite(observedAt) || now - observedAt > 60_000 || observedAt - now > 5_000) return 'unknown'
  const status = observation.status.trim().toLowerCase()
  return ['online', 'disconnected', 'faulted', 'bad', 'good'].includes(status) ? status : 'unknown'
}

export function severityTone(level: string): 'info' | 'warning' | 'error' {
  const value = level.toUpperCase()
  if (value.includes('ERROR') || value.includes('CRITICAL') || value === 'ERR' || value === 'CRT') return 'error'
  if (value.includes('WARN') || value === 'WRN') return 'warning'
  return 'info'
}

export function formatLogDuration(value: unknown): string {
  const number = Number(value)
  if (!Number.isFinite(number) || number < 0) return '—'
  return number < 1000 ? `${Math.round(number * 100) / 100} ms` : `${Math.round(number / 10) / 100} s`
}
