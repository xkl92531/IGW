import { mount } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import ElementPlus from 'element-plus'
import MesInterfaceListenerEditor from './MesInterfaceListenerEditor.vue'
import MesJsonEditor from './MesJsonEditor.vue'
import { defaultInterfaceListener } from '../utils/mesJsonPreview'
import { i18n } from '../i18n'
vi.mock('../api', () => ({ api: { parameterOptions: vi.fn(async () => ({ items: [], truncated: false })) } }))
const global = { plugins: [ElementPlus], config: { globalProperties: { $t: i18n.t } as any } }
describe('interface listener configuration', () => {
  it('defaults to SignalR and persists receive method and argument templates', async () => {
    const wrapper = mount(MesInterfaceListenerEditor, { props: { scopeGroup: 'Line.Tool.Results' }, global })
    expect((wrapper.get('select').element as HTMLSelectElement).value).toBe('SignalR')
    expect(wrapper.text()).toContain('SignalR')
    expect(wrapper.text()).not.toContain('旧版监听器自动下发条码')
    expect(wrapper.text()).not.toContain('SetVin')
    await wrapper.findAllComponents(MesJsonEditor)[0]!.vm.$emit('update:modelValue', '[{"station":"OP010"}]')
    expect(wrapper.emitted('update:modelValue')?.[0]?.[0]).toMatchObject({ protocol: 'SignalR', receiveMethod: 'ReceiveBarcode', subscriptionTemplate: '[{"station":"OP010"}]' })
    wrapper.unmount()
  })
  it('SSE disables outbound templates without deleting saved WebSocket settings', async () => {
    const config = { ...defaultInterfaceListener(), protocol: 'SSE' as const, subscriptionTemplate: '{"topic":"jobs"}', responseTemplate: '{"ok":true}' }
    const wrapper = mount(MesInterfaceListenerEditor, { props: { modelValue: config, scopeGroup: 'Line.Tool.Results' }, global })
    expect(wrapper.findAllComponents(MesJsonEditor).every(editor => editor.props('disabled'))).toBe(true)
    expect(wrapper.text()).toContain('HTTP GET')
    await wrapper.get('select').setValue('WebSocket')
    expect(wrapper.emitted('update:modelValue')?.[0]?.[0]).toMatchObject({ protocol: 'WebSocket', subscriptionTemplate: config.subscriptionTemplate, responseTemplate: config.responseTemplate })
    wrapper.unmount()
  })
  it('uses an independent ignored-message reply whose preview context has no job', async () => {
    const config = { ...defaultInterfaceListener(), ignoreResponseTemplate: '[{"state":"${job.state}"}]' }
    const wrapper = mount(MesInterfaceListenerEditor, { props: { modelValue: config, scopeGroup: 'Line.Tool.Results' }, global })
    const ignored = wrapper.findAllComponents(MesJsonEditor)[2]!
    expect(ignored.props('context')).toMatchObject({ fields: null })
    expect(ignored.props('context')).not.toHaveProperty('job')
    expect(ignored.find('[role="alert"]').text()).toContain('job.state')
    wrapper.unmount()
  })
  it('retains original input alongside the normalized mapped object in the local preview', () => {
    const config = { ...defaultInterfaceListener(), inputMappings: [{ sourcePath: 'data.serialNo', targetPath: 'serialNo' }] }
    const wrapper = mount(MesInterfaceListenerEditor, { props: { modelValue: config, scopeGroup: 'Line.Tool.Results' }, global })
    expect(wrapper.findAllComponents(MesJsonEditor)[1]!.props('context')).toMatchObject({ input: { data: { serialNo: 'VIN001' } }, mapped: { serialNo: 'VIN001' } })
    wrapper.unmount()
  })
  it('propagates invalid sample errors to replies and mappings instead of showing successful empty previews', async () => {
    const config = { ...defaultInterfaceListener(), responseTemplate: '[{"ok":true}]', ignoreResponseTemplate: '[{"ok":true}]' }
    const wrapper = mount(MesInterfaceListenerEditor, { props: { modelValue: config, scopeGroup: 'Line.Tool.Results' }, global })
    await wrapper.get('textarea[aria-label="样例原始消息 JSON"]').setValue('{broken')
    const replies = wrapper.findAllComponents(MesJsonEditor).slice(1)
    expect(replies.every(reply => reply.find('[role="alert"]').exists())).toBe(true)
    expect(wrapper.emitted('update:modelValue')).toBeUndefined()
    wrapper.unmount()
  })
  it('leaves leftover fields of an old listener untouched and offers no editor for them', async () => {
    const config = { ...defaultInterfaceListener(), barcodeTagId: 'Tool.Code', barcodeField: 'data.serialNo' } as any
    const wrapper = mount(MesInterfaceListenerEditor, { props: { modelValue: config, scopeGroup: 'Line.Tool.Results' }, global })
    expect(wrapper.text()).not.toContain('条码字段路径')
    expect(wrapper.emitted('update:modelValue')).toBeUndefined()
    wrapper.unmount()
  })
  it('edits typed change fields and headers, keeps invalid JSON out of persisted configuration', async () => {
    const wrapper = mount(MesInterfaceListenerEditor, { props: { scopeGroup: 'Line.Tool.Results' }, global })
    await wrapper.get('textarea[aria-label="变化字段路径"]').setValue('data.serialNo\n data.count\n')
    expect(wrapper.emitted('update:modelValue')?.[0]?.[0]).toMatchObject({ changeFields: ['data.serialNo', 'data.count'] })
    await wrapper.get('textarea[aria-label="请求头 JSON"]').setValue('{"Authorization":"Bearer DEMO"}')
    expect(wrapper.emitted('update:modelValue')?.[1]?.[0]).toMatchObject({ headers: { Authorization: 'Bearer DEMO' } })
    await wrapper.get('textarea[aria-label="请求头 JSON"]').setValue('{"bad":42}')
    expect(wrapper.find('[role="alert"]').exists()).toBe(true)
    expect(wrapper.emitted('update:modelValue')).toHaveLength(2)
    wrapper.unmount()
  })
})
