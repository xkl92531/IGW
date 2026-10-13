import { mount, flushPromises } from '@vue/test-utils'
import { expect, it, vi } from 'vitest'
import ElementPlus from 'element-plus'
import FlowTemplatePanel from './FlowTemplatePanel.vue'
import { i18n } from '../i18n'
const api = vi.hoisted(() => ({ flowTemplates: vi.fn(), flowTemplateExport: vi.fn(), flowTemplateApplyBulk: vi.fn() }))
vi.mock('../api', () => ({ api }))
it('shows delayed template loading and guards an export until failure clears its spinner', async () => {
  let finish!: (value: any) => void
  api.flowTemplates.mockImplementationOnce(() => new Promise(resolve => { finish = resolve }))
  api.flowTemplateExport.mockResolvedValue({ task: { flow: { nodes: [], edges: [] } } })
  const wrapper = mount(FlowTemplatePanel, { props: { catalog: { groups: [], deviceTemplates: [] } }, global: { plugins: [ElementPlus], stubs: { teleport: true }, config: { globalProperties: { $t: i18n.t } as any } } })
  await flushPromises()
  expect(wrapper.text()).toContain('正在加载流程模板')
  finish([{ name: 'Template', nodeCount: 1, fieldCount: 0 }])
  await flushPromises()
  api.flowTemplateExport.mockClear()
  let fail!: (reason: Error) => void
  api.flowTemplateExport.mockImplementationOnce(() => new Promise((_, reject) => { fail = reject }))
  const button = wrapper.findAll('button').find(item => item.text() === '导出')!
  await button.trigger('click')
  await vi.waitFor(() => expect(api.flowTemplateExport).toHaveBeenCalledTimes(1))
  await vi.waitFor(() => expect(wrapper.findAll('button').find(item => item.text() === '导出')!.classes()).toContain('is-loading'))
  await (wrapper.vm.$ as any).setupState.exportTpl('Template')
  expect(api.flowTemplateExport).toHaveBeenCalledTimes(1)
  fail(new Error('export unavailable'))
  await vi.waitFor(() => expect(wrapper.findAll('button').find(item => item.text() === '导出')!.classes()).not.toContain('is-loading'))
  expect(document.body.textContent).toContain('export unavailable')
  wrapper.unmount()
})
