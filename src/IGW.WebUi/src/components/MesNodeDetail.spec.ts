import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import MesNodeDetail from './MesNodeDetail.vue'
import { i18n } from '../i18n'

const item = (name: string) => ({
  tagId: `PLC1.OP10.${name}`, written: '1', readBack: '', attempts: 1,
  statusCode: 0x808a0000, statusName: 'BadNotConnected', stage: '连接/通信', error: 'PLC 未连接',
})

function view(items: any[], total: number) {
  return mount(MesNodeDetail, {
    props: { status: { id: 'n1', state: 'fail', ageMs: 0, detail: { kind: 'write', items, total } } },
    global: { config: { globalProperties: { $t: i18n.t } as any } },
  })
}

describe('MesNodeDetail write failures', () => {
  it('lists only failed tags with total and truncation hint', () => {
    const wrapper = view([item('A'), item('B')], 120)
    expect(wrapper.get('[data-test="write-fail-summary"]').text()).toContain('120')
    expect(wrapper.get('[data-test="write-fail-summary"]').text()).toContain('2')
    expect(wrapper.findAll('[data-test="write-fail-row"]')).toHaveLength(2)
    expect(wrapper.find('[data-test="write-fail-detail"]').exists()).toBe(false)
  })

  it('expands one row to show the reason', async () => {
    const wrapper = view([item('A'), item('B')], 2)
    await wrapper.findAll('[data-test="write-fail-row"]')[1].trigger('click')
    const detail = wrapper.get('[data-test="write-fail-detail"]').text()
    expect(detail).toContain('PLC1.OP10.B')
    expect(detail).toContain('PLC 未连接')
    expect(detail).toContain('0x808A0000')
    await wrapper.findAll('[data-test="write-fail-row"]')[1].trigger('click')
    expect(wrapper.find('[data-test="write-fail-detail"]').exists()).toBe(false)
  })

  it('labels a write failure recorded on an idle node', () => {
    const wrapper = mount(MesNodeDetail, {
      props: { status: { id: 'n1', state: 'idle', ageMs: 0, detail: { kind: 'write', items: [item('A')], total: 1 } } },
      global: { config: { globalProperties: { $t: i18n.t } as any } },
    })
    expect(wrapper.get('.head').text()).toContain('回写失败')
    expect(wrapper.get('.head').text()).not.toContain('idle')
  })
})
