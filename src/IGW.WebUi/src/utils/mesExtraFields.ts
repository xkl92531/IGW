import { selectJsonPath } from './mesJsonPreview'
import { ordinaryPayloadFields, usesAutomaticPayload, usesRangePayload, validateParameterRouting } from './mesParameterRouting'

export interface MesExtraField {
  name: string
  dataType: 'string' | 'int32' | 'double' | 'bool' | 'json'
  source: 'Fixed' | 'Tag' | 'Input'
  value: string
  path: string
  required: boolean
}
const conflicts = (left: string, right: string) => left === right || left.startsWith(right + '.') || right.startsWith(left + '.')
function typed(field: MesExtraField, raw: unknown, fixed = false): unknown {
  let value = raw
  if (fixed && field.dataType !== 'string') value = String(raw ?? '').trim() ? JSON.parse(String(raw)) : null
  if (value == null || typeof value === 'string' && !value.trim()) {
    if (field.required) throw new Error(`必填业务字段缺失：${field.name}`)
    return null
  }
  const valid = field.dataType === 'json' || field.dataType === 'string' && typeof value === 'string'
    || field.dataType === 'bool' && typeof value === 'boolean'
    || field.dataType === 'double' && typeof value === 'number' && Number.isFinite(value)
    || field.dataType === 'int32' && typeof value === 'number' && Number.isInteger(value) && value >= -2147483648 && value <= 2147483647
  if (!valid) throw new Error(`业务字段 ${field.name} 的值不符合 ${field.dataType} 类型`)
  return value
}
export function validateExtraFields(payload: any, automatic = true): void {
  const assigned: string[] = []
  const ranges = usesRangePayload(payload)
  if (automatic) validateParameterRouting(payload)
  const ordinary: string[] = [...Object.keys(payload?.append || {}), ...(ranges
    ? (payload?.dataRanges || []).map((row: any) => row.outputPath || '')
    : [...ordinaryPayloadFields(payload).filter((row: any) => !automatic || !String(row.parameterCode || '').trim()).map((row: any) => row.fieldName || row.tagId?.split('.').pop() || ''), ...(payload?.structs || []).filter((row: any) => (!automatic || !row.asDictionary) && (row.members?.length || row.structs?.length)).map((row: any) => row.aliasName || '')])]
  for (const field of (payload?.extraFields || []) as MesExtraField[]) {
    if (!field || !field.name || field.name.split('.').some(part => !part.trim() || part !== part.trim() || /[\[\]/$]/.test(part))) throw new Error(`业务字段输出路径无效：${field?.name}`)
    if ([...assigned, ...ordinary.filter(Boolean)].some(name => conflicts(name, field.name))) throw new Error(`业务字段输出路径重复或冲突：${field.name}`)
    assigned.push(field.name)
    if (!['string', 'int32', 'double', 'bool', 'json'].includes(field.dataType) || !['Fixed', 'Tag', 'Input'].includes(field.source)) throw new Error(`业务字段类型或来源无效：${field.name}`)
    if (field.source === 'Fixed') typed(field, field.value, true)
    else if (!field.path?.trim()) throw new Error(`业务字段来源路径不能为空：${field.name}`)
  }
}
export function validateTaskExtraFields(tasks: any[]): void {
  for (const task of tasks) {
    const steps = task.steps || [], flow = task.flow?.nodes || []
    const automatic = !flow.length && (!steps.length || steps.some((step: any) => usesAutomaticPayload(task.payload, step.http)))
    validateExtraFields(task.payload, automatic)
    for (const node of flow) if (node.type === 'Upload') validateExtraFields(node.payload, usesAutomaticPayload(node.payload, node.http))
  }
}
export function previewExtraFields(fields: MesExtraField[], context: any): unknown {
  validateExtraFields({ extraFields: fields })
  const result: Record<string, any> = Object.create(null)
  for (const field of fields) {
    let value: unknown = null
    if (field.source === 'Fixed') value = field.value
    else if (field.source === 'Tag') value = context.tags?.[field.path]
    else { try { value = selectJsonPath(context[field.source.toLowerCase()], field.path) } catch { value = null } }
    const parts = field.name.split('.')
    let parent = result
    for (const part of parts.slice(0, -1)) parent = (parent[part] ??= Object.create(null))
    parent[parts[parts.length - 1]!] = typed(field, value, field.source === 'Fixed')
  }
  return result
}
