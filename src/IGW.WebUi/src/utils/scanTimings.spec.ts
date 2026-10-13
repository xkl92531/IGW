import { describe, expect, it } from 'vitest'
import { filterScanTimingDevices, formatScanStartedAt, formatTimingMs, isPassiveTimingMode, scanTimingWindowSummary } from './scanTimings'

describe('scan timing presentation', () => {
  const devices = [
    { name: 'Line1.Plc1', mode: 'polling' },
    { name: 'Line2.Plc2', mode: 'subscription' },
  ] as any[]

  it('filters full device names by the selected monitor channels', () => {
    expect(filterScanTimingDevices(devices, new Set(['Line2'])).map(row => row.name)).toEqual(['Line2.Plc2'])
    expect(filterScanTimingDevices([{ name: 'Area.Line.Plc3' } as any], new Set(['Area.Line']))).toHaveLength(1)
    expect(filterScanTimingDevices(devices, new Set())).toHaveLength(2)
  })

  it('formats millisecond metrics without presenting missing values as zero', () => {
    expect(formatTimingMs(12)).toBe('12 ms')
    expect(formatTimingMs(null)).toBe('—')
    expect(isPassiveTimingMode('subscription')).toBe(true)
    expect(isPassiveTimingMode('standby')).toBe(true)
    expect(isPassiveTimingMode('mixed')).toBe(false)
    expect(formatScanStartedAt('2026-09-05T04:00:00.123Z')).toContain('.123')
  })

  it('summarizes the visible cache window and counts scans slower than their configured period', () => {
    expect(scanTimingWindowSummary([])).toEqual({ count: 0, averageTotalMs: null, maxTotalMs: null, slowCount: 0 })
    expect(scanTimingWindowSummary([
      { totalMs: 20, configuredPeriodMs: 100 },
      { totalMs: 140, configuredPeriodMs: 100 },
      { totalMs: 40, configuredPeriodMs: 0 },
    ] as any)).toEqual({ count: 3, averageTotalMs: 67, maxTotalMs: 140, slowCount: 1 })
  })
})
