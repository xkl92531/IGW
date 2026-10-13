const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const Module = require('node:module')
const ts = require('typescript')

function loadModule() {
  const filename = path.join(__dirname, 'mesDataRanges.ts')
  if (!fs.existsSync(filename)) return {}
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

const ranges = loadModule()

function project() {
  return {
    templates: [{
      name: 'T',
      groups: [{ name: 'DB1604', tags: [{ name: 'State' }], subGroups: [{ name: 'Tray', tags: [{ name: 'Code' }, { name: 'Debug' }], subGroups: [] }] }],
    }],
    channels: [{
      name: 'Line',
      devices: [
        { name: 'PLC_A', groups: [{ name: 'DBRecipe', tags: [{ name: 'Name' }], subGroups: [] }] },
        { name: 'PLC_B', templateRef: 'T', delta: { masks: ['DB1604.Tray.Debug'], overrides: [], adds: [{ groupPath: 'DB1604.Tray', tag: { name: 'Ok' } }] } },
      ],
    }],
  }
}

test('跨设备建立可搜索范围索引并展开模板实例差异', () => {
  assert.equal(typeof ranges.buildMesRangeIndex, 'function')
  const index = ranges.buildMesRangeIndex(project())
  const db = index.bySourcePath.get('Line.PLC_B.DB1604')
  assert.equal(db.tagCount, 3)
  assert.equal(db.defaultOutputPath, 'PLC_B.DB1604')
  assert.deepEqual(db.children[0].leafTags.map((x) => x.relativePath), ['Code', 'Ok'])
  assert.deepEqual(ranges.searchMesRangeOptions(index, 'quality'), [])
  assert.equal(ranges.searchMesRangeOptions(index, 'db1604')[0].sourcePath, 'Line.PLC_B.DB1604')
})

test('按完整数据块路径读取属性说明并兼容模板实例', () => {
  assert.equal(typeof ranges.mesGroupDescription, 'function')
  const value = project()
  value.channels[0].devices[0].groups[0].description = '配方数据说明'
  value.templates[0].groups[0].description = '模板数据块说明'

  assert.equal(ranges.mesGroupDescription(value, 'Line.PLC_A.DBRecipe'), '配方数据说明')
  assert.equal(ranges.mesGroupDescription(value, 'Line.PLC_B.DB1604'), '模板数据块说明')
  assert.equal(ranges.mesGroupDescription(value, 'Line.PLC_A.Unknown'), '')
})

test('父范围已选时子范围不可再选且摘要按PLC和点数统计', () => {
  const index = ranges.buildMesRangeIndex(project())
  const selected = [
    { sourcePath: 'Line.PLC_B.DB1604' },
    { sourcePath: 'Line.PLC_A.DBRecipe' },
  ]
  const child = index.bySourcePath.get('Line.PLC_B.DB1604.Tray')
  assert.match(ranges.validateRangeSelection(selected, child), /已被上级范围包含/)
  assert.equal(ranges.rangeSummary(selected, index), '2 PLC · 2 个数据范围 · 约 4 点')
})

test('重命名只更新自动输出路径并保留自定义输出路径', () => {
  const root = {
    dataRanges: [
      { sourcePath: 'Line.PLC_A.DBRecipe', outputPath: 'PLC_A.DBRecipe', outputPathMode: 'Auto' },
      { sourcePath: 'Line.PLC_A.DBRecipe.Sub', outputPath: 'custom.recipe', outputPathMode: 'Custom' },
    ],
  }
  ranges.migrateMesRangeRefs(root, 'Line.PLC_A', 'Line.PLC_Main')
  assert.deepEqual(root.dataRanges.map((x) => [x.sourcePath, x.outputPath]), [
    ['Line.PLC_Main.DBRecipe', 'PLC_Main.DBRecipe'],
    ['Line.PLC_Main.DBRecipe.Sub', 'custom.recipe'],
  ])
})

test('删除设备或DB后同步清理失效采集流程并保留当前设备流程', () => {
  const value = project()
  value.mesUpload = { tasks: [
    { id: 'keep', groupRef: 'Line.PLC_A.DBRecipe', deviceRef: '旧设备引用' },
    { id: 'deleted-device', groupRef: 'Line.PLC_Deleted.DB1604', deviceRef: 'Line.PLC_Deleted' },
    { id: 'deleted-db', groupRef: 'Line.PLC_A.DBDeleted', deviceRef: 'Line.PLC_A' },
    { id: 'legacy', groupRef: '', name: '未绑定兼容任务' },
  ] }

  const removed = ranges.pruneOrphanMesTasks(value)

  assert.deepEqual(removed.map((task) => task.id), ['deleted-device', 'deleted-db'])
  assert.deepEqual(value.mesUpload.tasks.map((task) => task.id), ['keep', 'legacy'])
  assert.equal(value.mesUpload.tasks[0].deviceRef, 'Line.PLC_A')
})

test('纯数字元素组按结构识别为一个数组入口且不展开全部点位', () => {
  const elements = Array.from({ length: 200 }, (_, index) => ({
    name: String(index),
    tags: [
      { name: 'Torque', dataType: 'Double', address: `ns=3;s="Data"."AnyName"[${index}]."Torque"` },
      { name: 'Result', dataType: 'Int16', address: `ns=3;s="Data"."AnyName"[${index}]."Result"` },
    ],
    subGroups: [],
  }))
  const value = {
    channels: [{
      name: 'Line', devices: [{ name: 'PLC', groups: [{
        name: 'OP200Data', tags: [{ name: 'DataReady' }], subGroups: [{
          name: 'AnyBusinessName', tags: [], subGroups: elements,
        }],
      }] }],
    }],
  }

  const index = ranges.buildMesRangeIndex(value)
  const root = index.bySourcePath.get('Line.PLC.OP200Data')
  const array = index.bySourcePath.get('Line.PLC.OP200Data.AnyBusinessName')

  assert.equal(root.tagCount, 401)
  assert.equal(root.children.length, 1)
  assert.equal(array.scope, 'Array')
  assert.equal(array.tagCount, 400)
  assert.deepEqual(array.array.lowerBounds, [0])
  assert.deepEqual(array.array.dimensions, [200])
  assert.deepEqual(array.arrayMembers.map((member) => member.path), ['Torque', 'Result'])
  assert.deepEqual(array.children, [])
  assert.ok(!index.all.some((option) => option.sourcePath.endsWith('.AnyBusinessName.0')))
})

test('方括号和下划线元素组在范围树中各折叠为一个数组入口', () => {
  for (const style of ['bracket', 'underscore']) {
    const names = style === 'bracket' ? ['Cells[0]', 'Cells[1]'] : ['Cells_0', 'Cells_1']
    const value = {
      channels: [{ name: 'Line', devices: [{ name: 'PLC', groups: [{
        name: 'DB', tags: [], subGroups: names.map((name, index) => ({
          name, tags: [{ name: 'Value', dataType: 'Int16', address: `DB.Cells[${index}].Value` }], subGroups: [],
        })),
      }] }] }],
    }

    const root = ranges.buildMesRangeIndex(value).roots[0]
    assert.deepEqual(root.children.map((child) => [child.label, child.scope]), [['Cells', 'Array']])
    assert.deepEqual(root.children[0].array.dimensions, [2])
  }
})

test('数字子组结构不一致时不误判为数组', () => {
  const value = {
    channels: [{ name: 'Line', devices: [{ name: 'PLC', groups: [{
      name: 'Area', tags: [], subGroups: [
        { name: '0', tags: [{ name: 'A', dataType: 'Int16' }], subGroups: [] },
        { name: '1', tags: [{ name: 'B', dataType: 'Int16' }], subGroups: [] },
      ],
    }] }] }],
  }

  const root = ranges.buildMesRangeIndex(value).roots[0]
  assert.equal(root.scope, 'Group')
  assert.deepEqual(root.children.map((child) => child.label), ['0', '1'])
})

test('嵌套数字数组仍只显示外层数组和一个内层数组成员', () => {
  const inner = () => ({
    name: 'Samples', tags: [], subGroups: [0, 1].map((index) => ({
      name: String(index), tags: [{ name: 'Pressure', dataType: 'Double' }], subGroups: [],
    })),
  })
  const value = {
    channels: [{ name: 'Line', devices: [{ name: 'PLC', groups: [{
      name: 'DB', tags: [], subGroups: [{
        name: 'Bolts', tags: [], subGroups: [0, 1].map((index) => ({
          name: String(index), tags: [{ name: 'Result', dataType: 'Int16' }], subGroups: [inner()],
        })),
      }],
    }] }] }],
  }

  const index = ranges.buildMesRangeIndex(value)
  const array = index.bySourcePath.get('Line.PLC.DB.Bolts')
  assert.equal(array.scope, 'Array')
  assert.deepEqual(array.arrayMembers.map((member) => [member.path, member.dataType]), [
    ['Result', 'Int16'], ['Samples', 'Array'],
  ])
  assert.ok(!index.all.some((option) => option.sourcePath.includes('.Bolts.0')))
})

test('目录仍存在但上报来源已失效时保留流程并清空该上报范围', () => {
  const value = project()
  const upload = {
    id: 'upload', type: 'Upload', title: '上报',
    http: { url: 'http://mes/JudgeBatch', method: 'POST' },
    payload: {
      mode: 'DataRanges',
      dataRanges: [{ sourcePath: 'Line.PLC_A.DBRecipe.RemovedStruct', outputPath: 'Structures' }],
      fields: [], structs: [], append: { station: 'A' },
    },
  }
  value.mesUpload = { tasks: [{
    id: 'keep-flow', groupRef: 'Line.PLC_A.DBRecipe', deviceRef: 'Line.PLC_A', enabled: true,
    flow: {
      nodes: [{ id: 'trigger', type: 'Trigger' }, upload],
      edges: [{ from: 'trigger', to: 'upload' }],
    },
  }] }

  const result = ranges.reconcileMesTaskTopology(value)

  assert.equal(result.removedTasks.length, 0)
  assert.equal(result.clearedPayloads, 1)
  assert.equal(result.clearedRanges, 1)
  assert.equal(value.mesUpload.tasks.length, 1)
  assert.equal(value.mesUpload.tasks[0].flow.nodes.length, 2)
  assert.equal(value.mesUpload.tasks[0].flow.edges.length, 1)
  assert.equal(upload.http.url, 'http://mes/JudgeBatch')
  assert.deepEqual(upload.payload.dataRanges, [])
  assert.deepEqual(upload.payload.append, { station: 'A' })
})

test('任务目录消失时直接删除任务且不误清有效上报范围', () => {
  const value = project()
  value.mesUpload = { tasks: [
    {
      id: 'keep', groupRef: 'Line.PLC_A.DBRecipe',
      payload: { mode: 'DataRanges', dataRanges: [{ sourcePath: 'Line.PLC_A.DBRecipe' }] },
    },
    {
      id: 'gone', groupRef: 'Line.PLC_A.RemovedDB',
      flow: { nodes: [{ id: 'upload', type: 'Upload', payload: { mode: 'DataRanges', dataRanges: [] } }], edges: [] },
    },
  ] }

  const result = ranges.reconcileMesTaskTopology(value)

  assert.deepEqual(result.removedTasks.map((task) => task.id), ['gone'])
  assert.equal(result.clearedPayloads, 0)
  assert.equal(value.mesUpload.tasks.length, 1)
  assert.equal(value.mesUpload.tasks[0].id, 'keep')
  assert.equal(value.mesUpload.tasks[0].payload.dataRanges.length, 1)
})

test('模板展开后的DB仍是有效采集流程绑定', () => {
  const value = project()
  value.mesUpload = { tasks: [{ id: 'template', groupRef: 'line.plc_b.db1604.tray' }] }

  assert.deepEqual(ranges.pruneOrphanMesTasks(value), [])
  assert.equal(value.mesUpload.tasks[0].groupRef, 'Line.PLC_B.DB1604.Tray')
  assert.equal(value.mesUpload.tasks[0].deviceRef, 'Line.PLC_B')
})

test('流程列表包含真实DB以及它的直接子组任务边界', () => {
  const value = {
    channels: [{
      name: 'Line',
      devices: [{
        name: 'PLC',
        groups: [{
          name: 'DB容器', tags: [], subGroups: [
            { name: 'DB2100', tags: [{ name: 'State' }], subGroups: [{ name: 'CellData_0', tags: [{ name: 'OCV' }], subGroups: [] }] },
            { name: 'DB2101', tags: [{ name: 'Alarm' }], subGroups: [] },
            { name: 'DB2102', tags: [{ name: 'Online' }], subGroups: [] },
            { name: 'DB2103', tags: [{ name: 'Card' }], subGroups: [] },
          ],
        }],
      }],
    }],
  }

  const boundaries = ranges.buildMesTaskBoundaries(value)

  assert.deepEqual(boundaries.map((item) => item.sourcePath), [
    'Line.PLC.DB容器.DB2100',
    'Line.PLC.DB容器.DB2100.CellData_0',
    'Line.PLC.DB容器.DB2101',
    'Line.PLC.DB容器.DB2102',
    'Line.PLC.DB容器.DB2103',
  ])
  assert.equal(boundaries[0].tagCount, 2)
  assert.equal(boundaries[1].tagCount, 1)
})

test('套用内置流程模板时空白上报节点自动绑定当前 DB 范围', () => {
  const upload = {
    type: 'Upload',
    payload: { mode: 'DataRanges', dataRanges: [], fields: [], structs: [], append: {} },
    http: { bodyTemplate: '' },
  }

  assert.equal(ranges.bindBlankTemplateUploads([upload], 'Line.PLC_A.DBRecipe'), 1)
  assert.deepEqual(upload.payload.dataRanges, [{
    sourcePath: 'Line.PLC_A.DBRecipe',
    scope: 'Group',
    outputPath: 'PLC_A.DBRecipe',
    outputPathMode: 'Auto',
    includeMode: 'All',
    excludes: [],
    offlineMode: 'Null',
  }])
})

test('套用模板不会覆盖上报节点已有范围或自定义报文配置', () => {
  const configured = {
    type: 'Upload',
    payload: {
      mode: 'DataRanges',
      dataRanges: [{ sourcePath: 'Line.PLC_A.DBRecipe.Sub', outputPath: 'Structures', outputPathMode: 'Custom' }],
      fields: [],
      structs: [],
    },
    http: { bodyTemplate: '' },
  }
  const customBody = {
    type: 'Upload',
    payload: { mode: 'DataRanges', dataRanges: [], fields: [], structs: [] },
    http: { bodyTemplate: '{"manual":true}' },
  }

  assert.equal(ranges.bindBlankTemplateUploads([configured, customBody], 'Line.PLC_A.DBRecipe'), 0)
  assert.equal(configured.payload.dataRanges[0].outputPath, 'Structures')
  assert.deepEqual(customBody.payload.dataRanges, [])
})

test('OPC UA 顶层主目录及其直接子组都可以独立创建流程', () => {
  const value = {
    channels: [{
      name: 'OPC通道',
      driverId: 'OpcUa.Client',
      devices: [{
        name: '设备',
        groups: [{
          name: 'DB2100_电芯OCV_DataDB',
          tags: [],
          subGroups: [
            { name: 'Header', tags: [{ name: 'MESFlag' }], subGroups: [] },
            { name: 'CellData_0', tags: [{ name: 'DMCCode' }, { name: 'IsNG' }], subGroups: [] },
          ],
        }],
      }],
    }],
  }

  const boundaries = ranges.buildMesTaskBoundaries(value)

  assert.deepEqual(boundaries.map((item) => item.sourcePath), [
    'OPC通道.设备.DB2100_电芯OCV_DataDB',
    'OPC通道.设备.DB2100_电芯OCV_DataDB.Header',
    'OPC通道.设备.DB2100_电芯OCV_DataDB.CellData_0',
  ])
  assert.equal(boundaries[0].tagCount, 3)
  assert.equal(boundaries[1].tagCount, 1)
  assert.equal(boundaries[2].tagCount, 2)
})

test('二级任务的数据范围只包含本级直属点位和全部后代点位', () => {
  const value = {
    channels: [{
      name: 'OPC通道', driverId: 'OpcUa.Client', devices: [{
        name: '设备', groups: [{
          name: 'OP030', tags: [{ name: 'RootState' }], subGroups: [
            {
              name: '进站', tags: [{ name: 'MESFlag' }], subGroups: [
                { name: '扫码', tags: [{ name: 'SN' }], subGroups: [] },
              ],
            },
            { name: '出站', tags: [{ name: 'PLCFlag' }], subGroups: [] },
          ],
        }],
      }],
    }],
  }

  const option = ranges.buildMesRangeIndex(value).bySourcePath.get('OPC通道.设备.OP030.进站')

  assert.ok(option)
  assert.deepEqual(option.leafTags.map((tag) => tag.tagId), [
    'OPC通道.设备.OP030.进站.MESFlag',
    'OPC通道.设备.OP030.进站.扫码.SN',
  ])
})

test('OPC UA 设备名称不作为 MES 边界且直属 DB 与二级子组可独立配置', () => {
  const value = {
    channels: [{
      name: 'OPC通道', driverId: 'OpcUa.Client', devices: [{
        name: 'S7Sim', groups: [
          { name: 'DB2100', tags: [], subGroups: [{ name: 'CellData_0', tags: [{ name: 'IsNG' }], subGroups: [] }] },
          { name: 'DB2101', tags: [{ name: 'Alarm' }], subGroups: [] },
        ],
      }],
    }],
  }

  assert.deepEqual(ranges.buildMesTaskBoundaries(value).map((item) => item.sourcePath), [
    'OPC通道.S7Sim.DB2100',
    'OPC通道.S7Sim.DB2100.CellData_0',
    'OPC通道.S7Sim.DB2101',
  ])
})

test('OPC UA 二级任务保留自己的完整组路径而不迁移回一级', () => {
  const value = {
    channels: [{
      name: 'OPC通道', driverId: 'OpcUa.Client', devices: [{
        name: '设备', groups: [{
          name: 'Main', tags: [], subGroups: [
            { name: 'Header', tags: [{ name: 'MESFlag' }], subGroups: [] },
            { name: 'Payload', tags: [{ name: 'Code' }], subGroups: [] },
          ],
        }],
      }],
    }],
    mesUpload: { tasks: [{ id: 'legacy', groupRef: 'OPC通道.设备.Main.Header', deviceRef: 'OPC通道.设备' }] },
  }

  assert.equal(ranges.normalizeMesTaskBoundaryRefs(value), 0)
  assert.equal(value.mesUpload.tasks[0].groupRef, 'OPC通道.设备.Main.Header')
})

test('同一主目录下已有多个旧流程时不自动合并或丢弃配置', () => {
  const value = {
    channels: [{
      name: 'OPC通道', driverId: 'OpcUa.Client', devices: [{
        name: '设备', groups: [{
          name: 'Main', tags: [], subGroups: [
            { name: 'Header', tags: [{ name: 'MESFlag' }], subGroups: [] },
            { name: 'Payload', tags: [{ name: 'Code' }], subGroups: [] },
          ],
        }],
      }],
    }],
    mesUpload: { tasks: [
      { id: 'first', groupRef: 'OPC通道.设备.Main.Header' },
      { id: 'second', groupRef: 'OPC通道.设备.Main.Payload' },
    ] },
  }

  assert.equal(ranges.normalizeMesTaskBoundaryRefs(value), 0)
  assert.deepEqual(value.mesUpload.tasks.map((task) => task.groupRef), [
    'OPC通道.设备.Main.Header',
    'OPC通道.设备.Main.Payload',
  ])
})
