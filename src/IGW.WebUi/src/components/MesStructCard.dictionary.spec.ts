import { mount, type VueWrapper } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { reactive } from 'vue'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import MesStructCard from './MesStructCard.vue'
import { i18n } from '../i18n'

const wrappers: VueWrapper[] = []
function struct(overrides: Record<string, unknown> = {}) {
  return {
    kind: 'Object', baseGroup: 'Line.PLC.DB10', aliasName: 'renamedData',
    countMode: 'Fixed', startIndex: 0, fixedCount: 1,
    members: [{ tagId: 'Temperature', fieldName: 'temp' }], structs: [], ...overrides,
  }
}
function card(value: ReturnType<typeof struct>, editable = true) {
  const wrapper = mount(MesStructCard, {
    props: { struct: reactive(value), editable },
    global: { plugins: [ElementPlus], config: { globalProperties: { $t: i18n.t } as any } },
  })
  wrappers.push(wrapper)
  return wrapper
}
const toggleSelector = '[data-testid="struct-dictionary-toggle"] input[type="checkbox"]'

beforeEach(() => i18n.setLocale('zh-CN'))
afterEach(() => { wrappers.splice(0).forEach(wrapper => wrapper.unmount()) })

describe('STRUCT 转为根 parameterDic 的选择', () => {
  it.each([undefined, false])('旧配置及 false 默认保留原样（asDictionary=%s）', asDictionary => {
    const value = struct(asDictionary === undefined ? {} : { asDictionary })
    const wrapper = card(value)
    expect((wrapper.get(toggleSelector).element as HTMLInputElement).checked).toBe(false)
    expect(wrapper.emitted('change')).toBeUndefined()
  })

  it('勾选整个 STRUCT 不要求成员参数码，切换绑定并通知保存', async () => {
    const value = struct()
    const wrapper = card(value)
    const control = wrapper.get(toggleSelector)
    expect(wrapper.get('[data-testid="struct-dictionary-toggle"]').text()).toBe('转为字典')
    await control.setValue(true)
    expect(value).toMatchObject({ asDictionary: true })
    expect(value.members).toEqual([{ tagId: 'Temperature', fieldName: 'temp' }])
    expect(wrapper.emitted('change')).toHaveLength(1)
    const descriptionId = control.attributes('aria-describedby')
    expect(descriptionId).toBeTruthy()
    expect(wrapper.get(`[id="${descriptionId}"]`).text()).toContain('完整 PLC 点位路径')
    expect(wrapper.get(`[id="${descriptionId}"]`).text()).toContain('别名')
    await control.setValue(false)
    expect(value).toMatchObject({ asDictionary: false })
    expect(wrapper.emitted('change')).toHaveLength(2)
  })

  it('只读账号独立禁用转换开关，伪造 change 也不能修改，仍能收起展开', async () => {
    const value = struct({ asDictionary: true })
    const wrapper = card(value, false)
    const control = wrapper.get(toggleSelector)
    expect(control.attributes('disabled')).toBeDefined()
    expect((control.element as HTMLInputElement).checked).toBe(true)
    ;(control.element as HTMLInputElement).checked = false
    await control.trigger('change')
    expect(value).toMatchObject({ asDictionary: true })
    expect(wrapper.emitted('change')).toBeUndefined()
    await wrapper.get('button.fold').trigger('click')
    expect(wrapper.find('.struct-member-row').exists()).toBe(false)
    await wrapper.get('button.fold').trigger('click')
    expect(wrapper.find('.struct-member-row').exists()).toBe(true)
  })

  it('子 STRUCT 收起时仍可独立选择，不开启父结构或兄弟结构', async () => {
    const child = struct({ baseGroup: 'Child' })
    const sibling = struct({ baseGroup: 'Sibling' })
    const value = struct({ structs: [child, sibling] })
    const wrapper = card(value)
    const controls = wrapper.findAll(toggleSelector)
    expect(controls).toHaveLength(3)
    await controls[1].setValue(true)
    expect(child).toMatchObject({ asDictionary: true })
    expect((value as any).asDictionary).toBeUndefined()
    expect((sibling as any).asDictionary).toBeUndefined()
    expect((controls[0].element as HTMLInputElement).checked).toBe(false)
    expect((controls[2].element as HTMLInputElement).checked).toBe(false)
    expect(wrapper.emitted('change')).toHaveLength(1)
  })

  it('只读权限传给子结构的转换开关', () => {
    const wrapper = card(struct({ structs: [struct({ baseGroup: 'Child' })] }), false)
    const controls = wrapper.findAll(toggleSelector)
    expect(controls).toHaveLength(2)
    controls.forEach(control => expect(control.attributes('disabled')).toBeDefined())
  })

  it('开启转换仍保留 50 个成员的分页', async () => {
    const wrapper = card(struct({ members: Array.from({ length: 51 }, (_, i) => ({ tagId: `Value${i}`, fieldName: '' })) }))
    await wrapper.get(toggleSelector).setValue(true)
    expect(wrapper.findAll('.struct-member-row')).toHaveLength(50)
    await wrapper.get('.mapping-pagination .btn-next').trigger('click')
    expect(wrapper.findAll('.struct-member-row')).toHaveLength(1)
    expect((wrapper.get('.struct-member-row input').element as HTMLInputElement).value).toBe('Value50')
  })
})
