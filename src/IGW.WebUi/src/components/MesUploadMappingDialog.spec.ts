import { DOMWrapper, flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { reactive } from 'vue'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import MesUploadMappingDialog from './MesUploadMappingDialog.vue'
import { api } from '../api'
import { i18n } from '../i18n'

vi.mock('../api', () => ({ api: { parameterOptions: vi.fn(async () => ({ items: [], truncated: false })) } }))
const wrappers: VueWrapper[] = []
const field = (i = 0) => ({ tagId: `Line.PLC.DB10.Value${i}`, fieldName: `Value${i}`, parameterCode: '' })
const struct = (i = 0) => ({ kind: 'Object', baseGroup: `Line.PLC.DB10.Struct${i}`, aliasName: `Struct${i}`, members: [{ tagId: 'Code', fieldName: '' }], structs: [] })
const payload = () => ({ mode: 'FieldMapping', fields: [field()], structs: [struct()], append: { lot: 'lot-1' } })
async function open(value: any = payload(), editable = true) {
  const wrapper = mount(MesUploadMappingDialog, {
    props: { modelValue: true, payload: reactive(value), editable, scopeGroup: 'Line.PLC.DB10' },
    attachTo: document.body,
    global: { plugins: [ElementPlus], config: { globalProperties: { $t: i18n.t } as any } },
  })
  wrappers.push(wrapper)
  await flushPromises()
  const content = new DOMWrapper(document.body)
  return { get: content.get.bind(content), findAll: content.findAll.bind(content), emitted: wrapper.emitted.bind(wrapper), setProps: wrapper.setProps.bind(wrapper), vm: wrapper.vm }
}
beforeEach(() => { i18n.setLocale('zh-CN'); vi.clearAllMocks() })
afterEach(() => { wrappers.splice(0).forEach(wrapper => wrapper.unmount()); document.body.innerHTML = '' })

describe('首次上报映射配置弹窗', () => {
  it('取消放弃字段及结构草稿，输入 payload 和嵌套成员保持不变', async () => {
    const value = payload()
    const wrapper = await open(value)
    await wrapper.get('[data-testid="field-name"]').setValue('renamed')
    await wrapper.get('[data-testid="struct-dictionary-toggle"] input').setValue(true)
    expect(value).toEqual(payload())
    await wrapper.get('[data-testid="upload-mapping-cancel"]').trigger('click')
    expect(wrapper.emitted('apply')).toBeUndefined()
    expect(wrapper.emitted('update:modelValue')).toEqual([[false]])
  })

  it('应用独立草稿，保留字段参数码和无需成员参数码的 STRUCT 字典选择', async () => {
    const value = payload()
    const wrapper = await open(value)
    await wrapper.get('[data-testid="field-code"]').setValue('P001')
    await wrapper.get('[data-testid="struct-dictionary-toggle"] input').setValue(true)
    await wrapper.get('[data-testid="upload-mapping-apply"]').trigger('click')
    const applied = wrapper.emitted('apply')![0][0] as any
    expect(applied.fields[0].parameterCode).toBe('P001')
    expect(applied.structs[0].asDictionary).toBe(true)
    expect(applied.structs[0].members).toEqual([{ tagId: 'Code', fieldName: '' }])
    expect(applied.append).toEqual({ lot: 'lot-1' })
    expect(value).toEqual(payload())
    applied.structs[0].members[0].tagId = 'Changed'
    expect(value.structs[0].members[0].tagId).toBe('Code')
  })

  it.each(['fields', 'structs'])('清空 %s 只清除对应草稿数组', async part => {
    const value = payload()
    const wrapper = await open(value)
    await wrapper.get(`[data-testid="clear-${part}"]`).trigger('click')
    await wrapper.get('[data-testid="upload-mapping-apply"]').trigger('click')
    const applied = wrapper.emitted('apply')![0][0] as any
    expect(applied[part]).toEqual([])
    expect(applied[part === 'fields' ? 'structs' : 'fields']).toEqual(value[part === 'fields' ? 'structs' : 'fields'])
    expect(value).toEqual(payload())
  })

  it('数千字段每页只渲染 50 行，搜索和下一页不加载点位候选', async () => {
    const wrapper = await open({ fields: Array.from({ length: 2001 }, (_, i) => field(i)), structs: [] })
    expect(wrapper.findAll('[data-testid="mapping-field-row"]')).toHaveLength(50)
    expect(wrapper.get('[data-testid="mapping-field-row"]').text()).toContain('Line.PLC.DB10.Value0')
    await wrapper.get('[data-testid="field-pagination"] .btn-next').trigger('click')
    expect(wrapper.get('[data-testid="mapping-field-row"]').text()).toContain('Line.PLC.DB10.Value50')
    await wrapper.get('[data-testid="field-filter"]').setValue('Value2000')
    expect(wrapper.findAll('[data-testid="mapping-field-row"]')).toHaveLength(1)
    expect(wrapper.get('[data-testid="mapping-field-row"]').text()).toContain('Line.PLC.DB10.Value2000')
    expect(api.parameterOptions).not.toHaveBeenCalled()
  })

  it('STRUCT 按 5 张卡分页，成员仍分页且搜索定位结构', async () => {
    const structs = Array.from({ length: 12 }, (_, i) => ({ ...struct(i), members: Array.from({ length: 1000 }, (_, j) => ({ tagId: `Member${j}` })) }))
    const wrapper = await open({ fields: [], structs })
    expect(wrapper.findAll('[data-testid="struct-card"]')).toHaveLength(5)
    expect(wrapper.findAll('.struct-member-row')).toHaveLength(250)
    await wrapper.get('[data-testid="struct-pagination"] .btn-next').trigger('click')
    expect(wrapper.findAll('[data-testid="struct-card"]')).toHaveLength(5)
    await wrapper.get('[data-testid="struct-filter"]').setValue('Struct11')
    expect(wrapper.findAll('[data-testid="struct-card"]')).toHaveLength(1)
    expect(api.parameterOptions).not.toHaveBeenCalled()
  })

  it('每次重新打开从最新 payload 复制，丢弃取消的编辑和筛选', async () => {
    const value = payload()
    const wrapper = await open(value)
    await wrapper.get('[data-testid="field-code"]').setValue('abandoned')
    await wrapper.get('[data-testid="field-filter"]').setValue('missing')
    await wrapper.setProps({ modelValue: false })
    value.fields[0].parameterCode = 'latest'
    await wrapper.setProps({ modelValue: true })
    await flushPromises()
    expect((wrapper.get('[data-testid="field-code"]').element as HTMLInputElement).value).toBe('latest')
    expect((wrapper.get('[data-testid="field-filter"]').element as HTMLInputElement).value).toBe('')
  })

  it('只读禁用编辑、删行、清空和应用，伪造应用不生效，取消可用', async () => {
    const wrapper = await open(payload(), false)
    for (const selector of ['[data-testid="field-name"]', '[data-testid="field-code"]', '[data-testid="delete-field"]', '[data-testid="clear-fields"]', '[data-testid="clear-structs"]', '[data-testid="upload-mapping-apply"]']) {
      expect(wrapper.get(selector).attributes('disabled'), selector).toBeDefined()
    }
    await (wrapper.vm as any).$.setupState.apply()
    expect(wrapper.emitted('apply')).toBeUndefined()
    await wrapper.get('[data-testid="upload-mapping-cancel"]').trigger('click')
    expect(wrapper.emitted('update:modelValue')).toEqual([[false]])
  })

  it('关闭状态不能应用旧草稿', async () => {
    const wrapper = await open()
    await wrapper.setProps({ modelValue: false })
    await (wrapper.vm as any).$.setupState.apply()
    expect(wrapper.emitted('apply')).toBeUndefined()
  })

  it('关闭按钮仅取消，不应用编辑的草稿', async () => {
    const value = payload()
    const wrapper = await open(value)
    await wrapper.get('[data-testid="field-code"]').setValue('abandoned')
    await wrapper.get('.el-dialog__headerbtn').trigger('click')
    await flushPromises()
    expect(wrapper.emitted('apply')).toBeUndefined()
    expect(wrapper.emitted('update:modelValue')).toEqual([[false]])
    expect(value).toEqual(payload())
  })
})
