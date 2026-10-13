import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus, { ElSelectV2 } from 'element-plus'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import MesParameterPicker from './MesParameterPicker.vue'
import { api } from '../api'
import { i18n } from '../i18n'

vi.mock('../api', () => ({
  api: {
    parameterOptions: vi.fn(async () => ({ items: [], truncated: false })),
  },
}))

describe('MesParameterPicker request timing', () => {
  beforeEach(() => {
    i18n.setLocale('zh-CN')
    vi.mocked(api.parameterOptions).mockClear()
  })

  it('rejects write targets outside the bound group', async () => {
    const target = 'Line.S7.Result.Torque'
    vi.mocked(api.parameterOptions).mockResolvedValue({ items: [{ value: target, fullPath: target, label: 'Torque', address: 'DB1.DBD0', kind: 'tag', scope: 'OtherDevice', description: '' }], truncated: false })
    const wrapper = mount(MesParameterPicker, {
      props: { modelValue: '', scopeGroup: 'Line.Tool.Result', access: 'write' },
      global: { plugins: [ElementPlus], config: { globalProperties: { $t: i18n.t } as any } },
    })
    const select = wrapper.findComponent(ElSelectV2)
    select.vm.$emit('visible-change', true)
    await vi.waitFor(() => expect(api.parameterOptions).toHaveBeenCalled())
    await flushPromises()
    expect(select.props('allowCreate')).toBe(false)
    expect(select.props('options').find((option: any) => option.value === target)?.disabled).toBe(true)
    select.vm.$emit('update:modelValue', target)
    select.vm.$emit('change', target)
    // 伪造的组件事件也不能选中服务端未返回为可写的目标。
    select.vm.$emit('update:modelValue', 'Line.S7.Missing')
    select.vm.$emit('change', 'Line.S7.Missing')
    expect(wrapper.emitted('update:modelValue')).toBeUndefined()
    expect(wrapper.emitted('change')).toBeUndefined()
    wrapper.unmount()
  })

  it('does not validate its current value until the user opens the picker', async () => {
    const currentPath = 'Line.PLC.OP200Data.DataReady'
    const wrapper = mount(MesParameterPicker, {
      props: {
        modelValue: currentPath,
        scopeGroup: 'Line.PLC.OP200Data',
      },
      global: {
        plugins: [ElementPlus],
        config: { globalProperties: { $t: i18n.t } as any },
      },
    })
    await flushPromises()
    expect(api.parameterOptions).not.toHaveBeenCalled()

    wrapper.findComponent(ElSelectV2).vm.$emit('visible-change', true)
    await flushPromises()
    expect(api.parameterOptions).toHaveBeenCalledTimes(1)
    expect(vi.mocked(api.parameterOptions).mock.calls[0]?.[0]).toMatchObject({
      q: currentPath,
      limit: 100,
    })

    wrapper.unmount()
  })

  it('shows the complete selected path and the real PLC address', async () => {
    vi.mocked(api.parameterOptions).mockResolvedValue({
      items: [{
        value: 'Line.PLC.OP200Data.StationEntrance.MESFlag',
        label: 'MESFlag',
        fullPath: 'Line.PLC.OP200Data.StationEntrance.MESFlag',
        address: 'ns=3;s="Unit1MES"."PLC=>MES"."MESFlag"',
        scope: 'CurrentGroup',
        kind: 'tag',
        description: 'MES 状态',
      }],
      truncated: false,
    })

    const wrapper = mount(MesParameterPicker, {
      props: {
        modelValue: 'Line.PLC.OP200Data.StationEntrance.MESFlag',
        scopeGroup: 'Line.PLC.OP200Data',
      },
      global: {
        plugins: [ElementPlus],
        config: { globalProperties: { $t: i18n.t } as any },
      },
    })

    expect(wrapper.get('.selected-parameter-path').text())
      .toBe('Line.PLC.OP200Data.StationEntrance.MESFlag')

    wrapper.findComponent(ElSelectV2).vm.$emit('visible-change', true)
    await flushPromises()

    expect(wrapper.get('.selected-parameter-address').text())
      .toContain('ns=3;s="Unit1MES"."PLC=>MES"."MESFlag"')
    wrapper.unmount()
  })
})
