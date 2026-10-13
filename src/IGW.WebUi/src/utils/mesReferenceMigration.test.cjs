const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const Module = require('node:module')
const ts = require('typescript')

function loadModule() {
  const filename = path.join(__dirname, 'mesDataRanges.ts')
  const output = ts.transpileModule(fs.readFileSync(filename, 'utf8'), {
    compilerOptions: { target: ts.ScriptTarget.ES2020, module: ts.ModuleKind.CommonJS },
    fileName: filename,
  }).outputText
  const loaded = new Module(filename, module)
  loaded.filename = filename
  loaded.paths = module.paths
  loaded._compile(output, filename)
  return loaded.exports
}

const migration = loadModule()

test('条件只迁移 PLC 点位，不修改接口字段或条件 JSON 路径', () => {
  const path = 'Line.Plc.DB.Value'
  const root = { conditions: [{ source: 'Tag', tagId: path }, { source: 'Input', path, tagId: path }], inputMappings: [{ sourcePath: path, targetPath: 'value' }] }
  assert.equal(migration.migrateMesReferences(root, 'Line.Plc.DB', 'Line.Plc.Area.DB'), 1)
  assert.equal(root.conditions[0].tagId, 'Line.Plc.Area.DB.Value')
  assert.equal(root.conditions[1].path, path)
  assert.equal(root.conditions[1].tagId, path)
  assert.equal(root.inputMappings[0].sourcePath, path)
})

test('业务字段仅迁移 Tag 来源，Input 路径和 Fixed 内容原样保留', () => {
  const path = 'Line.Plc.DB.Value'
  const extraFields = ['Tag', 'Input', 'Fixed'].map(source => ({ name: source, source, path, value: path }))
  const root = { payload: { extraFields }, flow: { nodes: [{ payload: { extraFields: [{ name: 'node', source: 'Tag', path }] } }] } }
  assert.equal(migration.migrateMesReferences(root, 'Line.Plc.DB', 'Line.Plc.Area.DB'), 2)
  assert.equal(extraFields[0].path, 'Line.Plc.Area.DB.Value')
  for (const field of extraFields.slice(1)) assert.equal(field.path, path)
  for (const field of extraFields) assert.equal(field.value, path)
  assert.equal(root.flow.nodes[0].payload.extraFields[0].path, 'Line.Plc.Area.DB.Value')
})

test('移动 OPC UA 文件夹后同步迁移全部 MES 点位引用且保留协议地址和 JSON 名称', () => {
  const root = {
    groupRef: 'Ua.Device.DB2100',
    deviceRef: 'Ua.Device',
    mesFlagTagId: 'Ua.Device.DB2100.MESFlag',
    plcFlagTagId: 'Ua.Device.DB2100.PLCFlag',
    flow: {
      nodes: [{
        switchTag: 'Ua.Device.DB2100.Request',
        payload: {
          fields: [{ tagId: 'Ua.Device.DB2100.Temperature', fieldName: 'temperature' }],
          structs: [{
            baseGroup: 'Ua.Device.DB2100.CellData',
            countTagId: 'Ua.Device.DB2100.CellCount',
            aliasName: 'structures',
          }],
          dataRanges: [
            { sourcePath: 'Ua.Device.DB2100', outputPath: 'Device.DB2100', outputPathMode: 'Auto' },
            { sourcePath: 'Ua.Device.DB2100.CellData', outputPath: 'structures', outputPathMode: 'Custom' },
          ],
        },
        writeBack: {
          fromResponse: [{ responseField: 'backResult.mesFlag', tagId: 'Ua.Device.DB2100.MESFlag' }],
          manual: [{ tagId: 'Ua.Device.DB2100.PLCFlag', copyFromTagId: 'Ua.Device.DB2100.Request' }],
        },
        protocolAddress: 'ns=2;s=S7Sim.DB2100.MESFlag',
      }],
    },
  }

  const changed = migration.migrateMesReferences(root, 'Ua.Device.DB2100', 'Ua.Device.AreaA.DB2100')

  assert.ok(changed >= 10)
  assert.equal(root.groupRef, 'Ua.Device.AreaA.DB2100')
  assert.equal(root.mesFlagTagId, 'Ua.Device.AreaA.DB2100.MESFlag')
  assert.equal(root.plcFlagTagId, 'Ua.Device.AreaA.DB2100.PLCFlag')
  assert.equal(root.flow.nodes[0].switchTag, 'Ua.Device.AreaA.DB2100.Request')
  assert.equal(root.flow.nodes[0].payload.fields[0].tagId, 'Ua.Device.AreaA.DB2100.Temperature')
  assert.equal(root.flow.nodes[0].payload.structs[0].baseGroup, 'Ua.Device.AreaA.DB2100.CellData')
  assert.equal(root.flow.nodes[0].payload.structs[0].aliasName, 'structures')
  assert.equal(root.flow.nodes[0].payload.dataRanges[0].outputPath, 'Device.AreaA.DB2100')
  assert.equal(root.flow.nodes[0].payload.dataRanges[1].outputPath, 'structures')
  assert.equal(root.flow.nodes[0].protocolAddress, 'ns=2;s=S7Sim.DB2100.MESFlag')
})

test('载入旧工程时按唯一结构后缀恢复已经保存的移动前路径', () => {
  const project = {
    channels: [{
      name: 'Ua',
      devices: [{
        name: 'Device',
        groups: [{
          name: 'AreaA', tags: [], subGroups: [{
            name: 'DB2100',
            tags: [{ name: 'MESFlag', address: 'ns=2;s=Server.DB2100.MESFlag' }],
            subGroups: [{ name: 'CellData_0', tags: [{ name: 'IsNG' }], subGroups: [] }],
          }],
        }],
      }],
    }],
    mesUpload: { tasks: [{
      groupRef: 'Ua.Device.DB2100',
      deviceRef: 'Ua.Device',
      flow: { nodes: [{
        conditions: [{ tagId: 'Ua.Device.DB2100.MESFlag' }],
        payload: { structs: [{ baseGroup: 'Ua.Device.DB2100.CellData_0' }] },
      }] },
    }] },
  }

  const changed = migration.repairMovedMesReferences(project)

  assert.ok(changed >= 3)
  const task = project.mesUpload.tasks[0]
  assert.equal(task.groupRef, 'Ua.Device.AreaA.DB2100')
  assert.equal(task.flow.nodes[0].conditions[0].tagId, 'Ua.Device.AreaA.DB2100.MESFlag')
  assert.equal(task.flow.nodes[0].payload.structs[0].baseGroup, 'Ua.Device.AreaA.DB2100.CellData_0')
})
