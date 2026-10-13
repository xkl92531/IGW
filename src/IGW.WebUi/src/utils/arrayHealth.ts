import type { RuntimeDeviceStatus } from './deviceRuntime'

export function selectArrayHealthDevices(
  devices: RuntimeDeviceStatus[] | null | undefined,
  channels: Set<string>,
): RuntimeDeviceStatus[] {
  return [...(devices || [])]
    .filter((device) => (device.stats?.arrayTags || 0) > 0)
    .filter((device) => channels.size === 0 || channels.has(String(device.name || '').split('.')[0]))
    .sort((left, right) =>
      (right.stats?.arrayBad || 0) - (left.stats?.arrayBad || 0)
      || (right.stats?.arrayUncertain || 0) - (left.stats?.arrayUncertain || 0)
      || String(left.name || '').localeCompare(String(right.name || ''), 'zh-CN'))
}

export function formatArrayBytes(bytes: number): string {
  const value = Math.max(0, Number(bytes || 0))
  if (value >= 1024 * 1024) return `${(value / 1024 / 1024).toFixed(1)} MB`
  if (value >= 1024) return `${(value / 1024).toFixed(1)} KB`
  return `${Math.round(value)} B`
}
