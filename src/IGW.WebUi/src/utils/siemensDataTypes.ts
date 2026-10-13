export interface DataTypeOption { value: string; label: string }

const typeKeys = [
  'bool', 'byte', 'int16', 'uint16', 'int32', 'uint32', 'int64', 'uint64', 'float', 'double',
  'string', 'bytearray', 'wstring', 'char', 'bcd', 'lbcd', 'struct',
  'dtl', 'date', 'tod', 'ltod', 'time', 'ltime', 'dt', 'ldt', 's5time', 's7char', 'wchar',
  'word', 'dword', 'lword', 'timer', 'counter', 'sint', 'datetime',
] as const

const typeLabels = [
  'Boolean', 'Byte', 'Short', 'Word', 'Long', 'DWord', 'LLong', 'QWord', 'Float', 'Double',
  'String', 'Byte Array', 'WString', 'Char (legacy signed byte)', 'BCD', 'LBCD', 'Struct',
  'DTL', 'DATE', 'TOD', 'LTOD', 'TIME', 'LTIME', 'DT', 'LDT', 'S5TIME', 'S7 CHAR', 'WCHAR',
  'WORD', 'DWORD', 'LWORD', 'TIMER', 'COUNTER', 'SINT', 'DateTime (UTC)',
] as const

export function dataTypeKey(value: unknown): string {
  if (typeof value === 'number') return typeKeys[value] ?? String(value)
  return String(value ?? '').trim().toLowerCase()
}

export function dataTypeDisplay(value: unknown): string {
  const key = dataTypeKey(value)
  return typeLabels[typeKeys.indexOf(key as typeof typeKeys[number])] ?? (String(value ?? '') || '—')
}

const primitiveDataTypes: readonly DataTypeOption[] = [
  { value: 'bool', label: 'BOOL / Boolean' },
  { value: 'byte', label: 'BYTE / Byte' },
  { value: 'char', label: 'Char (legacy signed byte)' },
  { value: 'int16', label: 'INT / Short' },
  { value: 'uint16', label: 'UINT / UInt16 / Word' },
  { value: 'int32', label: 'DINT / Long' },
  { value: 'uint32', label: 'UDINT / UInt32 / DWord' },
  { value: 'int64', label: 'LINT / LLong' },
  { value: 'uint64', label: 'ULINT / QWord' },
  { value: 'float', label: 'REAL / Float' },
  { value: 'double', label: 'LREAL / Double' },
  { value: 'string', label: 'STRING / String' },
  { value: 'wString', label: 'WSTRING / WString' },
  { value: 'byteArray', label: 'Byte Array' }, { value: 'bcd', label: 'BCD' },
  { value: 'lBcd', label: 'LBCD' },
]

const siemensSemanticDataTypes: readonly DataTypeOption[] = [
  { value: 'sint', label: 'SINT (8-bit signed)' },
  { value: 's7Char', label: 'CHAR / S7 CHAR' }, { value: 'wChar', label: 'WCHAR' },
  { value: 'word', label: 'WORD (16-bit bits)' }, { value: 'dword', label: 'DWORD (32-bit bits)' },
  { value: 'lword', label: 'LWORD (64-bit bits)' },
  { value: 'dtl', label: 'DTL' }, { value: 'date', label: 'DATE' },
  { value: 'tod', label: 'TOD / TIME_OF_DAY' }, { value: 'ltod', label: 'LTOD / LTIME_OF_DAY' },
  { value: 'time', label: 'TIME' }, { value: 'ltime', label: 'LTIME' },
  { value: 'dt', label: 'DT / DATE_AND_TIME' }, { value: 'ldt', label: 'LDT / DATE_AND_LTIME' },
  { value: 's5Time', label: 'S5TIME' }, { value: 'timer', label: 'TIMER' },
  { value: 'counter', label: 'COUNTER' }, { value: 'dateTime', label: 'DateTime (UTC)' },
]

export const editableDataTypes: readonly DataTypeOption[] = [...primitiveDataTypes, ...siemensSemanticDataTypes]

export function editableDataTypesForDriver(driverId: unknown): readonly DataTypeOption[] {
  const driver = String(driverId || '').toLowerCase()
  if (driver === 'modbus.tcp') return primitiveDataTypes.filter(option => !['string', 'wString', 'byteArray'].includes(option.value))
  return driver === 'opcua.client' ? editableDataTypes : primitiveDataTypes
}

export function dataTypeEditorValue(value: unknown): string {
  const key = dataTypeKey(value)
  return editableDataTypes.find(option => dataTypeKey(option.value) === key)?.value ?? String(value ?? '')
}

const textTypes = new Set(['dtl', 'date', 'tod', 'ltod', 'time', 'ltime', 'dt', 'ldt', 's5time', 'datetime'])
const exactIntegerTypes = new Set(['int64', 'uint64', 'lword'])
const integerRanges: Record<string, [number, number]> = {
  byte: [0, 255], char: [-128, 127], sint: [-128, 127], int16: [-32768, 32767], uint16: [0, 65535], word: [0, 65535],
  int32: [-2147483648, 2147483647], uint32: [0, 4294967295], dword: [0, 4294967295],
  timer: [0, 65535], counter: [0, 65535], bcd: [0, 65535], lbcd: [0, 4294967295],
}

export function parsePointWriteInput(input: string, dataType: unknown): { value?: unknown; error?: string } {
  const key = dataTypeKey(dataType)
  const raw = String(input ?? '')
  const text = raw.trim()
  if (key === 'bool') {
    if (!['true', 'false', '0', '1'].includes(text.toLowerCase())) return { error: '布尔需 true/false/0/1' }
    return { value: ['true', '1'].includes(text.toLowerCase()) }
  }
  if (['string', 'wstring'].includes(key)) return { value: raw }
  if (['s7char', 'wchar'].includes(key)) return Array.from(raw).length === 1 ? { value: raw } : { error: `${dataTypeDisplay(dataType)} 需一个字符` }
  if (textTypes.has(key)) return text ? { value: text } : { error: '值不能为空' }
  if (exactIntegerTypes.has(key)) {
    if (!/^[+-]?\d+$/.test(text)) return { error: `${dataTypeDisplay(dataType)} 需十进制整数` }
    const value = BigInt(text)
    const min = key === 'int64' ? -(1n << 63n) : 0n
    const max = key === 'int64' ? (1n << 63n) - 1n : (1n << 64n) - 1n
    return value >= min && value <= max ? { value: text } : { error: `${dataTypeDisplay(dataType)} 超出有效范围` }
  }
  if (integerRanges[key]) {
    if (!/^[+-]?\d+$/.test(text)) return { error: `${dataTypeDisplay(dataType)} 需整数` }
    const value = Number(text); const [min, max] = integerRanges[key]
    return value >= min && value <= max ? { value } : { error: `${dataTypeDisplay(dataType)} 超出 ${min}..${max}` }
  }
  const value = Number(text)
  return text && Number.isFinite(value) ? { value } : { error: `需数字(${key})` }
}

export function parseArrayWriteInput(input: string, elementType: unknown, slice: boolean): { value?: unknown; error?: string } {
  const key = dataTypeKey(elementType)
  if (!slice && exactIntegerTypes.has(key)) {
    const unquoted = String(input).trim().replace(/^"(.*)"$/, '$1')
    return parsePointWriteInput(unquoted, key)
  }
  let value: unknown
  try { value = JSON.parse(input) }
  catch { return { error: '请输入有效 JSON；字符串需要使用双引号' } }
  if (exactIntegerTypes.has(key)) {
    const validate = (item: unknown): string | undefined => {
      if (Array.isArray(item)) { for (const child of item) { const error = validate(child); if (error) return error }; return undefined }
      if (typeof item !== 'string') return `${dataTypeDisplay(elementType)} 数组元素必须使用十进制字符串，避免 64 位精度损失`
      return parsePointWriteInput(item, key).error
    }
    const error = validate(value)
    if (error) return { error }
  }
  return { value }
}

export function pointWriteHint(dataType: unknown): string {
  const hints: Record<string, string> = {
    dtl: 'yyyy-MM-dd HH:mm:ss.fffffffff（PLC 日历时间，不转换时区）', date: 'yyyy-MM-dd',
    tod: 'HH:mm:ss.fff', ltod: 'HH:mm:ss.fffffffff', dt: 'yyyy-MM-dd HH:mm:ss.fff',
    ldt: 'yyyy-MM-dd HH:mm:ss.fffffffff', datetime: 'UTC ISO 8601，例如 2026-09-05T04:03:02.000Z',
    time: 'T#<毫秒>ms 或毫秒整数', ltime: 'LT#<纳秒>ns 或纳秒整数', s5time: 'S5T#<毫秒>ms',
    s7char: '一个单字节字符', wchar: '一个 Unicode 字符', int64: '十进制整数（按文本传输，避免精度损失）',
    uint64: '十进制整数（按文本传输，避免精度损失）', lword: '无符号十进制整数（按文本传输）',
    word: '0..65535（原生 16-bit 位模式）', dword: '0..4294967295（原生 32-bit 位模式）',
    timer: '0..65535（原生 UInt16）', counter: '0..65535（原生 UInt16）', sint: '-128..127',
  }
  return hints[dataTypeKey(dataType)] || ''
}

function normalizeFraction(text: string, digits: number): string {
  return text.replace(/(^|[ T])(\d{2}:\d{2}:\d{2})(?:\.(\d{1,9}))?/, (_, lead, time, fraction = '') => `${lead}${time}.${fraction.padEnd(digits, '0').slice(0, digits)}`)
}

export function normalizeComparableValue(value: unknown, dataType: unknown): string {
  const key = dataTypeKey(dataType)
  const raw = String(value ?? '')
  if (['string', 'wstring', 's7char', 'wchar'].includes(key)) return raw
  let text = raw.trim()
  if (['dtl', 'date', 'tod', 'ltod', 'dt', 'ldt'].includes(key)) text = text.replace(/^[A-Z_]+#/i, '')
  if (key === 'ltime') text = text.replace(/^(?:LTIME|LT)#/i, '')
  else if (key === 'time') text = text.replace(/^T(?:IME)?#/i, '')
  else if (key === 's5time') text = text.replace(/^S5T(?:IME)?#/i, '')
  if (['dtl', 'ldt'].includes(key)) text = normalizeFraction(text.replace('T', ' '), 9)
  else if (key === 'dt') text = normalizeFraction(text.replace('T', ' '), 3)
  else if (key === 'ltod') text = normalizeFraction(text, 9)
  else if (key === 'tod') text = normalizeFraction(text, 3)
  return text.toLowerCase()
}
