/**
 * 采集记录、质量变化记录附带的点位值(igw_tag_values)。后端存成紧凑 JSON 字符串：
 * {"total":点数,"omitted":省略数,"badOnly":true,"points":[["通道.设备.组.点",值,"质量(非 Good 才有)"],...]}
 * 值是数字/布尔/null 或字符串(长字符串已截断，数组已摘要)。
 */
export const TagValuesField = 'igw_tag_values'

export interface LogTagValueRow { path: string; value: string; quality: string; good: boolean }
export interface LogTagValues { total: number; omitted: number; badOnly: boolean; rows: LogTagValueRow[] }

/** 解析失败(旧格式、被截断等)返回 null，详情里退回按普通字段原样显示。 */
export function parseLogTagValues(raw: unknown): LogTagValues | null {
  if (typeof raw !== 'string' || !raw.trim().startsWith('{')) return null
  try {
    const data = JSON.parse(raw) as { total?: unknown; omitted?: unknown; badOnly?: unknown; points?: unknown }
    if (!Array.isArray(data.points)) return null
    const rows = data.points
      .filter((point): point is unknown[] => Array.isArray(point) && typeof point[0] === 'string')
      .map(point => {
        const quality = typeof point[2] === 'string' ? point[2] : ''
        return { path: point[0] as string, value: displayValue(point[1]), quality, good: !quality || quality.startsWith('Good') }
      })
    const total = typeof data.total === 'number' ? data.total : rows.length
    const omitted = typeof data.omitted === 'number' ? data.omitted : Math.max(0, total - rows.length)
    return { total, omitted, badOnly: data.badOnly === true, rows }
  } catch { return null }
}

function displayValue(value: unknown): string {
  if (value === null || value === undefined) return '—'
  if (typeof value === 'string') return value === '' ? '""' : value
  return String(value)
}

/** 工程路径筛选命中的点：路径本身或它的下级(组路径命中组内的点)，按路径段判断，PLC6.D1 不会命中 PLC6.D10。 */
export function tagPathMatches(path: string, filter: string | null | undefined): boolean {
  if (!filter) return false
  return path === filter || path.startsWith(`${filter}.`) || path.startsWith(`${filter}[`)
}
