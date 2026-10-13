import { describe, expect, it } from 'vitest'
import { mapJson, renderJsonTemplate, selectJsonPath } from './mesJsonPreview'

describe('local MES JSON preview', () => {
  it('preserves native types and escapes embedded strings', () => {
    const context = { input: { data: { serialNo: 'A"\\B', count: 3, ok: true } } }
    expect(renderJsonTemplate('{"n":"${input.data.count}","ok":"${input.data.ok}","label":"SN:${input.data.serialNo}"}', context))
      .toEqual({ n: 3, ok: true, label: 'SN:A"\\B' })
  })
  it('selects arrays, distinguishes null from missing and rejects malformed paths', () => {
    expect(selectJsonPath({ data: [null, { n: 4 }] }, '$.data[1].n')).toBe(4)
    expect(selectJsonPath({ data: [null] }, 'data[0]')).toBeNull()
    expect(() => selectJsonPath({}, 'missing')).toThrow()
    expect(() => selectJsonPath({}, 'data..n')).toThrow()
  })
  it('maps nested data and treats reserved property names as literal data', () => {
    expect(mapJson({ data: [{ n: 4 }] }, [{ sourcePath: 'data[0].n', targetPath: 'result.count' }])).toEqual({ result: { count: 4 } })
    expect(JSON.stringify(mapJson({}, [{ sourcePath: '$', targetPath: '__proto__.x' }]))).toBe('{"__proto__":{"x":{}}}')
    expect(({} as any).x).toBeUndefined()
  })
  it('supports literal-key JSON pointers and mapped arrays, rejects overlapping targets', () => {
    expect(selectJsonPath({ 'a.b': { '/': 7 } }, '/a.b/~1')).toBe(7)
    expect(mapJson({ value: false }, [{ sourcePath: 'value', targetPath: 'items[0].ok' }])).toEqual({ items: [{ ok: false }] })
    expect(() => mapJson({ n: 1 }, [{ sourcePath: 'n', targetPath: 'a' }, { sourcePath: 'n', targetPath: 'a.b' }])).toThrow()
    expect(() => mapJson({ n: 1 }, [{ sourcePath: 'n', targetPath: 'a.b' }, { sourcePath: 'n', targetPath: 'a' }])).toThrow()
  })
})
