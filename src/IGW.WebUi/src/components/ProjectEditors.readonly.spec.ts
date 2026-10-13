import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { describe, expect, it, vi } from 'vitest'
import ModbusTagEditor from './ModbusTagEditor.vue'
import ProtocolDeviceSettings from './ProtocolDeviceSettings.vue'
import ArrayValueViewer from './ArrayValueViewer.vue'
import { api } from '../api'
import { i18n } from '../i18n'

vi.mock('../api', () => ({ api: { arrayInfo: vi.fn(), arraySlice: vi.fn(), writeArraySlice: vi.fn() } }))

describe('工程配置只能看时的子编辑器', () => {
  it('Modbus 点位编辑器全部禁用且不回写', async () => {
    const wrapper = mount(ModbusTagEditor, { props: { modelValue: { address: 'HR:0', dataType: 'int16', access: 'readWrite' }, editable: false } })
    const controls = wrapper.findAll('input, select')
    expect(controls.length).toBeGreaterThan(0)
    for (const control of controls) expect(control.attributes('disabled')).toBeDefined()
    ;(wrapper.vm as any).$.setupState.change({}, { area: 'DI' })
    expect(wrapper.emitted('update:modelValue')).toBeUndefined()
  })

  it('Modbus 设备参数全部禁用且不回写；默认可编辑', async () => {
    const readOnly = mount(ProtocolDeviceSettings, { props: { modelValue: { unitId: '1' }, driverId: 'Modbus.Tcp', editable: false } })
    for (const control of readOnly.findAll('input, select')) expect(control.attributes('disabled')).toBeDefined()
    await readOnly.get('[aria-label="unitId"]').trigger('change')
    expect(readOnly.emitted('update:modelValue')).toBeUndefined()

    const editable = mount(ProtocolDeviceSettings, { props: { modelValue: { unitId: '1' }, driverId: 'Modbus.Tcp' } })
    expect(editable.get('[aria-label="unitId"]').attributes('disabled')).toBeUndefined()
  })

  it('数组切片不可写时没有写入按钮，也不会调用写入接口', async () => {
    const info = { id: 'A', elementType: 'Int16', dimensions: [1], lowerBounds: [0], totalElements: 1, good: 1, bad: 0, uncertain: 0, overallQuality: 'Good' as const }
    vi.mocked(api.arrayInfo).mockResolvedValue(info)
    vi.mocked(api.arraySlice).mockResolvedValue({ ...info, start: [0], length: [1], values: [1], qualityCodes: [0] })
    const wrapper = mount(ArrayValueViewer, {
      props: { tagId: 'A', definition: { elementType: 'Int16', dimensions: [1], lowerBounds: [0] }, writable: false },
      global: { plugins: [ElementPlus], config: { globalProperties: { $t: i18n.t } as any } },
    })
    await flushPromises()
    expect(wrapper.findAll('button').map(button => button.text())).not.toContain('写当前切片')
    const vm = wrapper.vm as any
    vm.openSlice()
    expect(vm.editOpen).toBe(false)
    await vm.saveWrite()
    expect(api.writeArraySlice).not.toHaveBeenCalled()
    wrapper.unmount()
  })
})
