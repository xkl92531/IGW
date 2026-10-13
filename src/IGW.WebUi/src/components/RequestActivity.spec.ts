import { mount } from '@vue/test-utils'
import { nextTick } from 'vue'
import { describe, expect, it, vi } from 'vitest'
import RequestActivity from './RequestActivity.vue'
import { beginUiActivity } from '../utils/requestActivity'

describe('request status visibility', () => {
  it('avoids flashing fast reads and shows an elapsed wait for slow requests', async () => {
    vi.useFakeTimers()
    const wrapper = mount(RequestActivity)
    const fast = beginUiActivity('正在加载工程点位…')
    await nextTick(); await vi.advanceTimersByTimeAsync(100); fast(); await nextTick()
    expect(wrapper.find('[role=status]').exists()).toBe(false)
    const slow = beginUiActivity('正在加载工程点位…')
    await nextTick(); await vi.advanceTimersByTimeAsync(3500)
    expect(wrapper.get('[role=status]').text()).toContain('正在加载工程点位')
    expect(wrapper.text()).toContain('已等待 3 秒')
    slow(); await nextTick()
    expect(wrapper.find('[role=status]').exists()).toBe(false)
    wrapper.unmount(); vi.useRealTimers()
  })
})
