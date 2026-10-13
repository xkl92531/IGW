const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const Module = require('node:module')
const ts = require('typescript')

function loadModule() {
  const filename = path.join(__dirname, 'mesStructMapping.ts')
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

const mapping = loadModule()

test('同时识别方括号与西门子导入器的下划线数组元素组名', () => {
  assert.deepEqual(mapping.parseIndexedGroupName('Tray[10]'), {
    baseName: 'Tray', index: 10, style: 'bracket',
  })
  assert.deepEqual(mapping.parseIndexedGroupName('CellData_10'), {
    baseName: 'CellData', index: 10, style: 'underscore',
  })
  assert.equal(mapping.parseIndexedGroupName('CodeStruct'), null)
})

test('按完整路径识别数字子组与旧式元素组且保留真实数组路径', () => {
  assert.deepEqual(mapping.resolveIndexedElementPath('Line.PLC.OP200Data.Bolts.12'), {
    baseGroup: 'Line.PLC.OP200Data.Bolts', aliasName: 'Bolts', index: 12, style: 'numeric',
  })
  assert.deepEqual(mapping.resolveIndexedElementPath('Line.PLC.DB.CellData_12'), {
    baseGroup: 'Line.PLC.DB.CellData', aliasName: 'CellData', index: 12, style: 'underscore',
  })
  assert.equal(mapping.resolveIndexedElementPath('Line.PLC.DB.Header'), null)
})

test('旧版误识别的 CellData_N 值数组迁移为一个对象数组', () => {
  const members = ['DMCCode', 'KValue', 'Voltage'].map((tagId) => ({ tagId, fieldName: '', scale: 1 }))
  const payload = {
    structs: Array.from({ length: 11 }, (_, index) => ({
      kind: 'List',
      baseGroup: `S7Sim.Sim.DB2100.CellData_${index}`,
      aliasName: `CellData_${index}`,
      countMode: 'Fixed',
      startIndex: 0,
      fixedCount: members.length,
      takeIndex: false,
      members: members.map((member) => ({ ...member })),
    })),
  }

  assert.equal(mapping.upgradeLegacyImportedStructs(payload), 11)
  assert.equal(payload.structs.length, 1)
  assert.deepEqual(payload.structs[0], {
    kind: 'Array',
    baseGroup: 'S7Sim.Sim.DB2100.CellData',
    aliasName: 'CellData',
    countMode: 'Enum',
    startIndex: 0,
    fixedCount: 11,
    takeIndex: false,
    members,
  })
})

test('迁移结构体时保留用户为后台接口设置的 JSON 名称', () => {
  const payload = {
    structs: [0, 1].map((index) => ({
      kind: 'List', baseGroup: `S7Sim.Sim.DB2100.CellData_${index}`,
      aliasName: index === 0 ? 'Structures' : `CellData_${index}`,
      countMode: 'Fixed', fixedCount: 2,
      members: [{ tagId: 'DMCCode' }, { tagId: 'IsNG' }],
    })),
  }

  assert.equal(mapping.upgradeLegacyImportedStructs(payload), 2)
  assert.equal(payload.structs.length, 1)
  assert.equal(payload.structs[0].aliasName, 'Structures')
  assert.equal(payload.structs[0].baseGroup, 'S7Sim.Sim.DB2100.CellData')
})

test('不迁移不连续或明显由用户手工配置的值数组', () => {
  const payload = {
    structs: [0, 2].map((index) => ({
      kind: 'List', baseGroup: `DB.CellData_${index}`, aliasName: `CellData_${index}`,
      countMode: 'Fixed', fixedCount: 1, members: [{ tagId: 'Code' }],
    })),
  }
  assert.equal(mapping.upgradeLegacyImportedStructs(payload), 0)
  assert.equal(payload.structs.length, 2)
})

test('旧版纯数字元素组 Bolts.0 到 Bolts.N 迁移为一个对象数组', () => {
  const members = ['Torque', 'Angle', 'Result'].map((tagId) => ({ tagId, fieldName: tagId, scale: 1 }))
  const payload = {
    fields: [],
    structs: Array.from({ length: 200 }, (_, index) => ({
      kind: 'List',
      baseGroup: `Line.PLC.OP200Data.Bolts.${index}`,
      aliasName: String(index),
      countMode: 'Fixed',
      startIndex: 0,
      fixedCount: members.length,
      takeIndex: false,
      members: members.map((member) => ({ ...member })),
    })),
  }

  assert.equal(mapping.upgradeLegacyImportedStructs(payload), 200)
  assert.equal(payload.structs.length, 1)
  assert.deepEqual(payload.structs[0], {
    kind: 'Array',
    baseGroup: 'Line.PLC.OP200Data.Bolts',
    aliasName: 'Bolts',
    countMode: 'Enum',
    startIndex: 0,
    fixedCount: 200,
    takeIndex: false,
    members,
  })
})

test('旧版平铺的连续数组字段迁移为一个对象数组并保留成员 JSON 名称', () => {
  const payload = {
    fields: Array.from({ length: 3 }, (_, index) => [
      { tagId: `Line.PLC.OP200Data.Bolts.${index}.Torque`, fieldName: 'torqueValue', scale: 1 },
      { tagId: `Line.PLC.OP200Data.Bolts.${index}.Result`, fieldName: 'resultCode', scale: 1 },
    ]).flat(),
    structs: [],
  }

  assert.equal(mapping.upgradeLegacyImportedStructs(payload), 3)
  assert.deepEqual(payload.fields, [])
  assert.equal(payload.structs.length, 1)
  assert.deepEqual(payload.structs[0], {
    kind: 'Array',
    baseGroup: 'Line.PLC.OP200Data.Bolts',
    aliasName: 'Bolts',
    countMode: 'Enum',
    startIndex: 0,
    fixedCount: 3,
    members: [
      { tagId: 'Torque', fieldName: 'torqueValue', scale: 1 },
      { tagId: 'Result', fieldName: 'resultCode', scale: 1 },
    ],
  })
})

test('20000 个平铺数组点位仍压缩为一个逻辑数组而不是 20000 个编辑项', () => {
  const memberNames = Array.from({ length: 10 }, (_, index) => `Member${index}`)
  const payload = {
    fields: Array.from({ length: 2000 }, (_, elementIndex) => memberNames.map((member, memberIndex) => ({
      tagId: `Line.PLC.OP200Data.Bolts.${elementIndex}.${member}`,
      fieldName: `jsonMember${memberIndex}`,
      scale: 1,
    }))).flat(),
    structs: [],
  }

  assert.equal(mapping.upgradeLegacyImportedStructs(payload), 2000)
  assert.equal(payload.fields.length, 0)
  assert.equal(payload.structs.length, 1)
  assert.equal(payload.structs[0].fixedCount, 2000)
  assert.equal(payload.structs[0].members.length, 10)
})

test('重新自动识别点位时保留已配置的接口 JSON 名称', () => {
  const previous = {
    fields: [
      { tagId: 'S7Sim.Sim.DB2100.MESFlag', fieldName: 'mesFlag', scale: 1 },
    ],
    structs: [
      {
        kind: 'Array', baseGroup: 'S7Sim.Sim.DB2100.CellData', aliasName: 'Structures',
        members: [
          { tagId: 'ErrorMessage', fieldName: 'NgReason', scale: 1 },
          { tagId: 'IsNG', fieldName: 'IsNg', scale: 1 },
        ],
      },
    ],
  }
  const detected = {
    fields: [
      { tagId: 'S7Sim.Sim.DB2100.MESFlag', fieldName: 'MESFlag', scale: 1 },
      { tagId: 'S7Sim.Sim.DB2100.PLCFlag', fieldName: 'PLCFlag', scale: 1 },
    ],
    structs: [
      {
        kind: 'Array', baseGroup: 'S7Sim.Sim.DB2100.CellData', aliasName: 'CellData',
        members: [
          { tagId: 'ErrorMessage', fieldName: '', scale: 1 },
          { tagId: 'IsNG', fieldName: '', scale: 1 },
          { tagId: 'Voltage', fieldName: '', scale: 1 },
        ],
      },
    ],
  }

  mapping.preserveAutoDetectedMappings(previous, detected)

  assert.equal(detected.fields[0].fieldName, 'mesFlag')
  assert.equal(detected.fields[1].fieldName, 'PLCFlag')
  assert.equal(detected.structs[0].aliasName, 'Structures')
  assert.equal(detected.structs[0].members[0].fieldName, 'NgReason')
  assert.equal(detected.structs[0].members[1].fieldName, 'IsNg')
  assert.equal(detected.structs[0].members[2].fieldName, '')
})
