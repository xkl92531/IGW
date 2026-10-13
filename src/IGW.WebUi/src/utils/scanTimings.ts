import type { ScanTimingDeviceDto, ScanTimingRecordDto } from '../api'

export function filterScanTimingDevices(devices: readonly ScanTimingDeviceDto[], channels: ReadonlySet<string>): ScanTimingDeviceDto[] {
  if (!channels.size) return [...devices]
  return devices.filter(device => [...channels].some(channel => device.name === channel || device.name.startsWith(`${channel}.`)))
}

export function formatTimingMs(value: number | null | undefined): string {
  return value == null ? '—' : `${value} ms`
}

export function isPassiveTimingMode(mode: string): boolean {
  return mode === 'subscription' || mode === 'standby'
}

export function formatScanStartedAt(value: string): string {
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return value
  return `${date.toLocaleString()}.${String(date.getMilliseconds()).padStart(3, '0')}`
}

export function scanTimingWindowSummary(records: readonly ScanTimingRecordDto[]) {
  if (!records.length) return { count: 0, averageTotalMs: null, maxTotalMs: null, slowCount: 0 }
  const total = records.reduce((sum, record) => sum + record.totalMs, 0)
  return {
    count: records.length,
    averageTotalMs: Math.round(total / records.length),
    maxTotalMs: Math.max(...records.map(record => record.totalMs)),
    slowCount: records.filter(record => record.configuredPeriodMs > 0 && record.totalMs > record.configuredPeriodMs).length,
  }
}
