import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import LogTrendPanel from './LogTrendPanel.vue'

const buckets = [
  { start: '2026-09-13T07:58:00Z', total: 7, warnings: 2, errors: 1 },
  { start: '2026-09-13T07:59:00Z', total: 10, warnings: 3, errors: 2 },
]

describe('LogTrendPanel', () => {
  it('renders an original accessible SVG trend and supports series toggles', async () => {
    const wrapper = mount(LogTrendPanel, { props: { buckets } })
    expect(wrapper.find('svg[aria-label="日志趋势"]').exists()).toBe(true)
    expect(wrapper.findAll('.trend-bucket')).toHaveLength(2)
    await wrapper.find('[data-series="warnings"]').trigger('click')
    expect(wrapper.find('.series-warnings').exists()).toBe(false)
    await wrapper.findAll('.trend-bucket')[1].trigger('keydown', { key: 'Enter' })
    expect(wrapper.emitted('select')?.[0]).toEqual([buckets[1]])
  })
})
