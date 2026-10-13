import { dataTypeDisplay, dataTypeKey } from './siemensDataTypes.ts'

export function pointTypeKey(value: unknown): string {
  return dataTypeKey(value)
}

export function pointTypeDisplay(value: unknown): string {
  return dataTypeDisplay(value)
}

export function pointValueDisplay(value: string, dataType: unknown): string {
  if (pointTypeKey(dataType) !== 'bytearray' || value === '—') return value
  try {
    const bytes = atob(value)
    return `[${Array.from(bytes, byte => byte.charCodeAt(0)).join(', ')}]`
  } catch {
    return value
  }
}
