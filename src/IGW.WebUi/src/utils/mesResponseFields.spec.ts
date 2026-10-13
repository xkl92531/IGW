import { describe, expect, it } from 'vitest'
import { extractResponseBody, parseResponseFields, responseFields } from './mesResponseFields'

/**
 * 响应字段路径必须和后端 MesUploadEngine.FlattenResponse 展开的键完全一致，
 * 否则界面上选出来的字段运行时取不到值——那正是现场只能逐字手填的原因。
 */
describe('从接口响应 JSON 解析可选的响应字段', () => {
  it('对象用点分，根层不加前缀', () => {
    const fields = responseFields({ isSuccess: true, backResult: { errorCode: 'E1', errorMessage: '工单不存在' } })

    expect(fields.map(f => f.path)).toEqual(['isSuccess', 'backResult.errorCode', 'backResult.errorMessage'])
  })

  it('数组用下标，和后端一致', () => {
    const fields = responseFields({ items: [{ ok: true }, { ok: false }], nested: [[1]] })

    expect(fields.map(f => f.path)).toEqual(['items[0].ok', 'items[1].ok', 'nested[0][0]'])
  })

  /** 对象和数组本身写不进 PLC 点位，后端也不会把它们放进可取值的字典。 */
  it('只给标量叶子，不给中间节点', () => {
    const fields = responseFields({ backResult: { code: 1 }, empty: {}, list: [] })

    expect(fields.map(f => f.path)).toEqual(['backResult.code'])
  })

  it('null 是标量，照样可选', () =>
    expect(responseFields({ a: null }).map(f => f.path)).toEqual(['a']))

  it('根位置的标量没有路径可用，跳过', () =>
    expect(responseFields('OK')).toEqual([]))

  it('带上样例值供界面预览，长字符串截断', () => {
    const fields = responseFields({ msg: 'x'.repeat(60), n: 12, ok: false })

    expect(fields[0]!.preview).toBe('x'.repeat(40) + '…')
    expect(fields[1]!.preview).toBe('12')
    expect(fields[2]!.preview).toBe('false')
  })

  /** 现场响应可能很大；配置页不能因此卡死或白屏。 */
  it('超大响应截断而不是抛错', () => {
    const huge: Record<string, number> = {}
    for (let i = 0; i < 5000; i++) huge['k' + i] = i

    const fields = responseFields(huge)

    expect(fields.length).toBe(2000)
  })

  it('深层嵌套不会无限递归', () => {
    let deep: unknown = 1
    for (let i = 0; i < 200; i++) deep = { next: deep }

    expect(() => responseFields(deep)).not.toThrow()
  })
})

describe('解析用户粘贴或从日志取回的响应文本', () => {
  it('非法 JSON 给出错误而不是抛出', () => {
    const { fields, error } = parseResponseFields('{ 坏掉的 }')

    expect(fields).toEqual([])
    expect(error).not.toBe('')
  })

  it('空文本不算错误', () => expect(parseResponseFields('   ')).toEqual({ fields: [], error: '' }))

  it('正常 JSON 解析出字段', () =>
    expect(parseResponseFields('{"isSuccess":false}').fields.map(f => f.path)).toEqual(['isSuccess']))

  /** 日志导出的原始报文是「发送内容 + 返回内容」整段，只取返回那半。 */
  it('从整段报文里取出返回内容', () => {
    const text = '===== 发送内容 =====\n{"sn":"A1"}\n===== 返回内容 =====\n{"isSuccess":true}'

    expect(extractResponseBody(text)).toBe('{"isSuccess":true}')
  })

  it('已经是纯响应正文时原样返回', () =>
    expect(extractResponseBody('{"isSuccess":true}')).toBe('{"isSuccess":true}'))
})
