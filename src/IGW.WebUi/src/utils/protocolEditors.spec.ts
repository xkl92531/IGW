import { describe, expect, it } from 'vitest'
import { parseModbusAddress, validateModbusTag, modbusWireSummary, resolveTagProtocolContext, validateProtocolSettings } from './protocolEditors'

describe('Modbus editor wire addresses', () => {
  it('preserves explicit offsets and decodes both legacy address bases', () => {
    expect(parseModbusAddress('HR:0.3', '启用')).toEqual({ area: 'HR', offset: 0, bit: 3 })
    expect(parseModbusAddress('400001.15', '禁用')).toEqual({ area: 'HR', offset: 0, bit: 15 })
    expect(parseModbusAddress('40000', 'true')).toEqual({ area: 'HR', offset: 0, bit: null })
    expect(parseModbusAddress('10001')).toEqual({ area: 'DI', offset: 0, bit: null })
    expect(parseModbusAddress('40000')).toBeNull()
  })
  it('rejects invalid areas, types, input writes, bit ranges and register overflow', () => {
    for (const tag of [
      { address: 'DI:0', dataType: 'bool', access: 'readWrite' },
      { address: 'C:0', dataType: 'int16', access: 'read' },
      { address: 'HR:0.16', dataType: 'bool', access: 'read' },
      { address: 'HR:0', dataType: 'bool', access: 'read' },
      { address: 'HR:65535', dataType: 'float', access: 'read' },
      { address: 'HR:0', dataType: 'string', access: 'read' },
      { address: 'HR:0.3', dataType: 'int16', access: 'read' },
    ]) expect(validateModbusTag(tag)).toBeTruthy()
    expect(validateModbusTag({ address: 'HR:0.3', dataType: 'bool', access: 'readWrite' })).toBe('')
  })
  it('shows distinct coil and register functions, including forced multiple writes', () => {
    expect(modbusWireSummary({ address: 'C:2', dataType: 'bool', access: 'readWrite' }, {})).toContain('FC01 / FC05')
    expect(modbusWireSummary({ address: 'HR:2', dataType: 'int16', access: 'readWrite' }, { writeMode: 'multiple' })).toContain('FC03 / FC16')
    expect(modbusWireSummary({ address: 'IR:2', dataType: 'double', access: 'read' }, {})).toContain('FC04')
  })
  it('validates device request limits before storing settings', () => {
    expect(validateProtocolSettings('Modbus.Tcp', { regBlock: '126' })).toBeTruthy()
    expect(validateProtocolSettings('Modbus.Tcp', { coilBlock: '1', unitId: '1', regBlock: '125' })).toBe('')
    // 其它驱动(含本版本不支持的旧通道)的设备设置不在这里校验。
    expect(validateProtocolSettings('Legacy.Tcp', { timeoutMs: '99' })).toBe('')
  })
  it('bounds coil arrays and accounts for register Boolean bit packing', () => {
    expect(validateModbusTag({ address: 'C:65535', dataType: 'bool', access: 'read', array: { dimensions: [2] } })).toBeTruthy()
    expect(validateModbusTag({ address: 'HR:65535.0', dataType: 'bool', access: 'read', array: { dimensions: [16], elementStrideBits: 1 } })).toBe('')
    expect(validateModbusTag({ address: 'HR:0', dataType: 'float', access: 'readWrite' }, { writeMode: 'single' })).toBe('')
    expect(modbusWireSummary({ address: 'HR:0', dataType: 'float', access: 'readWrite' }, { writeMode: 'single' })).toContain('FC06')
    expect(modbusWireSummary({ address: 'IR:0.3', dataType: 'bool', access: 'read' }, {})).not.toContain('read-modify-write')
    expect(modbusWireSummary({ address: 'HR:0.0', dataType: 'bool', access: 'readWrite', array: { dimensions: [16], elementStrideBits: 1 } }, {})).toContain('FC06')
    expect(validateModbusTag({ address: 'HR:65535.0', dataType: 'bool', access: 'read', array: { dimensions: [2], elementStrideBytes: 2 } })).toBeTruthy()
  })
})

describe('protocol context contracts', () => {
  it('resolves tag or target list ownership even if another channel is selected', () => {
    const tag = { address: 'HR:0' }; const tags = [tag]
    const device = { name: 'PLC', groups: [{ name: 'Group', tags }], settings: { zeroBased: '启用' } }
    const project = { channels: [{ name: 'Modbus', driverId: 'Modbus.Tcp', devices: [device] }] }
    expect(resolveTagProtocolContext(project, tag, null)).toMatchObject({ driverId: 'Modbus.Tcp', prefix: 'Modbus.PLC.Group', device })
    expect(resolveTagProtocolContext(project, null, tags)?.driverId).toBe('Modbus.Tcp')
  })
})

