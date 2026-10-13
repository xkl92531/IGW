import { flushPromises, mount } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import ProjectImportDialog from './ProjectImportDialog.vue'
import ProjectExportDialog from './ProjectExportDialog.vue'

describe('project JSON dialogs', () => {
  it('inspects JSON, shows counts and blocks commit on compatibility errors', async () => {
    const inspectImport = vi.fn(async () => ({
      token: 'token', fileHash: 'hash', channels: 2, devices: 3, groups: 4, tags: 100,
      mesTasks: 5, hasPortableSecrets: true,
      compatibility: { issues: [{ path: 'PLC1', code: 'unsupported', severity: 'error' as const, message: '不兼容驱动' }] },
    }))
    const commitImport = vi.fn()
    const wrapper = mount(ProjectImportDialog, {
      props: { modelValue: true, service: { inspectImport, commitImport } },
    })
    const input = wrapper.get('input[type="file"]')
    expect(input.attributes('accept')).toBe('.json,application/json')
    Object.defineProperty(input.element, 'files', { value: [new File(['{}'], 'line.json')] })
    await input.trigger('change')
    await new Promise(resolve => setTimeout(resolve, 0))

    expect(wrapper.text()).toContain('100')
    expect(wrapper.text()).toContain('不兼容驱动')
    expect(wrapper.find('input[type="password"]').exists()).toBe(true)
    expect(wrapper.get('[data-action="commit"]').attributes('disabled')).toBeDefined()
  })

  it('has one export action named 导出 JSON and no legacy exchange wording', () => {
    const wrapper = mount(ProjectExportDialog, { props: { modelValue: true } })
    expect(wrapper.get('[data-action="export"]').text()).toBe('导出 JSON')
    expect(wrapper.text()).not.toContain('.igwproj')
    expect(wrapper.text()).not.toContain('导出 KEP')
  })

  it('labels inspection and commit separately, blocks duplicate submit and preserves a failed import', async () => {
    let inspect!: (value: any) => void, fail!: (error: Error) => void
    const inspectImport = vi.fn(() => new Promise<any>(resolve => { inspect = resolve }))
    const commitImport = vi.fn(() => new Promise<void>((_, reject) => { fail = reject }))
    const wrapper = mount(ProjectImportDialog, { props: { modelValue: true, service: { inspectImport, commitImport } } })
    const input = wrapper.get('input[type="file"]')
    Object.defineProperty(input.element, 'files', { value: [new File(['{}'], 'line.json')] })
    await input.trigger('change')
    expect(wrapper.get('[role="status"]').text()).toContain('正在检查文件')
    expect(wrapper.get('[aria-label="关闭"]').attributes('disabled')).toBeDefined()
    inspect({ token: 'token', fileHash: 'hash', channels: 1, devices: 1, groups: 1, tags: 1, mesTasks: 0, hasPortableSecrets: false, compatibility: { issues: [] } })
    await flushPromises()
    await wrapper.get('[data-action="commit"]').trigger('click')
    await wrapper.get('[data-action="commit"]').trigger('click')
    expect(commitImport).toHaveBeenCalledTimes(1)
    expect(wrapper.get('[role="status"]').text()).toContain('正在导入并应用工程')
    fail(new Error('服务器拒绝导入'))
    await flushPromises()
    expect(wrapper.text()).toContain('服务器拒绝导入')
    expect(wrapper.emitted('update:modelValue')).toBeUndefined()
    expect(wrapper.get('[data-action="commit"]').attributes('disabled')).toBeUndefined()
    expect(wrapper.find('[role="status"]').exists()).toBe(false)
  })

  it('holds download feedback until handoff settles and retains the dialog on failure', async () => {
    let fail!: (error: Error) => void
    const download = vi.fn(() => new Promise<void>((_, reject) => { fail = reject }))
    const wrapper = mount(ProjectExportDialog, { props: { modelValue: true, download } })
    await wrapper.get('[data-action="export"]').trigger('click')
    expect(wrapper.get('[role="status"]').text()).toContain('交给浏览器下载')
    await wrapper.get('[data-action="export"]').trigger('click')
    await vi.waitFor(() => expect(download).toHaveBeenCalledTimes(1))
    fail(new Error('下载准备失败'))
    await flushPromises()
    expect(wrapper.get('[role="alert"]').text()).toBe('下载准备失败')
    expect(wrapper.emitted('update:modelValue')).toBeUndefined()
    expect(wrapper.get('[data-action="export"]').attributes('disabled')).toBeUndefined()
  })
})
