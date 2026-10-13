import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { afterEach, describe, expect, it, vi } from 'vitest'

vi.mock('../features/project/api', () => ({ projectApi: { resolvePaths: vi.fn(), shell: vi.fn(), lazyChildren: vi.fn(), mutate: vi.fn() } }))
vi.mock('../api', () => ({ http: { get: vi.fn(), post: vi.fn(), put: vi.fn(), delete: vi.fn() } }))

import { clearSession, session, type AccountDto } from '../auth'
import { i18n } from '../i18n'
import { projectApi } from '../features/project/api'
import MoveTagDialog from './MoveTagDialog.vue'

function account(permissions: Record<string, any>): AccountDto {
  return { username: 'op', displayName: '', isAdmin: false, mustChangePassword: false, disabled: false, createdUtc: '', permissions }
}
async function open() {
  vi.mocked(projectApi.resolvePaths).mockResolvedValue([{ path: 'A.B.C', found: true, kind: 'tag', id: 't1', parentKind: 'device' }] as any)
  vi.mocked(projectApi.shell).mockResolvedValue({ project: { channels: [] }, lazy: {} } as any)
  const wrapper = mount(MoveTagDialog, {
    props: { modelValue: true, tagPath: 'A.B.C' }, attachTo: document.body,
    global: { plugins: [ElementPlus], config: { globalProperties: { $t: i18n.t } as any } },
  })
  await flushPromises()
  return wrapper
}
const saveButtonShown = () => [...document.querySelectorAll('.movedialog button')].some(b => b.textContent?.includes('移动并保存'))

afterEach(() => { clearSession(); document.body.innerHTML = '' })

describe('移动点位只读权限', () => {
  it('工程配置只能看时不显示「移动并保存」', async () => {
    session.account = account({ monitor: 'Edit', project: 'View' })
    const wrapper = await open()
    expect(saveButtonShown()).toBe(false)
    wrapper.unmount()
  }, 15000)

  it('工程配置可修改时显示「移动并保存」', async () => {
    session.account = account({ project: 'Edit' })
    const wrapper = await open()
    expect(saveButtonShown()).toBe(true)
    wrapper.unmount()
  }, 15000)
})
