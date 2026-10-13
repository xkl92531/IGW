import { flushPromises, shallowMount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

vi.mock('../api', () => ({ api: {
  logCenterLogs: vi.fn(), logCenterStatus: vi.fn(), logCleanupStatus: vi.fn(),
} }))

import { api } from '../api'
import { clearSession, session, type AccountDto } from '../auth'
import Logs from './Logs.vue'

const response = { items: [], total: 0, tookMs: 0, histogram: [], devices: [], outages: [] }
const status = { available: true, state: 'running', port: 28020, retentionDays: 30, storageDirectory: 'D:\IGW-Logs',
  spool: { pending: 0, failed: 0, dropped: 0 }, migrationStatus: null, migrationTotalBytes: 0, migrationCopiedBytes: 0, migrationFailure: null }
// 对话框桩要渲染 footer 插槽，才能检查保存/开始清理按钮是否存在
const options = { global: { stubs: {
  ElButton: true, ElInput: true, ElInputNumber: true, ElSelect: true, ElOption: true, ElDatePicker: true, ElAlert: true,
  ElPagination: true, RouterLink: true, ElDialog: { template: '<div class="dialog-stub"><slot /><slot name="footer" /></div>' },
} } }
function account(permissions: Record<string, any>, isAdmin = false): AccountDto {
  return { username: 'op', displayName: '', isAdmin, mustChangePassword: false, disabled: false, createdUtc: '', permissions }
}

beforeEach(() => {
  vi.mocked(api.logCenterLogs).mockResolvedValue(response as any)
  vi.mocked(api.logCenterStatus).mockResolvedValue(status as any)
  vi.mocked(api.logCleanupStatus).mockResolvedValue({ state: 'Idle', residues: [], deletedFiles: 0, deletedBytes: 0, skipped: [] } as any)
})
afterEach(() => { clearSession(); vi.clearAllMocks() })

describe('日志中心只读权限', () => {
  it('只能看时隐藏清理和保存，日志设置输入框禁用，导出和刷新保留', async () => {
    session.account = account({ logs: 'View' })
    const wrapper = shallowMount(Logs, options); await flushPromises()
    expect(wrapper.find('[data-testid="log-cleanup"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="log-cleanup-start"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="log-export"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="log-export-run"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="log-settings-save"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="log-storage-browse"]').exists()).toBe(false)
    expect(wrapper.findComponent({ name: 'RuntimeLogDirectoryDialog' }).exists()).toBe(false)
    expect(wrapper.find('el-input-number-stub').attributes('disabled')).toBe('true')
    wrapper.unmount()
  })

  it('可修改时清理、保存和浏览文件夹都在', async () => {
    session.account = account({ logs: 'Edit' })
    const wrapper = shallowMount(Logs, options); await flushPromises()
    expect(wrapper.find('[data-testid="log-cleanup"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="log-cleanup-start"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="log-settings-save"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="log-storage-browse"]').exists()).toBe(true)
    expect(wrapper.findComponent({ name: 'RuntimeLogDirectoryDialog' }).exists()).toBe(true)
    expect(wrapper.find('el-input-number-stub').attributes('disabled')).toBe('false')
    wrapper.unmount()
  })
})
