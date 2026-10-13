import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import MesBatchSaveDialog from './MesBatchSaveDialog.vue'
import { projectApi } from '../features/project/api'
import { i18n } from '../i18n'

vi.mock('../features/project/api', () => ({ projectApi: { mesTasks: vi.fn(), revision: vi.fn() } }))
const rows = Array.from({ length: 203 }, (_, i) => ({
  id: `flow-${i}`, name: `流程 ${i}`, deviceRef: 'PLC8.OP140', groupRef: `PLC8.OP140.Flow${i}`,
  enabled: i % 2 === 0, flowAvailable: true, collectIntervalMs: i === 0 ? 5000 : 1000,
  groupId: null, deviceId: null, channelId: null, description: null, rowVersion: 1,
}))
let wrapper: ReturnType<typeof mount> | undefined
const open = (saveBatch = vi.fn(async () => {}), tasks: any[] = []) => {
  wrapper = mount(MesBatchSaveDialog, {
    props: { modelValue: true, revision: 'rev-1', tasks, saveBatch },
    global: { plugins: [ElementPlus], stubs: { teleport: true } },
  })
  return wrapper
}
beforeEach(() => {
  vi.resetAllMocks(); i18n.setLocale('zh-CN')
  vi.mocked(projectApi.revision).mockReturnValue('rev-1')
  vi.mocked(projectApi.mesTasks).mockImplementation(async query => ({
    items: rows.slice(query.offset ?? 0, (query.offset ?? 0) + (query.limit ?? 50)),
    offset: query.offset ?? 0, limit: query.limit ?? 50, total: rows.length,
  }))
})
afterEach(() => { wrapper?.unmount(); wrapper = undefined })

describe('collection flow batch management', () => {
  it('shows slow batch save feedback and blocks duplicate submissions until failure resets it', async () => {
    let fail!: (reason: Error) => void
    const save = vi.fn(() => new Promise<void>((_, reject) => { fail = reject }))
    const view = open(save)
    await flushPromises()
    const button = view.findAll('button').find(item => item.text() === '保存全部流程（保留各自周期）')!
    await button.trigger('click')
    expect(view.text()).toContain('正在保存流程')
    await vi.waitFor(() => expect(view.findAll('button').find(item => item.text() === '保存全部流程（保留各自周期）')!.classes()).toContain('is-loading'))
    await (view.vm.$ as any).setupState.save(false)
    expect(save).toHaveBeenCalledTimes(1)
    fail(new Error('batch unavailable'))
    await vi.waitFor(() => expect(view.findAll('button').find(item => item.text() === '保存全部流程（保留各自周期）')!.classes()).not.toContain('is-loading'))
    expect(view.text()).toContain('batch unavailable')
  })
  it('allows editing the period after a slow catalog response', async () => {
    let finish!: (value: any) => void
    vi.mocked(projectApi.mesTasks).mockImplementationOnce(() => new Promise(resolve => { finish = resolve }))
    const view = open()
    await flushPromises()
    finish({ items: rows.slice(0, 50), total: rows.length, offset: 0, limit: 50 })
    await flushPromises()
    expect(view.find('input[role="spinbutton"]').attributes('aria-disabled')).toBe('false')
  })

  it('saves all without applying the period box or checkbox selection', async () => {
    const save = vi.fn(async () => {})
    const view = open(save, [{ id: 'flow-0', collectIntervalMs: 200, enabled: false }])
    await flushPromises()
    expect(view.text()).toContain('200 ms')
    expect(view.find('input[role="spinbutton"]').attributes('aria-disabled')).toBe('false')
    await view.find('.el-table__row input[type="checkbox"]').setValue(true)
    await view.findAll('button').find(button => button.text() === '保存全部流程（保留各自周期）')!.trigger('click')
    await flushPromises()
    expect(save).toHaveBeenCalledExactlyOnceWith({ periodTaskIds: [] }, 'rev-1')
    expect(view.text()).toContain('全部流程已保存')
  })

  it('loads every filtered page and applies a period only to selected ids', async () => {
    const save = vi.fn(async () => {})
    const view = open(save)
    await flushPromises()
    await view.findAll('button').find(button => button.text() === '全选筛选结果')!.trigger('click')
    await flushPromises()
    expect(view.text()).toContain('已选 203 个')
    expect(projectApi.mesTasks).toHaveBeenCalledWith(expect.objectContaining({ offset: 200, limit: 200 }), expect.any(AbortSignal))
    await view.find('.el-table__row input[type="checkbox"]').setValue(false)
    await view.findAll('button').find(button => button.text() === '修改选中周期并保存全部')!.trigger('click')
    await flushPromises()
    expect(save).toHaveBeenCalledExactlyOnceWith({ periodTaskIds: rows.slice(1).map(row => row.id), collectIntervalMs: 200 }, 'rev-1')
  })

  it('keeps selection and intended period after a rejected save', async () => {
    const view = open(vi.fn(async () => { throw new Error('配置版本冲突') }))
    await flushPromises()
    await view.find('.el-table__row input[type="checkbox"]').setValue(true)
    const interval = view.find('input[role="spinbutton"]')
    await interval.setValue('300'); await interval.trigger('change')
    await view.findAll('button').find(button => button.text() === '修改选中周期并保存全部')!.trigger('click')
    await flushPromises()
    expect(view.text()).toContain('配置版本冲突')
    expect(view.text()).toContain('已选 1 个')
    expect((interval.element as HTMLInputElement).value).toBe('300')
    expect(view.text()).not.toContain('全部流程已保存，已更新')
  })

  it('blocks saving a catalog newer than the editor drafts', async () => {
    vi.mocked(projectApi.revision).mockReturnValue('rev-2')
    const save = vi.fn(async () => {})
    const view = open(save)
    await flushPromises()
    expect(view.text()).toContain('工程配置已变化')
    const button = view.findAll('button').find(button => button.text() === '保存全部流程（保留各自周期）')!
    expect(button.attributes('disabled')).toBeDefined()
    expect(save).not.toHaveBeenCalled()
  })
})
