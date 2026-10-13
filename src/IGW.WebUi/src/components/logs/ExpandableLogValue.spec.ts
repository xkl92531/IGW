import { mount } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import ExpandableLogValue from './ExpandableLogValue.vue'

describe('ExpandableLogValue', () => {
  afterEach(() => {
    vi.restoreAllMocks()
    document.body.innerHTML = ''
  })

  it('keeps a long value compact and lets the user expand and collapse it', async () => {
    const value = `POST https://mes.example/report/${'segment/'.repeat(80)}`
    const wrapper = mount(ExpandableLogValue, { props: { value } })

    expect(wrapper.get('[data-testid="log-value-content"]').classes()).toContain('collapsed')
    expect(wrapper.text()).toContain('展开全文')
    expect(wrapper.text()).toContain(`${value.length} 个字符`)

    await wrapper.get('[data-testid="toggle-log-value"]').trigger('click')
    expect(wrapper.get('[data-testid="log-value-content"]').classes()).toContain('expanded')
    expect(wrapper.text()).toContain('收起全文')
  })

  it('copies the complete value instead of the compact preview', async () => {
    const value = JSON.stringify({ payload: 'x'.repeat(3000) })
    const writeText = vi.fn().mockResolvedValue(undefined)
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText } })
    const wrapper = mount(ExpandableLogValue, { props: { value } })

    await wrapper.get('[data-testid="copy-log-value"]').trigger('click')

    expect(writeText).toHaveBeenCalledWith(value)
    expect(wrapper.text()).toContain('已复制')
  })

  it('opens thousands of report tags in a bounded paged viewer with search and download', async () => {
    const value = JSON.stringify({ tags: Array.from({ length: 3000 }, (_, index) => ({ name: `Tag${index}`, value: index })) })
    const createObjectURL = vi.fn(() => 'blob:igw-large-log')
    const revokeObjectURL = vi.fn()
    Object.defineProperty(URL, 'createObjectURL', { configurable: true, value: createObjectURL })
    Object.defineProperty(URL, 'revokeObjectURL', { configurable: true, value: revokeObjectURL })
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
    const wrapper = mount(ExpandableLogValue, { props: { value }, attachTo: document.body })

    expect(wrapper.get('[data-testid="open-large-log-viewer"]').text()).toContain('大内容')
    await wrapper.get('[data-testid="open-large-log-viewer"]').trigger('click')

    const viewer = document.body.querySelector('[data-testid="large-log-viewer"]') as HTMLElement
    const page = viewer.querySelector('[data-testid="large-log-page"]') as HTMLTextAreaElement
    expect(viewer).not.toBeNull()
    expect(page.value).toContain('Tag0')
    expect(page.value).not.toContain('Tag2999')
    expect(page.value.length).toBeLessThan(50_000)

    const search = viewer.querySelector('[data-testid="large-log-search"]') as HTMLInputElement
    search.value = 'Tag2999'
    search.dispatchEvent(new Event('input'))
    await wrapper.vm.$nextTick()
    expect(page.value).toContain('Tag2999')

    ;(viewer.querySelector('[data-testid="download-large-log"]') as HTMLButtonElement).click()
    expect(createObjectURL).toHaveBeenCalledOnce()
    expect(click).toHaveBeenCalledOnce()
    expect(revokeObjectURL).toHaveBeenCalledWith('blob:igw-large-log')
  })
})
