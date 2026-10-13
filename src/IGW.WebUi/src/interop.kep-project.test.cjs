const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const Module = require('node:module')
const ts = require('typescript')

function loadInterop() {
  const filename = path.join(__dirname, 'interop.ts')
  const output = ts.transpileModule(fs.readFileSync(filename, 'utf8'), {
    compilerOptions: {
      target: ts.ScriptTarget.ES2020,
      module: ts.ModuleKind.CommonJS,
      esModuleInterop: true,
    },
    fileName: filename,
  }).outputText
  const loaded = new Module(filename, module)
  loaded.filename = filename
  loaded.paths = module.paths
  loaded._compile(output, filename)
  return loaded.exports
}

const interop = loadInterop()

const project = {
  schemaVersion: 1,
  name: '产线工程',
  settings: { restPort: 48020 },
  opcUa: { enabled: true, port: 49320 },
  mesUpload: {
    enabled: true,
    defaultMesUrl: 'http://mes.local/report',
    tasks: [{
      id: 'task-db2100', name: 'DB2100采集流程', enabled: true,
      groupRef: 'Line1.PLC1.DB2100', deviceRef: 'Line1.PLC1', collectIntervalMs: 200,
      flow: {
        nodes: [
          { id: 'trigger', type: 'Trigger', title: '采集周期', x: 20, y: 40 },
          { id: 'upload', type: 'Upload', title: '上报', x: 240, y: 40 },
        ],
        edges: [{ from: 'trigger', fromPort: 'out', to: 'upload' }],
      },
    }],
    flowTemplates: [{ name: '标准上报', sourceGroupRef: 'Line1.PLC1.DB2100' }],
  },
  channels: [{
    id: 'channel-id',
    name: 'Line1',
    description: '西门子产线',
    driverId: 'Siemens.S7',
    settings: {
      networkAdapter: 'WLAN',
      networkAdapterId: '{EFE64081-6561-4321-B629-1191A7B2ED46}',
      networkAdapterIpv4: '192.168.1.240',
      timeoutMs: '2000',
      wordOrder: 'ABCD',
    },
    devices: [{
      id: 'device-id',
      name: 'PLC1',
      description: '主 PLC',
      endpoint: '192.168.1.2:102',
      settings: {
        model: 'S7-1500', rack: '0', slot: '1', timeoutMs: '2000',
        scanMode: '请求所有数据以指定的扫描速率', dataCollection: '启用',
      },
      tags: [{
        id: 'root-tag', name: 'Heartbeat', address: 'DB1.DBX0.0', dataType: 'bool',
        access: 'read', scanRateMs: 200, description: '心跳',
      }],
      groups: [{
        id: 'db-id', name: 'DB2100', description: '电芯数据',
        tags: [],
        subGroups: [{
          id: 'struct-id', name: 'CellData', description: '结构体数组',
          tags: [{
            id: 'array-tag', name: 'Voltage', address: 'DB2100.DBD62', dataType: 'float', isArray: true,
            array: { elementType: 'float', dimensions: [2, 3], lowerBounds: [1, -2], elementStrideBytes: 4, sourceShape: 'ARRAY[1..2,-2..0] OF REAL' },
            metadata: { 'array.sourceFormat': 'Siemens DB', 'array.sourceAddress': 'DB2100.DBD62' },
            access: 'readWrite', scanRateMs: 10, description: '电压数组',
          }],
          subGroups: [],
        }],
      }],
    }],
  }],
}

test('JSON 配置同时包含 KEP 连接性结构和完整 IGW 工程扩展', () => {
  assert.equal(typeof interop.exportKepJson, 'function')
  const exported = JSON.parse(interop.exportKepJson(project))
  assert.deepEqual(Object.keys(exported), ['project', 'igw'])
  assert.equal(exported.project['servermain.PROJECT_TITLE'], '产线工程')
  assert.equal(exported.igw.format, 'IGW.JSON.Configuration')
  assert.equal(exported.igw.schemaVersion, 3)
  assert.deepEqual(exported.igw.project.mesUpload, project.mesUpload)

  const channel = exported.project.channels[0]
  assert.equal(channel['common.ALLTYPES_NAME'], 'Line1')
  assert.equal(channel['common.ALLTYPES_DESCRIPTION'], '西门子产线')
  assert.equal(channel['servermain.MULTIPLE_TYPES_DEVICE_DRIVER'], 'Siemens TCP/IP Ethernet')

  const device = channel.devices[0]
  assert.equal(device['common.ALLTYPES_NAME'], 'PLC1')
  assert.equal(device['servermain.DEVICE_ID_STRING'], '192.168.1.2')
  assert.equal(device['siemens_tcpip_ethernet.DEVICE_COMMUNICATIONS_PORT_NUMBER'], 102)
  assert.equal(device.tags[0]['servermain.TAG_ADDRESS'], 'DB1.DBX0.0')
  assert.equal(device.tags[0]['servermain.TAG_DATA_TYPE'], 1)
  assert.equal(device.tags[0]['servermain.TAG_READ_WRITE_ACCESS'], 0)
  assert.equal(device.tags[0]['servermain.TAG_SCAN_RATE_MILLISECONDS'], 200)
  assert.equal(device.tag_groups[0].tag_groups[0].tags[0]['servermain.TAG_DATA_TYPE'], 28)
})

test('IGW JSON 配置导出后重新导入保留采集流程和工程级配置', () => {
  assert.equal(typeof interop.exportKepJson, 'function')
  const exported = interop.exportKepJson(project)
  const imported = interop.importKepJson(exported)

  assert.equal(imported.channels, 1)
  assert.equal(imported.devices, 1)
  assert.equal(imported.tags, 2)
  assert.equal(imported.proj.name, '产线工程')
  assert.deepEqual(imported.proj.settings, project.settings)
  assert.deepEqual(imported.proj.opcUa, project.opcUa)
  assert.deepEqual(imported.proj.mesUpload, project.mesUpload)
  const channel = imported.proj.channels[0]
  assert.equal(channel.driverId, 'Siemens.S7')
  assert.equal(channel.description, '西门子产线')
  assert.deepEqual(channel.settings, project.channels[0].settings)
  const device = channel.devices[0]
  assert.equal(device.endpoint, '192.168.1.2:102')
  assert.equal(device.description, '主 PLC')
  assert.deepEqual(device.settings, project.channels[0].devices[0].settings)
  assert.deepEqual(device.groups.map((g) => g.name), ['DB2100'])
  assert.deepEqual(device.groups[0].subGroups.map((g) => g.name), ['CellData'])
  const tag = device.groups[0].subGroups[0].tags[0]
  assert.equal(tag.address, 'DB2100.DBD62')
  assert.equal(tag.dataType, 'float')
  assert.equal(tag.isArray, true)
  assert.deepEqual(tag.array, project.channels[0].devices[0].groups[0].subGroups[0].tags[0].array)
  assert.deepEqual(tag.metadata, project.channels[0].devices[0].groups[0].subGroups[0].tags[0].metadata)
  assert.equal(tag.access, 'readWrite')
  assert.equal(tag.scanRateMs, 10)
  assert.equal(tag.description, '电压数组')
})

test('没有 IGW 扩展的原生 KEP JSON 仍按连接性工程导入', () => {
  const exported = JSON.parse(interop.exportKepJson(project))
  delete exported.igw

  const imported = interop.importKepJson(JSON.stringify(exported))

  assert.equal(imported.channels, 1)
  assert.equal(imported.devices, 1)
  assert.equal(imported.tags, 2)
  assert.equal(imported.proj.name, '产线工程')
  assert.equal(imported.proj.mesUpload, undefined)
  assert.equal(imported.proj.channels[0].settings.networkAdapter, 'WLAN')
  assert.equal(imported.proj.channels[0].devices[0].settings.model, 'S7-1500')
  assert.equal(imported.proj.channels[0].devices[0].settings.rack, '0')
  assert.equal(imported.proj.channels[0].devices[0].settings.slot, '1')
})

test('旧版 IGW JSON 中的完整 channels 仍恢复原始驱动与运行参数', () => {
  const legacy = JSON.parse(interop.exportKepJson(project))
  legacy.igw.schemaVersion = 1
  legacy.igw.project.channels = project.channels
  delete legacy.igw.connectivity

  const imported = interop.importKepJson(JSON.stringify(legacy))

  assert.equal(imported.proj.channels[0].driverId, 'Siemens.S7')
  assert.deepEqual(imported.proj.channels[0].settings, project.channels[0].settings)
  assert.deepEqual(imported.proj.channels[0].devices[0].settings, project.channels[0].devices[0].settings)
})
