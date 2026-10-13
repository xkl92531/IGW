import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, shallowMount } from '@vue/test-utils'
import { api } from '../api'
import RuntimeLogDirectoryDialog from './RuntimeLogDirectoryDialog.vue'

vi.mock('../api', () => ({ api: { runtimeLogDirectories: vi.fn() } }))
afterEach(() => vi.clearAllMocks())

const mountOptions = (initialDirectory: string) => ({
  props: { modelValue: true, initialDirectory },
  global: {
    // 对话框桩要渲染插槽，否则列表内容不出现在 DOM 里。
    stubs: { ElDialog: { template: '<div><slot /><slot name="footer" /></div>' }, ElButton: true, ElInput: true, ElAlert: true },
    directives: { loading() {} },
  },
})

const drives = {
  currentPath: '', parentPath: null, truncated: false,
  directories: [
    { name: 'C:\\', path: 'C:\\', label: '系统', freeBytes: 20 * 1024 ** 3, totalBytes: 100 * 1024 ** 3 },
    { name: 'D:\\', path: 'D:\\', label: null, freeBytes: 512 * 1024 ** 2, totalBytes: 2 * 1024 ** 4 },
  ],
}

describe('gateway folder picker', () => {
  it('selects a server-confirmed directory instead of an unvalidated path in the address box', async () => {
    vi.mocked(api.runtimeLogDirectories).mockResolvedValue({ currentPath: 'D:\\logs', parentPath: 'D:\\', directories: [], truncated: false })
    const wrapper = shallowMount(RuntimeLogDirectoryDialog, mountOptions('D:\\logs'))
    await flushPromises()
    const vm = wrapper.vm as any
    expect(api.runtimeLogDirectories).toHaveBeenCalledWith('D:\\logs')
    vm.path = 'Z:\\unverified'
    vm.select()
    expect(wrapper.emitted('select')).toBeUndefined()
    vm.path = 'D:\\logs'
    vm.select()
    expect(wrapper.emitted('select')).toEqual([['D:\\logs']])
    wrapper.unmount()
  })

  it('shows drive label and free space in the drive list and cannot select the list itself', async () => {
    vi.mocked(api.runtimeLogDirectories).mockResolvedValue(drives)
    const wrapper = shallowMount(RuntimeLogDirectoryDialog, mountOptions(''))
    await flushPromises()
    const text = wrapper.text()
    expect(text).toContain('系统')
    expect(text).toContain('20.0 GB')
    expect(text).toContain('512 MB')
    const vm = wrapper.vm as any
    expect(vm.showingDrives).toBe(true)
    vm.select()
    expect(wrapper.emitted('select')).toBeUndefined()
    // 不足 1 GiB 的磁盘要提示：日志目录保存时会因空间不足被拒绝。
    expect(wrapper.findAll('.directory-row')[1].classes()).toContain('low-space')
    expect(text).not.toContain('可直接选择当前目录')
    wrapper.unmount()
  })

  it('falls back to the drive list when the initial folder cannot be opened', async () => {
    vi.mocked(api.runtimeLogDirectories)
      .mockRejectedValueOnce({ response: { data: { error: '无法浏览网关目录：文件夹不存在或无法访问：E:\\gone' } } })
      .mockResolvedValueOnce(drives)
    const wrapper = shallowMount(RuntimeLogDirectoryDialog, mountOptions('E:\\gone'))
    await flushPromises()
    const vm = wrapper.vm as any
    expect(api.runtimeLogDirectories).toHaveBeenNthCalledWith(1, 'E:\\gone')
    expect(api.runtimeLogDirectories).toHaveBeenNthCalledWith(2, '')
    expect(vm.showingDrives).toBe(true)
    expect(vm.error).toBe('')
    expect(vm.notice).toContain('E:\\gone')
    wrapper.unmount()
  })

  it('keeps the error when the user explicitly opens a missing folder', async () => {
    vi.mocked(api.runtimeLogDirectories)
      .mockResolvedValueOnce(drives)
      .mockRejectedValueOnce({ response: { data: { error: '无法浏览网关目录：文件夹不存在或无法访问：Z:\\x' } } })
    const wrapper = shallowMount(RuntimeLogDirectoryDialog, mountOptions(''))
    await flushPromises()
    const vm = wrapper.vm as any
    await vm.browse('Z:\\x')
    await flushPromises()
    expect(api.runtimeLogDirectories).toHaveBeenCalledTimes(2)
    expect(vm.error).toContain('Z:\\x')
    wrapper.unmount()
  })
})
