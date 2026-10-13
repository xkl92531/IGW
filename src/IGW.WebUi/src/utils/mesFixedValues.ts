/** 固定值(后端 MesFixedValue)：字段名、类型、值；「固定值」节点里还可以填写回点位。 */
export interface MesFixedValue {
  name: string
  dataType: 'Text' | 'Int' | 'Decimal' | 'Bool'
  value: string
  tagId?: string | null
}

export const FixedValueTypes: { value: MesFixedValue['dataType']; label: string }[] = [
  { value: 'Text', label: '文本' },
  { value: 'Int', label: '整数' },
  { value: 'Decimal', label: '小数' },
  { value: 'Bool', label: '布尔' },
]

/** 和后端 MesFixedValueTypes 同样的规则：值要能按类型转换，字段名不能为空、不能重复。返回每行的错误(没有错误为空串)。 */
export function fixedValueErrors(items: MesFixedValue[]): string[] {
  const seen = new Set<string>()
  return (items || []).map((item) => {
    const name = String(item?.name || '').trim()
    if (!name) return '字段名不能为空'
    if (seen.has(name)) return '字段名重复'
    seen.add(name)
    const value = String(item?.value ?? '').trim()
    if (item.dataType === 'Int' && !/^[+-]?\d+$/.test(value)) return '不是整数'
    if (item.dataType === 'Decimal' && (value === '' || !Number.isFinite(Number(value)))) return '不是数字'
    if (item.dataType === 'Bool' && !['true', 'false', '1', '0'].includes(value.toLowerCase())) return '不是布尔值'
    return ''
  })
}

/** 上报节点：以前只能填文本的附加字段(append)按「文本」类型显示在同一个列表里。 */
export function uploadFixedRows(payload: any): MesFixedValue[] {
  const legacy = Object.entries(payload?.append || {}).map(([name, value]) => ({ name, dataType: 'Text' as const, value: String(value ?? '') }))
  return [...legacy, ...(payload?.fixedValues || [])]
}

/** 保存编辑结果：全部写进 fixedValues，旧的 append 清空(报文内容不变，文本仍按字符串输出)。 */
export function setUploadFixedRows(payload: any, rows: MesFixedValue[]) {
  payload.fixedValues = rows.map(({ name, dataType, value }) => ({ name, dataType, value }))
  payload.append = {}
}
