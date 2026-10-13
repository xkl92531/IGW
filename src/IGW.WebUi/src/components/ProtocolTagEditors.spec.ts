import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import ModbusTagEditor from './ModbusTagEditor.vue'

describe('Modbus point controls', () => {
  it('displays legacy Modbus wire offset without rewriting until edited; area change enforces read-only Boolean', async () => {
    const wrapper = mount(ModbusTagEditor, { props: { modelValue: { address: '40001', dataType: 'int16', access: 'readWrite' }, settings: {} } })
    expect((wrapper.get('[aria-label="Modbus offset"]').element as HTMLInputElement).value).toBe('0')
    expect(wrapper.emitted('update:modelValue')).toBeUndefined()
    await wrapper.get('[aria-label="Modbus area"]').setValue('DI')
    expect(wrapper.emitted('update:modelValue')?.slice(-1)[0]?.[0]).toMatchObject({ address: 'DI:0', dataType: 'bool', access: 'read' })
    expect(wrapper.text()).not.toContain('PLC 下界')
  })
  it('emits explicit register bit addresses', async () => {
    const wrapper = mount(ModbusTagEditor, { props: { modelValue: { address: 'HR:0.0', dataType: 'bool', access: 'read' } } })
    await wrapper.get('[aria-label="Register bit"]').setValue('3')
    expect(wrapper.emitted('update:modelValue')?.slice(-1)[0]?.[0]).toMatchObject({ address: 'HR:0.3' })
  })
})
