import { describe, expect, it } from 'vitest'
import { buildLogQuery, createLogFilters, logCenterLink, logFiltersFromQuery, observedDeviceState } from './logCenter'

describe('log center query model', () => {
  it('builds a bounded query and omits empty filters', () => {
    const query = buildLogQuery(
      createLogFilters({ category: 'performance', from: '2026-09-13T07:00:00.000Z', to: '2026-09-13T08:00:00.000Z' }),
      2,
      500,
      Date.parse('2026-09-13T08:00:00Z'),
    )
    expect(query).toMatchObject({ category: 'performance', page: 2, pageSize: 200 })
    expect(query).not.toHaveProperty('device')
    expect(query.from).toBe('2026-09-13T07:00:00.000Z')
  })

  it('sends module feature task and interface filters without squeezing them into search', () => {
    const query = buildLogQuery(createLogFilters({
      from: '2026-09-13T07:00:00.000Z', to: '2026-09-13T08:00:00.000Z',
      module: 'acquisition', feature: 'plc_scan', task: 'OP140 出站', interface: '/report',
    }), 1, 50, Date.parse('2026-09-13T08:00:00Z'))
    expect(query).toMatchObject({
      module: 'acquisition', feature: 'plc_scan', task: 'OP140 出站', interface: '/report',
    })
  })

  it('trims optional filters and rejects a reversed range', () => {
    const filters = createLogFilters({
      from: '2026-09-13T08:00:00.000Z', to: '2026-09-13T07:00:00.000Z',
      device: '  PLC8.OP140 ', search: ' failed ',
    })
    expect(() => buildLogQuery(filters, 1, 50)).toThrow(/time range/i)
    filters.from = '2026-09-13T06:00:00.000Z'
    expect(buildLogQuery(filters, 1, 50)).toMatchObject({ path: 'PLC8.OP140', search: 'failed' })
  })

  it('sends only the most specific location: tag group > device > channel > project path > protocol', () => {
    const now = Date.parse('2026-10-08T08:00:00Z')
    const query = (overrides: Record<string, string>) => {
      const result = buildLogQuery(createLogFilters(overrides, now), 1, 50, now)
      return { task: result.task, path: result.path, protocol: result.protocol }
    }
    const all = { protocol: 'Siemens.S7', channel: 'PLC8', device: 'PLC8.OP205', path: 'PLC8.OP205.DB1' }
    // 标记组按它绑定的任务查：任务的接口调用、回写没有协议/设备字段，再叠加设备条件会把它们筛掉
    expect(query({ ...all, group: 'StationEntrance-16', task: 't16' })).toEqual({ task: 't16', path: undefined, protocol: undefined })
    expect(query(all)).toEqual({ task: undefined, path: 'PLC8.OP205', protocol: undefined })
    expect(query({ protocol: 'Siemens.S7', channel: 'PLC8' })).toEqual({ task: undefined, path: 'PLC8', protocol: undefined })
    expect(query({ protocol: 'Siemens.S7', path: 'PLC6.D1' })).toEqual({ task: undefined, path: 'PLC6.D1', protocol: undefined })
    expect(query({ protocol: 'Siemens.S7' })).toEqual({ task: undefined, path: undefined, protocol: 'Siemens.S7' })
  })

  it('keeps typed names or URLs as the most specific filter rather than ANDing the PLC protocol with HTTP logs', () => {
    const query = buildLogQuery(createLogFilters({ protocol: 'Siemens.S7', channel: 'PLC8', device: 'PLC8.OP205', group: 'report' }), 1, 50)
    expect(query).toHaveProperty('taskOrInterface', 'report')
    expect(query).not.toHaveProperty('path')
    expect(query).not.toHaveProperty('protocol')
    expect(query).not.toHaveProperty('task')
  })

  it('marks a stale device observation unknown after sixty seconds', () => {
    expect(observedDeviceState(
      { status: 'online', timestamp: '2026-09-13T07:58:00Z' },
      Date.parse('2026-09-13T08:00:00Z'),
    )).toBe('unknown')
    expect(observedDeviceState(
      { status: 'faulted', timestamp: '2026-09-13T07:59:30Z' },
      Date.parse('2026-09-13T08:00:00Z'),
    )).toBe('faulted')
  })
})

describe('工程配置右键「查看日志」', () => {
  it('链接带工程路径和最近 24 小时', () => {
    expect(logCenterLink({ path: 'PLC6.D1.OP051' })).toEqual({ path: '/logs', query: { path: 'PLC6.D1.OP051', hours: '24' } })
    expect(logCenterLink({ taskId: 't1' })).toEqual({ path: '/logs', query: { taskId: 't1', hours: '24' } })
    expect(logCenterLink({ scope: 'performance' })).toEqual({ path: '/logs', query: { scope: 'performance' } })
  })

  it('日志中心从地址读出筛选条件，并按 path 发给后端', () => {
    const now = Date.parse('2026-10-07T08:00:00Z')
    const overrides = logFiltersFromQuery({ path: ' PLC6.D1 ', hours: '24', scope: 'collection' }, now)
    expect(overrides).toMatchObject({ path: 'PLC6.D1', category: 'collection', to: '2026-10-07T08:00:00.000Z', from: '2026-10-06T08:00:00.000Z' })
    expect(logFiltersFromQuery({ scope: 'bogus', hours: '-1' }, now)).toEqual({})
    expect(logFiltersFromQuery({ hours: '9999' }, now).from).toBe('2026-09-06T08:00:00.000Z')   // 最多 31 天
    const query = buildLogQuery(createLogFilters(overrides, now), 1, 50, now)
    expect(query).toMatchObject({ path: 'PLC6.D1', category: 'collection' })
  })
})
