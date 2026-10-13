import { mount } from '@vue/test-utils'
import { expect, it } from 'vitest'
import ProtocolDeviceSettings from './ProtocolDeviceSettings.vue'

it('stores typed Modbus controls as legacy-compatible settings strings', async () => {
  const wrapper = mount(ProtocolDeviceSettings, { props: { driverId: 'Modbus.Tcp', modelValue: { unitId: '1', coilBlock: '1968' } } })
  expect(wrapper.get('[aria-label="coilBlock"]').attributes('min')).toBe('1')
  expect(wrapper.get('[aria-label="regBlock"]').attributes('max')).toBe('125')
  await wrapper.get('[aria-label="writeMode"]').setValue('single')
  expect(wrapper.emitted('update:modelValue')?.slice(-1)[0]?.[0]).toMatchObject({ unitId: '1', writeMode: 'single' })
})
