import { afterEach, describe, expect, it, vi } from 'vitest'

vi.mock('./api', () => ({ http: { get: vi.fn(), post: vi.fn(), put: vi.fn(), delete: vi.fn() } }))

import { canEdit, canView, clearSession, firstAllowedPath, levelOf, session, type AccountDto } from './auth'
import { withAccessToken } from './utils/sessionToken'

function account(partial: Partial<AccountDto>): AccountDto {
  return {
    username: 'op1', displayName: '', isAdmin: false, mustChangePassword: false, disabled: false,
    createdUtc: '', permissions: {}, ...partial,
  }
}

afterEach(() => { clearSession(); session.unlicensedFeatures = [] })

describe('页面权限', () => {
  it('管理员所有页面都可修改', () => {
    session.account = account({ isAdmin: true })
    expect(levelOf('settings')).toBe('Edit')
    expect(canEdit('project')).toBe(true)
  })

  it('普通用户按分配的级别：没分配的页面看不到', () => {
    session.account = account({ permissions: { monitor: 'View', project: 'Edit', logs: 'None' } })
    expect(canView('monitor')).toBe(true)
    expect(canEdit('monitor')).toBe(false)
    expect(canEdit('project')).toBe(true)
    expect(canView('logs')).toBe(false)
    expect(canView('settings')).toBe(false)
    expect(firstAllowedPath()).toBe('/project')
  })

  it('授权码不包含 MES 时，有权限也看不到采集流程', () => {
    session.account = account({ isAdmin: true })
    session.unlicensedFeatures = ['Mes']
    expect(canView('flow')).toBe(false)
    expect(canView('project')).toBe(true)
  })

  it('没有任何页面权限时没有可去的页面', () => {
    session.account = account({})
    expect(firstAllowedPath()).toBeNull()
  })

  it('下载链接带上登录凭证', () => {
    localStorage.setItem('igw_session', 'abc.def')
    expect(withAccessToken('/api/v1/project/export?x=1')).toBe('/api/v1/project/export?x=1&access_token=abc.def')
    expect(withAccessToken('/api/v1/templates/import-csv/sample')).toBe('/api/v1/templates/import-csv/sample?access_token=abc.def')
    localStorage.removeItem('igw_session')
    expect(withAccessToken('/a')).toBe('/a')
  })
})
