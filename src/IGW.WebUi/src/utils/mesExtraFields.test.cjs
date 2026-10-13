const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const ts = require('typescript')
require.extensions['.ts'] = (module, filename) => module._compile(ts.transpileModule(fs.readFileSync(filename, 'utf8'), { compilerOptions: { target: ts.ScriptTarget.ES2020, module: ts.ModuleKind.CommonJS } }).outputText, filename)
const fields = require('./mesExtraFields.ts')
test('typed fixed values and optional unavailable context preview without invented zeros', () => {
  const rows = [{ name: 'count', dataType: 'int32', source: 'Fixed', value: '2', required: true }, { name: 'product.code', dataType: 'string', source: 'Input', path: 'code' }, { name: 'missing', dataType: 'double', source: 'Input', path: 'count' }]
  assert.equal(JSON.stringify(fields.previewExtraFields(rows, { input: { code: 'ABC' } })), '{"count":2,"product":{"code":"ABC"},"missing":null}')
})
test('invalid fixed values and conflicting outputs fail before save', () => {
  assert.throws(() => fields.validateExtraFields({ extraFields: [{ name: 'sn', source: 'Legacy', dataType: 'string', path: 'barcode' }] }))
  assert.throws(() => fields.validateExtraFields({ extraFields: [{ name: 'count', source: 'Fixed', dataType: 'int32', value: '1.5', required: true }] }))
  assert.throws(() => fields.validateExtraFields({ fields: [{ fieldName: 'product' }], extraFields: [{ name: 'product.code', source: 'Fixed', dataType: 'string', value: 'A' }] }))
  assert.throws(() => fields.validateExtraFields({ extraFields: [{ name: 'a', source: 'Fixed', dataType: 'json', value: '{}' }, { name: 'a.b', source: 'Fixed', dataType: 'string', value: 'A' }] }))
})
test('parameter codes in the dictionary do not collide with independent outer fields', () => {
  const payload = {
    mode: 'FieldMapping', fieldKeyMode: 'ItemId',
    fields: [{ tagId: 'Line.PLC.DB.Value', fieldName: 'reading', parameterCode: 'P.VALUE' }],
    extraFields: [
      { name: 'reading', source: 'Fixed', dataType: 'int32', value: '2' },
      { name: 'Line.PLC.DB.Value', source: 'Fixed', dataType: 'int32', value: '3' },
    ],
  }
  assert.doesNotThrow(() => fields.validateExtraFields(payload))
  assert.doesNotThrow(() => fields.validateExtraFields({ ...payload, fieldKeyMode: 'Name' }))
  assert.throws(() => fields.validateExtraFields({ ...payload, fields: [{ tagId: 'Line.PLC.DB.Value', fieldName: 'reading' }] }))
  assert.doesNotThrow(() => fields.validateExtraFields({ ...payload, fields: [{ tagId: 'reading', parameterCode: 'P_OTHER' }] }))
  assert.throws(() => fields.validateExtraFields({
    ...payload, structs: [{ aliasName: 'reading', members: [{ tagId: 'Code' }] }],
  }))
})

test('parameterDic is reserved only when parameter codes or struct selections create a dictionary', () => {
  const additions = [
    { append: { parameterDic: 'old' } },
    { fixedValues: [{ name: 'ParameterDic', dataType: 'Text', value: 'old' }] },
    { extraFields: [{ name: 'PARAMETERDIC.value', source: 'Fixed', dataType: 'int32', value: '2' }] },
    { structs: [{ aliasName: 'parameterDic', members: [{ tagId: 'A' }] }] },
  ]
  for (const extra of additions) {
    assert.throws(() => fields.validateExtraFields({ mode: 'FieldMapping', fields: [{ tagId: 'L.P.DB.T', parameterCode: 'P_T' }], ...extra }), /parameterDic/i)
    assert.doesNotThrow(() => fields.validateExtraFields({ mode: 'FieldMapping', fieldKeyMode: 'ItemId', ...extra }))
    assert.doesNotThrow(() => fields.validateExtraFields({ mode: 'FieldMapping', fieldKeyMode: 'Name', ...extra }))
    assert.doesNotThrow(() => fields.validateExtraFields({ mode: 'DataRanges', fieldKeyMode: 'ItemId', ...extra }))
  }
  assert.doesNotThrow(() => fields.validateExtraFields({
    mode: 'FieldMapping', fieldKeyMode: 'ItemId', fields: [{ tagId: 'Line.PLC.DB.parameterDic' }],
    append: { 'parameterDic.literal': 'allowed' },
    structs: [{ aliasName: 'Product', members: [{ tagId: 'parameterDic' }] }],
  }))
})

test('payload mode parsing is case-insensitive when reserving parameterDic', () => {
  const payload = { fields: [{ tagId: 'L.P.DB.T', parameterCode: 'P_T' }], append: { parameterDic: 'legacy' } }
  for (const mode of ['dataranges', 'DATARANGES', 'DaTaRaNgEs'])
    assert.doesNotThrow(() => fields.validateExtraFields({ ...payload, mode }))
  assert.doesNotThrow(() => fields.validateExtraFields({ ...payload, mode: '', dataRanges: [{ sourcePath: 'L.P.DB' }] }))
  assert.throws(() => fields.validateExtraFields({ ...payload, mode: 'fieldmapping', dataRanges: [{ sourcePath: 'L.P.DB' }] }), /parameterDic/i)
})

test('required dynamic values are deferred on save but enforced against preview context', () => {
  const extraFields = [{ name: 'order', dataType: 'string', source: 'Input', path: 'order.code', required: true }]
  assert.doesNotThrow(() => fields.validateExtraFields({ extraFields }))
  assert.throws(() => fields.previewExtraFields(extraFields, {}))
})
