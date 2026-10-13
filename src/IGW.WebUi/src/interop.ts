// KEP / DCS 互操作(原桌面配置器的 KEP/DCS 导入导出，已移植到浏览器端)
// 保持与桌面完全一致的映射规则:KEP 点带 common./servermain. 前缀键;DCS 类型码 0/1/…;S7 地址 DB{n}.DB{X/B/W/D}{off}[.bit]。
// 数据类型用网页工程的 camelCase 字符串枚举(bool/int16/float/string/…),与 /api/v1/project 往返一致。

type Tag = { id: string; name: string; address: string; dataType: string; isArray?: boolean; array?: any | null; access: string; scanRateMs: number; description?: string; stringLength?: number; textEncoding?: 'auto' | 'utf8' | 'gb18030' | null; metadata?: Record<string, string> | null; [k: string]: any }
type Group = { id: string; name: string; description?: string; tags: Tag[]; subGroups: Group[] }
type Device = { id: string; name: string; endpoint: string; description?: string; settings: Record<string, any>; tags: Tag[]; groups: Group[] }
type Channel = { id: string; name: string; driverId: string; description?: string; settings: Record<string, any>; devices: Device[] }
type Project = { name: string; channels: Channel[]; opcUa?: any; [k: string]: any }

const newId = () => Array.from({ length: 8 }, () => Math.floor(Math.random() * 16).toString(16)).join('')
const str = (e: any, name: string) => (e && typeof e[name] === 'string' ? e[name] : '')
const intv = (e: any, name: string, def: number) => (e && Number.isFinite(e[name]) ? Math.trunc(e[name]) : def)
const boolv = (e: any, name: string) => !!(e && e[name] === true)

// ============ KEP 原生 JSON 导入 ============
export function importKepJson(json: string): { proj: Project; channels: number; devices: number; tags: number; notes: string[] } {
  const proj: Project = { name: 'KEP导入', channels: [] }
  let root: any
  try { root = JSON.parse(json) } catch { return { proj, channels: 0, devices: 0, tags: 0, notes: ['JSON 解析失败'] } }
  const p = root?.project
  if (!p || !Array.isArray(p.channels)) return { proj, channels: 0, devices: 0, tags: 0, notes: ['未找到 project.channels(不是 KEP 原生 JSON?)'] }
  proj.name = str(p, 'servermain.PROJECT_TITLE') || 'KEP导入'
  let devCount = 0, tagCount = 0
  const drvMap = new Set<string>()
  for (const chEl of p.channels) {
    const kepDrv = str(chEl, 'servermain.MULTIPLE_TYPES_DEVICE_DRIVER')
    const drvId = mapKepDriver(kepDrv)
    drvMap.add(`${kepDrv} → ${drvId}`)
    const ch: Channel = {
      id: newId(), name: str(chEl, 'common.ALLTYPES_NAME'), driverId: drvId,
      description: str(chEl, 'common.ALLTYPES_DESCRIPTION'), settings: kepChannelSettings(chEl), devices: [],
    }
    for (const dEl of (chEl.devices || [])) {
      const dev: Device = {
        id: newId(), name: str(dEl, 'common.ALLTYPES_NAME'), endpoint: kepEndpoint(dEl, drvId),
        description: str(dEl, 'common.ALLTYPES_DESCRIPTION'), settings: kepDeviceSettings(dEl, drvId), tags: [], groups: [],
      }
      devCount++
      tagCount += kepAddTags(dEl, dev.tags)
      tagCount += kepAddGroups(dEl, dev.groups)
      ch.devices.push(dev)
    }
    proj.channels.push(ch)
  }
  const notes = ['驱动映射:' + [...drvMap].join(';')]
  const igwProject = root?.igw?.format === 'IGW.JSON.Configuration' ? root.igw.project : null
  if (igwProject && typeof igwProject === 'object' && !Array.isArray(igwProject)) {
    // 连接性结构以 KEP project 为准；IGW 扩展只补回 MES 等 KEP 无法表达的工程级配置。
    // 早期 IGW JSON 曾在扩展中保存完整 channels；只取其运行元数据覆盖 KEP 重建结果，
    // 既兼容旧文件，也避免用旧副本覆盖用户后来编辑过的 KEP 点位结构。
    if (Array.isArray(igwProject.channels))
      applyIgwConnectivity(proj, { channels: igwProject.channels })
    const { channels: _ignoredChannels, ...projectExtensions } = igwProject
    Object.assign(proj, cloneJson(projectExtensions))
    notes.unshift('已恢复 IGW 专属配置（含采集流程）')
  }
  applyIgwConnectivity(proj, root?.igw?.connectivity)
  return { proj, channels: proj.channels.length, devices: devCount, tags: tagCount, notes }
}

function kepChannelSettings(channel: any): Record<string, any> {
  const adapter = str(channel, 'servermain.CHANNEL_ETHERNET_COMMUNICATIONS_NETWORK_ADAPTER_STRING').trim()
  return adapter ? { networkAdapter: adapter } : {}
}

function kepAddGroups(node: any, into: Group[]): number {
  let n = 0
  for (const gEl of (node?.tag_groups || [])) {
    const g: Group = {
      id: newId(), name: str(gEl, 'common.ALLTYPES_NAME'), description: str(gEl, 'common.ALLTYPES_DESCRIPTION'),
      tags: [], subGroups: [],
    }
    n += kepAddTags(gEl, g.tags)
    n += kepAddGroups(gEl, g.subGroups)
    into.push(g)
  }
  return n
}
function kepAddTags(node: any, into: Tag[]): number {
  let n = 0
  for (const tEl of (node?.tags || [])) {
    const name = str(tEl, 'common.ALLTYPES_NAME')
    if (!name) continue
    const addr = str(tEl, 'servermain.TAG_ADDRESS')
    const typeCode = intv(tEl, 'servermain.TAG_DATA_TYPE', 4)
    const mapped = kepMapType(typeCode, addr)
    into.push({
      id: newId(), name, address: addr,
      dataType: mapped.dataType,
      isArray: mapped.isArray,
      metadata: mapped.isArray ? {
        'array.sourceFormat': 'KEP JSON',
        'array.sourceAddress': addr,
        'kep.dataTypeCode': String(typeCode),
      } : null,
      access: intv(tEl, 'servermain.TAG_READ_WRITE_ACCESS', 1) === 0 ? 'read' : 'readWrite',
      scanRateMs: intv(tEl, 'servermain.TAG_SCAN_RATE_MILLISECONDS', 1000),
      description: str(tEl, 'common.ALLTYPES_DESCRIPTION'),
    })
    n++
  }
  return n
}
function mapKepDriver(kep: string): string {
  const s = (kep || '').toLowerCase()
  if (s.includes('opc ua')) return 'OpcUa.Client'
  if (s.includes('simulator')) return 'Simulator'
  if (s.includes('omron')) return s.includes('fins') ? 'Omron.Fins' : 'Omron.Cip'
  if (s.includes('fins')) return 'Omron.Fins'
  if (s.includes('allen') || s.includes('controllogix') || s.includes('compactlogix') || s.includes('micro8') || s.includes('logix')) return 'AB.Eip'
  if (s.includes('siemens') || s.includes('s7')) return 'Siemens.S7'
  if (s.includes('modbus')) return 'Modbus.Tcp'
  if (s.includes('mitsubishi')) return 'Mitsubishi.Mc'
  return kep && kep.trim() ? kep : 'Modbus.Tcp'
}
function kepEndpoint(dev: any, drvId: string): string {
  const ip = str(dev, 'servermain.DEVICE_ID_STRING')
  if (!ip.trim()) return ''
  let port = 0
  for (const k of Object.keys(dev)) if (k.endsWith('DEVICE_PORT_NUMBER') && Number.isFinite(dev[k])) { port = Math.trunc(dev[k]); break }
  if (port <= 0) port = drvId === 'Siemens.S7' ? 102 : drvId === 'Modbus.Tcp' ? 502 : drvId === 'Omron.Fins' ? 9600 : 44818
  return `${ip}:${port}`
}
function kepDeviceSettings(device: any, driverId: string): Record<string, any> {
  if (driverId !== 'Siemens.S7') return {}
  const models: Record<number, string> = { 0: 'S7-200', 1: 'S7-300', 2: 'S7-400', 3: 'S7-1200', 4: 'S7-1500' }
  const model = models[intv(device, 'servermain.DEVICE_MODEL', -1)]
  const settings: Record<string, any> = model ? { model } : {}
  const rackKey = 'siemens_tcpip_ethernet.DEVICE_S7_COMMUNICATIONS_CPU_RACK'
  const slotKey = 'siemens_tcpip_ethernet.DEVICE_S7_COMMUNICATIONS_CPU_SLOT'
  if (Number.isFinite(device?.[rackKey])) settings.rack = String(Math.trunc(device[rackKey]))
  if (Number.isFinite(device?.[slotKey])) settings.slot = String(Math.trunc(device[slotKey]))
  return settings
}
function kepMapType(code: number, addr: string): { dataType: string; isArray: boolean } {
  if (addr && /\/\d+\s*$/.test(addr)) return { dataType: 'bool', isArray: false }
  const arrayTypes: Record<number, string> = {
    20: 'string', 21: 'bool', 22: 'char', 23: 'byte', 24: 'int16', 25: 'uint16', 26: 'int32',
    27: 'uint32', 28: 'float', 29: 'double', 30: 'int16', 31: 'int32', 32: 'dateTime', 33: 'int64', 34: 'uint64',
  }
  if (arrayTypes[code]) return { dataType: arrayTypes[code], isArray: true }
  const scalarTypes: Record<number, string> = {
    0: 'string', 1: 'bool', 2: 'char', 3: 'byte', 4: 'int16', 5: 'uint16', 6: 'int32',
    7: 'uint32', 8: 'float', 9: 'double', 10: 'int16', 11: 'int32', 12: 'dateTime', 13: 'int64', 14: 'uint64',
  }
  return { dataType: scalarTypes[code] || 'int16', isArray: false }
}

// ============ KEPServer 原生 JSON 导出 ============
// 对齐 Configuration API 的完整工程序列化格式:GET /config/v1/project?content=serialize。
// project 保持 KEP 连接性结构；igw 扩展承载 MES/历史/授权等 KEP 无法表达的工程级配置。
// 扩展不重复 channels，避免百万点工程的导出文件和内存占用翻倍。
export function exportKepJson(proj: Project): string {
  const project = {
    'common.ALLTYPES_DESCRIPTION': '',
    'servermain.PROJECT_TITLE': proj.name || 'IGW',
    channels: (proj.channels || []).map(kepChannel),
  }
  const { channels: _ignoredChannels, ...projectExtensions } = proj
  const igw = {
    format: 'IGW.JSON.Configuration',
    schemaVersion: 3,
    project: cloneJson(projectExtensions),
    // KEP project 负责通道/点位兼容；该轻量侧车只保存 KEP 无法表达的 IGW 运行参数，
    // 避免为百万点工程重复序列化 channels，又保证 IGW 导出→导入完全保真。
    connectivity: {
      channels: (proj.channels || []).map((channel) => ({
        name: channel.name,
        driverId: channel.driverId,
        settings: cloneJson(channel.settings || {}),
        devices: (channel.devices || []).map((device) => ({
          name: device.name,
          endpoint: device.endpoint,
          settings: cloneJson(device.settings || {}),
          tagExtensions: collectTagExtensions(device),
        })),
      })),
    },
  }
  return JSON.stringify({ project, igw }, null, 2)
}

function applyIgwConnectivity(proj: Project, connectivity: any): void {
  if (!Array.isArray(connectivity?.channels)) return
  for (const savedChannel of connectivity.channels) {
    if (!savedChannel || typeof savedChannel !== 'object') continue
    const channel = proj.channels.find((item) => item.name === savedChannel.name)
    if (!channel) continue
    if (typeof savedChannel.driverId === 'string' && savedChannel.driverId.trim())
      channel.driverId = savedChannel.driverId
    if (savedChannel.settings && typeof savedChannel.settings === 'object' && !Array.isArray(savedChannel.settings))
      channel.settings = cloneJson(savedChannel.settings)
    if (!Array.isArray(savedChannel.devices)) continue
    for (const savedDevice of savedChannel.devices) {
      if (!savedDevice || typeof savedDevice !== 'object') continue
      const device = channel.devices.find((item) => item.name === savedDevice.name)
      if (!device) continue
      if (typeof savedDevice.endpoint === 'string') device.endpoint = savedDevice.endpoint
      if (savedDevice.settings && typeof savedDevice.settings === 'object' && !Array.isArray(savedDevice.settings))
        device.settings = cloneJson(savedDevice.settings)
      applyTagExtensions(device, savedDevice.tagExtensions)
    }
  }
}

type TagExtension = {
  path: string[]
  id?: string
  array?: any | null
  metadata?: Record<string, string> | null
  stringLength?: number | null
  textEncoding?: string | null
}

function collectTagExtensions(device: Device): TagExtension[] {
  const result: TagExtension[] = []
  const visit = (container: { tags?: Tag[]; groups?: Group[]; subGroups?: Group[] }, parent: string[]) => {
    for (const tag of container.tags || []) {
      if (!tag.array && !tag.metadata && tag.stringLength == null && tag.textEncoding == null) continue
      result.push({
        path: [...parent, tag.name], id: tag.id || undefined,
        array: tag.array ? cloneJson(tag.array) : null,
        metadata: tag.metadata ? cloneJson(tag.metadata) : null,
        stringLength: tag.stringLength ?? null,
        textEncoding: tag.textEncoding ?? null,
      })
    }
    for (const group of container.groups || container.subGroups || [])
      visit(group, [...parent, group.name])
  }
  visit(device, [])
  return result
}

function applyTagExtensions(device: Device, extensions: unknown): void {
  if (!Array.isArray(extensions)) return
  for (const raw of extensions) {
    if (!raw || typeof raw !== 'object' || !Array.isArray((raw as any).path)) continue
    const path = (raw as any).path.map((part: unknown) => String(part))
    if (!path.length) continue
    let container: any = device
    for (const segment of path.slice(0, -1)) {
      const groups = container.groups || container.subGroups || []
      container = groups.find((group: any) => group.name === segment)
      if (!container) break
    }
    if (!container) continue
    const tag = (container.tags || []).find((item: Tag) => item.name === path[path.length - 1])
    if (!tag) continue
    const extension = raw as TagExtension
    if (extension.id) tag.id = extension.id
    if (extension.array) {
      tag.array = cloneJson(extension.array)
      tag.isArray = true
      if (typeof extension.array.elementType === 'string') tag.dataType = extension.array.elementType
    }
    if (extension.metadata) tag.metadata = cloneJson(extension.metadata)
    if (extension.stringLength != null) tag.stringLength = extension.stringLength
    if (extension.textEncoding != null) tag.textEncoding = extension.textEncoding as Tag['textEncoding']
  }
}

function cloneJson<T>(value: T): T {
  return JSON.parse(JSON.stringify(value)) as T
}
function kepChannel(channel: Channel): any {
  const driver = kepDriverName(channel.driverId)
  const result: any = {
    'common.ALLTYPES_NAME': channel.name,
    'common.ALLTYPES_DESCRIPTION': channel.description || '',
    'servermain.MULTIPLE_TYPES_DEVICE_DRIVER': driver,
    devices: (channel.devices || []).map((device) => kepDevice(channel, device, driver)),
  }
  const adapter = channel.settings?.networkAdapter
  if (typeof adapter === 'string' && adapter.trim() && adapter !== '默认值')
    result['servermain.CHANNEL_ETHERNET_COMMUNICATIONS_NETWORK_ADAPTER_STRING'] = adapter
  return result
}
function kepDevice(channel: Channel, device: Device, driver: string): any {
  const endpoint = splitEndpoint(device.endpoint)
  const result: any = {
    'common.ALLTYPES_NAME': device.name,
    'common.ALLTYPES_DESCRIPTION': device.description || '',
    'servermain.DEVICE_CHANNEL_ASSIGNMENT': channel.name,
    'servermain.MULTIPLE_TYPES_DEVICE_DRIVER': driver,
    'servermain.DEVICE_ID_STRING': endpoint.host,
    'servermain.DEVICE_DATA_COLLECTION': device.settings?.dataCollection !== '禁用',
    'servermain.DEVICE_SIMULATED': false,
    tags: (device.tags || []).map(kepTag),
    tag_groups: (device.groups || []).map(kepGroup),
  }
  if (channel.driverId === 'Siemens.S7') {
    const models: Record<string, number> = { 'S7-200': 0, 'S7-300': 1, 'S7-400': 2, 'S7-1200': 3, 'S7-1500': 4 }
    result['servermain.DEVICE_MODEL'] = models[String(device.settings?.model || '')] ?? 4
    result['siemens_tcpip_ethernet.DEVICE_COMMUNICATIONS_PORT_NUMBER'] = endpoint.port || 102
    result['siemens_tcpip_ethernet.DEVICE_S7_COMMUNICATIONS_CPU_RACK'] = finiteInt(device.settings?.rack, 0)
    result['siemens_tcpip_ethernet.DEVICE_S7_COMMUNICATIONS_CPU_SLOT'] = finiteInt(device.settings?.slot, 1)
  }
  return result
}
function kepGroup(group: Group): any {
  return {
    'common.ALLTYPES_NAME': group.name,
    'common.ALLTYPES_DESCRIPTION': group.description || '',
    'servermain.TAGGROUP_AUTOGENERATED': false,
    tags: (group.tags || []).map(kepTag),
    tag_groups: (group.subGroups || []).map(kepGroup),
  }
}
function kepTag(tag: Tag): any {
  return {
    'common.ALLTYPES_NAME': tag.name,
    'common.ALLTYPES_DESCRIPTION': tag.description || '',
    'servermain.TAG_ADDRESS': tag.address || '',
    'servermain.TAG_DATA_TYPE': kepTypeCode(tag.dataType, !!tag.isArray),
    'servermain.TAG_READ_WRITE_ACCESS': tag.access === 'read' ? 0 : 1,
    'servermain.TAG_SCAN_RATE_MILLISECONDS': kepScanRate(tag.scanRateMs),
  }
}
function kepDriverName(driverId: string): string {
  const known: Record<string, string> = {
    'Siemens.S7': 'Siemens TCP/IP Ethernet',
    'Modbus.Tcp': 'Modbus TCP/IP Ethernet',
    'Omron.Fins': 'Omron FINS Ethernet',
    'Omron.Cip': 'Omron NJ Ethernet',
    'AB.Eip': 'Allen-Bradley ControlLogix Ethernet',
    'Mitsubishi.Mc': 'Mitsubishi Ethernet',
    'OpcUa.Client': 'OPC UA Client',
    Simulator: 'Simulator',
  }
  return known[driverId] || driverId || 'Simulator'
}
function kepTypeCode(dataType: string, isArray: boolean): number {
  const scalar: Record<string, number> = {
    string: 0, wString: 0, bool: 1, char: 2, byte: 3, int16: 4, uint16: 5, int32: 6,
    uint32: 7, float: 8, double: 9, dateTime: 12, int64: 13, uint64: 14,
  }
  const code = scalar[dataType] ?? 4
  if (!isArray) return code
  if (code === 0) return 20
  if (code >= 1 && code <= 14) return code + 20
  return 24
}
function kepScanRate(value: number): number {
  if (!Number.isFinite(value)) return 100
  return Math.min(99_999_990, Math.max(10, Math.round(value / 10) * 10))
}
function splitEndpoint(endpoint: string): { host: string; port: number } {
  const value = String(endpoint || '').trim()
  const ipv6 = value.match(/^\[([^\]]+)\](?::(\d+))?$/)
  if (ipv6) return { host: ipv6[1], port: Number(ipv6[2] || 0) }
  const lastColon = value.lastIndexOf(':')
  if (lastColon > 0 && value.indexOf(':') === lastColon && /^\d+$/.test(value.slice(lastColon + 1)))
    return { host: value.slice(0, lastColon), port: Number(value.slice(lastColon + 1)) }
  return { host: value, port: 0 }
}
function finiteInt(value: any, fallback: number): number {
  const parsed = Number(value)
  return Number.isFinite(parsed) ? Math.trunc(parsed) : fallback
}

// ============ DCS Config.json 导入 ============
export function importDcsConfig(json: string): Project {
  const proj: Project = { name: 'DCS导入', channels: [] }
  let root: any
  try { root = JSON.parse(json) } catch { return proj }
  const p = root?.Project
  if (!p || !Array.isArray(p.Channels)) return proj
  for (const chEl of p.Channels) {
    const ch: Channel = { id: newId(), name: str(chEl, 'ALLTYPES_NAME'), driverId: 'Siemens.S7', settings: {}, devices: [] }
    for (const dEl of (chEl.Devices || [])) {
      const ip = str(dEl, 'DEVICE_ID_STRING')
      const dev: Device = { id: newId(), name: str(dEl, 'ALLTYPES_NAME'), endpoint: ip.trim() ? ip + ':102' : '', settings: { model: 'S7-1500' }, tags: [], groups: [] }
      for (const tEl of (dEl.AllTags || [])) {
        const { groupPath, tag } = parseDcsTag(tEl)
        if (tag) placeDcsTag(dev, groupPath, tag)
      }
      ch.devices.push(dev)
    }
    proj.channels.push(ch)
  }
  return proj
}
function parseDcsTag(t: any): { groupPath: string[]; tag: Tag | null } {
  const name = str(t, 'ParameterCode') || str(t, 'ALLTYPES_NAME')
  if (!name) return { groupPath: [], tag: null }
  const dt = dcsTypeFromCode(intv(t, 'TAG_DATA_TYPE', 4))
  const { addr, strLen } = convertDcsAddress(str(t, 'Address'), str(t, 'TAG_ADDRESS'), dt)
  const tag: Tag = {
    id: newId(), name, address: addr, dataType: dt,
    access: boolv(t, 'WriteOnly') ? 'write' : 'read',
    scanRateMs: 100,
    description: typeof t.ALLTYPES_DESCRIPTION === 'string' ? t.ALLTYPES_DESCRIPTION : '',
  }
  if (strLen != null) tag.stringLength = strLen
  const segs = str(t, 'FullPathName').split('.')
  const groupPath = segs.length > 3 ? segs.slice(2, segs.length - 1) : []
  return { groupPath, tag }
}
function placeDcsTag(dev: Device, groupPath: string[], tag: Tag) {
  if (groupPath.length === 0) { dev.tags.push(tag); return }
  let subs = dev.groups
  let cur: Group | null = null
  for (const gname of groupPath) {
    cur = subs.find((g) => g.name === gname) || null
    if (!cur) { cur = { id: newId(), name: gname, tags: [], subGroups: [] }; subs.push(cur) }
    subs = cur.subGroups
  }
  cur!.tags.push(tag)
}
function convertDcsAddress(dcsAddr: string, tagAddr: string, dt: string): { addr: string; strLen: number | null } {
  const m = (dcsAddr || '').match(/DB(\d+)\.(?:DB[XBWD])?(\d+)(?:\.(\d+))?(?:\$(\d+))?/i)
  if (!m) return { addr: dcsAddr || '', strLen: null }
  const db = parseInt(m[1], 10), off = parseInt(m[2], 10)
  let len: number | null = m[4] ? parseInt(m[4], 10) : null
  if (len == null && tagAddr) { const lm = tagAddr.match(/W?STRING\d+\.(\d+)/i); if (lm) len = parseInt(lm[1], 10) }
  if (dt === 'string' || dt === 'wString') return { addr: `DB${db}.DBB${off}`, strLen: len }
  if (dt === 'bool') { const bit = m[3] ? parseInt(m[3], 10) : 0; return { addr: `DB${db}.DBX${off}.${bit}`, strLen: null } }
  const area = (dt === 'byte' || dt === 'char') ? 'DBB' : (dt === 'int16' || dt === 'uint16') ? 'DBW' : 'DBD'
  return { addr: `DB${db}.${area}${off}`, strLen: null }
}
function dcsTypeFromCode(code: number): string {
  return ({ 0: 'string', 1: 'bool', 2: 'char', 3: 'byte', 4: 'int16', 5: 'uint16', 6: 'int32', 7: 'uint32', 8: 'float', 9: 'double' } as any)[code] || 'int16'
}

// ============ DCS Config.json 导出 ============
const DCS_DEFAULT_LICENSE_IDS = '[4324510511317258240, 4376362132657278976]'   // int64,超 JS 数值精度 → 占位符注入,保原样
const DEFAULT_STRING_LEN = 254

export function exportDcsConfig(proj: Project, gatewayIp = '127.0.0.1'): string {
  const port = proj.opcUa && proj.opcUa.port > 0 ? proj.opcUa.port : 4840
  const opc = `opc.tcp://${gatewayIp}:${port}`
  const root: any = {
    Project: { Channels: (proj.channels || []).map((ch) => dcsChannel(ch, opc)) },
    SystemSet: dcsSystemSet(opc),
    LicenseIds: '__IGW_LICENSE_IDS__',
  }
  let out = JSON.stringify(root, null, 2)
  out = out.replace('"__IGW_LICENSE_IDS__"', DCS_DEFAULT_LICENSE_IDS)
  return out
}
function dcsChannel(ch: Channel, opc: string): any {
  return { Devices: (ch.devices || []).map((d) => dcsDevice(ch, d, opc)), ALLTYPES_NAME: ch.name, TAG_DATA_TYPE: -1, KepwareTagDataType: -1 }
}
function dcsDevice(ch: Channel, d: Device, opc: string): any {
  const allTags: any[] = []
  for (const t of (d.tags || [])) allTags.push(dcsTag(ch.name, d.name, '', t))
  for (const g of (d.groups || [])) dcsFlatten(ch.name, d.name, '', g, allTags)
  return {
    ChannelName: ch.name, DEVICE_ID_STRING: ipOf(d.endpoint), DEVICE_COMMUNICATIONS_PORT_NUMBER: 0, AppendJson: '',
    StatusTag: dcsStatusTag(ch.name, d.name, d), DeviceCommunicationType: 0, IsOk: false, Tags: [], AllTags: allTags,
    Tag_groups: dcsTagGroups(ch.name, d.name, d), Ip: opc, Port: 0, NameSpaceIndex: 2,
    ALLTYPES_NAME: d.name, TAG_DATA_TYPE: -1, KepwareTagDataType: -1,
  }
}
function dcsFlatten(ch: string, dev: string, prefix: string, g: Group, acc: any[]) {
  const path = prefix.length === 0 ? g.name : prefix + '.' + g.name
  for (const t of (g.tags || [])) acc.push(dcsTag(ch, dev, path, t))
  for (const sg of (g.subGroups || [])) dcsFlatten(ch, dev, path, sg, acc)
}
function dcsTag(ch: string, dev: string, groupPath: string, t: Tag): any {
  const full = groupPath.length === 0 ? `${ch}.${dev}.${t.name}` : `${ch}.${dev}.${groupPath}.${t.name}`
  const { tagAddr, addr } = dcsConvertAddr(t)
  const code = dcsCode(t.dataType)
  const o: any = {
    FullPathName: full, AliasName: '', ParameterCode: t.name, TAG_ADDRESS: tagAddr,
    WriteOnly: t.access === 'write', IsCheckEmpty: false, ContentLength: 0, Address: addr, ALLTYPES_NAME: t.name,
  }
  if (t.description && t.description.trim()) o.ALLTYPES_DESCRIPTION = t.description
  o.TAG_DATA_TYPE = code; o.KepwareTagDataType = code
  return o
}
function dcsStatusTag(ch: string, dev: string, d: Device): any {
  const all: { path: string; t: Tag }[] = []
  for (const t of (d.tags || [])) all.push({ path: '', t })
  const walk = (pre: string, g: Group) => {
    const p = pre.length === 0 ? g.name : pre + '.' + g.name
    for (const t of (g.tags || [])) all.push({ path: p, t })
    for (const sg of (g.subGroups || [])) walk(p, sg)
  }
  for (const g of (d.groups || [])) walk('', g)
  let pick = all.find((x) => /mode|status|state|heart/i.test(x.t.name))
  if (!pick) pick = all[0]
  return pick ? dcsTag(ch, dev, pick.path, pick.t) : dcsEmptyStatusTag()
}
function dcsEmptyStatusTag(): any {
  return { FullPathName: '', AliasName: '', ParameterCode: '', TAG_ADDRESS: '', WriteOnly: false, IsCheckEmpty: false, ContentLength: 0, Address: '', ALLTYPES_NAME: '', TAG_DATA_TYPE: 4, KepwareTagDataType: 4 }
}
function dcsTagGroups(ch: string, dev: string, d: Device): any[] {
  const groups: any[] = []
  if ((d.tags || []).length > 0) groups.push(dcsGroupNode(dev, false, (d.tags || []).map((t) => dcsTag(ch, dev, '', t)), []))
  for (const g of (d.groups || [])) groups.push(dcsBuildGroupNode(ch, dev, '', g, true))
  return groups
}
function dcsBuildGroupNode(ch: string, dev: string, prefix: string, g: Group, topLevel: boolean): any {
  const path = prefix.length === 0 ? g.name : prefix + '.' + g.name
  const tags = (g.tags || []).map((t) => dcsTag(ch, dev, path, t))
  const subs = (g.subGroups || []).map((sg) => dcsBuildGroupNode(ch, dev, path, sg, false))
  return dcsGroupNode(g.name, !topLevel, tags, subs)
}
function dcsGroupNode(name: string, isGroup: boolean, tags: any[], subs: any[]): any {
  const node: any = {
    IsGroup: isGroup, IsSendToMQ: false, IsCallBack: false, IsCallBackWrite: false, IsCollection: !isGroup,
    IsConfirmRewrite: true, CollectInterval: isGroup ? 0 : 60000, IsHaveStruct: false, IsStructCountFixed: false, StructFixedCount: 1,
    AppendJson: '{"WorkStationCode":"","StepCode":"","DeviceNumber":""}', CallBackWriteTags: [],
  }
  if (!isGroup) { node.DynamicParams = []; node.StepConfigs = [] }
  node.Tags = tags; node.Tag_groups = subs; node.IsHeartBeat = false; node.ALLTYPES_NAME = name; node.TAG_DATA_TYPE = -1; node.KepwareTagDataType = -1
  return node
}
function dcsConvertAddr(t: Tag): { tagAddr: string; addr: string } {
  const igw = t.address || ''
  const m = igw.match(/DB(\d+)\.DB([XBWD])(\d+)(?:\.(\d+))?/i)
  if (!m) return { tagAddr: igw, addr: igw }
  const db = parseInt(m[1], 10), off = parseInt(m[3], 10)
  const token = dcsToken(t.dataType)
  if (t.dataType === 'string' || t.dataType === 'wString') { const len = t.stringLength ?? DEFAULT_STRING_LEN; return { tagAddr: `DB${db},${token}${off}.${len}`, addr: igw } }
  if (t.dataType === 'bool' && m[4] != null) { const bit = parseInt(m[4], 10); return { tagAddr: `DB${db},${token}${off}.${bit}`, addr: igw } }
  return { tagAddr: `DB${db},${token}${off}`, addr: igw }
}
function dcsCode(dt: string): number {
  return ({ string: 0, wString: 0, bool: 1, char: 2, byte: 3, int16: 4, uint16: 5, int32: 6, uint32: 7, float: 8, double: 9 } as any)[dt] ?? 0
}
function dcsToken(dt: string): string {
  return ({ string: 'STRING', wString: 'STRING', bool: 'BOOL', byte: 'BYTE', char: 'CHAR', int16: 'INT', uint16: 'WORD', int32: 'DINT', uint32: 'DWORD', int64: 'LINT', uint64: 'LWORD', float: 'REAL', double: 'LREAL' } as any)[dt] || 'INT'
}
function ipOf(endpoint: string): string {
  if (!endpoint || !endpoint.trim()) return ''
  const i = endpoint.indexOf(':')
  return i > 0 ? endpoint.slice(0, i) : endpoint
}
function dcsSystemSet(opc: string): any {
  return {
    KepwareServerUrl: opc, DeviceIntervalInSeconds: 30, ReconnectIntervalInSeconds: 5, WorkWarnTimeOutSeconds: 2, RequestOutSeconds: 15,
    IsShowLeafNode: false, IsAutoMinSize: false, IsAutoStart: true, RewriteTimes: 2, IsAllowEmptyValue: true, HeartBeatMaxLimit: 1000,
    IsForceOpenThread: true, MinThreadCount: 0, MaxThreadCount: 0, HttpPort: 5000, ParameterDicName: 'parameterDic', IgnoreParameterDicNames: '',
  }
}
