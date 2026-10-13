import { pathParts } from './mesJsonPreview'

export type JsonFieldType = 'string' | 'number' | 'boolean' | 'null' | 'object' | 'array'
export interface JsonField {
  path: string
  name: string
  type: JsonFieldType
  value: unknown
  ancestors: string[]
  expandable: boolean
}

export function jsonFieldType(value: unknown): JsonFieldType {
  return value === null ? 'null' : Array.isArray(value) ? 'array' : typeof value as JsonFieldType
}

function fieldPath(parts: (string | number)[]): string {
  if (parts.some(part => typeof part === 'string' && !/^[A-Za-z_][\w-]*$/.test(part)))
    return '/' + parts.map(part => String(part).replace(/~/g, '~0').replace(/\//g, '~1')).join('/')
  return '$' + parts.map(part => typeof part === 'number' ? `[${part}]` : `.${part}`).join('')
}

export function inspectJsonFields(value: unknown): JsonField[] {
  const rows: JsonField[] = []
  const visit = (current: unknown, parts: (string | number)[], ancestors: string[]) => {
    if (rows.length >= 10000 || ancestors.length > 100) throw new Error('JSON inspection limit exceeded (10000 fields / 100 levels)')
    const path = fieldPath(parts)
    const entries = current !== null && typeof current === 'object' ? Object.entries(current) : []
    rows.push({ path, name: String(parts[parts.length - 1] ?? 'value'), type: jsonFieldType(current), value: current, ancestors, expandable: entries.length > 0 })
    for (const [key, child] of entries) visit(child, [...parts, Array.isArray(current) ? Number(key) : key], [...ancestors, path])
  }
  visit(value, [], [])
  return rows
}

export function parseJsonFieldValue(type: JsonFieldType, text: string): unknown {
  if (type === 'string') return text
  if (type === 'null') return null
  const value: unknown = JSON.parse(text)
  if (jsonFieldType(value) !== type || (type === 'number' && !Number.isFinite(value))) throw new Error(`Expected JSON ${type}`)
  return value
}

/** Edits a clone. Existing values may only be overwritten by an explicit replace action. */
export function changeJsonField(root: unknown, path: string, value: unknown, operation: 'add' | 'replace' | 'remove'): unknown {
  const result = JSON.parse(JSON.stringify(root))
  const pointer = path.startsWith('/')
  const parts = pointer ? path.slice(1).split('/').map(part => {
    if (/~(?![01])/.test(part)) throw new Error(`Invalid JSON pointer: ${path}`)
    return part.replace(/~1/g, '/').replace(/~0/g, '~')
  }) : pathParts(path)
  if (!parts.length) {
    if (operation !== 'replace') throw new Error('Select a child field')
    return value
  }
  let current = result
  for (let i = 0; i < parts.length; i++) {
    const part = parts[i]!
    if (current === null || typeof current !== 'object') throw new Error(`Parent is not an object or array: ${path}`)
    const array = Array.isArray(current)
    if (array ? (!/^(0|[1-9]\d*)$/.test(String(part)) || (!pointer && typeof part !== 'number') || Number(part) > current.length || Number(part) >= 10000) : (!pointer && typeof part === 'number'))
      throw new Error(`Invalid field index: ${path}`)
    const exists = Object.prototype.hasOwnProperty.call(current, part)
    const leaf = i === parts.length - 1
    if (leaf) {
      if (operation === 'add' && exists) throw new Error(`Field already exists: ${path}`)
      if (operation !== 'add' && !exists) throw new Error(`Missing JSON path: ${path}`)
      if (operation === 'remove') { if (array) current.splice(Number(part), 1); else delete current[part] }
      else Object.defineProperty(current, part, { value, enumerable: true, configurable: true, writable: true })
    } else {
      if (!exists) {
        if (operation !== 'add' || pointer) throw new Error(`Missing parent path: ${path}`)
        Object.defineProperty(current, part, { value: typeof parts[i + 1] === 'number' ? [] : {}, enumerable: true, configurable: true, writable: true })
      }
      current = current[part]
    }
  }
  return result
}
