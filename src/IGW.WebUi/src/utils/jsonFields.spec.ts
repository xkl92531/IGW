import { describe, expect, it } from 'vitest'
import { changeJsonField, inspectJsonFields, parseJsonFieldValue } from './jsonFields'
import { selectJsonPath } from './mesJsonPreview'

describe('JSON field inspection and structured editing', () => {
  it('uses runtime-compatible pointers for literal keys and preserves root and native types', () => {
    const root = { 'a.b': { '/~': [null, false, 12] }, '': 'empty', nested: { text: '${input.n}' } }
    const rows = inspectJsonFields(root)
    expect(rows.map(row => [row.path, row.type])).toEqual([
      ['$', 'object'], ['/a.b', 'object'], ['/a.b/~1~0', 'array'], ['/a.b/~1~0/0', 'null'],
      ['/a.b/~1~0/1', 'boolean'], ['/a.b/~1~0/2', 'number'], ['/', 'string'], ['$.nested', 'object'], ['$.nested.text', 'string'],
    ])
    for (const field of rows) expect(selectJsonPath(root, field.path)).toEqual(field.value)
  })
  it('adds nested containers and appends arrays without overwriting or expanding sparse arrays', () => {
    const root = { preserve: [{ n: 2 }] }
    expect(changeJsonField(root, 'data.items[0].ok', true, 'add')).toEqual({ preserve: [{ n: 2 }], data: { items: [{ ok: true }] } })
    expect(changeJsonField(root, 'preserve[1]', null, 'add')).toEqual({ preserve: [{ n: 2 }, null] })
    expect(() => changeJsonField(root, 'preserve[0]', {}, 'add')).toThrow('already exists')
    expect(() => changeJsonField(root, 'preserve[100000]', {}, 'add')).toThrow('index')
    expect(root).toEqual({ preserve: [{ n: 2 }] })
  })
  it('replaces literal-key values and deletes array entries with contiguous indices', () => {
    const root = { 'a.b': { n: [1, 2, 3] } }
    expect(changeJsonField(root, '/a.b/n/1', 4, 'replace')).toEqual({ 'a.b': { n: [1, 4, 3] } })
    expect(changeJsonField(root, '/a.b/n/1', undefined, 'remove')).toEqual({ 'a.b': { n: [1, 3] } })
    expect(() => changeJsonField(root, '/a.b/missing', false, 'replace')).toThrow('Missing')
    expect(() => changeJsonField(root, '$', undefined, 'remove')).toThrow('child')
  })
  it('treats reserved property names as data without mutating object prototypes', () => {
    const result = changeJsonField({}, '__proto__.constructor.flag', true, 'add')
    expect(JSON.stringify(result)).toBe('{"__proto__":{"constructor":{"flag":true}}}')
    expect(({} as any).flag).toBeUndefined()
  })
  it('validates the selected value type and preserves placeholder strings', () => {
    expect(parseJsonFieldValue('string', '${mapped.count}')).toBe('${mapped.count}')
    expect(parseJsonFieldValue('number', '2.5')).toBe(2.5)
    expect(parseJsonFieldValue('boolean', 'false')).toBe(false)
    expect(parseJsonFieldValue('null', '')).toBeNull()
    expect(parseJsonFieldValue('object', '{"n":[1]}')).toEqual({ n: [1] })
    expect(parseJsonFieldValue('array', '[1,null]')).toEqual([1, null])
    expect(() => parseJsonFieldValue('number', '"2"')).toThrow()
    expect(() => parseJsonFieldValue('number', '1e999')).toThrow()
    expect(() => parseJsonFieldValue('object', '[]')).toThrow()
  })
})
