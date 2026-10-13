const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const Module = require('node:module')
const ts = require('typescript')

function loadImportModule() {
  const filename = path.join(__dirname, 'kepTagImport.ts')
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

const imp = loadImportModule()

const header = 'Tag Name,Address,Data Type,Respect Data Type,Client Access,Scan Rate,Scaling,Raw Low,Raw High,Scaled Low,Scaled High,Scaled Data Type,Clamp Low,Clamp High,Eng Units,Description,Negate Value'
const kepRow = (name, address, type = 'Short') =>
  `"${name}","${address}",${type},1,R/W,100,,,,,,,,,,,""`

function plc4Equivalent() {
  const rows = [kepRow(
    'LogCheck1.EquipmentInfo_ResultFlag',
    'ns=3;s="AESCE2M_1"."EACEQPTINIT"."EquipmentInfo_ResultFlag"',
  )]
  for (let i = 1; i <= 41; i++)
    rows.push(kepRow(`LogOff1.Tag${i}`, `ns=3;s="LogOff"."Tag${i}"`, i % 3 === 0 ? 'String' : 'Short'))
  rows.push(kepRow('LogOff1.AESCM2E_1.EquipmentInfo_ProductInfo_SerialNoArray', 'ns=3;s="AESCM2E_1"."MACINBOUND"."SerialNoArray"', 'String Array'))
  rows.push(kepRow('LogOff1.AESCM2E_1.EquipmentInfo_ProductInfo_SlotIDArray', 'ns=3;s="AESCM2E_1"."MACINBOUND"."SlotIDArray"', 'String Array'))
  return [header, ...rows].join('\r\n')
}

test('KEP CSV 恢复44个点和嵌套组并保留NodeId内部引号', () => {
  assert.equal(typeof imp.parseKepTagCsv, 'function')
  const result = imp.parseKepTagCsv(plc4Equivalent())

  assert.deepEqual(result.errors, [])
  assert.equal(result.tags.length, 44)
  assert.deepEqual(result.tags.find((x) => x.name === 'EquipmentInfo_ProductInfo_SerialNoArray').groupPath, ['LogOff1', 'AESCM2E_1'])
  assert.equal(result.tags[0].address, 'ns=3;s="AESCE2M_1"."EACEQPTINIT"."EquipmentInfo_ResultFlag"')
})

test('String Array 保留字符串数组属性', () => {
  const result = imp.parseKepTagCsv(plc4Equivalent())
  const arrays = result.tags.filter((x) => x.isArray)

  assert.equal(arrays.length, 2)
  assert.ok(arrays.every((x) => x.dataType === 'string'))
  assert.ok(arrays.every((x) => x.metadata['array.sourceFormat'] === 'KEP CSV'))
  assert.ok(arrays.every((x) => x.metadata['kep.dataType'] === 'String Array'))
})

test('解析错误和冲突不会修改目标树', () => {
  assert.equal(typeof imp.buildImportPreview, 'function')
  assert.equal(typeof imp.applyTagImport, 'function')
  const target = {
    groups: [{ id: '', name: 'G', tags: [{ id: '', name: 'T', address: 'old' }], subGroups: [] }],
    tags: [],
  }
  const before = structuredClone(target)
  const preview = imp.buildImportPreview(target, [{
    groupPath: ['G'], name: 'T', address: 'new', dataType: 'int16', isArray: false,
    access: 'readWrite', scanRateMs: 100, description: '', row: 2,
  }])

  assert.equal(preview.conflicts.length, 1)
  assert.deepEqual(target, before)
  assert.throws(() => imp.applyTagImport(target, preview, false), /冲突/)
  assert.deepEqual(target, before)

  const applied = imp.applyTagImport(target, preview, true)
  assert.equal(applied.target.groups[0].tags[0].address, 'new')
  assert.equal(target.groups[0].tags[0].address, 'old')
})

test('不支持的数据类型按行报错且不会生成降级点位', () => {
  const csv = [header, kepRow('G.Unsupported', 'ns=3;s="G"."Unsupported"', 'DateTime')].join('\n')
  const result = imp.parseKepTagCsv(csv)

  assert.equal(result.tags.length, 0)
  assert.equal(result.errors.length, 1)
  assert.match(result.errors[0].message, /DateTime/)
})

test('同路径同NodeId直接跳过，不要求用户覆盖', () => {
  const target = {
    groups: [{ id: '', name: 'G', tags: [{ id: '', name: 'T', address: 'ns=3;s="G"."T"' }], subGroups: [] }],
    tags: [],
  }
  const imported = [{
    groupPath: ['G'], name: 'T', address: 'ns=3;s="G"."T"', dataType: 'int16', isArray: false,
    access: 'read', scanRateMs: 100, description: '', row: 2,
  }]

  const preview = imp.buildImportPreview(target, imported)
  assert.equal(preview.skipped, 1)
  assert.deepEqual(preview.conflicts, [])
  assert.deepEqual(preview.tags, [])
})

test('同路径同NodeId权限变化时只刷新权限并保留原有工程参数', () => {
  const target = {
    groups: [{
      id: '', name: 'G', subGroups: [],
      tags: [{
        id: 'existing', name: 'T', address: 'ns=3;s="G"."T"', dataType: 'int16', isArray: false,
        access: 'read', scanRateMs: 250, description: '现场说明', deadband: 3, deadbandType: 'absolute',
      }],
    }],
    tags: [],
  }
  const imported = [{
    groupPath: ['G'], name: 'T', address: 'ns=3;s="G"."T"', dataType: 'int16', isArray: false,
    access: 'readWrite', scanRateMs: 100, description: '', row: 2,
  }]

  const preview = imp.buildImportPreview(target, imported)
  const applied = imp.applyTagImport(target, preview, false)
  const tag = applied.target.groups[0].tags[0]

  assert.equal(preview.skipped, 0)
  assert.deepEqual(preview.conflicts, [])
  assert.equal(tag.access, 'readWrite')
  assert.equal(tag.scanRateMs, 250)
  assert.equal(tag.description, '现场说明')
  assert.equal(tag.deadband, 3)
})

test('一次可合并5000个同组点位且结构完整', () => {
  const imported = Array.from({ length: 5000 }, (_, i) => ({
    groupPath: ['LargeStruct'], name: `Tag${i}`, address: `ns=3;s="LargeStruct"."Tag${i}"`,
    dataType: 'int16', isArray: false, access: 'read', scanRateMs: 100, description: '', row: i + 2,
  }))
  const target = { groups: [], tags: [] }

  const preview = imp.buildImportPreview(target, imported)
  const applied = imp.applyTagImport(target, preview, false)

  assert.equal(preview.groupsToCreate, 1)
  assert.equal(applied.imported, 5000)
  assert.equal(applied.target.groups[0].tags.length, 5000)
})

test('导入应用保留协议数组元数据供后端识别且不共享对象', () => {
  const metadata = { 'array.sourceFormat': 'OPC UA', 'opcua.valueRank': '1' }
  const imported = [{
    groupPath: ['DB1'], name: 'Values', address: 'ns=2;s=Values', dataType: 'int16', isArray: true,
    array: { elementType: 'int16', dimensions: [10], lowerBounds: [0], elementStrideBytes: 2 },
    metadata, access: 'readWrite', scanRateMs: 100, description: '', row: 1,
  }]

  const preview = imp.buildImportPreview({ groups: [], tags: [] }, imported)
  const applied = imp.applyTagImport({ groups: [], tags: [] }, preview, false)
  const tag = applied.target.groups[0].tags[0]

  assert.deepEqual(tag.metadata, metadata)
  assert.notEqual(tag.metadata, metadata)
})

test('导入应用保留服务器公布的字符串容量', () => {
  const imported = [{
    groupPath: ['DB1'], name: 'Message', address: 'ns=3;s="DB1"."Message"',
    dataType: 'string', isArray: false, stringLength: 50,
    access: 'readWrite', scanRateMs: 100, description: '', row: 1,
  }]

  const preview = imp.buildImportPreview({ groups: [], tags: [] }, imported)
  const applied = imp.applyTagImport({ groups: [], tags: [] }, preview, false)

  assert.equal(applied.target.groups[0].tags[0].stringLength, 50)
})
