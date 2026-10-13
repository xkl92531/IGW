import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import MesJsonEditor from './MesJsonEditor.vue'
import MesJsonMappingEditor from './MesJsonMappingEditor.vue'

describe('inspectable JSON configuration', () => {
  it('adds typed nested template fields while preserving existing arrays and placeholders', async () => {
    const wrapper = mount(MesJsonEditor, { props: { modelValue: '{"items":[{"n":"${input.n}"}],"keep":true}' } })
    await wrapper.get('[aria-label="JSON 字段路径"]').setValue('items[0].ok')
    await wrapper.get('[aria-label="JSON 字段类型"]').setValue('boolean')
    await wrapper.get('[aria-label="JSON 字段值"]').setValue('false')
    await wrapper.get('[data-action="apply-field"]').trigger('click')
    expect(JSON.parse(wrapper.emitted('update:modelValue')![0]![0] as string)).toEqual({ items: [{ n: '${input.n}', ok: false }], keep: true })
    wrapper.unmount()
  })
  it('keeps invalid typed field drafts out of configuration and does not replace an existing field by accident', async () => {
    const wrapper = mount(MesJsonEditor, { props: { modelValue: '{"count":2}' } })
    await wrapper.get('[aria-label="JSON 字段路径"]').setValue('other')
    await wrapper.get('[aria-label="JSON 字段类型"]').setValue('number')
    await wrapper.get('[aria-label="JSON 字段值"]').setValue('oops')
    await wrapper.get('[data-action="apply-field"]').trigger('click')
    expect(wrapper.find('[role="alert"]').exists()).toBe(true)
    expect(wrapper.emitted('update:modelValue')).toBeUndefined()
    await wrapper.get('[aria-label="JSON 字段路径"]').setValue('count')
    await wrapper.get('[aria-label="JSON 字段值"]').setValue('7')
    await wrapper.get('[data-action="apply-field"]').trigger('click')
    expect(wrapper.emitted('update:modelValue')).toBeUndefined()
    wrapper.unmount()
  })
  it('inspects rendered types and reports malformed preview samples without changing templates', async () => {
    const wrapper = mount(MesJsonEditor, { props: { modelValue: '{"n":"${input.data.count}"}' } })
    expect(wrapper.get('[data-inspector="rendered"]').text()).toContain('number')
    await wrapper.get('textarea[aria-label="样例上下文 JSON"]').setValue('{oops')
    expect(wrapper.find('[role="alert"]').exists()).toBe(true)
    expect(wrapper.find('[data-inspector="rendered"]').exists()).toBe(false)
    expect(wrapper.emitted('update:modelValue')).toBeUndefined()
    wrapper.unmount()
  })
  it('adds a source mapping from a literal-key sample field and previews native types', async () => {
    const wrapper = mount(MesJsonMappingEditor, { props: { sample: { 'a.b': { count: false } } } })
    await wrapper.get('[data-json-path="/a.b/count"] [data-action="select-field"]').trigger('click')
    const rows = wrapper.emitted('update:modelValue')![0]![0] as any[]
    expect(rows).toEqual([{ sourcePath: '/a.b/count', targetPath: 'count' }])
    await wrapper.setProps({ modelValue: rows })
    expect(wrapper.get('[data-inspector="mapped"]').text()).toContain('boolean')
    expect(wrapper.get('[data-inspector="mapped"]').text()).toContain('false')
    wrapper.unmount()
  })
  it('collapses nested sample fields and keeps sample edits out of saved mappings', async () => {
    const wrapper = mount(MesJsonMappingEditor)
    const source = wrapper.get('[data-inspector="source"]')
    expect(source.find('[data-json-path="$.data.count"]').exists()).toBe(true)
    await source.get('[data-json-path="$.data"] [data-action="toggle-field"]').trigger('click')
    expect(source.find('[data-json-path="$.data.count"]').exists()).toBe(false)
    await wrapper.get('textarea[aria-label="样例响应 JSON"]').setValue('{"n":3}')
    expect(source.find('[data-json-path="$.n"]').exists()).toBe(true)
    expect(wrapper.emitted('update:modelValue')).toBeUndefined()
    wrapper.unmount()
  })
})
