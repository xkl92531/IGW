export type ModbusArea = 'C' | 'DI' | 'IR' | 'HR'
export interface ModbusAddress { area: ModbusArea; offset: number; bit: number | null }
export const modbusRegisterTypes = ['bool', 'byte', 'char', 'int16', 'uint16', 'int32', 'uint32', 'int64', 'uint64', 'float', 'double', 'bcd', 'lBcd']
const registerWidths: Record<string, number> = { bool: 1, byte: 1, char: 1, int16: 1, uint16: 1, int32: 2, uint32: 2, int64: 4, uint64: 4, float: 2, double: 4, bcd: 1, lbcd: 2 }
export function parseModbusAddress(address: string, zeroBased: unknown = false): ModbusAddress | null {
  const text = String(address || '').trim().toUpperCase()
  const explicit = /^(C|DI|IR|HR):(\d+)(?:\.(\d+))?$/.exec(text)
  const legacy = /^([0134])(\d+)(?:\.(\d+))?$/.exec(text)
  const match = explicit || legacy
  if (!match) return null
  const area = explicit ? match[1] as ModbusArea : ({ '0': 'C', '1': 'DI', '3': 'IR', '4': 'HR' } as const)[match[1] as '0' | '1' | '3' | '4']
  const enabled = ['true', '1', '启用', 'enabled', 'yes'].includes(String(zeroBased).toLowerCase())
  const offset = Number(match[2]) - (!explicit && !enabled ? 1 : 0)
  const bit = match[3] === undefined ? null : Number(match[3])
  if (!Number.isInteger(offset) || offset < 0 || offset > 65535 || (bit !== null && (bit > 15 || area === 'C' || area === 'DI'))) return null
  return { area, offset, bit }
}
export function validateModbusTag(tag: any, settings: any = {}): string {
  const address = parseModbusAddress(tag.address, settings.zeroBased)
  if (!address) return 'Modbus 地址无效：选择区域，偏移范围 0–65535，寄存器位范围 0–15。'
  const type = String(tag.dataType).toLowerCase()
  const dimensions = tag.array?.dimensions || [1]
  const count = dimensions.reduce((total: number, value: number) => total * value, 1)
  if (!dimensions.length || dimensions.some((value: number) => !Number.isSafeInteger(value) || value < 1) || !Number.isSafeInteger(count)) return '连续元素数量必须为正整数。'
  if ((address.area === 'IR' || address.area === 'DI') && tag.access !== 'read') return '输入区域仅支持只读。'
  if (address.area === 'C' || address.area === 'DI') return type !== 'bool' ? '线圈和离散输入仅支持 Boolean。' : address.offset + count > 65536 ? '读取范围超出 Modbus 地址上限。' : ''
  if (!registerWidths[type]) return 'Modbus 不支持此数据类型。'
  if ((type === 'bool') !== (address.bit !== null)) return '寄存器 Boolean 必须选择位；数值类型不能指定位。'
  const span = type === 'bool' && tag.array
    ? Math.ceil(((address.bit || 0) + (count - 1) * (tag.array.elementStrideBits || (tag.array.elementStrideBytes ? tag.array.elementStrideBytes * 8 : 1)) + 1) / 16)
    : (count - 1) * (tag.array?.elementStrideBytes ? tag.array.elementStrideBytes / 2 : registerWidths[type]) + registerWidths[type]
  if (address.offset + span > 65536) return '读取范围超出 Modbus 地址上限。'
  return ''
}
export function modbusWireSummary(tag: any, settings: any = {}): string {
  const address = parseModbusAddress(tag.address, settings.zeroBased)
  if (!address) return ''
  const read = { C: 'FC01', DI: 'FC02', IR: 'FC04', HR: 'FC03' }[address.area]
  const count = tag.array?.dimensions?.reduce((total: number, value: number) => total * value, 1) || 1
  const width = registerWidths[String(tag.dataType).toLowerCase()] || 1
  const wireCount = address.bit !== null && tag.array
    ? Math.ceil((address.bit + (count - 1) * (tag.array.elementStrideBits || (tag.array.elementStrideBytes ? tag.array.elementStrideBytes * 8 : 1)) + 1) / 16)
    : count * width
  const multiple = settings.writeMode !== 'single' && (settings.writeMode === 'multiple' || wireCount > 1)
  const write = address.area === 'C' ? multiple ? 'FC15' : 'FC05' : address.area === 'HR' ? multiple ? 'FC16' : 'FC06' : ''
  return `${read}${tag.access !== 'read' && write ? ` / ${write}` : ''} · offset ${address.offset} (0x${address.offset.toString(16).toUpperCase().padStart(4, '0')})${address.bit !== null && address.area === 'HR' && tag.access !== 'read' ? ' · 读-改-写 / read-modify-write' : ''}`
}
export function resolveTagProtocolContext(project: any, tag: any, targetList: any[] | null): any {
  for (const channel of project?.channels || []) for (const device of channel.devices || []) {
    const visit = (holder: any, prefix: string): any => {
      if ((tag && holder.tags?.includes(tag)) || (targetList && holder.tags === targetList)) return { channel, device, driverId: channel.driverId, prefix }
      for (const group of holder.groups || holder.subGroups || []) {
        const found = visit(group, `${prefix}.${group.name}`)
        if (found) return found
      }
      return null
    }
    const found = visit(device, `${channel.name}.${device.name}`)
    if (found) return found
  }
  return null
}
export function validateProtocolSettings(driverId: string, settings: any): string {
  if (driverId !== 'Modbus.Tcp') return ''
  const ranges: Record<string, [number, number]> = { unitId: [0, 255], regBlock: [1, 125], coilBlock: [1, 2000], timeoutMs: [1, 2147483647] }
  for (const [key, [min, max]] of Object.entries(ranges)) if (settings[key] !== undefined) {
    const value = Number(settings[key])
    if (!Number.isInteger(value) || value < min || value > max) return `${key}: ${min}–${max}`
  }
  if (settings.writeMode && !['auto', 'single', 'multiple'].includes(settings.writeMode)) return 'writeMode: auto / single / multiple'
  return ''
}
