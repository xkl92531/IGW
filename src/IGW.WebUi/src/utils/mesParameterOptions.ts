export type MesParameterKind = 'tag' | 'group'
export type MesParameterScope = 'CurrentGroup' | 'CurrentDevice' | 'OtherDevice'

export interface MesParameterOptionLike {
  value: string
  label: string
  fullPath: string
  scope: MesParameterScope
  kind: MesParameterKind
  description?: string
  address?: string
}

export type MesParameterPickerOption = MesParameterOptionLike & {
  missing: boolean
  pending: boolean
}

function samePath(left: string, right: string): boolean {
  return String(left || '').trim().toLocaleLowerCase() === String(right || '').trim().toLocaleLowerCase()
}

function leafName(path: string): string {
  const parts = path.split('.').filter(Boolean)
  return parts[parts.length - 1] || path
}

export function findExactParameterOption<T extends MesParameterOptionLike>(
  items: T[],
  current: string,
): T | null {
  const value = String(current || '').trim()
  if (!value) return null
  return items.find((item) => samePath(item.value, value)) || null
}

/**
 * Keeps the configured value visible while remote candidates load. A value is only marked missing
 * after a successful exact-path validation; an empty or filtered candidate list is not evidence
 * that the project point no longer exists.
 */
export function mergeCurrentParameterOptions<T extends MesParameterOptionLike>(
  items: T[],
  current: string,
  kind: MesParameterKind,
  resolvedOption: T | null,
  validationCompleted: boolean,
): Array<T | MesParameterPickerOption> {
  const value = String(current || '').trim()
  if (!value || findExactParameterOption(items, value)) return items
  if (resolvedOption && samePath(resolvedOption.value, value)) {
    return [{ ...resolvedOption, missing: false, pending: false }, ...items]
  }
  return [{
    value,
    label: leafName(value),
    fullPath: value,
    scope: 'CurrentGroup',
    kind,
    missing: validationCompleted,
    pending: !validationCompleted,
  }, ...items]
}
