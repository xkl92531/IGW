import { flushPromises, shallowMount } from '@vue/test-utils'
import { ElMessage } from 'element-plus'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from '../api'
import Logs from './Logs.vue'

vi.mock('../api', () => ({ api: {
  logCenterLogs: vi.fn(), logCenterStatus: vi.fn(), updateLogRetention: vi.fn(), changeLogStorage: vi.fn(),
} }))

const response = { items: [], total: 0, tookMs: 0, histogram: [], devices: [], outages: [] }
const initialStatus = { available: true, state: 'running', port: 28020, retentionDays: 30, storageDirectory: 'C:\\IGW\\logs',
  spool: { pending: 0, failed: 0, dropped: 0 }, migrationStatus: null, migrationTotalBytes: 0, migrationCopiedBytes: 0, migrationFailure: null }
let wrapper: ReturnType<typeof shallowMount>
const mountOptions = { global: { renderStubDefaultSlot: true, stubs: [
  'ElButton', 'ElDialog', 'ElInput', 'ElInputNumber', 'ElSelect', 'ElOption', 'ElDatePicker', 'ElAlert', 'ElPagination',
  'RouterLink',
] } }

beforeEach(() => {
  vi.mocked(api.logCenterLogs).mockReset().mockResolvedValue(response as any)
  vi.mocked(api.logCenterStatus).mockReset().mockResolvedValue(initialStatus as any)
  vi.mocked(api.updateLogRetention).mockReset().mockResolvedValue({ retentionDays: 14, restartPending: true })
  vi.mocked(api.changeLogStorage).mockReset().mockResolvedValue({ status: 'copying', sourceDirectory: initialStatus.storageDirectory,
    targetDirectory: 'D:\\生产日志', totalBytes: 1, copiedBytes: 0 })
  vi.spyOn(ElMessage, 'error').mockImplementation(() => ({ close() { } }))
  vi.spyOn(ElMessage, 'success').mockImplementation(() => ({ close() { } }))
})
afterEach(() => { wrapper?.unmount(); vi.restoreAllMocks(); vi.clearAllMocks() })

describe('log storage settings', () => {
  it.each([0, 1, 2])('handles numeric active migration status %s returned by the real backend', async value => {
    vi.mocked(api.logCenterStatus).mockResolvedValue({ ...initialStatus, engine: 'questdb', migrationStatus: value,
      migrationTotalBytes: 100, migrationCopiedBytes: 25 } as any)
    wrapper = shallowMount(Logs, mountOptions); await flushPromises()
    expect(wrapper.find('[data-testid="storage-migration-status"]').exists()).toBe(true)
    const vm = wrapper.vm as any
    vm.openSettings(); vm.storageDraft = 'D:\\新目录'; await vm.saveSettings()
    expect(api.changeLogStorage).not.toHaveBeenCalled()
  })

  it('handles numeric completed migration status and refreshes the ready state', async () => {
    vi.mocked(api.logCenterStatus).mockResolvedValue({ ...initialStatus, engine: 'questdb', migrationStatus: 3, historyImported: true } as any)
    wrapper = shallowMount(Logs, mountOptions); await flushPromises()
    expect(wrapper.find('[data-testid="storage-migration-status"]').exists()).toBe(false)
    expect(wrapper.text()).toContain('运行中')
    vi.mocked(api.logCenterStatus).mockResolvedValue({ ...initialStatus, engine: 'questdb', available: false, migrationStatus: 1 } as any)
    await (wrapper.vm as any).loadStatus(); await flushPromises()
    expect(wrapper.find('[data-testid="storage-migration-status"]').exists()).toBe(true)
  })
  it('shows QuestDB directory migration and history import through the existing settings', async () => {
    vi.mocked(api.logCenterStatus).mockResolvedValue({ ...initialStatus, available: false, engine: 'questdb',
      historyImported: false, historyTotalFiles: 10, historyImportedFiles: 3, historyImportedRows: 123,
      migrationStatus: 'copying', migrationTotalBytes: 100, migrationCopiedBytes: 25 } as any)
    wrapper = shallowMount(Logs, mountOptions); await flushPromises()
    expect(wrapper.find('[data-testid="history-import-status"]').exists()).toBe(false)
    ;(wrapper.vm as any).openSettings(); await flushPromises()
    expect(wrapper.text()).toContain('QuestDB')
    expect(wrapper.text()).not.toContain('按时间分段压缩保存在本机')
    expect(wrapper.find('[data-testid="history-import-status"]').text()).toContain('3/10')
    expect(wrapper.find('[data-testid="storage-migration-status"]').text()).toContain('25%')
    const vm = wrapper.vm as any
    vm.openSettings(); vm.storageDraft = 'D:\\新目录'; await vm.saveSettings()
    expect(api.changeLogStorage).not.toHaveBeenCalled()
  })

  it('keeps normal background history import quiet while fresh logs remain available', async () => {
    vi.mocked(api.logCenterStatus).mockResolvedValue({ ...initialStatus, engine: 'questdb', historyImported: false,
      historyTotalFiles: 1606, historyImportedFiles: 500 } as any)
    wrapper = shallowMount(Logs, mountOptions); await flushPromises()
    expect(wrapper.text()).toContain('运行中')
    expect(wrapper.find('[data-testid="history-import-status"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="history-import-error"]').exists()).toBe(false)
  })

  it('reports directory migration as started until the backend reports completion', async () => {
    wrapper = shallowMount(Logs, mountOptions); await flushPromises()
    const vm = wrapper.vm as any
    vm.openSettings(); vm.storageDraft = 'D:\\生产日志'; await vm.saveSettings()
    expect(api.changeLogStorage).toHaveBeenCalledWith('D:\\生产日志')
    expect(ElMessage.success).toHaveBeenCalledWith('日志目录迁移已开始，完成后自动切换；原目录暂时保留')
  })

  it('shows history failure without suggesting that a smaller time range will resolve it', async () => {
    vi.mocked(api.logCenterStatus).mockResolvedValue({ ...initialStatus, engine: 'questdb', historyImported: false,
      historyError: '旧分段损坏，原文件已保留' } as any)
    vi.mocked(api.logCenterLogs).mockResolvedValue({ ...response, partial: true } as any)
    wrapper = shallowMount(Logs, mountOptions); await flushPromises()
    expect(wrapper.find('[data-testid="history-import-error"]').attributes('title')).toContain('旧分段损坏')
    expect(wrapper.find('[data-testid="partial-result"]').text()).toContain('旧日志尚未全部导入')
  })
  it('opens settings without any OpenObserve credential request', async () => {
    wrapper = shallowMount(Logs, mountOptions); await flushPromises()
    const vm = wrapper.vm as any
    vm.openSettings(); await flushPromises()
    expect(vm.settingsOpen).toBe(true)
    expect(vm.retentionDraft).toBe(30)
    expect(vm.storageDraft).toBe(initialStatus.storageDirectory)
    expect(wrapper.text()).not.toContain('OpenObserve')
  })

  it('does not clear a newer manual loading indicator when an older query finishes', async () => {
    wrapper = shallowMount(Logs, mountOptions); await flushPromises()
    let oldDone!: (value: any) => void, newDone!: (value: any) => void
    vi.mocked(api.logCenterLogs).mockImplementationOnce(() => new Promise(resolve => { oldDone = resolve }))
      .mockImplementationOnce(() => new Promise(resolve => { newDone = resolve }))
    const vm = wrapper.vm as any
    const oldQuery = vm.reload('manual'), newQuery = vm.reload('manual')
    expect(vm.loading).toBe(true)
    oldDone(response); await oldQuery
    expect(vm.loading).toBe(true)
    newDone(response); await newQuery
    expect(vm.loading).toBe(false)
  })

  it('keeps edited values during status refresh and saves retention before storage migration', async () => {
    wrapper = shallowMount(Logs, mountOptions); await flushPromises()
    const vm = wrapper.vm as any
    vm.openSettings(); vm.storageDraft = 'D:\\生产日志'; vm.retentionDraft = 14
    await vm.loadStatus()
    expect(vm.storageDraft).toBe('D:\\生产日志')
    expect(vm.retentionDraft).toBe(14)
    vi.mocked(api.logCenterStatus).mockResolvedValue({ ...initialStatus, retentionDays: 14, storageDirectory: 'D:\\生产日志', migrationStatus: 'copying' } as any)
    await vm.saveSettings()
    expect(api.updateLogRetention).toHaveBeenCalledWith(14)
    expect(api.changeLogStorage).toHaveBeenCalledWith('D:\\生产日志')
    expect(vi.mocked(api.updateLogRetention).mock.invocationCallOrder[0])
      .toBeLessThan(vi.mocked(api.changeLogStorage).mock.invocationCallOrder[0])
    expect(vm.settingsOpen).toBe(false)
  })

  it('keeps the active path and unsaved draft when migration is rejected', async () => {
    wrapper = shallowMount(Logs, mountOptions); await flushPromises()
    const vm = wrapper.vm as any
    vm.openSettings(); vm.storageDraft = 'Z:\\logs'
    vi.mocked(api.changeLogStorage).mockRejectedValue({ response: { data: { error: '目录不可写' } } })
    await vm.saveSettings()
    expect(vm.status.storageDirectory).toBe(initialStatus.storageDirectory)
    expect(vm.storageDraft).toBe('Z:\\logs')
    expect(vm.settingsError).toBe('目录不可写')
    expect(vm.settingsOpen).toBe(true)
  })
})
