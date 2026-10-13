const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const Module = require('node:module')
const ts = require('typescript')

function loadModule() {
  const filename = path.join(__dirname, 'opcUaStructuredImport.ts')
  if (!fs.existsSync(filename)) return {}
  const source = fs.readFileSync(filename, 'utf8')
  const output = ts.transpileModule(source, {
    compilerOptions: { target: ts.ScriptTarget.ES2020, module: ts.ModuleKind.CommonJS },
    fileName: filename,
  }).outputText
  const loaded = new Module(filename, module)
  loaded.filename = filename
  loaded.paths = module.paths
  loaded._compile(output, filename)
  return loaded.exports
}

const imp = loadModule()

test('嵌套数组枚举转换为类型名称且不修改浏览结果', () => {
  const tag = { name: 'TimeStamp', address: 'ns=3;s=TimeStamp', path: '', dataType: 1, isArray: true,
    array: { elementType: 1, dimensions: [8], lowerBounds: [0], struct: { fields: [{ name: 'Flag', dataType: 0 }] } } }
  const imported = imp.toImportedOpcUaTag(tag, { rootGroupName: '', folderNames: {}, tagNames: {} })
  assert.equal(imported.array.elementType, 'byte')
  assert.equal(imported.array.struct.fields[0].dataType, 'bool')
  assert.equal(tag.array.elementType, 1)
})

test('Siemens语义类型的标量和数组枚举按后端类型原样导入', () => {
  const aliases = { rootGroupName: '', folderNames: {}, tagNames: {} }
  const scalar = imp.toImportedOpcUaTag({ ...sourceTag, dataType: 17 }, aliases)
  const array = imp.toImportedOpcUaTag({ ...sourceTag, dataType: 30, isArray: true,
    array: { elementType: 30, dimensions: [2], lowerBounds: [1] } }, aliases)
  assert.equal(scalar.dataType, 'dtl')
  assert.equal(array.dataType, 'lword')
  assert.equal(array.array.elementType, 'lword')
})

const sourceTag = {
  name: 'BatchId', address: 'ns=4;s=Recipe.Header.BatchId', path: 'Wrong.Flat',
  pathSegments: ['Cell.Data', 'Header'], dataType: 'string', isArray: false,
}

test('显式路径段优先且结构和点位重命名不改变NodeId', () => {
  const firstKey = imp.folderAliasKey(['Cell.Data'])
  const aliases = {
    rootGroupName: 'Line1',
    folderNames: { [firstKey]: 'Cells' },
    tagNames: { [sourceTag.address]: 'BatchCode' },
  }

  const result = imp.toImportedOpcUaTag(sourceTag, aliases)

  assert.deepEqual(result.groupPath, ['Line1', 'Cells', 'Header'])
  assert.equal(result.name, 'BatchCode')
  assert.equal(result.address, sourceTag.address)
})

test('OPC UA 浏览返回的当前会话读写权限会保留到导入点位', () => {
  const aliases = { rootGroupName: '', folderNames: {}, tagNames: {} }

  const textResult = imp.toImportedOpcUaTag({ ...sourceTag, access: 'readWrite' }, aliases)
  const enumResult = imp.toImportedOpcUaTag({ ...sourceTag, access: 2 }, aliases)

  assert.equal(textResult.access, 'readWrite')
  assert.equal(enumResult.access, 'readWrite')
})

test('OPC UA 服务器公布的字符串容量会保留到导入点位', () => {
  const aliases = { rootGroupName: '', folderNames: {}, tagNames: {} }

  const result = imp.toImportedOpcUaTag({ ...sourceTag, stringLength: 50 }, aliases)

  assert.equal(result.stringLength, 50)
})

test('同一NodeId位于不同实际路径时可分别设置点位名称', () => {
  const first = { ...sourceTag, pathSegments: ['DeviceSet', 'PLC8', 'DB1'] }
  const second = { ...sourceTag, pathSegments: ['L1_PLC08', 'PLC8', 'DB1'] }
  const aliases = {
    rootGroupName: '', folderNames: {}, tagNames: {
      [imp.opcUaTagAliasKey(first)]: 'DeviceSetBatch',
      [imp.opcUaTagAliasKey(second)]: 'LineBatch',
    },
  }

  assert.equal(imp.toImportedOpcUaTag(first, aliases).name, 'DeviceSetBatch')
  assert.equal(imp.toImportedOpcUaTag(second, aliases).name, 'LineBatch')
  assert.notEqual(imp.opcUaTagAliasKey(first), imp.opcUaTagAliasKey(second))
})

test('旧响应回退解析path并校验空名称和点号', () => {
  assert.deepEqual(imp.sourcePathSegments({ ...sourceTag, pathSegments: undefined, path: 'Recipe.Header' }), ['Recipe', 'Header'])
  assert.deepEqual(imp.validateOpcUaAliases({ rootGroupName: '', folderNames: { a: '' }, tagNames: { b: 'Bad.Name' } }), [
    '结构目标名称不能为空', '点位目标名称不能包含“.”',
  ])
})

test('导入结果按设备groups或组subGroups正确回写', () => {
  const importedDevice = { tags: [{ name: 'T' }], groups: [{ name: 'G' }] }
  const device = { tags: [], groups: [] }
  imp.replaceImportedTarget(device, importedDevice)
  assert.deepEqual(device.groups, [{ name: 'G' }])
  assert.deepEqual(device.tags, [{ name: 'T' }])

  const importedGroup = { tags: [{ name: 'T2' }], subGroups: [{ name: 'SG' }] }
  const group = { tags: [], subGroups: [] }
  imp.replaceImportedTarget(group, importedGroup)
  assert.deepEqual(group.subGroups, [{ name: 'SG' }])
  assert.deepEqual(group.tags, [{ name: 'T2' }])
  assert.equal('groups' in group, false)
})

test('扁平的结构体数组BrowseName按西门子规则还原为子组和字段', () => {
  const aliases = { rootGroupName: '', folderNames: {}, tagNames: {} }
  const imported = imp.toImportedOpcUaTag({
    name: 'CellData[10].ErrorMessage',
    address: 'ns=2;s=S7Sim.DB2100_电芯OCV_DataDB.CellData[10].ErrorMessage',
    path: '',
    pathSegments: [],
    dataType: 'string',
    isArray: false,
  }, aliases)

  assert.deepEqual(imported.groupPath, ['CellData_10'])
  assert.equal(imported.name, 'ErrorMessage')
  assert.equal(imported.address, 'ns=2;s=S7Sim.DB2100_电芯OCV_DataDB.CellData[10].ErrorMessage')
})

test('结构体数组路径来自服务器层级时同样采用西门子组名且允许重命名', () => {
  const tag = {
    name: 'Voltage', address: 'ns=2;s=Voltage', path: 'CellData[2]',
    pathSegments: ['CellData[2]'], dataType: 'float', isArray: false,
  }
  const key = imp.folderAliasKey(['CellData[2]'])

  assert.deepEqual(imp.toImportedOpcUaTag(tag, {
    rootGroupName: '', folderNames: {}, tagNames: {},
  }).groupPath, ['CellData_2'])
  assert.deepEqual(imp.toImportedOpcUaTag(tag, {
    rootGroupName: '', folderNames: { [key]: 'Cells_2' }, tagNames: {},
  }).groupPath, ['Cells_2'])
})

test('OPC UA 顶层浏览节点作为设备身份并从导入组路径中剥离', () => {
  const tags = [
    {
      name: 'MESFlag', address: 'ns=2;s=S7Sim.DB2100.MESFlag',
      path: 'S7Sim.DB2100', pathSegments: ['S7Sim', 'DB2100'], dataType: 'int16', isArray: false,
    },
    {
      name: 'DMCCode', address: 'ns=2;s=S7Sim.DB2100.CellData_0.DMCCode',
      path: 'S7Sim.DB2100.CellData_0', pathSegments: ['S7Sim', 'DB2100', 'CellData_0'], dataType: 'string', isArray: false,
    },
  ]
  const aliases = { rootGroupName: '', folderNames: {}, tagNames: {} }

  assert.deepEqual(imp.opcUaDeviceRoots(tags), ['S7Sim'])
  assert.deepEqual(imp.toImportedOpcUaTag(tags[0], aliases, 'S7Sim').groupPath, ['DB2100'])
  assert.deepEqual(imp.toImportedOpcUaTag(tags[1], aliases, 'S7Sim').groupPath, ['DB2100', 'CellData_0'])
  assert.equal(imp.toImportedOpcUaTag(tags[1], aliases, 'S7Sim').address, tags[1].address)
})

test('一次选择多个 OPC UA 顶层设备时保留全部设备名供界面阻止导入', () => {
  const tags = [
    { name: 'A', address: 'a', path: 'S7Sim.DB1', pathSegments: ['S7Sim', 'DB1'] },
    { name: 'B', address: 'b', path: 'OmronSim.Area', pathSegments: ['OmronSim', 'Area'] },
    { name: 'C', address: 'c', path: 's7sim.DB2', pathSegments: ['s7sim', 'DB2'] },
  ]

  assert.deepEqual(imp.opcUaDeviceRoots(tags), ['S7Sim', 'OmronSim'])
})

test('旧 OPC UA 设备包装组可无损展开且冲突时拒绝而不修改输入', () => {
  const target = {
    name: '设备', tags: [], groups: [{
      name: 'S7Sim', tags: [], subGroups: [{ name: 'DB2100', tags: [{ name: 'MESFlag' }], subGroups: [] }],
    }],
  }

  const flattened = imp.flattenLegacyOpcUaDeviceRoot(target, 'S7Sim')

  assert.equal(flattened.changed, true)
  assert.equal(flattened.error, undefined)
  assert.deepEqual(flattened.target.groups.map((group) => group.name), ['DB2100'])
  assert.deepEqual(target.groups.map((group) => group.name), ['S7Sim'])

  const conflictTarget = {
    name: '设备', tags: [], groups: [
      { name: 'DB2100', tags: [], subGroups: [] },
      { name: 'S7Sim', tags: [], subGroups: [{ name: 'DB2100', tags: [], subGroups: [] }] },
    ],
  }
  const rejected = imp.flattenLegacyOpcUaDeviceRoot(conflictTarget, 'S7Sim')
  assert.equal(rejected.changed, false)
  assert.match(rejected.error, /DB2100/)
  assert.deepEqual(conflictTarget.groups.map((group) => group.name), ['DB2100', 'S7Sim'])
})

test('OPC UA 原生数组作为一个逻辑点导入并保留维度下界和结构体布局', () => {
  const array = {
    elementType: 'struct', dimensions: [3], lowerBounds: [1], elementStrideBytes: 8,
    struct: { id: 'cell', name: 'Cell', sizeBytes: 8, fields: [{ name: 'Value', dataType: 'float', byteOffset: 0 }] },
  }
  const imported = imp.toImportedOpcUaTag({
    name: 'Cells', address: 'ns=2;s=Cells', path: 'S7Sim.DB1', pathSegments: ['S7Sim', 'DB1'],
    dataType: 'struct', isArray: true, array,
  }, { rootGroupName: '', folderNames: {}, tagNames: {} }, 'S7Sim')

  assert.equal(imported.isArray, true)
  assert.deepEqual(imported.array, array)
  assert.notEqual(imported.array, array)
})

test('OPC UA 数组协议证据随导入结果保留且不共享可变对象', () => {
  const metadata = {
    'array.sourceFormat': 'OPC UA',
    'opcua.valueRank': '2',
    'opcua.arrayDimensions': '2,3',
    'array.sourceAddress': 'ns=2;s=Matrix',
  }
  const imported = imp.toImportedOpcUaTag({
    name: 'Matrix', address: 'ns=2;s=Matrix', path: 'PLC.DB1', pathSegments: ['PLC', 'DB1'],
    dataType: 'int16', isArray: true,
    array: { elementType: 'int16', dimensions: [2, 3], lowerBounds: [0, 0], elementStrideBytes: 2 },
    metadata,
  }, { rootGroupName: '', folderNames: {}, tagNames: {} }, 'PLC')

  assert.deepEqual(imported.metadata, metadata)
  assert.notEqual(imported.metadata, metadata)
})
