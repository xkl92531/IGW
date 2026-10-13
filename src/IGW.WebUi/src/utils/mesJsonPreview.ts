import type { MesJsonMapping, MesInterfaceListener } from '../api'

export function pathParts(path: string): (string | number)[] {
  if (path === '$') return []
  if (!path.trim() || (path.startsWith('$') && !path.startsWith('$.') && !path.startsWith('$['))) throw new Error(`Invalid JSON path: ${path}`)
  const raw = path.replace(/^\$\.?/, '')
  if (!raw || path.startsWith('$.[') || !/^(?:[^.\[\]]+)?(?:\[\d+\])*(?:\.[^.\[\]]+(?:\[\d+\])*)*$/.test(raw)) throw new Error(`Invalid JSON path: ${path}`)
  return [...raw.matchAll(/([^.\[\]]+)|\[(\d+)\]/g)].map(m => m[2] !== undefined ? Number(m[2]) : m[1]!)
}
export function selectJsonPath(root: unknown, path: string): any {
  let value: any = root
  const parts = path.startsWith('/') ? path.slice(1).split('/').map(v => { if (/~(?![01])/u.test(v)) throw new Error(`Invalid JSON pointer: ${path}`); return v.replace(/~1/g, '/').replace(/~0/g, '~') }) : pathParts(path)
  for (const part of parts) {
    if (!path.startsWith('/') && (typeof part === 'number') !== Array.isArray(value)) throw new Error(`Invalid JSON path: ${path}`)
    if (path.startsWith('/') && Array.isArray(value) && !/^(0|[1-9]\d*)$/.test(String(part))) throw new Error(`Invalid JSON pointer: ${path}`)
    if (value === null || typeof value !== 'object' || !Object.prototype.hasOwnProperty.call(value, part)) throw new Error(`Missing JSON path: ${path}`)
    value = value[part]
  }
  return value
}
export function renderJsonTemplate(template: string, context: unknown): unknown {
  const visit = (value: any): any => {
    if (typeof value === 'string') {
      const full = /^\$\{([^{}]*)\}$/.exec(value)
      if (full) return selectJsonPath(context, full[1]!)
      return value.replace(/\$\{([^{}]*)\}/g, (_, path) => {
        const selected = selectJsonPath(context, path)
        return typeof selected === 'string' ? selected : JSON.stringify(selected)
      })
    }
    if (Array.isArray(value)) return value.map(visit)
    if (value && typeof value === 'object') return Object.fromEntries(Object.entries(value).map(([k, v]) => [k, visit(v)]))
    return value
  }
  return visit(JSON.parse(template))
}
export function mapJson(root: unknown, mappings: MesJsonMapping[]): unknown {
  if (!mappings.length) return root
  const result: Record<string, any> = Object.create(null)
  const assigned: (string | number)[][] = []
  for (const mapping of mappings) {
    const parts = pathParts(mapping.targetPath)
    if (!parts.length || typeof parts[0] !== 'string' || parts.some(p => typeof p === 'number' && p >= 10000)) throw new Error(`Invalid mapping target: ${mapping.targetPath}`)
    if (assigned.some(previous => previous.slice(0, Math.min(previous.length, parts.length)).every((part, i) => part === parts[i]))) throw new Error(`Mapping target conflict: ${mapping.targetPath}`)
    let target: any = result
    for (let i = 0; i < parts.length - 1; i++) {
      const part = parts[i]!
      const needsArray = typeof parts[i + 1] === 'number'
      if (Object.prototype.hasOwnProperty.call(target, part) && (target[part] === null || typeof target[part] !== 'object' || Array.isArray(target[part]) !== needsArray)) throw new Error(`Mapping target conflict: ${mapping.targetPath}`)
      target[part] ??= needsArray ? [] : Object.create(null)
      target = target[part]
    }
    target[parts[parts.length - 1]!] = selectJsonPath(root, mapping.sourcePath)
    assigned.push(parts)
  }
  return result
}
export function defaultInterfaceListener(): MesInterfaceListener {
  return { protocol: 'SignalR', receiveMethod: 'ReceiveBarcode', subscribeMethod: '', replyMethod: '', url: '', headers: {}, subscriptionTemplate: '', responseTemplate: '', ignoreResponseTemplate: '', eventName: '', idField: 'requestId', changeFields: [], triggerOnFirst: true, reconnectMs: 1000, idleTimeoutSec: 90, maxMessageBytes: 1048576, inputMappings: [] }
}
