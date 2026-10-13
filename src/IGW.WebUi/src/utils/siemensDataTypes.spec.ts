import { describe, expect, it } from 'vitest'
import {
  dataTypeDisplay, dataTypeEditorValue, dataTypeKey, editableDataTypes, editableDataTypesForDriver, normalizeComparableValue,
  parseArrayWriteInput, parsePointWriteInput, pointWriteHint,
} from './siemensDataTypes'

describe('Siemens datatype UI contract', () => {
  it('limits Modbus to supported register types', () => {
    expect(editableDataTypesForDriver('Modbus.Tcp').some(option => option.value === 'string')).toBe(false)
    expect(editableDataTypesForDriver('Modbus.Tcp').some(option => option.value === 'byteArray')).toBe(false)
  })
  it('maps shared enum values 17 through 34 without changing legacy values', () => {
    expect(dataTypeKey(13)).toBe('char')
    expect(Array.from({ length: 18 }, (_, index) => dataTypeKey(index + 17))).toEqual([
      'dtl', 'date', 'tod', 'ltod', 'time', 'ltime', 'dt', 'ldt', 's5time',
      's7char', 'wchar', 'word', 'dword', 'lword', 'timer', 'counter', 'sint', 'datetime',
    ])
    expect(dataTypeDisplay(17)).toBe('DTL')
    expect(dataTypeDisplay(34)).toBe('DateTime (UTC)')
  })

  it('offers all Siemens semantic types and familiar PLC aliases to editors', () => {
    const byValue = new Map(editableDataTypes.map(option => [option.value, option.label]))
    expect(byValue.get('int16')).toContain('INT')
    expect(byValue.get('int32')).toContain('DINT')
    expect(byValue.get('float')).toContain('REAL')
    expect(byValue.get('dtl')).toContain('DTL')
    expect(byValue.get('lword')).toContain('LWORD')
    expect(byValue.has('dateTime')).toBe(true)
    expect(byValue.has('struct')).toBe(false)
    expect(byValue.get('ldt')).toContain('DATE_AND_LTIME')
    expect(byValue.has('s7Char')).toBe(true)
    expect(byValue.has('wChar')).toBe(true)
    expect(editableDataTypesForDriver('Modbus.Tcp').some(option => option.value === 'dtl')).toBe(false)
    expect(editableDataTypesForDriver('OpcUa.Client').some(option => option.value === 'dtl')).toBe(true)
    expect(dataTypeEditorValue('S7Char')).toBe('s7Char')
    expect(dataTypeEditorValue('wchar')).toBe('wChar')
    expect(dataTypeEditorValue('DateTime')).toBe('dateTime')
  })

  it('keeps every enum 17..34 out of non-OPC driver choices while retaining generic unsigned types', () => {
    const nonOpc = editableDataTypesForDriver('Modbus.Tcp')
    const newEnumKeys = new Set(Array.from({ length: 18 }, (_, index) => dataTypeKey(index + 17)))
    expect(nonOpc.filter(option => newEnumKeys.has(dataTypeKey(option.value)))).toEqual([])
    expect(nonOpc.find(option => option.value === 'uint16')?.label).toContain('Word')
    expect(nonOpc.find(option => option.value === 'uint32')?.label).toContain('DWord')
  })

  it('keeps exact-width 64-bit integers as decimal strings and numeric widths as numbers', () => {
    expect(parsePointWriteInput('9223372036854775807', 'int64')).toEqual({ value: '9223372036854775807' })
    expect(parsePointWriteInput('18446744073709551615', 'lword')).toEqual({ value: '18446744073709551615' })
    expect(parsePointWriteInput('-1', 'uint64').error).toContain('超出')
    expect(parsePointWriteInput('18446744073709551616', 'lword').error).toContain('超出')
    expect(parsePointWriteInput('-128', 'sint')).toEqual({ value: -128 })
    expect(parsePointWriteInput('65535', 'word')).toEqual({ value: 65535 })
    expect(parsePointWriteInput('1.5', 'word').error).toContain('整数')
  })

  it('keeps Siemens calendar text intact and compares temporal prefixes and precision safely', () => {
    expect(parsePointWriteInput('2026-09-05 12:03:04.123456789', 'dtl')).toEqual({ value: '2026-09-05 12:03:04.123456789' })
    expect(normalizeComparableValue('LTIME#123ns', 'ltime')).toBe('123ns')
    expect(normalizeComparableValue('LT#123ns', 'ltime')).toBe('123ns')
    expect(normalizeComparableValue('2026-09-05 12:03:04.1', 'dtl')).toBe('2026-09-05 12:03:04.100000000')
    expect(normalizeComparableValue('2026-09-05T12:03:04.100000000', 'dtl')).toBe('2026-09-05 12:03:04.100000000')
    expect(normalizeComparableValue('TOD#12:03:04.1', 'tod')).toBe('12:03:04.100')
    expect(normalizeComparableValue('12:03:04.100', 'tod')).toBe('12:03:04.100')
    expect(pointWriteHint('tod')).toContain('HH:mm:ss.fff')
    expect(pointWriteHint('time')).toContain('ms')
  })

  it('rejects character writes that are not exactly one character', () => {
    expect(parsePointWriteInput('A', 's7char')).toEqual({ value: 'A' })
    expect(parsePointWriteInput(' ', 's7Char')).toEqual({ value: ' ' })
    expect(parsePointWriteInput('AB', 's7char').error).toContain('一个字符')
    expect(parsePointWriteInput('-128', 'char')).toEqual({ value: -128 })
    expect(parsePointWriteInput('A', 'char').error).toContain('整数')
  })

  it('preserves empty and whitespace-sensitive STRING values', () => {
    expect(parsePointWriteInput('', 'string')).toEqual({ value: '' })
    expect(parsePointWriteInput('  padded  ', 'wString')).toEqual({ value: '  padded  ' })
    expect(normalizeComparableValue(' A ', 'string')).toBe(' A ')
    expect(normalizeComparableValue('A', 's7Char')).toBe('A')
    expect(normalizeComparableValue('a', 's7Char')).toBe('a')
    expect(normalizeComparableValue(' ', 'wChar')).toBe(' ')
  })

  it('requires quoted decimal strings for exact 64-bit array slices', () => {
    expect(parseArrayWriteInput('18446744073709551615', 'lword', false)).toEqual({ value: '18446744073709551615' })
    expect(parseArrayWriteInput('["18446744073709551615","0"]', 'lword', true)).toEqual({ value: ['18446744073709551615', '0'] })
    expect(parseArrayWriteInput('[18446744073709551615]', 'lword', true).error).toContain('字符串')
    expect(parseArrayWriteInput('["-9223372036854775808","0"]', 'int64', true)).toEqual({ value: ['-9223372036854775808', '0'] })
    expect(parseArrayWriteInput('["-1"]', 'uint64', true).error).toContain('有效范围')
  })
})
