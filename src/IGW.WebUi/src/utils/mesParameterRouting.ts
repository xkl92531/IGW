const code = (field: any) => typeof field?.parameterCode === 'string' && field.parameterCode.trim() ? field.parameterCode : ''
const covered = (path: string, structs: any[]) => structs.some(item => {
  const base = String(item?.baseGroup || '')
  return base && (path === base || path.startsWith(base + '.') || path.startsWith(base + '[') || path.startsWith(base + '_'))
})
export function usesRangePayload(payload: any): boolean {
  const mode = String(payload?.mode || '').toLowerCase()
  return mode === 'dataranges' || mode !== 'fieldmapping' && (payload?.dataRanges?.length || 0) > 0
}
export function usesAutomaticPayload(payload: any, http: any): boolean {
  return !String(http?.jsonTemplate || '').trim() &&
    (usesRangePayload(payload) || !String(http?.bodyTemplate || '').trim())
}
export function ordinaryPayloadFields(payload: any): any[] {
  return (payload?.fields || []).filter((field: any) => !covered(String(field?.tagId || ''), payload?.structs || []))
}
export function hasParameterDictionary(payload: any): boolean {
  if (usesRangePayload(payload)) return false
  const selected = (structs: any[]): boolean => (structs || []).some(item => item.asDictionary === true ||
    (item.members || []).some((field: any) => !!code(field)) || selected(item.structs))
  return ordinaryPayloadFields(payload).some(field => !!code(field)) || selected(payload?.structs)
}
export function validateParameterRouting(payload: any): void {
  if (!hasParameterDictionary(payload)) return
  const original = ordinaryPayloadFields(payload).filter(field => !code(field)).map(field => field.fieldName || String(field.tagId || '').split('.').pop())
  const names = [...original, ...(payload?.structs || []).filter((item: any) => !item.asDictionary).map((item: any) => item.aliasName),
    ...Object.keys(payload?.append || {}), ...(payload?.fixedValues || []).map((field: any) => field.name),
    ...(payload?.extraFields || []).map((field: any) => String(field?.name || '').split('.')[0])]
  if (names.some(name => String(name || '').trim().toLowerCase() === 'parameterdic'))
    throw new Error('外层字段与 parameterDic 字典冲突，请修改原字段名称。')
  const keys = new Map<string, { target: string; scale: number }>()
  const add = (field: any, target: string) => {
    const key = code(field)
    if (!key) return
    const previous = keys.get(key.toLowerCase()), scale = Number(field.scale ?? 1)
    if (previous && (previous.target !== target || previous.scale !== scale)) throw new Error(`参数码重复或冲突：${key}`)
    keys.set(key.toLowerCase(), { target, scale })
  }
  ordinaryPayloadFields(payload).forEach(field => add(field, String(field.tagId || '')))
  const members = (structs: any[], parent = '') => {
    for (const item of structs || []) {
      // 整个结构转字典时使用完整点路径，成员参数码不参与字典键。
      if (item.asDictionary) continue
      const base = parent ? `${parent}.${item.baseGroup}` : String(item.baseGroup || '')
      const element = item.kind === 'Array' ? `${base}[]` : base
      ;(item.members || []).forEach((field: any) => add(field, `${element}.${field.tagId}`))
      members(item.structs, element)
    }
  }
  members(payload?.structs)
}
