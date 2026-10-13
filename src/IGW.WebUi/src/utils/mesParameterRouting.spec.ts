import { expect, it } from 'vitest'
import { hasParameterDictionary, validateParameterRouting } from './mesParameterRouting'
import { preserveAutoDetectedMappings } from './mesStructMapping'
import { validateTaskExtraFields } from './mesExtraFields'

it('uses parameter codes and struct selections, never the removed global format', () => {
  expect(hasParameterDictionary({ fieldKeyMode: 'ItemId', fields: [{ tagId: 'L.P.DB.T' }] })).toBe(false)
  expect(hasParameterDictionary({ fields: [{ tagId: 'L.P.DB.T', parameterCode: 'P_T' }] })).toBe(true)
  expect(hasParameterDictionary({ structs: [{ asDictionary: true, members: [{ tagId: 'T' }] }] })).toBe(true)
  expect(hasParameterDictionary({ structs: [{ structs: [{ asDictionary: true, members: [{ tagId: 'T' }] }] }] })).toBe(true)
  expect(hasParameterDictionary({ fields: [{ parameterCode: '  ' }] })).toBe(false)
})

it('rejects two point targets sharing one parameter code without interpreting dots as JSON paths', () => {
  expect(() => validateParameterRouting({ fields: [{ tagId: 'L.P.DB.A', parameterCode: 'P.same' }, { tagId: 'L.P.DB.B', parameterCode: 'P.same' }] })).toThrow(/重复|冲突/)
  expect(() => validateParameterRouting({ fields: [{ tagId: 'L.P.DB.A', parameterCode: 'P.a' }, { tagId: 'L.P.DB.B', parameterCode: 'P.b' }] })).not.toThrow()
})

it('allows repeated identical routes but rejects different scales for the same dictionary key', () => {
  const field = { tagId: 'L.P.DB.A', parameterCode: 'P_A', scale: 2 }
  expect(() => validateParameterRouting({ fields: [field, { ...field, fieldName: 'alternate' }] })).not.toThrow()
  expect(() => validateParameterRouting({ fields: [field, { ...field, scale: 3 }] })).toThrow(/重复|冲突/)
})

it('allows an original field named parameterDic until a real dictionary is requested', () => {
  expect(() => validateParameterRouting({ fields: [{ tagId: 'L.P.DB.T', fieldName: 'parameterDic' }] })).not.toThrow()
  expect(() => validateParameterRouting({ fields: [{ tagId: 'L.P.DB.T', fieldName: 'parameterDic' }, { tagId: 'L.P.DB.A', parameterCode: 'P_A' }] })).toThrow(/parameterDic/)
})

it('preserves dictionary selections on refill and rejects inconsistent legacy selections', () => {
  const array = (baseGroup: string, asDictionary = false) => ({ kind: 'Array', baseGroup, aliasName: 'cells', asDictionary, members: [{ tagId: 'T' }] })
  const previous: any = { structs: [array('L.P.DB.Slots')] }
  previous.structs[0].structs = [array('Cells', true)]
  const detected: any = { structs: [array('L.P.DB.Slots')] }
  detected.structs[0].structs = [array('Cells')]
  preserveAutoDetectedMappings(previous, detected)
  expect(detected.structs[0].structs[0].asDictionary).toBe(true)
  const flat = { structs: [array('L.P.DB.Slots[1].Cells', true), array('L.P.DB.Slots[2].Cells')] }
  expect(() => preserveAutoDetectedMappings(flat, detected)).toThrow(/不同|冲突/)
})

it.each(['bodyTemplate', 'jsonTemplate'])('does not validate unused automatic routing in an explicit %s', template => {
  const payload = { fields: [{ tagId: 'L.P.DB.A', fieldName: 'alias', parameterCode: 'X' }, { tagId: 'L.P.DB.B', fieldName: 'parameterDic' }] }
  const task = { flow: { nodes: [{ type: 'Upload', payload, http: { [template]: '{"a":"{alias}","b":"{parameterDic}"}' } }] } }
  expect(() => validateTaskExtraFields([task])).not.toThrow()
  expect(() => validateTaskExtraFields([{ flow: { nodes: [{ type: 'Upload', payload, http: {} }] } }])).toThrow(/parameterDic/)
})

it('validates linear task routing only if at least one actual step is automatic', () => {
  const payload = { fields: [{ tagId: 'L.P.DB.A', parameterCode: 'X' }, { tagId: 'L.P.DB.B', parameterCode: 'X' }] }
  const http = { bodyTemplate: '{"a":1}' }
  expect(() => validateTaskExtraFields([{ payload, steps: [{ http }] }])).not.toThrow()
  expect(() => validateTaskExtraFields([{ payload, steps: [{ http }, { http: {} }] }])).toThrow(/重复|冲突/)
  expect(() => validateTaskExtraFields([{ payload, flow: { nodes: [{ type: 'Trigger' }] } }])).not.toThrow()
})
